using System.Text;

namespace 青阳AI;

/// <summary>
/// AI 的文件工具：读 / 写 / 追加 / 替换 / 列目录 / 删除。
/// 所有操作都先过权限闸门（<see cref="FileGuard"/>），越界直接拒绝并返回说明文字，
/// 不抛异常给上层——调用方永远拿到一段可读文本。
/// </summary>
public static class FileTool
{
    /// <summary>单次读取的最大字符数（防止把整个大文件灌进上下文）。</summary>
    public const int MaxReadChars = 20000;

    /// <summary>单次列目录最多返回多少个条目。</summary>
    public const int MaxListEntries = 200;

    // ─────────── 主要操作 ───────────

    /// <summary>读取文本文件。返回内容或拒绝/错误说明。</summary>
    public static async Task<string> ReadAsync(string path)
    {
        var check = FileGuard.CheckRead(path);
        if (!check.Allowed) return check.Reason;

        try
        {
            var full = FileGuard.Resolve(path);
            if (!File.Exists(full)) return $"文件不存在：{path}";
            var info = new FileInfo(full);
            if (info.Length > 2 * 1024 * 1024) return $"文件太大（{info.Length / 1024 / 1024}MB），暂不支持读取。";

            var text = await File.ReadAllTextAsync(full, Encoding.UTF8);
            if (text.Length > MaxReadChars)
                text = text[..MaxReadChars] + $"\n\n…（已截断，全文共 {text.Length} 字）";
            return $"已读取 {StorageAccess.ToDisplay(full)}（{info.Length} 字节）：\n\n{text}";
        }
        catch (Exception ex)
        {
            return $"读取失败：{ex.Message}";
        }
    }

    /// <summary>写入（覆盖）文本文件，自动建目录。返回结果说明 + 行数变化。</summary>
    public static async Task<FileOpResult> WriteAsync(string path, string content)
    {
        var check = FileGuard.CheckWrite(path);
        if (!check.Allowed) return FileOpResult.Denied(check.Reason);

        try
        {
            var full = FileGuard.Resolve(path);
            var oldLines = File.Exists(full) ? CountLines(await File.ReadAllTextAsync(full, Encoding.UTF8)) : 0;
            var newLines = CountLines(content);

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content, Encoding.UTF8);

            var delta = newLines - oldLines;
            return new FileOpResult
            {
                Success = true,
                Added = Math.Max(0, delta),
                Removed = Math.Max(0, -delta),
                Message = $"已写入 {StorageAccess.ToDisplay(full)}（{newLines} 行）"
            };
        }
        catch (Exception ex)
        {
            return FileOpResult.Failed($"写入失败：{ex.Message}");
        }
    }

    /// <summary>把文本追加到文件末尾（不存在则创建）。</summary>
    public static async Task<FileOpResult> AppendAsync(string path, string content)
    {
        var check = FileGuard.CheckWrite(path);
        if (!check.Allowed) return FileOpResult.Denied(check.Reason);

        try
        {
            var full = FileGuard.Resolve(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.AppendAllTextAsync(full, content, Encoding.UTF8);
            var added = CountLines(content);
            return new FileOpResult
            {
                Success = true,
                Added = added,
                Message = $"已追加到 {StorageAccess.ToDisplay(full)}（+{added} 行）"
            };
        }
        catch (Exception ex)
        {
            return FileOpResult.Failed($"追加失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 精确替换文件中的一段文本（oldText → newText），只替换第一处。
    /// 这是 AI 修改代码的主要手段：比整文件覆写安全得多。
    /// </summary>
    public static async Task<FileOpResult> EditAsync(string path, string oldText, string newText)
    {
        var check = FileGuard.CheckWrite(path);
        if (!check.Allowed) return FileOpResult.Denied(check.Reason);

        try
        {
            var full = FileGuard.Resolve(path);
            if (!File.Exists(full)) return FileOpResult.Failed($"文件不存在：{path}");

            var text = await File.ReadAllTextAsync(full, Encoding.UTF8);
            var idx = text.IndexOf(oldText, StringComparison.Ordinal);
            if (idx < 0)
                return FileOpResult.Failed($"在 {path} 里没找到要替换的内容（请确认原文完全一致）。");

            var updated = text[..idx] + newText + text[(idx + oldText.Length)..];
            await File.WriteAllTextAsync(full, updated, Encoding.UTF8);

            var added = CountLines(newText);
            var removed = CountLines(oldText);
            return new FileOpResult
            {
                Success = true,
                Added = added,
                Removed = removed,
                Message = $"已编辑 {StorageAccess.ToDisplay(full)}（+{added} -{removed} 行）"
            };
        }
        catch (Exception ex)
        {
            return FileOpResult.Failed($"编辑失败：{ex.Message}");
        }
    }

    /// <summary>列出目录内容（目录在前，含文件大小）。</summary>
    public static async Task<string> ListAsync(string path)
    {
        var check = FileGuard.CheckRead(path);
        if (!check.Allowed) return check.Reason;

        try
        {
            var full = FileGuard.Resolve(path);
            if (!Directory.Exists(full)) return $"目录不存在：{path}";

            var dirs = Directory.GetDirectories(full).OrderBy(d => d).ToList();
            var files = Directory.GetFiles(full).OrderBy(f => f).ToList();

            var sb = new StringBuilder($"目录 {path}（{dirs.Count} 个子目录 / {files.Count} 个文件）：\n");
            int count = 0;
            foreach (var d in dirs)
            {
                if (count++ >= MaxListEntries) { sb.AppendLine("…（条目过多，已截断）"); break; }
                sb.AppendLine("[目录] " + Path.GetFileName(d) + "/");
            }
            foreach (var f in files)
            {
                if (count++ >= MaxListEntries) { sb.AppendLine("…（条目过多，已截断）"); break; }
                try
                {
                    var len = new FileInfo(f).Length;
                    sb.AppendLine($"[文件] {Path.GetFileName(f)}  ({len} 字节)");
                }
                catch { sb.AppendLine("[文件] " + Path.GetFileName(f)); }
            }
            return await Task.FromResult(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return $"列目录失败：{ex.Message}";
        }
    }

    /// <summary>删除文件。⚠️ 高风险：完全访问除外，一律要求调用方二次确认。</summary>
    public static Task<FileOpResult> DeleteAsync(string path)
    {
        var check = FileGuard.CheckWrite(path);
        if (!check.Allowed) return Task.FromResult(FileOpResult.Denied(check.Reason));

        try
        {
            var full = FileGuard.Resolve(path);
            if (!File.Exists(full)) return Task.FromResult(FileOpResult.Failed($"文件不存在：{path}"));

            // 删除前记下原行数，用于统计"减少了多少行"
            var removed = CountLines(File.ReadAllText(full, Encoding.UTF8));
            File.Delete(full);
            return Task.FromResult(new FileOpResult
            {
                Success = true,
                Removed = removed,
                Message = $"已删除 {StorageAccess.ToDisplay(full)}（-{removed} 行）"
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(FileOpResult.Failed($"删除失败：{ex.Message}"));
        }
    }

    /// <summary>统计行数（空串 = 0 行）。</summary>
    public static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int n = 1;
        foreach (var c in text)
            if (c == '\n') n++;
        // 以换行结尾时不算多出最后一行
        return text.EndsWith('\n') ? n - 1 : n;
    }
}

/// <summary>文件操作结果：成功与否 + 行数增减（供缩略行显示 +N/-N，并汇总）。</summary>
public sealed class FileOpResult
{
    public bool Success { get; set; }
    /// <summary>新增行数（绿色 +N）。</summary>
    public int Added { get; set; }
    /// <summary>删除行数（红色 -N）。</summary>
    public int Removed { get; set; }
    /// <summary>给模型/用户看的说明文字。</summary>
    public string Message { get; set; } = "";

    public static FileOpResult Denied(string reason) => new() { Success = false, Message = reason };
    public static FileOpResult Failed(string msg) => new() { Success = false, Message = msg };
}
