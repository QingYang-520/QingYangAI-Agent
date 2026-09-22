using System.Text;
// 全限定别名：Android 目标的隐式 using 里也有 Permission/PermissionStatus 相关类型，避免歧义
using MauiPermissions = Microsoft.Maui.ApplicationModel.Permissions;
using MauiPermissionStatus = Microsoft.Maui.ApplicationModel.PermissionStatus;

namespace 青阳AI;

/// <summary>
/// 真实存储权限 + 公共存储路径的唯一入口。
///
/// 为什么需要这一层：
/// 之前「完全访问」只是 APP 内部的虚拟开关——它只决定 FileGuard 放不放行，
/// 但 Android 系统层面并没有授予 MANAGE_EXTERNAL_STORAGE，于是写 /storage/emulated/0
/// 会直接抛 UnauthorizedAccessException；而且默认工作区落在 app 私有沙盒
/// （/data/user/0/包名/files/workspace），用户在文件管理器里根本看不到生成的文件。
///
/// 本类负责：
///   1. 查询/申请系统「所有文件访问」权限；
///   2. 给出公共存储（内部存储）下的默认工作区；
///   3. 把内部绝对路径翻译成人能看懂的路径；
///   4. 把沙盒里的文件一键搬运到公共 Download。
/// </summary>
public static class StorageAccess
{
    /// <summary>公共存储根：Android 上是 /storage/emulated/0（即"内部存储"）。</summary>
    public static string PublicRoot
    {
        get
        {
#if ANDROID
            try
            {
                var dir = Android.OS.Environment.ExternalStorageDirectory;
                if (dir?.AbsolutePath is { Length: > 0 } p) return p.TrimEnd('/');
            }
            catch { }
            return "/storage/emulated/0";
#else
            return "";
#endif
        }
    }

    /// <summary>公共 Download 目录。</summary>
    public static string PublicDownloads
    {
        get
        {
#if ANDROID
            try
            {
                var dir = Android.OS.Environment.GetExternalStoragePublicDirectory(
                    Android.OS.Environment.DirectoryDownloads);
                if (dir?.AbsolutePath is { Length: > 0 } p) return p.TrimEnd('/');
            }
            catch { }
#endif
            var root = PublicRoot;
            return string.IsNullOrEmpty(root) ? "" : Path.Combine(root, "Download");
        }
    }

    /// <summary>我们自己的公共存储根目录名：内部存储/QingYangAI。</summary>
    public const string AppFolderName = "QingYangAI";

    /// <summary>默认工作区子目录名。</summary>
    public const string WorkspaceFolderName = "WorkSpace";

    /// <summary>
    /// 授予全盘权限后，默认工作区落在公共存储：
    /// /storage/emulated/0/QingYangAI/WorkSpace —— 文件管理器里能直接看到。
    ///
    /// 这是一个"约定路径"（纯字符串拼接，不碰磁盘），
    /// 真正建目录由 AppSettings.EnsureDefaultWorkspace() 触发。
    /// </summary>
    public static string DefaultPublicWorkspace
    {
        get
        {
            var root = PublicRoot;
            if (string.IsNullOrEmpty(root)) return "";
            // 注意：这里刻意不用 Path.Combine 逐段拼，避免不同平台分隔符差异导致
            // 提示词里露出的路径和用户在文件管理器看到的不一致
            return root.TrimEnd('/') + "/" + AppFolderName + "/" + WorkspaceFolderName;
        }
    }

    /// <summary>
    /// 逐级创建默认工作区目录。中间任何一级缺失都会自动补建。
    /// 返回是否可用（失败不抛异常，交给调用方决定怎么提示）。
    /// </summary>
    public static bool EnsureDefaultWorkspace(out string reason)
    {
        reason = "";
        var target = DefaultPublicWorkspace;
        if (string.IsNullOrEmpty(target))
        {
            reason = "无法解析公共存储路径。";
            return false;
        }
        if (!IsAllFilesGranted())
        {
            reason = "尚未授予系统「所有文件访问」权限。";
            return false;
        }

        try
        {
            // Directory.CreateDirectory 本身就会逐级建好缺失的父目录，
            // 但这里显式分两级，方便在某一级失败时给出准确原因。
            var root = PublicRoot;
            if (!string.IsNullOrEmpty(root) && !Directory.Exists(root))
            {
                reason = $"公共存储根目录不存在：{root}";
                return false;
            }

            var appDir = Path.Combine(root, AppFolderName);
            Directory.CreateDirectory(appDir);

            Directory.CreateDirectory(target);

            if (!Directory.Exists(target))
            {
                reason = "目录创建后仍不存在，可能是存储不可写。";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = "创建目录失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>默认工作区当前是否已经真实存在。</summary>
    public static bool DefaultWorkspaceExists()
    {
        var target = DefaultPublicWorkspace;
        if (string.IsNullOrEmpty(target)) return false;
        try { return Directory.Exists(target); }
        catch { return false; }
    }

    // ─────────── 系统权限 ───────────

    /// <summary>
    /// 是否已获得全盘读写能力。
    ///
    /// ① 先问系统：Android 11+ 看「所有文件访问」（MANAGE_EXTERNAL_STORAGE）；
    ///    Android 10 及以下没有那个开关，看普通存储权限（配 requestLegacyExternalStorage）。
    /// ② 系统说"没有"时，再做一次**真实写入探测** ——
    ///    因为部分 ROM（实测：鸿蒙）的系统开关和实际能力对不上：
    ///    用户明明已经在系统设置里给了权限，开关却还是 false，
    ///    结果「完全访问」被永久锁死，点了没反应。
    ///    与其信开关，不如直接试着写一下。
    /// </summary>
    public static bool IsAllFilesGranted()
        => SuperAdmin.IsStorageGranted() || ProbePublicWritable();

    private static bool? _probeOk;
    private static DateTime _probeAt;

    /// <summary>
    /// 去公共存储根目录写一个探针文件再删掉 —— 这是"到底能不能写"的最终事实，
    /// 不依赖任何 ROM 的权限开关实现。结果缓存 10 秒，避免每次刷面板都做 I/O。
    /// </summary>
    private static bool ProbePublicWritable()
    {
        if (_probeOk.HasValue && (DateTime.UtcNow - _probeAt).TotalSeconds < 10)
            return _probeOk.Value;

        bool ok = false;
        try
        {
            var root = PublicRoot;
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
            {
                var probe = root.TrimEnd('/') + "/.qy_write_probe";
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                ok = true;
            }
        }
        catch { ok = false; }

        _probeOk = ok;
        _probeAt = DateTime.UtcNow;
        return ok;
    }

    /// <summary>权限状态可能变了（刚授权回来）时清掉探测缓存。</summary>
    public static void InvalidateProbe() { _probeOk = null; }

    /// <summary>这个权限在界面上叫什么（不同系统版本叫法不一样）。</summary>
    public static string PermissionLabel
        => SuperAdmin.HasAllFilesAccessApi ? "系统「所有文件访问」" : "系统「存储」权限";

    /// <summary>「去开启」按钮上的文案。</summary>
    public static string GrantButtonText
        => SuperAdmin.HasAllFilesAccessApi ? "去开启「所有文件访问」" : "去授予存储权限";

    /// <summary>
    /// 引导用户拿到全盘读写能力。
    /// Android 11+ ：跳系统「所有文件访问」页面；
    /// Android 10- ：系统里根本没有那个页面，直接申请运行时存储权限；
    ///               被永久拒绝时再跳到本应用的系统详情页让用户手动开。
    /// 返回调用后是否已经拿到权限。
    /// </summary>
    public static async Task<bool> RequestPermissionAsync()
    {
        if (IsAllFilesGranted()) return true;

        if (SuperAdmin.HasAllFilesAccessApi)
        {
            SuperAdmin.OpenStorageSettings();
            return false;
        }

        try
        {
            var w = await MauiPermissions.CheckStatusAsync<MauiPermissions.StorageWrite>();
            if (w != MauiPermissionStatus.Granted)
                w = await MauiPermissions.RequestAsync<MauiPermissions.StorageWrite>();

            var r = await MauiPermissions.CheckStatusAsync<MauiPermissions.StorageRead>();
            if (r != MauiPermissionStatus.Granted)
                r = await MauiPermissions.RequestAsync<MauiPermissions.StorageRead>();

            if (w == MauiPermissionStatus.Granted || r == MauiPermissionStatus.Granted) return true;

            // 用户选了「不再询问」之类 → 只能去应用详情页手动开
            SuperAdmin.OpenAppSettings();
        }
        catch { }
        return false;
    }

    /// <summary>不等待结果的版本（兼容旧的 void 调用点）。</summary>
    public static void RequestPermission() { _ = RequestPermissionAsync(); }

    // ─────────── 路径识别与显示 ───────────

    /// <summary>该路径是否位于 APP 私有沙盒内（外部文件管理器不可见）。</summary>
    public static bool IsSandboxed(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var appDir = FileSystem.AppDataDirectory;   // /data/user/0/包名/files
            if (string.IsNullOrEmpty(appDir)) return false;
            return FileGuard.IsUnder(Path.GetFullPath(path), appDir);
        }
        catch { return false; }
    }

    /// <summary>
    /// 把内部绝对路径翻译成人能看懂的路径：
    ///   /storage/emulated/0/Download/a.py → 内部存储/Download/a.py
    ///   /data/user/0/包名/files/workspace/a.py → 应用私有目录/a.py（外部不可见）
    /// </summary>
    public static string ToDisplay(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        string full;
        try { full = Path.GetFullPath(path); }
        catch { return path; }

        var root = PublicRoot;
        if (!string.IsNullOrEmpty(root) && FileGuard.IsUnder(full, root))
        {
            var rel = full.Length > root.Length ? full[(root.Length + 1)..] : "";
            return rel.Length > 0 ? "内部存储/" + rel.Replace('\\', '/') : "内部存储";
        }

        try
        {
            var appDir = FileSystem.AppDataDirectory;
            if (!string.IsNullOrEmpty(appDir) && FileGuard.IsUnder(full, appDir))
            {
                var rel = full.Length > appDir.Length ? full[(appDir.Length + 1)..] : "";
                return (rel.Length > 0 ? "应用私有目录/" + rel.Replace('\\', '/') : "应用私有目录")
                       + "（外部不可见）";
            }
        }
        catch { }

        return path;
    }

    /// <summary>工作区当前是否对外可见（决定要不要提示"外部看不到"）。</summary>
    public static bool WorkspaceVisible => !IsSandboxed(AppSettings.EffectiveWorkspacePath);

    // ─────────── 目录准备 ───────────

    /// <summary>确保目录存在。返回是否可用。绝不抛异常。</summary>
    public static bool EnsureDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        try
        {
            Directory.CreateDirectory(dir);
            return true;
        }
        catch { return false; }
    }

    // ─────────── 导出到公共存储 ───────────

    /// <summary>
    /// 把沙盒里的文件/目录复制到公共 Download 目录，让用户能在文件管理器里找到。
    /// 返回结果说明（成功/失败原因），绝不抛异常。
    /// </summary>
    public static async Task<(bool Ok, string Message)> ExportAsync(string path)
    {
        if (!IsAllFilesGranted())
            return (false, "还没有系统「所有文件访问」权限，无法写入公共存储。请先在盾牌面板里点「去开启」。");

        var downloads = PublicDownloads;
        if (string.IsNullOrEmpty(downloads))
            return (false, "找不到公共 Download 目录。");

        try
        {
            var full = FileGuard.Resolve(path);

            if (File.Exists(full))
            {
                var target = Path.Combine(downloads, Path.GetFileName(full));
                target = UniquePath(target);
                EnsureDir(downloads);
                await CopyFileAsync(full, target);
                return (true, $"已导出到 {ToDisplay(target)}");
            }

            if (Directory.Exists(full))
            {
                var dirName = new DirectoryInfo(full).Name;
                if (string.IsNullOrEmpty(dirName)) dirName = "workspace";
                var targetRoot = Path.Combine(downloads, dirName);
                targetRoot = UniquePath(targetRoot);
                int n = await CopyDirAsync(full, targetRoot);
                return (true, $"已导出 {n} 个文件到 {ToDisplay(targetRoot)}");
            }

            return (false, $"找不到要导出的路径：{path}");
        }
        catch (Exception ex)
        {
            return (false, "导出失败：" + ex.Message);
        }
    }

    /// <summary>把整个工作区导出到公共 Download。</summary>
    public static Task<(bool Ok, string Message)> ExportWorkspaceAsync()
        => ExportAsync(AppSettings.EffectiveWorkspacePath);

    // ─────────── 沙盒 → 公共存储 迁移 ───────────

    /// <summary>旧的沙盒工作区路径（不管权限如何，固定为 app 私有目录下的 workspace/）。</summary>
    private static string SandboxWorkspace
    {
        get
        {
            try { return Path.Combine(FileSystem.AppDataDirectory, "workspace"); }
            catch { return ""; }
        }
    }

    /// <summary>
    /// 用户首次授予全盘权限后，把原先落在沙盒里的工作区文件搬到公共存储，
    /// 免得"一授权文件就消失"（其实还在沙盒，只是用户看不到、Ta 也不再往那儿写）。
    /// 目标已存在同名文件时跳过，不覆盖。
    /// </summary>
    public static async Task<(bool Ok, string Message)> MigrateSandboxToPublicAsync()
    {
        var target = DefaultPublicWorkspace;
        var source = SandboxWorkspace;

        if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(source))
            return (false, "路径解析失败。");
        if (!IsAllFilesGranted())
            return (false, "还没有系统存储权限。");
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            return (false, "无需迁移。");
        if (!Directory.Exists(source))
            return (false, "沙盒工作区不存在，无需迁移。");

        try
        {
            // 确保目标目录（含中间层级）存在，否则复制会直接失败
            if (!EnsureDefaultWorkspace(out var reason))
                return (false, "无法准备目标目录：" + reason);

            int moved = 0, skipped = 0;

            foreach (var f in Directory.GetFiles(source))
            {
                var dst = Path.Combine(target, Path.GetFileName(f));
                if (File.Exists(dst)) { skipped++; continue; }
                await CopyFileAsync(f, dst);
                moved++;
            }

            if (moved == 0 && skipped == 0) return (false, "沙盒工作区是空的。");

            var msg = $"已把 {moved} 个文件搬到 {ToDisplay(target)}";
            if (skipped > 0) msg += $"（{skipped} 个同名文件已存在，跳过）";
            return (true, msg);
        }
        catch (Exception ex)
        {
            return (false, "迁移失败：" + ex.Message);
        }
    }

    /// <summary>目标已存在时自动加 (1)(2) 后缀，避免覆盖用户文件。</summary>
    private static string UniquePath(string target)
    {
        if (!File.Exists(target) && !Directory.Exists(target)) return target;
        var dir = Path.GetDirectoryName(target) ?? "";
        var name = Path.GetFileNameWithoutExtension(target);
        var ext = Path.GetExtension(target);
        for (int i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name}({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return target;
    }

    private static async Task CopyFileAsync(string src, string dst)
    {
        using var input = File.OpenRead(src);
        using var output = File.Create(dst);
        await input.CopyToAsync(output);
    }

    private static async Task<int> CopyDirAsync(string srcDir, string dstDir)
    {
        EnsureDir(dstDir);
        int count = 0;
        foreach (var f in Directory.GetFiles(srcDir))
        {
            await CopyFileAsync(f, Path.Combine(dstDir, Path.GetFileName(f)));
            count++;
        }
        foreach (var d in Directory.GetDirectories(srcDir))
            count += await CopyDirAsync(d, Path.Combine(dstDir, Path.GetFileName(d)));
        return count;
    }

    // ─────────── 给模型看的说明 ───────────

    /// <summary>
    /// 告诉模型"你现在能写到哪、用户能不能看到"。
    /// 这段会拼进 system 提示词，直接决定它会不会把文件写到用户找不到的地方。
    /// </summary>
    public static string BuildStorageText()
    {
        var sb = new StringBuilder();
        var ws = AppSettings.EffectiveWorkspacePath;
        var granted = IsAllFilesGranted();

        sb.AppendLine("【存储实况】");
        sb.AppendLine($"工作区：{ws}");
        sb.AppendLine($"用户可见性：{(WorkspaceVisible ? "可见（用户在文件管理器里能找到）" : "不可见（这是 APP 私有目录，用户在外面找不到）")}");
        sb.AppendLine($"系统「所有文件访问」权限：{(granted ? "已授予" : "未授予")}");

        if (granted)
        {
            sb.AppendLine("默认工作区是 " + DefaultPublicWorkspace + "（内部存储里，用户能直接看到），"
                          + "中间目录不存在时会自动建好，你直接读写即可。");
            sb.AppendLine("也可以写到公共存储的其他位置（如 " + PublicRoot + "/Download/xxx），用完整绝对路径。");
        }
        else
        {
            sb.AppendLine("你只能读写工作区（APP 私有目录），无法访问 "
                          + (PublicRoot.Length > 0 ? PublicRoot : "公共存储") + " 等公共路径。");
            sb.AppendLine("如果要写到公共存储，先告诉用户在聊天页盾牌图标里开启" + PermissionLabel + "。");
        }

        sb.AppendLine("注意：即使开启全盘权限，也只能访问公共存储，读不到其他 APP 的私有 /data 目录（Android 沙盒限制）。");
        return sb.ToString();
    }

    /// <summary>把内部绝对路径翻译成人能看懂的路径（供 UI 提示复用）。</summary>
    public static string DescribeDefaultWorkspace()
        => ToDisplay(DefaultPublicWorkspace);
}
