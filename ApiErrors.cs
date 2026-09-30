using System.Text.Json;

namespace 青阳AI;

/// <summary>
/// 网络/接口错误的**统一处理**：错误翻人话、判断值不值得重试、把服务商的错误正文捞出来。
///
/// 抽成独立类是因为**两条路都要用**：
///   · 普通聊天（`ChatPage`）
///   · Agent 循环（`AgentLoopService`）
/// 以前这些逻辑只写在 ChatPage 里，Agent 那条路啥都没有 —— 出错就直接把整个循环打断。
/// </summary>
public static class ApiErrors
{
    // ───────────── 错误翻人话 ─────────────

    /// <summary>把异常翻成用户能看懂的话。</summary>
    public static string Friendly(Exception ex)
    {
        // ① 有 HTTP 状态码：按码给具体原因（比笼统的"网络连接失败"有用得多）
        if (ex is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
        {
            int code = (int)httpEx.StatusCode.Value;
            var baseMsg = code switch
            {
                400 => "服务商拒绝了这次请求（400）。多半是模型名填错，或这个模型不支持当前参数。",
                401 => "API Key 无效，请检查设置",
                403 => "服务商拒绝了这次请求（403）：可能是 Key 没权限、余额不足，或模型没开通。",
                404 => "接口地址不对（404）。设置里的 URL 要填完整端点，比如 https://…/v1/chat/completions",
                429 => "被服务商限流了（429）。\n"
                     + "注意：不一定是总额度用完 —— 很多服务商还有「滚动时间窗」限流"
                     + "（比如 5 小时内用满，就得等窗口重置）。去控制台看「时间窗 / 5h 窗口」那一栏；"
                     + "那一栏满了就只能等它重置，或者换个模型。\n"
                     + "如果窗口也没满，那就是发得太快，稍等一会儿再发。",
                >= 500 => $"服务商那边出错了（{code}），稍后再试。",
                _ => $"服务商返回 HTTP {code}"
            };

            // 服务商正文里往往写了真正的原因（比如 insufficient_quota = 额度用完），
            // 带上它比只报个状态码有用得多
            var detail = httpEx.Message ?? "";
            if (!string.IsNullOrWhiteSpace(detail)
                && !detail.StartsWith("HTTP ", StringComparison.Ordinal))
                return baseMsg + "\n\n服务商原话：" + detail;

            return baseMsg;
        }

        // ② 超时
        if (ex is TaskCanceledException || ex is OperationCanceledException)
            return "请求超时了。可能是网络慢，或者服务商那边卡住了。";

        // ③ 底层是 Java 异常（Android 网络栈抛的）：
        //    .NET 拿到 Java 异常的 message 时经常是 "Exception_WasThrown" 这种占位符，
        //    直接甩给用户等于没提示 —— 翻成人话，并把真实类型带上，方便排查。
        var java = FindJava(ex);
        if (java != null)
            return "网络连接被中断（" + java + "）。\n"
                 + "常见原因：网络切换或断流、服务商掐线、代理/证书拦截。\n"
                 + "过一会儿重发试试；一直这样就检查网络，或换个接口地址。";

        return "网络连接失败：" + Describe(ex);
    }

    /// <summary>
    /// 值不值得自动重试一次。
    ///
    /// **重点**：Android 网络栈在连接被掐时抛的是 **Java 异常**（不是 .NET 的
    /// SocketException），以前没认出来 → 不重试 → 用户看到的就是"断断续续、每次都失败"。
    /// </summary>
    public static bool IsTransient(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is TimeoutException || e is System.Net.Sockets.SocketException) return true;
            if (e is IOException) return true;

            var full = e.GetType().FullName ?? "";
            var msg = e.Message ?? "";

            if (full.Contains("Java", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("Exception_WasThrown", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("Connection reset", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("unexpected end of stream", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("连接中断", StringComparison.Ordinal)) return true;
        }

        if (ex is TaskCanceledException || ex is OperationCanceledException) return true;

        // 有状态码的：只有 5xx 值得重试（4xx 重试一百次也一样）
        if (ex is HttpRequestException hx && hx.StatusCode.HasValue)
            return (int)hx.StatusCode.Value >= 500;

        return false;
    }

    // ───────────── 把服务商的错误正文捞出来 ─────────────

    /// <summary>
    /// 像 `EnsureSuccessStatusCode()` 一样，但**先把服务商的错误正文捞出来**再抛。
    ///
    /// 只报状态码用户没法排查：429 到底是"发太快"还是"额度用完"？403 是"没权限"还是"欠费"？
    /// OpenAI 兼容接口会在正文里写清楚，比如
    /// `{"error":{"message":"You exceeded your current quota…","code":"insufficient_quota"}}`。
    /// 所以这里把正文抠出来塞进异常消息，最后会显示成「服务商原话：…」。
    /// </summary>
    public static async Task EnsureOkAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;

        int code = (int)resp.StatusCode;
        string detail = "";
        try { detail = await resp.Content.ReadAsStringAsync(); } catch { }

        var msg = ExtractApiError(detail);
        throw new HttpRequestException(
            string.IsNullOrWhiteSpace(msg) ? $"HTTP {code}" : msg,
            inner: null,
            statusCode: resp.StatusCode);
    }

    /// <summary>从 OpenAI 兼容的错误正文里抠出 message / code（各家格式不完全一样，广撒网）。</summary>
    public static string ExtractApiError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                var m = err.TryGetProperty("message", out var mv) ? (mv.GetString() ?? "") : "";
                var c = err.TryGetProperty("code", out var cv) && cv.ValueKind == JsonValueKind.String
                        ? (cv.GetString() ?? "") : "";
                if (!string.IsNullOrWhiteSpace(m) && !string.IsNullOrWhiteSpace(c)) return $"{m}（{c}）";
                if (!string.IsNullOrWhiteSpace(m)) return m;
                if (!string.IsNullOrWhiteSpace(c)) return c;
            }

            if (root.TryGetProperty("message", out var m2) && m2.ValueKind == JsonValueKind.String)
                return m2.GetString() ?? "";
        }
        catch { /* 不是 JSON 就当纯文本处理 */ }

        return body.Length > 200 ? body[..200] : body;
    }

    // ───────────── 内部 ─────────────

    /// <summary>
    /// 往异常链里找 Java 异常（Android 网络栈抛的）。找到就返回它的类型名，没有返回 null。
    /// </summary>
    private static string? FindJava(Exception? ex)
    {
        for (int i = 0; ex != null && i < 6; i++, ex = ex.InnerException)
        {
            var full = ex.GetType().FullName ?? "";
            var msg = ex.Message ?? "";
            if (full.Contains("Java", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Exception_WasThrown", StringComparison.OrdinalIgnoreCase))
            {
                var detail = msg.Contains("Exception_WasThrown", StringComparison.OrdinalIgnoreCase)
                    ? full
                    : (string.IsNullOrWhiteSpace(msg) ? full : full + "：" + msg);
                return string.IsNullOrWhiteSpace(detail) ? ex.GetType().Name : detail;
            }
        }
        return null;
    }

    /// <summary>
    /// 把异常链拼成一行能读的（最多四层）。
    /// 有些异常 `Message` 是空的，那时候至少把类型名带上 —— 总比空白强。
    /// </summary>
    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null && parts.Count < 4; e = e.InnerException)
        {
            var m = e.Message ?? "";
            parts.Add(string.IsNullOrWhiteSpace(m) ? e.GetType().Name : e.GetType().Name + "：" + m);
        }
        return string.Join(" ← ", parts);
    }
}
