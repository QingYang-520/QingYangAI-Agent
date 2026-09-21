using Android.Content;
using Android.Database;
using Android.Provider;

namespace 青阳AI;

/// <summary>
/// 今日日程感知（需要 READ_CALENDAR 运行时权限，普通弹窗授权，与 Shizuku 无关）。
/// 读取今天剩余的日程（标题 + 时间），供Ta提前关心用户。
/// </summary>
public static class CalendarHelper
{
    /// <summary>是否已授予 READ_CALENDAR。</summary>
    public static bool IsGranted()
    {
        try
        {
            return global::Android.App.Application.Context
                .CheckSelfPermission(Android.Manifest.Permission.ReadCalendar)
                == Android.Content.PM.Permission.Granted;
        }
        catch { return false; }
    }

    /// <summary>今天剩余的日程，按开始时间排序（最多 6 条）。格式：HH:mm 标题 / 全天 标题。</summary>
    public static List<string> GetTodayEvents()
    {
        var result = new List<string>();
        try
        {
            if (!IsGranted()) return result;
            var context = global::Android.App.Application.Context;

            var cal = Java.Util.Calendar.Instance!;
            cal.TimeInMillis = Java.Lang.JavaSystem.CurrentTimeMillis();
            cal.Set(Java.Util.CalendarField.HourOfDay, 0);
            cal.Set(Java.Util.CalendarField.Minute, 0);
            cal.Set(Java.Util.CalendarField.Second, 0);
            cal.Set(Java.Util.CalendarField.Millisecond, 0);
            long dayStart = cal.TimeInMillis;
            long dayEnd = dayStart + 24 * 3600_000L;
            long now = Java.Lang.JavaSystem.CurrentTimeMillis();

            var builder = CalendarContract.Instances.ContentUri.BuildUpon();
            ContentUris.AppendId(builder, dayStart);
            ContentUris.AppendId(builder, dayEnd);
            var uri = builder.Build();
            if (uri == null) return result;

            string[] projection =
            {
                CalendarContract.Instances.InterfaceConsts.Title,
                CalendarContract.Instances.InterfaceConsts.Dtstart,
                CalendarContract.Instances.InterfaceConsts.Dtend,
                CalendarContract.Instances.InterfaceConsts.AllDay
            };

            using var cursor = context.ContentResolver?.Query(
                uri, projection,
                $"{CalendarContract.Instances.InterfaceConsts.Dtend} >= ?",
                new[] { now.ToString() },
                $"{CalendarContract.Instances.InterfaceConsts.Dtstart} ASC");

            while (cursor != null && cursor.MoveToNext() && result.Count < 6)
            {
                var title = cursor.GetString(0)?.Trim() ?? "";
                if (title.Length == 0) continue;
                long dtstart = cursor.GetLong(1);
                bool allDay = cursor.GetInt(3) != 0;

                var local = DateTimeOffset.FromUnixTimeMilliseconds(dtstart).LocalDateTime;
                var when = allDay ? "全天" : local.ToString("HH:mm");
                result.Add($"{when} {title}");
            }
        }
        catch { }
        return result;
    }
}
