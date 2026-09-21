using System.Text.Json;
using System.Text.Json.Serialization;

namespace 青阳AI;

/// <summary>
/// 数据备份/恢复：把全部聊天、记忆、日记、首次相遇时间和头像打包成一个 JSON 文件
/// （通过系统分享面板保存到任意位置），导入时覆盖式恢复。
/// 注意：聊天里的图片以文件形式存在设备本地，备份只保留路径，恢复后旧图片不可迁移。
/// </summary>
public static class BackupService
{
    private sealed class BackupData
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("exportedAt")] public string ExportedAt { get; set; } = "";
        [JsonPropertyName("companionSince")] public string? CompanionSince { get; set; }
        [JsonPropertyName("avatarPng")] public string? AvatarPng { get; set; }
        [JsonPropertyName("messages")] public List<MsgRow>? Messages { get; set; }
        [JsonPropertyName("memories")] public List<MemoryRow>? Memories { get; set; }
        [JsonPropertyName("diaries")] public List<DiaryRow>? Diaries { get; set; }
    }

    /// <summary>导出到缓存文件并返回完整路径（随后用分享面板发给用户）。</summary>
    public static async Task<string> ExportAsync()
    {
        var (msgs, memories, diaries) = await ChatStore.Instance.ExportAllAsync();

        string? avatarB64 = null;
        try
        {
            if (File.Exists(AvatarService.AvatarPath))
                avatarB64 = Convert.ToBase64String(await File.ReadAllBytesAsync(AvatarService.AvatarPath));
        }
        catch { }

        var data = new BackupData
        {
            ExportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            CompanionSince = AppSettings.CompanionSinceText,
            AvatarPng = avatarB64,
            Messages = msgs,
            Memories = memories,
            Diaries = diaries
        };

        #if ANDROID
            var dir = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.CacheDir?.AbsolutePath
                      ?? FileSystem.CacheDirectory
                      ?? ".";
#else
            var dir = FileSystem.CacheDirectory
                      ?? ".";
#endif
        var path = Path.Combine(dir, $"qingyang_backup_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = false }));
        return path;
    }

    /// <summary>从备份文件恢复（覆盖式：清空现有聊天/记忆/日记后写入）。</summary>
    public static async Task<bool> RestoreAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath);
        var data = JsonSerializer.Deserialize<BackupData>(json);
        if (data?.Messages == null) throw new Exception("备份文件格式不正确");

        // 图片路径是设备本地路径，恢复后旧图片不可用，置空避免悬空引用
        foreach (var m in data.Messages)
        {
            m.Id = 0;
            m.ImageUrl = "";
        }

        await ChatStore.Instance.RestoreAllAsync(data.Messages, data.Memories ?? new(), data.Diaries ?? new(), data.CompanionSince);

        try
        {
            if (!string.IsNullOrWhiteSpace(data.AvatarPng))
                await File.WriteAllBytesAsync(AvatarService.AvatarPath, Convert.FromBase64String(data.AvatarPng));
        }
        catch { }

        return true;
    }
}
