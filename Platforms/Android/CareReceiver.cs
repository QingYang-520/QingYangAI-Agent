using Android.App;
using Android.Content;
using Android.OS;

namespace 青阳AI;

/// <summary>
/// 主动关心定时器（Android）：AlarmManager 每 15 分钟唤醒一次本接收器，
/// 先过硬闸门（安静守则），然后把局面交给Ta自己——内心心跳里Ta可以
/// 调用感知 API 了解情况（如早上查一下昨晚睡得怎么样），再决定要不要开口。
/// 每次触发后自行续排下一次；总开关关闭时不再续排，开启时由设置页重新排期。
/// </summary>
[BroadcastReceiver(Enabled = true, Exported = false, Label = "青阳AI 主动关心")]
public class CareReceiver : BroadcastReceiver
{
    private const int RequestCode = 1001;
    private const int IntervalMs = 15 * 60 * 1000;

    public override void OnReceive(Context? context, Intent? intent)
    {
        PendingResult? pending = GoAsync();
        _ = RunAsync(context, pending);
    }

    private static async Task RunAsync(Context? context, PendingResult? pending)
    {
        try
        {
            if (context != null)
                EnsureScheduled(context);   // 周期自续
            await HeartbeatAsync();
        }
        catch { /* 后台任务失败静默 */ }
        finally
        {
            try { pending?.Finish(); } catch { }
        }
    }

    /// <summary>确保下一次检查已排期（App 启动、开机完成、每次触发后、设置开启时调用）。</summary>
    public static void EnsureScheduled(Context context)
    {
        try
        {
            if (!AppSettings.CareEnabled) return; // 关闭时不排期；设置页开启时会再调用

            var am = (AlarmManager?)context.GetSystemService(Context.AlarmService);
            if (am == null) return;

            var pi = PendingIntent.GetBroadcast(context, RequestCode,
                new Intent(context, typeof(CareReceiver)),
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

            // Doze 下也会触发（系统节流为约 15 分钟一次，正合需求）
            var triggerAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + IntervalMs;
            am.SetAndAllowWhileIdle(AlarmType.RtcWakeup, triggerAt, pi);
        }
        catch { }
    }

    /// <summary>
    /// 一次心跳，两件事（互不阻塞）：
    /// 1) 默默观察：到点就自己调感知 API 了解用户行动状态并存后台（不打扰，即使不说话也发生）；
    /// 2) 决定是否开口：过硬闸门 → Ta的内心自主决定 → 说话则入库 + 通知。
    /// </summary>
    private static async Task HeartbeatAsync()
    {
        if (string.IsNullOrWhiteSpace(AppSettings.ApiUrl)) return;

        // 先默默观察（每 45 分钟一次，不打扰，让 Ta 持续"了解"用户）
        await CareWatch.ObserveOnceAsync();

        var now = DateTime.Now;
        var (todayCount, lastProactiveAt) = await ChatStore.Instance.GetProactiveStatsAsync(now.Date);
        if (!CareScheduler.CanSpeak(now, todayCount, lastProactiveAt)) return;

        var lastUserMsgAt = await ChatStore.Instance.GetLastUserMessageTimeAsync();

        var say = await InnerLifeService.HeartbeatAsync(lastUserMsgAt, todayCount);
        if (string.IsNullOrWhiteSpace(say)) return;

        await ChatStore.Instance.InsertProactiveMessageAsync(say.Trim());
        CareNotification.Show(say.Trim());
    }
}
