using Android.App;
using Android.Content.PM;
using Android.OS;

namespace 青阳AI
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            // 边到边：内容延伸到状态栏/导航栏底下，系统条透明（玻璃垫层负责这两个区域的观感）
            try
            {
                Window?.SetStatusBarColor(Android.Graphics.Color.Transparent);
                Window?.SetNavigationBarColor(Android.Graphics.Color.Transparent);
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                    Window?.SetDecorFitsSystemWindows(false);
                else
                    Window?.AddFlags(Android.Views.WindowManagerFlags.LayoutNoLimits);
            }
            catch { }
        }
    }
}
