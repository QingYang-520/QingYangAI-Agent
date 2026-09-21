namespace 青阳AI;

/// <summary>
/// 「Ta可以调用的感知 API」注册表：把手机上的各类信息封装成命名的 API，
/// AI 通过 {api:"名"} 隐藏指令主动调用（想查什么就查什么），客户端执行后把结果回传。
/// 全部走系统标准 API，无需 Shizuku；未授权/不可用时返回说明文字而不是报错。
/// </summary>
public static class ApiRegistry
{
    public sealed class ApiInfo
    {
        public string Id = "";
        public string Name = "";   // 中文名（气泡与结果标注用）
        public string Desc = "";   // 给模型看的用途说明
        public Func<Task<string>> Run = () => Task.FromResult("");
    }

    public static readonly List<ApiInfo> All = new()
    {
        new() {
            Id = "screen_time", Name = "屏幕使用时间",
            Desc = "今天各应用的使用时长排行。想知道用户今天刷了多久手机、在忙什么、睡没睡好时调用",
            Run = DeviceContextService.GetScreenTimeTextAsync
        },
        new() {
            Id = "foreground", Name = "前台应用",
            Desc = "用户此刻正在用哪个应用。想确认用户当前在做什么时调用",
            Run = DeviceContextService.GetForegroundTextAsync
        },
        new() {
            Id = "notifications", Name = "最近通知",
            Desc = "最近几分钟手机收到的新消息通知（微信等）。想知道用户有没有收到/看到消息时调用",
            Run = DeviceContextService.GetNotificationsTextAsync
        },
        new() {
            Id = "calendar", Name = "今日日程",
            Desc = "用户日历里今天剩余的日程。早上或聊到安排、行程时调用",
            Run = DeviceContextService.GetCalendarTextAsync
        },
        new() {
            Id = "battery", Name = "电量状态",
            Desc = "电量和充电状态。电量低时提醒用户充电",
            Run = DeviceContextService.GetBatteryTextAsync
        },
        new() {
            Id = "music", Name = "媒体播放",
            Desc = "是否在播放音乐/媒体、铃声模式。想知道用户在不在听歌、是不是在开会静音时调用",
            Run = DeviceContextService.GetMediaTextAsync
        },
        new() {
            Id = "alarm", Name = "下一个闹钟",
            Desc = "系统里设置的下一个闹钟时间。结合当前时间判断用户睡没睡、该不该起时调用",
            Run = DeviceContextService.GetAlarmTextAsync
        },
        new() {
            Id = "screen", Name = "屏幕状态",
            Desc = "屏幕亮还是灭。判断用户是否正在使用手机时调用",
            Run = DeviceContextService.GetScreenTextAsync
        },
        new() {
            Id = "network", Name = "网络状态",
            Desc = "Wi-Fi 还是流量。判断用户在家还是在外面时调用",
            Run = DeviceContextService.GetNetworkTextAsync
        },
    };

    public static ApiInfo? Find(string id)
    {
        id = id.Trim();
        foreach (var a in All)
            if (a.Id == id) return a;
        return null;
    }

    /// <summary>执行一个感知 API，返回结果文本（失败/未授权返回说明文字）。</summary>
    public static async Task<string> ExecuteAsync(string id)
    {
        var api = Find(id);
        if (api == null) return $"没有叫「{id}」的 API";
        try
        {
            return await api.Run();
        }
        catch (Exception ex)
        {
            return "调用失败：" + ex.Message;
        }
    }

    /// <summary>给模型看的 API 菜单文本。</summary>
    public static string BuildMenuText()
    {
        var sb = new System.Text.StringBuilder("可调用的感知 API（输出 {api:\"名\"} 调用，一行一个，最多两条）：\n");
        foreach (var a in All)
            sb.AppendLine($"{a.Id} = {a.Name}：{a.Desc}");
        return sb.ToString();
    }
}
