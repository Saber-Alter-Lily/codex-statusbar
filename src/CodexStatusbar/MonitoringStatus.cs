using System.Globalization;

namespace CodexStatusbar;

internal static class MonitoringStatus
{
    internal static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(15);
    public static bool IsFresh(DateTime timestamp, DateTime now) => timestamp != DateTime.MinValue
        && timestamp <= now.AddSeconds(5) && now - timestamp <= FreshFor;

    public static double? CurrentTps(TokenSnapshot snapshot, DateTime now) =>
        snapshot.Streaming && snapshot.UsageBreakdownKnown
            && snapshot.LastUsageAtUtc is DateTime stamp && IsFresh(stamp, now) ? snapshot.Tps : null;

    public static double? RecordedTps(TokenSnapshot snapshot, DateTime now) => snapshot.UsageBreakdownKnown
        && snapshot.LastUsageAtUtc is DateTime stamp && stamp != DateTime.MinValue && stamp <= now.AddSeconds(5)
            ? snapshot.Streaming ? snapshot.Tps : snapshot.LastTurnTps ?? snapshot.Tps : null;

    public static string RecordedSpeed(TokenSnapshot snapshot, DateTime now) =>
        (snapshot.Streaming ? CurrentTps(snapshot, now) is null ? "上次" : "最近" : "上轮")
        + "事件推算(低可信) " + Speed(RecordedTps(snapshot, now))
        + " · 计数 " + Age(snapshot.LastUsageAtUtc, now);

    public static string State(TokenSnapshot snapshot, DateTime now) =>
        !snapshot.UsageBreakdownKnown ? "数据不完整 · 等待本地用量记录"
        : CurrentTps(snapshot, now) is not null ? "阶段计数已更新 · 速度估算"
        : !snapshot.Streaming && MetricFormat.IsUsableTps(snapshot.LastTurnTps ?? snapshot.Tps) ? "上轮阶段记录 · 当前无新计数"
        : "等待客户端下一次用量记录";

    public static string Route(ActiveThreadRouteStatus route) => !route.IsConnected
        ? route.LastError == "connect:UnauthorizedAccessException" ? "IPC 管道访问受限 · 前台归属未识别"
            : route.LastError == "identity:UnauthorizedAccessException" ? "IPC 身份校验未通过 · 前台归属未识别"
            : "IPC 未连接 · 自动归属未识别"
        : route.ActiveWindowCount > 1 ? "IPC 多个订阅 · 前台归属未识别"
        : route.ThreadId is null ? "IPC 已连接 · 未识别本地会话（Dot / 云任务可能不支持）"
        : "IPC 已连接 · 候选会话已发现，前台页面归属未核实";

    public static string ReadProgress(TokenSnapshot snapshot) => string.IsNullOrEmpty(snapshot.LogPath)
        ? "尚无可读日志" : snapshot.ObservedLogLength <= 0 ? "等待读取日志"
        : snapshot.RolloutOffset >= snapshot.ObservedLogLength ? "日志已读到末尾"
        : "正在追赶新增日志";

    public static string PollStatus(DateTime? completedAt, DateTime now) =>
        completedAt is not DateTime stamp || stamp == DateTime.MinValue ? "等待读取完成"
        : (now - stamp).TotalSeconds > 5 ? "监测检查延迟 · " + Age(stamp, now)
        : "监测检查 " + stamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Speed(double? value) => MetricFormat.IsUsableTps(value)
        ? "~" + value!.Value.ToString("0.0", CultureInfo.InvariantCulture) + " tok/s" : "--";

    public static string Timestamp(DateTime? value) => value is not DateTime stamp || stamp == DateTime.MinValue
        ? "未上报" : stamp.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public static string Age(DateTime? value, DateTime now) => value is not DateTime stamp || stamp == DateTime.MinValue
        ? "未知" : stamp > now.AddSeconds(5) ? "时钟待核对"
        : (now - stamp).TotalSeconds < 60 ? Math.Max(0, (int)(now - stamp).TotalSeconds) + " 秒前"
        : (now - stamp).TotalMinutes < 60 ? (int)(now - stamp).TotalMinutes + " 分钟前"
        : (int)(now - stamp).TotalHours + " 小时前";
}
