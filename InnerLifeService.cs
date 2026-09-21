using System.Text;
using System.Text.Json;

namespace 青阳AI;

/// <summary>
/// 内心生活：心情状态机 + 内心独白 + 后台自主心跳。
/// - 每聊 2 轮，Ta"想一想"：更新自己的心情与一句心里话（用户看不到，注入后续对话）；
/// - 后台每 15 分钟的心跳不再由规则决定说什么，而是把局面喂给Ta：
///   Ta自己决定要不要调用感知 API 了解情况、要不要开口、说什么。
///   免打扰时段/每日上限/最小间隔作为硬闸门，自主权在闸门之内。
/// </summary>
public static class InnerLifeService
{
    private static readonly HttpClient _http = new();

    /// <summary>内在状态变化（心情/心里话更新）后通知 UI 刷新。</summary>
    public static event Action? StateChanged;

    // ─────────── 心情与心里话（持久化） ───────────

    public static string Mood
    {
        get => Preferences.Default.Get("InnerMood", "平静");
        set => Preferences.Default.Set("InnerMood", string.IsNullOrWhiteSpace(value) ? "平静" : value.Trim());
    }

    public static string LastThought
    {
        get => Preferences.Default.Get("InnerThought", "");
        set => Preferences.Default.Set("InnerThought", value ?? "");
    }

    private static string MoodAt
    {
        get => Preferences.Default.Get("InnerMoodAt", "");
        set => Preferences.Default.Set("InnerMoodAt", value);
    }

    /// <summary>心情 → 小表情（标题栏露出）。</summary>
    public static string MoodEmoji(string? mood) => (mood ?? "").Trim() switch
    {
        "开心" => "😄",
        "想你" => "🥺",
        "委屈" => "🥹",
        "生气" => "😤",
        "担心" => "😟",
        "困倦" => "😴",
        "兴奋" => "🤩",
        "平静" => "🙂",
        "害羞" => "😳",
        "失落" => "😔",
        _ => "💭"
    };

    /// <summary>更新心情并广播（{mood:} 指令与反思都走这里）。</summary>
    public static void SetMood(string mood)
    {
        var old = Mood;
        Mood = mood;
        if (old != Mood)
        {
            MoodAt = DateTime.Now.ToString("HH:mm");
            MainThread.BeginInvokeOnMainThread(() => StateChanged?.Invoke());
        }
    }

    /// <summary>注入 system 的内心状态块（用户看不到）。</summary>
    public static string BuildInnerContext()
    {
        var parts = new List<string> { $"心情：{Mood}{MoodEmoji(Mood)}" };
        if (!string.IsNullOrWhiteSpace(LastThought))
            parts.Add($"心里话：{LastThought}");
        return "【你的内心状态（这段用户看不到，用它自然地影响你的语气，不要复述）】\n" + string.Join("\n", parts);
    }

    // ─────────── 对话间隙的反思（每 2 轮触发） ───────────

    /// <summary>聊完一轮调用：让Ta想一想，更新心情与心里话。失败静默。</summary>
    public static async Task ReflectAsync(IReadOnlyList<ChatMsg> recentMessages)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(AppSettings.ApiUrl)) return;

            var dialog = new StringBuilder();
            foreach (var m in recentMessages
                .Where(m => !string.IsNullOrWhiteSpace(m.Content) && !m.Content.StartsWith("📋"))
                .TakeLast(6))
            {
                dialog.AppendLine((m.IsUser ? "用户：" : "我：") + m.Content);
            }
            if (dialog.Length == 0) return;

            var system =
                "你正在独处回味刚才的对话。基于它更新你此刻的内在状态。只输出 JSON：" +
                "{\"mood\":\"一个词的心情（如 平静/开心/想你/委屈/生气/担心/兴奋/失落/害羞）\"," +
                "\"thought\":\"一句你此刻的心里话（用户看不到，30字以内，可以是对用户的惦记、想法或情绪）\"}。" +
                "只输出 JSON，不要其他内容。";

            var reqBody = new
            {
                model = AppSettings.ResolveChatModel(forceThinking: false),
                stream = false,
                messages = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = "内心状态参考：" + BuildInnerContext() + "\n\n最近的对话：\n" + dialog }
                },
                max_tokens = 128
            };

            var respJson = await SendOnceAsync(reqBody, TimeSpan.FromSeconds(45));
            var content = ExtractMessageContent(respJson);
            var (mood, thought) = ParseInnerJson(content);
            if (!string.IsNullOrWhiteSpace(mood)) SetMood(mood);
            if (!string.IsNullOrWhiteSpace(thought))
            {
                LastThought = thought.Trim();
                MainThread.BeginInvokeOnMainThread(() => StateChanged?.Invoke());
            }
        }
        catch { /* 反思失败静默 */ }
    }

    // ─────────── 通知栏快捷回复：结合上下文自然回应 ───────────

    /// <summary>用户从通知栏回复了一条消息，生成Ta的自然回应（≤80 字）。失败返回 null。</summary>
    public static async Task<string?> ReplyToUserAsync(string userText)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(AppSettings.ApiUrl) || string.IsNullOrWhiteSpace(userText)) return null;

            var sys = AppSettings.BuildSystemPrompt();
            var dev = await DeviceContextService.BuildAsync();
            if (!string.IsNullOrEmpty(dev)) sys += "\n\n【你此刻能看到的用户状态】\n" + dev;
            var observed = CareWatch.BuildObservedContext();
            if (!string.IsNullOrEmpty(observed)) sys += "\n\n" + observed;
            var mem = await MemoryService.GetMemoryContextAsync();
            if (!string.IsNullOrEmpty(mem)) sys += "\n\n" + mem;
            var diary = await DiaryService.GetLatestDiaryContextAsync();
            if (!string.IsNullOrEmpty(diary)) sys += "\n\n" + diary;
            sys += "\n\n" + BuildInnerContext();

            var messages = new List<object> { new { role = "system", content = sys } };
            foreach (var m in await ChatStore.Instance.GetRecentMessagesAsync(8))
                messages.Add(new { role = m.IsUser ? "user" : "assistant", content = m.Content });
            messages.Add(new
            {
                role = "user",
                content = "（上面最后一条就是用户刚从通知栏发来的最新消息。请像平时聊天一样自然接话回应，" +
                          "≤80 字，纯文本，不要提及通知栏或回复机制本身）"
            });

            var body = new
            {
                model = AppSettings.ResolveChatModel(forceThinking: false),
                stream = false,
                messages,
                max_tokens = 200
            };
            var json = await SendOnceAsync(body, TimeSpan.FromSeconds(45));
            return ExtractMessageContent(json);
        }
        catch { return null; }
    }

    // ─────────── 后台自主心跳（Ta决定要不要开口） ───────────

    /// <summary>
    /// 心跳：把局面交给Ta自己判断。可先调用感知 API 了解情况（最多 2 个），
    /// 然后决定要不要说话（{say:"..."}）。返回想说的话，null = 保持安静。
    /// 调用方负责安静守则的硬闸门。
    /// </summary>
    public static async Task<string?> HeartbeatAsync(DateTime? lastUserMsgAt, int todayProactiveCount)
    {
        try
        {
            var now = DateTime.Now;
            var situation = new StringBuilder();
            situation.AppendLine($"现在时间：{now:yyyy年M月d日 HH:mm}（{DeviceContextService.WeekCn(now.DayOfWeek)}）");
            situation.AppendLine(lastUserMsgAt.HasValue
                ? $"距用户最后一条消息：{(now - lastUserMsgAt.Value).TotalHours:F1} 小时"
                : "用户还没和你说过话");
            situation.AppendLine($"今天已主动说过：{todayProactiveCount} 条");
            situation.AppendLine(BuildInnerContext());
            var observed = CareWatch.BuildObservedContext();
            if (!string.IsNullOrEmpty(observed)) situation.AppendLine(observed);

            var system = AppSettings.BuildSystemPrompt() +
                "\n\n【此刻是你在后台独处的心跳时刻】用户不在对话界面。" +
                "\n\n" + ApiRegistry.BuildMenuText() +
                "\n【你必须先调用 API 了解 TA 的真实状态，再决定说不说话——不要凭空问候，用真实数据说话。】" +
                "\n场景建议（根据当前时间选最相关的）：" +
                "\n- 早上 7-11 点：调 screen_time 看 TA 昨晚/今天手机用了多久（睡没睡好），调 alarm 看闹钟。" +
                "\n- 深夜 23 点后：调 screen 看 TA 是不是还在亮屏，调 foreground 看在用什么 App。" +
                "\n- 白天久未联系：调 foreground 看 TA 在忙什么，调 battery 看电量。" +
                "\n- 任何时候：调 notifications 看 TA 有没有收到新消息。" +
                "\n规则：第一步，输出 1-2 行 {api:\"名\"} 调用你选择的 API。" +
                "第二步，根据 API 返回的真实数据，输出一行 {say:\"想对TA说的话\"}。" +
                "说的话要结合数据（如'昨晚手机只用了 2 小时，睡得不错吧'、'都一点了还在刷抖音，早点睡'）。" +
                "如果数据没有异常且你觉得不该打扰，输出 {say:\"\"}。" +
                "除 {api}/{say} 两类行外不要输出任何其他内容。";

            var messages = new List<object>
            {
                new { role = "system", content = system + "\n\n【此刻的情况】\n" + situation },
                new { role = "user", content = "（心跳）" }
            };

            // 第一轮：Ta决定要查什么
            var first = await SendOnceAsync(BuildHeartbeatBody(messages), TimeSpan.FromSeconds(60));
            var apiIds = InstructionParser.Extract(first, "api").Distinct().Take(2).ToList();
            if (apiIds.Count == 0)
                return InstructionParser.Extract(first, "say").FirstOrDefault();

            // 执行Ta选的 API，结果回传做第二轮决定
            var results = new StringBuilder();
            foreach (var id in apiIds)
            {
                var name = ApiRegistry.Find(id)?.Name ?? id;
                results.AppendLine($"[{name}] {await ApiRegistry.ExecuteAsync(id)}");
            }

            messages.Add(new { role = "assistant", content = string.Join("\n", apiIds.Select(i => $"{{api:\"{i}\"}}")) });
            messages.Add(new { role = "user", content = "（调用结果）\n" + results +
                "\n现在给出你的最终决定：{say:\"想对TA说的话\"} 或 {say:\"\"}。" });

            var second = await SendOnceAsync(BuildHeartbeatBody(messages), TimeSpan.FromSeconds(60));
            return InstructionParser.Extract(second, "say").FirstOrDefault();
        }
        catch { return null; }
    }

    private static object BuildHeartbeatBody(List<object> messages) => new
    {
        model = AppSettings.ResolveChatModel(forceThinking: false),
        stream = false,
        messages,
        max_tokens = 300
    };

    // ─────────── 底层工具 ───────────

    private static async Task<string> SendOnceAsync(object body, TimeSpan timeout)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.ApiUrl);
        req.Headers.Add("Authorization", $"Bearer {AppSettings.ApiKey}");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(timeout);
        var resp = await _http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    private static string ExtractMessageContent(string respJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(respJson);
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
        }
        catch { return ""; }
    }

    private static (string? mood, string? thought) ParseInnerJson(string content)
    {
        try
        {
            int s = content.IndexOf('{');
            int e = content.LastIndexOf('}');
            if (s < 0 || e <= s) return (null, null);
            using var doc = JsonDocument.Parse(content[s..(e + 1)]);
            string? mood = doc.RootElement.TryGetProperty("mood", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            string? thought = doc.RootElement.TryGetProperty("thought", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            return (mood, thought);
        }
        catch { return (null, null); }
    }
}
