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

    /// <summary>
    /// 当前系统有没有「所有文件访问」这个特殊权限。
    /// MANAGE_EXTERNAL_STORAGE 是 Android 11（API 30）才引入的，
    /// 在 Android 10 及以下的系统上：
    ///   · Environment.IsExternalStorageManager 这个 API 根本不存在（调用会抛异常）
    ///   · 系统设置里也没有「所有文件访问」这个页面（跳转必然失败）
    /// 所以低版本必须走普通存储权限那条路，不能拿这个开关判定。
    /// </summary>
    public static bool HasAllFilesAccessApi
    {
        get
        {
#if ANDROID
            try { return Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.R; }
            catch { return false; }
#else
            return false;
#endif
        }
    }

    /// <summary>当前系统的版本号文本，用于界面提示/排查。</summary>
    public static string AndroidVersionText
    {
        get
        {
#if ANDROID
            try
            {
                var v = Android.OS.Build.VERSION.Release ?? "";
                var sdk = (int)Android.OS.Build.VERSION.SdkInt;
                return string.IsNullOrEmpty(v) ? ("API " + sdk) : ("Android " + v + "（API " + sdk + "）");
            }
            catch { return "未知"; }
#else
            return "非 Android";
#endif
        }
    }

    /// <summary>
    /// 是否已获得「全盘读写」能力。按系统版本走两条完全不同的判定：
    ///   · Android 11+ ：查 MANAGE_EXTERNAL_STORAGE（所有文件访问）
    ///   · Android 10- ：没有那个开关，查普通存储权限
    ///     （配合 manifest 的 requestLegacyExternalStorage，拿到后同样是全盘可读写）
    /// </summary>
    public static bool IsStorageGranted()
    {
#if ANDROID
        try
        {
            if (HasAllFilesAccessApi)
                return Android.OS.Environment.IsExternalStorageManager;

            var ctx = Android.App.Application.Context;
            return ctx.CheckSelfPermission(Android.Manifest.Permission.ReadExternalStorage)
                       == Android.Content.PM.Permission.Granted
                || ctx.CheckSelfPermission(Android.Manifest.Permission.WriteExternalStorage)
                       == Android.Content.PM.Permission.Granted;
        }
        catch { return false; }
#else
        return false;
#endif
    }

    /// <summary>打开系统「所有文件访问」设置页（仅 Android 11+ 有效）。</summary>
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

    /// <summary>
    /// 打开本应用的系统详情页（权限入口）。
    /// Android 10 及以下没有「所有文件访问」页面，用这个兜底，
    /// 用户可以在里面手动开「存储」权限。
    /// </summary>
    public static void OpenAppSettings()
    {
#if ANDROID
        try
        {
            var intent = new Android.Content.Intent(
                Android.Provider.Settings.ActionApplicationDetailsSettings,
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
