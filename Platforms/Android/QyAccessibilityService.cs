using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.Views;
using Android.Views.Accessibility;

namespace 青阳AI;

/// <summary>
/// 无障碍通知感知（鸿蒙可用，无需 USB/Shizuku）：监听通知栏事件，
/// 把新到达的通知缓存进与 CareNotificationListener 共享的持久化缓冲。
/// 只读通知、不做任何点击/手势/滚动操作，风险面最小。
/// </summary>
[Service(Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE",
         Exported = true, Label = "青阳AI 通知感知（无障碍）")]
[IntentFilter(new[] { "android.accessibilityservice.AccessibilityService" })]
[MetaData("android.accessibilityservice", Resource = "@xml/qy_accessibility")]
public class QyAccessibilityService : AccessibilityService
{
    public override void OnAccessibilityEvent(AccessibilityEvent? e)
    {
        try
        {
            if (e == null) return;
            // 只关心通知类事件（新通知弹出）
            if (e.EventType != EventTypes.NotificationStateChanged) return;
            if (e.ParcelableData is not Android.App.Notification notif) return;
            if (e.PackageName == null || e.PackageName == PackageName) return;

            string title = "", text = "";
            var extras = notif.Extras;
            if (extras != null)
            {
                title = extras.GetString(Android.App.Notification.ExtraTitle) ?? "";
                text = extras.GetString(Android.App.Notification.ExtraText) ?? "";
            }
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(text)) return;

            CareNotificationListener.Store(e.PackageName?.ToString() ?? "", title, text);
        }
        catch { }
    }

    public override void OnInterrupt() { }

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        try
        {
            Preferences.Default.Set("NotifA11yConnected", DateTime.Now.ToString("HH:mm:ss"));
        }
        catch { }
    }

    /// <summary>无障碍服务是否已开启。</summary>
    public static bool IsEnabled()
    {
        try
        {
            var enabled = Android.Provider.Settings.Secure.GetString(
                global::Android.App.Application.Context.ContentResolver,
                Android.Provider.Settings.Secure.EnabledAccessibilityServices) ?? "";
            return enabled.Contains(global::Android.App.Application.Context.PackageName!, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>打开系统无障碍设置页。</summary>
    public static void OpenSettings()
    {
        try
        {
            var intent = new Intent(Android.Provider.Settings.ActionAccessibilitySettings);
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }
        catch { }
    }
}
