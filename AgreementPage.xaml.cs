namespace 青阳AI;

public partial class AgreementPage : ContentPage
{
    public AgreementPage()
    {
        InitializeComponent();
    }

    private async void OnAgreeClicked(object sender, EventArgs e)
    {
        if (chkAgree.IsChecked == true)
        {
            Preferences.Set("UserAgreePrivacy", true);
            // 必须包 NavigationPage：裸页面会让 PushAsync 全部失效（设置/日记打不开）
            // 同意后先走「快速开始」（两步引导），引导内结束再进聊天页
            Application.Current!.Windows[0].Page = new NavigationPage(new QuickStartPage());
        }
        else
        {
            await DisplayAlert("提示", "请勾选同意协议", "确定");
        }
    }
}