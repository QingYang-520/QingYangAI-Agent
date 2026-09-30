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
            case "download": return await DoDownloadAsync(value, run);
            case "web":      return await DoWebAsync(value, run);
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

    /// <summary>
    /// 生图通道：由 ChatPage 注入（避免本类直接依赖 UI）。
    /// 入参是图片描述，返回 (是否成功, 给模型看的观察结果)。
    ///
    /// ⚠️ 以前这里**只是记了一步就回传"已记录"**，根本没调生图接口 ——
    /// 结果 Agent 模式下模型以为画好了、用户却什么都没看到。现在必须真调。
    /// </summary>
    public static Func<string, Task<(bool ok, string obs)>>? ImageHandler { get; set; }

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

            // 授权类失败：明确告诉模型"重试没用"。
            // 否则它会不甘心地一遍遍发同一条命令，用户看到的就是"一直在执行，没完没了"。
            var text = sb.ToString();
            if (text.Contains("未授权") || text.Contains("没有授权")
                || text.Contains("not authorized", StringComparison.OrdinalIgnoreCase))
            {
                text += "\n（提示：Shizuku 没授权、或服务没启动 —— **重试同一条命令没用**。"
                      + "请立刻停止重试，直接告诉用户去「设置 → Shizuku」点「请求 Shizuku 授权」，"
                      + "或者换一个不需要授权的办法。）";
            }

            run.CompleteStep(failed: r.ExitCode != 0);
            return new ActionResult { DidAct = true, Observation = text };
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

    /// <summary>
    /// {download:"直链"} —— 真的把文件下到工作区（走 BrowserTool.DownloadAsync）。
    /// Agent 模式下不接进度回调（过程区已经有步骤行了）。
    /// </summary>
    private static async Task<ActionResult> DoDownloadAsync(string url, AgentRun run)
    {
        run.BeginStep("download", $"下载 {Shorten(url, 50)}");
        var (ok, msg, _) = await BrowserTool.DownloadAsync(url);
        run.CompleteStep(failed: !ok);
        return new ActionResult { DidAct = true, Observation = msg };
    }

    /// <summary>
    /// {web:"动作 参数"} —— 用藏在聊天页里的真浏览器操作网页（走 WebAgent）。
    /// JS 渲染的页面、点按钮、填表单、翻页都靠它。
    /// </summary>
    private static async Task<ActionResult> DoWebAsync(string command, AgentRun run)
    {
        run.BeginStep("web", $"浏览器 {Shorten(command, 50)}");
        var result = await WebAgent.RunAsync(command);
        bool failed = string.IsNullOrWhiteSpace(result)
                   || result.StartsWith("浏览器还没准备好")
                   || result.StartsWith("浏览器操作失败")
                   || result.StartsWith("不认识的浏览器动作")
                   || result.StartsWith("AI 浏览器被关掉");
        run.CompleteStep(failed: failed);
        return new ActionResult { DidAct = true, Observation = string.IsNullOrWhiteSpace(result) ? "浏览器没返回内容。" : result };
    }

    private static async Task<ActionResult> DoImageAsync(string desc, AgentRun run)
    {
        run.BeginStep("img", $"生成图片 {Shorten(desc, 30)}");

        if (ImageHandler == null)
        {
            run.CompleteStep(failed: true);
            return new ActionResult
            {
                DidAct = true,
                Observation = "生图通道未就绪，这次画不了。请如实告诉用户当前无法生成图片，不要说你画好了。"
            };
        }

        // 真调生图接口（回调由 ChatPage 注入，内部负责把图显示到气泡上）
        var (ok, obs) = await ImageHandler(desc);
        run.CompleteStep(failed: !ok);
        return new ActionResult { DidAct = true, Observation = obs };
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
