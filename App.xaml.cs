namespace 青阳AI;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        AppSettings.MigrateLegacyModelConfig();  // 旧版配置迁移到「配置1」
        ShizukuLifecycle.Init();   // Android 下注册 Shizuku 授权监听
        PrewarmUi();               // 启动时后台预加载设置页用到的重资源，避免打开设置卡顿
        HookCrashLogging();        // 记录未处理异常，下次打开时弹窗展示（排查闪退用）
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
            return new Window(new NavigationPage(new ChatPage()));
        }
        else
        {
            // 未同意协议：先进启动动画页，动画结束后由 IntroPage 跳转到协议页
            return new Window(new NavigationPage(new IntroPage()));
        }
    }
}