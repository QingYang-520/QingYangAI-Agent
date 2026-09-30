using System.Text;

namespace 青阳AI;

/// <summary>
/// Agent 循环的“一步”：一个动作 + 一行缩略描述。
/// 界面只渲染 <see cref="Summary"/>（单行），行尾带 +N/-N 行数变化。
/// </summary>
public sealed class AgentStep
{
    /// <summary>步骤类型（决定 emoji 与颜色）。</summary>
    public string Kind { get; set; } = "think";

    /// <summary>动作描述，如 “读取 README.md”。</summary>
    public string Text { get; set; } = "";

    /// <summary>新增行数（绿色 +N）。</summary>
    public int Added { get; set; }

    /// <summary>减少行数（红色 -N）。</summary>
    public int Removed { get; set; }

    /// <summary>步骤是否仍在进行中（进行中显示流光）。</summary>
    public bool Running { get; set; }

    /// <summary>步骤是否失败（红色叉）。</summary>
    public bool Failed { get; set; }

    /// <summary>单色 emoji：按动作类型区分，不使用彩色。</summary>
    public string Emoji => Kind switch
    {
        "think" => "🧠",
        "plan" => "📋",
        "read" => "📄",
        "write" => "📝",
        "append" => "📎",
        "edit" => "✏️",
        "ls" => "📁",
        "del" => "🗑",
        "cmd" => "⌨️",
        "api" => "📡",
        "browse" => "🌐",
        "img" => "🎨",
        "done" => "✅",
        "fail" => "⚠️",
        _ => "•"
    };

    /// <summary>
    /// 渲染成一行缩略文本：`✏️ 编辑 ChatPage.xaml.cs +3 -1`。
    /// 加减只在非零时出现（避免每行都挂个 +0 -0）。
    /// </summary>
    public string Summary
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(Emoji).Append(' ').Append(Text);
            if (Added > 0) sb.Append($" +{Added}");
            if (Removed > 0) sb.Append($" -{Removed}");
            if (Running) sb.Append(" …");
            if (Failed) sb.Append(" 失败");
            return sb.ToString();
        }
    }
}

/// <summary>
/// Agent 循环执行模式的运行时状态：所有步骤 + 累计行数变化 + 任务完成后的折叠。
///
/// 一个"回环"= 思考 → 动作 → 拿结果 → 再思考 …… 直到任务完成。
/// 最后总结时把整个回环里所有 +N/-N 汇总成一行总账。
/// </summary>
public sealed class AgentRun
{
    /// <summary>全部步骤（按时间顺序）。</summary>
    public List<AgentStep> Steps { get; } = new();

    /// <summary>累计新增行数。</summary>
    public int TotalAdded { get; private set; }

    /// <summary>累计减少行数。</summary>
    public int TotalRemoved { get; private set; }

    /// <summary>循环是否已结束。</summary>
    public bool Finished { get; private set; }

    /// <summary>过程区是否折叠（任务完成后自动折叠，用户可点开）。</summary>
    public bool Collapsed { get; set; }

    /// <summary>
    /// 模型此刻正在思考的内容（推理流的末尾一小段）。
    /// 只在运行中显示，用来告诉用户"Ta 确实在动脑子"，任务结束即清空。
    /// </summary>
    public string LiveThought { get; private set; } = "";

    /// <summary>更新实时思考文本（由界面在推理流回调里调用）。</summary>
    public void SetLiveThought(string text)
    {
        LiveThought = text ?? "";
    }

    /// <summary>当前正在进行的步骤（用于把"完成"状态回填到同一行）。</summary>
    private AgentStep? _current;

    /// <summary>开始一步（进行中）。返回该步骤，调用方在完成后调 <see cref="CompleteStep"/>。</summary>
    public AgentStep BeginStep(string kind, string text)
    {
        _current = new AgentStep { Kind = kind, Text = text, Running = true };
        Steps.Add(_current);
        return _current;
    }

    /// <summary>完成当前步骤，并累计行数变化。</summary>
    public void CompleteStep(int added = 0, int removed = 0, bool failed = false, string? newText = null)
    {
        if (_current == null) return;
        _current.Running = false;
        _current.Failed = failed;
        _current.Added = added;
        _current.Removed = removed;
        if (newText != null) _current.Text = newText;
        TotalAdded += added;
        TotalRemoved += removed;
        _current = null;
    }

    /// <summary>追加一条即时步骤（一次性完成，不停留）。</summary>
    public void AddStep(string kind, string text, int added = 0, int removed = 0)
    {
        Steps.Add(new AgentStep { Kind = kind, Text = text, Added = added, Removed = removed });
        TotalAdded += added;
        TotalRemoved += removed;
    }

    /// <summary>标记整个循环结束并折叠。</summary>
    public void Finish()
    {
        Finished = true;
        Collapsed = true;
        LiveThought = "";
        _current = null;
    }

    /// <summary>
    /// 折叠/展开时顶部显示的一行摘要：
    /// 运行中 → `🧠 正在执行 · 第 3 步 · 读取 ChatPage.xaml.cs`（让用户知道此刻在干嘛）
    /// 结束后 → `🧠 任务执行过程 · 12 步 · +8 -3`
    /// </summary>
    public string CollapsedSummary
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append("🧠 ");

            if (!Finished)
            {
                // 运行中：优先显示"当前正在做什么"，比单纯报步数有信息量得多
                sb.Append("正在执行 · 第 ").Append(Math.Max(Steps.Count, 1)).Append(" 步");
                var cur = Steps.LastOrDefault(s => s.Running);
                if (cur != null) sb.Append(" · ").Append(cur.Text);
                // 有实时思考内容就再挂一小段，让标题栏"活"起来
                if (!string.IsNullOrWhiteSpace(LiveThought))
                    sb.Append(" ｜ ").Append(LiveThought);
                return sb.ToString();
            }

            sb.Append("任务执行过程 · ").Append(Steps.Count).Append(" 步");
            if (TotalAdded > 0 || TotalRemoved > 0)
            {
                sb.Append(" ·");
                if (TotalAdded > 0) sb.Append($" +{TotalAdded}");
                if (TotalRemoved > 0) sb.Append($" -{TotalRemoved}");
            }
            return sb.ToString();
        }
    }

    /// <summary>全部步骤的完整文本（展开时显示，一行一步）。</summary>
    public string FullText => string.Join("\n", Steps.Select(s => s.Summary));

    /// <summary>行数总账（总结时用）：`本次共修改 12 行、删除 3 行`。</summary>
    public string DeltaSummary
    {
        get
        {
            if (TotalAdded == 0 && TotalRemoved == 0) return "本次没有改动文件";
            var parts = new List<string>();
            if (TotalAdded > 0) parts.Add($"新增 {TotalAdded} 行");
            if (TotalRemoved > 0) parts.Add($"删除 {TotalRemoved} 行");
            return "本次共 " + string.Join("、", parts);
        }
    }
}
