using Microsoft.Maui.Controls;
using System.Text.Json;

namespace 青阳AI;

public partial class SettingsPage : ContentPage
{
    private static readonly HttpClient _http = new();

    // 本次「获取模型」拉到的 模型→最大上下文 映射（max_tokens / max_output_tokens 取较大者）
    private Dictionary<string, int>? _fetchedMaxTokens;
    // 模型选项显示文本 → 原始模型 id（因为 Picker 显示字符串，需要反解）
    private readonly Dictionary<string, string> _modelOptions = new();
    // 模型 id → 最大 token（供列表显示、保存用）
    private readonly Dictionary<string, int> _idToMax = new(StringComparer.OrdinalIgnoreCase);
    // 模型 id → 推理档位能力（是否支持 + 档位列表），来自 /models 返回
    private readonly Dictionary<string, (bool Supported, List<string> Levels)> _reasoningByModel = new(StringComparer.OrdinalIgnoreCase);
    // 人设模式：true=官方预设，false=自定义
    private bool _usePreset = true;

    /// <summary>二次方 ease-out 曲线：先快后慢。</summary>
    private static readonly Easing QuadraticEaseOut = new(t => 1 - (1 - t) * (1 - t));

    public SettingsPage()
    {
        InitializeComponent();
        ParseUi();
        UpdateShizukuStatus();
        UpdateTabHighlight(AppSettings.ActiveModelConfig); // 高亮当前配置 Tab
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            UpdateShizukuStatus();
            UpdateSensingStatus();
            UpdateSuperAdminState();
            UpdateWorkspaceHint();   // 从系统设置回来后刷新工作区可见性/权限状态
            UpdateStreamDiagLabel(); // 聊天页刚发过消息，顺手把流式诊断刷新一下
            UpdateAgreementVersionLabel();
            _ = UpdateMemoryCountAsync();
            _ = UpdateCompanionLabelAsync();
            // 每次打开设置页都检测连通状态；延迟到导航动画结束后异步执行，避免卡顿
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(600), ScheduleFullRecheck);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings OnAppearing] {ex.Message}");
        }
    }

    /// <summary>刷新相伴天数显示。</summary>
    private async Task UpdateCompanionLabelAsync()
    {
        try
        {
            // 确保首次相遇时间已初始化（ChatStore 首次加载时写入）
            await ChatStore.Instance.CountMemoriesAsync();
            var since = AppSettings.CompanionSinceText;
            var sinceDate = DateTime.TryParse(since, out var d) ? d.ToString("yyyy年M月d日") : DateTime.Now.ToString("yyyy年M月d日");
            lblCompanionDays.Text = $"💗 已陪伴 {AppSettings.CompanionDays} 天（自 {sinceDate} 起）";
        }
        catch { }
    }

    /// <summary>导出全部数据为备份文件，并通过分享面板保存。</summary>
    private async void OnExportDataClicked(object? sender, EventArgs e)
    {
        btnExportData.IsEnabled = false;
        try
        {
            var path = await BackupService.ExportAsync();
            await Microsoft.Maui.ApplicationModel.DataTransfer.Share.RequestAsync(
                new Microsoft.Maui.ApplicationModel.DataTransfer.ShareFileRequest
                {
                    Title = "青阳AI 数据备份",
                    File = new Microsoft.Maui.ApplicationModel.DataTransfer.ShareFile(path)
                });
        }
        catch (Exception ex)
        {
            await DisplayAlert("导出失败", ex.Message, "知道了");
        }
        finally
        {
            btnExportData.IsEnabled = true;
        }
    }

    /// <summary>从备份文件恢复（覆盖式）。</summary>
    private async void OnImportDataClicked(object? sender, EventArgs e)
    {
        if (!await DisplayAlert("导入备份", "导入会覆盖现有的全部聊天、记忆和日记，确定继续吗？", "继续导入", "取消"))
            return;

        btnImportData.IsEnabled = false;
        try
        {
            var file = await Microsoft.Maui.Storage.FilePicker.Default.PickAsync(new Microsoft.Maui.Storage.PickOptions
            {
                PickerTitle = "选择备份 JSON 文件"
            });
            if (file == null) return;

            await BackupService.RestoreAsync(file.FullPath);
            await UpdateMemoryCountAsync();
            await UpdateCompanionLabelAsync();
            await DisplayAlert("恢复完成", "数据已恢复。聊天记录在重启应用后完整显示。", "知道了");
        }
        catch (Exception ex)
        {
            await DisplayAlert("导入失败", ex.Message, "知道了");
        }
        finally
        {
            btnImportData.IsEnabled = true;
        }
    }

    // ─── 设置搜索 ───

    /// <summary>搜索栏文本变化：按关键词显示/隐藏各设置分区。</summary>
    private void OnSettingsSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        var q = e.NewTextValue?.Trim() ?? "";
        bool showAll = q.Length == 0;
        searchClearIcon.IsVisible = !showAll;

        if (this.Content is ScrollView sv && sv.Content is VerticalStackLayout root)
        {
            foreach (var child in root.Children)
            {
                if (child is VerticalStackLayout section)
                {
                    bool match = showAll;
                    if (!match)
                    {
                        foreach (var el in section.Children)
                        {
                            if (el is Label lb && lb.Text != null &&
                                lb.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
                            { match = true; break; }
                            if (el is Border bd && bd.Content is VerticalStackLayout vs)
                            {
                                foreach (var sub in vs.Children)
                                {
                                    if (sub is Label sl && sl.Text != null &&
                                        sl.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
                                    { match = true; break; }
                                }
                                if (match) break;
                            }
                        }
                    }
                    section.IsVisible = match;
                }
            }
        }
    }

    /// <summary>清除搜索。</summary>
    private void OnSearchClearTapped(object? sender, EventArgs e)
    {
        settingsSearch.Text = "";
    }

    // 全量重检防抖：连续切配置/连续触发时只跑最后一次，避免检测与徽章动画风暴拖死 UI
    private CancellationTokenSource? _recheckCts;

    private void ScheduleFullRecheck()
    {
        _recheckCts?.Cancel();
        _recheckCts = new CancellationTokenSource();
        var token = _recheckCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(500, token); }
            catch (OperationCanceledException) { return; }
            if (token.IsCancellationRequested) return;
            await Dispatcher.DispatchAsync(() => _ = CheckAllModelsStatusAsync());
        });
    }

    /// <summary>刷新三项生活感知授权状态显示（从系统授权页返回后也会重进 OnAppearing）。</summary>
    private void UpdateSensingStatus()
    {
#if ANDROID
        try
        {
            bool usage = UsageStatsHelper.IsGranted();
            lblGrantUsage.Text = usage
                ? "使用情况访问权（前台应用/使用时长）：已授权"
                : "使用情况访问权（前台应用/使用时长）：未授权";
            lblGrantUsage.TextColor = usage ? Colors.LimeGreen : Color.FromArgb("#AAAAAA");
            btnGrantUsage.Text = usage ? "已授权" : "去授权";
            btnGrantUsage.IsEnabled = !usage;

            bool notif = CareNotificationListener.IsEnabled();
            var svcAt = Preferences.Default.Get("NotifSvcConnected", "");
            lblGrantNotif.Text = notif
                ? (string.IsNullOrEmpty(svcAt)
                    ? "通知使用权：已开启（服务尚未连接，试重启App）"
                    : $"通知使用权：已开启（服务已连接 {svcAt}）")
                : "通知使用权（新消息提醒）：未开启";
            lblGrantNotif.TextColor = notif ? Colors.LimeGreen : Color.FromArgb("#AAAAAA");
            btnGrantNotif.Text = notif ? "已开启" : "去授权";
            btnGrantNotif.IsEnabled = !notif;

            bool cal = CalendarHelper.IsGranted();
            lblGrantCalendar.Text = cal
                ? "日历（今天的日程）：已授权"
                : "日历（今天的日程）：未授权";
            lblGrantCalendar.TextColor = cal ? Colors.LimeGreen : Color.FromArgb("#AAAAAA");
            btnGrantCalendar.Text = cal ? "已授权" : "申请权限";
            btnGrantCalendar.IsEnabled = !cal;

            // 无障碍（鸿蒙读通知通道）
            bool a11y = QyAccessibilityService.IsEnabled();
            lblGrantA11y.Text = a11y
                ? "无障碍（鸿蒙读通知）：已开启"
                : "无障碍（鸿蒙读通知）：未开启";
            lblGrantA11y.TextColor = a11y ? Colors.LimeGreen : Color.FromArgb("#AAAAAA");
            btnGrantA11y.Text = a11y ? "已开启" : "去开启";
            btnGrantA11y.IsEnabled = !a11y;
        }
        catch { }
#endif
    }

    private void OnGrantUsageClicked(object? sender, EventArgs e)
    {
#if ANDROID
        UsageStatsHelper.OpenSettings();
#endif
    }

    // ─── Super Admin（高风险开关） ───

    /// <summary>刷新 Super Admin 两权限状态。</summary>
    private void UpdateSuperAdminState()
    {
#if ANDROID
        bool admin = SuperAdmin.IsAdminActive();
        bool storage = SuperAdmin.IsStorageGranted();

        lblStorageState.Text = storage ? "已授予（全盘访问）" : "未授予";
        lblStorageState.TextColor = storage ? Colors.LimeGreen : Color.FromArgb("#9E8D8C");
        btnStorage.Text = storage ? "已开启" : "去开启";
        btnStorage.IsEnabled = !storage;

        lblAdminState.Text = admin ? "已激活" : "未激活";
        lblAdminState.TextColor = admin ? Colors.LimeGreen : Color.FromArgb("#9E8D8C");
        btnAdmin.Text = admin ? "已激活" : "去激活";
        btnAdmin.IsEnabled = !admin;

        bool any = admin || storage;
        swSuperAdmin.IsToggled = any;
        lblSuperAdminState.Text = any ? "开启（任一权限已授予）" : "关闭";
#endif
    }

    private void OnSuperAdminToggled(object? sender, ToggledEventArgs e)
    {
        // 说明文字由状态按钮承担；总开关打开时若有未授权项，引导去开启
        UpdateSuperAdminState();
    }

    private async void OnStorageClicked(object? sender, EventArgs e)
    {
        await StorageAccess.RequestPermissionAsync();
        StorageAccess.InvalidateProbe();
        UpdateSuperAdminState();
    }

    private void OnAdminClicked(object? sender, EventArgs e)
    {
#if ANDROID
        SuperAdmin.RequestAdmin();
#endif
    }

    private void OnGrantNotifClicked(object? sender, EventArgs e)
    {
#if ANDROID
        CareNotificationListener.OpenSettings();
#endif
    }

    /// <summary>去开启无障碍（鸿蒙读通知通道）。</summary>
    private void OnGrantA11yClicked(object? sender, EventArgs e)
    {
#if ANDROID
        QyAccessibilityService.OpenSettings();
#endif
    }

    private async void OnGrantCalendarClicked(object? sender, EventArgs e)
    {
#if ANDROID
        try
        {
            var status = await Permissions.RequestAsync<Permissions.CalendarRead>();
            if (status != PermissionStatus.Granted)
                await DisplayAlert("未授权", "没有日历权限的话，Ta看不到你的日程。", "知道了");
        }
        catch { }
        UpdateSensingStatus();
#endif
    }

    /// <summary>刷新长期记忆条数显示。</summary>
    private async Task UpdateMemoryCountAsync()
    {
        try
        {
            int count = await ChatStore.Instance.CountMemoriesAsync();
            lblMemoryCount.Text = $"已记住 {count} 条关于你的事";
        }
        catch { }
    }

    /// <summary>打开记忆面板：查看Ta记住的每一条、删除单条。</summary>
    private async void OnMemoriesTapped(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new MemoryListPage());
    }

    /// <summary>让Ta用文生图给自己画一张头像（保存后聊天标题栏生效）。</summary>
    private async void OnDrawAvatarClicked(object? sender, EventArgs e)
    {
        if (!AppSettings.ImgEnabled)
        {
            await DisplayAlert("提示", "请先在上方配置文生图模型的 URL 和 Key。", "知道了");
            return;
        }

        btnAvatar.IsEnabled = false;
        try
        {
            bool ok = await AvatarService.GenerateAsync(
                "根据你的人设和性格，画一张你自己喜欢的自画像，体现你的气质");
            if (ok)
                await DisplayAlert("完成", "Ta画好了自己的头像，回到聊天页就能看到。", "知道了");
            else
                await DisplayAlert("失败", "头像生成失败，请检查文生图模型配置。", "知道了");
        }
        catch (Exception ex)
        {
            await DisplayAlert("失败", ex.Message, "知道了");
        }
        finally
        {
            btnAvatar.IsEnabled = true;
        }
    }

    /// <summary>清空Ta记住的关于你的全部记忆。</summary>
    private async void OnClearMemoryClicked(object? sender, EventArgs e)
    {
        if (!await DisplayAlert("确认", "确定清空Ta记住的关于你的全部记忆吗？此操作不可恢复。", "清空", "取消"))
            return;
        try
        {
            await ChatStore.Instance.ClearMemoriesAsync();
            await UpdateMemoryCountAsync();
            await DisplayAlert("已清空", "记忆已清空", "确定");
        }
        catch (Exception ex)
        {
            await DisplayAlert("失败", ex.Message, "确定");
        }
    }

    /// <summary>并发检测 5 个模型（文本对话/文生图/听觉/视觉/TTS）连通状态，更新气泡右侧标签。</summary>
    private async Task CheckAllModelsStatusAsync()
    {
        // 先从当前输入框取值（打开设置页时已由 ParseUi 填充，但用户可能正在编辑，故实时读）
        var chatUrl = entApiUrl.Text?.Trim(); var chatKey = entApiKey.Text?.Trim();
        var imgUrl = entImgApiUrl.Text?.Trim(); var imgKey = entImgApiKey.Text?.Trim();
        var audUrl = entAudioApiUrl.Text?.Trim(); var audKey = entAudioApiKey.Text?.Trim();
        var visUrl = entVisionApiUrl.Text?.Trim(); var visKey = entVisionApiKey.Text?.Trim();
        var ttsUrl = entTtsApiUrl.Text?.Trim(); var ttsKey = entTtsApiKey.Text?.Trim();

        // 先显示"检测中"
        SetStatus(stChatStatus, null); SetStatus(stImgStatus, null);
        SetStatus(stAudioStatus, null); SetStatus(stVisionStatus, null); SetStatus(stTtsStatus, null);

        try
        {
            var chatTask = TestConnectionAsync(chatUrl, chatKey);
            var imgTask = TestConnectionAsync(imgUrl, imgKey);
            var audTask = TestConnectionAsync(audUrl, audKey);
            var visTask = TestConnectionAsync(visUrl, visKey);
            var ttsTask = TestConnectionAsync(ttsUrl, ttsKey);

            // 等待所有检测完成（最长 8s；超时不抛异常，直接按离线处理）
            var all = Task.WhenAll(chatTask, imgTask, audTask, visTask, ttsTask);
            try { await all.WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (TimeoutException) { }
            catch { }

            SetStatus(stChatStatus, await SafeResultAsync(chatTask));
            SetStatus(stImgStatus, await SafeResultAsync(imgTask));
            SetStatus(stAudioStatus, await SafeResultAsync(audTask));
            SetStatus(stVisionStatus, await SafeResultAsync(visTask));
            SetStatus(stTtsStatus, await SafeResultAsync(ttsTask));
        }
        catch { /* 页面卸载等场景忽略 */ }
    }

    /// <summary>安全获取任务结果：任务未完成/抛异常一律视为离线。</summary>
    private static async Task<bool> SafeResultAsync(Task<bool> task)
    {
        try { return task.IsCompletedSuccessfully && await task; }
        catch { return false; }
    }

    /// <summary>设置状态徽章：true=·在线(绿)，false=·离线(红)，null=检测中/空。</summary>
    private static void SetStatus(StatusBadge badge, bool? online)
        => badge.SetStatus(online);

    // 测试模型 API 连通性：请求 /models 端点，返回是否成功。
    private async Task<bool> TestConnectionAsync(string? url, string? key)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key)) return false;
        try
        {
            var modelsUrl = DeriveModelsUrl(url);
            using var req = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
            req.Headers.Add("Authorization", $"Bearer {key}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>重检单个模型并刷新其状态徽章。</summary>
    private async Task CheckOneModelStatusAsync(Entry urlEntry, Entry keyEntry, StatusBadge badge)
    {
        var url = urlEntry.Text?.Trim();
        var key = keyEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
        {
            badge.SetStatus(false); // 未填 → 离线
            return;
        }
        SetStatus(badge, null); // 检测中
        bool ok = await TestConnectionAsync(url, key);
        SetStatus(badge, ok);
    }

    // 编辑配置自动重检：防抖（停止输入 800ms 后才发请求），避免每次敲字都请求
    private CancellationTokenSource? _debounceCts;

    private void ScheduleCheck(Entry urlEntry, Entry keyEntry, StatusBadge badge)
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        _ = Task.Run(async () =>
        {
            await Task.Delay(800, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            await Dispatcher.DispatchAsync(() => CheckOneModelStatusAsync(urlEntry, keyEntry, badge));
        });
    }

    private void OnApiUrlTextChanged(object? sender, TextChangedEventArgs e)
        => ScheduleCheck(entApiUrl, entApiKey, stChatStatus);
    private void OnApiKeyTextChanged(object? sender, TextChangedEventArgs e)
        => ScheduleCheck(entApiUrl, entApiKey, stChatStatus);

    private void OnImgUrlTextChanged(object? sender, TextChangedEventArgs e)
        => ScheduleCheck(entImgApiUrl, entImgApiKey, stImgStatus);
    private void OnImgKeyTextChanged(object? sender, TextChangedEventArgs e)
        => ScheduleCheck(entImgApiUrl, entImgApiKey, stImgStatus);

    private void OnAudioUrlTextChanged(object? sender, TextChangedEventArgs e)
        => ScheduleCheck(entAudioApiUrl, entAudioApiKey, stAudioStatus);
    private void OnAudioKeyTextChanged(object? sender, TextChangedEventArgs e)
        => ScheduleCheck(entAudioApiUrl, entAudioApiKey, stAudioStatus);

    private void OnVisionUrlTextChanged(object? sender, TextChangedEventArgs e)
        => ScheduleCheck(entVisionApiUrl, entVisionApiKey, stVisionStatus);
    private void OnVisionKeyTextChanged(object? sender, TextChangedEventArgs e)
        => ScheduleCheck(entVisionApiUrl, entVisionApiKey, stVisionStatus);

    private void OnTtsUrlTextChanged(object? sender, TextChangedEventArgs e)
        => ScheduleCheck(entTtsApiUrl, entTtsApiKey, stTtsStatus);
    private void OnTtsKeyTextChanged(object? sender, TextChangedEventArgs e)
        => ScheduleCheck(entTtsApiUrl, entTtsApiKey, stTtsStatus);

    /// <summary>从任意 API 端点推导 /models 列表地址。</summary>
    private static string DeriveModelsUrl(string url)
    {
        foreach (var suffix in new[] { "/chat/completions", "/images/generations", "/audio/transcriptions", "/audio/speech" })
        {
            if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return url[..^suffix.Length] + "/models";
        }
        return url.TrimEnd('/') + "/models";
    }

    /// <summary>刷新 Shizuku 连接状态显示：online（绿）/ offline（红）。</summary>
    private void UpdateShizukuStatus()
    {
        bool online = ShizukuHelper.IsOnline;
        lblShizukuStatus.Text = online ? "online" : "offline";
        lblShizukuStatus.TextColor = online ? Colors.LimeGreen : Colors.Red;
    }

    private void ParseUi()
    {
        entApiKey.Text = AppSettings.ApiKey;
        entApiUrl.Text = AppSettings.ApiUrl;
        entThinkingModel.Text = AppSettings.ThinkingModel;
        edtPersona.Text = AppSettings.Persona;
        swForceThink.IsToggled = AppSettings.ForceThinking;

        // AI 性格
        picPersonality.ItemsSource = AppSettings.Personalities;
        int personalityIdx = Array.IndexOf(AppSettings.Personalities, AppSettings.Personality);
        picPersonality.SelectedIndex = personalityIdx >= 0 ? personalityIdx : 0;

        // 人设模式：已有自定义人设 → 自定义；否则官方预设
        _usePreset = string.IsNullOrWhiteSpace(AppSettings.Persona);
        ApplyPersonaMode(_usePreset, animate: false);

        // 模型：为空时留空，点「获取模型」后填入列表
        if (string.IsNullOrWhiteSpace(AppSettings.Model))
        {
            picModel.ItemsSource = Array.Empty<string>();
            picModel.SelectedIndex = -1;
        }
        else
        {
            // 找到当前模型对应的显示项（可能带 token）
            string? label = null;
            foreach (var kv in _modelOptions)
            {
                if (kv.Value.Equals(AppSettings.Model, StringComparison.OrdinalIgnoreCase))
                { label = kv.Key; break; }
            }
            if (label == null)
                label = AppSettings.Model;
            picModel.ItemsSource = new[] { label };
            picModel.SelectedIndex = 0;
        }

        OnForceThinkToggled(this, new ToggledEventArgs(swForceThink.IsToggled));

        // 自动上下文压缩阈值
        int threshold = AppSettings.AutoCompressThreshold;
        bool enabled = threshold > 0;
        swAutoCompress.IsToggled = enabled;
        sldAutoCompress.IsEnabled = enabled;
        if (threshold >= 80)
            sldAutoCompress.Value = threshold;
        else
            sldAutoCompress.Value = 80;
        UpdateAutoCompressLabel();

        // 多模态模型配置
        entImgApiUrl.Text = AppSettings.ImgApiUrl;
        entImgApiKey.Text = AppSettings.ImgApiKey;
        FillModelPicker(picImgModel, AppSettings.ImgModel);
        entAudioApiUrl.Text = AppSettings.AudioApiUrl;
        entAudioApiKey.Text = AppSettings.AudioApiKey;
        FillModelPicker(picAudioModel, AppSettings.AudioModel);
        entVisionApiUrl.Text = AppSettings.VisionApiUrl;
        entVisionApiKey.Text = AppSettings.VisionApiKey;
        FillModelPicker(picVisionModel, AppSettings.VisionModel);
        entTtsApiUrl.Text = AppSettings.TtsApiUrl;
        entTtsApiKey.Text = AppSettings.TtsApiKey;
        FillModelPicker(picTtsModel, AppSettings.TtsModel);
        entTtsVoice.Text = AppSettings.TtsVoice;
        swTtsAutoPlay.IsToggled = AppSettings.TtsAutoPlay;

        // 主动关心
        swCare.IsToggled = AppSettings.CareEnabled;
        entCareQuietStart.Text = $"{AppSettings.CareQuietStartMin / 60}:{AppSettings.CareQuietStartMin % 60:00}";
        entCareQuietEnd.Text = $"{AppSettings.CareQuietEndMin / 60}:{AppSettings.CareQuietEndMin % 60:00}";
        entCareDailyCap.Text = AppSettings.CareDailyCap.ToString();
        swCareSense.IsToggled = AppSettings.CareSenseEnabled;
        UpdateCareDesc(swCare.IsToggled);

        // 每条消息 AI 必看
        swReviewEach.IsToggled = AppSettings.ReviewEachMessage;
        UpdateReviewDesc(swReviewEach.IsToggled);

        // Agent 循环执行模式
        swAgentLoop.IsToggled = AppSettings.AgentLoopEnabled;
        sldAgentTime.Value = AppSettings.AgentTimeLimitLevel;
        sldAgentTime.IsEnabled = swAgentLoop.IsToggled;
        UpdateAgentLoopDesc(swAgentLoop.IsToggled);
        UpdateAgentTimeLabel();
        entWorkspace.Text = AppSettings.WorkspacePath;
        UpdateWorkspaceHint();

        // AI 浏览器
        InitAiBrowserUi();

        // 主题色
        BuildThemeColorPicker();
    }

    // ══════════════ 🌐 AI 浏览器 ══════════════

    private const string UaCustomTag = "自定义…";

    private void InitAiBrowserUi()
    {
        swAiBrowser.IsToggled = AppSettings.AiBrowserEnabled;
        UpdateAiBrowserDesc(swAiBrowser.IsToggled);

        swSaveCookies.IsToggled = AppSettings.BrowserSaveCookies;
        UpdateSaveCookiesDesc(swSaveCookies.IsToggled);

        swLoadImages.IsToggled = AppSettings.BrowserLoadImages;
        UpdateLoadImagesDesc(swLoadImages.IsToggled);

        picUserAgent.ItemsSource = new List<string> { "手机（默认）", "桌面 Chrome", UaCustomTag };
        picUserAgent.SelectedIndex = AppSettings.BrowserUserAgent switch
        {
            "desktop" => 1,
            "" or "mobile" => 0,
            _ => 2
        };
        entUserAgent.Text = AppSettings.BrowserUserAgent is "" or "mobile" or "desktop"
            ? "" : AppSettings.BrowserUserAgent;

        picDownloadDir.ItemsSource = new List<string> { "工作区（内部存储/QingYangAI/WorkSpace）", "公共 Download 目录" };
        picDownloadDir.SelectedIndex = AppSettings.BrowserDownloadDir == "download" ? 1 : 0;

        entMaxDownloadMb.Text = AppSettings.BrowserMaxDownloadMb.ToString();

        swManagedHttp.IsToggled = AppSettings.UseManagedHttpStack;
        UpdateManagedHttpDesc(swManagedHttp.IsToggled);
        UpdateStreamDiagLabel();

        UpdateBrowserHistoryLabel();
    }

    private void UpdateStreamDiagLabel() => lblStreamDiag.Text = AppSettings.LastStreamDiag;

    private void UpdateManagedHttpDesc(bool on) => lblManagedHttpDesc.Text = on ? "流式兼容模式：开" : "流式兼容模式：关";

    private void OnManagedHttpToggled(object? sender, ToggledEventArgs e)
    {
        AppSettings.UseManagedHttpStack = e.Value;
        UpdateManagedHttpDesc(e.Value);
    }

    private void UpdateBrowserHistoryLabel() =>
        lblBrowserHistory.Text = "浏览历史：" + AppSettings.BrowserHistory().Count + " 条";

    private void UpdateAiBrowserDesc(bool on) => lblAiBrowserDesc.Text = on ? "开启" : "关闭";
    private void UpdateSaveCookiesDesc(bool on) => lblSaveCookiesDesc.Text = on ? "保存（保留登录态）" : "不保存（每次新访客）";
    private void UpdateLoadImagesDesc(bool on) => lblLoadImagesDesc.Text = on ? "加载" : "不加载（省流量）";

    private void OnAiBrowserToggled(object? sender, ToggledEventArgs e)
    {
        AppSettings.AiBrowserEnabled = e.Value;
        UpdateAiBrowserDesc(e.Value);
    }

    private void OnSaveCookiesToggled(object? sender, ToggledEventArgs e)
    {
        AppSettings.BrowserSaveCookies = e.Value;
        UpdateSaveCookiesDesc(e.Value);
        if (!e.Value)
        {
            // 关掉就顺手清一次，别留着旧登录态
            WebAgent.ClearCookies();
            BrowserTool.ClearCookies();
        }
    }

    private void OnLoadImagesToggled(object? sender, ToggledEventArgs e)
    {
        AppSettings.BrowserLoadImages = e.Value;
        UpdateLoadImagesDesc(e.Value);
        WebAgent.ApplySettings();
    }

    private void OnUserAgentChanged(object? sender, EventArgs e)
    {
        var v = picUserAgent.SelectedIndex switch
        {
            1 => "desktop",
            2 => entUserAgent.Text?.Trim() ?? "",
            _ => ""
        };
        AppSettings.BrowserUserAgent = v;
        WebAgent.ApplySettings();
        BrowserTool.ApplySettings();
    }

    private void OnUserAgentTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (picUserAgent.SelectedIndex != 2) return;
        AppSettings.BrowserUserAgent = e.NewTextValue?.Trim() ?? "";
        WebAgent.ApplySettings();
        BrowserTool.ApplySettings();
    }

    private void OnDownloadDirChanged(object? sender, EventArgs e)
    {
        AppSettings.BrowserDownloadDir = picDownloadDir.SelectedIndex == 1 ? "download" : "";
    }

    private void OnMaxDownloadMbChanged(object? sender, FocusEventArgs e)
    {
        if (int.TryParse(entMaxDownloadMb.Text?.Trim(), out var mb))
            AppSettings.BrowserMaxDownloadMb = mb;
        entMaxDownloadMb.Text = AppSettings.BrowserMaxDownloadMb.ToString();
    }

    private async void OnViewHistoryClicked(object? sender, EventArgs e)
    {
        var list = AppSettings.BrowserHistory();
        var text = list.Count == 0 ? "还没有浏览记录。" : string.Join("\n", list);
        await DisplayAlert("Ta 的浏览历史（最近 50 条）", text, "好");
    }

    private async void OnClearCookiesClicked(object? sender, EventArgs e)
    {
        bool ok = await DisplayAlert("清除 Cookie",
            "会把 Ta 在浏览器里的所有登录态清掉（下次访问网站需要重新登录）。\n\n确定吗？", "清除", "取消");
        if (!ok) return;
        WebAgent.ClearCookies();
        BrowserTool.ClearCookies();
        await DisplayAlert("已清除", "Cookie 清干净了。", "好");
    }

    private async void OnClearHistoryClicked(object? sender, EventArgs e)
    {
        bool ok = await DisplayAlert("清空浏览历史", "只清记录，不动 Cookie。确定吗？", "清空", "取消");
        if (!ok) return;
        AppSettings.ClearBrowserHistory();
        UpdateBrowserHistoryLabel();
        await DisplayAlert("已清空", "浏览历史清空了。", "好");
    }

    // ══════════════ 📄 关于与协议 ══════════════

    private void UpdateAgreementVersionLabel() =>
        lblAgreementVersion.Text = $"版本 v{AgreementContent.Version} · 生效日期 {AgreementContent.EffectiveDate}";

    /// <summary>点进只读协议页（正文与首次启动那份完全一致，只是按钮变成「返回」）。</summary>
    private async void OnAgreementTapped(object? sender, EventArgs e)
    {
        try
        {
            await Navigation.PushAsync(new AgreementPage(AgreementMode.ReadOnly));
        }
        catch (Exception ex)
        {
            // 正常不会走到这儿；万一没有 NavigationPage，给个人话而不是闪退
            await DisplayAlert("打不开协议页", ex.Message, "好");
        }
    }

    /// <summary>刷新「Agent 循环」说明文字。</summary>
    private void UpdateAgentLoopDesc(bool on) =>
        lblAgentLoopDesc.Text = on
            ? "开启：Ta 会自主多步推进任务，过程可展开查看"
            : "关闭：一次问答一个来回";

    /// <summary>刷新时限档位标签。</summary>
    private void UpdateAgentTimeLabel() =>
        lblAgentTimeValue.Text = $"执行时限：{AppSettings.AgentTimeLimitText}";

    /// <summary>刷新工作区提示：显示真实路径、用户可见性、以及系统存储权限状态。</summary>
    private void UpdateWorkspaceHint()
    {
        var effective = AppSettings.EffectiveWorkspacePath;
        var display = StorageAccess.ToDisplay(effective);
        bool granted = StorageAccess.IsAllFilesGranted();
        bool visible = StorageAccess.WorkspaceVisible;

        var sb = new System.Text.StringBuilder();
        sb.Append(string.IsNullOrWhiteSpace(AppSettings.WorkspacePath) ? "当前使用默认目录：" : "当前工作区：");
        sb.Append(display);
        sb.AppendLine();
        sb.Append(visible
            ? "✓ 在文件管理器里可见（内部存储）"
            : "✗ 位于应用私有目录，文件管理器看不到；可在聊天页盾牌面板里「导出到 Download」");
        sb.AppendLine();
        sb.Append(granted
            ? "✓ 系统「所有文件访问」已授予"
            : "✗ 系统「所有文件访问」未授予（全盘读写不可用，可在下方「存储访问」里开启）");

        lblWorkspaceHint.Text = sb.ToString();
    }

    /// <summary>Agent 循环开关（即时生效）。</summary>
    private void OnAgentLoopToggled(object? sender, ToggledEventArgs e)
    {
        UpdateAgentLoopDesc(e.Value);
        sldAgentTime.IsEnabled = e.Value;
        if (e.Value == AppSettings.AgentLoopEnabled) return;
        AppSettings.AgentLoopEnabled = e.Value;
    }

    /// <summary>Agent 时限档位滑动（即时生效）。</summary>
    private void OnAgentTimeChanged(object? sender, ValueChangedEventArgs e)
    {
        AppSettings.AgentTimeLimitLevel = (int)Math.Round(e.NewValue);
        UpdateAgentTimeLabel();
    }

    /// <summary>导出工作区文件到公共 Download 目录。</summary>
    private async void OnExportWorkspaceClicked(object? sender, EventArgs e)
    {
        if (StorageAccess.WorkspaceVisible)
        {
            await DisplayAlert("无需导出",
                $"当前工作区已经在公共存储里，文件管理器可以直接看到：\n\n"
                + StorageAccess.ToDisplay(AppSettings.EffectiveWorkspacePath),
                "好");
            return;
        }

        if (!StorageAccess.IsAllFilesGranted())
        {
            bool go = await DisplayAlert("需要存储权限",
                "导出到 Download 需要" + StorageAccess.PermissionLabel + "。\n\n"
                + (SuperAdmin.HasAllFilesAccessApi
                    ? "点「去开启」跳转系统设置，找到「青阳AI」并打开「允许访问所有文件」。"
                    : "点「去开启」会弹出系统的存储权限申请，允许即可。"),
                "去开启", "暂不");
            if (go)
            {
                await StorageAccess.RequestPermissionAsync();
                StorageAccess.InvalidateProbe();
                UpdateWorkspaceHint();
            }
            return;
        }

        var (ok, msg) = await StorageAccess.ExportWorkspaceAsync();
        await DisplayAlert(ok ? "导出完成" : "导出失败", msg, "好");
        UpdateWorkspaceHint();
    }

    /// <summary>刷新主动关心开关旁的说明文字。</summary>
    private void UpdateCareDesc(bool on) =>
        lblCareDesc.Text = on
            ? "开启：Ta会主动关心你（遵守免打扰与每日上限）"
            : "关闭：Ta不会主动找你";

    /// <summary>刷新「每条消息 AI 必看」开关旁的说明文字。</summary>
    private void UpdateReviewDesc(bool on) =>
        lblReviewDesc.Text = on ? "开启" : "关闭";

    /// <summary>构建主题色选择器（圆点按钮）。</summary>
    private void BuildThemeColorPicker()
    {
        var current = AppSettings.ThemeColorHex;
        foreach (var (name, hex) in AppSettings.ThemeColorPresets)
        {
            var isSelected = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
            var dot = new Border
            {
                WidthRequest = 36,
                HeightRequest = 36,
                BackgroundColor = Color.Parse(hex),
                StrokeThickness = isSelected ? 3 : 0,
                Stroke = Colors.White,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 }
            };
            dot.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(async () => await OnThemeColorSelectedAsync(hex, name))
            });
            themeColorRow.Children.Add(dot);
        }
    }

    /// <summary>用户选择一个主题色。</summary>
    private async Task OnThemeColorSelectedAsync(string hex, string name)
    {
        AppSettings.ThemeColorHex = hex;
        BuildThemeColorPicker(); // 重建以更新选中状态
        ApplyThemeColor();
        await DisplayAlert("主题色", $"已切换为「{name}」", "好");
    }

    /// <summary>把当前主题色应用到设置页的关键元素。</summary>
    private void ApplyThemeColor()
    {
        var theme = AppSettings.ThemeColor;
        var oldColor = Color.Parse("#FBB5B2");
        // 所有 #FBB5B2 的标题、按钮、图标
        ApplyThemeToVisualTree(this, theme, oldColor);
    }

    /// <summary>递归遍历可视树，把 oldColor 替换成 theme。</summary>
    private static void ApplyThemeToVisualTree(VisualElement element, Color theme, Color oldColor)
    {
        if (element is Label lbl && lbl.TextColor == oldColor) lbl.TextColor = theme;
        if (element is Button btn && btn.BackgroundColor == oldColor) btn.BackgroundColor = theme;
        if (element is Border bd)
        {
            if (bd.Stroke is SolidColorBrush sb && sb.Color == oldColor) bd.Stroke = new SolidColorBrush(theme);
            if (bd.BackgroundColor == oldColor) bd.BackgroundColor = theme;
        }
        if (element is MorphIcon mi && mi.IconColor == oldColor) mi.IconColor = theme;

        if (element is Layout layout)
        {
            foreach (var child in layout.Children)
            {
                if (child is VisualElement ve)
                    ApplyThemeToVisualTree(ve, theme, oldColor);
            }
        }
    }

    /// <summary>每条消息 AI 必看 开关切换。</summary>
    private void OnReviewEachToggled(object? sender, ToggledEventArgs e)
    {
        if (e.Value == AppSettings.ReviewEachMessage) return;
        AppSettings.ReviewEachMessage = e.Value;
        if (!e.Value) MessageReviewService.Clear();
    }

    /// <summary>清空已生成的复盘缓存（不关闭开关，只清历史）。</summary>
    private async void OnClearReviewClicked(object? sender, EventArgs e)
    {
        MessageReviewService.Clear();
        await DisplayAlert("已清空", "已生成的复盘缓存已删除。下次你发消息时重新开始。", "好");
    }

    private static void FillModelPicker(Picker picker, string model)
    {
        if (string.IsNullOrWhiteSpace(model))
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

    /// <summary>应用人设模式：官方预设 / 自定义。animate=true 时标题变色+宽高变化走二次方 ease-out。</summary>
    private void ApplyPersonaMode(bool usePreset, bool animate)
    {
        if (usePreset == _usePreset && !animate) { /* 初始无动画直接摆好 */ }
        _usePreset = usePreset;

        // 标题子集文字
        lblPersonaSub.Text = usePreset ? " > 青阳官方预设" : " > 自定义";
        lblPersonaSub.IsVisible = true;

        // 两个小气泡选中态边框/文字色
        bubblePreset.Stroke = usePreset ? Color.FromArgb("#7B68EE") : Color.FromArgb("#555555");
        bubbleCustom.Stroke = usePreset ? Color.FromArgb("#555555") : Color.FromArgb("#7B68EE");
        lblPreset.TextColor = usePreset ? Colors.White : Color.FromArgb("#AAAAAA");
        lblCustom.TextColor = usePreset ? Color.FromArgb("#AAAAAA") : Colors.White;

        if (animate)
        {
            // 标题白→灰（二次方 ease-out）：
            AnimateLabelColor(lblPersonaTitle, Colors.White, Color.FromArgb("#9E9E9E"));
            // 内容区高度变化（二次方 ease-out）：
            AnimateContentHeight(usePreset);
        }
        else
        {
            lblPersonaTitle.TextColor = Color.FromArgb("#9E9E9E");
        }

        // 内容切换（先隐藏旧面板避免闪烁，再显示新面板）
        panelPreset.IsVisible = usePreset;
        panelCustom.IsVisible = !usePreset;
    }

    /// <summary>Label 颜色过渡动画（二次方 ease-out：先快后慢）。</summary>
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
        label.Animate("labelColor" + label.GetHashCode(), Step, 0, 1, 16, durationMs, QuadraticEaseOut);
    }

    /// <summary>内容区高度过渡动画（二次方 ease-out）。</summary>
    private void AnimateContentHeight(bool usePreset)
    {
        double targetH = usePreset ? 72 : 176; // 预设面板(下拉) / 自定义(140 编辑器+边距)
        var box = personaContentBox;
        box.BatchBegin();
        double fromH = box.Height > 0 ? box.Height : targetH;
        box.BatchCommit();

        box.Animate("contentHeight", v => box.HeightRequest = v, fromH, targetH, 16, 320, QuadraticEaseOut,
            finished: (v, cancelled) =>
            {
                if (cancelled) return;
                box.HeightRequest = -1; // 恢复自动高度
            });
    }

    /// <summary>点击「青阳AI官方预设」小气泡。</summary>
    private void OnPresetTapped(object? sender, EventArgs e)
    {
        if (_usePreset) return;
        ApplyPersonaMode(true, animate: true);
    }

    /// <summary>点击「自定义」小气泡。</summary>
    private void OnCustomTapped(object? sender, EventArgs e)
    {
        if (!_usePreset) return;
        ApplyPersonaMode(false, animate: true);
    }

    /// <summary>粘贴 API Key（从剪贴板）。</summary>
    private async void OnPasteApiKeyClicked(object? sender, EventArgs e)
    {
        var text = await Clipboard.Default.GetTextAsync();
        if (!string.IsNullOrEmpty(text)) entApiKey.Text = text.Trim();
    }

    /// <summary>展开/收起「文本对话模型」气泡。</summary>
    private void OnToggleChatBubble(object? sender, EventArgs e)
    {
        ToggleBubble(chatBubbleContent, chatArrow);
    }

    /// <summary>展开/收起「文生图模型」气泡。</summary>
    private void OnToggleImgBubble(object? sender, EventArgs e)
    {
        ToggleBubble(imgBubbleContent, imgArrow);
    }

    /// <summary>展开/收起「听觉模型」气泡。</summary>
    private void OnToggleAudioBubble(object? sender, EventArgs e)
    {
        ToggleBubble(audioBubbleContent, audioArrow);
    }

    /// <summary>展开/收起「视觉模型」气泡。</summary>
    private void OnToggleVisionBubble(object? sender, EventArgs e)
    {
        ToggleBubble(visionBubbleContent, visionArrow);
    }

    /// <summary>展开/收起「文字转语音模型」气泡。</summary>
    private void OnToggleTtsBubble(object? sender, EventArgs e)
    {
        ToggleBubble(ttsBubbleContent, ttsArrow);
    }

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

            content.Animate("bubbleHeight", v => content.HeightRequest = v, 0d, targetH, 16, 260, QuadraticEaseOut);
            await content.FadeTo(1, 220, QuadraticEaseOut);
            await Task.Delay(60);            // 等高度动画收尾
            content.HeightRequest = -1;      // 恢复自动高度
        }
        else
        {
            double fromH = content.Height > 0 ? content.Height : content.HeightRequest;
            if (fromH <= 0) fromH = 200;

            content.Animate("bubbleHeight", v => content.HeightRequest = Math.Max(0, v), fromH, 0d, 16, 200, QuadraticEaseOut);
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

    /// <summary>清空 API Key。</summary>
    private void OnClearApiKeyClicked(object? sender, EventArgs e) => entApiKey.Text = "";

    /// <summary>粘贴 API URL（从剪贴板）。</summary>
    private async void OnPasteApiUrlClicked(object? sender, EventArgs e)
    {
        var text = await Clipboard.Default.GetTextAsync();
        if (!string.IsNullOrEmpty(text)) entApiUrl.Text = text.Trim();
    }

    /// <summary>清空 API URL。</summary>
    private void OnClearApiUrlClicked(object? sender, EventArgs e) => entApiUrl.Text = "";

    // ─────────── 多模态模型：粘贴/清空（URL 与 Key）───────────
    private async void OnPasteImgUrlClicked(object? sender, EventArgs e) => await PasteToAsync(entImgApiUrl);
    private void OnClearImgUrlClicked(object? sender, EventArgs e) => entImgApiUrl.Text = "";
    private async void OnPasteImgKeyClicked(object? sender, EventArgs e) => await PasteToAsync(entImgApiKey);
    private void OnClearImgKeyClicked(object? sender, EventArgs e) => entImgApiKey.Text = "";

    private async void OnPasteAudioUrlClicked(object? sender, EventArgs e) => await PasteToAsync(entAudioApiUrl);
    private void OnClearAudioUrlClicked(object? sender, EventArgs e) => entAudioApiUrl.Text = "";
    private async void OnPasteAudioKeyClicked(object? sender, EventArgs e) => await PasteToAsync(entAudioApiKey);
    private void OnClearAudioKeyClicked(object? sender, EventArgs e) => entAudioApiKey.Text = "";

    private async void OnPasteVisionUrlClicked(object? sender, EventArgs e) => await PasteToAsync(entVisionApiUrl);
    private void OnClearVisionUrlClicked(object? sender, EventArgs e) => entVisionApiUrl.Text = "";
    private async void OnPasteVisionKeyClicked(object? sender, EventArgs e) => await PasteToAsync(entVisionApiKey);
    private void OnClearVisionKeyClicked(object? sender, EventArgs e) => entVisionApiKey.Text = "";

    private async void OnPasteTtsUrlClicked(object? sender, EventArgs e) => await PasteToAsync(entTtsApiUrl);
    private void OnClearTtsUrlClicked(object? sender, EventArgs e) => entTtsApiUrl.Text = "";
    private async void OnPasteTtsKeyClicked(object? sender, EventArgs e) => await PasteToAsync(entTtsApiKey);
    private void OnClearTtsKeyClicked(object? sender, EventArgs e) => entTtsApiKey.Text = "";

    /// <summary>把剪贴板文本粘贴到指定输入框。</summary>
    private static async Task PasteToAsync(Entry entry)
    {
        var text = await Clipboard.Default.GetTextAsync();
        if (!string.IsNullOrEmpty(text)) entry.Text = text.Trim();
    }

    private void OnForceThinkToggled(object? sender, ToggledEventArgs e)
    {
        // 说明当前推理等级与思考模式的映射关系
        lblForceThink.Text = e.Value
            ? "已开启：使用深度思考模型（deepseek-reasoner）"
            : "已关闭：使用普通模型（按所选模型）";
    }

    private void OnAutoCompressToggled(object? sender, ToggledEventArgs e)
    {
        sldAutoCompress.IsEnabled = e.Value;
        UpdateAutoCompressLabel();
    }

    private void OnAutoCompressChanged(object? sender, ValueChangedEventArgs e)
    {
        // 自动吸附到最近 5% 档位（80、85、90、95）
        double snapped = Math.Round(e.NewValue / 5.0) * 5.0;
        snapped = Math.Clamp(snapped, 80, 95);
        if (Math.Abs(snapped - e.NewValue) > 0.001)
            sldAutoCompress.Value = snapped;
        UpdateAutoCompressLabel();
    }

    private void UpdateAutoCompressLabel()
    {
        bool enabled = swAutoCompress.IsToggled;
        int threshold = enabled ? (int)Math.Round(sldAutoCompress.Value) : 0;
        sldAutoCompress.IsEnabled = enabled;
        lblAutoCompressDesc.Text = enabled ? "已开启" : "禁用";
        lblAutoCompressDesc.TextColor = enabled ? Colors.LimeGreen : Color.FromArgb("#AAAAAA");
        lblAutoCompressValue.Text = enabled ? $"阈值：{threshold}%" : "阈值：--";
    }

    private async void OnFetchModelsClicked(object? sender, EventArgs e)
    {
        var key = entApiKey.Text?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            await DisplayAlert("提示", "请先填写 API Key", "确定");
            return;
        }

        btnFetchModels.IsEnabled = false;
        try
        {
            var url = AppSettings.GetModelsEndpoint();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Authorization", $"Bearer {key}");
            var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var ids = new List<string>();
            // 每个模型的上下文上限：从多个可能的字段名取值（取较大者）
            var maxTokenMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = m.GetProperty("id").GetString();
                if (string.IsNullOrEmpty(id)) continue;
                ids.Add(id);

                int mt = 0, mot = 0;
                if (m.TryGetProperty("max_tokens", out var t1) && t1.TryGetInt32(out var v1)) mt = v1;
                if (m.TryGetProperty("max_output_tokens", out var t2) && t2.TryGetInt32(out var v2)) mot = v2;
                int extra = 0;
                // 兼容尽量多的字段语法
                TryGetFirstInt(m, out extra,
                    "context_length", "context_window", "max_input_tokens",
                    "max_context_tokens", "context_size", "max_context_length",
                    "context_window_size", "max_sequence_length", "n_ctx");
                var maxN = Math.Max(Math.Max(mt, mot), extra);
                if (maxN > 0)
                    maxTokenMap[id] = maxN;

                // 推理档位能力：模型返回了就记下来（聊天页顶部「思考」按钮用）
                var reasoning = ParseReasoningCapability(m);
                if (reasoning.Supported)
                    _reasoningByModel[id] = reasoning;
            }

            if (ids.Count == 0)
            {
                await DisplayAlert("提示", "接口没有返回任何模型", "确定");
                return;
            }

            // 选项显示 模型id + 最大token（有则显示，无则不显示）
            _modelOptions.Clear(); _idToMax.Clear();
            var labels = new List<string>();
            foreach (var id in ids)
            {
                _idToMax[id] = maxTokenMap.TryGetValue(id, out var mtk) ? mtk : 0;
                string label = _idToMax[id] > 0 ? $"{id} ({AppSettings.FormatTokens(_idToMax[id])})" : id;
                labels.Add(label);
                _modelOptions[label] = id;
            }

            picModel.ItemsSource = labels;
            // 选中当前使用的模型（通过反查原始 id 的 label）
            int cur = -1;
            for (int i = 0; i < labels.Count; i++)
            {
                if (_modelOptions[labels[i]].Equals(AppSettings.Model, StringComparison.OrdinalIgnoreCase))
                { cur = i; break; }
            }
            picModel.SelectedIndex = cur >= 0 ? cur : 0;

            // 记住本次拉取到的 上下文表，供保存时按所选模型写入
            _fetchedMaxTokens = maxTokenMap;

            // 统计能显示最大 token 的模型数量
            int shown = _idToMax.Values.Count(v => v > 0);
            await DisplayAlert("成功",
                $"获取到 {ids.Count} 个模型，其中 {shown} 个返回了上下文上限。", "确定");
        }
        catch (Exception ex)
        {
            await DisplayAlert("获取失败", ex.Message, "确定");
        }
        finally
        {
            btnFetchModels.IsEnabled = true;
        }
    }

    // ─────────── 多模态模型：获取模型列表（各模型独立 URL/Key）───────────

    private async void OnFetchImgModelsClicked(object? sender, EventArgs e)
        => await FetchModalModelsAsync(entImgApiUrl, entImgApiKey, picImgModel, btnFetchImgModels, "文生图");

    private async void OnFetchAudioModelsClicked(object? sender, EventArgs e)
        => await FetchModalModelsAsync(entAudioApiUrl, entAudioApiKey, picAudioModel, btnFetchAudioModels, "听觉");

    private async void OnFetchVisionModelsClicked(object? sender, EventArgs e)
        => await FetchModalModelsAsync(entVisionApiUrl, entVisionApiKey, picVisionModel, btnFetchVisionModels, "视觉");

    private async void OnFetchTtsModelsClicked(object? sender, EventArgs e)
        => await FetchModalModelsAsync(entTtsApiUrl, entTtsApiKey, picTtsModel, btnFetchTtsModels, "文字转语音");

    /// <summary>从对应 API 的 /models 端点拉取模型列表填入 Picker。</summary>
    private async Task FetchModalModelsAsync(Entry urlEntry, Entry keyEntry, Picker picker, Button btn, string label)
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
            // 从 chat/completions 或 images/generations 等端点推导 /models
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
            picker.ItemsSource = ids;
            picker.SelectedIndex = ids.Count > 0 ? 0 : -1;
            await DisplayAlert("成功", $"获取到 {ids.Count} 个{label}模型。", "确定");
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

    /// <summary>按顺序尝试从 JsonElement 读取多个字段名中的第一个整数值。</summary>
    private static void TryGetFirstInt(System.Text.Json.JsonElement el, out int result, params string[] names)
    {
        result = 0;
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var v) && v.TryGetInt32(out var val) && val > 0)
            {
                result = val;
                return;
            }
        }
    }

    /// <summary>
    /// 解析单个模型条目的推理档位能力（各厂商字段风格不同，尽量多认几种）：
    /// - reasoning_effort 为数组 → 档位列表；为字符串 → 支持，默认 low/medium/high
    /// - reasoning 为 true / 对象（内含 levels/options/effort(s) 数组）
    /// - supported_parameters 数组提及 reasoning_effort / reasoning
    /// - reasoning_levels 数组
    /// 都没有 → 不支持。
    /// </summary>
    private static (bool Supported, List<string> Levels) ParseReasoningCapability(System.Text.Json.JsonElement m)
    {
        var levels = new List<string>();
        try
        {
            if (m.TryGetProperty("reasoning_effort", out var re))
            {
                if (re.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var v in re.EnumerateArray())
                        if (v.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                            levels.Add(v.GetString()!.Trim());
                }
                else if (re.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    levels.AddRange(new[] { "low", "medium", "high" });
                }
            }

            if (levels.Count == 0 && m.TryGetProperty("reasoning", out var r))
            {
                if (r.ValueKind == System.Text.Json.JsonValueKind.True)
                {
                    levels.AddRange(new[] { "low", "medium", "high" });
                }
                else if (r.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var key in new[] { "levels", "options", "effort", "efforts" })
                    {
                        if (r.TryGetProperty(key, out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var v in arr.EnumerateArray())
                                if (v.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                                    levels.Add(v.GetString()!.Trim());
                            if (levels.Count > 0) break;
                        }
                    }
                }
            }

            if (levels.Count == 0 && m.TryGetProperty("supported_parameters", out var sp) && sp.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var v in sp.EnumerateArray())
                {
                    if (v.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                    var s = v.GetString() ?? "";
                    if (s.Equals("reasoning_effort", StringComparison.OrdinalIgnoreCase) ||
                        s.Equals("reasoning", StringComparison.OrdinalIgnoreCase))
                    {
                        levels.AddRange(new[] { "low", "medium", "high" });
                        break;
                    }
                }
            }

            if (levels.Count == 0 && m.TryGetProperty("reasoning_levels", out var rl) && rl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var v in rl.EnumerateArray())
                    if (v.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                        levels.Add(v.GetString()!.Trim());
            }
        }
        catch { }

        return (levels.Count > 0, levels);
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        SaveModelConfigFromUi();   // 把当前 UI 的模型配置保存到当前激活的配置套

        AppSettings.Persona = edtPersona.Text ?? "";
        if (picPersonality.SelectedIndex >= 0 && picPersonality.SelectedItem is string selPersonality)
            AppSettings.Personality = selPersonality;
        AppSettings.ForceThinking = swForceThink.IsToggled;
        AppSettings.AutoCompressThreshold = swAutoCompress.IsToggled
            ? (int)Math.Round(sldAutoCompress.Value)
            : 0;

        // 语音回复
        AppSettings.TtsAutoPlay = swTtsAutoPlay.IsToggled;

        // 主动关心（总开关在切换时即时生效；这里保存其余参数）
        if (TryParseHm(entCareQuietStart.Text, out var quietStart))
            AppSettings.CareQuietStartMin = quietStart;
        if (TryParseHm(entCareQuietEnd.Text, out var quietEnd))
            AppSettings.CareQuietEndMin = quietEnd;
        if (int.TryParse(entCareDailyCap.Text, out var cap) && cap >= 1)
            AppSettings.CareDailyCap = cap;
        AppSettings.CareSenseEnabled = swCareSense.IsToggled;

        // ReviewEachMessage 在切换时即时生效
        // AgentLoopEnabled / AgentTimeLimitLevel 在切换时即时生效，这里保存工作区目录
        AppSettings.WorkspacePath = entWorkspace.Text?.Trim() ?? "";
        UpdateWorkspaceHint();

        await DisplayAlert("已保存", "设置已生效", "确定");
    }

    /// <summary>解析 HH:mm 时间为当日分钟数。</summary>
    private static bool TryParseHm(string? s, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0].Trim(), out var h) || !int.TryParse(parts[1].Trim(), out var m)) return false;
        if (h < 0 || h > 23 || m < 0 || m > 59) return false;
        minutes = h * 60 + m;
        return true;
    }

    /// <summary>主动关心开关切换：即时生效；开启时申请通知权限并排期。</summary>
    private void OnCareToggled(object? sender, ToggledEventArgs e)
    {
        UpdateCareDesc(e.Value);
        if (e.Value == AppSettings.CareEnabled) return; // 初始化回显，不触发副作用
        AppSettings.CareEnabled = e.Value;
        if (e.Value)
            _ = OnCareEnabledAsync();
    }

    private async Task OnCareEnabledAsync()
    {
        try
        {
#if ANDROID
            var status = await Permissions.RequestAsync<Permissions.PostNotifications>();
            if (status != PermissionStatus.Granted)
                await DisplayAlert("需要通知权限",
                    "没有通知权限的话，Ta主动说话时你看不到提示（消息仍会保存在聊天里）。",
                    "知道了");
            var ctx = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
                      ?? Android.App.Application.Context;
            CareReceiver.EnsureScheduled(ctx);
#endif
        }
        catch { }
    }

    /// <summary>把当前 UI 中的 5 组模型配置保存到当前激活的配置套（供保存与 Tab 切换复用）。</summary>
    private void SaveModelConfigFromUi()
    {
        AppSettings.ApiKey = entApiKey.Text?.Trim() ?? "";
        AppSettings.ApiUrl = entApiUrl.Text ?? "";
        AppSettings.ThinkingModel = entThinkingModel.Text ?? "";
        if (picModel.SelectedIndex >= 0 && picModel.SelectedItem is string sel)
        {
            // 选中的是带 token 的显示文本，反解出原始模型 id
            string id = _modelOptions.TryGetValue(sel, out var rid) ? rid : sel;
            AppSettings.Model = id;
            if (_idToMax.TryGetValue(id, out var maxTok) && maxTok > 0)
                AppSettings.ModelMaxTokens = maxTok;

            // 写入该模型的推理档位能力（没拉过模型列表则保留原值）
            if (_reasoningByModel.TryGetValue(id, out var cap))
            {
                AppSettings.ReasoningSupported = cap.Supported;
                AppSettings.ReasoningOptionsJson = System.Text.Json.JsonSerializer.Serialize(cap.Levels);
                // 当前档位不在新模型的档位列表里 → 重置为关闭
                if (!cap.Levels.Contains(AppSettings.ReasoningChoice, StringComparer.OrdinalIgnoreCase))
                    AppSettings.ReasoningChoice = "";
            }
        }

        // 多模态模型配置
        AppSettings.ImgApiUrl = entImgApiUrl.Text ?? "";
        AppSettings.ImgApiKey = entImgApiKey.Text?.Trim() ?? "";
        AppSettings.ImgModel = picImgModel.SelectedItem as string ?? "";
        AppSettings.AudioApiUrl = entAudioApiUrl.Text ?? "";
        AppSettings.AudioApiKey = entAudioApiKey.Text?.Trim() ?? "";
        AppSettings.AudioModel = picAudioModel.SelectedItem as string ?? "";
        AppSettings.VisionApiUrl = entVisionApiUrl.Text ?? "";
        AppSettings.VisionApiKey = entVisionApiKey.Text?.Trim() ?? "";
        AppSettings.VisionModel = picVisionModel.SelectedItem as string ?? "";
        AppSettings.TtsApiUrl = entTtsApiUrl.Text ?? "";
        AppSettings.TtsApiKey = entTtsApiKey.Text?.Trim() ?? "";
        AppSettings.TtsModel = picTtsModel.SelectedItem as string ?? "";
        AppSettings.TtsVoice = entTtsVoice.Text ?? "";
    }

    /// <summary>切换配置套（1/2/3）：先把当前 UI 存回当前套，再切换激活套并重新加载 UI，随后防抖重检连通状态。</summary>
    private void SwitchConfigTab(int newIndex)
    {
        if (AppSettings.ActiveModelConfig == newIndex) return;
        SaveModelConfigFromUi();                       // 保存当前套
        AppSettings.ActiveModelConfig = newIndex;      // 切换激活套
        LoadModelConfigToUi();                         // 加载目标套到 UI
        UpdateTabHighlight(newIndex);
        ScheduleFullRecheck();                         // 切换后防抖刷新 5 个模型状态
    }

    /// <summary>把当前激活配置套的模型配置加载到 UI（不碰全局设置）。</summary>
    private void LoadModelConfigToUi()
    {
        entApiKey.Text = AppSettings.ApiKey;
        entApiUrl.Text = AppSettings.ApiUrl;
        entThinkingModel.Text = AppSettings.ThinkingModel;
        FillModelPicker(picModel, AppSettings.Model);
        entImgApiUrl.Text = AppSettings.ImgApiUrl;
        entImgApiKey.Text = AppSettings.ImgApiKey;
        FillModelPicker(picImgModel, AppSettings.ImgModel);
        entAudioApiUrl.Text = AppSettings.AudioApiUrl;
        entAudioApiKey.Text = AppSettings.AudioApiKey;
        FillModelPicker(picAudioModel, AppSettings.AudioModel);
        entVisionApiUrl.Text = AppSettings.VisionApiUrl;
        entVisionApiKey.Text = AppSettings.VisionApiKey;
        FillModelPicker(picVisionModel, AppSettings.VisionModel);
        entTtsApiUrl.Text = AppSettings.TtsApiUrl;
        entTtsApiKey.Text = AppSettings.TtsApiKey;
        FillModelPicker(picTtsModel, AppSettings.TtsModel);
        entTtsVoice.Text = AppSettings.TtsVoice;
    }

    /// <summary>Tab 高亮：当前配置紫色，其它灰色。</summary>
    private void UpdateTabHighlight(int active)
    {
        var tabs = new[] { tabConfig1, tabConfig2, tabConfig3 };
        var labels = new[] { lblTab1, lblTab2, lblTab3 };
        for (int i = 0; i < tabs.Length; i++)
        {
            bool on = i == active;
            tabs[i].BackgroundColor = on ? Color.FromArgb("#7B68EE") : Color.FromArgb("#3C3C3C");
            labels[i].TextColor = on ? Colors.White : Color.FromArgb("#AAAAAA");
            labels[i].FontAttributes = on ? FontAttributes.Bold : FontAttributes.None;
        }
    }

    private void OnTab1Clicked(object? sender, EventArgs e) => SwitchConfigTab(0);
    private void OnTab2Clicked(object? sender, EventArgs e) => SwitchConfigTab(1);
    private void OnTab3Clicked(object? sender, EventArgs e) => SwitchConfigTab(2);

    /// <summary>清空全部聊天记录文件并重置聊天页。</summary>
    private async void OnClearChatClicked(object? sender, EventArgs e)
    {
        if (!await DisplayAlert("确认", "确定清空全部聊天数据吗？此操作不可恢复。", "清空", "取消"))
            return;

        // 找到聊天页实例（无论当前导航栈在哪一层）
        Page? root = Application.Current?.Windows[0].Page;
        var nav = root as NavigationPage;
        var chat = nav?.RootPage as ChatPage ?? root as ChatPage;

        if (chat != null)
        {
            await ChatStore.Instance.ClearMessagesAsync();   // 清空数据库消息
            chat.Messages.Clear();                           // 清空界面消息
            chat.UpdateEmptyState();
        }

        // 顺带清空「每条消息 AI 必看」的复盘缓存（跟聊天一起归零）
        MessageReviewService.Clear();

        await DisplayAlert("已清空", "聊天数据已清空", "确定");
    }

    /// <summary>请求 Shizuku 授权。</summary>
    private async void OnRequestShizukuClicked(object? sender, EventArgs e)
    {
        if (!ShizukuHelper.IsOnline)
        {
            await DisplayAlert("Shizuku 未运行",
                "请先在手机上启动 Shizuku 服务，使本页状态变为 online 后再授权。", "知道了");
            return;
        }
        ShizukuHelper.RequestPermission();
        UpdateShizukuStatus();
        await DisplayAlert("已请求授权",
            "请在系统弹出的窗口中授予「青阳AI」使用 Shizuku 的权限。", "确定");
    }
}
