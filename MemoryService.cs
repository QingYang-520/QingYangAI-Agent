using System.Text;
using System.Text.Json;

namespace 青阳AI;

/// <summary>
/// 长期记忆：从对话中提取"关于用户的稳定事实"存入 SQLite，并注入每次请求的 system 提示词。
/// 提取异步执行，不阻塞聊天；每完成 2 轮对话触发一次。
/// </summary>
public static class MemoryService
{
    private static readonly HttpClient _http = new();
    private static int _exchangesSinceExtract;
    private const int ExtractEveryExchanges = 2;
    private const int MaxContextMemories = 10;

    /// <summary>记忆更新后通知 UI 刷新注入上下文。</summary>
    public static event Action? MemoriesChanged;

    /// <summary>外部变更记忆（面板删除单条等）后手动触发刷新。</summary>
    public static void NotifyMemoriesChanged() =>
        MainThread.BeginInvokeOnMainThread(() => MemoriesChanged?.Invoke());

    /// <summary>对话完成一轮（用户问 + AI 答）后调用。内部按节奏触发提取。</summary>
    public static void OnExchangeCompleted(IReadOnlyList<ChatMsg> recentMessages)
    {
        _exchangesSinceExtract++;
        if (_exchangesSinceExtract < ExtractEveryExchanges) return;
        _exchangesSinceExtract = 0;

        // 快照最近 8 条有内容的消息，供提取使用
        var snapshot = recentMessages
            .Where(m => !string.IsNullOrWhiteSpace(m.Content) && !m.Content.StartsWith("📋"))
            .TakeLast(8)
            .Select(m => new ChatMsg { Content = m.Content, IsUser = m.IsUser })
            .ToList();
        _ = ExtractAsync(snapshot);
    }

    private static async Task ExtractAsync(List<ChatMsg> snapshot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(AppSettings.ApiUrl) || snapshot.Count == 0) return;

            var mems = await ChatStore.Instance.GetRecentMemoriesAsync(40);
            var dialog = new StringBuilder();
            foreach (var m in snapshot)
                dialog.AppendLine((m.IsUser ? "用户：" : "AI：") + m.Content);

            var system =
                "你是一个记忆提取器。从对话中提取值得长期记住的、关于用户的稳定事实" +
                "（喜好、习惯、重要的人、约定、情绪雷区、身体状况等）。" +
                "每条一句话，以第三人称陈述，例如：用户不吃香菜。用户每周五开会到很晚。用户养了一只叫团团的猫。" +
                "不要提取一次性讨论内容、寒暄、AI 自己的回答。输出 JSON 字符串数组，没有值得记的就输出 []。只输出 JSON。";
            var user = "已有记忆（不要重复这些）：\n" +
                       (mems.Count > 0 ? string.Join("\n", mems) : "（无）") +
                       "\n\n最近对话：\n" + dialog;

            var reqBody = new
            {
                model = AppSettings.ResolveChatModel(forceThinking: false),
                stream = false,
                messages = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user }
                },
                max_tokens = 512
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.ApiUrl);
            req.Headers.Add("Authorization", $"Bearer {AppSettings.ApiKey}");
            req.Content = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var resp = await _http.SendAsync(req, cts.Token);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            var facts = ParseFactArray(content);
            bool changed = false;
            foreach (var fact in facts)
                changed |= await ChatStore.Instance.AddMemoryAsync(fact);

            if (changed)
                MainThread.BeginInvokeOnMainThread(() => MemoriesChanged?.Invoke());
        }
        catch { /* 提取失败静默，不影响聊天 */ }
    }

    /// <summary>解析模型输出的事实数组（容忍 ```json 围栏与杂文字）。</summary>
    private static List<string> ParseFactArray(string content)
    {
        var result = new List<string>();
        try
        {
            int s = content.IndexOf('[');
            int e = content.LastIndexOf(']');
            if (s < 0 || e <= s) return result;

            using var doc = JsonDocument.Parse(content[s..(e + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } f && f.Trim().Length > 0)
                    result.Add(f.Trim());
            }
        }
        catch { }
        return result;
    }

    /// <summary>生成注入 system 的记忆块（最近 10 条，无记忆返回空）。</summary>
    public static async Task<string> GetMemoryContextAsync()
    {
        try
        {
            var mems = await ChatStore.Instance.GetRecentMemoriesAsync(MaxContextMemories);
            if (mems.Count == 0) return "";
            return "【你记得的关于用户的事】\n" + string.Join("\n", mems);
        }
        catch { return ""; }
    }
}
