namespace 青阳AI;

/// <summary>
/// 隐藏指令协议解析：{cmd:"..."} / {api:"..."} / {img:"..."} / {mood:"..."} / {avatar:"..."} / {say:"..."}。
/// - Extract：从整段文本提取某个标记的所有值；
/// - MatchLen：显示层过滤用——判断缓冲区某位置是否是指令开头（指令不打字上屏，只做副作用）。
/// </summary>
public static class InstructionParser
{
    /// <summary>全部指令标记（按最长匹配优先无需排序，逐个全字比对）。</summary>
    public static readonly string[] Markers =
    {
        "{cmd:\"", "{api:\"", "{img:\"", "{mood:\"", "{avatar:\"", "{say:\"",
    };

    /// <summary>提取 content 里所有 {marker:"值"} 的值。</summary>
    public static List<string> Extract(string content, string marker)
    {
        var results = new List<string>();
        if (string.IsNullOrEmpty(content)) return results;

        var token = "{marker:\"".Replace("marker", marker);
        int idx = 0;
        while (idx < content.Length)
        {
            int start = content.IndexOf(token, idx, StringComparison.Ordinal);
            if (start < 0) break;
            int valueStart = start + token.Length;
            int valueEnd = content.IndexOf('"', valueStart);
            if (valueEnd < 0) break;
            var value = content[valueStart..valueEnd].Trim();
            if (!string.IsNullOrEmpty(value)) results.Add(value);
            idx = valueEnd + 1;
        }
        return results;
    }

    /// <summary>
    /// 显示层过滤：buf 的 pos 处是否指令开头。
    /// 返回 >0 = 命中（值即标记长度，显示层应跳过整段 token）；
    /// -1 = 流未收完且尾部疑似指令前缀，等更多字符再判断；0 = 不是指令。
    /// </summary>
    public static int MatchLen(System.Text.StringBuilder buf, int pos, bool readDone)
    {
        foreach (var m in Markers)
        {
            int avail = buf.Length - pos;
            if (avail >= m.Length)
            {
                bool eq = true;
                for (int j = 0; j < m.Length; j++)
                {
                    if (buf[pos + j] != m[j]) { eq = false; break; }
                }
                if (eq) return m.Length;
            }
            else
            {
                bool prefix = true;
                for (int j = 0; j < avail; j++)
                {
                    if (buf[pos + j] != m[j]) { prefix = false; break; }
                }
                if (prefix && !readDone) return -1;
            }
        }
        return 0;
    }
}
