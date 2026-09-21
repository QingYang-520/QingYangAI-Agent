using System.Text;
using System.Text.Json;

namespace 青阳AI;

/// <summary>
/// Ta的 头像：{avatar:"自画像描述"} 指令或设置页触发，用文生图 API 画一张
/// 并存为固定文件 avatar.png，聊天标题栏显示。生成失败静默（保持旧头像）。
/// </summary>
public static class AvatarService
{
    public static string AvatarPath => Path.Combine(FileSystem.AppDataDirectory, "avatar.png");

    public static ImageSource? Load()
    {
        try
        {
            return File.Exists(AvatarPath) ? ImageSource.FromFile(AvatarPath) : null;
        }
        catch { return null; }
    }

    /// <summary>生成并保存头像。返回是否成功。</summary>
    public static async Task<bool> GenerateAsync(string desc)
    {
        try
        {
            if (!AppSettings.ImgEnabled || string.IsNullOrWhiteSpace(desc)) return false;

            var reqBody = new
            {
                model = string.IsNullOrWhiteSpace(AppSettings.ImgModel) ? "gpt-image-1" : AppSettings.ImgModel,
                prompt = $"为一位 AI 陪伴助手画一张头像：{desc}。要求温暖、简洁、适合做圆形头像，纯色或柔和背景。",
                response_format = "b64_json",
                size = "1024x1024"
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.ImgApiUrl);
            req.Headers.Add("Authorization", $"Bearer {AppSettings.ImgApiKey}");
            req.Content = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var resp = await new HttpClient().SendAsync(req, cts.Token);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();

            string? b64 = null;
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
                {
                    var first = data[0];
                    if (first.TryGetProperty("b64_json", out var b)) b64 = b.GetString();
                    else if (first.TryGetProperty("url", out var u)) b64 = u.GetString();
                }
            }
            if (string.IsNullOrWhiteSpace(b64)) return false;

            var bytes = b64.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? await new HttpClient().GetByteArrayAsync(b64)
                : Convert.FromBase64String(b64);

            await File.WriteAllBytesAsync(AvatarPath, bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
