namespace 青阳AI;

/// <summary>
/// Shizuku 生命周期与授权结果管理。
/// 在应用启动时把本应用注册进 Shizuku 的授权监听，使「青阳AI」
/// 能正常出现在 Shizuku 的授权队列并通过系统弹窗完成授权。
/// </summary>
public static class ShizukuLifecycle
{
    private static bool _initialized;

    /// <summary>是否已授予 Shizuku 权限（持久化）。</summary>
    public static bool IsGranted
    {
        get => Preferences.Default.Get("ShizukuGranted", false);
        private set => Preferences.Default.Set("ShizukuGranted", value);
    }

    /// <summary>在应用启动时调用一次，注册 Shizuku 监听。</summary>
    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;

#if ANDROID
        try
        {
            // 1) 监听 binder 就绪：Shizuku 服务已连接
            Rikka.Shizuku.Shizuku.AddBinderReceivedListenerSticky(
                new BinderListener());

            // 2) 监听授权结果：用户通过系统弹窗授权后写回状态
            Rikka.Shizuku.Shizuku.AddRequestPermissionResultListener(
                new PermissionListener());
        }
        catch { }
#endif
    }

#if ANDROID
    /// <summary>Shizuku binder 就绪回调。</summary>
    private sealed class BinderListener : Java.Lang.Object,
        Rikka.Shizuku.Shizuku.IOnBinderReceivedListener
    {
        public void OnBinderReceived()
        {
            // binder 到达，Shizuku 可 ping 通
        }
    }

    /// <summary>授权结果回调。</summary>
    private sealed class PermissionListener : Java.Lang.Object,
        Rikka.Shizuku.Shizuku.IOnRequestPermissionResultListener
    {
        public void OnRequestPermissionResult(int requestCode, int grantResult)
        {
            // 后台线程回调：切到主线程更新状态
            MainThread.BeginInvokeOnMainThread(() =>
            {
                // 0 = 允许；-1 = 拒绝
                IsGranted = grantResult == 0;
                if (requestCode == ShizukuRunner.PermissionRequestCode)
                    ShizukuRunner.OnPermissionResult(grantResult == 0);
            });
        }
    }
#endif
}
