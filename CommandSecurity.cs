namespace 青阳AI;

/// <summary>
/// 高危命令检测。用于对用户即将通过 Shizuku 执行的命令做安全判定。
/// </summary>
public static class CommandSecurity
{
   
    private static readonly string[] BlockedPrefixes =
    {
        "rm -rf /", "rm -fr /", "rm -r /", "format", "mkfs", "dd if=/dev/zero",
        "reboot", "shutdown", "poweroff", "halt", "wipe", "fastboot erase", "fastboot flash",
    };

  
    private static readonly string[] DangerousTokens =
    {
        "rm -rf", "rm -fr", "chmod 777", "chmod 666", "pm uninstall", "pm clear",
        "settings clear", "settings delete", "content delete", "iplist", "iptables -F",
        "svc wifi disable", "svc data disable", "mount -o rw", "su -c", "dd ",
    };

    /// <summary>是否是高危命令（需要 5 秒倒计时确认）。</summary>
    public static bool IsHighRisk(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var c = command.Trim().ToLowerInvariant();
        foreach (var p in BlockedPrefixes)
            if (c.StartsWith(p, System.StringComparison.Ordinal)) return true;
        foreach (var t in DangerousTokens)
            if (c.Contains(t, System.StringComparison.Ordinal)) return true;
        return false;
    }
}
