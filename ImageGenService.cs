using System.Text;
using System.Text.Json;

namespace 青阳AI;

/// <summary>
/// 文生图统一入口。收敛原来散在两处的重复实现（聊天生图管线 + 头像生成）。
///
/// 关键行为：
///   · **失败自动重试一次** —— 第 2 次去掉服务商可能不认的 `response_format` / `size`，
///     只留 `{model, prompt}`，兼容面更广（用户明确要求"先重试一次再报错"）
///   · 兼容两种返回：`data[0].b64_json`（base64）与 `data[0].url`（再 GET 成字节）
///   · 错误一律翻成人话，可直接上屏
///
/// ⚠️ **地址原样使用，不做任何补全** —— 用户决定不修 404 那条，
///    所以 `AppSettings.ImgApiUrl` 必须填**完整端点**（如 https://…/v1/images/generations）。
/// </summary>
public static class ImageGenService
{
    /// <summary>一次生成的结果。</summary>
    public sealed class Result
    {
        /// <summary>是否成功。</summary>
        public bool Ok { get; set; }

        /// <summary>图片字节（成功时非空）。</summary>
        public byte[]? Bytes { get; set; }

        /// <summary>base64 原文（服务商直接返回 b64 时带上，界面可直接显示）。</summary>
        public string? B64 { get; set; }

        /// <summary>人话错误（失败时非空，可直接上屏）。</summary>
        public string Error { get; set; } = "";

        /// <summary>实际尝试了几次（1 或 2）。</summary>
        public int Attempts { get; set; }
    }

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };

    /// <summary>生成一张图。失败会自动重试一次；仍然失败就返回人话错误（不抛异常）。</summary>
    public static async Task<Result> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        var r = new Result();

        if (!AppSettings.ImgEnabled)
        {
            r.Error = "没有配置文生图模型（设置 → 文生图：接口地址和 API Key 都要填）";
            return r;
        }
        if (string.IsNullOrWhiteSpace(prompt))
        {
            r.Error = "图片描述是空的";
            return r;
        }

        var model = string.IsNullOrWhiteSpace(AppSettings.ImgModel) ? "gpt-image-1" : AppSettings.ImgModel;

        // 第 1 次：完整参数；第 2 次：只留 model + prompt（兼容不认那两个字段的服务商）
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            r.Attempts = attempt;
            try
            {
                object body = attempt == 1
                    ? new { model, prompt, response_format = "b64_json", size = "1024x1024" }
                    : new { model, prompt };

                using var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.ImgApiUrl);
                req.Headers.Add("Authorization", $"Bearer {AppSettings.ImgApiKey}");
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

                using var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    r.Error = HumanizeStatus((int)resp.StatusCode);
                    continue;   // 换个参数再试一次
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                var (b64, url) = ParseImage(json);
                if (string.IsNullOrWhiteSpace(b64) && string.IsNullOrWhiteSpace(url))
                {
                    r.Error = "接口没有返回图片数据";
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(b64)
                    && !b64.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    r.Bytes = Convert.FromBase64String(b64);
                    r.B64 = b64;
                }
                else
                {
                    // 服务商给的是图片链接，再拉一次
                    var link = !string.IsNullOrWhiteSpace(url) ? url : b64!;
                    r.Bytes = await _http.GetByteArrayAsync(link, ct);
                }

                r.Ok = true;
                r.Error = "";
                return r;
            }
            catch (OperationCanceledException)
            {
                r.Error = "生成超时（超过 3 分钟）";
            }
            catch (Exception ex)
            {
                r.Error = "调用生图接口失败：" + ex.Message;
            }
        }

        return r;
    }

    // ───────────── 内部 ─────────────

    /// <summary>解析 data[0] 里的 b64_json / url。</summary>
    private static (string? b64, string? url) ParseImage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
            {
                var first = data[0];
                string? b = first.TryGetProperty("b64_json", out var bv) ? bv.GetString() : null;
                string? u = first.TryGetProperty("url", out var uv) ? uv.GetString() : null;
                return (b, u);
            }
        }
        catch { }
        return (null, null);
    }

    /// <summary>把 HTTP 状态码翻成人话。</summary>
    private static string HumanizeStatus(int code) => code switch
    {
        401 or 403 => "生图 API Key 无效或没有权限",
        404 => "生图接口地址不对（设置里的地址要填完整端点，比如 https://…/v1/images/generations）",
        429 => "生图请求太频繁，稍后再试",
        >= 500 => "生图服务商那边出错了，稍后再试",
        400 or 422 => "服务商不接受这组参数（已经试过简化参数了）",
        _ => $"生图接口返回 HTTP {code}"
    };
}
