namespace 青阳AI;

/// <summary>协议页的两种用途。</summary>
public enum AgreementMode
{
    /// <summary>首次启动 / 协议更新后：必须勾选同意才能继续。</summary>
    Consent,

    /// <summary>从设置页点进来查看：只读，底部按钮是「返回」。</summary>
    ReadOnly
}

/// <summary>
/// 用户协议与隐私政策页。
/// 正文来自 <see cref="AgreementContent.Markdown"/>（唯一来源），
/// 排版由 <see cref="MarkdownRenderer"/> 在代码里渲染。
/// </summary>
public partial class AgreementPage : ContentPage
{
    private readonly AgreementMode _mode;

    /// <summary>默认「同意」模式（保留无参构造，兼容老调用点）。</summary>
    public AgreementPage() : this(AgreementMode.Consent) { }

    public AgreementPage(AgreementMode mode)
    {
        _mode = mode;
        InitializeComponent();
        ApplyMode();
    }

    /// <summary>按模式摆好界面：渲染全文、决定底部栏显示什么。</summary>
    private void ApplyMode()
    {
        // 正文两种模式一样：全文渲染
        agreementBody.Add(MarkdownRenderer.Render(AgreementContent.Markdown));
        lblVersion.Text = $"版本 v{AgreementContent.Version} · 生效日期 {AgreementContent.EffectiveDate}";

        if (_mode == AgreementMode.ReadOnly)
        {
            // 设置页进来查看：没有勾选框，按钮是返回
            lblHeader.Text = "用户协议与隐私政策";
            consentBar.IsVisible = false;
        }
        else
        {
            lblHeader.Text = "欢迎使用青阳AI";
            btnConfirm.Text = "同意并继续";
        }

        try { btnConfirm.BackgroundColor = AppSettings.ThemeColor; } catch { }
    }

    private async void OnAgreeClicked(object sender, EventArgs e)
    {
        // ── 只读模式：直接退回 ──
        if (_mode == AgreementMode.ReadOnly)
        {
            if (Navigation.NavigationStack.Count > 1)
                await Navigation.PopAsync();
            else
                Application.Current!.Windows[0].Page = new NavigationPage(new ChatPage());
            return;
        }

        // ── 同意模式 ──
        if (chkAgree.IsChecked != true)
        {
            await DisplayAlert("提示", "请先勾选「我已经阅读并同意以上全部条款」", "确定");
            return;
        }

        // 记下「已同意」+ 当前协议版本。
        // 以后只有 AgreementContent.Version 变了，老用户才会被重新弹一次。
        Preferences.Set("UserAgreePrivacy", true);
        AppSettings.AgreedAgreementVersion = AgreementContent.Version;

        // 必须包 NavigationPage：裸页面会让 PushAsync 全部失效（设置/日记打不开）
        // 没走过「快速开始」的先去引导，走过了直接进聊天页
        bool quickStartDone = Preferences.Get("QuickStartDone", false);
        Application.Current!.Windows[0].Page = quickStartDone
            ? new NavigationPage(new ChatPage())
            : new NavigationPage(new QuickStartPage());
    }
}
