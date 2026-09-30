using Microsoft.Maui.Controls;
using System.Text.Json;

namespace 青阳AI;

/// <summary>
/// 新用户快速开始：两步走 —— ① 接上模型 ② 挑人设。
/// 左下角「跳过」在整个流程里常驻；气泡展开/收起统一走二次方 ease-out（先快后慢）。
/// </summary>
public partial class QuickStartPage : ContentPage
{
    private static readonly HttpClient _http = new();

    /// <summary>二次方 ease-out 曲线：先快后慢。</summary>
    private static readonly Easing QuadraticEaseOut = new(t => 1 - (1 - t) * (1 - t));

    private const string DoneKey = "QuickStartDone";

    private int _step = 1;
    private bool _switching;
    private bool _usePreset = true;

    // 文本对话模型：显示文本 → 原始 id；id → 最大 token
    private readonly Dictionary<string, string> _modelOptions = new();
    private readonly Dictionary<string, int> _idToMax = new(StringComparer.OrdinalIgnoreCase);
    // 多模态 Picker 挂着的模型表（保存时反解显示文本用）
    private readonly Dictionary<AppSettings.ModelCat, List<AppSettings.CachedModel>> _modalCache = new();

    public QuickStartPage()
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false); // 引导流程不显示导航栏
        LoadFromSettings();
        UpdateStepUi();
        ShowStep(1, animate: false);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            // 主按钮跟随主题色（默认紫），与设置页风格一致
            btnNext.BackgroundColor = AppSettings.ThemeColor;
        }
        catch { }
    }

    /// <summary>Android 返回键：第二步退回第一步。</summary>
    protected override bool OnBackButtonPressed()
    {
        if (_step == 2)
        {
            _ = GoStepAsync(1);
            return true;
        }
        return base.OnBackButtonPressed();
    }

    // ─────────── 步骤切换 ───────────

    private void UpdateStepUi()
    {
        bool first = _step == 1;
        lblStep.Text = first ? "第 1 / 2 步 · 接上一个模型就能聊" : "第 2 / 2 步 · 挑一个你喜欢的人设";
        barStep1.BackgroundColor = first ? Color.FromArgb("#FBB5B2") : Color.FromArgb("#3A3333");
        barStep2.BackgroundColor = first ? Color.FromArgb("#3A3333") : Color.FromArgb("#FBB5B2");
        btnPrev.IsVisible = !first;
        btnNext.Text = first ? "下一步" : "开始聊天";
    }

    /// <summary>切换步骤：旧页淡出并横移，新页从反方向淡入归位，全程二次方 ease-out。</summary>
    private void ShowStep(int step, bool animate)
    {
        var outgoing = step == 2 ? stepModel : stepPersona;
        var incoming = step == 2 ? stepPersona : stepModel;
        double dir = step == 2 ? -1 : 1; // 前进时旧页左移，后退时旧页右移

        if (!animate)
        {
            outgoing.IsVisible = false;
            outgoing.Opacity = 0;
            outgoing.TranslationX = 0;
            incoming.IsVisible = true;
            incoming.Opacity = 1;
            incoming.TranslationX = 0;
            return;
        }

        _ = RunStepAnimationAsync(outgoing, incoming, dir);
    }

    private async Task RunStepAnimationAsync(VisualElement outgoing, VisualElement incoming, double dir)
    {
        if (_switching) return;
        _switching = true;
        try
        {
            await Task.WhenAll(
                outgoing.FadeTo(0, 180, QuadraticEaseOut),
                outgoing.TranslateTo(24 * dir, 0, 180, QuadraticEaseOut));
            outgoing.IsVisible = false;
            outgoing.TranslationX = 0;

            incoming.IsVisible = true;
            incoming.Opacity = 0;
            incoming.TranslationX = -24 * dir;
            await Task.WhenAll(
                incoming.FadeTo(1, 260, QuadraticEaseOut),
                incoming.TranslateTo(0, 0, 260, QuadraticEaseOut));
        }
        finally
        {
            _switching = false;
        }
    }

    private async Task GoStepAsync(int step)
    {
        if (_step == step) return;
        _step = step;
        UpdateStepUi();
        ShowStep(step, animate: true);
        await Task.CompletedTask;
    }

    // ─────────── 底部按钮 ───────────

    private async void OnNextClicked(object? sender, EventArgs e)
    {
        if (_switching) return;

        if (_step == 1)
        {
            SaveModelConfigFromUi();
            if (string.IsNullOrWhiteSpace(AppSettings.ApiKey))
            {
                bool goOn = await DisplayAlert("还没填 API Key",
                    "没有 Key 就没法聊天哦。也可以先跳过，之后在设置里补上。", "仍然继续", "返回填写");
                if (!goOn) return;
            }
            await GoStepAsync(2);
        }
        else
        {
            SavePersonaFromUi();
            Finish();
        }
    }

    private async void OnPrevClicked(object? sender, EventArgs e)
    {
        if (_switching) return;
        SavePersonaFromUi();
        await GoStepAsync(1);
    }

    /// <summary>跳过：保存已填内容后直接进入聊天页（不强制任何一项）。</summary>
    private void OnSkipTapped(object? sender, EventArgs e)
    {
        try
        {
            SaveModelConfigFromUi();
            if (_step == 2) SavePersonaFromUi();
        }
        catch { }
        Finish();
    }

    /// <summary>结束引导，进入聊天页（必须包 NavigationPage，否则 PushAsync 全失效）。</summary>
    private void Finish()
    {
        Preferences.Set(DoneKey, true);
        Application.Current!.Windows[0].Page = new NavigationPage(new ChatPage());
    }

    // ─────────── 初始值回显 ───────────

    private void LoadFromSettings()
    {
        qsEntApiKey.Text = AppSettings.ApiKey;
        qsEntApiUrl.Text = AppSettings.ApiUrl;
        qsEntThinkingModel.Text = AppSettings.ThinkingModel;

        // 先把上次「获取模型列表」的缓存还原到内存（下次不用再点获取）
        RestoreModelCache();

        FillModelPicker(qsPicModel, AppSettings.Model, AppSettings.ModelCat.Chat);
        FillModelPicker(qsPicImgModel, AppSettings.ImgModel, AppSettings.ModelCat.Img);
        FillModelPicker(qsPicAudioModel, AppSettings.AudioModel, AppSettings.ModelCat.Audio);
        FillModelPicker(qsPicVisionModel, AppSettings.VisionModel, AppSettings.ModelCat.Vision);
        FillModelPicker(qsPicTtsModel, AppSettings.TtsModel, AppSettings.ModelCat.Tts);

        qsEntImgApiUrl.Text = AppSettings.ImgApiUrl;
        qsEntImgApiKey.Text = AppSettings.ImgApiKey;
        qsEntAudioApiUrl.Text = AppSettings.AudioApiUrl;
        qsEntAudioApiKey.Text = AppSettings.AudioApiKey;
        qsEntVisionApiUrl.Text = AppSettings.VisionApiUrl;
        qsEntVisionApiKey.Text = AppSettings.VisionApiKey;
        qsEntTtsApiUrl.Text = AppSettings.TtsApiUrl;
        qsEntTtsApiKey.Text = AppSettings.TtsApiKey;
        qsEntTtsVoice.Text = AppSettings.TtsVoice;

        // 人设：已有自定义人设 → 自定义；否则官方预设
        qsEdtPersona.Text = AppSettings.Persona;
        qsPicPersonality.ItemsSource = AppSettings.Personalities;
        int idx = Array.IndexOf(AppSettings.Personalities, AppSettings.Personality);
        qsPicPersonality.SelectedIndex = idx >= 0 ? idx : 0;

        _usePreset = string.IsNullOrWhiteSpace(AppSettings.Persona);
        ApplyPersonaMode(_usePreset, animate: false);
    }

    /// <summary>某个多模态类别当前保存的模型 id（拉取后自动选中）。</summary>
    private static string CurrentModalModel(AppSettings.ModelCat cat) => cat switch
    {
        AppSettings.ModelCat.Img => AppSettings.ImgModel,
        AppSettings.ModelCat.Audio => AppSettings.AudioModel,
        AppSettings.ModelCat.Vision => AppSettings.VisionModel,
        AppSettings.ModelCat.Tts => AppSettings.TtsModel,
        _ => "",
    };

    /// <summary>从多模态 Picker 选中项反解模型 id。</summary>
    private string ResolveModalModel(AppSettings.ModelCat cat, Picker picker)
    {
        if (picker.SelectedItem is not string sel || string.IsNullOrWhiteSpace(sel)) return "";
        if (_modalCache.TryGetValue(cat, out var list))
        {
            var hit = list.FirstOrDefault(m => AppSettings.ModelLabel(m.Id, m.Max) == sel);
            if (hit != null) return hit.Id;
        }
        return AppSettings.ModelIdFromLabel(sel);
    }

    /// <summary>还原「获取模型列表」的持久化缓存：填充内存字典 + 多模态候选表。</summary>
    private void RestoreModelCache()
    {
        _modelOptions.Clear();
        _idToMax.Clear();
        foreach (var m in AppSettings.GetCachedModelList(AppSettings.ModelCat.Chat))
        {
            _idToMax[m.Id] = m.Max;
            _modelOptions[AppSettings.ModelLabel(m.Id, m.Max)] = m.Id;
        }
        foreach (var c in new[] { AppSettings.ModelCat.Img, AppSettings.ModelCat.Audio,
                                  AppSettings.ModelCat.Vision, AppSettings.ModelCat.Tts })
            _modalCache[c] = AppSettings.GetCachedModelList(c);
    }

    /// <summary>
    /// 填充模型 Picker：优先用上次获取到的完整列表缓存（不用再点「获取模型」）；
    /// 没有缓存时退化成只显示当前值。
    /// </summary>
    private void FillModelPicker(Picker picker, string model, AppSettings.ModelCat cat)
    {
        var cached = AppSettings.GetCachedModelList(cat);
        if (cached.Count > 0)
        {
            var labels = cached.Select(m => AppSettings.ModelLabel(m.Id, m.Max)).ToList();
            int cur = cached.FindIndex(m => m.Id.Equals(model, StringComparison.OrdinalIgnoreCase));
            if (cur < 0 && !string.IsNullOrWhiteSpace(model))
            {
                labels.Insert(0, model);
                cur = 0;
            }
            picker.ItemsSource = labels;
            picker.SelectedIndex = cur >= 0 ? cur : (labels.Count > 0 ? 0 : -1);
        }
        else if (string.IsNullOrWhiteSpace(model))
        {
            picker.ItemsSource = Array.Empty<string>();
            picker.SelectedIndex = -1;
        }
        else
        {
            picker.ItemsSource = new[] { model };
            picker.SelectedIndex = 0;
        }
    }

    // ─────────── 气泡展开 / 收起（二次方 ease-out）───────────

    private void OnToggleQsChatBubble(object? sender, EventArgs e)
        => ToggleBubble(qsChatBubbleContent, qsChatArrow);
    private void OnToggleQsMoreBubble(object? sender, EventArgs e)
        => ToggleBubble(qsMoreBubbleContent, qsMoreArrow);
    private void OnToggleQsImgBubble(object? sender, EventArgs e)
        => ToggleBubble(qsImgBubbleContent, qsImgArrow);
    private void OnToggleQsAudioBubble(object? sender, EventArgs e)
        => ToggleBubble(qsAudioBubbleContent, qsAudioArrow);
    private void OnToggleQsVisionBubble(object? sender, EventArgs e)
        => ToggleBubble(qsVisionBubbleContent, qsVisionArrow);
    private void OnToggleQsTtsBubble(object? sender, EventArgs e)
        => ToggleBubble(qsTtsBubbleContent, qsTtsArrow);

    /// <summary>
    /// 气泡展开/收起：高度展开/收拢 + 淡入淡出，全程二次方 ease-out（先快后慢）。
    /// 展开时先量目标高度，从 0 长到目标后恢复自动高度；收起时从当前高度缩到 0 再隐藏。
    /// </summary>
    private async void ToggleBubble(VerticalStackLayout content, MorphIcon arrow)
    {
        bool expand = !content.IsVisible;
        arrow.Icon = expand ? "ChevronDown" : "ChevronRight";

        if (expand)
        {
            content.Opacity = 0;
            content.HeightRequest = 0;
            content.IsVisible = true;

            double targetH = MeasureBubbleHeight(content);

            content.Animate("qsBubble" + content.GetHashCode(), v => content.HeightRequest = v,
                0d, targetH, 16, 260, QuadraticEaseOut);
            await content.FadeTo(1, 220, QuadraticEaseOut);
            await Task.Delay(60);       // 等高度动画收尾
            content.HeightRequest = -1; // 恢复自动高度
        }
        else
        {
            double fromH = content.Height > 0 ? content.Height : content.HeightRequest;
            if (fromH <= 0) fromH = 200;

            content.Animate("qsBubble" + content.GetHashCode(), v => content.HeightRequest = Math.Max(0, v),
                fromH, 0d, 16, 200, QuadraticEaseOut);
            await content.FadeTo(0, 180, QuadraticEaseOut);
            await Task.Delay(40);
            content.IsVisible = false;
            content.Opacity = 1;
            content.HeightRequest = -1;
        }
    }

    /// <summary>测量气泡内容的目标高度（以父容器宽度为约束，加余量防裁切）。</summary>
    private double MeasureBubbleHeight(VisualElement content)
    {
        double width = content.Width;
        if (width <= 0) width = (content.Parent as VisualElement)?.Width ?? -1;
        if (width <= 0) width = this.Width - 60;
        if (width <= 0) width = 320;

        try
        {
            var request = content.Measure(width, double.PositiveInfinity, MeasureFlags.IncludeMargins);
            return Math.Max(60, request.Request.Height + 2);
        }
        catch
        {
            return 220; // 测量失败给个保守值，宁可多留空白
        }
    }

    // ─────────── 人设模式（官方预设 / 自定义），与设置页同款 ───────────

    private void OnQsPresetTapped(object? sender, EventArgs e)
    {
        if (_usePreset) return;
        ApplyPersonaMode(true, animate: true);
    }

    private void OnQsCustomTapped(object? sender, EventArgs e)
    {
        if (!_usePreset) return;
        ApplyPersonaMode(false, animate: true);
    }

    private void ApplyPersonaMode(bool usePreset, bool animate)
    {
        _usePreset = usePreset;

        qsLblPersonaSub.Text = usePreset ? " > 青阳官方预设" : " > 自定义";
        qsLblPersonaSub.IsVisible = true;

        qsBubblePreset.Stroke = usePreset ? Color.FromArgb("#7B68EE") : Color.FromArgb("#555555");
        qsBubbleCustom.Stroke = usePreset ? Color.FromArgb("#555555") : Color.FromArgb("#7B68EE");
        qsLblPreset.TextColor = usePreset ? Colors.White : Color.FromArgb("#AAAAAA");
        qsLblCustom.TextColor = usePreset ? Color.FromArgb("#AAAAAA") : Colors.White;

        if (animate)
        {
            AnimateLabelColor(qsLblPersonaTitle, Colors.White, Color.FromArgb("#9E9E9E"));
            AnimateContentHeight(usePreset);
        }
        else
        {
            qsLblPersonaTitle.TextColor = Color.FromArgb("#9E9E9E");
        }

        qsPanelPreset.IsVisible = usePreset;
        qsPanelCustom.IsVisible = !usePreset;
    }

    private void AnimateLabelColor(Label label, Color from, Color to, uint durationMs = 300)
    {
        void Step(double t)
        {
            label.TextColor = Color.FromRgba(
                from.Red + (to.Red - from.Red) * t,
                from.Green + (to.Green - from.Green) * t,
                from.Blue + (to.Blue - from.Blue) * t,
                1);
        }
        label.Animate("qsLabelColor" + label.GetHashCode(), Step, 0, 1, 16, durationMs, QuadraticEaseOut);
    }

    private void AnimateContentHeight(bool usePreset)
    {
        double targetH = usePreset ? 72 : 176;
        var box = qsPersonaContentBox;
        double fromH = box.Height > 0 ? box.Height : targetH;

        box.Animate("qsContentHeight", v => box.HeightRequest = v, fromH, targetH, 16, 320, QuadraticEaseOut,
            finished: (v, cancelled) =>
            {
                if (cancelled) return;
                box.HeightRequest = -1; // 恢复自动高度
            });
    }

    // ─────────── 获取模型列表 ───────────

    private async void OnFetchChatModelsClicked(object? sender, EventArgs e)
    {
        var key = qsEntApiKey.Text?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            await DisplayAlert("提示", "请先填写 API Key", "确定");
            return;
        }

        qsBtnFetchModels.IsEnabled = false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, AppSettings.GetModelsEndpoint());
            req.Headers.Add("Authorization", $"Bearer {key}");
            var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var ids = new List<string>();
            var maxTokenMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = m.GetProperty("id").GetString();
                if (string.IsNullOrEmpty(id)) continue;
                ids.Add(id);

                int n = 0;
                if (m.TryGetProperty("max_tokens", out var t1) && t1.TryGetInt32(out var v1)) n = Math.Max(n, v1);
                if (m.TryGetProperty("max_output_tokens", out var t2) && t2.TryGetInt32(out var v2)) n = Math.Max(n, v2);
                if (m.TryGetProperty("context_length", out var t3) && t3.TryGetInt32(out var v3)) n = Math.Max(n, v3);
                if (n > 0) maxTokenMap[id] = n;
            }

            if (ids.Count == 0)
            {
                await DisplayAlert("提示", "接口没有返回任何模型", "确定");
                return;
            }

            _modelOptions.Clear();
            _idToMax.Clear();
            var labels = new List<string>();
            var cached = new List<AppSettings.CachedModel>();
            foreach (var id in ids)
            {
                _idToMax[id] = maxTokenMap.TryGetValue(id, out var mt) ? mt : 0;
                string label = AppSettings.ModelLabel(id, _idToMax[id]);
                labels.Add(label);
                _modelOptions[label] = id;
                cached.Add(new AppSettings.CachedModel { Id = id, Max = _idToMax[id] });
            }

            qsPicModel.ItemsSource = labels;
            int cur = -1;
            for (int i = 0; i < labels.Count; i++)
            {
                if (_modelOptions[labels[i]].Equals(AppSettings.Model, StringComparison.OrdinalIgnoreCase))
                { cur = i; break; }
            }
            qsPicModel.SelectedIndex = cur >= 0 ? cur : 0;

            // ★ 持久化整张表，下次不用再点获取
            AppSettings.SetCachedModelList(AppSettings.ModelCat.Chat, cached);

            int shown = _idToMax.Values.Count(v => v > 0);
            await DisplayAlert("成功", $"获取到 {ids.Count} 个模型，其中 {shown} 个返回了上下文上限。\n已保存，下次可直接选。", "确定");
        }
        catch (Exception ex)
        {
            await DisplayAlert("获取失败", ex.Message, "确定");
        }
        finally
        {
            qsBtnFetchModels.IsEnabled = true;
        }
    }

    private async void OnFetchImgModelsClicked(object? sender, EventArgs e)
        => await FetchModalModelsAsync(qsEntImgApiUrl, qsEntImgApiKey, qsPicImgModel, qsBtnFetchImgModels, "文生图", AppSettings.ModelCat.Img);
    private async void OnFetchAudioModelsClicked(object? sender, EventArgs e)
        => await FetchModalModelsAsync(qsEntAudioApiUrl, qsEntAudioApiKey, qsPicAudioModel, qsBtnFetchAudioModels, "听觉", AppSettings.ModelCat.Audio);
    private async void OnFetchVisionModelsClicked(object? sender, EventArgs e)
        => await FetchModalModelsAsync(qsEntVisionApiUrl, qsEntVisionApiKey, qsPicVisionModel, qsBtnFetchVisionModels, "视觉", AppSettings.ModelCat.Vision);
    private async void OnFetchTtsModelsClicked(object? sender, EventArgs e)
        => await FetchModalModelsAsync(qsEntTtsApiUrl, qsEntTtsApiKey, qsPicTtsModel, qsBtnFetchTtsModels, "文字转语音", AppSettings.ModelCat.Tts);

    private async Task FetchModalModelsAsync(Entry urlEntry, Entry keyEntry, Picker picker, Button btn, string label, AppSettings.ModelCat cat)
    {
        var url = urlEntry.Text?.Trim();
        var key = keyEntry.Text?.Trim();
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
        {
            await DisplayAlert("提示", $"请先填写{label}模型的 API URL 与 API Key", "确定");
            return;
        }

        btn.IsEnabled = false;
        try
        {
            var modelsUrl = url;
            foreach (var suffix in new[] { "/chat/completions", "/images/generations", "/audio/transcriptions", "/audio/speech" })
            {
                if (modelsUrl.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    modelsUrl = modelsUrl[..^suffix.Length] + "/models";
                    break;
                }
            }
            if (!modelsUrl.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                modelsUrl = modelsUrl.TrimEnd('/') + "/models";

            using var req = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
            req.Headers.Add("Authorization", $"Bearer {key}");
            var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var ids = new List<string>();
            foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = m.GetProperty("id").GetString();
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }

            if (ids.Count == 0)
            {
                await DisplayAlert("提示", "接口没有返回任何模型", "确定");
                return;
            }

            var cached = ids.Select(id => new AppSettings.CachedModel { Id = id }).ToList();
            _modalCache[cat] = cached;
            var labels = cached.Select(m => AppSettings.ModelLabel(m.Id, m.Max)).ToList();
            picker.ItemsSource = labels;
            int cur = cached.FindIndex(m => m.Id.Equals(CurrentModalModel(cat), StringComparison.OrdinalIgnoreCase));
            picker.SelectedIndex = cur >= 0 ? cur : 0;

            // ★ 持久化：下次进设置页不用再点「获取」
            AppSettings.SetCachedModelList(cat, cached);

            await DisplayAlert("成功", $"获取到 {ids.Count} 个{label}模型。\n已保存，下次可直接选。", "确定");
        }
        catch (Exception ex)
        {
            await DisplayAlert("获取失败", ex.Message, "确定");
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    // ─────────── 粘贴 / 清空 ───────────

    private async Task PasteToAsync(Entry entry)
    {
        var text = await Clipboard.Default.GetTextAsync();
        if (!string.IsNullOrEmpty(text)) entry.Text = text.Trim();
    }

    private async void OnPasteApiKeyClicked(object? sender, EventArgs e) => await PasteToAsync(qsEntApiKey);
    private void OnClearApiKeyClicked(object? sender, EventArgs e) => qsEntApiKey.Text = "";
    private async void OnPasteApiUrlClicked(object? sender, EventArgs e) => await PasteToAsync(qsEntApiUrl);
    private void OnClearApiUrlClicked(object? sender, EventArgs e) => qsEntApiUrl.Text = "";

    private async void OnPasteImgUrlClicked(object? sender, EventArgs e) => await PasteToAsync(qsEntImgApiUrl);
    private void OnClearImgUrlClicked(object? sender, EventArgs e) => qsEntImgApiUrl.Text = "";
    private async void OnPasteImgKeyClicked(object? sender, EventArgs e) => await PasteToAsync(qsEntImgApiKey);
    private void OnClearImgKeyClicked(object? sender, EventArgs e) => qsEntImgApiKey.Text = "";

    private async void OnPasteAudioUrlClicked(object? sender, EventArgs e) => await PasteToAsync(qsEntAudioApiUrl);
    private void OnClearAudioUrlClicked(object? sender, EventArgs e) => qsEntAudioApiUrl.Text = "";
    private async void OnPasteAudioKeyClicked(object? sender, EventArgs e) => await PasteToAsync(qsEntAudioApiKey);
    private void OnClearAudioKeyClicked(object? sender, EventArgs e) => qsEntAudioApiKey.Text = "";

    private async void OnPasteVisionUrlClicked(object? sender, EventArgs e) => await PasteToAsync(qsEntVisionApiUrl);
    private void OnClearVisionUrlClicked(object? sender, EventArgs e) => qsEntVisionApiUrl.Text = "";
    private async void OnPasteVisionKeyClicked(object? sender, EventArgs e) => await PasteToAsync(qsEntVisionApiKey);
    private void OnClearVisionKeyClicked(object? sender, EventArgs e) => qsEntVisionApiKey.Text = "";

    private async void OnPasteTtsUrlClicked(object? sender, EventArgs e) => await PasteToAsync(qsEntTtsApiUrl);
    private void OnClearTtsUrlClicked(object? sender, EventArgs e) => qsEntTtsApiUrl.Text = "";
    private async void OnPasteTtsKeyClicked(object? sender, EventArgs e) => await PasteToAsync(qsEntTtsApiKey);
    private void OnClearTtsKeyClicked(object? sender, EventArgs e) => qsEntTtsApiKey.Text = "";

    // ─────────── 保存到 AppSettings ───────────

    /// <summary>把第 1 步的 5 组模型配置写入当前激活的配置套。</summary>
    private void SaveModelConfigFromUi()
    {
        AppSettings.ApiKey = qsEntApiKey.Text?.Trim() ?? "";
        AppSettings.ApiUrl = qsEntApiUrl.Text ?? "";
        AppSettings.ThinkingModel = qsEntThinkingModel.Text ?? "";

        if (qsPicModel.SelectedIndex >= 0 && qsPicModel.SelectedItem is string sel)
        {
            string id = _modelOptions.TryGetValue(sel, out var rid) ? rid : sel;
            AppSettings.Model = id;
            if (_idToMax.TryGetValue(id, out var maxTok) && maxTok > 0)
                AppSettings.ModelMaxTokens = maxTok;
        }

        AppSettings.ImgApiUrl = qsEntImgApiUrl.Text ?? "";
        AppSettings.ImgApiKey = qsEntImgApiKey.Text?.Trim() ?? "";
        AppSettings.ImgModel = ResolveModalModel(AppSettings.ModelCat.Img, qsPicImgModel);
        AppSettings.AudioApiUrl = qsEntAudioApiUrl.Text ?? "";
        AppSettings.AudioApiKey = qsEntAudioApiKey.Text?.Trim() ?? "";
        AppSettings.AudioModel = ResolveModalModel(AppSettings.ModelCat.Audio, qsPicAudioModel);
        AppSettings.VisionApiUrl = qsEntVisionApiUrl.Text ?? "";
        AppSettings.VisionApiKey = qsEntVisionApiKey.Text?.Trim() ?? "";
        AppSettings.VisionModel = ResolveModalModel(AppSettings.ModelCat.Vision, qsPicVisionModel);
        AppSettings.TtsApiUrl = qsEntTtsApiUrl.Text ?? "";
        AppSettings.TtsApiKey = qsEntTtsApiKey.Text?.Trim() ?? "";
        AppSettings.TtsModel = ResolveModalModel(AppSettings.ModelCat.Tts, qsPicTtsModel);
        AppSettings.TtsVoice = qsEntTtsVoice.Text ?? "";
    }

    /// <summary>把第 2 步的人设写入设置（官方预设 = 人设留空）。</summary>
    private void SavePersonaFromUi()
    {
        AppSettings.Persona = _usePreset ? "" : (qsEdtPersona.Text ?? "");
        if (qsPicPersonality.SelectedIndex >= 0 && qsPicPersonality.SelectedItem is string sel)
            AppSettings.Personality = sel;
    }
}
