namespace 青阳AI;

/// <summary>
/// 极简 Markdown → MAUI 视图渲染器。**只为协议页服务**，故意只支持协议正文用到的那几种语法：
///   `# 一级标题` / `## 二级标题` / `**行内加粗**` / `1\. 数字列表` / 空行分段。
///
/// 为什么不用 Markdig / WebView：
///   · 语法就这几种，手写百来行够了，不值得为一个法务文档引 NuGet 依赖；
///   · 项目已踩过 MAUI 10 WebView 的坑（`IntroPage` 得 `DispatchDelayed(80ms)` 才不闪退），
///     为一个纯文本页再引一个 WebView 不划算。
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>把 Markdown 文本渲染成一个可直接塞进 ScrollView 的纵向布局。</summary>
    public static View Render(string markdown)
    {
        var stack = new VerticalStackLayout { Spacing = 9 };
        if (string.IsNullOrWhiteSpace(markdown)) return stack;

        foreach (var raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;   // 空行靠 Spacing 体现段落间隔

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                stack.Add(MakeLabel(line[3..], 18, AppSettings.ThemeColor, true, 0, 14));
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                stack.Add(MakeLabel(line[2..], 23, Colors.White, true, 0, 18));
            }
            else
            {
                bool isList = IsListLine(line);
                stack.Add(MakeLabel(Unescape(line),
                                    isList ? 14.5 : 14,
                                    isList ? Colors.White : Color.FromArgb("#E9E1E0"),
                                    false,
                                    isList ? 14 : 0,
                                    isList ? 4 : 6));
            }
        }

        return stack;
    }

    // ───────────── 内部 ─────────────

    /// <summary>是不是 `1\. ` / `12\. ` 这种数字列表行。</summary>
    private static bool IsListLine(string line)
    {
        int i = 0;
        while (i < line.Length && char.IsDigit(line[i])) i++;
        if (i == 0) return false;                                        // 不是数字开头
        if (i + 1 < line.Length && line[i] == '\\' && line[i + 1] == '.') return true;  // "1\."
        return i < line.Length && line[i] == '.';                        // "1."
    }

    /// <summary>还原 markdown 转义：`\.` → `.`、`\*` → `*`、`\_` → `_`、`\\` → `\`。</summary>
    private static string Unescape(string s)
        => s.Replace("\\\\", "\u0001")
            .Replace("\\.", ".")
            .Replace("\\*", "*")
            .Replace("\\_", "_")
            .Replace("\u0001", "\\");

    /// <summary>
    /// 造一行 Label。行内 `**加粗**` 会拆成多个 Span —— 按 `**` 切分后，**奇数段加粗**。
    /// （偶数段是正常文字、奇数段是被包裹的内容，所以是 i % 2 == 1）
    /// </summary>
    private static Label MakeLabel(string text, double fontSize, Color color, bool bold,
                                   double leftMargin, double bottomMargin)
    {
        var fs = new FormattedString();

        var parts = text.Split("**");
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            fs.Spans.Add(new Span
            {
                Text = parts[i],
                FontSize = fontSize,
                TextColor = color,
                FontAttributes = (bold || i % 2 == 1) ? FontAttributes.Bold : FontAttributes.None
            });
        }

        // 兜底：整行只有一个 ** 之类的情况，别渲染出空 Label
        if (fs.Spans.Count == 0)
            fs.Spans.Add(new Span { Text = text, FontSize = fontSize, TextColor = color });

        return new Label
        {
            FormattedText = fs,
            LineBreakMode = LineBreakMode.WordWrap,
            Margin = new Thickness(leftMargin, 0, 0, bottomMargin)
        };
    }
}
