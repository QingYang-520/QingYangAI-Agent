using Android.App;
using Android.Runtime;

namespace 青阳AI
{
    [Application]
    public class MainApplication : MauiApplication
    {
        public MainApplication(IntPtr handle, JniHandleOwnership ownership)
            : base(handle, ownership)
        {
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

        public override void OnCreate()
        {
            base.OnCreate();
            // 启动时确保主动关心定时任务已排期（总开关开启时才真正排期）
            try { CareReceiver.EnsureScheduled(this); } catch { }
            // 刷新桌面小组件（最新消息/心情/相伴天数）
            try { CareWidget.RefreshAll(this); } catch { }
        }
    }
}
