using Android.App;
using Android.App.Admin;
using Android.Content;
using Android.OS;

namespace 青阳AI;

/// <summary>青阳AI 设备管理器接收器（Super Admin 开关启用后生效）。仅用于系统级管理能力声明。</summary>
[BroadcastReceiver(Permission = "android.permission.BIND_DEVICE_ADMIN", Enabled = true, Exported = true)]
[MetaData("android.app.device_admin", Resource = "@xml/qy_device_admin")]
[IntentFilter(new[] { "android.app.action.DEVICE_ADMIN_ENABLED" })]
public class QyAdminReceiver : DeviceAdminReceiver
{
    public override void OnEnabled(Context? context, Intent? intent)
    {
        base.OnEnabled(context, intent);
        try { Preferences.Default.Set("QyAdminGranted", true); } catch { }
    }

    public override void OnDisabled(Context? context, Intent? intent)
    {
        base.OnDisabled(context, intent);
        try { Preferences.Default.Set("QyAdminGranted", false); } catch { }
    }
}
