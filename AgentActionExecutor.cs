using System.Text;

namespace 青阳AI;

/// <summary>
/// Agent 动作执行器：把模型输出的隐藏指令翻译成真实动作，
/// 并把每一步写进 <see cref="AgentRun"/>（供界面渲染单行缩略）。
///
/// 这里是 Agent 循环唯一"动手"的地方，权限校验全部下沉到 FileGuard / ApiRegistry。
/// </summary>
public static class AgentActionExecutor
{
    /// <summary>转义后的竖线分隔（{write:"路径|内容"}、{edit:"路径|旧|新"}）。</summary>
    private const char Sep = '|';

    /// <summary>本次动作是否真的动了手（用于判断循环要不要继续）。</summary>
    public sealed class ActionResult
    {
        public bool DidAct { get; set; }
        /// <summary>回传给模型的观察结果文本。</summary>
        public string Observation { get; set; } = "";
        /// <summary>模型是否明确表示任务已完成（{done:"..."}）。</summary>
        public bool TaskDone { get; set; }
        /// <summary>任务完成时模型的最终确认话术。</summary>
        public string DoneMessage { get; set; } = "";
    }

    /// <summary>
    /// 执行模型输出里的一条指令。返回是否动手 + 观察结果。
    /// 一条回复里可能带多条指令，调用方循环调用本方法。
    /// </summary>
    public static async Task<ActionResult> ExecuteOneAsync(string marker, string value, AgentRun run)
    {
        switch (marker)
        {
            case "read":   return await DoReadAsync(value, run);
            case "ls":     return await DoListAsync(value, run);
            case "write":  return await DoWriteAsync(value, run);
            case "append": return await DoAppendAsync(value, run);
            case "edit":   return await DoEditAsync(value, run);
            case "del":    return await DoDeleteAsync(value, run);
            case "cmd":    return await DoCommandAsync(value, run);
            case "api":    return await DoApiAsync(value, run);
            case "browse": return await DoBrowseAsync(value, run);
            case "img":    return await DoImageAsync(value, run);
            case "todo":   return DoTodo(value, run);
            case "plan":   return DoTodo(value, run);
            case "done":   return new ActionResult { TaskDone = true, DoneMessage = value };
            default:       return new ActionResult();
        }
    }

    // ─────────── 计划（让模型先把步骤摆出来，循环才有"目标感"）───────────

    /// <summary>
    /// {todo:"1. 读文件 2. 改代码 3. 验证"} —— 把模型自报的计划登记进过程区。
    /// 这不会"动手"，但能显著提升模型后续按计划推进的稳定性。
    /// </summary>
    private static ActionResult DoTodo(string value, AgentRun run)
    {
        var plan = (value ?? "").Replace("\n", " ").Trim();
        run.AddStep("plan", $"计划：{Shorten(plan, 60)}");
        return new ActionResult
        {
            DidAct = true,
            Observation = "计划已登记。请按顺序逐步推进，每轮只做一步。"
        };
    }

    // ─────────── 文件类 ───────────

    private static async Task<ActionResult> DoReadAsync(string path, AgentRun run)
    {
        run.BeginStep("read", $"读取 {Short(path)}");
        var result = await FileTool.ReadAsync(path);
        bool ok = !result.StartsWith("读取失败") && !result.StartsWith("文件不存在")
                  && !result.StartsWith("现在没有文件访问权限") && !result.StartsWith("当前权限");
        run.CompleteStep(failed: !ok);
        return new ActionResult { DidAct = true, Observation = result };
    }

    private static async Task<ActionResult> DoListAsync(string path, AgentRun run)
    {
        run.BeginStep("ls", $"浏览 {Short(path)}");
        var result = await FileTool.ListAsync(path);
        bool ok = !result.StartsWith("列目录失败") && !result.StartsWith("目录不存在")
                  && !result.StartsWith("现在没有文件访问权限") && !result.StartsWith("当前权限");
        run.CompleteStep(failed: !ok);
        return new ActionResult { DidAct = true, Observation = result };
    }

    private static async Task<ActionResult> DoWriteAsync(string value, AgentRun run)
    {
        var (path, content) = Split2(value);
        run.BeginStep("write", $"写入 {Short(path)}");
        var r = await FileTool.WriteAsync(path, content);
        run.CompleteStep(r.Added, r.Removed, failed: !r.Success);
        return new ActionResult { DidAct = true, Observation = r.Message };
    }

    private static async Task<ActionResult> DoAppendAsync(string value, AgentRun run)
    {
        var (path, content) = Split2(value);
        run.BeginStep("append", $"追加 {Short(path)}");
        var r = await FileTool.AppendAsync(path, content);
        run.CompleteStep(r.Added, r.Removed, failed: !r.Success);
        return new ActionResult { DidAct = true, Observation = r.Message };
    }

    private static async Task<ActionResult> DoEditAsync(string value, AgentRun run)
    {
        var parts = SplitN(value, 3);
        if (parts.Count < 3)
            return new ActionResult { DidAct = true, Observation = "edit 指令格式不对，应为 {edit:\"路径|原文|新内容\"}" };

        var (path, oldText, newText) = (parts[0], parts[1], parts[2]);
        run.BeginStep("edit", $"编辑 {Short(path)}");
        var r = await FileTool.EditAsync(path, oldText, newText);
        run.CompleteStep(r.Added, r.Removed, failed: !r.Success);
        return new ActionResult { DidAct = true, Observation = r.Message };
    }

    private static async Task<ActionResult> DoDeleteAsync(string path, AgentRun run)
    {
        // 删除是高风险动作：除完全访问外，一律先要用户确认
        if (!AppSettings.FullAccess)
        {
            bool ok = await ConfirmDeleteAsync(path);
            if (!ok)
            {
                run.AddStep("del", $"删除 {Short(path)}", removed: 0);
                return new ActionResult { DidAct = true, Observation = $"用户拒绝删除 {path}，请换一种方式或征询用户意见。" };
            }
        }
        run.BeginStep("del", $"删除 {Short(path)}");
        var r = await FileTool.DeleteAsync(path);
        run.CompleteStep(r.Added, r.Removed, failed: !r.Success);
        return new ActionResult { DidAct = true, Observation = r.Message };
    }

    /// <summary>删除确认通道：由 ChatPage 注入（避免本类直接依赖 UI）。</summary>
    public static Func<string, Task<bool>>? DeleteConfirmer { get; set; }

    private static Task<bool> ConfirmDeleteAsync(string path)
        => DeleteConfirmer?.Invoke(path) ?? Task.FromResult(false);

    // ─────────── 既有能力（终端 / 感知 API / 上网 / 生图）───────────

    private static async Task<ActionResult> DoCommandAsync(string cmd, AgentRun run)
    {
        run.BeginStep("cmd", $"执行命令 {Shorten(cmd, 40)}");
#if ANDROID
        try
        {
            var r = await ShizukuRunner.ExecuteAsync(cmd);
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(r.Stdout)) sb.Append(r.Stdout);
            if (!string.IsNullOrWhiteSpace(r.Stderr)) sb.AppendLine("[stderr] " + r.Stderr);
            if (r.ExitCode != 0) sb.Append($"(exit {r.ExitCode})");
            run.CompleteStep(failed: r.ExitCode != 0);
            return new ActionResult { DidAct = true, Observation = sb.ToString() };
        }
        catch (Exception ex)
        {
            run.CompleteStep(failed: true);
            return new ActionResult { DidAct = true, Observation = "命令执行失败：" + ex.Message };
        }
#else
        run.CompleteStep(failed: true);
        return new ActionResult { DidAct = true, Observation = "终端命令仅在 Android 上可用。" };
#endif
    }

    private static async Task<ActionResult> DoApiAsync(string id, AgentRun run)
    {
        var name = ApiRegistry.Find(id.Trim())?.Name ?? id;
        run.BeginStep("api", $"查询{name}");
        var result = await ApiRegistry.ExecuteAsync(id);
        bool failed = result.StartsWith("没有叫") || result.StartsWith("调用失败");
        run.CompleteStep(failed: failed);
        return new ActionResult { DidAct = true, Observation = $"[{name}] {result}" };
    }

    private static async Task<ActionResult> DoBrowseAsync(string url, AgentRun run)
    {
        run.BeginStep("browse", $"上网 {Shorten(url, 40)}");
        var text = await BrowserTool.FetchAsync(url);
        bool failed = string.IsNullOrWhiteSpace(text) || text.StartsWith("抓取失败") || text.StartsWith("无法");
        run.CompleteStep(failed: failed);
        return new ActionResult { DidAct = true, Observation = string.IsNullOrWhiteSpace(text) ? "网页抓取失败。" : text };
    }

    private static async Task<ActionResult> DoImageAsync(string desc, AgentRun run)
    {
        run.AddStep("img", $"生成图片 {Shorten(desc, 30)}");
        return new ActionResult { DidAct = true, Observation = "（图片生成指令已记录，由聊天页的图片管线处理）" };
    }

    // ─────────── 工具 ───────────

    /// <summary>把值按第一个竖线切成两段（路径 | 内容）。</summary>
    private static (string a, string b) Split2(string value)
    {
        int i = value.IndexOf(Sep);
        if (i < 0) return (value.Trim(), "");
        return (value[..i].Trim(), value[(i + 1)..]);
    }

    /// <summary>把值按竖线切成最多 n 段（路径 | 原文 | 新内容）。</summary>
    private static List<string> SplitN(string value, int n)
    {
        var raw = value.Split(Sep);
        var list = new List<string>();
        for (int i = 0; i < n - 1 && i < raw.Length; i++)
            list.Add(raw[i]);
        if (raw.Length >= n)
            list.Add(string.Join(Sep.ToString(), raw.Skip(n - 1)));
        return list;
    }

    /// <summary>路径缩短显示：只留最后两段，避免缩略行太长。</summary>
    private static string Short(string path)
    {
        path = (path ?? "").Trim();
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2) return path;
        return "…/" + string.Join("/", parts.Skip(parts.Length - 2));
    }

    private static string Shorten(string s, int max)
    {
        s = (s ?? "").Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
