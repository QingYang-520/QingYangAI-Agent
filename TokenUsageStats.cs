using System;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace 青阳AI;

/// <summary>
/// 与厂商无关的 usage 内部模型：不管上游字段叫什么，最终都收敛成这两个数。
/// </summary>
public sealed class TokenUsage
{
    /// <summary>本次请求的 prompt（输入）token 总数。</summary>
    public int PromptTokens { get; set; }

    /// <summary>其中命中缓存的 token 数。上游没有缓存字段时为 0，不抛异常。</summary>
    public int CachedTokens { get; set; }

    /// <summary>缓存命中率（百分比，保留 1 位小数；prompt 为 0 时返回 0）。</summary>
    public double HitRate
    {
        get
        {
            if (PromptTokens <= 0) return 0.0;
            double rate = (double)CachedTokens / (double)PromptTokens * 100.0;
            return Math.Round(rate, 1, MidpointRounding.AwayFromZero);
        }
    }
}

/// <summary>
/// 多厂商 usage 字段映射层。
/// 覆盖 OpenAI / DeepSeek / 智谱 / Moonshot / Anthropic / Gemini / 火山等常见结构：
/// prompt 字段、cached 字段、嵌套 details 容器全部做别名匹配（忽略大小写与下划线）。
/// 解析失败或缺字段一律返回 0，绝不抛异常。
/// </summary>
public static class TokenUsageMapper
{
    // prompt（输入）token 的别名
    private static readonly string[] PromptKeys =
    {
        "prompt_tokens", "input_tokens", "prompt_token_count", "input_token_count",
        "promptTokens", "inputTokens", "prompt_token", "input_token", "total_tokens"
    };

    // 缓存命中 token 的别名
    private static readonly string[] CachedKeys =
    {
        "cached_tokens", "cache_tokens", "prompt_cache_hit_tokens", "cache_read_input_tokens",
        "cachedTokens", "cacheTokens", "promptCacheHitTokens", "cachedContentTokenCount",
        "cached_content_token_count", "cached_prompt_tokens", "cache_hit_tokens"
    };

    // 缓存未命中 token 的别名（用于 prompt 缺失时反推 prompt = 命中 + 未命中）
    private static readonly string[] MissKeys =
    {
        "prompt_cache_miss_tokens", "uncached_tokens", "cache_miss_tokens",
        "uncachedTokens", "promptCacheMissTokens", "cache_creation_input_tokens"
    };

    // 嵌套容器：OpenAI 的 prompt_tokens_details、DeepSeek 的 prompt_cache_hit_token_details 等
    private static readonly string[] DetailKeys =
    {
        "prompt_tokens_details", "prompt_cache_hit_token_details", "input_tokens_details",
        "cache_tokens_details", "promptTokensDetails", "inputTokenDetails", "usage_details"
    };

    /// <summary>从完整响应 JSON 字符串里解析 usage（自动定位根节点的 usage 字段）。</summary>
    public static TokenUsage FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new TokenUsage();
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FromElement(doc.RootElement);
        }
        catch
        {
            return new TokenUsage();
        }
    }

    /// <summary>从 JsonElement 解析 usage。传 null / 非对象 / 无 usage 都返回空模型。</summary>
    public static TokenUsage FromElement(JsonElement? element)
    {
        if (element == null) return new TokenUsage();
        return FromElement(element.Value);
    }

    /// <summary>从 JsonElement 解析 usage。根节点带 usage 字段时自动下钻。</summary>
    public static TokenUsage FromElement(JsonElement element)
    {
        try
        {
            if (element.ValueKind != JsonValueKind.Object) return new TokenUsage();

            // 传进来的是完整响应体时，下钻到 usage 节点
            if (TryGet(element, "usage", out var inner) && inner.ValueKind == JsonValueKind.Object)
                element = inner;

            int prompt = ReadFirst(element, PromptKeys);
            int cached = ReadFirst(element, CachedKeys);

            // 顶层没找到 cached，去嵌套 details 里再找一轮
            if (cached <= 0)
            {
                foreach (var key in DetailKeys)
                {
                    if (!TryGet(element, key, out var detail) || detail.ValueKind != JsonValueKind.Object) continue;
                    cached = ReadFirst(detail, CachedKeys);
                    if (cached > 0) break;
                }
            }

            int miss = ReadFirst(element, MissKeys);

            // Anthropic 风格：input_tokens 不含 cache_read / cache_creation，需要加回来
            if (HasAny(element, "cache_read_input_tokens") && prompt > 0)
                prompt = prompt + cached;
            else if (prompt <= 0)
                prompt = cached + miss;   // DeepSeek 风格：只有命中/未命中，反推 prompt

            if (prompt < 0) prompt = 0;
            if (cached < 0) cached = 0;
            if (cached > prompt) cached = prompt;

            return new TokenUsage { PromptTokens = prompt, CachedTokens = cached };
        }
        catch
        {
            return new TokenUsage();
        }
    }

    /// <summary>按别名顺序取第一个存在的字段值（找不到返回 0）。</summary>
    private static int ReadFirst(JsonElement obj, string[] keys)
    {
        foreach (var key in keys)
        {
            if (TryGet(obj, key, out var value))
            {
                int num = ToInt(value);
                if (num > 0) return num;
            }
        }
        return 0;
    }

    /// <summary>是否存在任一别名字段。</summary>
    private static bool HasAny(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
            if (TryGet(obj, key, out _)) return true;
        return false;
    }

    /// <summary>取属性：先精确匹配，再按「忽略大小写 + 忽略下划线/连字符」匹配。</summary>
    private static bool TryGet(JsonElement obj, string key, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        if (obj.TryGetProperty(key, out value)) return true;

        string target = Normalize(key);
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(Normalize(prop.Name), target, StringComparison.Ordinal))
            {
                value = prop.Value;
                return true;
            }
        }
        return false;
    }

    private static string Normalize(string name)
    {
        var buffer = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (ch == '_' || ch == '-' || ch == ' ') continue;
            buffer.Append(char.ToLowerInvariant(ch));
        }
        return buffer.ToString();
    }

    /// <summary>容错取值：数字、数字字符串都能读，读不出就 0。</summary>
    private static int ToInt(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt32(out var i)) return i < 0 ? 0 : i;
            if (value.TryGetDouble(out var d)) return d <= 0 ? 0 : (int)Math.Round(d);
            return 0;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var s))
                return s < 0 ? 0 : s;
        }
        return 0;
    }
}

/// <summary>
/// 统计中心：本次 usage + 全局累计 usage，实现 INotifyPropertyChanged，改完自动刷新 UI。
/// 累计数持久化在 Preferences 里，重启应用不清零。
/// </summary>
public sealed class UsageStats : INotifyPropertyChanged
{
    private const string PrefTotalPrompt = "UsageStats.TotalPromptTokens";
    private const string PrefTotalCached = "UsageStats.TotalCachedTokens";

    public static UsageStats Current { get; } = new UsageStats();

    private int _lastPromptTokens;
    private int _lastCachedTokens;
    private int _totalPromptTokens;
    private int _totalCachedTokens;

    private UsageStats()
    {
        _totalPromptTokens = Preferences.Get(PrefTotalPrompt, 0);
        _totalCachedTokens = Preferences.Get(PrefTotalCached, 0);
    }

    // ---------- 本次 ----------
    public int LastPromptTokens
    {
        get => _lastPromptTokens;
        private set
        {
            if (_lastPromptTokens == value) return;
            _lastPromptTokens = value;
            OnPropertyChanged(nameof(LastPromptTokens));
            OnPropertyChanged(nameof(LastHitRate));
            OnPropertyChanged(nameof(LastText));
        }
    }

    public int LastCachedTokens
    {
        get => _lastCachedTokens;
        private set
        {
            if (_lastCachedTokens == value) return;
            _lastCachedTokens = value;
            OnPropertyChanged(nameof(LastCachedTokens));
            OnPropertyChanged(nameof(LastHitRate));
            OnPropertyChanged(nameof(LastText));
        }
    }

    /// <summary>本次缓存命中率（百分比，1 位小数）。</summary>
    public double LastHitRate => CalcRate(LastPromptTokens, LastCachedTokens);

    /// <summary>本次一行文本，直接给 UI 绑。</summary>
    public string LastText =>
        "本轮 " + FormatCount(LastPromptTokens) + " · 缓存 " + FormatCount(LastCachedTokens) +
        " · " + FormatRate(LastHitRate);

    // ---------- 全局累计 ----------
    public int TotalPromptTokens
    {
        get => _totalPromptTokens;
        private set
        {
            if (_totalPromptTokens == value) return;
            _totalPromptTokens = value;
            OnPropertyChanged(nameof(TotalPromptTokens));
            OnPropertyChanged(nameof(TotalHitRate));
            OnPropertyChanged(nameof(TotalText));
        }
    }

    public int TotalCachedTokens
    {
        get => _totalCachedTokens;
        private set
        {
            if (_totalCachedTokens == value) return;
            _totalCachedTokens = value;
            OnPropertyChanged(nameof(TotalCachedTokens));
            OnPropertyChanged(nameof(TotalHitRate));
            OnPropertyChanged(nameof(TotalText));
        }
    }

    /// <summary>全局缓存命中率（百分比，1 位小数）。</summary>
    public double TotalHitRate => CalcRate(TotalPromptTokens, TotalCachedTokens);

    /// <summary>累计一行文本，直接给 UI 绑。</summary>
    public string TotalText =>
        "累计 " + FormatCount(TotalPromptTokens) + " · 缓存 " + FormatCount(TotalCachedTokens) +
        " · " + FormatRate(TotalHitRate);

    // ---------- 更新 & 累加 ----------

    /// <summary>拿到一次模型返回的 usage：更新【本次】，并累加进【全局累计】。</summary>
    public void Apply(TokenUsage? usage)
    {
        if (usage == null) return;

        int prompt = usage.PromptTokens < 0 ? 0 : usage.PromptTokens;
        int cached = usage.CachedTokens < 0 ? 0 : usage.CachedTokens;
        if (cached > prompt) cached = prompt;
        if (prompt <= 0 && cached <= 0) return;   // 该次没有 usage 数据，不刷新

        void Update()
        {
            LastPromptTokens = prompt;
            LastCachedTokens = cached;
            TotalPromptTokens = TotalPromptTokens + prompt;
            TotalCachedTokens = TotalCachedTokens + cached;

            Preferences.Set(PrefTotalPrompt, TotalPromptTokens);
            Preferences.Set(PrefTotalCached, TotalCachedTokens);
        }

        if (MainThread.IsMainThread) Update();
        else MainThread.BeginInvokeOnMainThread(Update);
    }

    /// <summary>直接从响应 JSON 字符串解析并累加（内部走字段映射层）。</summary>
    public void ApplyFromJson(string? json) => Apply(TokenUsageMapper.FromJson(json));

    /// <summary>直接从 usage 节点解析并累加。</summary>
    public void ApplyFromElement(JsonElement? element) => Apply(TokenUsageMapper.FromElement(element));

    /// <summary>清空累计（设置页可挂个重置入口）。</summary>
    public void ResetTotals()
    {
        void Update()
        {
            TotalPromptTokens = 0;
            TotalCachedTokens = 0;
            Preferences.Remove(PrefTotalPrompt);
            Preferences.Remove(PrefTotalCached);
        }

        if (MainThread.IsMainThread) Update();
        else MainThread.BeginInvokeOnMainThread(Update);
    }

    // ---------- 工具 ----------
    private static double CalcRate(int prompt, int cached)
    {
        if (prompt <= 0) return 0.0;
        return Math.Round((double)cached / (double)prompt * 100.0, 1, MidpointRounding.AwayFromZero);
    }

    private static string FormatRate(double rate) =>
        rate.ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string FormatCount(int value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
