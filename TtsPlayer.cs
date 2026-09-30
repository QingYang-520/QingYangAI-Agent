using System.Text;

namespace 青阳AI;

/// <summary>
/// TTS 语音播放：OpenAI 兼容 /audio/speech 接口 → mp3 文件 → MediaPlayer 播放。
/// 全局同一时刻只播一条：新播放前自动停止上一个；离开聊天页停止。
/// 非 Android 平台为空实现。
/// </summary>
public static class TtsPlayer
{
    private static readonly HttpClient _http = new();

#if ANDROID
    private static Android.Media.MediaPlayer? _player;
    private static int _playSeq;

    /// <summary>立即停止当前播放。</summary>
    public static void Stop()
    {
        _playSeq++;
        var player = _player;
        _player = null;
        if (player == null) return;
        try
        {
            if (player.IsPlaying) player.Stop();
            player.Release();
        }
        catch { }
    }

    /// <summary>请求 TTS 音频并播放。文本超长自动截断（TTS 接口有输入上限）。</summary>
    public static async Task PlayAsync(string text)
    {
        if (!AppSettings.TtsEnabled || string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        if (text.Length > 800) text = text[..800];

        Stop();
        int seq = ++_playSeq;

        var reqBody = new
        {
            model = string.IsNullOrWhiteSpace(AppSettings.TtsModel) ? "tts-1" : AppSettings.TtsModel,
            input = text,
            voice = string.IsNullOrWhiteSpace(AppSettings.TtsVoice) ? "alloy" : AppSettings.TtsVoice,
            response_format = "mp3"
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.TtsApiUrl);
        req.Headers.Add("Authorization", $"Bearer {AppSettings.TtsApiKey}");
        req.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var resp = await _http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        if (bytes.Length == 0) throw new Exception("TTS 接口未返回音频");

        var path = Path.Combine(FileSystem.CacheDirectory ?? ".", $"tts_{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(path, bytes);

        // 等待音频下载期间可能已有新的播放请求或被 Stop，过期则丢弃
        if (seq != _playSeq) return;

        var player = new Android.Media.MediaPlayer();
        try
        {
            player.SetAudioAttributes(new Android.Media.AudioAttributes.Builder()
                .SetUsage(Android.Media.AudioUsageKind.Media)
                .SetContentType(Android.Media.AudioContentType.Speech)
                .Build());
            player.SetDataSource(path);
            player.Prepare();
            player.Completion += (s, e) =>
            {
                try { player.Release(); } catch { }
                if (ReferenceEquals(_player, player)) _player = null;
            };
            _player = player;
            player.Start();
        }
        catch
        {
            try { player.Release(); } catch { }
            throw;
        }
    }
#else
    public static void Stop() { }
    public static Task PlayAsync(string text) => Task.CompletedTask;
#endif
}
