using Android.App;
using Android.Content;
using Android.OS;

namespace 青阳AI;

/// <summary>
/// 通知栏快捷回复接收器：用户在通知上直接回复 → 消息入库 →
/// 她结合上下文生成回应 → 回复入库并更新通知（可连续多轮）。
/// </summary>
[BroadcastReceiver(Enabled = true, Exported = false, Label = "青阳AI 快捷回复")]
public class DirectReplyReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        var pending = GoAsync();
        var text = RemoteInput.GetResultsFromIntent(intent)?
            .GetCharSequence(CareNotification.KeyReplyText)?.ToString();

        _ = Task.Run(async () =>
        {
            try
            {
                if (context != null && !string.IsNullOrWhiteSpace(text))
                    await CareReply.HandleAsync(context, text.Trim());
            }
            catch { }
            finally
            {
                try { pending?.Finish(); } catch { }
            }
        });
    }
}

/// <summary>快捷回复处理：入库 → AI 应答 → 回复入库 → 通知刷新。</summary>
public static class CareReply
{
    public static async Task HandleAsync(Context context, string userText)
    {
        // 1) 用户回复入库（先存，就算 AI 回复失败也不丢）
        await ChatStore.Instance.InsertUserMessageAsync(userText);

        // 2) 她结合上下文回应（网络不佳时给出兜底话术）
        var reply = await InnerLifeService.ReplyToUserAsync(userText);
        if (string.IsNullOrWhiteSpace(reply))
            reply = "（这边网络好像不太好…你先发着，我看到就回）";

        // 3) 回复入库 + 刷新通知（通知上仍带回复框，可继续聊）
        await ChatStore.Instance.InsertAiMessageAsync(reply);
        CareNotification.Show(reply);
    }
}
