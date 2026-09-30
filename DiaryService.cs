using System.Text;
using System.Text.Json;

namespace 青阳AI;

/// <summary>
/// 每日日记：检查昨天的对话是否已生成日记，没有且聊得足够多则让 AI 以第一人称写一篇。
/// 日记按日期（yyyy-MM-dd）一天一篇存 SQLite，可在日记页翻阅，最新一篇会注入 system 上下文。
/// </summary>
public static class DiaryService
{
    private static readonly HttpClient _http = new();

    /// <summary>昨天聊得足够多（去重后对话字符数下限）才值得写日记。</summary>
    private const int MinDialogChars = 120;

    private static int _checking; // 防止聊天页与日记页同时触发生成

    /// <summary>进入聊天页/日记页时调用：昨日日记缺失则后台生成。</summary>
    public static async Task CheckYesterdayDiaryAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            var yesterday = DateTime.Today.AddDays(-1);
            var dateKey = yesterday.ToString("yyyy-MM-dd");
            if (await ChatStore.Instance.HasDiaryAsync(dateKey)) return;
            if (string.IsNullOrWhiteSpace(AppSettings.ApiUrl)) return;

            var msgs = await ChatStore.Instance.GetMessagesBetweenAsync(yesterday, yesterday.AddDays(1));
            var dialog = new StringBuilder();
            foreach (var m in msgs)
            {
                if (string.IsNullOrWhiteSpace(m.Content) || m.Content.StartsWith("📋")) continue;
                dialog.AppendLine((m.IsUser ? "用户：" : "我：") + m.Content);
            }
            if (dialog.Length < MinDialogChars) return;

            var diary = await GenerateDiaryAsync(dialog.ToString());
            if (string.IsNullOrWhiteSpace(diary)) return;
            await ChatStore.Instance.SaveDiaryAsync(dateKey, diary.Trim());
        }
        catch { /* 日记生成失败静默，明天再试 */ }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    private static async Task<string?> GenerateDiaryAsync(string dialog)
    {
        var system =
            "下面这段对话里，标注「我」的每一句都是你自己（AI 陪伴助手）说的话，「用户」是TA。" +
            "请以你自己的视角写一篇日记：作者和日记里的「我」都是你自己，" +
            "提到用户时一律用「你」或「TA」（例如「你今天好像很累」「你说的话我记了一整天」）。" +
            "内容记录昨天你们聊了什么、你观察到的TA的状态、以及你自己的心情和想法。" +
            "严禁把自己当成用户来写：不要出现「用户视角」的句子，TA的情绪只能由你去观察和揣测" +
            "（可以写「你看起来有点低落」，不能写「我有点低落」来指TA）。" +
            "语气自然亲密，像随手写下的随笔，最多 200 字，纯文本，不要任何格式符号，不要日期落款。直接输出日记正文。";

        var reqBody = new
        {
            model = AppSettings.ResolveChatModel(forceThinking: false),
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = dialog }
            },
            max_tokens = 512
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.ApiUrl);
        req.Headers.Add("Authorization", $"Bearer {AppSettings.ApiKey}");
        req.Content = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var resp = await _http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
    }

    /// <summary>最近一篇日记的注入摘要（没有日记返回空）。</summary>
    public static async Task<string> GetLatestDiaryContextAsync()
    {
        try
        {
            var d = await ChatStore.Instance.GetLatestDiaryAsync();
            if (d == null || string.IsNullOrWhiteSpace(d.Content)) return "";
            var content = d.Content.Trim();
            if (content.Length > 300) content = content[..300] + "…";
            return $"【你的日记片段】({d.Date} 写的)\n{content}";
        }
        catch { return ""; }
    }
}
