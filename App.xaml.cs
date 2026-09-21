namespace 青阳AI;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        AppSettings.MigrateLegacyModelConfig();  // 旧版配置迁移到「配置1」
        MarkOldUserQuickStartDone();   // 升级上来的老用户，已配好模型的不再强制走快速开始
        ShizukuLifecycle.Init();   // Android 下注册 Shizuku 授权监听
        PrewarmUi();               // 启动时后台预加载设置页用到的重资源，避免打开设置卡顿
        HookCrashLogging();        // 记录未处理异常，下次打开时弹窗展示（排查闪退用）
        ScheduleCareIfEnabled();   // 启动时排期主动关心心跳（修复开机后不触发 bug）
    }

    /// <summary>
    /// 老用户升级兼容：升级前已经同意过协议的用户，若模型已配好则视为引导已完成；
    /// 没配好的仍然进快速开始（正好帮他把模型接上）。全新安装不受影响。
    /// </summary>
    private static void MarkOldUserQuickStartDone()
    {
        try
        {
            if (!Preferences.ContainsKey("QuickStartDone") && Preferences.Get("UserAgreePrivacy", false))
                Preferences.Set("QuickStartDone", !string.IsNullOrWhiteSpace(AppSettings.ApiKey));
        }
        catch { }
    }

    /// <summary>如果主动关心已开启，启动时立即排期 AlarmManager 心跳（否则只有设置页开关时才排期）。</summary>
    private void ScheduleCareIfEnabled()
    {
#if ANDROID
        try
        {
            if (AppSettings.CareEnabled)
            {
                var ctx = Android.App.Application.Context;
                CareReceiver.EnsureScheduled(ctx);
            }
        }
        catch { }
#endif
    }

    /// <summary>把未处理异常写进 Preferences，下次打开聊天页时展示一次（排查真机闪退）。</summary>
    private void HookCrashLogging()
    {
        System.AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                var msg = e.ExceptionObject switch
                {
                    Exception ex => ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace,
                    object o => o?.ToString() ?? "未知异常",
                    null => "未知异常"
                };
                if (msg.Length > 900) msg = msg[..900];
                Preferences.Default.Set("LastCrash", DateTime.Now.ToString("MM-dd HH:mm:ss") + "\n" + msg);
            }
            catch { }
        };
    }

    /// <summary>应用启动时后台预热设置页相关资源（图标路径解析、控件树构建等）。</summary>
    private void PrewarmUi()
    {
        _ = Task.Run(() =>
        {
            try
            {
                MorphIcon.Prewarm();      // 图标 SVG 路径 → Geometry 解析
                StatusBadge.Prewarm();    // 状态徽章控件树构建
                // 其它重资源可在此追加
            }
            catch { /* 预热失败不影响运行 */ }
        });
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        bool isAgreed = Preferences.Get("UserAgreePrivacy", false);

        if (isAgreed)
        {
            // 引导没走完 → 先去快速开始（两步：接模型 → 选人设）
            bool quickStartDone = Preferences.Get("QuickStartDone", false);
            if (!quickStartDone)
                return new Window(new NavigationPage(new QuickStartPage()));

            return new Window(new NavigationPage(new ChatPage()));
        }
        else
        {
            // 未同意协议：先进启动动画页，动画结束后由 IntroPage 跳转到协议页
            return new Window(new NavigationPage(new IntroPage()));
        }
    }
}