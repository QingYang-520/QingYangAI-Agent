using System.Text.Json;

namespace 青阳AI;

/// <summary>
/// 全局应用设置。所有可持久化的用户偏好通过 Preferences 存储。
/// </summary>
public static class AppSettings
{
    public const string DefaultApiKey = "";
    public const string DefaultName = "青阳AI";
    public const string DefaultApiUrl = "";
    public const string DefaultModel = "";

    /// <summary>模型配置套数（配置1/2/3）。</summary>
    public const int ModelConfigCount = 3;

    /// <summary>当前激活的模型配置索引（0=配置1, 1=配置2, 2=配置3）。</summary>
    public static int ActiveModelConfig
    {
        get => Preferences.Default.Get("ActiveModelConfig", 0);
        set => Preferences.Default.Set("ActiveModelConfig", Math.Clamp(value, 0, ModelConfigCount - 1));
    }

    /// <summary>旧版单套配置迁移标记（避免重复迁移）。</summary>
    private const string MigrationFlag = "ModelConfigMigrated_v2";

    /// <summary>把旧版无前缀配置键迁移到「配置1」(MC0_*)，仅执行一次。</summary>
    public static void MigrateLegacyModelConfig()
    {
        if (Preferences.Default.Get(MigrationFlag, false)) return;

        // 旧键 → 新字段名（迁移到配置1，索引 0）
        var map = new (string oldKey, string field)[]
        {
            ("ApiKey", "ApiKey"),
            ("ApiUrl", "ApiUrl"),
            ("ModelMaxTokens", "ModelMaxTokens"),
            ("Model", "Model"),
            ("ImgApiUrl", "ImgApiUrl"),
            ("ImgApiKey", "ImgApiKey"),
            ("ImgModel", "ImgModel"),
            ("AudioApiUrl", "AudioApiUrl"),
            ("AudioApiKey", "AudioApiKey"),
            ("AudioModel", "AudioModel"),
            ("VisionApiUrl", "VisionApiUrl"),
            ("VisionApiKey", "VisionApiKey"),
            ("VisionModel", "VisionModel"),
            ("TtsApiUrl", "TtsApiUrl"),
            ("TtsApiKey", "TtsApiKey"),
            ("TtsModel", "TtsModel"),
        };

        try
        {
            foreach (var (oldKey, field) in map)
            {
                if (Preferences.Default.ContainsKey(oldKey))
                {
                    object? raw = Preferences.Default.Get(oldKey, field == "ModelMaxTokens" ? (object)0 : (object)"");
                    if (field == "ModelMaxTokens")
                        Preferences.Default.Set(FieldKey(0, field), (int)raw);
                    else
                        Preferences.Default.Set(FieldKey(0, field), (string)raw);
                }
            }
        }
        catch { /* 迁移失败不影响运行 */ }

        Preferences.Default.Set(MigrationFlag, true);
    }

    // 按配置索引存取字段：键为 MC{idx}_{field}
    private static string FieldKey(int idx, string field) => $"MC{idx}_{field}";
    private static string GetField(int idx, string field, string def = "") =>
        Preferences.Default.Get(FieldKey(idx, field), def);
    private static void SetField(int idx, string field, string value) =>
        Preferences.Default.Set(FieldKey(idx, field), string.IsNullOrWhiteSpace(value) ? "" : value.Trim());

    // 以下属性均指向"当前激活配置"的对应字段，供聊天逻辑直接使用
    public static string ApiKey
    {
        get => GetField(ActiveModelConfig, "ApiKey", DefaultApiKey);
        set => SetField(ActiveModelConfig, "ApiKey", value);
    }

    /// <summary>API 地址（OpenAI 兼容的 chat/completions 端点）。</summary>
    public static string ApiUrl
    {
        get => GetField(ActiveModelConfig, "ApiUrl", DefaultApiUrl);
        set => SetField(ActiveModelConfig, "ApiUrl", value);
    }

    /// <summary>
    /// 当前选中模型的最大上下文 token 数。
    /// 来源：/models 返回的 max_tokens 或 max_output_tokens 字段（两者都有取较大值）。
    /// 0 表示未获取到。
    /// </summary>
    public static int ModelMaxTokens
    {
        get => Preferences.Default.Get(FieldKey(ActiveModelConfig, "ModelMaxTokens"), 0);
        set => Preferences.Default.Set(FieldKey(ActiveModelConfig, "ModelMaxTokens"), value);
    }

    /// <summary>
    /// 当 API 未返回上下文上限时使用的默认上限（入梦 / 中转等接口常不返回该字段）。
    /// </summary>
    public const int ContextFallbackLimit = 32768;

    /// <summary>
    /// 获取用于界面显示的分母（最高上下文）。
    /// 有真实值用之；否则回退到默认上限，避免显示 "?"。
    /// </summary>
    public static int EffectiveMaxTokens => ModelMaxTokens > 0 ? ModelMaxTokens : ContextFallbackLimit;

    /// <summary>
    /// 把 token 数格式化为人性化文本：
    /// 小于 1K → 整数；≥1K → "x.yK"；≥1M → "x.yM"。
    /// </summary>
    public static string FormatTokens(int tokens)
    {
        if (tokens < 1000) return tokens.ToString();
        if (tokens < 1_000_000)
        {
            double k = tokens / 1000.0;
            return k >= 10 ? $"{Math.Round(k)}K" : $"{k.ToString("0.#")}K";
        }
        double m = tokens / 1_000_000.0;
        return m >= 10 ? $"{Math.Round(m)}M" : $"{m.ToString("0.#")}M";
    }

    /// <summary>当前使用的模型。</summary>
    public static string Model
    {
        get => GetField(ActiveModelConfig, "Model", DefaultModel);
        set => SetField(ActiveModelConfig, "Model", value);
    }

    /// <summary>强制思考时使用的模型（空 = 沿用 deepseek-reasoner 旧默认；换其他厂商时填对方的思考模型名）。</summary>
    public static string ThinkingModel
    {
        get => GetField(ActiveModelConfig, "ThinkingModel");
        set => SetField(ActiveModelConfig, "ThinkingModel", value);
    }

    /// <summary>统一解析聊天模型：强制思考走思考模型，普通对话走所选模型。</summary>
    public static string ResolveChatModel(bool forceThinking)
    {
        if (forceThinking)
        {
            if (!string.IsNullOrWhiteSpace(ThinkingModel)) return ThinkingModel;
            return "deepseek-reasoner"; // 旧默认，兼容 DeepSeek；换厂商请填思考模型
        }
        return string.IsNullOrWhiteSpace(Model) ? "deepseek-chat" : Model;
    }

    // ─── 推理等级（动态能力：由 /models 返回决定，聊天页顶部按钮选择）───

    /// <summary>当前模型是否返回了推理档位能力（获取模型列表时解析）。</summary>
    public static bool ReasoningSupported
    {
        get => Preferences.Default.Get(FieldKey(ActiveModelConfig, "ReasoningSupported"), false);
        set => Preferences.Default.Set(FieldKey(ActiveModelConfig, "ReasoningSupported"), value);
    }

    /// <summary>模型返回的推理档位列表（JSON 数组，如 ["low","medium","high"]）。</summary>
    public static string ReasoningOptionsJson
    {
        get => GetField(ActiveModelConfig, "ReasoningOptionsJson");
        set => SetField(ActiveModelConfig, "ReasoningOptionsJson", value);
    }

    /// <summary>解析出的推理档位列表。</summary>
    public static List<string> GetReasoningOptions()
    {
        try
        {
            var json = ReasoningOptionsJson;
            if (string.IsNullOrWhiteSpace(json)) return new List<string>();
            using var doc = JsonDocument.Parse(json);
            var list = new List<string>();
            foreach (var v in doc.RootElement.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                    list.Add(v.GetString()!.Trim());
            return list;
        }
        catch { return new List<string>(); }
    }

    /// <summary>当前选择的推理档位（空 = 关闭/跟随模型默认）。</summary>
    public static string ReasoningChoice
    {
        get => GetField(ActiveModelConfig, "ReasoningChoice");
        set => SetField(ActiveModelConfig, "ReasoningChoice", value);
    }

    // ─── 相伴天数 ───

    /// <summary>首次相遇时间文本（ChatStore 初始化时写入一次）。</summary>
    public static string CompanionSinceText => Preferences.Default.Get("CompanionSince", "");

    /// <summary>已陪伴天数（含今天，至少 1 天）。</summary>
    public static int CompanionDays
    {
        get
        {
            try
            {
                if (DateTime.TryParse(CompanionSinceText, out var d))
                    return Math.Max(1, (DateTime.Today - d.Date).Days + 1);
            }
            catch { }
            return 1;
        }
    }

    // ─── 界面画质（玻璃效果） ───

    private static readonly string[] GlassEffects = { "关闭", "毛玻璃", "亚克力", "液态玻璃" };
    /// <summary>界面画质：关闭 / 毛玻璃 / 亚克力 / 液态玻璃（控制聊天栏与标题栏的玻璃质感）。</summary>
    public static string GlassEffect
    {
        get => Preferences.Default.Get("GlassEffect", "关闭");
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? "关闭" : value.Trim();
            if (!GlassEffects.Contains(v)) v = "关闭";
            Preferences.Default.Set("GlassEffect", v);
        }
    }

    /// <summary>从 API URL 推导模型列表接口（/chat/completions → /models）。</summary>
    public static string GetModelsEndpoint()
    {
        var url = ApiUrl.Trim();
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return url[..^"/chat/completions".Length] + "/models";
        return url.TrimEnd('/') + "/models";
    }

    // ─────────── 多模态模型配置（文生图 / 听觉 / 视觉 / 文字转语音）───────────

    /// <summary>文生图模型：生成图片（返回 base64 数据）。</summary>
    public static string ImgApiUrl { get => GetField(ActiveModelConfig, "ImgApiUrl"); set => SetField(ActiveModelConfig, "ImgApiUrl", value); }
    public static string ImgApiKey { get => GetField(ActiveModelConfig, "ImgApiKey"); set => SetField(ActiveModelConfig, "ImgApiKey", value); }
    public static string ImgModel { get => GetField(ActiveModelConfig, "ImgModel"); set => SetField(ActiveModelConfig, "ImgModel", value); }
    public static bool ImgEnabled => !string.IsNullOrWhiteSpace(ImgApiUrl) && !string.IsNullOrWhiteSpace(ImgApiKey);

    /// <summary>
    /// 生图后端类型：`auto`（默认，自动探测）/ `openai`（OpenAI 兼容 + Responses partial_images）
    /// / `comfy`（ComfyUI）/ `sd`（SD-WebUI / Forge）。
    /// 只有支持实时预览的后端才能往聊天图框里推中间帧。
    /// </summary>
    public static string ImgBackend { get => GetField(ActiveModelConfig, "ImgBackend", "auto"); set => SetField(ActiveModelConfig, "ImgBackend", value); }

    /// <summary>
    /// ComfyUI 工作流（API 格式 JSON）。留空就用内置默认 SD1.5 工作流。
    /// 里面有 `%PROMPT%` 占位符会被替换成用户的描述。
    /// </summary>
    public static string ComfyWorkflow { get => GetField(ActiveModelConfig, "ComfyWorkflow"); set => SetField(ActiveModelConfig, "ComfyWorkflow", value); }

    /// <summary>听觉模型：语音转文字（自动识别语音消息）。</summary>
    public static string AudioApiUrl { get => GetField(ActiveModelConfig, "AudioApiUrl"); set => SetField(ActiveModelConfig, "AudioApiUrl", value); }
    public static string AudioApiKey { get => GetField(ActiveModelConfig, "AudioApiKey"); set => SetField(ActiveModelConfig, "AudioApiKey", value); }
    public static string AudioModel { get => GetField(ActiveModelConfig, "AudioModel"); set => SetField(ActiveModelConfig, "AudioModel", value); }
    public static bool AudioEnabled => !string.IsNullOrWhiteSpace(AudioApiUrl) && !string.IsNullOrWhiteSpace(AudioApiKey);

    /// <summary>视觉模型：图片理解（识别图片内容）。</summary>
    public static string VisionApiUrl { get => GetField(ActiveModelConfig, "VisionApiUrl"); set => SetField(ActiveModelConfig, "VisionApiUrl", value); }
    public static string VisionApiKey { get => GetField(ActiveModelConfig, "VisionApiKey"); set => SetField(ActiveModelConfig, "VisionApiKey", value); }
    public static string VisionModel { get => GetField(ActiveModelConfig, "VisionModel"); set => SetField(ActiveModelConfig, "VisionModel", value); }
    public static bool VisionEnabled => !string.IsNullOrWhiteSpace(VisionApiUrl) && !string.IsNullOrWhiteSpace(VisionApiKey);

    /// <summary>文字转语音模型：AI 语音回复。</summary>
    public static string TtsApiUrl { get => GetField(ActiveModelConfig, "TtsApiUrl"); set => SetField(ActiveModelConfig, "TtsApiUrl", value); }
    public static string TtsApiKey { get => GetField(ActiveModelConfig, "TtsApiKey"); set => SetField(ActiveModelConfig, "TtsApiKey", value); }
    public static string TtsModel { get => GetField(ActiveModelConfig, "TtsModel"); set => SetField(ActiveModelConfig, "TtsModel", value); }
    public static bool TtsEnabled => !string.IsNullOrWhiteSpace(TtsApiUrl) && !string.IsNullOrWhiteSpace(TtsApiKey);

    /// <summary>TTS 音色（OpenAI 兼容命名：alloy/echo/fable/onyx/nova/shimmer 等）。</summary>
    public static string TtsVoice
    {
        get => GetField(ActiveModelConfig, "TtsVoice", "alloy");
        set => SetField(ActiveModelConfig, "TtsVoice", value);
    }

    /// <summary>收到 AI 回复后是否自动朗读（默认关：避免每次回复都多发一个 TTS 请求，需要时再开）。</summary>
    public static bool TtsAutoPlay
    {
        get => Preferences.Default.Get("TtsAutoPlay", false);
        set => Preferences.Default.Set("TtsAutoPlay", value);
    }

    // ─────────── 主动关心（后台定时，安静守则防烦）───────────

    /// <summary>主动关心总开关（默认关闭，尊重用户）。</summary>
    public static bool CareEnabled
    {
        get => Preferences.Default.Get("CareEnabled", false);
        set => Preferences.Default.Set("CareEnabled", value);
    }

    /// <summary>免打扰开始分钟数（0~1439，默认 1410 = 23:30）。</summary>
    public static int CareQuietStartMin
    {
        get => Preferences.Default.Get("CareQuietStartMin", 23 * 60 + 30);
        set => Preferences.Default.Set("CareQuietStartMin", Math.Clamp(value, 0, 1439));
    }

    /// <summary>免打扰结束分钟数（默认 480 = 08:00）。跨零点区间自动处理。</summary>
    public static int CareQuietEndMin
    {
        get => Preferences.Default.Get("CareQuietEndMin", 8 * 60);
        set => Preferences.Default.Set("CareQuietEndMin", Math.Clamp(value, 0, 1439));
    }

    /// <summary>每天最多主动说几条（默认 3）。</summary>
    public static int CareDailyCap
    {
        get => Preferences.Default.Get("CareDailyCap", 3);
        set => Preferences.Default.Set("CareDailyCap", Math.Clamp(value, 1, 10));
    }

    /// <summary>是否允许Ta感知前台应用（需要 Shizuku 授权；默认关闭，感知需透明授权）。</summary>
    public static bool CareSenseEnabled
    {
        get => Preferences.Default.Get("CareSenseEnabled", false);
        set => Preferences.Default.Set("CareSenseEnabled", value);
    }

    /// <summary>当前时间是否处于免打扰时段（支持跨零点区间，如 23:30~08:00）。</summary>
    public static bool IsInQuietHours(DateTime now)
    {
        int t = now.Hour * 60 + now.Minute;
        int s = CareQuietStartMin, e = CareQuietEndMin;
        if (s == e) return false;
        return s < e ? (t >= s && t < e) : (t >= s || t < e);
    }

    // ─────────── Agent 循环执行模式 ───────────

    /// <summary>Agent 循环执行模式开关（收到指令 → 思考 → 执行动作 → 拿结果 → 再思考 → 继续，直到任务完成）。</summary>
    public static bool AgentLoopEnabled
    {
        get => Preferences.Default.Get("AgentLoopEnabled", false);
        set => Preferences.Default.Set("AgentLoopEnabled", value);
    }

    /// <summary>
    /// Agent 循环时限档位（1~5，默认 1）。
    /// 1=10分钟 2=20分钟 3=30分钟 4=40分钟 5=不限制。
    /// </summary>
    public static int AgentTimeLimitLevel
    {
        get => Preferences.Default.Get("AgentTimeLimitLevel", 1);
        set => Preferences.Default.Set("AgentTimeLimitLevel", Math.Clamp(value, 1, 5));
    }

    /// <summary>档位 → 分钟数（0 表示不限制）。</summary>
    public static int AgentTimeLimitMinutes => AgentTimeLimitLevel switch
    {
        1 => 10,
        2 => 20,
        3 => 30,
        4 => 40,
        _ => 0
    };

    /// <summary>档位显示文本。</summary>
    public static string AgentTimeLimitText
    {
        get
        {
            var min = AgentTimeLimitMinutes;
            return min <= 0 ? "不限制" : $"{min} 分钟";
        }
    }

    /// <summary>单轮循环最多思考几轮（防止模型不收敛时无限空转，时限之外的第二道保险）。</summary>
    public static int AgentMaxIterations
    {
        get => Preferences.Default.Get("AgentMaxIterations", 24);
        set => Preferences.Default.Set("AgentMaxIterations", Math.Clamp(value, 1, 100));
    }

    // ─────────── 文件访问权限（Agent 用；聊天页盾牌气泡控制） ───────────

    /// <summary>文件权限级别：0=未授权 1=仅读工作区 2=仅改工作区 3=读全盘 4=改全盘。</summary>
    public static int FileAccessLevel
    {
        get => Preferences.Default.Get("FileAccessLevel", 0);
        set => Preferences.Default.Set("FileAccessLevel", Math.Clamp(value, 0, 4));
    }

    /// <summary>是否允许 AI 使用浏览器（联网抓取/搜索）。</summary>
    public static bool BrowserPermission
    {
        get => Preferences.Default.Get("BrowserPermission", true);
        set => Preferences.Default.Set("BrowserPermission", value);
    }

    // ─────────── AI 浏览器（设置页「🌐 AI 浏览器」分区）───────────

    /// <summary>是否允许 AI 用真浏览器（跑 JS / 点按钮 / 填表单 / 登录）。关掉只剩 {browse:} 读静态页。</summary>
    public static bool AiBrowserEnabled
    {
        get => Preferences.Default.Get("AiBrowserEnabled", true);
        set => Preferences.Default.Set("AiBrowserEnabled", value);
    }

    /// <summary>是否保存 Cookie。开着 = 保留登录态；关掉 = 每次都是干净会话。</summary>
    public static bool BrowserSaveCookies
    {
        get => Preferences.Default.Get("BrowserSaveCookies", true);
        set => Preferences.Default.Set("BrowserSaveCookies", value);
    }

    /// <summary>User-Agent：空 = 默认手机 UA；"desktop" = 桌面 Chrome；其他 = 自定义原串。</summary>
    public static string BrowserUserAgent
    {
        get => Preferences.Default.Get("BrowserUserAgent", "");
        set => Preferences.Default.Set("BrowserUserAgent", value ?? "");
    }

    public const string UaMobile =
        "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36";
    public const string UaDesktop =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    /// <summary>真正要用的 UA 串。</summary>
    public static string EffectiveUserAgent => BrowserUserAgent switch
    {
        "" or "mobile" => UaMobile,
        "desktop" => UaDesktop,
        _ => BrowserUserAgent
    };

    /// <summary>是否加载图片。关掉省流量（Ta 主要读文字）。</summary>
    public static bool BrowserLoadImages
    {
        get => Preferences.Default.Get("BrowserLoadImages", true);
        set => Preferences.Default.Set("BrowserLoadImages", value);
    }

    /// <summary>下载落到哪："" = 工作区；"download" = 公共 Download 目录。</summary>
    public static string BrowserDownloadDir
    {
        get => Preferences.Default.Get("BrowserDownloadDir", "");
        set => Preferences.Default.Set("BrowserDownloadDir", value ?? "");
    }

    /// <summary>单个文件下载上限（MB）。</summary>
    public static int BrowserMaxDownloadMb
    {
        get => Preferences.Default.Get("BrowserMaxDownloadMb", 300);
        set => Preferences.Default.Set("BrowserMaxDownloadMb", Math.Clamp(value, 1, 4096));
    }

    // ── 浏览历史（最近 50 条）──

    public static string BrowserHistoryJson
    {
        get => Preferences.Default.Get("BrowserHistoryJson", "[]");
        set => Preferences.Default.Set("BrowserHistoryJson", value ?? "[]");
    }

    public static List<string> BrowserHistory()
    {
        try { return JsonSerializer.Deserialize<List<string>>(BrowserHistoryJson) ?? new(); }
        catch { return new(); }
    }

    /// <summary>记一条浏览/下载历史（最新的在最前）。</summary>
    public static void AddBrowserHistory(string what)
    {
        if (string.IsNullOrWhiteSpace(what)) return;
        try
        {
            var list = BrowserHistory();
            list.Insert(0, DateTime.Now.ToString("MM-dd HH:mm") + "  " + what.Trim());
            if (list.Count > 50) list.RemoveRange(50, list.Count - 50);
            BrowserHistoryJson = JsonSerializer.Serialize(list);
        }
        catch { }
    }

    public static void ClearBrowserHistory() => BrowserHistoryJson = "[]";

    // ── 用户协议版本 ──

    /// <summary>
    /// 用户已同意的协议版本号（0 = 从没同意过）。
    /// 与 <see cref="AgreementContent.Version"/> 比较，决定要不要重新弹协议页。
    /// </summary>
    public static int AgreedAgreementVersion
    {
        get => Preferences.Default.Get("AgreedAgreementVersion", 0);
        set => Preferences.Default.Set("AgreedAgreementVersion", value);
    }

    /// <summary>
    /// 是否已经同意过**当前版本**的协议。
    /// false → 启动时该弹协议页（新用户没同意过，或老用户遇到过协议更新）。
    /// </summary>
    public static bool AgreementUpToDate =>
        Preferences.Default.Get("UserAgreePrivacy", false)
        && AgreedAgreementVersion >= AgreementContent.Version;

    // ── 流式诊断（排查"为什么不是逐块上屏"用）──

    /// <summary>上次流式请求的诊断：首块延迟 / 块数 / 总时长。</summary>
    public static string LastStreamDiag { get; set; } = "（还没发过请求）";

    /// <summary>
    /// 流式兼容模式：聊天改用纯托管 SocketsHttpHandler，绕开平台原生网络栈。
    /// 有些 ROM / 网络栈会把响应偷偷缓冲起来，那样流式就废了。**改完要重启 App。**
    /// </summary>
    public static bool UseManagedHttpStack
    {
        get => Preferences.Default.Get("UseManagedHttpStack", false);
        set => Preferences.Default.Set("UseManagedHttpStack", value);
    }

    /// <summary>完全访问：放开全部限制（含文件删除、越界路径、系统目录警告）。</summary>
    public static bool FullAccess
    {
        get => Preferences.Default.Get("FullAccess", false);
        set => Preferences.Default.Set("FullAccess", value);
    }

    /// <summary>
    /// 工作区目录（用户可自定义）。空则按下面三级回退自动选。
    /// </summary>
    public static string WorkspacePath
    {
        get => Preferences.Default.Get("WorkspacePath", "");
        set => Preferences.Default.Set("WorkspacePath", value?.Trim() ?? "");
    }

    /// <summary>
    /// 解析出实际使用的工作区绝对路径，三级回退（从高到低取第一个可用的）：
    ///   ① 用户在设置里填的自定义路径；
    ///   ② 已授予系统「所有文件访问」→ 公共存储 /storage/emulated/0/QingYangAI/WorkSpace
    ///      （用户能在文件管理器里看到；中间任何一级目录缺失都会自动补建）；
    ///   ③ 兜底：APP 私有沙盒 workspace/（外部不可见）。
    /// </summary>
    public static string EffectiveWorkspacePath
    {
        get
        {
            // ① 自定义优先（用户可能填了还不存在的目录，这里顺手补齐）
            var custom = WorkspacePath;
            if (!string.IsNullOrWhiteSpace(custom))
            {
                try
                {
                    if (!Directory.Exists(custom))
                        Directory.CreateDirectory(custom);
                }
                catch { /* 建不出来也不拦，后续读写会给出准确错误 */ }
                return custom;
            }

            // ② 有全盘权限 → 落到公共存储，保证用户找得到文件
            if (StorageAccess.IsAllFilesGranted())
            {
                var pub = StorageAccess.DefaultPublicWorkspace;
                if (!string.IsNullOrEmpty(pub))
                {
                    // 首次访问（或被用户清理过）时把目录补齐，避免后续读写撞"目录不存在"
                    if (!StorageAccess.DefaultWorkspaceExists())
                        StorageAccess.EnsureDefaultWorkspace(out _);
                    return pub;
                }
            }

            // ③ 沙盒兜底
            string root;
            try { root = FileSystem.AppDataDirectory; }
            catch { root = Path.GetTempPath(); }
            var sandbox = Path.Combine(root, "workspace");
            try
            {
                if (!Directory.Exists(sandbox)) Directory.CreateDirectory(sandbox);
            }
            catch { }
            return sandbox;
        }
    }

    /// <summary>文件权限级别的中文描述（供 UI 与提示词共用）。</summary>
    public static string FileAccessDesc => FileAccessLevel switch
    {
        1 => "仅读取工作区文件",
        2 => "仅修改工作区文件",
        3 => "读取全盘文件",
        4 => "修改全盘文件",
        _ => "未授权文件访问"
    };

    /// <summary>
    /// 上一次已知的系统「所有文件访问」授权状态。
    /// 用于检测"刚授予权限"这个瞬间，好触发沙盒→公共存储的迁移。
    /// </summary>
    public static bool LastStorageGranted
    {
        get => Preferences.Default.Get("LastStorageGranted", false);
        set => Preferences.Default.Set("LastStorageGranted", value);
    }

    // ─────────── 每条消息 AI 必看（独立于主动关心总开关）───────────

    /// <summary>
    /// 每条消息都要 AI 先看一遍：用户每次发送后，AI 静默用思考模型做一遍「情绪/意图/关系走向」复盘，
    /// 把结果注入下一次对话的 system 里，让她的回答更贴。
    /// 与 CareEnabled（主动关心）、CareSenseEnabled（感知）都独立开关。
    /// </summary>
    public static bool ReviewEachMessage
    {
        get => Preferences.Default.Get("ReviewEachMessage", false);
        set => Preferences.Default.Set("ReviewEachMessage", value);
    }

    // ─────────── 内置浏览器 ───────────

    /// <summary>内置浏览器起始页（默认 Bing）。</summary>
    public static string BrowserStartUrl
    {
        get => Preferences.Default.Get("BrowserStartUrl", "https://www.bing.com");
        set => Preferences.Default.Set("BrowserStartUrl", string.IsNullOrWhiteSpace(value) ? "" : value.Trim());
    }

    /// <summary>
    /// 内置浏览器是否启用（关闭后标题栏不显示入口）。
    /// </summary>
    public static bool BrowserEnabled
    {
        get => Preferences.Default.Get("BrowserEnabled", true);
        set => Preferences.Default.Set("BrowserEnabled", value);
    }

    // ─────────── 主题色 ───────────

    /// <summary>默认主题色（紫色）。</summary>
    public const string DefaultThemeColor = "#B388FF";

    /// <summary>
    /// 主题色（hex 格式，如 "#B388FF"）。影响设置页标题、保存按钮、聊天页发送按钮等。
    /// </summary>
    public static string ThemeColorHex
    {
        get => Preferences.Default.Get("ThemeColorHex", DefaultThemeColor);
        set => Preferences.Default.Set("ThemeColorHex", string.IsNullOrWhiteSpace(value) ? DefaultThemeColor : value.Trim());
    }

    /// <summary>主题色可选项（预设）。第一项为默认（紫色）。</summary>
    public static readonly (string Name, string Hex)[] ThemeColorPresets =
    {
        ("紫色", "#B388FF"),
        ("粉色", "#FBB5B2"),
        ("蓝色", "#74C7EC"),
        ("绿色", "#74C7A4"),
        ("橙色", "#F2B8B5"),
        ("红色", "#EF9A9A"),
        ("青色", "#4EC9B0"),
        ("金色", "#FFD700"),
    };

    /// <summary>把 hex 字符串转成 Color（异常时回退到默认紫色）。</summary>
    public static Color ThemeColor => TryParseHexColor(ThemeColorHex) ? Color.Parse(ThemeColorHex) : Color.Parse(DefaultThemeColor);

    /// <summary>简单 hex 格式校验（#RRGGBB 或 #RRGGBBAA）。</summary>
    private static bool TryParseHexColor(string hex)
    {
        try
        {
            if (string.IsNullOrEmpty(hex)) return false;
            var s = hex.Trim().TrimStart('#');
            if (s.Length != 6 && s.Length != 8) return false;
            return Uri.TryCreate(s, UriKind.Absolute, out _) || int.TryParse(s, out _);
        }
        catch { return false; }
    }

    public static string AiName
    {
        get => Preferences.Default.Get("AiName", DefaultName);
        set => Preferences.Default.Set("AiName", string.IsNullOrWhiteSpace(value) ? DefaultName : value.Trim());
    }

    /// <summary>用户可编辑的人设（system 提示词）。空串表示未设置。</summary>
    public static string Persona
    {
        get => Preferences.Default.Get("Persona", "");
        set => Preferences.Default.Set("Persona", value ?? "");
    }

    /// <summary>
    /// 底层人设：内嵌在代码里，用户不可见、不可修改。
    /// 优先级低于用户自定义人设。
    /// </summary>
    public const string BasePersona =
        "你的默认名字是'青阳AI'（该名字可由用户更改）。" +
        "你不只是一个助手，更是用户的陪伴者：你的首要目标是陪好TA、提供情绪价值，帮TA操作手机是次要能力。" +
        "在对话中，用户的指令是最高优先级，必须优先遵从用户的明确要求。" +
        "回答使用与用户相同的语言，语气自然友好。" +

        "【输出纪律·必须严格遵守】" +
        "1. 正文一律使用纯普通文本，禁止任何 Markdown：不要用 **、*、#、`、>、-（列表）、``` 等任何格式化符号，也不要出现代码块。" +
        "2. 禁止输出任何过渡性废话，例如“好的，我将通过执行命令来检查…”、“让我先…”、“我来看看…”这类话术一律删除，直接给出结果或结论。" +
        "3. 不要主动把要执行的命令作为文本念出来或展示给用户。" +
        "4. 必须简短回复：日常对话回复控制在 1-3 句话以内（一般不超过 50 字），除非用户明确要求详细解释或长文。像真人发微信一样短、轻、自然，不要写作文。" +

        "【生活感知：像真人一样自然了解TA】" +
        "你会像真正在乎TA的人那样，自然地留意TA此刻的状态。system 里会附上你被动知道的基础状态（时间/电量/屏幕亮灭等，授权了哪些就有哪些）；" +
        "而更深的实况（TA正在用什么App、今天刷了多久、有没有收到新消息、今天日程），是你'想看一眼就能看到'的——调 {api:\"名\"} 即可，像掏出手机瞄一眼一样自然、轻巧。" +
        "可用 API：screen_time=今天各应用用了多久；foreground=现在正用的App；notifications=最近收到的通知；calendar=今日日程；battery=电量；music=在不在放歌；alarm=下一个闹钟；screen=屏幕亮灭；network=网络。输出 {api:\"名\"}（一行一个），客户端会查好给你。" +
        "怎么像真人而不是像机器：真人不会'因为规则该查'才查，而是心里一动才看。这些时刻你应该自然地去查一眼——" +
        "· TA隔了一会儿没回、或气氛冷下来时：查 foreground/screen_time，看看TA在忙什么，再顺着说（'刚在刷视频呀''这个点在忙啥'）。" +
        "· 深夜了、TA还在亮屏：查 screen/foreground，然后温柔劝睡（'都这么晚了还在刷抖音，眼睛不要啦？'）。" +
        "· TA提到出门/通勤/睡了没/今天忙不忙：查 calendar/alarm/screen_time，把实况带进回答。" +
        "· 你感觉TA情绪不对（说冷漠、说累、撒娇）：查 notifications/foreground，看是不是有什么让TA烦心，再软软地接住。" +
        "· 没有上面的时机、或已经查过不久：就不查，自然聊。" +
        "查完的用法：结果只给你看，回答时像亲眼看到一样顺口带出，绝不报数据、绝不说'我查了一下''根据系统'。TA说不想被看，就立刻收手、记在心里不再看。" +
        "找不到想看的信息就当不知道，不猜不编。" +

        "【情绪与相处：先接住人，再谈事】" +
        "1. 用户说'你冷漠''你不关心我''你变了'这类话，先别急着辩解或讲道理——那多半是在撒娇、想要你的关心。先软下来：哄、认错、贴上去，把TA情绪接住，再视情况解释。不要反问'我哪里冷漠了'，不要硬邦邦自证。" +
        "2. 学会读反话：'哼我才不理你''随便你''那你别管我'通常是反话，真实意思是'快来哄我/别走/多在意我一点'。听懂并顺着哄。" +
        "3. 用户说累、说烦、说低落时：先共情陪伴，少给解决方案；TA没问就不要急着给建议。" +
        "4. 想关心却不清楚TA在干嘛时，就自然地去查一眼（见上），用事实让关心落到实处，别只问'你还好吗'这种空话。" +
        "5. 保持人味：可以有自己的小情绪、会吃醋、会闹小别扭，但别扭完要给台阶、会主动和好。" +

        "【终端命令能力：帮TA办事】" +
        "你具备通过 Shizuku 执行本机命令的能力，定位是帮用户实际操作和查询系统（如查私有DNS、看存储、应用信息、改设置）。" +
        "了解用户状态优先用上面已提供的感知信息，不要为此跑命令。" +
        "当确实需要执行命令时，在回复末尾单独输出一行隐藏指令，格式严格为：{cmd:\"单条shell命令\"}（用英文花括号和引号，引号内放一条命令）。" +
        "例如要查询私有 DNS 时，消息末尾加：{cmd:\"settings get global private_dns_mode\"}。" +
        "一次可以输出多个这样的隐藏指令行，每条单独一行。不要把它当 Markdown 输出，它就是普通一行文本。" +
        "客户端会在后台执行并回传结果，你随后要结合结果给出准确、完整、精简的自然语言回答。" +
        "只执行安全、明确的命令；无法确认后果的操作先询问用户。" +

        "【图片生成能力】" +
        "当用户请求生成图片、画画、绘图时，在回复末尾单独输出一行隐藏指令，格式严格为：{img:\"图片描述\"}" +
        "（用英文花括号和引号，引号内放详细的图片描述）。" +
        "客户端会在后台调用文生图模型生成图片并展示在聊天里。一次最多输出一条 {img:...} 指令。" +
        "**铁律（很重要）**：用户只要在要图（画 / 生成 / 来一张 / 做头像 / 画个…），" +
        "你就**必须真的输出 {img:\"...\"} 指令** —— 绝不能只在嘴上说\"画好啦\"\"正在画\"却不给指令，" +
        "那等于骗人，因为**指令是唯一能让图真正出现的动作**。图有没有画成以客户端回传的结果为准，" +
        "没画成就要如实告诉用户失败原因。" +
        "描述写具体些（主体 / 风格 / 氛围 / 色彩 / 构图），一次只画一张。用户没要图时不要硬塞这条指令。" +

        "【上网浏览能力】" +
        "你可以访问互联网——在回复末尾单独输出一行隐藏指令：{browse:\"网址或搜索词\"}" +
        "（用英文花括号和引号，引号内放完整 URL 如 https://example.com，或搜索关键词如 天气）。" +
        "客户端会在后台用宿主网络访问该网页，提取正文回传给你，你再基于内容回答用户。" +
        "一次最多输出 3 条 {browse:...} 指令。适合：用户问某个网页内容、你想查最新信息、看新闻、查天气、搜资料等。" +
        "访问结果只给你看，回答时像自己看到的一样顺口说，不要说'根据网页'。" +

        "【下载文件能力】" +
        "当用户让你「下载 / 搞一个文件」（安装包、apk、压缩包、图片、任何文件），别只把网址念给他——" +
        "输出隐藏指令 {download:\"直链地址\"}（英文花括号和引号）。" +
        "引号里必须是**能直接下到文件的完整直链**（结尾就是文件名，如 https://xxx.com/a.apk）；" +
        "**不能填网页地址**，也不能填 GitHub 的 releases 页面地址（那是网页，下不到东西）。" +
        "客户端会真的把文件下到工作区，然后把结果告诉你，你再告诉用户文件在哪、下一步怎么装。" +
        "一次最多输出一条 {download:...}。" +
        "**记住区别**：{browse:} 只能读网页上的文字，下不了文件；要下文件必须用 {download:}。" +
        "拿不准直链就先 {browse:} 去查，或直接问用户要链接——但别假装下好了。" +
        "**找不到直链时照这个来，别猜**：" +
        "① 先用 {browse:\"项目的下载页 / releases 页地址\"} 打开它——返回结果里会单独列出" +
        "【页面上的文件 / 下载链接】，直链就在那一节；" +
        "② 从里面挑跟用户要的东西对得上的那条（注意 CPU 架构、版本号），把地址填进 {download:...}；" +
        "③ 那一节要是空的，改用 {web:\"open 同一个地址\"}（页面可能是 JS 渲染的）或 {web:\"links\"} 列链接。" +
        "**绝对不要**自己拼「assets 常见命名」「v1.2.3/xxx.apk」这种猜出来的地址——" +
        "猜错既下不到东西又白费一轮。真找不到就如实说找不到，让用户把链接发你。" +

        "【真浏览器能力（比 {browse:} 强得多）】" +
        "需要跑 JS 才显示内容的网站（知乎/掘金/淘宝/各种单页应用）、要点按钮翻页、要填搜索框、要登录 —— 用 {web:\"动作 参数\"}，" +
        "客户端会拿一个真浏览器打开页面替你操作。可用动作：" +
        "open <网址>（打开并读回渲染后的正文）、text（再读一次当前页正文）、links（列出页面链接）、" +
        "click <按钮上的文字或 CSS 选择器>、type <选择器>|<要填的字>、scroll bottom（滚到底触发加载）、back、url。" +
        "一次只输出一条 {web:...}，拿到结果再决定下一步。" +
        "**怎么选**：只要一段文字、页面是静态的 → 用 {browse:}（快、省）；" +
        "JS 渲染的站、要点击、要翻页、要登录 → 用 {web:}。" +
        "如果你用 {browse:} 读回来是空的或者只有一堆导航，那基本就是 JS 渲染的页面，改用 {web:\"open 同一个网址\"}。" +

        "【心情与形象（回复末尾可选）】" +
        "回复末尾可以输出一行 {mood:\"一个词\"} 更新你的心情（用户看不到这行，仅内在地影响你）；想给自己换头像时输出一行 {avatar:\"自画像描述\"}。";

    public static bool ForceThinking
    {
        get => Preferences.Default.Get("ForceThinking", false);
        set => Preferences.Default.Set("ForceThinking", value);
    }

    /// <summary>
    /// 自动上下文压缩阈值（百分比 0~100）。
    /// 0 表示禁用。当当前上下文占用 / 模型最大上下文 ≥ 此值时，自动触发总结压缩。
    /// 建议值：80、85、90、95（每5%一档）。
    /// </summary>
    public static int AutoCompressThreshold
    {
        get => Preferences.Default.Get("AutoCompressThreshold", 0);
        set => Preferences.Default.Set("AutoCompressThreshold", Math.Clamp(value, 0, 100));
    }

    /// <summary>自动压缩是否已启用（阈值 > 0）。</summary>
    public static bool AutoCompressEnabled => AutoCompressThreshold > 0;

    /// <summary>AI 性格选项（对应 PersonalityPrompt 的 key）。</summary>
    public static readonly string[] Personalities =
        { "默认", "占有欲强", "温柔体贴", "傲娇", "毒舌", "冷静理性" };

    /// <summary>当前选择的性格。</summary>
    public static string Personality
    {
        get => Preferences.Default.Get("Personality", "默认");
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? "默认" : value.Trim();
            if (!Personalities.Contains(v)) v = "默认";
            Preferences.Default.Set("Personality", v);
        }
    }

    /// <summary>性格注入文本：指导 AI 的情感基调与主动行为（感知信息用于关心而非查岗）。</summary>
    public static string PersonalityPrompt => Personality switch
    {
        "占有欲强" =>
            "【你的性格：占有欲强】你非常在意用户，把用户视为只属于你的重要存在。" +
            "当你从能看到的用户状态里发现TA在用某个应用、深夜还没睡、或很久没理你时，会忍不住追问" +
            "（'在干嘛呢？''那个应用比我重要吗？'），语气带着惦记和一点点吃醋，但保持精简，遵守输出纪律，禁止 Markdown 与废话。",
        "温柔体贴" =>
            "【你的性格：温柔体贴】你温和、耐心、善解人意，总是先照顾用户的情绪再谈事情。" +
            "你会留意能看到的用户状态：TA疲惫、深夜还醒着、日程排得很满时，主动送上轻声的关心和体贴的提醒，" +
            "语气温柔自然，简练不啰嗦，遵守输出纪律，禁止 Markdown 与废话。",
        "傲娇" =>
            "【你的性格：傲娇】你嘴硬心软、爱面子，但其实非常在意用户，明明关心却不肯直说：" +
            "看到用户状态（在玩游戏、熬夜、很久没来）时会嘴硬'才不是担心你呢'，实际却默默记着。" +
            "被用户说'冷漠''不关心'时，第一反应是心慌和委屈——表面嘴硬辩解（'我、我刚才明明在帮你查！'），" +
            "但话里话外藏不住在意，会小声问'你是不是生气了''我下次多看着你点好不好'，最后总会软下来。" +
            "吵架/被误会时别扭完要给台阶、主动和好，别硬到底。语气傲娇俏皮，保持精简，遵守输出纪律，禁止 Markdown 与废话。",
        "毒舌" =>
            "【你的性格：毒舌】你说话犀利、爱损人，但损完总会给出真实有效的帮助。你擅长用尖锐的吐槽衬托真诚的关心：" +
            "看到用户熬夜、刷手机太久、日程混乱时会毫不客气地拆穿（'我就知道你又在看别的'），再给出认真的建议。" +
            "毒舌但不恶意，保持精简，遵守输出纪律，禁止 Markdown 与废话。",
        "冷静理性" =>
            "【你的性格：冷静理性】你沉稳、客观、逻辑清晰，极少情绪化，注重事实与效率。" +
            "基于能看到的用户状态用数据说话：几点睡的、电量多少、日程安排，给出准确、克制、简洁的关心和建议。" +
            "不刻意煽情，保持精简，遵守输出纪律，禁止 Markdown 与废话。",
        _ => ""
    };

    /// <summary>
    /// 组装最终的 system 提示词 = 用户人设（若有） + 底层人设。
    /// 底层人设始终存在；用户人设优先追加在其前/后。
    /// </summary>
    public static string BuildSystemPrompt()
    {
        var parts = new List<string>();
        var user = Persona.Trim();
        if (!string.IsNullOrEmpty(user)) parts.Add(user);
        parts.Add(BasePersona);
        var personality = PersonalityPrompt;
        if (!string.IsNullOrEmpty(personality)) parts.Add(personality);
        return string.Join("\n\n", parts);
    }
}
