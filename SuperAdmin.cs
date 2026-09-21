namespace 青阳AI;

/// <summary>
/// Super Admin 深度权限状态与引导（高风险，设置页独立开关控制并明示风险）。
/// - 存储访问：MANAGE_EXTERNAL_STORAGE，全盘读写文件；
/// - 设备管理器：DevicePolicyManager 系统级能力。
/// 两个都是 Android 特殊权限，需用户到系统设置手动开启（本类提供一键跳转）。
/// </summary>
public static class SuperAdmin
{
    // ── 存储访问 ──

    /// <summary>是否已授予全盘存储访问。</summary>
    public static bool IsStorageGranted()
    {
#if ANDROID
        try
        {
            return Android.OS.Environment.IsExternalStorageManager;
        }
        catch { return false; }
#else
        return false;
#endif
    }

    /// <summary>打开系统「所有文件访问」设置页。</summary>
    public static void OpenStorageSettings()
    {
#if ANDROID
        try
        {
            var intent = new Android.Content.Intent(
                Android.Provider.Settings.ActionManageAllFilesAccessPermission,
                Android.Net.Uri.Parse("package:" + Android.App.Application.Context.PackageName));
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
        }
        catch { }
#endif
    }

    // ── 设备管理器 ──

    /// <summary>设备管理器是否已激活。</summary>
    public static bool IsAdminActive()
    {
#if ANDROID
        try
        {
            var dpm = (Android.App.Admin.DevicePolicyManager?)Android.App.Application.Context
                .GetSystemService(Android.Content.Context.DevicePolicyService);
            if (dpm == null) return false;
            var cn = new Android.Content.ComponentName(Android.App.Application.Context,
                Java.Lang.Class.FromType(typeof(QyAdminReceiver)));
            return dpm.IsAdminActive(cn);
        }
        catch { return false; }
#else
        return false;
#endif
    }

    /// <summary>拉起系统「激活设备管理器」界面。</summary>
    public static void RequestAdmin()
    {
#if ANDROID
        try
        {
            var cn = new Android.Content.ComponentName(Android.App.Application.Context,
                Java.Lang.Class.FromType(typeof(QyAdminReceiver)));
            var intent = new Android.Content.Intent(
                Android.App.Admin.DevicePolicyManager.ActionAddDeviceAdmin);
            intent.PutExtra(Android.App.Admin.DevicePolicyManager.ExtraDeviceAdmin, cn);
            intent.PutExtra(Android.App.Admin.DevicePolicyManager.ExtraAddExplanation,
                "青阳AI 需要设备管理器权限以提供系统级管理能力（高风险，可锁屏/清数据等）。仅在你信任时启用。");
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
        }
        catch { }
#endif
    }

    /// <summary>解除设备管理器激活。</summary>
    public static void RemoveAdmin()
    {
#if ANDROID
        try
        {
            var dpm = (Android.App.Admin.DevicePolicyManager?)Android.App.Application.Context
                .GetSystemService(Android.Content.Context.DevicePolicyService);
            if (dpm == null) return;
            var cn = new Android.Content.ComponentName(Android.App.Application.Context,
                Java.Lang.Class.FromType(typeof(QyAdminReceiver)));
            if (dpm.IsAdminActive(cn))
                dpm.RemoveActiveAdmin(cn);
        }
        catch { }
#endif
    }
}
