using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace 青阳AI;

/// <summary>
/// 每条消息 AI 必看：用户每次发送后，AI 静默用当前模型做一次复盘，
/// 抽取「情绪 · 意图 · 潜台词 · 关系走向」四个维度，把结果注入下一次对话的 system，
/// 让她下一句话更贴 TA。
///
/// 设计要点：
/// - 开关 AppSettings.ReviewEachMessage 独立于 CareEnabled / CareSenseEnabled
/// - 失败静默降级：不影响正常对话
/// - 只存最近 6 条复盘，避免 prompt 无限膨胀
/// - 复盘不写库、不上屏、不进历史，只在内存里给下一次对话用
/// </summary>
public static class MessageReviewService
{
    private static readonly HttpClient _http = new();
    private static readonly object _lock = new();
    private static readonly List<string> _reviews = new();
    private const int MaxReviews = 6;

    /// <summary>组装成 system 里的一段上下文（用户看不到，只给 AI）。</summary>
    public static string BuildReviewContext()
    {
        lock (_lock)
        {
            if (_reviews.Count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine("【最近几条你的私下复盘（不展示给用户，仅供你心里有数）】");
            foreach (var r in _reviews)
                sb.AppendLine("- " + r);
            return sb.ToString();
        }
    }

    /// <summary>当前有无任何复盘缓存。</summary>
    public static bool HasAny
    {
        get { lock (_lock) return _reviews.Count > 0; }
    }

    /// <summary>清空全部复盘缓存（设置里"清空记忆"时一起清）。</summary>
    public static void Clear()
    {
        lock (_lock) _reviews.Clear();
    }

    /// <summary>
    /// 对一条用户消息做 AI 复盘。返回 void，异步不阻塞。
    /// 结果写入 _reviews，可通过 BuildReviewContext() 注入下一次对话。
    /// </summary>
    public static async Task ReviewAsync(string userText, int turnIndex)
    {
        if (!AppSettings.ReviewEachMessage) return;
        if (string.IsNullOrWhiteSpace(userText)) return;
        if (string.IsNullOrWhiteSpace(AppSettings.ApiUrl)) return;
        if (string.IsNullOrWhiteSpace(AppSettings.ApiKey)) return;

        try
        {
            var model = AppSettings.ResolveChatModel(forceThinking: false);
            if (string.IsNullOrWhiteSpace(model)) return;

            // 复盘 prompt：简短、结构化、不啰嗦
            var systemPrompt =
                "你是这条聊天里 AI 的另一个分身，专门在用户每次说话后做一次静默复盘。" +
                "只输出四行纯文本，每行一个维度，禁止 Markdown，禁止废话，禁止解释格式，总长不超过 120 字。" +
                "格式严格如下（冒号后跟内容）：" +
                "情绪：（用户此刻情绪，一个词或短语）" +
                "意图：（用户真正想要什么，一个词或短语）" +
                "潜台词：（话里没说的，一个词或短语；看不出写\"无\"）" +
                "走向：（这段关系正在往哪个方向走，一个词或短语）";

            var body = new Dictionary<string, object>
            {
                ["model"] = model,
                ["stream"] = false,
                ["messages"] = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userText }
                },
                ["max_tokens"] = 200
            };

            var json = JsonSerializer.Serialize(body);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _http.DefaultRequestHeaders.Remove("Authorization");
            _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {AppSettings.ApiKey.Trim()}");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var resp = await _http.PostAsync(AppSettings.ApiUrl, content, cts.Token);
            if (!resp.IsSuccessStatusCode) return;

            var raw = await resp.Content.ReadAsStringAsync(cts.Token);
            var text = ExtractContent(raw);
            if (string.IsNullOrWhiteSpace(text)) return;

            // 简单规整：去掉可能的多余空行/代码围栏
            text = text.Trim();
            text = RegexReplace(text, "^```[a-z]*\\s*", "");
            text = RegexReplace(text, "\\s*```$", "");
            // 换行 → " / " 拼接成一行
            text = RegexReplace(text, "\\r?\\n", " / ");
            text = RegexReplace(text, "\\s{2,}", " ");
            text = text.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            lock (_lock)
            {
                _reviews.Add(text);
                while (_reviews.Count > MaxReviews)
                    _reviews.RemoveAt(0);
            }
        }
        catch
        {
            // 静默：复盘失败不影响正常对话
        }
    }

    /// <summary>从非流式响应 JSON 里抽出 content。</summary>
    private static string ExtractContent(string raw)
    {
        try
        {
            var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("choices", out var ch)
                && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0
                && ch[0].TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var c))
                return c.GetString() ?? "";
        }
        catch { }
        return "";
    }

    private static string RegexReplace(string s, string pattern, string repl)
    {
        try { return System.Text.RegularExpressions.Regex.Replace(s, pattern, repl); }
        catch { return s; }
    }
}
