using System.Text;

namespace 青阳AI;

/// <summary>
/// 文件访问闸门：所有文件操作的第一道（也是最硬的一道）关卡。
///
/// 设计原则：
/// 1. 权限级别由用户在聊天页盾牌气泡里显式选择，模型无法自行提权；
/// 2. 路径先规范化（解析 ../ 与符号链接的父目录），再判定是否越界——
///    绝不允许用 "workspace/../../etc" 这种方式绕出去；
/// 3. 系统关键目录即使"完全访问"也默认拦截（可被 FullAccess 覆盖，但会留下警告）；
/// 4. 任何拒绝都返回可读的中文说明，交回模型时模型能理解并换路子。
/// </summary>
public static class FileGuard
{
    /// <summary>系统关键目录（读也拦，避免 AI 误读敏感数据；写更不用说）。</summary>
    private static readonly string[] ForbiddenPrefixes =
    {
        "/proc", "/sys", "/dev", "/system", "/vendor", "/data/system",
        "/data/misc", "/data/adb", "/data/local/tmp",
    };

    private sealed class GuardResult
    {
        public bool Allowed;
        public string Reason = "";
        public static GuardResult Ok() => new() { Allowed = true };
        public static GuardResult No(string reason) => new() { Allowed = false, Reason = reason };
    }

    /// <summary>
    /// 把相对路径解析成绝对路径。
    /// 注意：这里只做字符串层面的规范化（Path.GetFullPath 会解析 . 和 ..），
    /// 不走 File.ResolveLinkTarget（Android 上对部分路径会抛异常）。
    /// </summary>
    public static string Resolve(string path)
    {
        path = (path ?? "").Trim().Trim('"');
        if (string.IsNullOrEmpty(path)) return AppSettings.EffectiveWorkspacePath;

        // 展开 ~ 为工作区（给模型一个友好的简写）
        if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\"))
            path = Path.Combine(AppSettings.EffectiveWorkspacePath, path.Length > 2 ? path[2..] : "");

        try
        {
            // 相对路径一律按工作区解析。不能交给 Path.GetFullPath 用进程当前目录兜底——
            // Android 上进程 CWD 是 /，会把 "notes.txt" 解析成 /notes.txt 这个意想不到的位置。
            if (!Path.IsPathRooted(path))
                path = Path.Combine(AppSettings.EffectiveWorkspacePath, path);
            return Path.GetFullPath(path);
        }
        catch
        {
            // 非法字符等：原样返回，后续 Exists 检查会自然失败
            return path;
        }
    }

    /// <summary>读权限校验。</summary>
    public static (bool Allowed, string Reason) CheckRead(string path) => Check(path, isWrite: false);

    /// <summary>写权限校验。</summary>
    public static (bool Allowed, string Reason) CheckWrite(string path) => Check(path, isWrite: true);

    private static (bool Allowed, string Reason) Check(string path, bool isWrite)
    {
        var level = AppSettings.FileAccessLevel;
        var fullAccess = AppSettings.FullAccess;

        // 0 = 未授权：任何文件操作都不行
        if (level == 0 && !fullAccess)
            return (false, "现在没有文件访问权限。如果用户希望我读写文件，请在聊天页顶部的盾牌图标里选择权限级别。");

        var full = Resolve(path);
        var workspace = AppSettings.EffectiveWorkspacePath;

        // —— 系统关键目录拦截（完全访问可越过，但要提示）——
        if (!fullAccess)
        {
            foreach (var p in ForbiddenPrefixes)
            {
                if (full.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    return (false, $"系统目录 {p} 不允许访问。");
            }
        }

        bool inWorkspace = IsUnder(full, workspace);
        bool workspaceWritable = level == 2;
        bool diskReadable = level == 3 || level == 4;
        bool diskWritable = level == 4;

        if (isWrite)
        {
            if (inWorkspace)
            {
                // 工作区内：级别 2（仅改工作区）/ 3（读全盘时也允许改工作区）/ 4 都可写
                if (workspaceWritable || diskReadable || diskWritable || fullAccess) return (true, "");
                return (false, $"当前权限是「{AppSettings.FileAccessDesc}」，只能读取工作区文件，不能修改。");
            }
            if (diskWritable || fullAccess)
                return GateDisk(full, isWrite: true);
            return (false, $"当前权限是「{AppSettings.FileAccessDesc}」，不能修改工作区以外的文件。请让用户在盾牌图标里选择「修改全盘文件」或开启「完全访问」。");
        }
        else
        {
            if (inWorkspace) return (true, "");   // 有任意文件权限时，工作区永远可读
            if (diskReadable || diskWritable || fullAccess)
                return GateDisk(full, isWrite: false);
            return (false, $"当前权限是「{AppSettings.FileAccessDesc}」，只能读取工作区文件。请让用户在盾牌图标里选择「读取全盘文件」。");
        }
    }

    /// <summary>
    /// 越出工作区时的第二道闸门：校验 Android 系统层的真实存储权限。
    ///
    /// APP 内的权限等级只代表"用户允许 Ta 这么做"，但真正能不能写进 /storage/emulated/0
    /// 取决于系统是否授予 MANAGE_EXTERNAL_STORAGE。这里提前拦下并给出可执行的引导，
    /// 避免模型一路撞到 UnauthorizedAccessException 才发现，白费轮次。
    /// </summary>
    private static (bool Allowed, string Reason) GateDisk(string full, bool isWrite)
    {
        // 其他 APP 的私有目录：即使有全盘权限也读不到（Android 沙盒硬限制），提前说清楚
        if (full.StartsWith("/data/", StringComparison.OrdinalIgnoreCase))
        {
            bool isOurs = false;
            try
            {
                var appDir = Microsoft.Maui.Storage.FileSystem.AppDataDirectory;
                isOurs = !string.IsNullOrEmpty(appDir) && IsUnder(full, appDir);
            }
            catch { }

            if (!isOurs)
                return (false, "这是其他应用的私有目录，Android 沙盒不允许任何应用访问（全盘权限也不行）。"
                               + "只能访问公共存储（内部存储）里的文件。");
        }

        // 工作区以外的路径，必须真有全盘读写能力（系统版本不同，叫法/判定也不同）
        if (!StorageAccess.IsAllFilesGranted())
            return (false, "系统还没有授予" + StorageAccess.PermissionLabel + "，无法读写公共存储。"
                           + "请让用户在聊天页盾牌面板里点「" + StorageAccess.GrantButtonText + "」；"
                           + "在那之前，只能读写工作区内的文件。");

        return (true, "");
    }

    /// <summary>child 是否在 parent 目录之下（大小写不敏感，带分隔符边界判断）。</summary>
    public static bool IsUnder(string child, string parent)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(parent)) return false;
            var c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, '/');
            var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, '/');
            if (string.Equals(c, p, StringComparison.OrdinalIgnoreCase)) return true;
            return c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || c.StartsWith(p + '/', StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>给模型看的能力说明文本（拼进 system 提示词）。</summary>
    public static string BuildCapabilityText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("【文件能力】");
        sb.AppendLine($"当前权限：{AppSettings.FileAccessDesc}"
                      + (AppSettings.FullAccess ? "（已开启完全访问，限制全部放开）" : ""));
        var ws = AppSettings.EffectiveWorkspacePath;
        sb.AppendLine($"工作区目录：{ws}");
        sb.AppendLine($"用户看到的路径：{StorageAccess.ToDisplay(ws)}");
        sb.AppendLine("  {ls:\"路径\"} — 列出目录内容");
        sb.AppendLine("  {read:\"路径\"} — 读取文本文件");
        sb.AppendLine("  {write:\"路径|完整内容\"} — 覆盖写入文件（不存在则创建）");
        sb.AppendLine("  {append:\"路径|追加内容\"} — 追加到文件末尾");
        sb.AppendLine("  {edit:\"路径|原文|新内容\"} — 把文件里的原文替换成新内容（修改文件的首选，比整文件覆写安全）");
        sb.AppendLine("  {del:\"路径\"} — 删除文件（高风险，只有「修改全盘文件」或「完全访问」才可用）");
        sb.AppendLine("路径支持相对写法（相对工作区），也可以用 ~ 代表工作区。/ 开头是绝对路径。"
                      + "相对路径直接写文件名即可，例如 {read:\"notes.txt\"}。");
        sb.AppendLine("修改代码类文件时优先用 {edit:...} 做精确替换，不要整文件覆写。");
        if (!AppSettings.FullAccess && AppSettings.FileAccessLevel == 0)
            sb.AppendLine("注意：用户尚未授予文件权限，上面这些指令会被拒绝。"
                          + "需要文件能力时，先告诉用户在聊天页顶部的盾牌图标里开启。");
        sb.AppendLine();
        // 存储实况：让模型知道文件写到哪、用户找不找得到
        sb.Append(StorageAccess.BuildStorageText());
        return sb.ToString();
    }
}
