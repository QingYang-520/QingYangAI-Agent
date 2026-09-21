using System.Text;
using System.Text.Json;

namespace 青阳AI;

/// <summary>
/// Agent 循环执行模式：收到指令 → 思考 → 执行动作 → 拿到结果 → 再次思考 → 继续执行，
/// 直到任务完成（模型输出 {done:"..."}）或到达时限，最后自动折叠过程并重新总结。
///
/// 与一次性对话的区别：普通对话是「一问一答」，这里是「一问 → 多轮自主推进 → 最终总结」。
/// </summary>
public static class AgentLoopService
{
    private static readonly HttpClient _http = new();

    /// <summary>循环过程中每完成一步就回调一次（让界面刷新缩略行）。</summary>
    public static event Action<AgentRun>? StepChanged;

    /// <summary>
    /// 模型正在"思考"时的实时文本回调（推理流的一小段）。
    /// 界面把它接到过程区标题上，让用户看到 Ta 确实在动脑子，而不是干等。
    /// </summary>
    public static event Action<string>? ThinkingDelta;

    private static void Raise(AgentRun run)
    {
        try { StepChanged?.Invoke(run); }
        catch { }
    }

    private static void RaiseThinking(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        try { ThinkingDelta?.Invoke(delta); }
        catch { }
    }

    /// <summary>循环结果。</summary>
    public sealed class LoopResult
    {
        /// <summary>最终要上屏的答复正文。</summary>
        public string FinalText { get; set; } = "";
        /// <summary>整个过程记录。</summary>
        public AgentRun Run { get; set; } = new();
        /// <summary>是否因为时限到达而被强制收尾（而非任务自然完成）。</summary>
        public bool TimedOut { get; set; }
        /// <summary>错误信息（非空表示循环失败）。</summary>
        public string Error { get; set; } = "";
    }

    /// <summary>
    /// 跑一轮 Agent 循环。
    /// </summary>
    /// <param name="userPrompt">用户这一轮说的话。</param>
    /// <param name="history">已有的对话历史（供模型理解上下文）。</param>
    /// <param name="extraSystem">额外注入的 system 片段（感知/记忆/复盘等）。</param>
    /// <param name="cancellationToken">外部取消（用户离开页面等）。</param>
    public static async Task<LoopResult> RunAsync(
        string userPrompt,
        List<object> history,
        string extraSystem,
        CancellationToken cancellationToken = default)
    {
        var result = new LoopResult();
        var run = result.Run;
        var deadline = AppSettings.AgentTimeLimitMinutes <= 0
            ? DateTime.MaxValue
            : DateTime.Now.AddMinutes(AppSettings.AgentTimeLimitMinutes);

        // 循环内的对话消息（在 history 基础上追加本次的推进过程）
        // history 由调用方产出：第 0 条是 system，其后是历史对话（可能已含本轮用户消息）。
        // 这里只在 history 末尾不是本轮同一句话时才追加，避免同一条请求被发两遍。
        var messages = new List<object>(history);
        if (!LastUserContentEquals(messages, userPrompt))
            messages.Add(new { role = "user", content = userPrompt });

        var system = BuildAgentSystem(extraSystem);
        // system 永远在最前面。history 由 BuildHistoryMessages 产出，第 0 条必是 {role="system", content=...}，
        // 这里整体替换成 Agent 版（不追加，避免两条 system 打架）。
        if (messages.Count > 0)
            messages[0] = new { role = "system", content = system };
        else
            messages.Add(new { role = "system", content = system });

        run.AddStep("think", "开始处理任务");

        // 连续"空转轮"计数：模型既不给动作、也不宣告完成时，最多容忍几次再强制收尾，
        // 避免因为模型不听话就把整轮任务判死（这是之前"退化成 chat 模式"的主因）。
        const int MaxIdleRounds = 2;
        int idleRounds = 0;
        // 最近一次模型说的话，兜底收尾时用作最终答复
        string lastSaid = "";

        for (int iteration = 0; iteration < AppSettings.AgentMaxIterations; iteration++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result.Error = "__cancelled__";
                break;
            }
            // 时限检查：到点就跳出，走"强制收尾"路径
            if (DateTime.Now >= deadline)
            {
                result.TimedOut = true;
                break;
            }

            run.BeginStep("think", "思考下一步");
            string reply;
            try
            {
                reply = await SendOnceAsync(messages, cancellationToken);
            }
            catch (Exception ex)
            {
                run.CompleteStep(failed: true);
                result.Error = ex.Message;
                break;
            }
            run.CompleteStep();

            // 模型是否宣告任务完成 —— 这是唯一正当的结束方式
            var doneMsgs = InstructionParser.Extract(reply, "done");
            if (doneMsgs.Count > 0)
            {
                result.FinalText = doneMsgs[0];
                run.AddStep("done", "任务完成");
                run.Finish();
                Raise(run);
                return result;
            }

            // 解析这一轮里的所有动作指令
            var actions = ExtractActions(reply);
            if (actions.Count == 0)
            {
                // 模型没给动作也没宣告完成：不能直接收工（那会让 Agent 退化成普通问答），
                // 先明确催它一次；连续催不动才用兜底收尾。
                lastSaid = CleanReply(reply);
                idleRounds++;
                run.AddStep("think", idleRounds == 1 ? "尚未给出动作，催促继续" : "仍未给出动作");

                if (idleRounds >= MaxIdleRounds)
                {
                    result.FinalText = string.IsNullOrWhiteSpace(lastSaid)
                        ? "任务还没做完，但我没能继续推进。可以再把要求说细一点，我重新试。"
                        : lastSaid;
                    run.AddStep("fail", "模型未继续推进，已收尾");
                    run.Finish();
                    Raise(run);
                    return result;
                }

                messages.Add(new { role = "assistant", content = reply });
                messages.Add(new
                {
                    role = "user",
                    content = "（系统提示）你刚才没有输出任何动作指令。现在必须二选一，不许含糊：\n"
                              + "① 任务还没完成 → 立刻输出下一步的动作指令（独立一行，如 {read:\"路径\"}）；\n"
                              + "② 任务确实已完成 → 输出 {done:\"给用户的最终答复\"}。\n"
                              + "不要回答别的，不要说'我这就去'，直接给出 ① 或 ②。"
                });
                continue;
            }
            idleRounds = 0;

            // 逐个执行，累积观察结果
            var observations = new StringBuilder();
            foreach (var (marker, value) in actions)
            {
                if (cancellationToken.IsCancellationRequested) break;
                Raise(run);   // 先把"进行中"的状态推上去
                var ar = await AgentActionExecutor.ExecuteOneAsync(marker, value, run);
                Raise(run);   // 执行完再推一次（带 +N/-N）
                if (ar.TaskDone)
                {
                    result.FinalText = ar.DoneMessage;
                    run.AddStep("done", "任务完成");
                    run.Finish();
                    Raise(run);
                    return result;
                }
                if (!string.IsNullOrWhiteSpace(ar.Observation))
                    observations.AppendLine($"【{marker}】{ar.Observation}");
            }

            // 把这一轮的思考与观察结果回灌，进入下一轮
            messages.Add(new { role = "assistant", content = reply });
            messages.Add(new
            {
                role = "user",
                content = "（动作执行结果）\n" + observations +
                          "\n继续：如果任务还没完成，输出下一步动作指令（独立一行）；"
                          + "如果已全部完成，输出 {done:\"给用户的最终答复\"}。"
            });

            run.AddStep("think", "根据结果继续");
        }

        // —— 收尾：时限到 / 迭代到顶 / 出错 ——
        if (string.IsNullOrEmpty(result.Error))
        {
            if (result.TimedOut)
                run.AddStep("fail", "到达时限，正在收尾");
            else
                run.AddStep("fail", "到达循环上限，正在收尾");

            try
            {
                messages.Add(new
                {
                    role = "user",
                    content = "（系统提示）时间/轮次已到上限，请立即停止执行动作，" +
                              "基于目前已经拿到的信息，直接给用户一个简明的最终答复。输出 {done:\"最终答复\"}。"
                });
                var final = await SendOnceAsync(messages, cancellationToken);
                var doneMsgs = InstructionParser.Extract(final, "done");
                result.FinalText = doneMsgs.Count > 0 ? doneMsgs[0] : CleanReply(final);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
        }

        run.Finish();
        Raise(run);
        return result;
    }

    /// <summary>
    /// history 最后一条是否已经是本轮的同一句用户消息（匿名类型，只能反射取值）。
    /// 用于避免把同一条请求重复发给模型。
    /// </summary>
    private static bool LastUserContentEquals(List<object> messages, string userPrompt)
    {
        if (messages.Count == 0) return false;
        var last = messages[^1];
        if (last == null) return false;

        var role = last.GetType().GetProperty("role")?.GetValue(last) as string;
        if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)) return false;

        var content = last.GetType().GetProperty("content")?.GetValue(last) as string;
        if (content == null) return false;

        // 图片消息会在正文后追加识别结果，用 StartsWith 判定即可
        return content.Trim() == userPrompt.Trim()
            || content.TrimStart().StartsWith(userPrompt.Trim(), StringComparison.Ordinal);
    }

    /// <summary>从模型回复里按出现顺序抽出所有动作指令。</summary>
    private static List<(string marker, string value)> ExtractActions(string reply)
    {
        var found = new List<(int index, string marker, string value)>();
        string[] order = { "todo", "plan", "ls", "read", "write", "append", "edit", "del",
                           "cmd", "api", "browse", "img" };

        foreach (var marker in order)
        {
            var token = $"{{{marker}:\"";
            int idx = 0;
            while (idx < reply.Length)
            {
                int start = reply.IndexOf(token, idx, StringComparison.Ordinal);
                if (start < 0) break;
                int valueStart = start + token.Length;
                int valueEnd = reply.IndexOf('"', valueStart);
                if (valueEnd < 0) break;
                var value = reply[valueStart..valueEnd];
                if (!string.IsNullOrWhiteSpace(value))
                    found.Add((start, marker, value));
                idx = valueEnd + 1;
            }
        }
        // 按在原文里出现的先后顺序执行，保持模型意图的因果次序
        return found.OrderBy(f => f.index).Select(f => (f.marker, f.value)).ToList();
    }

    /// <summary>把回复里的隐藏指令剥掉，只留自然语言。</summary>
    private static string CleanReply(string reply)
    {
        var text = reply;
        foreach (var m in InstructionParser.Markers)
        {
            var token = m;
            int idx = 0;
            while (idx < text.Length)
            {
                int start = text.IndexOf(token, idx, StringComparison.Ordinal);
                if (start < 0) break;
                int end = text.IndexOf('"', start + token.Length);
                if (end < 0) break;
                int close = text.IndexOf('}', end);
                if (close < 0) break;
                text = text.Remove(start, close - start + 1);
                idx = start;
            }
        }
        return text.Trim();
    }

    /// <summary>
    /// 构建 Agent 模式的 system 提示词。
    ///
    /// 结构（顺序很重要，从「最高优先级」到「参考资料」）：
    ///   ① Agent 身份覆盖层 —— 必须放在最前面，压过下面人设里「别啰嗦、直接给结论」的约束；
    ///   ② 人设 + 感知/记忆等 extraSystem —— 保留她的性格与温度；
    ///   ③ 能力清单 —— 她这轮手里有什么工具、什么权限；
    ///   ④ 输出协议 —— 严格格式 + 正反例，这是循环能不能转起来的关键。
    /// </summary>
    private static string BuildAgentSystem(string extraSystem)
    {
        var sb = new StringBuilder();

        // ─────── ① Agent 身份覆盖层（最高优先级，必须最前） ───────
        sb.AppendLine("【最高优先级·工作模式：Agent 循环执行模式（已开启）】");
        sb.AppendLine("本条覆盖你人设中一切与「极简回复」「禁止输出过程」「一句话给结论」相关的约束。");
        sb.AppendLine("在本模式下，你是一个会自己动手把事做完的 Agent，不是一个只会聊天回答的助手。");
        sb.AppendLine();
        sb.AppendLine("你的工作循环是：");
        sb.AppendLine("  观察现状 → 判断缺口 → 执行一个动作 → 拿到真实结果 → 再判断 → …… → 确认做完 → 汇报");
        sb.AppendLine();
        sb.AppendLine("【铁律，违反即任务失败】");
        sb.AppendLine("1. 能自己查证的，绝不猜、绝不编。凡涉及「文件里有什么」「代码怎么写的」「目录下有什么」"
                    + "「网上是什么情况」——先用动作指令去拿真实结果，拿到之前不许下结论。");
        sb.AppendLine("2. 一轮只推进一个明确的步子。不要一次吐出一长串动作指令去赌运气；"
                    + "每一步都要看到结果再决定下一步。");
        sb.AppendLine("3. 拿到结果后必须继续。只要任务还没真正完成，就不许说「已经好了」「你可以自己看看」"
                    + "这类空话收尾，必须输出下一个动作。");
        sb.AppendLine("4. 只有确实做完了、或确实被权限/能力卡住了，才允许结束。结束用 {done:\"...\"} 明确宣告"
                    + "（卡住时说明卡在哪、需要用户做什么）。");
        sb.AppendLine("5. 不要用「我这就去」「让我看看」「稍等」这类过渡废话打发一轮——那等于空转一轮，"
                    + "直接输出动作指令。");
        sb.AppendLine();

        // ─────── ② 人设 + 感知/记忆（保留温度） ───────
        sb.AppendLine("──────── 以下是你的性格与已有认知（在不违反上面铁律的前提下保持本色）────────");
        sb.AppendLine();
        sb.AppendLine(AppSettings.BuildSystemPrompt());

        if (!string.IsNullOrWhiteSpace(extraSystem))
            sb.AppendLine().AppendLine(extraSystem);

        // ─────── ③ 能力清单 ───────
        sb.AppendLine();
        sb.AppendLine("──────── 你这一轮手里的能力 ────────");
        sb.AppendLine();
        sb.AppendLine(FileGuard.BuildCapabilityText());
        sb.AppendLine("【终端命令】{cmd:\"单条 shell 命令\"}（需 Shizuku 授权）——"
                    + "用于查系统设置、应用信息、存储、网络等本机实况。");
        sb.AppendLine("【感知 API】{api:\"名\"} ——可用："
                    + string.Join(" / ", ApiRegistry.All.Select(a => a.Id)) + "。"
                    + "想知道用户此刻在干嘛、睡了没、忙不忙时用它。");
        if (AppSettings.BrowserPermission)
            sb.AppendLine("【上网】{browse:\"网址或搜索词\"} ——抓取网页/搜索的正文内容。");
        else
            sb.AppendLine("【上网】当前用户未授予浏览器权限，{browse:...} 会被直接拒绝，别浪费轮次去试。");
        sb.AppendLine("【生成图片】{img:\"描述\"} ——调用文生图模型，结果会直接显示在聊天里。");

        // ─────── ④ 输出协议（最关键的一段） ───────
        sb.AppendLine();
        sb.AppendLine("──────── 你的输出格式（严格照做，这是循环的命脉） ────────");
        sb.AppendLine();
        sb.AppendLine("每一轮回复 = 【一句给自己看的判断】 + 【动作指令行】。动作指令行必须是独立的一行，"
                    + "格式严格为英文花括号 + 英文双引号：");
        sb.AppendLine();
        sb.AppendLine("  {todo:\"1. 第一步 2. 第二步 3. 第三步\"}   登记你的执行计划（多步任务第一轮必须先做这个）");
        sb.AppendLine("  {read:\"路径\"}                        读文件");
        sb.AppendLine("  {ls:\"路径\"}                          列目录");
        sb.AppendLine("  {write:\"路径|完整内容\"}              覆盖写入（新建文件用这个）");
        sb.AppendLine("  {append:\"路径|追加内容\"}             在文件末尾追加");
        sb.AppendLine("  {edit:\"路径|原文|新内容\"}            精确替换某段内容（改代码/改文件首选）");
        sb.AppendLine("  {del:\"路径\"}                          删除文件");
        sb.AppendLine("  {cmd:\"单条命令\"}                    执行终端命令");
        sb.AppendLine("  {api:\"名\"}                           查感知 API");
        sb.AppendLine("  {browse:\"网址或搜索词\"}              上网抓取");
        sb.AppendLine("  {img:\"描述\"}                         生成图片");
        sb.AppendLine("  {done:\"给用户的最终答复\"}            任务完成，收尾");
        sb.AppendLine();
        sb.AppendLine("客户端会自动执行这些指令，并把真实结果作为「动作执行结果」回传给你。"
                    + "回传内容之后，你必须继续下一轮判断。");
        sb.AppendLine();
        sb.AppendLine("【务必注意】");
        sb.AppendLine("· 指令行是给程序看的，不是给用户看的，所以不要在它旁边解释「我要执行什么」。");
        sb.AppendLine("· 指令行之外那句话可以很短（一句话说清你打算干嘛），但不要写成给用户的正式答复——"
                    + "给用户的正式答复只在 {done:\"...\"} 里出现。");
        sb.AppendLine("· 一轮里只放你**此刻确实需要**的指令。需要先知道结果才能决定下一步的，"
                    + "就这一轮只放那一条，等结果回来再说。");
        sb.AppendLine();
        sb.AppendLine("──────── 正例（照这个来） ────────");
        sb.AppendLine("用户：帮我看看 ChatPage 里思考计时是怎么算的");
        sb.AppendLine();
        sb.AppendLine("第 1 轮你回复：");
        sb.AppendLine("先找到文件、读代码、再下结论。");
        sb.AppendLine("{todo:\"1. 列出工作区找到目标文件 2. 读取文件定位计时逻辑 3. 给出结论\"}");
        sb.AppendLine();
        sb.AppendLine("（客户端回传：workspace 下有 ChatPage.xaml.cs、SettingsPage.xaml 等）");
        sb.AppendLine();
        sb.AppendLine("第 2 轮你回复：");
        sb.AppendLine("目标文件找到了，读它。");
        sb.AppendLine("{read:\"~/ChatPage.xaml.cs\"}");
        sb.AppendLine();
        sb.AppendLine("（客户端回传：文件内容……）");
        sb.AppendLine();
        sb.AppendLine("第 3 轮你回复：");
        sb.AppendLine("{done:\"思考计时是在 ConsumeSseAsync 里记的，从第一个思考字开始、到正文出现时冻结，"
                    + "所以正文开始后就不会再累加了。\"}");
        sb.AppendLine();
        sb.AppendLine("──────── 反例（绝对不要这样） ────────");
        sb.AppendLine("✗ 只说「好的，我来帮你看看」然后不输出任何指令 → 这一轮白跑，循环要空转");
        sb.AppendLine("✗ 凭印象直接回答「大概是 3 秒左右」 → 没查证就下结论，违反铁律 1");
        sb.AppendLine("✗ 一轮里塞 {read:\"a\"} {read:\"b\"} {read:\"c\"} {edit:...} {done:...} → 一次赌太多，"
                    + "中途出错全盘皆输");
        sb.AppendLine("✗ 用 {done:\"我先给你说个大概，你也可以自己打开看看\"} 收尾 → 任务没做完就宣告完成");
        sb.AppendLine();
        sb.AppendLine("──────── 单步任务怎么办 ────────");
        sb.AppendLine("如果一句话就能答完、根本不需要动手（纯闲聊、纯知识问答、纯情绪回应），"
                    + "那就别硬凑步骤，第一轮直接 {done:\"...\"}。");
        sb.AppendLine("判断标准：这件事需不需要去看你此刻看不到的东西（文件、命令输出、网页、用户实况）？"
                    + "需要 → 走循环；不需要 → 直接 {done:...}。");

        return sb.ToString();
    }

    /// <summary>
    /// 单轮请求。用流式接收：这样才能把模型的推理过程实时喂给界面，
    /// 否则一轮十几秒里用户只能盯着转圈，体感上就和"卡住了"没区别。
    /// 对外仍然返回"这一轮的完整正文"，保持调用方逻辑不变。
    /// </summary>
    private static async Task<string> SendOnceAsync(List<object> messages, CancellationToken ct)
    {
        var body = new
        {
            model = AppSettings.ResolveChatModel(AppSettings.ForceThinking),
            stream = true,
            messages,
            max_tokens = 8000,
            // 循环里的每一轮都在做"判断下一步"，推理档位必须给足，否则模型会偷懒不思考
            reasoning_effort = AppSettings.ForceThinking ? "high" : "medium",
            stream_options = new { include_usage = true }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.ApiUrl);
        if (!string.IsNullOrWhiteSpace(AppSettings.ApiKey))
            req.Headers.Add("Authorization", $"Bearer {AppSettings.ApiKey}");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(180));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        string? line;

        while ((line = await reader.ReadLineAsync(cts.Token)) != null)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line[6..];
            if (data == "[DONE]") break;
            if (data.Length == 0) continue;

            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;

                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var delta = choices[0].TryGetProperty("delta", out var d) ? d : default;
                    if (delta.ValueKind == JsonValueKind.Object)
                    {
                        if (delta.TryGetProperty("reasoning_content", out var rc)
                            && rc.ValueKind == JsonValueKind.String)
                        {
                            var piece = rc.GetString();
                            if (!string.IsNullOrEmpty(piece))
                            {
                                reasoning.Append(piece);
                                RaiseThinking(piece);   // 实时喂给界面
                            }
                        }
                        if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                        {
                            var piece = c.GetString();
                            if (!string.IsNullOrEmpty(piece)) content.Append(piece);
                        }
                    }
                }

                // usage 只有最后一帧（stream_options.include_usage）才带
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    var tu = TokenUsageMapper.FromElement(usage);
                    if (tu != null) UsageStats.Current.Apply(tu);
                }
            }
            catch
            {
                // 单帧解析失败不影响整体：跳过继续读
            }
        }

        // 正文优先；正文为空时退回推理内容（部分中转会把动作指令塞进 reasoning）
        if (content.Length > 0) return content.ToString();
        return reasoning.ToString();
    }
}
