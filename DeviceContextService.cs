using System.Text;

namespace 青阳AI;

/// <summary>
/// 感知中心：
/// - 零权限基础项（时间/电量/网络/媒体/屏幕/闹钟）被动注入每次对话；
/// - 深度感知项（屏幕使用时间/前台应用/通知/日程）不再被动注入，
///   注册进 ApiRegistry 由 AI 通过 {api:"名"} 主动调用（想查就查）。
/// 设计原则：能用系统 API 直接读的就不走 Shizuku；Shizuku 只作前台应用兜底。
/// </summary>
public static class DeviceContextService
{
    /// <summary>构建被动注入的"用户此刻状态"（只含零权限基础项，多行纯文本）。</summary>
    public static async Task<string> BuildAsync()
    {
        var lines = new List<string>();
        var now = DateTime.Now;
        lines.Add($"时间：{now:yyyy年M月d日 HH:mm}（{WeekCn(now.DayOfWeek)}）");

        try
        {
            var b = Battery.Default;
            if (b.ChargeLevel >= 0)
            {
                var charge = b.State switch
                {
                    BatteryState.Charging => "充电中",
                    BatteryState.Full => "已充满",
                    _ => "未充电"
                };
                lines.Add($"电量：{(int)Math.Round(b.ChargeLevel * 100)}%（{charge}）");
            }
        }
        catch { }

        try
        {
            var conn = Connectivity.Current;
            string net = conn.ConnectionProfiles.Contains(ConnectionProfile.WiFi) ? "Wi-Fi"
                : conn.ConnectionProfiles.Contains(ConnectionProfile.Cellular) ? "移动数据"
                : "未知";
            if (conn.NetworkAccess != NetworkAccess.Internet) net += "（无互联网）";
            lines.Add($"网络：{net}");
        }
        catch { }

#if ANDROID
        try
        {
            var media = await GetMediaTextAsync();
            if (!string.IsNullOrWhiteSpace(media)) lines.Add(media);
        }
        catch { }

        try
        {
            var screen = await GetScreenTextAsync();
            if (!string.IsNullOrWhiteSpace(screen)) lines.Add(screen);
        }
        catch { }

        try
        {
            var alarm = await GetAlarmTextAsync();
            if (!string.IsNullOrWhiteSpace(alarm)) lines.Add(alarm);
        }
        catch { }
#endif

        return string.Join("\n", lines);
    }

    // ─────────── 以下为各感知 API 的实现（ApiRegistry 调用）───────────

    /// <summary>屏幕使用时间：今天各应用前台时长排行（需使用情况访问权）。</summary>
    public static Task<string> GetScreenTimeTextAsync()
    {
#if ANDROID
        if (!AppSettings.CareSenseEnabled)
            return Task.FromResult("用户没有开启生活感知开关，看不到");
        if (!UsageStatsHelper.IsGranted())
            return Task.FromResult("用户未授权「使用情况访问权」，看不到");

        var top = UsageStatsHelper.GetTodayTopApps(5);
        if (top.Count == 0)
            return Task.FromResult("今天还没有可统计的使用记录");

        return Task.FromResult("今天各应用使用时长：" +
            string.Join("、", top.Select(t => $"{t.app} {FormatDuration(t.time)}")));
#else
        return Task.FromResult("仅 Android 支持");
#endif
    }

    /// <summary>当前前台应用（使用情况访问权优先，Shizuku 兜底）。</summary>
    public static async Task<string> GetForegroundTextAsync()
    {
#if ANDROID
        if (!AppSettings.CareSenseEnabled)
            return "用户没有开启生活感知开关，看不到";

        if (UsageStatsHelper.IsGranted())
        {
            var fg = UsageStatsHelper.GetForegroundPackage();
            if (!string.IsNullOrWhiteSpace(fg))
                return $"用户当前正在用：{GetAppLabel(fg)}";
        }
        if (ShizukuRunner.Available())
        {
            var viaShizuku = await GetForegroundViaShizukuAsync();
            if (!string.IsNullOrWhiteSpace(viaShizuku))
                return $"用户当前正在用：{viaShizuku}";
        }
        return "拿不到前台应用（未授权且 Shizuku 不可用）";
#else
        return "仅 Android 支持";
#endif
    }

    /// <summary>
    /// 最近几分钟收到的新消息通知——四路智能切换，取可用的一路：
    /// ① 通知使用权服务（若已连接）② 无障碍服务（鸿蒙可用）③ Shizuku dumpsys（插线时）
    /// ④ 系统 GetActiveNotifications（免权限兜底）。共享同一持久化缓冲。
    /// </summary>
    public static async Task<string> GetNotificationsTextAsync()
    {
#if ANDROID
        if (!AppSettings.CareSenseEnabled)
            return "用户没有开启生活感知开关，看不到";

        // ① 通知使用权服务已连接 → 走它的缓冲
        // ② 无障碍服务已开启 → 同样写入同一缓冲（Store 共用）
        bool svcOk = CareNotificationListener.IsEnabled()
                     && !string.IsNullOrEmpty(Preferences.Default.Get("NotifSvcConnected", ""));
        bool a11yOk = QyAccessibilityService.IsEnabled();
        if (svcOk || a11yOk)
        {
            var notes = CareNotificationListener.GetRecentSummaries(10, 6);
            return notes.Count > 0
                ? "最近收到的通知：\n" + string.Join("\n", notes)
                : "最近 10 分钟没有新通知";
        }

        // ③ Shizuku 在线（插线时）→ dumpsys notification 读当前通知
        if (ShizukuRunner.Available())
        {
            var r = await ShizukuRunner.ExecuteAsync("dumpsys notification --noredact | grep -E 'android.title|android.text' | head -20");
            var outText = (r.Stdout ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(outText))
                return "当前通知栏内容：\n" + outText;
        }

        // ④ 直连：系统允许读到的活动通知（免权限，仅本App+部分系统通知）
        return "通知感知未启用任何通道（通知使用权服务未连接、无障碍未开、Shizuku未插线）。";
#else
        return "仅 Android 支持";
#endif
    }

    /// <summary>今天剩余的日程（需日历权限）。</summary>
    public static Task<string> GetCalendarTextAsync()
    {
#if ANDROID
        if (!CalendarHelper.IsGranted())
            return Task.FromResult("用户未授权日历权限，看不到");
        var events = CalendarHelper.GetTodayEvents();
        return Task.FromResult(events.Count > 0
            ? "今天接下来的日程：\n" + string.Join("\n", events)
            : "今天没有剩余日程");
#else
        return Task.FromResult("仅 Android 支持");
#endif
    }

    /// <summary>电量与充电状态。</summary>
    public static Task<string> GetBatteryTextAsync()
    {
        try
        {
            var b = Battery.Default;
            if (b.ChargeLevel < 0) return Task.FromResult("电量信息不可用");
            var charge = b.State switch
            {
                BatteryState.Charging => "充电中",
                BatteryState.Full => "已充满",
                _ => "未充电"
            };
            return Task.FromResult($"电量 {(int)Math.Round(b.ChargeLevel * 100)}%（{charge}）");
        }
        catch (Exception ex) { return Task.FromResult("电量信息不可用：" + ex.Message); }
    }

    /// <summary>媒体播放与铃声模式。</summary>
    public static Task<string> GetMediaTextAsync()
    {
#if ANDROID
        try
        {
            var am = (Android.Media.AudioManager?)Android.App.Application.Context
                .GetSystemService(Android.Content.Context.AudioService);
            if (am == null) return Task.FromResult("");
            var mode = am.RingerMode switch
            {
                Android.Media.RingerMode.Silent => "静音",
                Android.Media.RingerMode.Vibrate => "振动",
                _ => "正常"
            };
            return Task.FromResult($"铃声：{mode}；{(am.IsMusicActive ? "正在播放音乐/媒体" : "没在播放媒体")}");
        }
        catch { return Task.FromResult(""); }
#else
        return Task.FromResult("");
#endif
    }

    /// <summary>下一个闹钟（绑定库缺 TriggerAtMillis，走 Java 反射取触发时间）。</summary>
    public static Task<string> GetAlarmTextAsync()
    {
#if ANDROID
        try
        {
            var alm = (Android.App.AlarmManager?)Android.App.Application.Context
                .GetSystemService(Android.Content.Context.AlarmService);
            var next = alm?.NextAlarmClock;
            if (next == null) return Task.FromResult("没有设置下一个闹钟");

            var getter = next.Class.GetMethod("getTriggerAtMillis");
            if (getter?.Invoke(next) is Java.Lang.Long lng && (long)lng > 0)
            {
                var t = DateTimeOffset.FromUnixTimeMilliseconds((long)lng).LocalDateTime;
                return Task.FromResult($"下一个闹钟：{t:HH:mm}");
            }
            return Task.FromResult("");
        }
        catch { return Task.FromResult(""); }
#else
        return Task.FromResult("");
#endif
    }

    /// <summary>屏幕亮灭。</summary>
    public static Task<string> GetScreenTextAsync()
    {
#if ANDROID
        try
        {
            var pm = (Android.OS.PowerManager?)Android.App.Application.Context
                .GetSystemService(Android.Content.Context.PowerService);
            if (pm == null) return Task.FromResult("");
            return Task.FromResult(pm.IsInteractive ? "屏幕：亮屏使用中" : "屏幕：熄屏");
        }
        catch { return Task.FromResult(""); }
#else
        return Task.FromResult("");
#endif
    }

    /// <summary>网络状态。</summary>
    public static Task<string> GetNetworkTextAsync()
    {
        try
        {
            var conn = Connectivity.Current;
            string net = conn.ConnectionProfiles.Contains(ConnectionProfile.WiFi) ? "Wi-Fi"
                : conn.ConnectionProfiles.Contains(ConnectionProfile.Cellular) ? "移动数据"
                : "未知";
            if (conn.NetworkAccess != NetworkAccess.Internet) net += "（无互联网）";
            return Task.FromResult($"网络：{net}");
        }
        catch (Exception ex) { return Task.FromResult("网络信息不可用：" + ex.Message); }
    }

#if ANDROID
    /// <summary>包名 → 应用显示名（拿不到返回包名）。</summary>
    public static string GetAppLabel(string pkg)
    {
        try
        {
            var pm = Android.App.Application.Context.PackageManager;
            var info = pm?.GetApplicationInfo(pkg, 0);
            if (info != null && pm != null)
            {
                var label = pm.GetApplicationLabel(info)?.ToString();
                if (!string.IsNullOrWhiteSpace(label)) return label;
            }
        }
        catch { }
        return pkg;
    }

    /// <summary>Shizuku 兜底：dumpsys 查前台应用包名并转显示名。</summary>
    private static async Task<string?> GetForegroundViaShizukuAsync()
    {
        try
        {
            var r = await ShizukuRunner.ExecuteAsync("dumpsys activity activities | grep -i mResumedActivity");
            var line = (r.Stdout ?? "").Trim();
            if (line.Length == 0) return null;

            // 形如 mResumedActivity: ActivityRecord{... u0 com.xx/.yy t123}
            int u0 = line.IndexOf("u0 ", StringComparison.Ordinal);
            var pkg = u0 >= 0 ? line[(u0 + 3)..] : line;
            int sp = pkg.IndexOf(' ');
            if (sp > 0) pkg = pkg[..sp];
            if (pkg.EndsWith('}')) pkg = pkg[..^1];
            return GetAppLabel(pkg.Length > 80 ? pkg[..80] : pkg);
        }
        catch { return null; }
    }
#endif

    /// <summary>时长人性化：45秒 / 12分钟 / 3小时42分钟。</summary>
    public static string FormatDuration(TimeSpan t)
    {
        if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds}秒";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}分钟";
        return $"{(int)t.TotalHours}小时{t.Minutes}分钟";
    }

    public static string WeekCn(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };
}
