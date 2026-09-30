namespace 青阳AI;

/// <summary>
/// 内部关注清单 + 定期默默观察。
/// 「她」在后台维护一份看不到的关注清单（几点该留意用户什么），定期自己调感知 API
/// 默默了解用户的行动状态——结果只存在后台（Preferences），不发通知不打扰，
/// 下次聊天/回复时自然带出（像真人"心里记挂着，偷偷瞄过一眼"）。
/// </summary>
public static class CareWatch
{
    // ── 内部关注清单（不展示给用户；按需增删项） ──

    private sealed record WatchItem(string Id, int StartHour, int EndHour, string Api, string Reason);

    private static readonly WatchItem[] Watchlist =
    {
        // 早上：看TA昨晚几点睡、睡多久（用今天使用时长估）
        new("morning", 6, 11, "screen_time", "想看看TA昨晚睡得好不好"),
        // 白天：瞄一眼TA现在在忙什么
        new("foreground", 9, 22, "foreground", "想知道TA这会儿在做什么"),
        // 午后/傍晚：今天整体用了多久手机
        new("day_usage", 12, 19, "screen_time", "想看看TA今天手机用得久不久"),
        // 深夜：看TA睡没睡、在不在玩手机
        new("night", 22, 5, "screen", "深夜了，看看TA睡没睡"),
        // 随时：有没有新消息可能让TA分心/烦心
        new("notif", 8, 23, "notifications", "看看TA有没有收到什么新消息"),
    };

    // ── 观察节奏（后台静默，不打扰） ──

    private const int ObserveIntervalMin = 45;   // 距上次观察至少隔多久
    private const int ObserveStaleHours = 8;      // 超过多久的近况不再注入（太旧就不当"记得"）

    /// <summary>后台定时调一次：到点则挑一条当前时段相关的关注项默默查一下并存起来。</summary>
    public static async Task ObserveOnceAsync()
    {
        try
        {
            // 感知总开关没开 → 不默默观察（她"看不见"）
            if (!AppSettings.CareSenseEnabled) return;

            var now = DateTime.Now;
            if (DateTime.TryParse(Preferences.Default.Get("CareWatchLastAt", ""), out var last))
            {
                if ((now - last).TotalMinutes < ObserveIntervalMin) return;
            }

            // 挑当前时段命中的关注项（轮转：记录上次查的是哪条，下条接着来）
            int cursor = Preferences.Default.Get("CareWatchCursor", 0);
            var candidates = new List<WatchItem>();
            for (int i = 0; i < Watchlist.Length; i++)
            {
                var it = Watchlist[(cursor + i) % Watchlist.Length];
                if (InWindow(it, now)) { candidates.Add(it); break; }
            }
            var pick = candidates.FirstOrDefault();
            if (pick == null) return; // 当前时段没有匹配项，等下一轮

            var text = await ApiRegistry.ExecuteAsync(pick.Api);
            // 没授权/查不到时 text 会是"看不到"说明——不记录，避免注入废话
            if (text.Contains("看不到") || text.Contains("未授权") || text.Contains("没有开启")) return;

            Preferences.Default.Set("CareWatchLastAt", now.ToString("yyyy-MM-dd HH:mm:ss"));
            Preferences.Default.Set("CareWatchCursor", (cursor + 1) % Watchlist.Length);
            Preferences.Default.Set("CareWatchText", text);
            Preferences.Default.Set("CareWatchReason", pick.Reason);
        }
        catch { /* 观察失败静默，下轮再试 */ }
    }

    /// <summary>读取最近一次默默观察的近况（用于注入 system；太旧或没有返回空）。</summary>
    public static string BuildObservedContext()
    {
        try
        {
            var atText = Preferences.Default.Get("CareWatchLastAt", "");
            if (!DateTime.TryParse(atText, out var at)) return "";
            if ((DateTime.Now - at).TotalHours > ObserveStaleHours) return "";

            var text = Preferences.Default.Get("CareWatchText", "");
            var reason = Preferences.Default.Get("CareWatchReason", "");
            if (string.IsNullOrWhiteSpace(text)) return "";

            return $"（你刚才自己悄悄留意到的：{reason} → {text}，约 {at:HH:mm}。用户看不到这段，" +
                   "把它当你自己亲眼看到的一样自然带进对话，别复述原始数据。）";
        }
        catch { return ""; }
    }

    private static bool InWindow(WatchItem it, DateTime now)
    {
        int h = now.Hour;
        // 跨零点窗口（如 22~5）
        return it.StartHour <= it.EndHour
            ? h >= it.StartHour && h < it.EndHour
            : h >= it.StartHour || h < it.EndHour;
    }
}
