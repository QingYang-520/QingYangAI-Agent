#if ANDROID
using Rikka.Shizuku;
using Moe.Shizuku.Server;
using Android.Content;
using Android;
#endif
using System.Text;

namespace 青阳AI;

/// <summary>
/// 通过 Shizuku 执行本地 shell 命令。
/// </summary>
public static class ShizukuRunner
{
    #if ANDROID
private static IShizukuService? _service;
#else
private static object? _service;
#endif

    /// <summary>Shizuku 授权请求码。</summary>
    public const int PermissionRequestCode = 100;

    /// <summary>授权结果回调（由 ShizukuLifecycle 触发）。</summary>
    public static void OnPermissionResult(bool granted)
    {
        // 授权后刷新 _service，下次执行直接用新 binder
        if (!granted) _service = null;
    }

    public sealed class Result
    {
        public int ExitCode { get; set; } = -1;
        public string Stdout { get; set; } = "";
        public string Stderr { get; set; } = "";
        public bool Ok => ExitCode == 0;
    }

    /// <summary>Shizuku 是否在线且已授权。</summary>
    public static bool Available()
    {
#if ANDROID
        try
        {
            return Rikka.Shizuku.Shizuku.PingBinder()
                && Rikka.Shizuku.Shizuku.CheckSelfPermission() == 0;
        }
        catch { return false; }
#else
        return false;
#endif
    }

    /// <summary>拉起 Shizuku 授权界面。</summary>
    public static void RequestPermission()
    {
#if ANDROID
        try { Rikka.Shizuku.Shizuku.RequestPermission(PermissionRequestCode); } catch { }
#endif
    }

    private static bool Bind()
    {
#if ANDROID
        if (_service != null) return true;
        try
        {
            var binder = Rikka.Shizuku.Shizuku.Binder;
            if (binder == null) return false;
            _service = IShizukuService.Stub.AsInterface(binder);
            return _service != null;
        }
        catch { return false; }
#else
        _service = null;
        return false;
#endif
    }

    public static async Task<Result> ExecuteAsync(string command)
    {
        var r = new Result();
#if ANDROID
        try
        {
            if (!Bind())
            {
                r.Stderr = "Shizuku 未连接或未授权";
                return r;
            }

            var process = _service.NewProcess(
                new[] { "/system/bin/sh", "-c", command },
                null,
                null);

            if (process == null)
            {
                r.Stderr = "无法创建进程";
                return r;
            }

            r.Stdout = await ReadFdAsync(process.InputStream);
            r.Stderr = await ReadFdAsync(process.ErrorStream);
            r.ExitCode = process.WaitFor();
        }
        catch (Exception ex)
        {
            r.Stderr = ex.Message;
        }
#else
        r.Stderr = "仅 Android 支持 Shizuku";
#endif
        return r;
    }

#if ANDROID
    private static Task<string> ReadFdAsync(Android.OS.ParcelFileDescriptor? fd)
    {
        return Task.Run(() =>
        {
            if (fd == null) return "";
            try
            {
                using var input = new Android.OS.ParcelFileDescriptor.AutoCloseInputStream(fd);
                var memory = new MemoryStream();
                var buffer = new byte[8192];
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    memory.Write(buffer, 0, n);
                return Encoding.UTF8.GetString(memory.ToArray());
            }
            catch { return ""; }
        });
    }
#endif
}
