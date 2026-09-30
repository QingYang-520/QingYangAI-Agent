namespace 青阳AI;

/// <summary>
/// 「关于应用」页：应用版本、开发人、版权说明。
/// 版本取自 AppInfo、年份取运行时当前年份 —— 升版本 / 跨年都自动跟上，无需手改代码。
/// </summary>
public partial class AboutPage : ContentPage
{
    public AboutPage()
    {
        InitializeComponent();
        RefreshInfo();
    }

    /// <summary>刷新版本号与版权年份。</summary>
    private void RefreshInfo()
    {
        try
        {
            var ver = AppInfo.Current.VersionString;
            var build = AppInfo.Current.BuildString;
            lblVersion.Text = $"版本 v{ver}（构建号 {build}） · {PlatformName}";
        }
        catch
        {
            lblVersion.Text = "版本信息不可用";
        }

        // 年份用运行时当前年份：跨年后无需改代码
        lblCopyright.Text = $"Copyright © {DateTime.Now.Year} 青阳AI All Rights Reserved";
    }

    /// <summary>当前平台名。用条件编译，零额外依赖。</summary>
    private static string PlatformName
    {
        get
        {
#if ANDROID
            return "Android";
#else
            return "Windows";
#endif
        }
    }
}
