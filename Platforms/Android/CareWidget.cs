using Android.App;
using Android.Content;
using Android.Widget;

namespace 青阳AI;

/// <summary>
/// 桌面陪伴小组件：显示她的最新一句话、心情表情和相伴天数，点击打开聊天页。
/// 消息变化、心情变化、应用启动、主动消息到达时都会刷新。
/// </summary>
[BroadcastReceiver(Enabled = true, Exported = true, Label = "青阳AI 陪伴小组件")]
[IntentFilter(new[] { "android.appwidget.action.APPWIDGET_UPDATE" })]
[MetaData("android.appwidget.provider", Resource = "@xml/widget_care_info")]
public class CareWidgetProvider : global::Android.Appwidget.AppWidgetProvider
{
    public override void OnUpdate(Context? context, global::Android.Appwidget.AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        CareWidget.Refresh(context, appWidgetManager, appWidgetIds);
    }
}

/// <summary>小组件刷新逻辑（任何消息/心情变化后调用 CareWidget.RefreshAll）。</summary>
public static class CareWidget
{
    /// <summary>刷新全部小组件（无小组件时静默跳过）。</summary>
    public static void RefreshAll(Context? context)
    {
        try
        {
            if (context == null) return;
            var manager = global::Android.Appwidget.AppWidgetManager.GetInstance(context);
            if (manager == null) return;
            var ids = manager.GetAppWidgetIds(
                new global::Android.Content.ComponentName(context, Java.Lang.Class.FromType(typeof(CareWidgetProvider))));
            Refresh(context, manager, ids);
        }
        catch { }
    }

    public static void Refresh(Context? context, global::Android.Appwidget.AppWidgetManager? manager, int[]? ids)
    {
        try
        {
            if (context == null || manager == null || ids == null || ids.Length == 0) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    string msg = "…";
                    try
                    {
                        var last = await ChatStore.Instance.GetLastMessageAsync();
                        if (last != null && !string.IsNullOrWhiteSpace(last.Content))
                            msg = (last.IsUser ? "你：" : "") + last.Content;
                    }
                    catch { }

                    string moodLine;
                    try
                    {
                        moodLine = $"{InnerLifeService.MoodEmoji(InnerLifeService.Mood)} {InnerLifeService.Mood} · 已陪伴 {AppSettings.CompanionDays} 天";
                    }
                    catch
                    {
                        moodLine = "已陪伴中";
                    }

                    var views = new RemoteViews(context.PackageName, Resource.Layout.widget_care);
                    views.SetTextViewText(Resource.Id.widget_title, "青阳AI · " + moodLine);
                    views.SetTextViewText(Resource.Id.widget_msg, msg);
                    views.SetTextViewText(Resource.Id.widget_days, DateTime.Now.ToString("M月d日 HH:mm"));

                    // 点击小组件打开聊天页
                    var tap = PendingIntent.GetActivity(context, 5001,
                        new Intent(context, typeof(MainActivity)),
                        PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                    views.SetOnClickPendingIntent(Resource.Id.widget_msg, tap);

                    manager.UpdateAppWidget(ids, views);
                }
                catch { }
            });
        }
        catch { }
    }
}
