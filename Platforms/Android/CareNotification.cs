using Android.App;
using Android.Content;
using Android.OS;

namespace 青阳AI;

/// <summary>主动关心消息的系统通知：渠道「Ta的消息」，点击打开聊天页，带快捷回复框。</summary>
public static class CareNotification
{
    public const string ChannelId = "companion_care";
    public const string KeyReplyText = "key_reply_text";
    private const int NotifyId = 3001;

    public static void EnsureChannel(Context context)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (manager == null || manager.GetNotificationChannel(ChannelId) != null) return;

        var channel = new NotificationChannel(ChannelId, "Ta的消息", NotificationImportance.High)
        {
            Description = "AI 主动发来的关心消息"
        };
        manager.CreateNotificationChannel(channel);
    }

    public static void Show(string text)
    {
        try
        {
            var context = global::Android.App.Application.Context;
            EnsureChannel(context);

            var intent = new Intent(context, typeof(MainActivity));
            intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
            var pi = PendingIntent.GetActivity(context, 2001, intent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

            var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
                ? new Notification.Builder(context, ChannelId)
                : new Notification.Builder(context);
            builder.SetContentTitle(AppSettings.AiName)
                   .SetContentText(text)
                   .SetStyle(new Notification.BigTextStyle().BigText(text))
                   .SetSmallIcon(Resource.Drawable.dotnet_bot)
                   .SetContentIntent(pi)
                   .SetAutoCancel(true);
            if (Build.VERSION.SdkInt < BuildVersionCodes.O)
                builder.SetPriority((int)NotificationPriority.High);

            // 通知栏快捷回复：不打开 app 也能回应Ta
            try
            {
                var remoteInput = new RemoteInput.Builder(KeyReplyText).SetLabel("回复Ta…").Build();
                var replyPi = PendingIntent.GetBroadcast(context, 4001,
                    new Intent(context, typeof(DirectReplyReceiver)),
                    PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                var replyAction = new Notification.Action.Builder(
                    Resource.Drawable.dotnet_bot, "回复", replyPi)
                    .AddRemoteInput(remoteInput)
                    .Build();
                builder.AddAction(replyAction);
            }
            catch { }

            var nm = (NotificationManager?)context.GetSystemService(Context.NotificationService);
            nm?.Notify(NotifyId, builder.Build());

            // 桌面小组件同步最新消息
            CareWidget.RefreshAll(context);
        }
        catch { /* 通知失败不影响消息入库 */ }
    }
}

/// <summary>开机完成后重排主动关心任务。</summary>
[BroadcastReceiver(Enabled = true, Exported = true, Label = "青阳AI 开机重排任务")]
[IntentFilter(new[] { Intent.ActionBootCompleted })]
public class CareBootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        try { if (context != null && AppSettings.CareEnabled) CareReceiver.EnsureScheduled(context); } catch { }
    }
}
