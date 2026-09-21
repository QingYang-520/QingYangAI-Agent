using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace 青阳AI;

/// <summary>
/// AI 的上网工具：用 {browse:"url"} 让 AI 后台访问网页，提取正文回传。
/// 走 HttpClient（宿主网络栈：Wi-Fi/蜂窩/DNS/代理），不需要 UI WebView。
/// 抽取正文：去 script/style/nav/footer，取 body innerText，截断 6000 字。
/// </summary>
public static class BrowserTool
{
    private static readonly HttpClient _http = new();

    static BrowserTool()
    {
        // 带上 UA，避免被一些网站挡
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36");
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

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
}
