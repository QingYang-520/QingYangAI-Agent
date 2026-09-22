using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace 青阳AI;

/// <summary>
/// AI 的上网工具：
///   · {browse:"url"}  —— 后台访问网页，提取正文回传（只读文本）
///   · {download:"url"} —— 把文件真的下下来存到工作区（二进制，见 DownloadAsync）
/// 走 HttpClient（宿主网络栈：Wi-Fi/蜂窝/DNS/代理），不需要 UI WebView。
/// </summary>
public static class BrowserTool
{
    /// <summary>抓网页正文用（短超时，读不到就赶紧报错）。</summary>
    private static readonly HttpClient _http;

    /// <summary>下文件用（长超时——一个几十 MB 的包 20 秒下不完）。</summary>
    private static readonly HttpClient _dl;

    /// <summary>Cookie 罐子：让 HttpClient 也保持登录态（设置里「保存 Cookie」控制）。</summary>
    private static readonly System.Net.CookieContainer _cookies = new();

    /// <summary>单个文件下载上限兜底（设置里可调，默认 300 MB）。</summary>
    public const long DefaultMaxDownloadBytes = 300L * 1024 * 1024;

    static BrowserTool()
    {
        // 自动跟随重定向 + 自动解压 + 共享 Cookie 罐
        System.Net.Http.HttpClientHandler NewHandler() => new()
        {
            CookieContainer = _cookies,
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            UseCookies = true
        };

        _http = new HttpClient(NewHandler()) { Timeout = TimeSpan.FromSeconds(20) };
        _dl = new HttpClient(NewHandler()) { Timeout = TimeSpan.FromMinutes(10) };

        ApplySettings();
    }

    /// <summary>把设置里的 UA 应用到两个 HttpClient（设置页改 UA 时调用）。</summary>
    public static void ApplySettings()
    {
        try
        {
            foreach (var c in new[] { _http, _dl })
            {
                c.DefaultRequestHeaders.UserAgent.Clear();
                c.DefaultRequestHeaders.UserAgent.ParseAdd(AppSettings.EffectiveUserAgent);
            }
        }
        catch { }
    }

    /// <summary>清掉 HttpClient 这边的 Cookie（设置页「清除 Cookie」调用）。</summary>
    public static void ClearCookies()
    {
        try { _cookies.Clear(); } catch { }
    }

    /// <summary>当前生效的下载上限（字节）。</summary>
    private static long MaxBytes => Math.Max(1, AppSettings.BrowserMaxDownloadMb) * 1024L * 1024;

    /// <summary>
    /// 后台访问一个 URL，返回正文摘要（标题 + 正文，≤6000 字）。
    /// 失败返回错误说明文字（不抛异常）。
    /// </summary>
    public static async Task<string> FetchAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "URL 为空";

        // 规范化 URL
        url = NormalizeUrl(url);

        try
        {
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
                return $"HTTP {resp.StatusCode}";

            var html = await resp.Content.ReadAsStringAsync();

            var title = ExtractTitle(html);
            var text = ExtractText(html);

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(title))
                sb.AppendLine("标题：" + title.Trim());
            sb.AppendLine("URL：" + url);
            sb.AppendLine();
            sb.AppendLine("正文：");
            sb.AppendLine(text);

            return sb.ToString();
        }
        catch (TaskCanceledException)
        {
            return "访问超时（20 秒）";
        }
        catch (Exception ex)
        {
            return "访问失败：" + ex.Message;
        }
    }

    /// <summary>同时访问多个 URL（并发），合并结果。</summary>
    public static async Task<string> FetchMultipleAsync(List<string> urls)
    {
        if (urls.Count == 0) return "没有要访问的 URL";
        if (urls.Count == 1) return await FetchAsync(urls[0]);

        var tasks = urls.Select(FetchAsync).ToList();
        var results = await Task.WhenAll(tasks);

        var sb = new StringBuilder();
        for (int i = 0; i < results.Length; i++)
        {
            sb.AppendLine($"━━━ 页面 {i + 1} ━━━");
            sb.AppendLine(results[i]);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ───────────── 下载文件 ─────────────

    /// <summary>
    /// 真的把一个文件下下来，存进工作区（`{download:"url"}` 走这里）。
    ///
    /// 这是 Ta「把东西弄到本地」的唯一通道 —— FetchAsync 只把网页正文读成文字，
    /// 遇到 apk / zip / 图片这类二进制它什么也拿不到，只能把网址念给用户听。
    ///
    /// 返回 (是否成功, 给人看的说明, 落盘的绝对路径)。绝不抛异常。
    /// </summary>
    public static async Task<(bool Ok, string Message, string Path)> DownloadAsync(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
            return (false, "没给下载地址。", "");

        var url = NormalizeUrl(rawUrl);

        try
        {
            using var resp = await _dl.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
                return (false, $"下载失败：HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}", "");

            var declared = resp.Content.Headers.ContentLength;
            if (declared is > 0 && declared.Value > MaxBytes)
                return (false, $"文件太大（{HumanSize(declared.Value)}），超过 {AppSettings.BrowserMaxDownloadMb} MB 上限，没下。", "");

            // 落盘目录：设置里选了「公共 Download」且确实能写就用它，否则退回工作区
            var dir = AppSettings.EffectiveWorkspacePath;
            if (AppSettings.BrowserDownloadDir == "download" && StorageAccess.IsAllFilesGranted())
            {
                var dl = StorageAccess.PublicDownloads;
                if (!string.IsNullOrEmpty(dl)) dir = dl;
            }
            if (string.IsNullOrWhiteSpace(dir))
                return (false, "保存目录解析失败。", "");
            if (!StorageAccess.EnsureDir(dir))
                return (false, "保存目录建不出来，可能没有写权限。", "");

            var full = UniquePath(Path.Combine(dir, GuessFileName(resp, url)));

            long written = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(full))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    written += n;
                    if (written > MaxBytes)
                    {
                        await dst.DisposeAsync();
                        try { File.Delete(full); } catch { }
                        return (false, $"文件超过 {AppSettings.BrowserMaxDownloadMb} MB 上限，已中断并删掉半截文件。", "");
                    }
                    await dst.WriteAsync(buf.AsMemory(0, n));
                }
            }

            AppSettings.AddBrowserHistory("下载 " + Path.GetFileName(full) + $"（{HumanSize(written)}）");
            var shown = StorageAccess.ToDisplay(full);
            var note = StorageAccess.WorkspaceVisible ? "" : "（注意：工作区在应用私有目录，文件管理器看不到，需要的话点「导出工作区到 Download」搬出来）";
            return (true, $"已下载 {Path.GetFileName(full)}，{HumanSize(written)}，位置：{shown}{note}", full);
        }
        catch (TaskCanceledException)
        {
            return (false, "下载超时（超过 10 分钟）。", "");
        }
        catch (Exception ex)
        {
            return (false, "下载失败：" + ex.Message, "");
        }
    }

    // ───────────── 内部工具 ─────────────

    private static string NormalizeUrl(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return s;
        // 看起来像域名就补 https://
        if (s.Contains('.') && !s.Contains(' '))
            return "https://" + s;
        // 不像 URL 就当搜索
        return "https://www.bing.com/search?q=" + Uri.EscapeDataString(s);
    }

    private static string ExtractTitle(string html)
    {
        try
        {
            var m = Regex.Match(html, @"<title[^>]*>(.*?)</title>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (m.Success)
            {
                var t = m.Groups[1].Value.Trim();
                return DecodeHtmlEntities(t);
            }
        }
        catch { }
        return "";
    }

    /// <summary>
    /// 从 HTML 抽取正文文本：去 script/style/nav/footer/header，
    /// 取 body（或 main/article），去标签，压缩空白，截断 6000 字。
    /// </summary>
    private static string ExtractText(string html)
    {
        try
        {
            // 去 script、style、noscript、svg、iframe 块
            html = Regex.Replace(html,
                @"<(script|style|noscript|svg|iframe)[^>]*>.*?</\1>",
                "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // 去 nav、footer、header、aside 块
            html = Regex.Replace(html,
                @"<(nav|footer|header|aside|form)[^>]*>.*?</\1>",
                "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // 去 HTML 注释
            html = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);

            // 取 body（优先 main > article > body）
            string body = html;
            var mainMatch = Regex.Match(html, @"<main[^>]*>(.*?)</main>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (mainMatch.Success)
                body = mainMatch.Groups[1].Value;
            else
            {
                var articleMatch = Regex.Match(html, @"<article[^>]*>(.*?)</article>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (articleMatch.Success)
                    body = articleMatch.Groups[1].Value;
                else
                {
                    var bodyMatch = Regex.Match(html, @"<body[^>]*>(.*?)</body>",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (bodyMatch.Success)
                        body = bodyMatch.Groups[1].Value;
                }
            }

            // 去所有标签
            body = Regex.Replace(body, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            body = Regex.Replace(body, @"</p>", "\n\n", RegexOptions.IgnoreCase);
            body = Regex.Replace(body, @"</div>", "\n", RegexOptions.IgnoreCase);
            body = Regex.Replace(body, @"<[^>]+>", "");

            // 解码 HTML 实体
            body = DecodeHtmlEntities(body);

            // 压缩空白
            body = Regex.Replace(body, @"[ \t]+", " ");
            body = Regex.Replace(body, @"\n{3,}", "\n\n");
            body = body.Trim();

            // 截断
            if (body.Length > 6000)
                body = body.Substring(0, 6000) + "…（截断）";

            return body;
        }
        catch (Exception ex)
        {
            return "正文提取失败：" + ex.Message;
        }
    }

    private static string DecodeHtmlEntities(string s)
    {
        return s
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Replace("&hellip;", "…")
            .Replace("&mdash;", "—")
            .Replace("&ndash;", "–");
    }

    // ───────────── 下载用的小工具 ─────────────

    /// <summary>猜文件名：优先 Content-Disposition，其次 URL 末段，最后按 MIME 兜底。</summary>
    private static string GuessFileName(HttpResponseMessage resp, string url)
    {
        // 1) Content-Disposition: attachment; filename="xxx.apk"
        try
        {
            var cd = resp.Content.Headers.ContentDisposition;
            var n = cd?.FileNameStar ?? cd?.FileName;
            if (!string.IsNullOrWhiteSpace(n))
                return Sanitize(n.Trim().Trim('"'));
        }
        catch { }

        // 2) URL 路径最后一段（GitHub releases 的直链一般带文件名）
        try
        {
            var last = new Uri(url).AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(last) && last.Contains('.'))
                return Sanitize(Uri.UnescapeDataString(last));
        }
        catch { }

        // 3) 按 MIME 猜扩展名，再不行就 .bin
        var ext = ".bin";
        try
        {
            ext = (resp.Content.Headers.ContentType?.MediaType ?? "") switch
            {
                "application/vnd.android.package-archive" => ".apk",
                "application/zip" => ".zip",
                "application/x-zip-compressed" => ".zip",
                "application/pdf" => ".pdf",
                "application/json" => ".json",
                "text/plain" => ".txt",
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/webp" => ".webp",
                _ => ".bin"
            };
        }
        catch { }
        return "下载_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ext;
    }

    /// <summary>洗掉文件名里的非法字符和路径分隔符——防止 `../` 之类越界写到工作区外面。</summary>
    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Replace('/', '_').Replace('\\', '_').Replace("..", "_");
        if (name.Length > 120) name = name[..120];
        return string.IsNullOrWhiteSpace(name) ? "download.bin" : name;
    }

    /// <summary>重名自动加 (1)(2)…，不覆盖已有文件。</summary>
    private static string UniquePath(string target)
    {
        if (!File.Exists(target)) return target;
        var dir = Path.GetDirectoryName(target) ?? "";
        var stem = Path.GetFileNameWithoutExtension(target);
        var ext = Path.GetExtension(target);
        for (int i = 1; i < 1000; i++)
        {
            var c = Path.Combine(dir, $"{stem}({i}){ext}");
            if (!File.Exists(c)) return c;
        }
        return target;
    }

    private static string HumanSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.0") + " MB";
        return (bytes / 1024.0 / 1024 / 1024).ToString("0.00") + " GB";
    }
}
