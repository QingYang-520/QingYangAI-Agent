using Android.App.Usage;
using Android.Content;
using Java.Util;

namespace 青阳AI;

/// <summary>
/// 使用情况感知（免 Shizuku）：读取前台应用与各 App 使用时长。
/// 权限：PACKAGE_USAGE_STATS，用户需在「设置→特殊应用权限→使用情况访问权」里授权，
/// 由 UsageStatsHelper.OpenSettings() 一键跳转。
/// </summary>
public static class UsageStatsHelper
{
    private static Context Ctx => global::Android.App.Application.Context;

    /// <summary>是否已授予「使用情况访问权」。</summary>
    public static bool IsGranted()
    {
        try
        {
            var appOps = (Android.App.AppOpsManager?)Ctx.GetSystemService(Context.AppOpsService);
            if (appOps == null) return false;
            var mode = appOps.CheckOpNoThrow(
                Android.App.AppOpsManager.OpstrGetUsageStats,
                Android.OS.Process.MyUid(),
                Ctx.PackageName!);
            return mode == Android.App.AppOpsManagerMode.Allowed;
        }
        catch { return false; }
    }

    /// <summary>打开系统「使用情况访问权」设置页。</summary>
    public static void OpenSettings()
    {
        try
        {
            var intent = new Intent(Android.Provider.Settings.ActionUsageAccessSettings);
            intent.AddFlags(ActivityFlags.NewTask);
            Ctx.StartActivity(intent);
        }
        catch { }
    }

    /// <summary>最近约 1 分钟内的前台应用包名（queryEvents 判定，拿不到返回 null）。</summary>
    public static string? GetForegroundPackage()
    {
        try
        {
            var usm = (UsageStatsManager?)Ctx.GetSystemService(Context.UsageStatsService);
            if (usm == null) return null;

            long now = Java.Lang.JavaSystem.CurrentTimeMillis();
            var events = usm.QueryEvents(now - 60_000, now);
            string? lastResumed = null;
            var evt = new UsageEvents.Event();
            while (events.HasNextEvent)
            {
                if (events.GetNextEvent(evt) &&
                    evt.EventType == UsageEventType.ActivityResumed)
                    lastResumed = evt.PackageName;
            }
            return lastResumed;
        }
        catch { return null; }
    }

    /// <summary>今天前台使用时长 TopN，返回 (应用显示名, 时长)。</summary>
    public static List<(string app, TimeSpan time)> GetTodayTopApps(int topN = 3)
    {
        var result = new List<(string, TimeSpan)>();
        try
        {
            var usm = (UsageStatsManager?)Ctx.GetSystemService(Context.UsageStatsService);
            if (usm == null) return result;

            var cal = Calendar.Instance!;
            cal.TimeInMillis = Java.Lang.JavaSystem.CurrentTimeMillis();
            cal.Set(CalendarField.HourOfDay, 0);
            cal.Set(CalendarField.Minute, 0);
            cal.Set(CalendarField.Second, 0);
            cal.Set(CalendarField.Millisecond, 0);
            long dayStart = cal.TimeInMillis;
            long now = Java.Lang.JavaSystem.CurrentTimeMillis();

            var stats = usm.QueryUsageStats(UsageStatsInterval.Daily, dayStart, now);
            if (stats == null) return result;

            var agg = new Dictionary<string, long>();
            foreach (var s in stats)
            {
                if (string.IsNullOrEmpty(s.PackageName)) continue;
                agg.TryGetValue(s.PackageName, out var t);
                agg[s.PackageName] = t + s.TotalTimeInForeground;
            }

            result = agg
                .Where(kv => kv.Value > 60_000) // 忽略不足 1 分钟的
                .OrderByDescending(kv => kv.Value)
                .Take(topN)
                .Select(kv => (DeviceContextService.GetAppLabel(kv.Key), TimeSpan.FromMilliseconds(kv.Value)))
                .ToList();
        }
        catch { }
        return result;
    }
}
