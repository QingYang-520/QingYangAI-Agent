namespace 青阳AI;

/// <summary>
/// Shizuku 连接状态与权限辅助。
/// </summary>
public static class ShizukuHelper
{
    public const string ShizukuPermission = "moe.shizuku.manager.permission.API_V23";

#if ANDROID
    private static bool Ping()
    {
        try { return Rikka.Shizuku.Shizuku.PingBinder(); }
        catch { return false; }
    }
#endif

    /// <summary>Shizuku adb 服务是否在线（ping 成功）。</summary>
    public static bool IsOnline
    {
        get
        {
#if ANDROID
            return Ping();
#else
            return false;
#endif
        }
    }

    public static string StatusText => IsOnline ? "online" : "offline";

    /// <summary>Shizuku 权限是否已授予。离线=必然未授权。</summary>
    public static bool IsPermissionGranted
    {
        get
        {
#if ANDROID
            if (!IsOnline) return false;
            try { return Rikka.Shizuku.Shizuku.CheckSelfPermission() == 0; }
            catch { return false; }
#else
            return false;
#endif
        }
    }

    /// <summary>请求 Shizuku 授权（拉起 Shizuku 授权界面）。</summary>
    public static void RequestPermission()
    {
#if ANDROID
        try
        {
            if (!IsOnline) return;
            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.M)
                Rikka.Shizuku.Shizuku.RequestPermission(ShizukuRunner.PermissionRequestCode);
        }
        catch { }
#endif
    }
}
