namespace 青阳AI;

/// <summary>
/// 主动消息的硬闸门（安静守则）：免打扰时段、每日上限、最小间隔。
/// 要不要说话、说什么，由 InnerLifeService 的心跳让Ta自己判断；
/// 这里只负责"什么情况下绝对不打扰"的底线。
/// </summary>
public static class CareScheduler
{
    /// <summary>两条主动消息之间的最小间隔（小时）。</summary>
    public const double MinProactiveGapHours = 3;

    /// <summary>当前是否允许发主动消息（硬闸门，通过后由内心心跳自主决定说不说）。</summary>
    public static bool CanSpeak(DateTime now, int todayProactiveCount, DateTime? lastProactiveAt)
    {
        if (!AppSettings.CareEnabled) return false;
        if (AppSettings.IsInQuietHours(now)) return false;
        if (todayProactiveCount >= AppSettings.CareDailyCap) return false;
        if (lastProactiveAt.HasValue &&
            (now - lastProactiveAt.Value).TotalHours < MinProactiveGapHours) return false;
        return true;
    }
}
