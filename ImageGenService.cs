using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace 青阳AI;

/// <summary>
/// 文生图统一入口。支持**三种后端**，并且尽量把生成过程中的**进度与中间预览帧**实时推给界面。
///
/// 后端与预览能力：
///   · **SD-WebUI / Forge** —— 提交后轮询 `/sdapi/v1/progress`，拿 `progress`（百分比）
///     和 `current_image`（服务端 VAE 解码好的预览）→ **有进度、有预览**
///   · **ComfyUI** —— websocket 收 `preview` 二进制帧（服务端解码好的）→ **有预览**
///     （需要一份工作流，设置里留空就用内置 SD1.5 的）
///   · **OpenAI 兼容** —— 先试 Responses API 的 `partial_images`（SSE 推 `partial_image_b64`）
///     → **有预览、无百分比**；不支持就退回标准 `/images/generations`
///
/// 三条路都不通 → 走标准接口，并明确告诉界面「当前模型不支持实时预览」。
///
/// ⚠️ 地址原样使用，不做补全（用户决定不修 404 那条）。`ImgApiUrl` 要填**完整端点**。
/// </summary>
public static class ImageGenService
{
    /// <summary>生图后端类型。</summary>
    public enum Backend { OpenAi, ComfyUI, SdWebUi }

    /// <summary>生成过程中的一次进度回报。</summary>
    public sealed class ImageProgress
    {
        /// <summary>百分比；-1 = 这个后端不给百分比。</summary>
        public int Percent { get; set; } = -1;

        /// <summary>显示在转圈下面那行字；空 = 界面不显示这一行。</summary>
        public string Note { get; set; } = "";

        /// <summary>中间预览帧（JPEG/PNG 字节）；null = 这次没带预览。</summary>
        public byte[]? Preview { get; set; }

        /// <summary>true = 后端**明确**不支持实时预览（界面显示小字提示）。</summary>
        public bool Unsupported { get; set; }
    }

    /// <summary>一次生成的结果。</summary>
    public sealed class Result
    {
        public bool Ok { get; set; }
        public byte[]? Bytes { get; set; }
        public string? B64 { get; set; }
        public string Error { get; set; } = "";
        public int Attempts { get; set; }
    }

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>探测出来的后端（null = 还没探过）。</summary>
    private static Backend? _detected;

    /// <summary>地址或后端设置变了就调一下，下次重新探测。</summary>
    public static void Forget() => _detected = null;

    // ───────────── 对外主入口 ─────────────

    /// <summary>生成一张图。会尽量通过 <paramref name="progress"/> 推中间预览帧与进度。</summary>
    public static async Task<Result> GenerateAsync(string prompt,
                                                  IProgress<ImageProgress>? progress = null,
                                                  CancellationToken ct = default)
    {
        var r = new Result();

        if (!AppSettings.ImgEnabled)
        {
            r.Error = "没有配置文生图模型（设置 → 文生图：接口地址和 API Key 都要填）";
            return r;
        }
        if (string.IsNullOrWhiteSpace(prompt))
        {
            r.Error = "图片描述是空的";
            return r;
        }

        var backend = await DetectAsync(ct);
        Result primary;

        try
        {
            primary = backend switch
            {
                Backend.SdWebUi => await GenerateSdAsync(prompt, progress, ct),
                Backend.ComfyUI => await GenerateComfyAsync(prompt, progress, ct),
                _               => await GenerateOpenAiStreamAsync(prompt, progress, ct),
            };
        }
        catch (Exception ex)
        {
            primary = new Result { Error = "调用生图后端失败：" + ex.Message };
        }

        if (primary.Ok) return primary;

        // 兜底：标准 /images/generations —— 这条路**没有任何中间帧**，
        // 所以明确告诉界面"不支持实时预览"，让用户知道不是卡住了。
        progress?.Report(new ImageProgress { Unsupported = true, Note = "" });

        var std = await GenerateStandardAsync(prompt, ct);
        if (std.Ok) return std;

        // 两条路都挂了：优先报更具体的那个错
        std.Error = !string.IsNullOrWhiteSpace(std.Error) ? std.Error : primary.Error;
        return std;
    }

    // ───────────── 后端探测 ─────────────

    private static async Task<Backend> DetectAsync(CancellationToken ct)
    {
        var configured = (AppSettings.ImgBackend ?? "auto").Trim().ToLowerInvariant();
        if (configured == "openai") return Backend.OpenAi;
        if (configured == "comfy") return Backend.ComfyUI;
        if (configured == "sd") return Backend.SdWebUi;

        if (_detected.HasValue) return _detected.Value;

        var url = (AppSettings.ImgApiUrl ?? "").Trim();
        var lower = url.ToLowerInvariant();

        // 地址里能直接看出来的，先看地址
        if (lower.Contains("/sdapi/")) return Cache(Backend.SdWebUi);
        if (lower.Contains(":8188") || lower.Contains("/comfy")) return Cache(Backend.ComfyUI);

        // 看不出来就各探一个便宜接口
        var baseUrl = DeriveBase(url);
        if (baseUrl != null)
        {
            if (await ProbeAsync(baseUrl + "/sdapi/v1/options", ct)) return Cache(Backend.SdWebUi);
            if (await ProbeAsync(baseUrl + "/system_stats", ct)) return Cache(Backend.ComfyUI);
        }

        return Cache(Backend.OpenAi);
    }

    private static Backend Cache(Backend b) { _detected = b; return b; }

    private static async Task<bool> ProbeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var resp = await _http.GetAsync(url, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>从完整端点里推出站点根（去掉 /v1/images/generations、/sdapi/... 这些尾巴）。</summary>
    private static string? DeriveBase(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var u = new Uri(url.Trim());
            return u.GetLeftPart(UriPartial.Authority).TrimEnd('/');   // scheme://host:port
        }
        catch { return null; }
    }

    // ───────────── ① SD-WebUI / Forge ─────────────

    private static async Task<Result> GenerateSdAsync(string prompt,
                                                      IProgress<ImageProgress>? progress,
                                                      CancellationToken ct)
    {
        var r = new Result();
        var baseUrl = DeriveBase(AppSettings.ImgApiUrl);
        if (baseUrl == null) { r.Error = "SD-WebUI 地址解析不出来"; return r; }

        var body = new
        {
            prompt,
            negative_prompt = "",
            steps = 20,
            width = 512,
            height = 512,
            cfg_scale = 7,
            sampler_name = "Euler a"
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/sdapi/v1/txt2img");
        if (!string.IsNullOrWhiteSpace(AppSettings.ImgApiKey))
            req.Headers.Add("Authorization", $"Bearer {AppSettings.ImgApiKey}");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        // txt2img 是**同步阻塞**的（跑完才返回），所以一边等一边轮询 progress 拿预览
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var genTask = _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        var poller = Task.Run(async () =>
        {
            try
            {
                while (!pollCts.IsCancellationRequested)
                {
                    await Task.Delay(600, pollCts.Token);

                    using var pr = await _http.GetAsync(
                        baseUrl + "/sdapi/v1/progress?skip_current_image=false", pollCts.Token);
                    if (!pr.IsSuccessStatusCode) continue;

                    var pj = await pr.Content.ReadAsStringAsync(pollCts.Token);
                    using var doc = JsonDocument.Parse(pj);
                    var root = doc.RootElement;

                    int pct = -1;
                    if (root.TryGetProperty("progress", out var pv) && pv.ValueKind == JsonValueKind.Number)
                        pct = (int)Math.Round(Math.Clamp(pv.GetDouble(), 0, 1) * 100);

                    byte[]? preview = null;
                    if (root.TryGetProperty("current_image", out var ci) && ci.ValueKind == JsonValueKind.String)
                    {
                        var s = ci.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            try { preview = Convert.FromBase64String(s); } catch { }
                        }
                    }

                    if (pct >= 0 || preview != null)
                        progress?.Report(new ImageProgress
                        {
                            Percent = pct,
                            Note = pct >= 0 ? $"正在生成… {pct}%" : "正在生成…",
                            Preview = preview
                        });
                }
            }
            catch { }
        }, pollCts.Token);

        try
        {
            using var resp = await genTask;
            pollCts.Cancel();
            if (!resp.IsSuccessStatusCode) { r.Error = HumanizeStatus((int)resp.StatusCode); return r; }

            var json = await resp.Content.ReadAsStringAsync(ct);
            var b64 = FirstStringInArray(json, "images");
            if (string.IsNullOrWhiteSpace(b64)) { r.Error = "SD-WebUI 没有返回图片数据"; return r; }

            r.Bytes = Convert.FromBase64String(b64);
            r.B64 = b64;
            r.Ok = true;
            r.Attempts = 1;
            return r;
        }
        catch (OperationCanceledException) { r.Error = "生成超时（超过 5 分钟）"; return r; }
        catch (Exception ex) { r.Error = "SD-WebUI 调用失败：" + ex.Message; return r; }
        finally
        {
            try { pollCts.Cancel(); } catch { }
            _ = poller;
        }
    }

    // ───────────── ② ComfyUI ─────────────

    private static async Task<Result> GenerateComfyAsync(string prompt,
                                                         IProgress<ImageProgress>? progress,
                                                         CancellationToken ct)
    {
        var r = new Result();
        var baseUrl = DeriveBase(AppSettings.ImgApiUrl);
        if (baseUrl == null) { r.Error = "ComfyUI 地址解析不出来"; return r; }

        var clientId = Guid.NewGuid().ToString("N");
        var wsBase = baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? "wss://" + baseUrl[8..]
            : baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? "ws://" + baseUrl[7..]
                : "ws://" + baseUrl;

        using var ws = new ClientWebSocket();
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await ws.ConnectAsync(new Uri($"{wsBase}/ws?clientId={clientId}"), pumpCts.Token);
        }
        catch (Exception ex)
        {
            r.Error = "连不上 ComfyUI 的 websocket（" + ex.Message + "）";
            return r;
        }

        // 先把监听挂上，再提交任务 —— 否则最早的预览帧会漏掉
        var pump = Task.Run(() => PumpComfyAsync(ws, progress, pumpCts.Token), pumpCts.Token);

        // 提交任务
        JsonElement workflow;
        try { workflow = BuildComfyWorkflow(prompt); }
        catch (Exception ex) { r.Error = "ComfyUI 工作流 JSON 解析失败：" + ex.Message; return r; }

        var submitBody = JsonSerializer.Serialize(new { prompt = workflow, client_id = clientId });

        string? promptId;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/prompt");
            req.Content = new StringContent(submitBody, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) { r.Error = "ComfyUI 提交任务失败（HTTP " + (int)resp.StatusCode + "）"; return r; }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            promptId = doc.RootElement.TryGetProperty("prompt_id", out var pid) ? pid.GetString() : null;
            if (string.IsNullOrWhiteSpace(promptId)) { r.Error = "ComfyUI 没返回任务号"; return r; }
        }
        catch (Exception ex) { r.Error = "ComfyUI 提交任务失败：" + ex.Message; return r; }

        // 轮询 /history 等它跑完，再取图
        try
        {
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(700, ct);

                using var hresp = await _http.GetAsync($"{baseUrl}/history/{promptId}", ct);
                if (!hresp.IsSuccessStatusCode) continue;

                var hj = await hresp.Content.ReadAsStringAsync(ct);
                var file = FindComfyOutputFile(hj, promptId);
                if (file == null) continue;

                using var iresp = await _http.GetAsync(
                    $"{baseUrl}/view?filename={Uri.EscapeDataString(file.Value.name)}" +
                    $"&subfolder={Uri.EscapeDataString(file.Value.subfolder)}" +
                    $"&type={Uri.EscapeDataString(file.Value.type)}", ct);
                if (!iresp.IsSuccessStatusCode) { r.Error = "ComfyUI 取图失败"; return r; }

                r.Bytes = await iresp.Content.ReadAsByteArrayAsync(ct);
                if (r.Bytes.Length == 0) { r.Error = "ComfyUI 返回了空图片"; return r; }
                r.Ok = true;
                r.Attempts = 1;
                return r;
            }

            r.Error = "ComfyUI 生成超时（超过 5 分钟）";
            return r;
        }
        catch (OperationCanceledException) { r.Error = "ComfyUI 生成超时"; return r; }
        catch (Exception ex) { r.Error = "ComfyUI 取图失败：" + ex.Message; return r; }
        finally
        {
            try { pumpCts.Cancel(); } catch { }
            _ = pump;
        }
    }

    /// <summary>收 ComfyUI 的 websocket：`preview` 二进制帧就是服务端解码好的预览图。</summary>
    private static async Task PumpComfyAsync(ClientWebSocket ws,
                                             IProgress<ImageProgress>? progress,
                                             CancellationToken ct)
    {
        var buffer = new byte[512 * 1024];
        var assembled = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                assembled.SetLength(0);
                WebSocketReceiveResult res;
                do
                {
                    res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (res.MessageType == WebSocketMessageType.Close) return;
                    assembled.Write(buffer, 0, res.Count);
                }
                while (!res.EndOfMessage);

                if (res.MessageType != WebSocketMessageType.Binary) continue;

                var bytes = assembled.ToArray();
                // 二进制帧：4 字节事件类型 + 4 字节 payload 类型 + 数据
                // 事件类型 1 = PREVIEW_IMAGE；payload 1 = JPEG / 2 = PNG
                if (bytes.Length < 8) continue;
                int eventType = (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
                if (eventType != 1) continue;

                var image = new byte[bytes.Length - 8];
                Array.Copy(bytes, 8, image, 0, image.Length);
                if (image.Length > 0)
                    progress?.Report(new ImageProgress { Note = "正在生成…", Preview = image });
            }
        }
        catch { /* 断开 / 取消都正常，不往上抛 */ }
    }

    /// <summary>内置默认 ComfyUI 工作流（SD1.5）。用户可在设置里贴自己的覆盖掉。</summary>
    private const string DefaultComfyWorkflow = """
{
  "3": { "class_type": "KSampler", "inputs": { "seed": 0, "steps": 20, "cfg": 7, "sampler_name": "euler", "scheduler": "normal", "denoise": 1, "model": ["4", 0], "positive": ["6", 0], "negative": ["7", 0], "latent_image": ["5", 0] } },
  "4": { "class_type": "CheckpointLoaderSimple", "inputs": { "ckpt_name": "v1-5-pruned-emaonly.safetensors" } },
  "5": { "class_type": "EmptyLatentImage", "inputs": { "width": 512, "height": 512, "batch_size": 1 } },
  "6": { "class_type": "CLIPTextEncode", "inputs": { "text": "%PROMPT%", "clip": ["4", 1] } },
  "7": { "class_type": "CLIPTextEncode", "inputs": { "text": "", "clip": ["4", 1] } },
  "8": { "class_type": "VAEDecode", "inputs": { "samples": ["3", 0], "vae": ["4", 2] } },
  "9": { "class_type": "SaveImage", "inputs": { "filename_prefix": "qingyang", "images": ["8", 0] } }
}
""";

    /// <summary>把工作流里的 %PROMPT% 换成用户描述，解析成 JsonElement 供提交。</summary>
    private static JsonElement BuildComfyWorkflow(string prompt)
    {
        var raw = string.IsNullOrWhiteSpace(AppSettings.ComfyWorkflow)
            ? DefaultComfyWorkflow
            : AppSettings.ComfyWorkflow;

        var filled = raw.Replace("%PROMPT%", EscapeJsonString(prompt));
        using var doc = JsonDocument.Parse(filled);
        return doc.RootElement.Clone();
    }

    /// <summary>往 JSON 字符串里塞用户文本时要转义。</summary>
    private static string EscapeJsonString(string s)
        => JsonSerializer.Serialize(s).Trim('"');

    /// <summary>从 /history 的返回里挑出第一张输出图。</summary>
    private static (string name, string subfolder, string type)? FindComfyOutputFile(string historyJson, string promptId)
    {
        try
        {
            using var doc = JsonDocument.Parse(historyJson);
            if (!doc.RootElement.TryGetProperty(promptId, out var entry)) return null;
            if (!entry.TryGetProperty("outputs", out var outputs)) return null;

            foreach (var node in outputs.EnumerateObject())
            {
                if (!node.Value.TryGetProperty("images", out var imgs) || imgs.ValueKind != JsonValueKind.Array)
                    continue;
                if (imgs.GetArrayLength() == 0) continue;

                var first = imgs[0];
                var name = first.TryGetProperty("filename", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var sub = first.TryGetProperty("subfolder", out var s) ? s.GetString() : "";
                var type = first.TryGetProperty("type", out var t) ? t.GetString() : "output";
                return (name!, sub ?? "", type ?? "output");
            }
        }
        catch { }
        return null;
    }

    // ───────────── ③ OpenAI 兼容（先试 partial_images 流式，再退标准）─────────────

    private static async Task<Result> GenerateOpenAiStreamAsync(string prompt,
                                                                IProgress<ImageProgress>? progress,
                                                                CancellationToken ct)
    {
        var r = new Result();
        var baseUrl = DeriveBase(AppSettings.ImgApiUrl);
        if (baseUrl == null) { r.Error = "生图地址解析不出来"; return r; }

        try
        {
            // Responses API：由一个**文本模型**驱动 image_generation 工具，
            // partial_images 让服务端在采样过程中推中间帧（服务端 VAE 解码好的）。
            var body = new
            {
                model = AppSettings.ResolveChatModel(false),
                input = prompt,
                tools = new object[] { new { type = "image_generation", partial_images = 3 } },
                stream = true
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/responses");
            if (!string.IsNullOrWhiteSpace(AppSettings.ImgApiKey))
                req.Headers.Add("Authorization", $"Bearer {AppSettings.ImgApiKey}");
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                r.Error = HumanizeStatus((int)resp.StatusCode);
                return r;   // 交给外面退回标准接口
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            string? finalB64 = null;
            string? finalUrl = null;

            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

                var data = line[6..];
                if (data == "[DONE]") break;
                if (data.Length == 0) continue;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var tv) ? tv.GetString() : null;

                    // 中间预览帧
                    if (type == "response.image_generation_call.partial_image"
                        && root.TryGetProperty("partial_image_b64", out var pb)
                        && pb.ValueKind == JsonValueKind.String)
                    {
                        var b64 = pb.GetString();
                        if (!string.IsNullOrWhiteSpace(b64))
                        {
                            byte[]? frame = null;
                            try { frame = Convert.FromBase64String(b64); } catch { }
                            progress?.Report(new ImageProgress { Note = "正在生成…", Preview = frame });
                        }
                        continue;
                    }

                    // 最终图
                    if (type == "response.image_generation_call.done" || type == "response.output_item.done")
                    {
                        var (b64, url) = ScanForImage(root);
                        if (!string.IsNullOrWhiteSpace(b64)) finalB64 = b64;
                        if (!string.IsNullOrWhiteSpace(url)) finalUrl = url;
                    }
                }
                catch { /* 单个事件解析失败不影响整体 */ }
            }

            if (string.IsNullOrWhiteSpace(finalB64) && string.IsNullOrWhiteSpace(finalUrl))
            {
                r.Error = "Responses 流里没拿到最终图片";
                return r;
            }

            if (!string.IsNullOrWhiteSpace(finalB64)
                && !finalB64.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                r.Bytes = Convert.FromBase64String(finalB64);
                r.B64 = finalB64;
            }
            else
            {
                var link = !string.IsNullOrWhiteSpace(finalUrl) ? finalUrl : finalB64!;
                r.Bytes = await _http.GetByteArrayAsync(link, ct);
            }

            r.Ok = true;
            r.Attempts = 1;
            return r;
        }
        catch (OperationCanceledException) { r.Error = "生成超时"; return r; }
        catch (Exception ex) { r.Error = "Responses 流式生图失败：" + ex.Message; return r; }
    }

    /// <summary>在任意 JSON 子树里找 base64 图片或图片链接（各家中转字段名不一致，广撒网）。</summary>
    private static (string? b64, string? url) ScanForImage(JsonElement el)
    {
        string? b64 = null, url = null;

        void Walk(JsonElement node, int depth)
        {
            if (depth > 6 || (b64 != null && url != null)) return;

            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in node.EnumerateObject())
                {
                    var n = p.Name.ToLowerInvariant();
                    if (p.Value.ValueKind == JsonValueKind.String)
                    {
                        var v = p.Value.GetString();
                        if (string.IsNullOrWhiteSpace(v)) continue;
                        if (b64 == null && (n.Contains("b64") || n.Contains("base64"))) b64 = v;
                        else if (url == null && n == "url" && v.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = v;
                    }
                    else Walk(p.Value, depth + 1);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray()) Walk(item, depth + 1);
            }
        }

        Walk(el, 0);
        return (b64, url);
    }

    // ───────────── ④ 标准 /images/generations（兜底，无预览）─────────────

    private static async Task<Result> GenerateStandardAsync(string prompt, CancellationToken ct)
    {
        var r = new Result();
        var model = string.IsNullOrWhiteSpace(AppSettings.ImgModel) ? "gpt-image-1" : AppSettings.ImgModel;

        // 第 1 次：完整参数；第 2 次：只留 model + prompt（兼容不认那两个字段的服务商）
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            r.Attempts = attempt;
            try
            {
                // 用 JsonObject 而不是匿名类型 —— 这样"关水印"字段才能灵活并进去
                var node = new JsonObject
                {
                    ["model"] = model,
                    ["prompt"] = prompt
                };
                if (attempt == 1)
                {
                    node["response_format"] = "b64_json";
                    node["size"] = "1024x1024";
                }
                ApplyWatermarkSetting(node);

                using var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.ImgApiUrl);
                req.Headers.Add("Authorization", $"Bearer {AppSettings.ImgApiKey}");
                req.Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");

                using var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    r.Error = HumanizeStatus((int)resp.StatusCode);
                    continue;
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                var (b64, url) = ParseStandardImage(json);
                if (string.IsNullOrWhiteSpace(b64) && string.IsNullOrWhiteSpace(url))
                {
                    r.Error = "接口没有返回图片数据";
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(b64)
                    && !b64.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    r.Bytes = Convert.FromBase64String(b64);
                    r.B64 = b64;
                }
                else
                {
                    var link = !string.IsNullOrWhiteSpace(url) ? url : b64!;
                    r.Bytes = await _http.GetByteArrayAsync(link, ct);
                }

                r.Ok = true;
                r.Error = "";
                return r;
            }
            catch (OperationCanceledException) { r.Error = "生成超时（超过 5 分钟）"; }
            catch (Exception ex) { r.Error = "调用生图接口失败：" + ex.Message; }
        }

        return r;
    }

    /// <summary>解析 data[0] 里的 b64_json / url。</summary>
    private static (string? b64, string? url) ParseStandardImage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
            {
                var first = data[0];
                string? b = first.TryGetProperty("b64_json", out var bv) ? bv.GetString() : null;
                string? u = first.TryGetProperty("url", out var uv) ? uv.GetString() : null;
                return (b, u);
            }
        }
        catch { }
        return (null, null);
    }

    /// <summary>取 JSON 里某个数组字段的第一个字符串（SD-WebUI 的 images 就是这种）。</summary>
    private static string? FirstStringInArray(string json, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(field, out var arr)
                && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            {
                var v = arr[0];
                return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 按设置往请求体里并"关水印"字段。
    ///
    /// 各家服务商这个开关的字段名不统一（watermark / watermark_enabled / add_watermark / …），
    /// 所以做成**可选集合 + 自定义**：用户在设置里挑一个能用的，或者直接填一段 JSON。
    /// 默认（空）**什么都不发**，和以前的行为完全一致。
    /// </summary>
    private static void ApplyWatermarkSetting(JsonObject body)
    {
        switch ((AppSettings.ImgWatermarkMode ?? "").Trim())
        {
            case "":                        return;   // 默认：不处理
            case "watermark_false":         body["watermark"] = false; return;
            case "watermark_enabled_false": body["watermark_enabled"] = false; return;
            case "add_watermark_false":     body["add_watermark"] = false; return;
            case "disable_watermark_true":  body["disable_watermark"] = true; return;
            case "no_watermark_true":       body["no_watermark"] = true; return;
            case "watermark_0":             body["watermark"] = 0; return;

            case "custom":
                var raw = (AppSettings.ImgWatermarkCustom ?? "").Trim();
                if (raw.Length == 0) return;
                try
                {
                    if (JsonNode.Parse(raw) is JsonObject extra)
                        foreach (var kv in extra)
                            body[kv.Key] = kv.Value?.DeepClone();
                }
                catch { /* 用户填的 JSON 不合法，就当没填 */ }
                return;
        }
    }

    /// <summary>把 HTTP 状态码翻成人话。</summary>
    private static string HumanizeStatus(int code) => code switch
    {
        401 or 403 => "生图 API Key 无效或没有权限",
        404 => "生图接口地址不对（设置里的地址要填完整端点，比如 https://…/v1/images/generations）",
        429 => "生图请求太频繁，稍后再试",
        >= 500 => "生图服务商那边出错了，稍后再试",
        400 or 422 => "服务商不接受这组参数（已经试过简化参数了）",
        _ => $"生图接口返回 HTTP {code}"
    };
}
