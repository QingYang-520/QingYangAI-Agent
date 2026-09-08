using SQLite;

namespace 青阳AI;

/// <summary>
/// 本地统一存储：聊天消息 / 长期记忆 / 日记，全部走 SQLite（qingyang.db3）。
/// 首次启动时把旧版 chahis.qingyang 单 JSON 历史迁入并改名 .bak 保留。
/// 生成的图片落盘为文件，消息只存路径，避免历史体积膨胀。
/// </summary>
public sealed class ChatStore
{
    private static readonly Lazy<ChatStore> _lazy = new(() => new ChatStore());
    public static ChatStore Instance => _lazy.Value;

    private readonly SQLiteAsyncConnection _db;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>旧版聊天历史文件名（迁入数据库后改名 .bak）。</summary>
    private const string LegacyHistoryFile = "chahis.qingyang";

    private ChatStore()
    {
        var dbPath = Path.Combine(FileSystem.AppDataDirectory, "qingyang.db3");
        _db = new SQLiteAsyncConnection(dbPath);
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;
            await _db.CreateTableAsync<MsgRow>();
            await _db.CreateTableAsync<MemoryRow>();
            await _db.CreateTableAsync<DiaryRow>();
            await MigrateLegacyJsonAsync();
            await EnsureCompanionSinceAsync();
            _initialized = true;
        }
        finally { _initLock.Release(); }
    }

    // ────────────────────────── 旧版 JSON 迁移 ──────────────────────────

    private async Task MigrateLegacyJsonAsync()
    {
        try
        {
            var oldPath = Path.Combine(FileSystem.AppDataDirectory, LegacyHistoryFile);
            if (!File.Exists(oldPath)) return;

            var json = await File.ReadAllTextAsync(oldPath);
            var list = System.Text.Json.JsonSerializer.Deserialize<List<ChatMsg>>(json);
            if (list != null)
            {
                foreach (var m in list)
                {
                    // 旧数据里 AI 生成图以 base64 内联，迁移时落盘为文件
                    if (!string.IsNullOrEmpty(m.ImageBase64) && string.IsNullOrEmpty(m.ImageUrl))
                    {
                        try
                        {
                            var bytes = Convert.FromBase64String(m.ImageBase64);
                            m.ImageUrl = await SaveImageBytesAsync(bytes, ".png");
                        }
                        catch { /* 图片损坏则丢弃图片，保留文字 */ }
                    }
                    await _db.InsertAsync(ToRow(m));
                }
            }
            File.Move(oldPath, oldPath + ".bak", true);
        }
        catch { /* 迁移失败不影响运行，下次再试 */ }
    }

    /// <summary>首次相遇时间：取库里最早的消息时间，没有则记为现在（只记一次，相伴天数从这天起算）。</summary>
    private async Task EnsureCompanionSinceAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Preferences.Default.Get("CompanionSince", ""))) return;
            var first = await _db.Table<MsgRow>().OrderBy(r => r.Timestamp).FirstOrDefaultAsync();
            var since = first?.Timestamp ?? DateTime.Now;
            Preferences.Default.Set("CompanionSince", since.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch { }
    }

    // ────────────────────────── 图片落盘 ──────────────────────────

    /// <summary>把图片字节写入 {AppData}/images/{guid}{ext}，返回文件路径。</summary>
    public static async Task<string> SaveImageBytesAsync(byte[] bytes, string ext)
    {
        var dir = Path.Combine(FileSystem.AppDataDirectory, "images");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    // ────────────────────────── 聊天消息 ──────────────────────────

    /// <summary>按入库顺序加载全部聊天消息。</summary>
    public async Task<List<ChatMsg>> LoadMessagesAsync()
    {
        await EnsureInitAsync();
        var rows = await _db.Table<MsgRow>().OrderBy(r => r.Id).ToListAsync();
        return rows.Select(ToMsg).ToList();
    }

    /// <summary>加载某条消息之后的新消息（后台主动消息到达后增量同步用）。</summary>
    public async Task<List<ChatMsg>> GetMessagesAfterAsync(int id)
    {
        await EnsureInitAsync();
        var rows = await _db.Table<MsgRow>().Where(r => r.Id > id).OrderBy(r => r.Id).ToListAsync();
        return rows.Select(ToMsg).ToList();
    }

    /// <summary>保存一条消息：新消息插入并回写 Id，已有 Id 的更新。</summary>
    public async Task SaveMessageAsync(ChatMsg m)
    {
        await EnsureInitAsync();
        if (m.Id == 0)
            m.Id = await _db.InsertAsync(ToRow(m));
        else
            await _db.UpdateAsync(ToRow(m));
    }

    /// <summary>后台主动消息专用插入（不依赖 UI 的 ChatMsg 实例）。</summary>
    public async Task<int> InsertProactiveMessageAsync(string content)
    {
        await EnsureInitAsync();
        var row = new MsgRow
        {
            Timestamp = DateTime.Now,
            IsUser = false,
            Content = content,
            IsProactive = true
        };
        return await _db.InsertAsync(row);
    }

    public async Task DeleteMessagesAsync(IEnumerable<int> ids)
    {
        await EnsureInitAsync();
        foreach (var id in ids)
            await _db.DeleteAsync<MsgRow>(id);
    }

    public async Task ClearMessagesAsync()
    {
        await EnsureInitAsync();
        await _db.DeleteAllAsync<MsgRow>();
    }

    /// <summary>取某时间段内的消息（日记生成用）。</summary>
    public async Task<List<ChatMsg>> GetMessagesBetweenAsync(DateTime start, DateTime end)
    {
        await EnsureInitAsync();
        var rows = await _db.Table<MsgRow>()
            .Where(r => r.Timestamp >= start && r.Timestamp < end)
            .OrderBy(r => r.Id).ToListAsync();
        return rows.Select(ToMsg).ToList();
    }

    /// <summary>最近 n 条消息（时间正序，通知快捷回复生成上下文用）。</summary>
    public async Task<List<ChatMsg>> GetRecentMessagesAsync(int n)
    {
        await EnsureInitAsync();
        var rows = await _db.Table<MsgRow>().OrderByDescending(r => r.Id).Take(n).ToListAsync();
        return rows.Select(ToMsg).Reverse().ToList();
    }

    /// <summary>插入一条用户消息（通知栏快捷回复用）。</summary>
    public async Task<int> InsertUserMessageAsync(string content)
    {
        await EnsureInitAsync();
        return await _db.InsertAsync(new MsgRow
        {
            Timestamp = DateTime.Now,
            IsUser = true,
            Content = content
        });
    }

    /// <summary>插入一条 AI 回复（通知快捷回复用，非主动消息）。</summary>
    public async Task<int> InsertAiMessageAsync(string content)
    {
        await EnsureInitAsync();
        return await _db.InsertAsync(new MsgRow
        {
            Timestamp = DateTime.Now,
            IsUser = false,
            Content = content
        });
    }

    /// <summary>最新一条消息（桌面小组件用），没有返回 null。</summary>
    public async Task<MsgRow?> GetLastMessageAsync()
    {
        await EnsureInitAsync();
        return await _db.Table<MsgRow>().OrderByDescending(r => r.Id).FirstOrDefaultAsync();
    }

    /// <summary>取最新一条用户消息时间（主动关心决策用）。</summary>
    public async Task<DateTime?> GetLastUserMessageTimeAsync()
    {
        await EnsureInitAsync();
        var row = await _db.Table<MsgRow>().Where(r => r.IsUser).OrderByDescending(r => r.Id).FirstOrDefaultAsync();
        return row?.Timestamp;
    }

    /// <summary>主动消息统计：今天已发条数、最近一次主动消息时间。</summary>
    public async Task<(int todayCount, DateTime? lastProactiveAt)> GetProactiveStatsAsync(DateTime todayStart)
    {
        await EnsureInitAsync();
        var rows = await _db.Table<MsgRow>().Where(r => r.IsProactive).ToListAsync();
        var last = rows.Count > 0 ? rows.Max(r => r.Timestamp) : (DateTime?)null;
        var todayCount = rows.Count(r => r.Timestamp >= todayStart);
        return (todayCount, last);
    }

    // ────────────────────────── 长期记忆 ──────────────────────────

    /// <summary>记忆条数上限：超出时删除最早的。</summary>
    private const int MemoryCap = 100;

    /// <summary>添加一条记忆。重复内容不重复存；超出上限删最旧。</summary>
    public async Task<bool> AddMemoryAsync(string content)
    {
        await EnsureInitAsync();
        content = content.Trim();
        if (content.Length == 0 || content.Length > 200) return false;

        var exists = await _db.Table<MemoryRow>()
            .Where(r => r.Content == content).FirstOrDefaultAsync();
        if (exists != null) return false;

        await _db.InsertAsync(new MemoryRow { Content = content, CreatedAt = DateTime.Now });

        var all = await _db.Table<MemoryRow>().OrderBy(r => r.Id).ToListAsync();
        if (all.Count > MemoryCap)
        {
            foreach (var r in all.Take(all.Count - MemoryCap))
                await _db.DeleteAsync(r);
        }
        return true;
    }

    /// <summary>取最近 n 条记忆（新的在前）。</summary>
    public async Task<List<string>> GetRecentMemoriesAsync(int n)
    {
        await EnsureInitAsync();
        var rows = await _db.Table<MemoryRow>().OrderByDescending(r => r.Id).Take(n).ToListAsync();
        return rows.Select(r => r.Content).ToList();
    }

    public async Task<int> CountMemoriesAsync()
    {
        await EnsureInitAsync();
        return await _db.Table<MemoryRow>().CountAsync();
    }

    /// <summary>全部记忆，新的在前（记忆面板用）。</summary>
    public async Task<List<MemoryRow>> GetMemoriesAsync()
    {
        await EnsureInitAsync();
        return await _db.Table<MemoryRow>().OrderByDescending(r => r.Id).ToListAsync();
    }

    /// <summary>删除单条记忆（记忆面板用）。</summary>
    public async Task DeleteMemoryAsync(int id)
    {
        await EnsureInitAsync();
        await _db.DeleteAsync<MemoryRow>(id);
    }

    public async Task ClearMemoriesAsync()
    {
        await EnsureInitAsync();
        await _db.DeleteAllAsync<MemoryRow>();
    }

    // ────────────────────────── 日记 ──────────────────────────

    /// <summary>是否已有指定日期（yyyy-MM-dd）的日记。</summary>
    public async Task<bool> HasDiaryAsync(string date)
    {
        await EnsureInitAsync();
        return await _db.FindAsync<DiaryRow>(date) != null;
    }

    public async Task SaveDiaryAsync(string date, string content)
    {
        await EnsureInitAsync();
        await _db.InsertOrReplaceAsync(new DiaryRow { Date = date, Content = content, CreatedAt = DateTime.Now });
    }

    /// <summary>全部日记，按日期倒序。</summary>
    public async Task<List<DiaryRow>> GetDiariesAsync()
    {
        await EnsureInitAsync();
        return await _db.Table<DiaryRow>().OrderByDescending(r => r.Date).ToListAsync();
    }

    /// <summary>取最近一篇日记（注入上下文用，没有返回 null）。</summary>
    public async Task<DiaryRow?> GetLatestDiaryAsync()
    {
        await EnsureInitAsync();
        return await _db.Table<DiaryRow>().OrderByDescending(r => r.Date).FirstOrDefaultAsync();
    }

    // ────────────────────────── 备份与恢复 ──────────────────────────

    /// <summary>导出全部数据（消息/记忆/日记）。</summary>
    public async Task<(List<MsgRow> msgs, List<MemoryRow> memories, List<DiaryRow> diaries)> ExportAllAsync()
    {
        await EnsureInitAsync();
        var msgs = await _db.Table<MsgRow>().OrderBy(r => r.Id).ToListAsync();
        var memories = await _db.Table<MemoryRow>().OrderBy(r => r.Id).ToListAsync();
        var diaries = await _db.Table<DiaryRow>().OrderByDescending(r => r.Date).ToListAsync();
        return (msgs, memories, diaries);
    }

    /// <summary>覆盖式恢复：清空三张表后写入备份数据，并恢复首次相遇时间。</summary>
    public async Task RestoreAllAsync(List<MsgRow> msgs, List<MemoryRow> memories, List<DiaryRow> diaries, string? companionSince)
    {
        await EnsureInitAsync();
        await _db.RunInTransactionAsync(tr =>
        {
            tr.Execute("DELETE FROM messages");
            tr.Execute("DELETE FROM memories");
            tr.Execute("DELETE FROM diaries");
            foreach (var m in msgs) tr.Insert(m);
            foreach (var mm in memories) tr.Insert(mm);
            foreach (var d in diaries) tr.Insert(d);
        });
        if (!string.IsNullOrWhiteSpace(companionSince))
            Preferences.Default.Set("CompanionSince", companionSince);
    }

    // ────────────────────────── 行映射 ──────────────────────────

    private static MsgRow ToRow(ChatMsg m) => new()
    {
        Id = m.Id,
        Timestamp = m.Timestamp == default ? DateTime.Now : m.Timestamp,
        IsUser = m.IsUser,
        Content = m.Content ?? "",
        Thinking = m.Thinking ?? "",
        TerminalTitle = m.TerminalTitle ?? "",
        TerminalExecLog = m.TerminalExecLog ?? "",
        VisionResult = m.VisionResult ?? "",
        ImageUrl = m.ImageUrl ?? "",
        ImageState = m.ImageState ?? "",
        VoicePath = m.VoicePath ?? ""
    };

    private static ChatMsg ToMsg(MsgRow r) => new()
    {
        Id = r.Id,
        Timestamp = r.Timestamp,
        IsUser = r.IsUser,
        Content = r.Content,
        Thinking = r.Thinking,
        TerminalTitle = r.TerminalTitle,
        TerminalExecLog = r.TerminalExecLog,
        VisionResult = r.VisionResult,
        ImageUrl = r.ImageUrl,
        ImageState = r.ImageState,
        VoicePath = r.VoicePath
    };
}

[Table("messages")]
public class MsgRow
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    public bool IsUser { get; set; }
    public string Content { get; set; } = "";
    public string Thinking { get; set; } = "";
    /// <summary>工具气泡标题（如 调用「屏幕使用时间」API / 调用 Shizuku 终端命令），空 = 旧默认文案。</summary>
    public string TerminalTitle { get; set; } = "";
    public string TerminalExecLog { get; set; } = "";
    public string VisionResult { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string ImageState { get; set; } = "";
    public string VoicePath { get; set; } = "";
    /// <summary>是否为后台主动发来的关心消息（不参与普通编辑流程）。</summary>
    public bool IsProactive { get; set; }
}

[Table("memories")]
public class MemoryRow
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

[Table("diaries")]
public class DiaryRow
{
    /// <summary>日期 yyyy-MM-dd，一天一篇。</summary>
    [PrimaryKey] public string Date { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
