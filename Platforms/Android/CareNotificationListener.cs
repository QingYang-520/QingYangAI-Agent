using Android.App;
using Android.Content;
using Android.OS;
using Android.Service.Notification;
using AndroidX.Core.App;

namespace 青阳AI;

/// <summary>
/// 通知使用权监听：把最近收到的新消息通知缓存进内存环形缓冲，供"Ta"感知
/// （如微信来消息了）。只保留真正的新消息，跳过自己/常驻/汇总通知。
/// 需用户在「设置→通知使用权」里开启本应用，由 CareNotificationListener.OpenSettings() 跳转。
/// </summary>
[Service(Permission = "android.permission.BIND_NOTIFICATION_LISTENER_SERVICE",
         Exported = true, Label = "青阳AI 通知感知")]
[IntentFilter(new[] { "android.service.notification.NotificationListenerService" })]
public class CareNotificationListener : NotificationListenerService
{
    private const int MaxItems = 30;
    private const long KeepMs = 30 * 60 * 1000; // 保留最近 30 分钟（含持久化，重启不丢）

    // android.app.Notification.FLAG_GROUP_SUMMARY（绑定库未暴露该常量，取 AOSP 定值）
    private const NotificationFlags FlagGroupSummary = (NotificationFlags)0x2;

    private static Context Ctx => global::Android.App.Application.Context;

    public override void OnListenerConnected()
    {
        base.OnListenerConnected();
        // 服务被系统真正绑定时触发——记录到本地，设置页据此判断"服务是否真的在跑"
        try { Preferences.Default.Set("NotifSvcConnected", DateTime.Now.ToString("HH:mm:ss")); } catch { }
    }

    public override void OnNotificationPosted(StatusBarNotification? sbn)
    {
        try
        {
            if (sbn?.Notification == null) return;
            if (sbn.PackageName == Ctx.PackageName) return; // 自己的通知不算
            if (sbn.IsOngoing) return;                      // 常驻通知（音乐/下载等）跳过
            if ((sbn.Notification.Flags & FlagGroupSummary) != 0)
                return;                                     // 汇总通知跳过

            string title = "", text = "";
            var extras = sbn.Notification.Extras;
            if (extras != null)
            {
                title = extras.GetString(Notification.ExtraTitle) ?? "";
                text = extras.GetString(Notification.ExtraText) ?? "";
            }
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(text)) return;

            Store(sbn.PackageName ?? "", title, text);
        }
        catch { }
    }

    public override void OnNotificationRemoved(StatusBarNotification? sbn) { }

    /// <summary>通知使用权是否已开启。</summary>
    public static bool IsEnabled()
    {
        try
        {
            var enabled = NotificationManagerCompat.GetEnabledListenerPackages(Ctx);
            return enabled?.Contains(Ctx.PackageName!) == true;
        }
        catch { return false; }
    }

    /// <summary>打开系统「通知使用权」设置页。</summary>
    public static void OpenSettings()
    {
        try
        {
            var intent = new Intent(Android.Provider.Settings.ActionNotificationListenerSettings);
            intent.AddFlags(ActivityFlags.NewTask);
            Ctx.StartActivity(intent);
        }
        catch { }
    }

    // ── 持久化环形缓冲（Preferences：JSON 数组，重启/服务回收不丢） ──

    private const string StoreKey = "RecentNotif";

    /// <summary>存入一条通知（供通知使用权服务与无障碍服务共用）。</summary>
    public static void Store(string app, string title, string text)
    {
        try
        {
            var list = Load();
            list.Add((Java.Lang.JavaSystem.CurrentTimeMillis(), app, title, text));
            if (list.Count > MaxItems) list.RemoveRange(0, list.Count - MaxItems);
            Save(list);
        }
        catch { }
    }

    private static List<(long time, string app, string title, string text)> Load()
    {
        try
        {
            var json = Preferences.Default.Get(StoreKey, "[]");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var list = new List<(long, string, string, string)>();
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return list;
            foreach (var it in doc.RootElement.EnumerateArray())
            {
                try
                {
                    list.Add((it[0].GetInt64(), it[1].GetString() ?? "", it[2].GetString() ?? "", it[3].GetString() ?? ""));
                }
                catch { }
            }
            return list;
        }
        catch { return new List<(long, string, string, string)>(); }
    }

    private static void Save(List<(long time, string app, string title, string text)> list)
    {
        try
        {
            var arr = new System.Text.Json.Nodes.JsonArray();
            foreach (var (t, a, ti, tx) in list)
            {
                arr.Add(new System.Text.Json.Nodes.JsonArray(t, a, ti, tx));
            }
            Preferences.Default.Set(StoreKey, arr.ToJsonString());
        }
        catch { }
    }

    /// <summary>取最近 minutes 分钟内的新消息通知摘要（最多 max 条）。持久化存储，重启不丢。</summary>
    public static List<string> GetRecentSummaries(int minutes = 5, int max = 5)
    {
        var result = new List<string>();
        long cutoff = Java.Lang.JavaSystem.CurrentTimeMillis() - minutes * 60_000L;
        foreach (var (time, app, title, text) in Load())
        {
            if (time < cutoff) continue;
            var line = $"{DeviceContextService.GetAppLabel(app)}：{title}".Trim();
            if (!string.IsNullOrWhiteSpace(text))
                line += " " + text.Trim();
            if (line.Length > 80) line = line[..80];
            result.Add(line);
            if (result.Count >= max) break;
        }
        return result;
    }
}
