using Microsoft.Maui.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        msgList.Scrolled += OnMsgListScrolled;
        btnSend.Clicked += SendClick;
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
    private void UpdateEmptyState()
    {
        bool empty = Messages.Count == 0;
        emptyState.IsVisible = empty;
        if (empty) emptyMotto.Text = _motto;
    }

    /// <summary>字号自适应：六个字至少占屏幕宽度的 3/4。</summary>
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width > 0)
            emptyMotto.FontSize = Math.Max(30, Math.Min(72, width * 0.75 / 6.0));
    }

    /// <summary>启动时从 SQLite 加载聊天记录并刷新上下文状态。</summary>
    private async Task InitAsync()
    {
        try
        {
            var loaded = await ChatStore.Instance.LoadMessagesAsync();
            foreach (var m in loaded)
                Messages.Add(m);
        }
        catch { /* 数据库异常时从空聊天开始 */ }

        // 空状态：随机抽一句挂屏幕正中央（每次打开应用重新抽）
        _motto = EmptyMottos[Random.Shared.Next(EmptyMottos.Length)];
        UpdateEmptyState();
        UpdateContextLabel();

        // 一次性绑定消息源，并在列表可见前无动画定位到底部（多阶段兜底）——
        // 冷启动直接看到最后一屏，而不是"从上面滚下来"
        _historyLoaded = true;
        msgList.ItemsSource = Messages;
        ScrollToBottomInstant();
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(60), ScrollToBottomInstant);
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(200), () =>
        {
            ScrollToBottomInstant();
            msgList.IsVisible = true;
        });
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(600), ScrollToBottomInstant);

        // 记忆/日记/内心上下文（供 BuildHistoryMessages 注入）；有更新时自动刷新
        MemoryService.MemoriesChanged += OnMemoriesChanged;
        InnerLifeService.StateChanged += OnInnerStateChanged;
        UpdateMoodUi();
        UpdateReasoningChip();
        imgAvatar.Source = AvatarService.Load();
        ApplyGlass();
        ShowLastCrashOnce();
        _ = RefreshContextsAsync();
        _ = DiaryService.CheckYesterdayDiaryAsync();
    }

    /// <summary>上次有闪退的话，打开聊天页时展示一次并清除（把内容发给开发即可定位）。</summary>
    private async Task ShowLastCrashOnce()
    {
        try
        {
            var crash = Preferences.Default.Get("LastCrash", "");
            if (string.IsNullOrWhiteSpace(crash)) return;
            Preferences.Default.Set("LastCrash", "");
            await DisplayAlert("上次闪退信息（请截图/复制发我）", crash, "知道了");
        }
        catch { }
    }

    private async Task RefreshContextsAsync()
    {
        _memoryContext = await MemoryService.GetMemoryContextAsync();
        _diaryContext = await DiaryService.GetLatestDiaryContextAsync();
        _deviceContext = await DeviceContextService.BuildAsync();
        _innerContext = InnerLifeService.BuildInnerContext();
        _observedContext = CareWatch.BuildObservedContext();
        UpdateContextLabel();
    }

    private async void OnMemoriesChanged() => await RefreshContextsAsync();

    /// <summary>内心状态（心情/心里话）变化：刷新标题栏表情与注入上下文。</summary>
    private void OnInnerStateChanged()
    {
        UpdateMoodUi();
        _ = RefreshContextsAsync();
    }

    private void UpdateMoodUi() => lblMood.Text = InnerLifeService.MoodEmoji(InnerLifeService.Mood);

    /// <summary>页面出现时无动画直接定位到最后一条消息（不先显示顶端再滚下来）。</summary>
    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (!_historyLoaded) return; // 冷启动首屏由 InitAsync 负责定位，避免与加载竞态

        // 回到前台：定位到底部 + 同步后台主动发来的新消息 + 刷新上下文
        ApplyApiKey();                            // 设置页可能改了 Key（修复旧版"要再进出一次才生效"）
        imgAvatar.Source = AvatarService.Load();  // 设置页可能新生成了头像
        UpdateReasoningChip();                    // 配置套可能切换，推理能力随当前模型变化
        ApplyGlass();                             // 画质选择可能变化
        ScrollToBottomInstant();
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), ScrollToBottomInstant);
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(200), ScrollToBottomInstant);
        _ = SyncNewFromDbAsync();
        _ = RefreshContextsAsync();
    }

    /// <summary>离开页面：停止语音播放。</summary>
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        TtsPlayer.Stop();
    }

    /// <summary>把数据库里比界面更新（Id 更大）的消息补进来（后台主动关心消息场景）。</summary>
    private async Task SyncNewFromDbAsync()
    {
        try
        {
            int maxKnown = Messages.Count > 0 ? Messages.Max(m => m.Id) : 0;
            var fresh = await ChatStore.Instance.GetMessagesAfterAsync(maxKnown);
            if (fresh.Count == 0) return;
            foreach (var m in fresh)
                Messages.Add(m);
            UpdateEmptyState();
            ScrollToBottom();
            UpdateContextLabel();
        }
        catch { }
    }

    /// <summary>清空聊天数据：删除数据库消息并回到空状态语句（供设置页调用）。</summary>
    public async Task ClearMessages()
    {
        Messages.Clear();
        try { await ChatStore.Instance.ClearMessagesAsync(); } catch { }
        UpdateEmptyState();
        UpdateContextLabel();
    }

    /// <summary>
    /// 更精确的 token 估算：按常见中英混合加权。
    /// 中文/全角字符按 1 字≈1 token（略保守加系数 0.1），
    /// 英文/数字/空格按 4 字符≈1 token（0.25/字符）。
    /// 仅作界面使用上下文参考，非 API 精确值。
    /// </summary>
    private int EstimateTokens()
    {
        long chars = 0;
        foreach (var m in Messages) chars += m.Content?.Length ?? 0;
        chars += AppSettings.BuildSystemPrompt().Length;
        chars += _memoryContext.Length + _diaryContext.Length + _deviceContext.Length + _innerContext.Length + _observedContext.Length;

        // 无法区分具体字符时，用混合加权：假设约 1/3 为中文字符、2/3 为其它
        // 中文 1 字≈1.1 token，其它 4 字符≈1 token
        double tokens = 0;
        tokens += chars * (1.0 / 3.0) * 1.1;       // 中文字符占比 1/3
        tokens += chars * (2.0 / 3.0) * 0.25;      // 其余字符占比 2/3
        return (int)Math.Round(tokens) + 32;       // 基础头信息附加
    }

    /// <summary>刷新标题栏「当前使用上下文/最高上下文」显示（人性化格式：整数/K/M）。</summary>
    private void UpdateContextLabel()
    {
        int used = EstimateTokens();
        int max = AppSettings.EffectiveMaxTokens;
        int pct = max > 0 ? (int)Math.Round(used * 100.0 / max) : 0;
        lblCtx.Text = $"上下文 {AppSettings.FormatTokens(used)}/{AppSettings.FormatTokens(max)} · {pct}%";
    }

    /// <summary>刷新标题栏「缓存命中 xx%」：命中 token / (命中+未命中)。无数据时显示占位。</summary>
    private void UpdateCacheHitLabel()
    {
        long total = (long)_lastCacheHit + _lastCacheMiss;
        if (total <= 0)
        {
            lblCacheHit.Text = "缓存命中 --";
            return;
        }
        int pct = (int)Math.Round(_lastCacheHit * 100.0 / total);
        lblCacheHit.Text = $"缓存命中 {pct}%";
    }

    private void ApplyApiKey()
    {
        _httpClient.DefaultRequestHeaders.Remove("Authorization");
        var key = AppSettings.ApiKey.Trim();
        if (!string.IsNullOrEmpty(key))
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {key}");
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
            // 记忆提取 + 内心反思（异步，不阻塞发送流程）
            MemoryService.OnExchangeCompleted(Messages);
            _ = InnerLifeService.ReflectAsync(Messages);
            // 自动压缩检查（异步，不阻塞发送流程）
            _ = CheckAndAutoCompressAsync();
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

    private async Task StreamRequest(string prompt, ChatMsg aiBubble)
    {
        // 模型选择：强制思考或高推理等级 → 思考模型；否则用用户选择的模型
        string model = AppSettings.ResolveChatModel(AppSettings.ForceThinking);

        // 消息列表：system(底层+用户人设+感知+内心+记忆+日记) + 完整聊天历史（含当前用户消息，跳过正在生成的 aiBubble）
        var messages = BuildHistoryMessages(aiBubble);

        var reqBody = BuildChatBody(model, messages, stream: true);
        var json = JsonSerializer.Serialize(reqBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var resp = await _httpClient.PostAsync(AppSettings.ApiUrl, content);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        // 网络侧全速收流 + 显示侧逐字上屏（见 ConsumeSseAsync）。
        // {cmd}/{api}/{img} 指令在网络侧出现即断流进入对应管线（第一轮过渡语不显示）。
        var res = await ConsumeSseAsync(reader, aiBubble, watchCommands: true);

        if (res.ThinkingStart.HasValue)
            aiBubble.ThinkingSeconds = (DateTime.Now - res.ThinkingStart.Value).TotalSeconds;
        aiBubble.IsThinkingActive = false;

        var rawText = res.Buffer.ToString();

        if (res.CancelledForCommand)
        {
            aiBubble.Content = "";
            await RunTerminalPipeline(prompt, rawText, aiBubble);
            return;
        }
        if (res.CancelledForApi)
        {
            aiBubble.Content = "";
            await RunApiPipeline(prompt, rawText, aiBubble);
            return;
        }
        if (res.CancelledForImage)
        {
            aiBubble.Content = "";
            _ = GenerateImagePipelineAsync(rawText, aiBubble);
            return;
        }

        // {mood}/{avatar} 副作用（显示层已隐藏这些 token，正文本身干净）
        var moods = InstructionParser.Extract(rawText, "mood");
        if (moods.Count > 0) InnerLifeService.SetMood(moods[^1]);
        var avatars = InstructionParser.Extract(rawText, "avatar");
        if (avatars.Count > 0) _ = ApplyAvatarAsync(avatars[^1]);

        // 语音回复：配置了 TTS 且开启自动朗读时，把最终正文读出来
        await SpeakIfEnabledAsync(aiBubble.Content);
    }

    /// <summary>{avatar:"..."}：Ta用文生图给自己画新头像，完成后刷新标题栏。</summary>
    private async Task ApplyAvatarAsync(string desc)
    {
        try
        {
            if (await AvatarService.GenerateAsync(desc))
                imgAvatar.Source = AvatarService.Load();
        }
        catch { }
    }

    /// <summary>
    /// 消费 SSE 流：网络侧全速收流（连接尽快收完关闭，把暴露在「切后台被系统掐线 /
    /// 服务端掐慢连接」风险下的时间从几十秒缩到几秒），显示侧并发地按一秒五十字的
    /// 节奏逐字上屏，打字手感与旧版一致。
    /// watchCommands=true 时，正文一旦出现完整 {cmd:"..."} / {img:"..."} 指令立即断流。
    /// </summary>
    private async Task<SseConsumeResult> ConsumeSseAsync(StreamReader reader, ChatMsg bubble, bool watchCommands)
    {
        var res = new SseConsumeResult();
        var displayCts = new CancellationTokenSource();

        // 显示协程：追着网络侧收到的正文逐字上屏（一秒五十字）；
        // 指令 token（{cmd}/{api}/{img}/{mood}/{avatar}/{say}）不上屏、只做副作用；
        // 网络收完后继续把剩余部分放完
        async Task DisplayAsync()
        {
            int src = 0;
            bool hiding = false;
            try
            {
                while (true)
                {
                    displayCts.Token.ThrowIfCancellationRequested();

                    if (src >= res.Buffer.Length)
                    {
                        if (res.ReadDone) break;
                        await Task.Delay(40); // 等网络再吐一点
                        continue;
                    }

                    if (hiding)
                    {
                        if (res.Buffer[src] == '}') hiding = false;
                        src++;
                        continue;
                    }

                    int m = InstructionParser.MatchLen(res.Buffer, src, res.ReadDone);
                    if (m > 0) { hiding = true; src += m; continue; }
                    if (m < 0 && !res.ReadDone) { await Task.Delay(40); continue; } // 尾部疑似指令前缀，等更多字符

                    bubble.Content += res.Buffer[src];
                    src++;
                    FollowBottomIfNeeded();
                    await Task.Delay(20);
                }
            }
            catch (OperationCanceledException) { }
        }

        var displayTask = DisplayAsync();

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line.Substring(6);
            if (data == "[DONE]") break;
            try
            {
                var chunk = JsonSerializer.Deserialize<StreamChunk>(data);

                // 捕获缓存命中数据（usage 通常出现在流末尾的最后一个 chunk）
                if (chunk?.usage != null)
                {
                    var (hit, miss) = CacheUsage.Extract(chunk.usage);
                    _lastCacheHit = Math.Max(_lastCacheHit, hit);
                    _lastCacheMiss = Math.Max(_lastCacheMiss, miss);
                    UpdateCacheHitLabel();
                }

                // 思考模型（deepseek-reasoner）的推理过程：逐块累积到 Thinking
                var reasoning = chunk?.choices?[0]?.delta?.reasoning_content;
                if (!string.IsNullOrEmpty(reasoning))
                {
                    if (res.ThinkingStart == null) res.ThinkingStart = DateTime.Now;
                    if (!res.ThinkingActive && !res.ContentStarted)
                    {
                        res.ThinkingActive = true;
                        bubble.ThinkingExpanded = true; // 思考中：展开显示思考过程
                        bubble.IsThinkingActive = true; // 思考中：启动流光
                    }
                    bubble.Thinking += reasoning;
                    FollowBottomIfNeeded();
                }

                var delta = chunk?.choices?[0]?.delta?.content;
                if (!string.IsNullOrEmpty(delta))
                {
                    if (!res.ContentStarted)
                    {
                        res.ContentStarted = true;
                        res.ThinkingActive = false;
                        bubble.ThinkingExpanded = false; // 正文开始：自动收起思考过程
                        bubble.IsThinkingActive = false; // 正文开始：停止思考流光
                    }
                    res.Buffer.Append(delta);

                    if (watchCommands)
                    {
                        var raw = res.Buffer.ToString();
                        if (ExtractRunCommands(raw).Count > 0) { res.CancelledForCommand = true; break; }
                        if (InstructionParser.Extract(raw, "api").Count > 0) { res.CancelledForApi = true; break; }
                        if (ExtractImageCommands(raw).Count > 0) { res.CancelledForImage = true; break; }
                    }
                }
            }
            catch { }
        }
        res.ReadDone = true;

        if (res.CancelledForCommand || res.CancelledForApi || res.CancelledForImage)
            displayCts.Cancel(); // 指令场景：正文不显示第一轮过渡语，停掉打字协程

        await displayTask; // 指令场景：立即返回；正常场景：等剩余正文放完
        return res;
    }

    /// <summary>开启 TTS 自动朗读且配置有效时朗读文本；失败静默（不影响文字回复）。</summary>
    private async Task SpeakIfEnabledAsync(string? text)
    {
        try
        {
            if (!AppSettings.TtsAutoPlay || string.IsNullOrWhiteSpace(text)) return;
            await TtsPlayer.PlayAsync(text);
        }
        catch { /* 朗读失败不影响聊天 */ }
    }

    // ─────────── 布局整理（取消玻璃，稳定优先） ───────────

    /// <summary>
    /// 取消玻璃效果后的布局整理：
    /// - 标题栏实色、无玻璃
    /// - 悬浮输入栏去掉纯色背板（透明，只留控件）
    /// - 聊天列表顶部让出标题栏、底部让出发送栏（气泡最低位在发送栏上方）
    /// </summary>
    private void ApplyGlass()
    {
#if ANDROID
        var (statusH, navH) = GetSystemBarInsets();
#else
        int statusH = 0, navH = 0;
#endif

        // 标题栏实色（保持原有观感）
        titleBar.BackgroundColor = Color.FromArgb("#181211");
        titleBar.Padding = new Thickness(14, statusH + 10, 14, 10);

        // 悬浮输入栏：去掉纯色背板，完全透明，只保留内部控件
        inputBar.BackgroundColor = Colors.Transparent;
        inputBar.Stroke = Colors.Transparent;
        inputBar.StrokeThickness = 0;

        // 聊天列表：顶部让出标题栏高度；底部让出发送栏高度 + 边距（气泡最低位在发送栏上方）
        double topM = titleBar.Height > 0 ? titleBar.Height : statusH + 52;
        double inputH = inputBar.Height > 0 ? inputBar.Height : 52;
        msgList.Margin = new Thickness(0, topM, 0, navH + inputH + 16);

        // 布局完成后用实测高度再校正一次
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), () =>
        {
            double top = titleBar.Height > 0 ? titleBar.Height : statusH + 52;
            msgList.Margin = new Thickness(0, top, 0, navH + Math.Max(inputBar.Height, 52) + 16);
        });
    }

#if ANDROID
    /// <summary>取系统条高度（状态栏/导航栏）。旧 API 的弃用属性在 Android 15 前仍返回正确值。</summary>
    private static (int top, int bottom) GetSystemBarInsets()
    {
        try
        {
            var insets = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window?.DecorView?.RootWindowInsets;
            if (insets != null)
                return (insets.SystemWindowInsetTop, insets.SystemWindowInsetBottom);
        }
        catch { }
        return (0, 0);
    }
#endif

    /// <summary>点击悬浮输入栏空白处：聚焦输入框（玻璃栏有内边距，点了边角也能打字）。</summary>
    private void OnInputBarTapped(object? sender, EventArgs e)
    {
        txtInput.Focus();
    }

    /// <summary>是否为可自动重试的瞬时网络错误（连接被掐/中断类）。</summary>
    private static bool IsTransientNetworkError(Exception ex)
    {
        var msg = ex.Message ?? "";
        return msg.Contains("Socket closed", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("connection abort", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Connection reset", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Read error", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把底层网络异常翻译成人话，别再让"Socket closed"吓人。</summary>
    private static string FriendlyNetworkError(Exception ex)
    {
        var msg = ex.Message ?? "";
        if (msg.Contains("Socket closed", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("connection abort", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Read error", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Connection reset", StringComparison.OrdinalIgnoreCase))
            return "网络连接被中断了（切后台太久被系统冻结、或服务端断开都可能）。已自动重试过一次仍失败，检查一下网络后重新发送即可。";
        if (msg.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return "请求超时了，网络可能不稳定，稍后再发一次。";
        return "请求失败：" + msg;
    }

    /// <summary>
    /// 终端交互流水线（全程后台自动，不弹前台）：
    /// 1) 检测 AI 隐藏指令 {cmd:"..."} → 打开三行头部，状态=与Shizuku通信
    /// 2) 拉取终端 → 状态=拉取终端
    /// 3) 执行命令 → 状态=运行终端
    /// 4) 有输出 → 状态=完成，结果填入第三行
    /// 5) 把命令输出回传给模型，结合用户问题生成最终答案。
    /// rawText 是 AI 第一轮的原始输出（含 {cmd:...} 指令）。
    /// 命令场景下第一轮正文不显示，正文仅由第二轮的最终总结生成。
    /// </summary>
    private async Task RunTerminalPipeline(string prompt, string rawText, ChatMsg aiBubble)
    {
        // 打开三行头部小气泡，初始状态；命令场景不显示第一轮正文
        aiBubble.HasTerminalHeader = true;
        aiBubble.IsThinkingActive = false; // 进入终端管线：停止思考流光
        aiBubble.TerminalTitle = "调用 Shizuku 终端命令";
        aiBubble.TerminalStep = "与Shizuku通信";
        aiBubble.TerminalOutput = "";
        aiBubble.Content = "";

        // 从原始文本提取 {cmd:"..."} 隐藏指令
        var commands = ExtractRunCommands(rawText);
        if (commands.Count == 0)
        {
            // 命令提取失败（异常兜底）：给用户一个明确提示，避免空白
            aiBubble.TerminalStep = "完成";
            aiBubble.TerminalOutput = "未解析到命令";
            aiBubble.Content = "未能从回复中解析出要执行的命令。";
            return;
        }

        // 状态机：拉取终端
        aiBubble.TerminalStep = "拉取终端";

        var outputs = new List<(string cmd, string result)>();
        foreach (var cmd in commands)
        {
            // 高危命令：要求用户确认后才执行
            if (CommandSecurity.IsHighRisk(cmd))
            {
                bool ok = await HighRiskConfirmAsync(cmd);
                if (!ok) continue;
            }

            // 状态：运行终端
            aiBubble.TerminalStep = "运行终端";
            if (!ShizukuRunner.Available())
            {
                ShizukuRunner.RequestPermission();
                aiBubble.TerminalOutput = "Shizuku 未授权";
                continue;
            }

            var result = await ShizukuRunner.ExecuteAsync(cmd);
            var combined = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(result.Stdout)) combined.Append(result.Stdout);
            if (!string.IsNullOrWhiteSpace(result.Stderr)) combined.AppendLine("[stderr] " + result.Stderr);
            if (result.ExitCode != 0) combined.Append($"(exit {result.ExitCode})");
            outputs.Add((cmd, combined.ToString()));

            // 先把结果写入第三行，但保持“运行终端”状态一段时间，
            // 让浅绿扫光动画可被看到（验收），再切到“完成”。
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
    }

    /// <summary>
    /// 感知 API 调用流水线（{api:"名"}）：三行气泡实时显示正在调用哪个 API
    /// （如 调用「屏幕使用时间」API），结果回传给模型生成最终回答，
    /// 并存入 TerminalExecLog 作为后续上下文。
    /// </summary>
    private async Task RunApiPipeline(string prompt, string rawText, ChatMsg aiBubble)
    {
        aiBubble.HasTerminalHeader = true;
        aiBubble.IsThinkingActive = false;
        aiBubble.Content = "";

        var ids = InstructionParser.Extract(rawText, "api").Distinct().Take(3).ToList();
        if (ids.Count == 0)
        {
            aiBubble.TerminalTitle = "调用感知 API";
            aiBubble.TerminalStep = "完成";
            aiBubble.TerminalOutput = "未解析到要调用的 API";
            aiBubble.Content = "未能解析出要调用的 API。";
            return;
        }

        var results = new StringBuilder();
        foreach (var id in ids)
        {
            var api = ApiRegistry.Find(id);
            aiBubble.TerminalTitle = api != null ? $"调用「{api.Name}」API" : $"调用 {id}";
            aiBubble.TerminalStep = "调用中";
            aiBubble.TerminalOutput = "";

            var result = await ApiRegistry.ExecuteAsync(id);
            results.AppendLine($"[{api?.Name ?? id}] {result}");

            aiBubble.TerminalOutput = NormalizeSingleLine(result);
            aiBubble.TerminalStep = "完成";
            FollowBottomIfNeeded();
            await Task.Delay(600);
        }

        aiBubble.TerminalExecLog = results.ToString();
        await StreamSummaryReply(prompt, results.ToString(), aiBubble);
    }

    /// <summary>把多行文本压缩为单行：换行/制表符替换为空格，超长截断。保证第三行右括号不单独换行。</summary>
    private static string NormalizeSingleLine(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var oneLine = input
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\t", " ")
            .Trim();
        // 合并连续空格
        while (oneLine.Contains("  ")) oneLine = oneLine.Replace("  ", " ");
        return oneLine.Length > 200 ? oneLine.Substring(0, 200) + "…" : oneLine;
    }

    /// <summary>第二轮：把工具（终端命令/感知 API）结果回传给模型，生成结合用户问题的最终回答。</summary>
    private async Task StreamSummaryReply(string prompt, string toolResults, ChatMsg aiBubble)
    {
        string model = AppSettings.ResolveChatModel(AppSettings.ForceThinking);

        // 工具场景下，最终正文只显示总结回复，不叠加第一轮正文
        aiBubble.Content = "";
        FollowBottomIfNeeded();

        // 完整历史 + 工具结果 + 最终总结指令（让 AI 保持上下文连贯）
        var messages = BuildHistoryMessages(aiBubble);
        messages.Add(new { role = "assistant", content =
                "（工具调用结果如下）\n" + toolResults });
        messages.Add(new { role = "user", content =
                "（刚查到的这些就是咱们私下能看到的小情报，你顺着看一眼就好）\n" +
                toolResults + "\n" +
                "现在像你平时的自己那样接话——保持你们之间一贯的语气和亲密感，别变成汇报。" +
                "用这些信息自然地聊：带出你在意TA的点、顺着关心或打趣一句都可以。别报数据、别说'根据查询'，就像自己看见的一样。纯文本，不要任何 Markdown 符号。" });

        var reqBody = BuildChatBody(model, messages, stream: true);
        var json = JsonSerializer.Serialize(reqBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var resp = await _httpClient.PostAsync(AppSettings.ApiUrl, content);
            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            await ConsumeSseAsync(reader, aiBubble, watchCommands: false);
        }
        catch (Exception ex)
        {
            aiBubble.Content += "\n（最终总结失败：" + ex.Message + "）";
        }

        // 语音回复：终端总结完成后朗读
        await SpeakIfEnabledAsync(aiBubble.Content);
    }

    /// <summary>
    /// 组装 chat/completions 请求体：模型返回了推理档位且用户选了档时，
    /// 附带 reasoning_effort 参数；否则保持最简请求。
    /// </summary>
    private static Dictionary<string, object> BuildChatBody(string model, List<object> messages, bool stream)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["stream"] = stream,
            ["messages"] = messages
        };
        if (AppSettings.ReasoningSupported)
        {
            var effort = AppSettings.ReasoningChoice;
            if (!string.IsNullOrWhiteSpace(effort))
                body["reasoning_effort"] = effort;
        }
        return body;
    }

    /// <summary>刷新顶部推理等级按钮：仅当模型返回了推理档位时显示，文案为当前档位。</summary>
    private void UpdateReasoningChip()
    {
        var options = AppSettings.GetReasoningOptions();
        bool show = AppSettings.ReasoningSupported && options.Count > 0;
        reasonChip.IsVisible = show;
        if (show)
        {
            var choice = AppSettings.ReasoningChoice;
            reasonLbl.Text = string.IsNullOrWhiteSpace(choice) ? "思考:关" : $"思考:{choice}";
        }
    }

    /// <summary>点击推理按钮：弹出模型返回的档位列表（档位数=模型返回数），选择后立即生效。</summary>
    private async void OnReasoningChipTapped(object? sender, EventArgs e)
    {
        var options = AppSettings.GetReasoningOptions();
        if (options.Count == 0) return;

        var items = new List<string> { "关闭" };
        items.AddRange(options);
        var picked = await DisplayActionSheet("推理等级", "取消", null, items.ToArray());
        if (string.IsNullOrEmpty(picked) || picked == "取消") return;

        AppSettings.ReasoningChoice = picked == "关闭" ? "" : picked;
        UpdateReasoningChip();
    }

    /// <summary>提取 AI 答复中的隐藏指令 {cmd:"..."} 中的命令。</summary>
    private static List<string> ExtractRunCommands(string content) => InstructionParser.Extract(content, "cmd");

    /// <summary>提取 AI 回复中的 {img:"描述"} 指令。</summary>
    private static List<string> ExtractImageCommands(string content) => InstructionParser.Extract(content, "img");
    private async Task<bool> HighRiskConfirmAsync(string command)
    {
        // 先提示「高风险，5 秒倒计时」
        await DisplayAlert("⚠️ 高危操作",
            $"AI 请求执行高危命令：\n\n{command}\n\n5 秒后弹出最终确认。",
            "知道了");

        // 5 秒倒计时（简单延迟）
        for (int i = 5; i >= 1; i--)
        {
            await DisplayAlert("倒计时", $"高危命令将在 {i} 秒后可确认。", "等待");
            if (i > 1) await Task.Delay(1000);
        }

        // 最终确认
        return await DisplayAlert("⚠️ 最终确认",
            $"确认执行这条高危命令吗？\n\n{command}\n\n此操作可能不可恢复。",
            "确认执行", "取消");
    }

    /// <summary>点击思考过程头部：切换展开/收起。</summary>
    private void OnThinkingHeaderTapped(object? sender, EventArgs e)
    {
        if (sender is BindableObject bo && bo.BindingContext is ChatMsg msg)
            msg.ThinkingExpanded = !msg.ThinkingExpanded;
    }

    /// <summary>收起键盘（发送消息后自动隐藏）。</summary>
    private void HideKeyboard()
    {
#if ANDROID
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        var view = activity?.CurrentFocus;
        if (view != null)
        {
            var imm = (Android.Views.InputMethods.InputMethodManager?)
                activity?.GetSystemService(Android.Content.Context.InputMethodService);
            imm?.HideSoftInputFromWindow(view.WindowToken, 0);
            view.ClearFocus();
        }
#endif
        txtInput.Unfocus();
    }

    private void ScrollToBottom()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (Messages.Count > 0)
            {
                msgList.ScrollTo(Messages[^1], null, ScrollToPosition.MakeVisible, true);
            }
        });
    }

    /// <summary>无动画直接定位到最后一条消息（打开页面/恢复时用，避免先到顶再滚的视觉跳动）。</summary>
    private void ScrollToBottomInstant()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (Messages.Count > 0)
            {
                msgList.ScrollTo(Messages.Count - 1, position: ScrollToPosition.End, animate: false);
            }
        });
    }

    /// <summary>监听滚动：判断用户是否停留在最底部（用于决定是否跟随输出）。</summary>
    private void OnMsgListScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
#if ANDROID
        // 还能向下滚动 = 未在底部；滚不动 = 已在底部
        if (msgList.Handler?.PlatformView is AndroidX.RecyclerView.Widget.RecyclerView recycler)
        {
            _followBottom = !recycler.CanScrollVertically(1);
            return;
        }
#endif
        // 非 Android 兜底：最后可见项接近消息末尾视为在底部
        _followBottom = e.LastVisibleItemIndex >= Messages.Count - 1;
    }

    /// <summary>
    /// 跟随输出滚动：仅当用户停留在最底部时，把最新内容保持在屏幕最下方；
    /// 用户上滑浏览历史时保持当前高度，绝不强制拉回（避免跳动）。
    /// </summary>
    private void FollowBottomIfNeeded()
    {
        if (!_followBottom) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (Messages.Count > 0)
            {
                msgList.ScrollTo(Messages.Count - 1, position: ScrollToPosition.End, animate: false);
            }
        });
    }

    /// <summary>长按开始：记录按下的时间与被按消息。</summary>
    private void OnMsgPointerPressed(object? sender, EventArgs e)
    {
        _pressTime = DateTime.Now;
        _pressedMsg = (sender as BindableObject)?.BindingContext as ChatMsg;
    }

    /// <summary>长按结束：若超过阈值则复制该消息内容到剪贴板。</summary>
    private async void OnMsgPointerReleased(object? sender, EventArgs e)
    {
        if ((DateTime.Now - _pressTime).TotalMilliseconds < LongPressMs)
        {
            _pressedMsg = null;
            return;
        }
        if (_pressedMsg == null) return;
        var msg = _pressedMsg;
        _pressedMsg = null;

        var text = msg.Content;
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            await Clipboard.Default.SetTextAsync(text);
            var preview = text.Length > 30 ? text[..30] + "…" : text;
            await DisplayAlert("已复制", preview, "知道了");
        }
        catch { }
    }

    // ────────────────────────── 自动上下文压缩 ──────────────────────────

    /// <summary>检查当前上下文占用是否超过阈值，是则触发 AI 总结压缩。</summary>
    private async Task CheckAndAutoCompressAsync()
    {
        if (!AppSettings.AutoCompressEnabled) return;
        if (!await _compressLock.WaitAsync(0)) return; // 已在压缩中，跳过

        try
        {
            int used = EstimateTokens();
            int max = AppSettings.EffectiveMaxTokens;
            double ratio = (double)used / max;
            int threshold = AppSettings.AutoCompressThreshold;

            if (ratio * 100 < threshold) return;

            // 确保最少保留 4 条消息（最近两轮对话，2 问 2 答）才压缩
            const int keepCount = 4;
            if (Messages.Count <= keepCount + 1) return;

            await AutoCompressAsync(keepCount);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AutoCompress] 压缩失败: {ex.Message}");
        }
        finally
        {
            _compressLock.Release();
        }
    }

    /// <summary>调用 AI 总结旧消息，替换为一条摘要消息。</summary>
    private async Task AutoCompressAsync(int keepCount)
    {
        // 要压缩的消息：除最后 keepCount 条之外的全部
        int compressEnd = Messages.Count - keepCount;
        var toCompress = Messages.Take(compressEnd).ToList();
        if (toCompress.Count == 0) return;

        // 构建对话原文供 AI 总结
        var dialog = new StringBuilder();
        foreach (var m in toCompress)
        {
            string role = m.IsUser ? "用户" : "AI";
            dialog.AppendLine($"{role}：{m.Content}");
        }

        // 调用 API 总结（非流式，简洁快速）
        string model = AppSettings.ResolveChatModel(AppSettings.ForceThinking);

        var summaryPrompt = new
        {
            model,
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = "你是一个对话摘要助手。请将下面的对话记录总结为一段简短精炼的摘要（保留关键信息，去除冗余，用中文）。摘要要能独立阅读，让后续对话者理解已讨论过的内容。不要加任何格式标记，纯文本。" },
                new { role = "user", content = dialog.ToString() }
            },
            max_tokens = 1024
        };

        var json = JsonSerializer.Serialize(summaryPrompt);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var resp = await _httpClient.PostAsync(AppSettings.ApiUrl, content);
        resp.EnsureSuccessStatusCode();
        var respJson = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(respJson);
        var summary = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(summary)) return;

        // 在主线程上替换消息
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // 移除旧消息（先收集数据库 Id，用于删除对应行）
            var removedIds = new List<int>();
            for (int i = compressEnd - 1; i >= 0; i--)
            {
                if (Messages[i].Id != 0) removedIds.Add(Messages[i].Id);
                Messages.RemoveAt(i);
            }

            // 插入一条压缩摘要消息（标记为 AI 消息，但 content 以 📋 前缀标识）
            var summaryMsg = new ChatMsg
            {
                Content = $"📋 历史摘要：{summary}",
                IsUser = false
            };
            Messages.Insert(0, summaryMsg);

            _ = ChatStore.Instance.DeleteMessagesAsync(removedIds);
            _ = ChatStore.Instance.SaveMessageAsync(summaryMsg);

            UpdateContextLabel();
            ScrollToBottom();
        });
    }

    // ────────────────────────── 多模态：语音 / 图片 / 文生图 ──────────────────────────

    /// <summary>点击语音按钮：系统语音识别优先，失败走听觉模型 API。</summary>
    private async void OnVoiceClicked(object? sender, EventArgs e)
    {
        try
        {
            string? text = await RecognizeVoiceAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                await DisplayAlert("语音识别", "没有识别到内容", "确定");
                return;
            }
            txtInput.Text = text;
            await DisplayAlert("语音识别", $"识别结果：{text}\n\n确认后点击发送。", "确定");
        }
        catch (Exception ex)
        {
            await DisplayAlert("语音识别失败", ex.Message, "确定");
        }
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
        }
    }

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
            if (Math.Abs(_thinkingSeconds - value) < 0.1) return;
            _thinkingSeconds = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThinkingSeconds)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThinkingStatusText)));
        }
    }

    /// <summary>思考状态文字：思考已完成 用时x秒 / 用时xx分xx秒。</summary>
    [JsonIgnore]
    public string ThinkingStatusText
    {
        get
        {
            if (_thinkingSeconds < 60)
                return $"思考已完成 用时{_thinkingSeconds:F0}秒";
            int min = (int)_thinkingSeconds / 60;
            int sec = (int)_thinkingSeconds % 60;
            return $"思考已完成 用时{min}分{sec}秒";
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
    public UsageItem? usage { get; set; }
}

/// <summary>ConsumeSseAsync 的收流结果。</summary>
public sealed class SseConsumeResult
{
    /// <summary>网络侧收到的完整正文（含尚未上屏的部分）。</summary>
    public StringBuilder Buffer { get; } = new();
    public DateTime? ThinkingStart { get; set; }
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
        var matches = bundle?.GetStringArrayList(Android.Speech.SpeechRecognizer.ResultsRecognition);
        if (matches == null) return;
        foreach (var m in matches)
        {
            if (!string.IsNullOrEmpty(m)) _sb.Append(m);
        }
    }
}
#endif