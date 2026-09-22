using Microsoft.Maui.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Maui.Controls.Xaml;
using Microsoft.Maui.Controls.Internals;
using Microsoft.Maui.Controls;

namespace 青阳AI;

public partial class ChatPage : ContentPage
{
    private static readonly HttpClient _httpClient = new();
    public ObservableCollection<ChatMsg> Messages { get; set; }
    private DateTime _pressTime;
    private ChatMsg? _pressedMsg;
    private const double LongPressMs = 500;
    private readonly SemaphoreSlim _compressLock = new(1, 1);
    private bool _followBottom = true; // 是否跟随到底部（用户手动上滑后停止跟随）
    private int _lastCacheHit;
    private int _lastCacheMiss;

    public ChatPage()
    {
InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);
        Messages = new ObservableCollection<ChatMsg>();
        BindingContext = this;
        msgList.Scrolled += OnMsgListScrolled;
        msgList.Loaded += OnMsgListLoaded;
        btnSend.Clicked += SendClick;
        imgAvatar.Source = AvatarService.Load();
        ApplyApiKey();
        _ = InitAsync();
    }

    /// <summary>是否已完成首次历史加载（冷启动时 OnAppearing 早于加载完成，用此标志防竞态）。</summary>
    private bool _historyLoaded;

    /// <summary>空状态语句（每次打开应用随机抽一句）。</summary>
    private static readonly string[] EmptyMottos =
        { "解码万象玄理", "奔赴智海遐方", "叩问数域洪流", "洞悉未知奥义" };
    private string _motto = "";

    /// <summary>更新空状态：无消息时屏幕正中央显示随机语句，有消息时隐藏。</summary>
    public void UpdateEmptyState()
    {
        bool empty = Messages.Count == 0;
        emptyState.IsVisible = empty;
        if (empty) emptyMotto.Text = _motto;
    }

    /// <summary>字号自适应：六个字至少占屏幕宽度的 3/4。</summary>
    #if ANDROID
    private void OnMsgListScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
        // 用户手动上滑后停止自动跟随底部
        if (e.LastVisibleItemIndex < Messages.Count - 2)
            _followBottom = false;
    }
#else
    private void OnMsgListScrolled(object? sender, EventArgs e)
    {
        // Windows 平台不需要滚动事件处理
    }
#endif

    /// <summary>消息长按开始：记录时间和目标消息。</summary>
    private void OnMsgPointerPressed(object? sender, PointerEventArgs e)
    {
        if (sender is BindableObject bo && bo.BindingContext is ChatMsg msg)
        {
            _pressTime = DateTime.Now;
            _pressedMsg = msg;
        }
    }

    /// <summary>消息长按结束：超过阈值则弹出操作菜单。</summary>
    private async void OnMsgPointerReleased(object? sender, PointerEventArgs e)
    {
        if (_pressedMsg == null) return;
        var elapsed = (DateTime.Now - _pressTime).TotalMilliseconds;
        var msg = _pressedMsg;
        _pressedMsg = null;

        if (elapsed < LongPressMs) return;

        // 长按：复制或删除
        var action = await DisplayActionSheet("消息操作", "取消", "删除", "复制文字");
        if (action == "复制文字" && !string.IsNullOrEmpty(msg.Content))
            await Clipboard.Default.SetTextAsync(msg.Content);
        else if (action == "删除")
        {
            Messages.Remove(msg);
            await ChatStore.Instance.DeleteMessagesAsync(new[] { msg.Id });
        }
    }

    /// <summary>隐藏软键盘（Android 专用）。</summary>
#if ANDROID
    private void HideKeyboard()
    {
        var window = Platform.CurrentActivity?.Window;
        if (window != null)
        {
            var imm = Platform.CurrentActivity?.GetSystemService("input_method") as Android.Views.InputMethods.InputMethodManager;
            var view = window.DecorView.RootView;
            imm?.HideSoftInputFromWindow(view.WindowToken, 0);
        }
    }
#else
    private void HideKeyboard() { }
#endif

    /// <summary>滚动到底部（如果 _followBottom=true）。</summary>
    private void ScrollToBottom()
    {
        if (_followBottom && Messages.Count > 0)
            msgList.ScrollTo(Messages.Count - 1, position: ScrollToPosition.End, animate: false);
    }

    /// <summary>CollectionView 首次布局完成后再定位底部；Loaded 后渲染已就绪，滚动一次即到位。</summary>
    private void OnMsgListLoaded(object? sender, EventArgs e)
    {
        if (Messages.Count == 0) return;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), () =>
        {
            msgList.ScrollTo(Messages.Count - 1, position: ScrollToPosition.End, animate: false);
        });
    }

    /// <summary>必要时跟随到底部（防消息发送时被用户手动上滑干扰）。</summary>
    private void FollowBottomIfNeeded()
    {
        if (_followBottom) ScrollToBottom();
    }

    /// <summary>首次加载（异步，不阻塞构造）。</summary>
    private async Task InitAsync()
    {
        // 列表立即显示（XAML 初始 IsVisible=False，这里第一时间打开，避免整页空白）
        msgList.IsVisible = true;
        try
        {
            await LoadHistoryAsync();
#if ANDROID
            _deviceContext = await DeviceContextService.BuildAsync();
            _memoryContext = await MemoryService.GetMemoryContextAsync();
            _diaryContext = await DiaryService.GetLatestDiaryContextAsync();
            _innerContext = InnerLifeService.BuildInnerContext();
            _observedContext = CareWatch.BuildObservedContext();
#endif
            _reviewContext = MessageReviewService.BuildReviewContext();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Init] {ex.Message}"); }
        _historyLoaded = true;
        UpdateEmptyState();
        // 兜底：列表显示后再异步滚一次（Loaded 事件也可能触发，重复也无害，无动画）
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), () =>
        {
            if (Messages.Count > 0)
                msgList.ScrollTo(Messages.Count - 1, position: ScrollToPosition.End, animate: false);
        });
        UpdateMoodUi();
        UpdateReasoningChip();
        ApplyThemeColor();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // 把页面里的隐藏 WebView 交给 AI 当浏览器用（顺带把 UA / 图片开关打进去）
        try { WebAgent.Attach(agentWeb); } catch { }
        try
        {
            // 无论 InitAsync 是否完成，列表都保持可见（首次由 InitAsync 负责加载历史）
            msgList.IsVisible = true;
            if (_historyLoaded)
            {
#if ANDROID
                _deviceContext = await DeviceContextService.BuildAsync();
                _observedContext = CareWatch.BuildObservedContext();
#endif
                await SyncNewFromDbAsync();
            }
            UpdateContextLabel();
            UpdateCacheHitLabel();
            UpdateMoodUi();
            UpdateReasoningChip();
            ApplyThemeColor();

            // 从系统设置回来后重新校验存储权限（用户可能刚授予、也可能收回了）
            await RevalidateStoragePermissionAsync();
            if (permPanel.IsVisible) RefreshPermPanel();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[OnAppearing] {ex.Message}"); }
    }

    /// <summary>
    /// 每次回到页面时校验系统存储权限，防止出现"APP 里显示已授权、系统里其实没有"的假放开。
    /// - 刚授予：把沙盒里的旧工作区文件搬到公共存储，避免"一授权文件就消失"；
    /// - 被收回：自动降级文件权限级别。
    /// </summary>
    private async Task RevalidateStoragePermissionAsync()
    {
        try
        {
            StorageAccess.InvalidateProbe();   // 刚回页面，重新探测一次真实写入能力
            bool granted = StorageAccess.IsAllFilesGranted();
            bool wasGranted = AppSettings.LastStorageGranted;

            if (granted && !wasGranted)
            {
                AppSettings.LastStorageGranted = true;
                var (ok, msg) = await StorageAccess.MigrateSandboxToPublicAsync();
                if (ok)
                    await DisplayAlert("工作区已迁移",
                        msg + "\n\n以后 Ta 生成的文件会直接放在这里，你在文件管理器里能直接看到。", "好");
            }
            else if (granted != wasGranted)
            {
                AppSettings.LastStorageGranted = granted;
            }

            if (granted) return;

            // —— 权限不存在（或已被收回）：降级，避免"假放开" ——
            bool changed = false;
            if (AppSettings.FileAccessLevel >= 3)
            {
                AppSettings.FileAccessLevel = 2;   // 3/4 都依赖系统权限，降为「仅修改工作区」
                changed = true;
            }
            if (AppSettings.FullAccess)
            {
                AppSettings.FullAccess = false;
                changed = true;
            }
            if (changed)
                System.Diagnostics.Debug.WriteLine("[Storage] 系统权限已失效，文件权限自动降级");
        }
        catch { }
    }

    /// <summary>增量同步数据库中的新消息（后台主动消息等）。</summary>
    private async Task SyncNewFromDbAsync()
    {
        try
        {
            var all = await ChatStore.Instance.LoadMessagesAsync();
            var existingIds = new HashSet<int>(Messages.Select(m => m.Id));
            foreach (var msg in all)
            {
                if (!existingIds.Contains(msg.Id))
                    Messages.Add(msg);
            }
            if (Messages.Count > 0) ScrollToBottom();
        }
        catch { }
    }

    /// <summary>加载历史消息（SQLite，异步）。</summary>
    private async Task LoadHistoryAsync()
    {
        var loaded = await ChatStore.Instance.LoadMessagesAsync();
        foreach (var msg in loaded) Messages.Add(msg);
        #if ANDROID
        // 初始化感知上下文（异步不阻塞）
        _ = Task.Run(async () =>
        {
            _deviceContext = await DeviceContextService.BuildAsync();
            _memoryContext = await MemoryService.GetMemoryContextAsync();
            _diaryContext = await DiaryService.GetLatestDiaryContextAsync();
            _innerContext = InnerLifeService.BuildInnerContext();
            _observedContext = CareWatch.BuildObservedContext();
            UpdateMoodUi();
            UpdateReasoningChip();
            ApplyThemeColor();
        });
#endif
    }

    /// <summary>刷新心情显示（直接读取 InnerLifeService.Mood）。</summary>
    private void UpdateMoodUi()
    {
        try
        {
            var mood = InnerLifeService.Mood;
            var emoji = InnerLifeService.MoodEmoji(mood);
            lblMood.Text = $" {mood}{emoji}";
        }
        catch { lblMood.Text = ""; }
    }

    /// <summary>刷新推理等级芯片：模型支持推理或开启强制思考时显示。</summary>
    private void UpdateReasoningChip()
    {
        try
        {
            bool show = AppSettings.ReasoningSupported || AppSettings.ForceThinking;
            reasonChip.IsVisible = show;
            if (show)
            {
                var options = AppSettings.GetReasoningOptions();
                var choice = AppSettings.ReasoningChoice;
                reasonLbl.Text = AppSettings.ForceThinking && options.Count == 0
                    ? "思考:开"
                    : (string.IsNullOrEmpty(choice) ? "思考:关" : "思考:" + choice);
            }
        }
        catch { reasonChip.IsVisible = false; }
    }

    /// <summary>点击推理芯片：弹出档位选择器。</summary>
    private async void OnReasoningChipTapped(object? sender, EventArgs e)
    {
        var options = AppSettings.GetReasoningOptions();
        if (options.Count == 0)
        {
            AppSettings.ForceThinking = !AppSettings.ForceThinking;
            UpdateReasoningChip();
            return;
        }
        var current = AppSettings.ReasoningChoice;
        var allOptions = new List<string> { "关", current };
        foreach (var o in options) if (!allOptions.Contains(o)) allOptions.Add(o);
        var selected = await DisplayActionSheet("选择推理等级", null, null, allOptions.ToArray());
        if (selected != null)
        {
            if (selected == "关") AppSettings.ReasoningChoice = "";
            else AppSettings.ReasoningChoice = selected;
            UpdateReasoningChip();
        }
    }

    /// <summary>刷新标题栏上下文用量显示：上下文:352K/1M·35.2%（模型返回的 prompt token 占用 / 上下文窗口 · 占比）。</summary>
    private void UpdateContextLabel()
    {
        try
        {
            var stats = UsageStats.Current;
            int used = stats.LastPromptTokens;          // 模型返回 usage 的 prompt 总数（本次占用）
            if (used <= 0)
            {
                // 模型未返回 usage（部分中转会摘掉）：用本地估算兜底，保证有数
                used = Messages.Sum(m => EstimateTokens(m.Content));
            }
            int cap = AppSettings.EffectiveMaxTokens;   // 模型上下文窗口（从 /models 的 context_length 抓取）
            if (cap <= 0) cap = AppSettings.ContextFallbackLimit;
            double pct = (double)used / cap * 100.0;
            lblCtx.Text = $"上下文:{FormatK(used)}/{FormatK(cap)}·{pct:F1}%";
        }
        catch { lblCtx.Text = ""; }
    }

    /// <summary>刷新缓存命中率显示：缓存命中:98%（模型返回 cached/prompt 的占比）。</summary>
    private void UpdateCacheHitLabel()
    {
        try
        {
            var stats = UsageStats.Current;
            double rate = stats.LastHitRate;
            lblCacheHit.Text = $"缓存命中:{rate:F0}%";
        }
        catch { lblCacheHit.Text = ""; }
    }

    /// <summary>数字转 K/M 人性化格式（352K / 1M）。</summary>
    private static string FormatK(int value)
    {
        if (value >= 1_000_000) return (value / 1_000_000.0).ToString("0.#") + "M";
        if (value >= 1_000) return (value / 1_000.0).ToString("0.#") + "K";
        return value.ToString();
    }

    private void ApplyApiKey()
    {
        _httpClient.DefaultRequestHeaders.Remove("Authorization");
        var key = AppSettings.ApiKey.Trim();
        if (!string.IsNullOrEmpty(key))
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {key}");
    }

    /// <summary>把当前主题色应用到聊天页的关键元素。</summary>
    private void ApplyThemeColor()
    {
        var theme = AppSettings.ThemeColor;
        var oldColor = Color.Parse("#FBB5B2");
        ApplyThemeToVisualTree(this, theme, oldColor);
    }

/// <summary>递归遍历可视树，把 oldColor 替换成 theme。聊天气泡与输入栏内部组件固定原色不跟主题。</summary>
    private static void ApplyThemeToVisualTree(VisualElement element, Color theme, Color oldColor)
    {
        if (element is Border iBar && iBar.StyleId == "输入栏跳过") return;
        if (element is Label lbl && lbl.TextColor == oldColor && lbl.StyleId != "btnSend") lbl.TextColor = theme;
        if (element is Button btn && btn.BackgroundColor == oldColor && btn.StyleId != "btnSend") btn.BackgroundColor = theme;
        if (element is Border bd && bd.BackgroundColor == oldColor && bd.StyleId != "chatBubble") bd.BackgroundColor = theme;
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

    private async void OnSettingsClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new SettingsPage());
        // 返回后刷新名字、Key、URL
        ApplyApiKey();
    }

    /// <summary>打开日记页。</summary>
    private async void OnDiaryClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new DiaryPage());
    }

    /// <summary>Entry 回车发送。</summary>
    private void OnInputCompleted(object? sender, EventArgs e)
    {
        SendClick(this, EventArgs.Empty);
    }

    /// <summary>点击输入栏：聚焦输入框。</summary>
    private void OnInputBarTapped(object? sender, EventArgs e)
    {
        txtInput.Focus();
    }

    /// <summary>点击语音按钮：语音识别后发送。</summary>
    private async void OnVoiceClicked(object? sender, EventArgs e)
    {
        try
        {
            var result = await RecognizeVoiceAsync();
            if (!string.IsNullOrWhiteSpace(result))
            {
                txtInput.Text = result;
                SendClick(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("语音识别", $"识别失败：{ex.Message}", "确定");
        }
    }

    private async void SendClick(object? sender, EventArgs e)
    {
        var text = txtInput.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        _followBottom = true; // 发送消息：恢复跟随底部
        _lastCacheHit = 0;
        _lastCacheMiss = 0;
        UpdateCacheHitLabel();
        var userMsg = new ChatMsg { Content = text, IsUser = true };
        Messages.Add(userMsg);
        UpdateEmptyState();
        txtInput.Text = "";
        HideKeyboard();
        btnSend.IsEnabled = false;
        ScrollToBottom();
        _ = ChatStore.Instance.SaveMessageAsync(userMsg);

        var aiMsg = new ChatMsg { Content = "", IsUser = false, IsWaiting = true };
        Messages.Add(aiMsg);
        ScrollToBottom();

        try
        {
            if (string.IsNullOrWhiteSpace(AppSettings.ApiUrl))
            {
                aiMsg.Content = "请先在「设置」中填写 API URL";
                return;
            }
            // 发送前刷新感知上下文，保证"此刻"信息准确（时间/前台应用/通知等）
            _deviceContext = await DeviceContextService.BuildAsync();
            // 刷新上一条消息的复盘上下文（如果开关开着且已生成）
            _reviewContext = MessageReviewService.BuildReviewContext();

            if (AppSettings.AgentLoopEnabled)
            {
                // Agent 循环执行模式：多轮自主推进，边跑边出过程，完成后折叠并重新总结
                await RunAgentLoopAsync(text, aiMsg);
                return;
            }

            try
            {
                await StreamRequest(text, aiMsg);
            }
            catch (Exception ex) when (IsTransientNetworkError(ex))
            {
                // 切后台被系统冻结、服务端掐线等导致的连接中断：自动重试一次
                aiMsg.Content = "";
                aiMsg.Thinking = "";
                aiMsg.ThinkingExpanded = false;
                await StreamRequest(text, aiMsg);
            }
        }
        catch (Exception ex)
        {
            aiMsg.Content = FriendlyNetworkError(ex);
        }
        finally
        {
            btnSend.IsEnabled = true;
            aiMsg.IsWaiting = false;
            _ = ChatStore.Instance.SaveMessageAsync(aiMsg);
            UpdateContextLabel();
            UpdateCacheHitLabel();
            // 记忆提取 + 内心反思（异步，不阻塞发送流程）
            MemoryService.OnExchangeCompleted(Messages);
            _ = InnerLifeService.ReflectAsync(Messages);
            // 每条消息 AI 必看（开关独立，异步不阻塞）
            _ = MessageReviewService.ReviewAsync(text, turnIndex: Messages.Count);
            // 自动压缩检查（异步，不阻塞发送流程）
            _ = CheckAndAutoCompressAsync();
        }
    }

    /// <summary>
    /// Agent 循环执行：把局面交给 AgentLoopService，
    /// 过程中把每一步的缩略行实时刷进气泡（{emoji 动作 +N -N}），
    /// 完成后过程区自动折叠，正文显示重新总结过的最终答复。
    /// </summary>
    private async Task RunAgentLoopAsync(string text, ChatMsg aiBubble)
    {
        var run = new AgentRun();
        aiBubble.AgentRun = run;
        aiBubble.IsWaiting = true;

        // 推理流实时显示：让用户看到 Ta 正在想什么（限流刷新，避免每来一个字就重排界面）
        var thinkingBuf = new StringBuilder();
        var lastFlush = DateTime.Now;
        void OnThinking(string delta)
        {
            thinkingBuf.Append(delta);
            // 每 300ms 刷一次，或缓冲区已攒够一截就刷
            if ((DateTime.Now - lastFlush).TotalMilliseconds < 300 && thinkingBuf.Length < 120) return;
            lastFlush = DateTime.Now;
            var tail = thinkingBuf.ToString();
            // 只保留末尾一小段（流光是对整串文字做逐帧 MeasureText，太长会拖慢渲染）
            if (tail.Length > 48) tail = "…" + tail[^48..];
            tail = tail.Replace('\n', ' ').Replace('\r', ' ').Trim();
            run.SetLiveThought(tail);
            aiBubble.NotifyAgentChanged();
        }
        AgentLoopService.ThinkingDelta += OnThinking;

        // 每完成一步刷新界面；同时保证滚动跟着走
        void OnStep(AgentRun r)
        {
            // 进入新一轮思考时先把上一轮的残留思考文字清掉，避免误导用户
            if (r.Steps.Count > 0 && r.Steps[^1].Kind == "think")
            {
                thinkingBuf.Clear();
                r.SetLiveThought("");
            }
            aiBubble.NotifyAgentChanged();
            msgList.ScrollTo(Messages.Count - 1, position: ScrollToPosition.End, animate: false);
        }
        AgentLoopService.StepChanged += OnStep;

        // 删除确认通道直接接到当前页面（AgentActionExecutor 不依赖 UI）
        AgentActionExecutor.DeleteConfirmer = async path =>
        {
            try { return await DisplayAlert("确认删除", $"Ta 想删除文件：\n{path}\n\n确定允许吗？", "删除", "拒绝"); }
            catch { return false; }
        };

        try
        {
            // 注意：历史里已经包含本轮用户消息（BuildHistoryMessages 会带上），
            // RunAsync 内部会做去重，这里不需要额外处理。
            var history = BuildHistoryMessages(aiBubble);
            var extra = new StringBuilder();
            if (!string.IsNullOrEmpty(_deviceContext)) extra.AppendLine("【你此刻能看到的用户状态】\n" + _deviceContext);
            if (!string.IsNullOrEmpty(_innerContext)) extra.AppendLine(_innerContext);
            if (!string.IsNullOrEmpty(_observedContext)) extra.AppendLine(_observedContext);
            if (!string.IsNullOrEmpty(_reviewContext)) extra.AppendLine(_reviewContext);
            if (!string.IsNullOrEmpty(_memoryContext)) extra.AppendLine(_memoryContext);
            if (!string.IsNullOrEmpty(_diaryContext)) extra.AppendLine(_diaryContext);

            var loopResult = await AgentLoopService.RunAsync(text, history, extra.ToString());

            // 时限到 / 出错：给个交代而不是静默
            if (!string.IsNullOrEmpty(loopResult.Error) && loopResult.Error != "__cancelled__")
                aiBubble.Content = FriendlyNetworkError(new Exception(loopResult.Error));
            else
            {
                var head = loopResult.TimedOut
                    ? $"（已到 {AppSettings.AgentTimeLimitText} 时限，先做到这里）\n\n"
                    : "";
                aiBubble.Content = head + loopResult.FinalText;
            }

            // 任务收尾：把整轮回环的行数总账附在最后
            if (run.TotalAdded > 0 || run.TotalRemoved > 0)
                aiBubble.Content += "\n\n（" + run.DeltaSummary + "）";
        }
        catch (Exception ex)
        {
            aiBubble.Content = FriendlyNetworkError(ex);
        }
        finally
        {
            AgentLoopService.StepChanged -= OnStep;
            AgentLoopService.ThinkingDelta -= OnThinking;
            AgentActionExecutor.DeleteConfirmer = null;
            run.Finish();
            aiBubble.NotifyAgentChanged();
            aiBubble.IsWaiting = false;
            msgList.ScrollTo(Messages.Count - 1, position: ScrollToPosition.End, animate: false);
        }
    }

    /// <summary>缓存的长期记忆上下文（每次进入页面/记忆更新后刷新）。</summary>
    private string _memoryContext = "";

    /// <summary>缓存的最近日记摘要上下文。</summary>
    private string _diaryContext = "";

    /// <summary>缓存的设备/生活感知上下文（每次发送前刷新，保证"此刻"准确）。</summary>
    private string _deviceContext = "";

    /// <summary>缓存的内心状态上下文（心情/心里话，用户看不到）。</summary>
    private string _innerContext = "";

    /// <summary>缓存的"她默默观察到的用户近况"（后台静默了解，用户看不到）。</summary>
    private string _observedContext = "";

    /// <summary>缓存的"每条消息 AI 必看的复盘上下文"（用户看不到，仅下一次对话用）。</summary>
    private string _reviewContext = "";

    /// <summary>
    /// 构建完整请求消息列表：system(底层+用户人设+感知+内心+记忆+日记+默默观察) + 全部聊天历史。
    /// 跳过正在生成的 aiBubble 与空内容消息。
    /// </summary>
    private List<object> BuildHistoryMessages(ChatMsg skipBubble)
    {
        var systemPrompt = AppSettings.BuildSystemPrompt();
        if (!string.IsNullOrEmpty(_deviceContext))
            systemPrompt += "\n\n【你此刻能看到的用户状态】\n" + _deviceContext;
        if (!string.IsNullOrEmpty(_innerContext))
            systemPrompt += "\n\n" + _innerContext;
        if (!string.IsNullOrEmpty(_observedContext))
            systemPrompt += "\n\n" + _observedContext;
        if (!string.IsNullOrEmpty(_reviewContext))
            systemPrompt += "\n\n" + _reviewContext;
        if (!string.IsNullOrEmpty(_memoryContext))
            systemPrompt += "\n\n" + _memoryContext;
        if (!string.IsNullOrEmpty(_diaryContext))
            systemPrompt += "\n\n" + _diaryContext;

        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };
        foreach (var m in Messages)
        {
            if (ReferenceEquals(m, skipBubble)) continue;
            if (string.IsNullOrWhiteSpace(m.Content) && string.IsNullOrWhiteSpace(m.TerminalExecLog)
                && string.IsNullOrWhiteSpace(m.VisionResult)) continue;

            if (m.IsUser)
            {
                // 用户消息：正文 + 图片识别文字（如有，图片内容进入历史供后续对话引用）
                var uc = m.Content ?? "";
                if (!string.IsNullOrWhiteSpace(m.VisionResult))
                    uc += "\n\n（图片识别结果）\n" + m.VisionResult;
                messages.Add(new { role = "user", content = uc });
            }
            else
            {
                // AI 消息：正文 + 若有终端执行记录则附带（命令与输出结果），帮助模型保持上下文
                var c = m.Content ?? "";
                if (!string.IsNullOrWhiteSpace(m.TerminalExecLog))
                    c += "\n\n（此前终端执行记录）\n" + m.TerminalExecLog;
                messages.Add(new { role = "assistant", content = c });
            }
        }
        return messages;
    }

    /// <summary>友好的网络错误提示。</summary>
    private string FriendlyNetworkError(Exception ex)
    {
        if (ex is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
        {
            var code = (int)httpEx.StatusCode.Value;
            if (code == 401) return "API Key 无效，请检查设置";
            if (code == 429) return "请求太频繁，请稍后再试";
            if (code >= 500) return "服务器错误，请稍后再试";
        }
        return "网络连接失败：" + ex.Message;
    }

    /// <summary>是否为可重试的网络错误（连接中断/超时）。</summary>
    private bool IsTransientNetworkError(Exception ex)
    {
        return ex is HttpRequestException && ex.InnerException is TimeoutException
            || ex is HttpRequestException && ex.InnerException is System.Net.Sockets.SocketException
            || ex is TaskCanceledException;
    }

    /// <summary>流式请求：发送用户输入 → 流式接收 AI 回复。</summary>
    private async Task StreamRequest(string text, ChatMsg aiBubble)
    {
        string model = AppSettings.ResolveChatModel(AppSettings.ForceThinking);

        var messages = BuildHistoryMessages(aiBubble);
        var reqBody = BuildChatBody(model, messages, stream: true);
        var json = JsonSerializer.Serialize(reqBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _httpClient.PostAsync(AppSettings.ApiUrl, content);
        resp.EnsureSuccessStatusCode();

        // 流式处理：实时显示，同时检测指令（{cmd:"..."} / {api:"..."} / {img:"..."} / {browse:"..."}）
        var sseResult = await ConsumeSseAsync(resp, aiBubble);
        if (sseResult.CancelledForCommand)
        {
            // 检测到 Shizuku 指令：拉取终端命令列表并执行
            await RunCommandPipeline(text, aiBubble);
        }
        else if (sseResult.CancelledForApi)
        {
            // 检测到 API 指令：调用感知 API 并回传结果
            await RunApiPipeline(text, aiBubble.Content, aiBubble);
        }
        else if (sseResult.CancelledForImage)
        {
            // 检测到文生图指令：调用图片生成 API
            await GenerateImagePipelineAsync(aiBubble.Content, aiBubble);
        }
        else if (sseResult.CancelledForBrowse)
        {
            // 检测到上网指令：调用浏览器插件
            var url = ExtractBrowseCommand(aiBubble.Content);
            if (!string.IsNullOrEmpty(url))
            {
                var browseResult = await BrowserTool.FetchAsync(url);
                // 把网页内容作为额外上下文继续对话
                var continueMessages = new List<object>
                {
                    new { role = "system", content = AppSettings.BuildSystemPrompt() },
                    new { role = "user", content = text },
                    new { role = "assistant", content = aiBubble.Content },
                    new { role = "user", content = $"请根据以下网页内容回答：\n\n{browseResult}" }
                };
                var continueReq = BuildChatBody(model, continueMessages, stream: true);
                var continueJson = JsonSerializer.Serialize(continueReq);
                var continueContent = new StringContent(continueJson, Encoding.UTF8, "application/json");
                var continueResp = await _httpClient.PostAsync(AppSettings.ApiUrl, continueContent);
                continueResp.EnsureSuccessStatusCode();
                await ConsumeSseAsync(continueResp, aiBubble);
            }
        }
        else if (sseResult.CancelledForDownload)
        {
            // 检测到下载指令：真的把文件下到工作区
            await RunDownloadPipelineAsync(text, aiBubble);
        }
        else if (sseResult.CancelledForWeb)
        {
            // 检测到真浏览器指令：交给隐藏 WebView 执行（跑 JS / 点击 / 填表）
            await RunWebPipelineAsync(text, aiBubble);
        }
        // 管线都跑完了，指令已经没用了：从气泡里整段抹掉，别让用户看到 {download:"…"} 这种东西
        aiBubble.Content = InstructionParser.RemoveInstructions(aiBubble.Content);
        _ = ChatStore.Instance.SaveMessageAsync(aiBubble);
        await SpeakIfEnabledAsync(aiBubble.Content);
    }

    /// <summary>提取下载指令（{download:"..."}）。</summary>
    private string ExtractDownloadCommand(string text)
    {
        var start = text.IndexOf("{download:\"");
        if (start < 0) return "";
        var end = text.IndexOf("\"}", start + 11);
        if (end < start) return "";
        return text.Substring(start + 11, end - start - 11).Trim();
    }

    /// <summary>提取真浏览器指令（{web:"动作 参数"}）。</summary>
    private string ExtractWebCommand(string text)
    {
        var start = text.IndexOf("{web:\"");
        if (start < 0) return "";
        var end = text.IndexOf("\"}", start + 6);
        if (end < start) return "";
        return text.Substring(start + 6, end - start - 6).Trim();
    }

    /// <summary>
    /// 真浏览器管线：{web:"动作 参数"} → 藏在聊天页里的隐藏 WebView 执行 → 结果回传。
    /// 这是 HttpClient 做不到的那部分：JS 渲染的页面、点按钮、翻页、填表单、登录。
    /// </summary>
    private async Task RunWebPipelineAsync(string userText, ChatMsg aiBubble)
    {
        var cmd = ExtractWebCommand(aiBubble.Content);
        if (string.IsNullOrEmpty(cmd))
        {
            aiBubble.Content = "（没拿到有效的浏览器指令。）";
            return;
        }

        var result = await WebAgent.RunAsync(cmd);

        var hint = "【浏览器执行结果】\n" + result + "\n\n" +
                   "请用自然的话把结果告诉用户（像你自己看到的一样），不要说'根据网页'。" +
                   "如果结果是空页面或提示还在加载，可以再试一次 {web:\"text\"}，或换个思路。" +
                   "不要再输出 {web:...}。";

        var continueMessages = new List<object>
        {
            new { role = "system", content = AppSettings.BuildSystemPrompt() },
            new { role = "user", content = userText },
            new { role = "assistant", content = InstructionParser.RemoveInstructions(aiBubble.Content) },
            new { role = "user", content = hint }
        };
        try
        {
            var body = BuildChatBody(AppSettings.ResolveChatModel(AppSettings.ForceThinking), continueMessages, stream: true);
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var resp = await _httpClient.PostAsync(AppSettings.ApiUrl, content);
            resp.EnsureSuccessStatusCode();
            await ConsumeSseAsync(resp, aiBubble);
        }
        catch
        {
            aiBubble.Content = result;   // 模型这轮挂了，至少把浏览器结果摆出来
        }
    }

    /// <summary>
    /// 下载管线：{download:"url"} → 真的把文件下到工作区 → 结果回传给模型继续说话。
    ///
    /// 为什么要有这条：Ta 原先只有 {browse:} 一种联网能力，而 browse 只把网页正文读成文字，
    /// 遇到 apk / zip / 图片这类二进制它什么也拿不到 —— 于是只能把网址念给用户听。
    /// 这条管线把"手"接上。
    /// </summary>
    private async Task RunDownloadPipelineAsync(string userText, ChatMsg aiBubble)
    {
        if (!AppSettings.BrowserPermission)
        {
            aiBubble.Content = "（联网能力已关闭，没法下载。需要的话去设置页打开「允许 AI 联网」。）";
            return;
        }

        var url = ExtractDownloadCommand(aiBubble.Content);
        if (string.IsNullOrEmpty(url))
        {
            aiBubble.Content = "（没拿到有效的下载地址。）";
            return;
        }

        var (ok, msg, _) = await BrowserTool.DownloadAsync(url);

        var hint = ok
            ? $"【下载结果】{msg}\n\n请用一两句话告诉用户：文件已经下好了、放在哪个路径、下一步怎么打开或安装（apk 的话提示他去文件管理器点一下安装）。不要再输出 {{download:...}}。"
            : $"【下载失败】{msg}\n\n请用一两句话如实告诉用户失败了，猜一下原因（网络不通 / 地址失效 / 没有存储权限 / 文件太大），并给个替代办法。不要再输出 {{download:...}}。";

        var continueMessages = new List<object>
        {
            new { role = "system", content = AppSettings.BuildSystemPrompt() },
            new { role = "user", content = userText },
            new { role = "assistant", content = InstructionParser.RemoveInstructions(aiBubble.Content) },
            new { role = "user", content = hint }
        };
        try
        {
            var body = BuildChatBody(AppSettings.ResolveChatModel(AppSettings.ForceThinking), continueMessages, stream: true);
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var resp = await _httpClient.PostAsync(AppSettings.ApiUrl, content);
            resp.EnsureSuccessStatusCode();
            await ConsumeSseAsync(resp, aiBubble);
        }
        catch (Exception ex)
        {
            aiBubble.Content = ok ? "文件下好了：" + msg : "下载失败：" + msg;
            _ = ex;
        }
    }

    /// <summary>构建请求体（支持推理开关）。流式请求带 stream_options 让 DeepSeek 等返回 usage，供上下文/缓存统计。</summary>
    private object BuildChatBody(string model, List<object> messages, bool stream)
    {
        var body = new
        {
            model,
            messages,
            stream,
            max_tokens = 4000,
            reasoning_effort = AppSettings.ForceThinking ? "high" : "none",
            stream_options = stream ? new { include_usage = true } : null
        };
        return body;
    }

    /// <summary>消费 SSE 流，返回解析结果。</summary>
    private async Task<SseConsumeResult> ConsumeSseAsync(HttpResponseMessage resp, ChatMsg aiBubble)
    {
        var result = new SseConsumeResult();
        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line.Substring(6);
            if (data == "[DONE]") break;
            try
            {
                var chunk = JsonSerializer.Deserialize<StreamChunk>(data);
                var delta = chunk?.choices?[0]?.delta;
                if (delta != null)
                {
                    // 推理内容（独立字段）
                    if (!string.IsNullOrEmpty(delta.reasoning_content))
                    {
                        // 首个思考字：开始计时；此后每个思考字都在刷新"思考结束时刻"
                        if (!result.ThinkingStart.HasValue) result.ThinkingStart = DateTime.Now;
                        result.ThinkingEnd = DateTime.Now;
                        aiBubble.Thinking += delta.reasoning_content;
                        aiBubble.IsThinkingActive = true;
                        result.ThinkingActive = true;
                        // 实时刷新用时（只算思考过程，正文不计入）
                        aiBubble.ThinkingSeconds = (result.ThinkingEnd.Value - result.ThinkingStart.Value).TotalSeconds;
                    }
                    // 普通内容
                    if (!string.IsNullOrEmpty(delta.content))
                    {
                        aiBubble.Content += delta.content;
                        result.ContentStarted = true;
                        // 正文开始 = 思考过程结束：冻结用时（此后不再累加）
                        aiBubble.IsThinkingActive = false;
                        result.ThinkingFrozen = true;
                        // 检测指令：{cmd:} / {api:} / {img:} / {browse:} / {download:} / {web:}
                        if (delta.content.Contains("{cmd:") || delta.content.Contains("{api:") || 
                            delta.content.Contains("{img:") || delta.content.Contains("{browse:") ||
                            delta.content.Contains("{download:") || delta.content.Contains("{web:"))
                        {
                            // 提前断流，交给后续管线处理
                            if (delta.content.Contains("{cmd:")) result.CancelledForCommand = true;
                            if (delta.content.Contains("{api:")) result.CancelledForApi = true;
                            if (delta.content.Contains("{img:")) result.CancelledForImage = true;
                            if (delta.content.Contains("{browse:")) result.CancelledForBrowse = true;
                            if (delta.content.Contains("{download:")) result.CancelledForDownload = true;
                            if (delta.content.Contains("{web:")) result.CancelledForWeb = true;
                            break;
                        }
                    }
                }
                // 处理 usage 统计（缓存命中率 + 上下文用量）
                if (chunk?.usage != null && chunk.usage.Value.ValueKind == JsonValueKind.Object)
                {
                    try
                    {
                        var tu = TokenUsageMapper.FromElement(chunk.usage);
                        if (tu != null)
                        {
                            UsageStats.Current.Apply(tu);
                            _lastCacheHit = tu.CachedTokens;
                            _lastCacheMiss = tu.PromptTokens - tu.CachedTokens;
                            UpdateCacheHitLabel();
                            UpdateContextLabel();
                        }
                    }
                    catch { }
                }
            }
            catch { }
            FollowBottomIfNeeded();
        }
        result.ReadDone = true;
        aiBubble.IsThinkingActive = false;
        // 用时只算"首个思考字 → 最后一个思考字"；若思考后直接结束（无正文）也在此冻结
        if (result.ThinkingStart.HasValue && result.ThinkingEnd.HasValue)
            aiBubble.ThinkingSeconds = (result.ThinkingEnd.Value - result.ThinkingStart.Value).TotalSeconds;
        return result;
    }

    /// <summary>提取 Shizuku 指令（{cmd:"..."}）。</summary>
    private List<string> ExtractCommands(string text)
    {
        var cmds = new List<string>();
        var start = text.IndexOf("{cmd:\"");
        while (start >= 0)
        {
            var end = text.IndexOf("\"}", start + 6);
            if (end < start) break;
            var cmd = text.Substring(start + 6, end - start - 6).Trim();
            if (!string.IsNullOrEmpty(cmd)) cmds.Add(cmd);
            start = text.IndexOf("{cmd:\"", end + 2);
        }
        return cmds;
    }

    /// <summary>提取 API 指令（{api:"..."}）。</summary>
    private string ExtractApiCommand(string text)
    {
        var start = text.IndexOf("{api:\"");
        if (start < 0) return "";
        var end = text.IndexOf("\"}", start + 6);
        if (end < start) return "";
        return text.Substring(start + 6, end - start - 6).Trim();
    }

    /// <summary>提取文生图指令（{img:"..."}）。</summary>
    private List<string> ExtractImageCommands(string text)
    {
        var descs = new List<string>();
        var start = text.IndexOf("{img:\"");
        while (start >= 0)
        {
            var end = text.IndexOf("\"}", start + 6);
            if (end < start) break;
            var desc = text.Substring(start + 6, end - start - 6).Trim();
            if (!string.IsNullOrEmpty(desc)) descs.Add(desc);
            start = text.IndexOf("{img:\"", end + 2);
        }
        return descs;
    }

    /// <summary>提取上网指令（{browse:"..."}）。</summary>
    private string ExtractBrowseCommand(string text)
    {
        var start = text.IndexOf("{browse:\"");
        if (start < 0) return "";
        var end = text.IndexOf("\"}", start + 9);
        if (end < start) return "";
        return text.Substring(start + 9, end - start - 9).Trim();
    }

    /// <summary>
    /// Shizuku 终端命令管线：检测到 {cmd:"..."} → 解析命令列表 → 逐条执行 → 三行气泡显示执行过程
    /// → 把结果回传给模型生成最终答案。
    /// </summary>
    private async Task RunCommandPipeline(string prompt, ChatMsg aiBubble)
    {
        // 1) 初始化终端状态
        aiBubble.HasTerminalHeader = true;
        aiBubble.TerminalStep = "与Shizuku通信";
        aiBubble.TerminalOutput = "";
        aiBubble.TerminalTitle = "终端执行";
        ScrollToBottom();
        FollowBottomIfNeeded();

        // 2) 检查 Shizuku 权限
#if ANDROID
        if (!ShizukuRunner.Available())
        {
            aiBubble.TerminalOutput = "Shizuku 未授权";
            return;
        }

        var cmds = ExtractCommands(aiBubble.Content);
        if (cmds.Count == 0)
        {
            aiBubble.TerminalStep = "完成";
            aiBubble.TerminalOutput = "未找到有效命令";
            return;
        }

        // 3) 逐条执行命令
        var outputs = new List<(string cmd, string result)>();
        foreach (var cmd in cmds)
        {
            aiBubble.TerminalStep = "拉取终端";
            aiBubble.TerminalOutput = "";
            ScrollToBottom();
            FollowBottomIfNeeded();

            var result = await ShizukuRunner.ExecuteAsync(cmd);
            var combined = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(result.Stdout)) combined.Append(result.Stdout);
            if (!string.IsNullOrWhiteSpace(result.Stderr)) combined.AppendLine("[stderr] " + result.Stderr);
            if (result.ExitCode != 0) combined.Append($"(exit {result.ExitCode})");
            outputs.Add((cmd, combined.ToString()));

            // 先把结果写入第三行，但保持"运行终端"状态一段时间，
            // 让浅绿扫光动画可被看到（验收），再切到"完成"。
            aiBubble.TerminalOutput = NormalizeSingleLine(combined.ToString());
            aiBubble.TerminalStep = "运行终端";
            FollowBottomIfNeeded();
            await Task.Delay(1500);
            aiBubble.TerminalStep = "完成";
            FollowBottomIfNeeded();
        }

        if (outputs.Count == 0)
        {
            aiBubble.TerminalStep = "完成";
            if (string.IsNullOrEmpty(aiBubble.TerminalOutput)) aiBubble.TerminalOutput = "已取消或无授权";
            return;
        }

        // 有输出则状态保持完成
        aiBubble.TerminalStep = "完成";

        // 保存终端执行记录（命令+输出），供后续作为上下文发送给模型
        var execLog = new StringBuilder();
        foreach (var o in outputs)
            execLog.AppendLine("$ " + o.cmd + "\n" + o.result + "\n");
        aiBubble.TerminalExecLog = execLog.ToString();

        // 第二轮：把命令执行结果回传给模型，生成结合用户问题的最终答案
        var toolResults = new StringBuilder("[Shizuku 终端命令]\n");
        foreach (var o in outputs)
            toolResults.AppendLine($"$ {o.cmd}\n{o.result}");
        await StreamSummaryReply(prompt, toolResults.ToString(), aiBubble);
#endif
    }

    /// <summary>
    /// 感知 API 调用流水线（{api:"名"}）：三行气泡实时显示正在调用哪个 API
    /// （如 调用「屏幕使用时间」API），结果回传给模型生成最终回答，
    /// 并存入 TerminalExecLog 作为后续上下文。
    /// </summary>
    private async Task RunApiPipeline(string prompt, string rawText, ChatMsg aiBubble)
    {
        var apiCmd = ExtractApiCommand(rawText);
        if (string.IsNullOrEmpty(apiCmd)) return;

        // 1) 初始化终端状态
        aiBubble.HasTerminalHeader = true;
        aiBubble.TerminalStep = "调用API";
        aiBubble.TerminalOutput = "";
        aiBubble.TerminalTitle = $"调用「{apiCmd}」API";
        ScrollToBottom();
        FollowBottomIfNeeded();

        try
        {
            // 2) 调用感知 API（DeviceContextService 统一入口）
            var apiResult = await ApiRegistry.ExecuteAsync(apiCmd);
            if (string.IsNullOrEmpty(apiResult))
            {
                aiBubble.TerminalOutput = "API调用失败：无返回结果";
                return;
            }

            // 3) 显示结果
            aiBubble.TerminalStep = "完成";
            aiBubble.TerminalOutput = apiResult;
            aiBubble.TerminalExecLog = $"[{apiCmd} API]\n{apiResult}";
            ScrollToBottom();
            FollowBottomIfNeeded();

            // 4) 把 API 结果回传给模型，生成最终回答
            await StreamSummaryReply(prompt, $"[{apiCmd} API]\n{apiResult}", aiBubble);
        }
        catch (Exception ex)
        {
            aiBubble.TerminalStep = "完成";
            aiBubble.TerminalOutput = $"API调用失败：{ex.Message}";
            aiBubble.TerminalExecLog = $"[{apiCmd} API]\n错误：{ex.Message}";
        }
    }

    /// <summary>流式总结回复：把工具结果（终端执行/API调用/图片识别）交给模型生成最终答案。</summary>
    private async Task StreamSummaryReply(string originalPrompt, string toolResults, ChatMsg aiBubble)
    {
        string model = AppSettings.ResolveChatModel(AppSettings.ForceThinking);

        var summarySystem = AppSettings.BuildSystemPrompt();
        if (!string.IsNullOrEmpty(_memoryContext))
            summarySystem += "\n\n" + _memoryContext;

        var messages = new List<object>
        {
            new { role = "system", content = summarySystem },
            new { role = "user", content = originalPrompt },
            new { role = "assistant", content = toolResults },
            new { role = "user", content = "请根据上面的工具执行结果回答用户的问题。" }
        };

        var reqBody = BuildChatBody(model, messages, stream: true);
        var json = JsonSerializer.Serialize(reqBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _httpClient.PostAsync(AppSettings.ApiUrl, content);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line.Substring(6);
            if (data == "[DONE]") break;
            try
            {
                var chunk = JsonSerializer.Deserialize<StreamChunk>(data);
                var delta = chunk?.choices?[0]?.delta?.content;
                if (string.IsNullOrEmpty(delta)) continue;
                aiBubble.Content += delta;
                FollowBottomIfNeeded();
            }
            catch { }
        }
        _ = ChatStore.Instance.SaveMessageAsync(aiBubble);
        await SpeakIfEnabledAsync(aiBubble.Content);
    }

    /// <summary>单行化终端输出（去除换行，避免气泡溢出）。</summary>
    private string NormalizeSingleLine(string text)
    {
        return text.Replace("\n", " ").Replace("\r", " ").Trim();
    }

    /// <summary>自动压缩：当上下文占用超过阈值（百分比）时，询问用户是否压缩早期消息。</summary>
    private async Task CheckAndAutoCompressAsync()
    {
        int threshold = AppSettings.AutoCompressThreshold;
        if (threshold <= 0) return;   // 0 表示禁用

        var allTokens = Messages.Sum(m => EstimateTokens(m.Content));
        int cap = AppSettings.EffectiveMaxTokens;   // 模型上下文窗口
        if (cap <= 0) cap = AppSettings.ContextFallbackLimit;
        double pct = (double)allTokens / cap * 100.0;
        if (pct < threshold) return;  // 占用未达阈值百分比，不弹窗

        var alert = await DisplayAlert("上下文过长", $"当前对话已用 {allTokens} tokens（占模型上下文 {pct:F0}%），是否压缩早期消息？", "压缩", "取消");
        if (!alert) return;

        await CompressEarlyMessagesAsync();
    }

    /// <summary>压缩早期消息（保留最近 10 条，早期消息只保留摘要）。</summary>
    private async Task CompressEarlyMessagesAsync()
    {
        await _compressLock.WaitAsync();
        try
        {
            if (Messages.Count <= 10) return;

            var compressed = new List<ChatMsg>();
            // 保留最近 10 条
            for (int i = Messages.Count - 10; i < Messages.Count; i++)
                compressed.Add(Messages[i]);

            // 压缩早期的消息（只保留关键信息）
            var earlySummary = new StringBuilder("早期对话摘要：\n");
            for (int i = 0; i < Messages.Count - 10; i++)
            {
                var msg = Messages[i];
                if (msg.IsUser)
                    earlySummary.AppendLine($"用户: {msg.Content}");
                else
                    earlySummary.AppendLine($"AI: {msg.Content}");
            }

            // 清空并重新添加
            Messages.Clear();
            foreach (var msg in compressed)
                Messages.Add(msg);

            // 添加压缩标记
            var compressMsg = new ChatMsg
            {
                Content = earlySummary.ToString(),
                IsUser = false,
                IsCompressed = true
            };
            Messages.Insert(0, compressMsg);
            _ = ChatStore.Instance.SaveMessageAsync(compressMsg);

            ScrollToBottom();
        }
        finally
        {
            _compressLock.Release();
        }
    }

    /// <summary>估算消息的 token 数（粗略估算）。</summary>
    private int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int chars = text.Length;
        // 中文 1 字≈1.1 token，其它 4 字符≈1 token
        double tokens = 0;
        tokens += chars * (1.0 / 3.0) * 1.1;       // 中文字符占比 1/3
        tokens += chars * (2.0 / 3.0) * 0.25;      // 其余字符占比 2/3
        return (int)Math.Round(tokens) + 32;       // 基础头信息附加
    }

    /// <summary>语音识别：优先 Android 系统 SpeechRecognizer，失败回退听觉模型 API。</summary>
    private async Task<string?> RecognizeVoiceAsync()
    {
#if ANDROID
        // 1) 系统识别
        try
        {
            var tcs = new TaskCompletionSource<string?>();
            var recognizer = Android.Speech.SpeechRecognizer.CreateSpeechRecognizer(
                Microsoft.Maui.ApplicationModel.Platform.AppContext);
            if (recognizer == null) throw new Exception("系统不支持语音识别");
            recognizer.SetRecognitionListener(new SystemRecognizerListener(tcs));
            recognizer.StartListening(new Android.Content.Intent(Android.Speech.RecognizerIntent.ActionRecognizeSpeech)
                .PutExtra(Android.Speech.RecognizerIntent.ExtraLanguage, "zh-CN"));
            var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            recognizer.Destroy();
            if (!string.IsNullOrWhiteSpace(result)) return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Voice] 系统识别失败: {ex.Message}");
        }
#endif

        // 2) 听觉模型 API 回退：录音（弹窗期间录音，最长 15 秒）→ multipart 上传转写
#if ANDROID
        if (AppSettings.AudioEnabled)
            return await RecordAndTranscribeAsync();
#endif

        throw new Exception("语音识别不可用：未配置听觉模型，且系统识别失败。");
    }

#if ANDROID
    /// <summary>录音并上传听觉模型转写。弹窗期间录音，点「完成」提前结束，最长 15 秒。</summary>
    private async Task<string?> RecordAndTranscribeAsync()
    {
        var status = await Permissions.RequestAsync<Permissions.Microphone>();
        if (status != PermissionStatus.Granted)
            throw new Exception("没有麦克风权限，无法录音");

        var path = Path.Combine(FileSystem.CacheDirectory ?? ".", $"voice_{Guid.NewGuid():N}.m4a");
        var recorder = new Android.Media.MediaRecorder();
        bool started = false;
        try
        {
            recorder.SetAudioSource(Android.Media.AudioSource.Mic);
            recorder.SetOutputFormat(Android.Media.OutputFormat.Mpeg4);
            recorder.SetAudioEncoder(Android.Media.AudioEncoder.Aac);
            recorder.SetAudioEncodingBitRate(96000);
            recorder.SetAudioSamplingRate(44100);
            recorder.SetOutputFile(path);
            recorder.Prepare();
            recorder.Start();
            started = true;
        }
        catch (Exception ex)
        {
            recorder.Release();
            throw new Exception("录音启动失败：" + ex.Message);
        }

        try
        {
            // 录音提示：说完点「完成」提前结束；最长 15 秒自动结束
            var alert = DisplayAlert("正在听你说…", "说完后点击「完成」结束录音。", "完成");
            await Task.WhenAny(alert, Task.Delay(15000));
        }
        finally
        {
            if (started) { try { recorder.Stop(); } catch { } }
            recorder.Release();
        }

        var file = new FileInfo(path);
        if (!file.Exists || file.Length < 1024)
            throw new Exception("没有录到声音");

        return await TranscribeWithAudioModelAsync(path);
    }

    /// <summary>调用听觉模型 /audio/transcriptions 转写音频文件（OpenAI 兼容 multipart）。</summary>
    private async Task<string?> TranscribeWithAudioModelAsync(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.AudioApiUrl);
        req.Headers.Add("Authorization", $"Bearer {AppSettings.AudioApiKey}");

        var fileContent = new StreamContent(File.OpenRead(path));
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mp4");
        using var form = new MultipartFormDataContent();
        form.Add(fileContent, "file", Path.GetFileName(path));
        form.Add(new StringContent(
            string.IsNullOrWhiteSpace(AppSettings.AudioModel) ? "whisper-1" : AppSettings.AudioModel), "model");
        req.Content = form;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var resp = await _httpClient.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
    }
#endif

    /// <summary>点击图片按钮：相册选图 → 视觉模型识别 → 文本对话模型回答。</summary>
    private async void OnImageClicked(object? sender, EventArgs e)
    {
        try
        {
            var file = await MediaPicker.Default.PickPhotoAsync(new MediaPickerOptions { Title = "选择图片" });
            if (file == null) return;

            var path = file.FullPath;
            // 用户图片消息（识别结果稍后存入，作为历史上下文）
            var userMsg = new ChatMsg { IsUser = true, ImageUrl = path, Content = "（图片）" };
            Messages.Add(userMsg);
            ScrollToBottom();

            var aiMsg = new ChatMsg { Content = "", IsUser = false };
            Messages.Add(aiMsg);
            ScrollToBottom();

            // 视觉模型识别
            string visionResult;
            if (AppSettings.VisionEnabled)
                visionResult = await AnalyzeImageAsync(path);
            else
                visionResult = "（未配置视觉模型，无法识别图片内容）";

            // 识别结果存入图片消息历史（后续对话 AI 也能记住图片内容）
            userMsg.VisionResult = visionResult;
            _ = ChatStore.Instance.SaveMessageAsync(userMsg);

            // 把识别结果交给文本对话模型回答
            await StreamVisionReply("请描述这张图片的内容。", visionResult, path, aiMsg);
        }
        catch (Exception ex)
        {
            // 诊断提示：显示视觉模型 URL 与图片体积，便于区分"连不上/格式错/体积过大"
            string hint = AppSettings.VisionEnabled ? $"\n\n视觉模型：{AppSettings.VisionApiUrl}" : "\n\n（未配置视觉模型）";
            hint += $"\n图片压缩后约 {_lastVisionBytes / 1024} KB（base64 约 {_lastVisionB64 / 1024} KB）";
            await DisplayAlert("图片发送失败", ex.Message + hint, "确定");
        }
    }

    private long _lastVisionBytes;
    private long _lastVisionB64;

    /// <summary>调用视觉模型识别图片，返回描述文本（先压缩避免超限）。请求体严格对齐 SenseNova 官方示例。</summary>
    private async Task<string> AnalyzeImageAsync(string path)
    {
        byte[] bytes = await CompressImageAsync(path);
        _lastVisionBytes = bytes.Length;
        string b64 = Convert.ToBase64String(bytes);
        _lastVisionB64 = b64.Length;
        string mime = "image/jpeg"; // 统一压缩为 JPEG

        var messages = new object[]
        {
            new { content = "你是一个说话客观公正的小助手", role = "system" },
            new { role = "user", content = new object[]
            {
                new { type = "text", text = "请描述这张图片的内容，尽量详细。" },
                new { type = "image_url", image_url = new { url = $"data:{mime};base64,{b64}" } }
            } }
        };
        var reqBody = new
        {
            model = string.IsNullOrWhiteSpace(AppSettings.VisionModel) ? "sensenova-6.8-flash-lite" : AppSettings.VisionModel,
            messages,
            n = 1,
            stream = true,           // 用 SSE 流式：边生成边推，连接持续活跃，避免长连接被掐断
            max_tokens = 1000,
            reasoning_effort = "none"
        };
        string reqJson = JsonSerializer.Serialize(reqBody);

        // 429 限流自动重试（退避重试，最多 3 次），偶发限流能自愈
        // 注意：HttpRequestMessage 只能发送一次，每次重试必须新建请求对象
        const int maxRetries = 3;
        HttpResponseMessage resp = null!;
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.VisionApiUrl);
            req.Headers.Add("Authorization", $"Bearer {AppSettings.VisionApiKey}");
            req.Content = new StringContent(reqJson, Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try
            {
                resp = await _httpClient.SendAsync(req, cts.Token);
                if ((int)resp.StatusCode != 429) break; // 非限流，直接返回
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                resp?.Dispose();
                await Task.Delay(1000 * (attempt + 1)); // 退避：1s, 2s, 3s
                continue;
            }
            resp?.Dispose();
            await Task.Delay(2000 * (attempt + 1)); // 429 退避：2s, 4s, 6s
        }
        resp.EnsureSuccessStatusCode();

        // SSE 流式解析：累积所有 delta.content
        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var sb = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line.Substring(6);
            if (data == "[DONE]") break;
            try
            {
                var chunk = JsonSerializer.Deserialize<StreamChunk>(data);
                var delta = chunk?.choices?[0]?.delta?.content;
                if (!string.IsNullOrEmpty(delta)) sb.Append(delta);
            }
            catch { }
        }
        string result = sb.ToString();
        if (string.IsNullOrWhiteSpace(result))
        {
            // 兜底：有些实现把结果放 choices[0].message.content，尝试整段解析
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0
                && ch[0].TryGetProperty("message", out var msg))
            {
                result = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
            }
        }
        return result;
    }

    /// <summary>压缩图片到合适尺寸（最长边 512px、JPEG 质量 60），尽可能缩小 base64 体积，避免上传被服务端断开。</summary>
    private async Task<byte[]> CompressImageAsync(string path)
    {
#if ANDROID
        var opts = new Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
        Android.Graphics.BitmapFactory.DecodeFile(path, opts);
        int srcW = opts.OutWidth, srcH = opts.OutHeight;
        if (srcW <= 0 || srcH <= 0)
            return await File.ReadAllBytesAsync(path);

        const int maxEdge = 512;
        int sample = 1;
        while (Math.Max(srcW / sample, srcH / sample) > maxEdge * 2) sample *= 2;

        var decodeOpts = new Android.Graphics.BitmapFactory.Options { InSampleSize = sample };
        using var src = Android.Graphics.BitmapFactory.DecodeFile(path, decodeOpts);
        if (src == null)
            return await File.ReadAllBytesAsync(path);

        int scale = 1;
        while (Math.Max(src.Width / scale, src.Height / scale) > maxEdge) scale *= 2;
        using var scaled = scale > 1
            ? Android.Graphics.Bitmap.CreateScaledBitmap(src, src.Width / scale, src.Height / scale, true)
            : Android.Graphics.Bitmap.CreateBitmap(src);

        using var ms = new MemoryStream();
        scaled.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg, 60, ms);
        return ms.ToArray();
#else
        return await File.ReadAllBytesAsync(path);
#endif
    }

    /// <summary>把视觉识别结果回传给文本对话模型，流式生成回答。</summary>
    private async Task StreamVisionReply(string prompt, string visionResult, string imagePath, ChatMsg aiBubble)
    {
        string model = AppSettings.ResolveChatModel(AppSettings.ForceThinking);

        var visionSystem = AppSettings.BuildSystemPrompt();
        if (!string.IsNullOrEmpty(_memoryContext))
            visionSystem += "\n\n" + _memoryContext;

        var messages = new List<object>
        {
            new { role = "system", content = visionSystem }
        };
        // 把图片路径与视觉识别结果作为临时上下文（不持久化到 Messages，避免历史文件保存 base64）
        messages.Add(new { role = "user", content = prompt });
        messages.Add(new { role = "assistant", content = "（视觉识别结果）\n" + visionResult });
        messages.Add(new { role = "user", content =
            "请根据上面的视觉识别结果回答用户的问题，用纯普通文本，禁止 Markdown，直接给结论。" });

        var reqBody = BuildChatBody(model, messages, stream: true);
        var json = JsonSerializer.Serialize(reqBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _httpClient.PostAsync(AppSettings.ApiUrl, content);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line.Substring(6);
            if (data == "[DONE]") break;
            try
            {
                var chunk = JsonSerializer.Deserialize<StreamChunk>(data);
                var delta = chunk?.choices?[0]?.delta?.content;
                if (string.IsNullOrEmpty(delta)) continue;
                aiBubble.Content += delta;
                FollowBottomIfNeeded();
            }
            catch { }
        }
        _ = ChatStore.Instance.SaveMessageAsync(aiBubble);
        await SpeakIfEnabledAsync(aiBubble.Content);
    }

    /// <summary>点击思考过程折叠头部：展开/收起。</summary>
    private void OnThinkingHeaderTapped(object? sender, EventArgs e)
    {
        if (sender is not BindableObject bo) return;
        if (bo.BindingContext is ChatMsg msg)
            msg.ThinkingExpanded = !msg.ThinkingExpanded;
    }

    /// <summary>点击 Agent 过程区头部：展开/收起（折叠后仍可点开查看明细）。</summary>
    private void OnAgentHeaderTapped(object? sender, EventArgs e)
    {
        if (sender is not BindableObject bo) return;
        if (bo.BindingContext is ChatMsg msg && msg.AgentRun != null)
        {
            msg.AgentRun.Collapsed = !msg.AgentRun.Collapsed;
            msg.NotifyAgentChanged();
        }
    }

    // ─────────── 权限气泡（盾牌） ───────────

    /// <summary>点击盾牌图标：展开/收起权限面板。</summary>
    private void OnShieldTapped(object? sender, EventArgs e)
    {
        permPanel.IsVisible = !permPanel.IsVisible;
        if (permPanel.IsVisible) RefreshPermPanel();
    }

    /// <summary>刷新权限面板的勾选状态与摘要。</summary>
    private void RefreshPermPanel()
    {
        int level = AppSettings.FileAccessLevel;
        bool granted = StorageAccess.IsAllFilesGranted();

        chkReadWs.Text = level == 1 ? "●" : "○";
        chkWriteWs.Text = level == 2 ? "●" : "○";
        chkReadDisk.Text = level == 3 ? "●" : "○";
        chkWriteDisk.Text = level == 4 ? "●" : "○";

        // 选中项高亮，未选中置灰
        icoReadWs.Opacity = level == 1 ? 1 : 0.45;
        icoWriteWs.Opacity = level == 2 ? 1 : 0.45;

        chkBrowser.Text = AppSettings.BrowserPermission ? "☑" : "☐";
        chkFullAccess.Text = AppSettings.FullAccess ? "☑" : "☐";

        // 完全访问时上面四个单选置灰（已被覆盖）
        permSingleGroup.Opacity = AppSettings.FullAccess ? 0.4 : 1;

        // 全盘相关项：没有系统权限时一律置灰，避免"假放开"
        bool diskDim = AppSettings.FullAccess || !granted;
        permDiskGroup.Opacity = diskDim ? 0.4 : 1;
        rowFullAccess.Opacity = granted ? 1 : 0.4;

        // 没系统权限 → 顶部亮出引导条（按系统版本给不同说法：
        // Android 11+ 叫「所有文件访问」，Android 10- 只有普通「存储」权限）
        storageWarnBox.IsVisible = !granted;
        lblGrantStorage.Text = StorageAccess.GrantButtonText;
        if (!granted)
            lblStorageWarn.Text = "尚未授予" + StorageAccess.PermissionLabel + "，全盘读写与完全访问无法生效。"
                                + "开启后 Ta 才能读写内部存储、生成的文件你也能在文件管理器里看到。";

        // 摘要：说清当前真实状态 + 边界
        var sb = new StringBuilder();
        if (!granted)
        {
            sb.Append("当前仅能访问工作区。");
            sb.Append(StorageAccess.WorkspaceVisible
                ? "工作区在公共存储，文件管理器可见。"
                : "工作区在应用私有目录，文件管理器看不到，可用下方「导出」搬到 Download。");
        }
        else if (AppSettings.FullAccess)
        {
            sb.Append("完全访问已开启：所有限制放开，Ta 可读写公共存储任意位置。");
        }
        else
        {
            sb.Append("权限：").Append(AppSettings.FileAccessDesc).Append("。");
            sb.Append(StorageAccess.WorkspaceVisible
                ? "工作区在公共存储，文件管理器可见。"
                : "工作区在应用私有目录，外部不可见。");
        }
        sb.Append("\n注：即使全盘权限也只覆盖公共存储，读不到其他 App 的私有目录。");
        // 排查用：把系统版本和权限判定结果直接摆出来，出问题一眼能看到
        sb.Append("\n系统：").Append(SuperAdmin.AndroidVersionText)
          .Append(" · ").Append(StorageAccess.PermissionLabel)
          .Append(granted ? "已授予" : "未授予");
        lblPermSummary.Text = sb.ToString();
    }

    /// <summary>选择文件权限级别（四选一互斥）。</summary>
    private async void OnPermLevelTapped(object? sender, EventArgs e)
    {
        if (AppSettings.FullAccess)
        {
            await DisplayAlert("完全访问已开启", "当前是「完全访问」模式，已覆盖所有文件权限。请先取消完全访问再单独选择。", "好");
            return;
        }
        if (sender is not TapGestureRecognizer tg || tg.CommandParameter is not string s) return;
        if (!int.TryParse(s, out var level)) return;

        // 全盘级别（3/4）需要系统真实权限，先拦一道并引导
        if (level >= 3 && !StorageAccess.IsAllFilesGranted())
        {
            await PromptGrantStorageAsync();
            return;
        }

        // 再点一次同一项 = 取消授权（回到未授权）
        AppSettings.FileAccessLevel = AppSettings.FileAccessLevel == level ? 0 : level;
        RefreshPermPanel();
    }

    /// <summary>切换浏览器权限。</summary>
    private void OnPermBrowserTapped(object? sender, EventArgs e)
    {
        AppSettings.BrowserPermission = !AppSettings.BrowserPermission;
        RefreshPermPanel();
    }

    /// <summary>切换完全访问（高风险，需二次确认 + 系统权限前置检测）。</summary>
    private async void OnPermFullAccessTapped(object? sender, EventArgs e)
    {
        if (!AppSettings.FullAccess)
        {
            // 没有系统「所有文件访问」权限时不允许开启，避免出现"假放开"
            if (!StorageAccess.IsAllFilesGranted())
            {
                await PromptGrantStorageAsync();
                return;
            }

            bool ok = await DisplayAlert("⚠️ 开启完全访问",
                "开启后 Ta 可以读写公共存储上任意文件、不受目录限制，风险很高。\n\n"
                + "请确认你完全信任当前模型与接口（中间的 API 服务商也能看到文件内容）。\n\n"
                + "注意：该权限只能访问公共存储，读不到其他 App 的私有目录。",
                "我确认", "取消");
            if (!ok) return;
        }
        AppSettings.FullAccess = !AppSettings.FullAccess;
        RefreshPermPanel();
    }

    /// <summary>
    /// 点「去开启」：拿存储权限。
    /// Android 11+ 跳系统「所有文件访问」页；Android 10 及以下直接弹运行时权限申请。
    /// 拿到后立刻刷新面板，不用等回到页面。
    /// </summary>
    private async void OnGrantStorageTapped(object? sender, EventArgs e)
    {
        await StorageAccess.RequestPermissionAsync();
        StorageAccess.InvalidateProbe();   // 重新探测，别拿旧缓存判
        RefreshPermPanel();
    }

    /// <summary>
    /// 全盘权限缺失时的统一引导：说清为什么、给出跳转按钮。
    /// 用户从系统设置回来后，OnAppearing 会重新刷新面板状态。
    /// </summary>
    private async Task PromptGrantStorageAsync()
    {
        string how = SuperAdmin.HasAllFilesAccessApi
            ? "点「去开启」会跳到系统设置，找到「青阳AI」并打开「允许访问所有文件」，回来后权限就会自动生效。\n\n"
              + "（部分定制系统——比如鸿蒙/EMUI——把入口放在「设置 → 应用 → 应用管理 → 青阳AI → 权限」里，"
              + "找不到「所有文件访问」时去那儿翻一下。）"
            : "点「去开启」会弹出系统的存储权限申请，允许即可。\n\n"
              + "（你这台是 " + SuperAdmin.AndroidVersionText + "，这个版本的系统没有「所有文件访问」那个开关，"
              + "用普通存储权限 + 传统存储模式实现同样的全盘读写效果。）";

        bool go = await DisplayAlert("需要存储权限",
            "「读取/修改全盘文件」和「完全访问」需要" + StorageAccess.PermissionLabel + "，"
            + "当前尚未授予，所以这两个选项还不能生效。\n\n"
            + how + "\n\n"
            + "（开启后 Ta 生成的文件会放在 内部存储/QingYangAI/WorkSpace，你在文件管理器里能直接看到）",
            "去开启", "暂不");
        if (go)
        {
            await StorageAccess.RequestPermissionAsync();
            StorageAccess.InvalidateProbe();
            RefreshPermPanel();
        }
    }

    /// <summary>把工作区文件导出到公共 Download 目录。</summary>
    private async void OnExportWorkspaceTapped(object? sender, EventArgs e)
    {
        if (StorageAccess.WorkspaceVisible)
        {
            await DisplayAlert("无需导出",
                $"当前工作区已经在公共存储里，文件管理器可以直接看到：\n\n{StorageAccess.ToDisplay(AppSettings.EffectiveWorkspacePath)}",
                "好");
            return;
        }

        if (!StorageAccess.IsAllFilesGranted())
        {
            await PromptGrantStorageAsync();
            return;
        }

        var (ok, msg) = await StorageAccess.ExportWorkspaceAsync();
        await DisplayAlert(ok ? "导出完成" : "导出失败", msg, "好");
        RefreshPermPanel();
    }

    /// <summary>点击图片框架：进入全屏鉴赏模式。</summary>
    private async void OnImageFrameTapped(object? sender, EventArgs e)
    {
        if (sender is not BindableObject bo || bo.BindingContext is not ChatMsg msg || !msg.HasImage) return;
        ImageSource source = msg.ImageSource;
        if (source == null) return;
        await Navigation.PushAsync(new ImagePreviewPage(source));
    }

    /// <summary>点击 AI 气泡上的小喇叭：朗读该条回复。</summary>
    private async void OnSpeakTapped(object? sender, EventArgs e)
    {
        if (sender is not BindableObject bo || bo.BindingContext is not ChatMsg msg) return;
        var text = msg.Content;
        if (string.IsNullOrWhiteSpace(text)) return;
        try { await TtsPlayer.PlayAsync(text); }
        catch (Exception ex) { await DisplayAlert("语音播放失败", ex.Message, "确定"); }
    }

    /// <summary>
    /// 文生图管线：检测到 {img:"描述"} → 先显示预加载图片框架，调用文生图 API
    /// 生成图片（返回 base64），完成后填充进框架；失败则显示错误状态。
    /// </summary>
    private async Task GenerateImagePipelineAsync(string rawText, ChatMsg aiBubble)
    {
        var descs = ExtractImageCommands(rawText);
        if (descs.Count == 0) return;

        if (!AppSettings.ImgEnabled)
        {
            aiBubble.ImageState = "error";
            aiBubble.Content = "（未配置文生图模型，无法生成图片）";
            _ = ChatStore.Instance.SaveMessageAsync(aiBubble);
            UpdateContextLabel();
            return;
        }

        // 1) 预加载状态：先摆出一个加载中的图片框架
        aiBubble.Content = "";
        aiBubble.ImageState = "loading";
        ScrollToBottom();
        FollowBottomIfNeeded();

        try
        {
            // 2) 调用文生图 API（OpenAI 兼容，返回 base64）
            string model = string.IsNullOrWhiteSpace(AppSettings.ImgModel) ? "gpt-image-1" : AppSettings.ImgModel;
            var reqBody = new
            {
                model,
                prompt = descs[0],
                response_format = "b64_json",
                size = "1024x1024"
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.ImgApiUrl);
            req.Headers.Add("Authorization", $"Bearer {AppSettings.ImgApiKey}");
            req.Content = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");

            // 图片生成耗时较长，放宽超时
            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var resp = await _httpClient.SendAsync(req, cts.Token);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            string? b64 = null;
            if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
            {
                var first = data[0];
                if (first.TryGetProperty("b64_json", out var b)) b64 = b.GetString();
                else if (first.TryGetProperty("url", out var u)) b64 = u.GetString(); // 备用：URL 形式
            }

            if (string.IsNullOrWhiteSpace(b64))
                throw new Exception("接口未返回图片数据");

            // 3) 完成：图片落盘为文件（历史只存路径），base64 仅供本次会话显示
            var imagePath = await ChatStore.SaveImageBytesAsync(Convert.FromBase64String(b64), ".png");
            aiBubble.ImageBase64 = b64;
            aiBubble.ImageUrl = imagePath;
            aiBubble.ImageState = "done";
            aiBubble.Content = $"（已生成图片：{descs[0]}）";
            ScrollToBottom();
        }
        catch (Exception ex)
        {
            aiBubble.ImageState = "error";
            aiBubble.Content = "（图片生成失败：" + ex.Message + "）";
        }
        finally
        {
            _ = ChatStore.Instance.SaveMessageAsync(aiBubble);
            UpdateContextLabel();
        }
    }

    /// <summary>语音播放：如果开启 TTS 且自动播放，则在回复完成后朗读。</summary>
    private async Task SpeakIfEnabledAsync(string text)
    {
        if (!string.IsNullOrWhiteSpace(text) && AppSettings.TtsEnabled && AppSettings.TtsAutoPlay)
        {
            try { await TtsPlayer.PlayAsync(text); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[TTS] 播放失败: {ex.Message}"); }
        }
    }

#if ANDROID
/// <summary>Android 系统语音识别结果监听。</summary>
public class SystemRecognizerListener : Java.Lang.Object, Android.Speech.IRecognitionListener
{
    private readonly TaskCompletionSource<string?> _tcs;
    private readonly StringBuilder _sb = new();

    public SystemRecognizerListener(TaskCompletionSource<string?> tcs) => _tcs = tcs;

    public void OnReadyForSpeech(Android.OS.Bundle? p0) { }
    public void OnBeginningOfSpeech() { }
    public void OnRmsChanged(float p0) { }
    public void OnBufferReceived(byte[]? p0) { }
    public void OnEndOfSpeech() => _tcs.TrySetResult(_sb.ToString());
    public void OnError(Android.Speech.SpeechRecognizerError p0) =>
        _tcs.TrySetException(new Exception("系统语音识别错误：" + p0));
    public void OnEvent(int p0, Android.OS.Bundle? p1) { }
    public void OnPartialResults(Android.OS.Bundle? p0) => Collect(p0);
    public void OnResults(Android.OS.Bundle? p0)
    {
        Collect(p0);
        _tcs.TrySetResult(_sb.ToString());
    }

    private void Collect(Android.OS.Bundle? bundle)
    {
        var matches = bundle?.GetStringArrayList("android.speech.extra.RESULTS_RECOGNITION");
        if (matches == null) return;
        foreach (var m in matches)
        {
            if (!string.IsNullOrEmpty(m)) _sb.Append(m);
        }
    }
}
#endif
}

public class ChatMsg : INotifyPropertyChanged
{
    private string _content = "";
    private bool _isUser;
    private bool _hasTerminalHeader;
    private string _terminalStep = "与Shizuku通信";
    private string _terminalOutput = "";
    private string _terminalTitle = "";
    private string _thinking = "";
    private string _terminalExecLog = "";
    private string _visionResult = "";    // 图片识别文字（存历史，供后续对话使用）
    private string _imageBase64 = "";     // 生成的图片数据
    private string _imageUrl = "";        // 用户发送的图片来源（本地路径）
    private string _imageState = "";      // "" 无图 / "loading" 预加载 / "done" 完成 / "error" 失败
    private string _voicePath = "";       // 语音消息音频文件路径

    /// <summary>数据库主键（0 表示尚未入库）。</summary>
    public int Id { get; set; }

    /// <summary>消息时间（后台主动消息、日记归属日期都依赖它）。</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>是否为后台主动发来的关心消息。</summary>
    public bool IsProactive { get; set; }

    /// <summary>是否为压缩摘要消息（替代早期历史消息）。</summary>
    public bool IsCompressed { get; set; }

    private bool _isWaiting;

    /// <summary>是否正在等待 AI 回复（显示转圈指示器，收到首字后关闭）。</summary>
    public bool IsWaiting
    {
        get => _isWaiting;
        set
        {
            if (_isWaiting == value) return;
            _isWaiting = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsWaiting)));
        }
    }

    /// <summary>消息时间（小时:分钟，展示用；时间源为 Timestamp）。</summary>
    [JsonIgnore]
    public string TimeText => Timestamp.ToString("HH:mm");

    /// <summary>该条消息的终端执行记录（命令+输出），供后续上下文发送给模型。</summary>
    public string TerminalExecLog
    {
        get => _terminalExecLog;
        set
        {
            if (_terminalExecLog == value) return;
            _terminalExecLog = value ?? "";
        }
    }

    /// <summary>图片识别文字（视觉模型结果，存入历史供后续对话使用）。</summary>
    public string VisionResult
    {
        get => _visionResult;
        set
        {
            if (_visionResult == value) return;
            _visionResult = value ?? "";
        }
    }

    /// <summary>生成的图片数据（base64）。</summary>
    public string ImageBase64
    {
        get => _imageBase64;
        set
        {
            if (_imageBase64 == value) return;
            _imageBase64 = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageBase64)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasImage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageSource)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsImageDone)));
        }
    }

    /// <summary>用户发送的图片来源（本地路径或 URL）。</summary>
    public string ImageUrl
    {
        get => _imageUrl;
        set
        {
            if (_imageUrl == value) return;
            _imageUrl = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageUrl)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasImage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageSource)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsImageDone)));
        }
    }

    /// <summary>图片状态："" 无图 / "loading" 预加载 / "done" 完成 / "error" 失败。</summary>
    public string ImageState
    {
        get => _imageState;
        set
        {
            if (_imageState == value) return;
            _imageState = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageState)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsImageLoading)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsImageError)));
        }
    }

    /// <summary>语音消息音频文件路径。</summary>
    public string VoicePath
    {
        get => _voicePath;
        set
        {
            if (_voicePath == value) return;
            _voicePath = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VoicePath)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasVoice)));
        }
    }

    /// <summary>是否有图片（生成图或用户图）。</summary>
    [JsonIgnore]
    public bool HasImage => !string.IsNullOrEmpty(_imageBase64) || !string.IsNullOrEmpty(_imageUrl);

    /// <summary>图片是否生成完成（可显示）。</summary>
    [JsonIgnore]
    public bool IsImageDone => !string.IsNullOrEmpty(_imageBase64) || !string.IsNullOrEmpty(_imageUrl);

    /// <summary>是否图片预加载中。</summary>
    [JsonIgnore]
    public bool IsImageLoading => _imageState == "loading";

    /// <summary>图片是否生成失败。</summary>
    [JsonIgnore]
    public bool IsImageError => _imageState == "error";

    /// <summary>是否有语音消息。</summary>
    [JsonIgnore]
    public bool HasVoice => !string.IsNullOrEmpty(_voicePath);

    /// <summary>图片显示源：base64 优先，否则本地文件/URL。</summary>
    [JsonIgnore]
    public ImageSource ImageSource
    {
        get
        {
            if (!string.IsNullOrEmpty(_imageBase64))
            {
                try { return ImageSource.FromStream(() => new MemoryStream(Convert.FromBase64String(_imageBase64))); }
                catch { }
            }
            if (!string.IsNullOrEmpty(_imageUrl))
            {
                if (File.Exists(_imageUrl)) return ImageSource.FromFile(_imageUrl);
                return ImageSource.FromUri(new Uri(_imageUrl));
            }
            return null!;
        }
    }

    /// <summary>是否有思考过程（推理内容）。</summary>
    [JsonIgnore]
    public bool HasThinking => !string.IsNullOrEmpty(_thinking);

    // ─────────── Agent 循环过程（单行缩略 + 折叠） ───────────

    private AgentRun? _agentRun;

    /// <summary>本条消息的 Agent 循环过程（未开启 Agent 模式时为 null）。</summary>
    [JsonIgnore]
    public AgentRun? AgentRun
    {
        get => _agentRun;
        set
        {
            _agentRun = value;
            NotifyAgentChanged();
        }
    }

    /// <summary>是否显示 Agent 过程区。</summary>
    [JsonIgnore]
    public bool HasAgentRun => _agentRun != null && _agentRun.Steps.Count > 0;

    /// <summary>Agent 过程区标题：运行中显示当前动作，结束后显示步骤总账（▸/▾ 表示能否展开）。</summary>
    [JsonIgnore]
    public string AgentHeaderText
    {
        get
        {
            if (_agentRun == null) return "";
            // 运行中不给箭头（还没内容可展）；结束后给箭头提示可点开明细
            if (!_agentRun.Finished) return _agentRun.CollapsedSummary;
            return _agentRun.CollapsedSummary + (_agentRun.Collapsed ? "  ▸" : "  ▾");
        }
    }

    /// <summary>Agent 步骤明细（展开时显示，一行一步）。</summary>
    [JsonIgnore]
    public string AgentStepsText => _agentRun?.FullText ?? "";

    /// <summary>过程区是否展开。</summary>
    [JsonIgnore]
    public bool AgentExpanded => _agentRun != null && !_agentRun.Collapsed;

    /// <summary>折叠时是否显示明细。</summary>
    [JsonIgnore]
    public bool AgentDetailsVisible => HasAgentRun && AgentExpanded;

    /// <summary>任务是否仍在执行中（标题流光）。</summary>
    [JsonIgnore]
    public bool AgentRunning => _agentRun != null && !_agentRun.Finished;

    /// <summary>把 Agent 过程区的所有绑定属性一次性通知刷新。</summary>
    public void NotifyAgentChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AgentRun)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAgentRun)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AgentHeaderText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AgentStepsText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AgentExpanded)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AgentDetailsVisible)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AgentRunning)));
    }

    /// <summary>思考过程与下方内容（终端或正文）之间的内嵌分割线是否显示。</summary>
    [JsonIgnore]
    public bool ShowThinkingDivider => HasThinking && (HasTerminalHeader || !string.IsNullOrEmpty(_content));

    /// <summary>思考过程文本（deepseek-reasoner 的 reasoning_content）。</summary>
    public string Thinking
    {
        get => _thinking;
        set
        {
            if (_thinking == value) return;
            _thinking = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thinking)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasThinking)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowThinkingDivider)));
        }
    }

    /// <summary>思考是否正在生成（思考中 → 流光；正文开始或完成 → 停）。</summary>
    private bool _isThinkingActive;

    [JsonIgnore]
    public bool IsThinkingActive
    {
        get => _isThinkingActive;
        set
        {
            if (_isThinkingActive == value) return;
            _isThinkingActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsThinkingActive)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThinkingStatusText)));
        }
    }

    /// <summary>正文是否已开始（决定思考状态文字："思考中…" → "思考结束·用时N秒"）。</summary>
    [JsonIgnore]
    public bool ContentStarted => !string.IsNullOrEmpty(_content);

    /// <summary>终端是否正在运行（有终端头部且未完成 → 步骤行流光）。</summary>
    [JsonIgnore]
    public bool IsTerminalRunning =>
        _hasTerminalHeader && !string.Equals(_terminalStep, "完成", StringComparison.Ordinal);

    /// <summary>思考是否展开（默认收起）。</summary>
    private bool _thinkingExpanded;

    [JsonIgnore]
    public bool ThinkingExpanded
    {
        get => _thinkingExpanded;
        set
        {
            if (_thinkingExpanded == value) return;
            _thinkingExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThinkingExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThinkingArrow)));
        }
    }

    /// <summary>三角符号：展开 ▼ / 收起 ▶。</summary>
    [JsonIgnore]
    public string ThinkingArrow => ThinkingExpanded ? "ChevronDown" : "ChevronRight";

    /// <summary>思考用时（秒）。</summary>
    private double _thinkingSeconds;

    [JsonIgnore]
    public double ThinkingSeconds
    {
        get => _thinkingSeconds;
        set
        {
            if (Math.Abs(_thinkingSeconds - value) < 0.05) return;
            _thinkingSeconds = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThinkingSeconds)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThinkingStatusText)));
        }
    }

    /// <summary>
    /// 思考状态文字：
    /// 思考过程中 →「思考中…」（流光下动态跳动）；
    /// 正文开始后 →「思考结束·用时N秒」（只计思考过程，正文不计入）。
    /// </summary>
    [JsonIgnore]
    public string ThinkingStatusText
    {
        get
        {
            if (_isThinkingActive && !ContentStarted) return "思考中…";
            if (_thinkingSeconds < 60)
                return $"思考结束·用时{_thinkingSeconds:F0}秒";
            int min = (int)_thinkingSeconds / 60;
            int sec = (int)_thinkingSeconds % 60;
            return $"思考结束·用时{min}分{sec}秒";
        }
    }

    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Content)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasContent)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowThinkingDivider)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContentStarted)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThinkingStatusText)));
        }
    }

    /// <summary>是否有正文内容（控制朗读按钮显隐）。</summary>
    [JsonIgnore]
    public bool HasContent => !string.IsNullOrEmpty(_content);

    public bool IsUser
    {
        get => _isUser;
        set
        {
            if (_isUser == value) return;
            _isUser = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUser)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAI)));
        }
    }

    [JsonIgnore]
    public bool IsAI => !_isUser;

    /// <summary>是否显示终端头部三行小气泡。</summary>
    public bool HasTerminalHeader
    {
        get => _hasTerminalHeader;
        set
        {
            if (_hasTerminalHeader == value) return;
            _hasTerminalHeader = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTerminalHeader)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowThinkingDivider)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTerminalRunning)));
        }
    }

    /// <summary>头部第二行状态：与Shizuku通信 / 拉取终端 / 运行终端 / 完成。</summary>
    public string TerminalStep
    {
        get => _terminalStep;
        set
        {
            if (_terminalStep == value) return;
            _terminalStep = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TerminalStep)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TerminalStepLine)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TerminalHeaderText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTerminalRunning)));
        }
    }

    /// <summary>头部第三行输出结果。</summary>
    public string TerminalOutput
    {
        get => _terminalOutput;
        set
        {
            if (_terminalOutput == value) return;
            _terminalOutput = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TerminalOutput)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TerminalOutputLine)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TerminalHeaderText)));
        }
    }

    /// <summary>第一行固定文案。</summary>
    [JsonIgnore]
    public string TerminalTitleLine => string.IsNullOrEmpty(_terminalTitle) ? "使用shizuku — 终端" : _terminalTitle;

    /// <summary>工具气泡标题：本次实际调用了什么（API 名 / Shizuku 终端命令），空 = 旧默认文案。</summary>
    public string TerminalTitle
    {
        get => _terminalTitle;
        set
        {
            if (_terminalTitle == value) return;
            _terminalTitle = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TerminalTitle)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TerminalTitleLine)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TerminalHeaderText)));
        }
    }

    /// <summary>第二行：当前步骤。</summary>
    [JsonIgnore]
    public string TerminalStepLine => $"当前步骤：{TerminalStep}";

    /// <summary>第三行：输出。</summary>
    [JsonIgnore]
    public string TerminalOutputLine => $"输出：[{TerminalOutput}]";

    /// <summary>三行完整文本（供折叠/展示）。</summary>
    [JsonIgnore]
    public string TerminalHeaderText =>
        TerminalTitleLine + "\n" + TerminalStepLine + "\n" + TerminalOutputLine;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class StreamChunk
{
    public List<ChoiceItem>? choices { get; set; }

    /// <summary>原始 usage 节点（保留成 JsonElement，交给 TokenUsageMapper 做多厂商字段映射）。</summary>
    public JsonElement? usage { get; set; }
}

/// <summary>ConsumeSseAsync 的收流结果。</summary>
public sealed class SseConsumeResult
{
    /// <summary>网络侧收到的完整正文（含尚未上屏的部分）。</summary>
    public StringBuilder Buffer { get; } = new();
    public DateTime? ThinkingStart { get; set; }
    /// <summary>最后一个思考字到达的时刻（思考用时的终点，正文不计入）。</summary>
    public DateTime? ThinkingEnd { get; set; }
    /// <summary>思考用时是否已冻结（正文已开始）。</summary>
    public bool ThinkingFrozen { get; set; }
    public bool ThinkingActive { get; set; }
    public bool ContentStarted { get; set; }
    /// <summary>网络侧是否已收完流。</summary>
    public bool ReadDone { get; set; }
    /// <summary>正文中出现 {cmd:"..."} 指令，已提前断流。</summary>
    public bool CancelledForCommand { get; set; }
    /// <summary>正文中出现 {api:"..."} 指令，已提前断流。</summary>
    public bool CancelledForApi { get; set; }
    /// <summary>正文中出现 {img:"..."} 指令，已提前断流。</summary>
    public bool CancelledForImage { get; set; }
    /// <summary>正文中出现 {browse:"..."} 指令，已提前断流。</summary>
    public bool CancelledForBrowse { get; set; }

    /// <summary>正文中出现 {download:"..."} 指令，已提前断流。</summary>
    public bool CancelledForDownload { get; set; }

    /// <summary>正文中出现 {web:"..."} 指令（真浏览器），已提前断流。</summary>
    public bool CancelledForWeb { get; set; }
}
public class ChoiceItem
{
    public DeltaItem? delta { get; set; }
}
public class DeltaItem
{
    public string? content { get; set; }
    public string? reasoning_content { get; set; }
}
public class UsageItem
{
    public int prompt_cache_hit_tokens { get; set; }
    public int prompt_cache_miss_tokens { get; set; }

    // 兼容嵌套结构：prompt_cache_hit_token_details.cache_tokens
    public CacheDetails? prompt_cache_hit_token_details { get; set; }

    // 个别接口用 cached_tokens / uncached_tokens（如 Anthropic 风格）
    public int? cached_tokens { get; set; }
    public int? uncached_tokens { get; set; }
}

public class CacheDetails
{
    public int? cache_tokens { get; set; }
    public int? text_tokens { get; set; }
}

/// <summary>从 usage 兼容提取 (命中, 未命中)。</summary>
public static class CacheUsage
{
    public static (int hit, int miss) Extract(UsageItem? usage)
    {
        if (usage == null) return (0, 0);
        int hit = usage.prompt_cache_hit_tokens;
        int miss = usage.prompt_cache_miss_tokens;

        // 嵌套 details.cache_tokens 兜底
        if (hit <= 0 && usage.prompt_cache_hit_token_details?.cache_tokens is int ch && ch > 0)
            hit = ch;
        // cached_tokens / uncached_tokens 兜底
        if (hit <= 0 && usage.cached_tokens is int ct && ct > 0) hit = ct;
        if (miss <= 0 && usage.uncached_tokens is int ut && ut > 0) miss = ut;
        return (hit, miss);
    }
}