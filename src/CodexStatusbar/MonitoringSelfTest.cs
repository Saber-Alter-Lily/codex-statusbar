using System.Reflection;
using System.Text.Json;

namespace CodexStatusbar;

internal static class MonitoringSelfTest
{
    public static IReadOnlyList<(string Name, bool Passed)> Evaluate()
    {
        var checks = new List<(string Name, bool Passed)>();
        var now = DateTime.UtcNow;
        var sample = new TokenSnapshot("00000000-0000-0000-0000-000000000001", "", 120, 100, 80, 20,
            5, 0, 0, now, Title: "synthetic-private-title", Streaming: true, Tps: 20,
            LastUsageAtUtc: now, TurnId: "synthetic-turn", TurnInputTokens: 100, TurnCachedInputTokens: 80);
        checks.Add(("current speed expires without another counter event", MonitoringStatus.CurrentTps(sample, now) == 20
            && MonitoringStatus.CurrentTps(sample, now.AddSeconds(16)) is null));
        var completed = sample with { Streaming = false, LastTurnTps = 20 };
        checks.Add(("completed speed is never presented as current", MonitoringStatus.CurrentTps(completed, now) is null));
        checks.Add(("speed without a reported counter timestamp stays unavailable", MonitoringStatus.RecordedTps(sample with { LastUsageAtUtc = null }, now) is null
            && MonitoringStatus.CurrentTps(sample with { LastUsageAtUtc = null }, now) is null));
        checks.Add(("stale recorded speed is explicitly low confidence and dated", MonitoringStatus.RecordedSpeed(sample, now.AddMinutes(4)).Contains("上次事件推算(低可信)")
            && MonitoringStatus.RecordedSpeed(sample, now.AddMinutes(4)).Contains("4 分钟前")));
        RunResponseIdentity(checks);
        checks.Add(("unknown breakdown does not imply zero usage or current TPS", MonitoringStatus.CurrentTps(sample with { UsageBreakdownKnown = false }, now) is null));
        checks.Add(("future clock mismatch is not fresh", !MonitoringStatus.IsFresh(now.AddMinutes(10), now)));
        var gap = sample with { UpdatedAtUtc = now.AddSeconds(-25), LastUsageAtUtc = now.AddMinutes(-4) };
        checks.Add(("phase gaps do not turn an unended round into a liveness assertion", MonitoringStatus.State(gap, now).Contains("等待")
            && !MonitoringStatus.State(gap, now).Contains("结束") && !MonitoringStatus.State(gap, now).Contains("运行")
            && MonitoringStatus.CurrentTps(gap, now) is null));
        checks.Add(("activity without usage keeps speed unknown", MonitoringStatus.State(gap with { UpdatedAtUtc = now }, now).Contains("等待")
            && MonitoringStatus.CurrentTps(gap with { UpdatedAtUtc = now }, now) is null));
        checks.Add(("missing round evidence does not imply completed or idle", !MonitoringStatus.State(sample with { Streaming = false, Tps = null, LastTurnTps = null }, now).Contains("完成")
            && !MonitoringStatus.State(sample with { Streaming = false, Tps = null, LastTurnTps = null }, now).Contains("空闲")));
        checks.Add(("poll heartbeat uses completed reads rather than UI time", MonitoringStatus.PollStatus(null, now).Contains("等待读取")
            && MonitoringStatus.PollStatus(now.AddSeconds(-6), now).Contains("延迟")));
        checks.Add(("read offset reports caught-up and pending data separately", MonitoringStatus.ReadProgress(sample with { LogPath = "synthetic.jsonl", RolloutOffset = 10, ObservedLogLength = 10 }).Contains("末尾")
            && MonitoringStatus.ReadProgress(sample with { LogPath = "synthetic.jsonl", RolloutOffset = 10, ObservedLogLength = 20 }).Contains("追赶")));
        var parser = new RolloutStreamState();
        parser.ObserveLine(Usage(now.AddSeconds(-40), sample.ThreadId, 100, 80, 20));
        parser.ObserveLine(JsonSerializer.Serialize(new { type = "response_item", timestamp = now.ToString("O"), payload = new { } }));
        checks.Add(("response activity and usage timestamps are independent", parser.LastMetadataAtUtc == now
            && parser.LastUsageAtUtc == now.AddSeconds(-40) && parser.TotalTokens == 120));
        parser.ObserveLine(JsonSerializer.Serialize(new { type = "event_msg", timestamp = now.AddSeconds(1).ToString("O"),
            payload = new { type = "token_count", info = new {
                total_token_usage = new { input_tokens = 90, cached_input_tokens = 70, output_tokens = 10, total_tokens = 100 },
                last_token_usage = new { input_tokens = 90, cached_input_tokens = 70, output_tokens = 10, total_tokens = 100 } } } }));
        checks.Add(("lagging legacy carrier cannot regress explicit cumulative counters or refresh their timestamp", parser.TotalTokens == 120
            && parser.LastUsageAtUtc == now.AddSeconds(-40) && parser.LastMetadataAtUtc == now.AddSeconds(1)));
        var rate = new RolloutStreamState();
        rate.ObserveLine(Usage(now.AddSeconds(-4), sample.ThreadId, 100, 80, 20));
        rate.ObserveLine(Event(now.AddSeconds(-3), "task_started", new { turn_id = "turn-a" }));
        rate.ObserveLine(Event(now.AddSeconds(-2), "item_completed", new { turn_id = "turn-a", item = new { type = "Reasoning" }, started_at_ms = 1000, completed_at_ms = 2000 }));
        rate.ObserveLine(Usage(now.AddSeconds(-1), sample.ThreadId, 200, 160, 40));
        var checkpointSpeed = rate.Tps.DisplayTps;
        rate.ObserveLine(Event(now, "item_completed", new { turn_id = "turn-a", item = new { type = "Reasoning" }, started_at_ms = 3000, completed_at_ms = 4000 }));
        checks.Add(("output-only activity cannot recalculate speed using an older token numerator", checkpointSpeed == 20
            && rate.Tps.DisplayTps == checkpointSpeed && rate.LastUsageAtUtc == now.AddSeconds(-1)));
        var defaults = OverlaySettings.ParseJson("{\"settingsVersion\":4}").Settings;
        checks.Add(("legacy settings default to all four requested floating metrics", defaults.CompactFields == StripFieldRules.Default));
        defaults.CompactFields = StripField.Total | StripField.Ttft;
        var restored = OverlaySettings.ParseJson(defaults.Serialize()).Settings;
        checks.Add(("floating field selection persists without moving its saved anchor", restored.CompactFields == defaults.CompactFields
            && restored.MainAttachment == defaults.MainAttachment && restored.PositionMode == defaults.PositionMode));
        var fourMetrics = OverlayPresentationBuilder.BuildCapsuleVariants(sample);
        checks.Add(("all floating widths retain TTFT and cumulative-cache labels", fourMetrics.All(text => text.Contains("TTFT") && text.Contains("缓存"))));
        var selectedOnly = OverlayPresentationBuilder.BuildCapsuleVariants(sample, StripField.Total | StripField.Ttft);
        checks.Add(("unchecked floating metrics disappear while selected metrics remain", selectedOnly.All(text => text.Contains("TTFT")
            && !text.Contains("缓存") && !text.Contains("t/s"))));
        checks.Add(("unknown TTFT never reuses an old reported number", OverlayPresentationBuilder.BuildCapsuleText(sample).Contains("TTFT--")));
        checks.Add(("reported TTFT is visible on the floating strip", OverlayPresentationBuilder.BuildCapsuleText(completed with { TtftMs = 3966 }).Contains("TTFT3.97s")));
        using (var strip = new TokenStripForm())
        {
            strip.SetPresentation(OverlayPresentationBuilder.Create(sample with { Source = "manual" }, DisplayField.Tps,
                DisplayField.Total, DisplayField.Tps | DisplayField.Total));
            var widths = strip.MeasureCapsuleWidthsDip(96);
            var measured = OverlayLayoutCalculator.GetCapsuleSizeForText(widths[0], 96, 100);
            var host = new CodexWindowInfo(IntPtr.Zero, new(0, 0, 1600, 1000), new(0, 0, 1600, 1000), null,
                new(0, 0, 1600, 1000), 96, default);
            var layout = OverlayLayoutCalculator.Calculate(new(host, AnchorMode.Auto, false, 0, false,
                new Point(100, 30), 100, measured));
            checks.Add(("four-metric manual float uses measured width without clipping", widths[0] > 330
                && layout.CapsuleBounds.Width == measured.Width && layout.State != OverlayVisualState.HiddenForSpace));
        }
        var route = new ActiveThreadRouteStatus(null, 0, true, 1, null);
        checks.Add(("empty connected route is explicitly unidentified", MonitoringStatus.Route(route).Contains("未识别")));
        checks.Add(("disconnected route is explicitly unidentified", MonitoringStatus.Route(route with { IsConnected = false }).Contains("未识别")));
        var capsule = OverlayPresentationBuilder.BuildCapsuleText(completed);
        checks.Add(("completed strip labels its retained estimate as historical", capsule.Contains("上轮记录") && capsule.Contains("上轮低可信估 ~20")
            && capsule.Contains("计数")));
        using (var panel = new MonitoringPanelForm())
        {
            panel.Present(completed, [], route, sample.ThreadId, false, now, "synthetic");
            checks.Add(("panel identifies completed speed as a dated low-confidence event estimate", panel.OverviewText.Contains("上轮事件推算(低可信) ~20.0")
                && panel.OverviewText.Contains("计数")));
            checks.Add(("panel never renders conversation titles", !panel.OverviewText.Contains("synthetic-private-title")));
            panel.Present(gap, [], route, sample.ThreadId, false, now, "synthetic", now.AddSeconds(-8));
            checks.Add(("panel exposes an overdue reader heartbeat", panel.PollText.Contains("延迟")));
            StripField? changedFields = null;
            panel.StripFieldsChanged += fields => changedFields = fields;
            panel.StripCheck(StripField.Ttft).Checked = false;
            checks.Add(("field checkbox dispatches the selected configuration", changedFields == (StripFieldRules.Default & ~StripField.Ttft)));
            panel.Present(null, [], route, null, false, now, "synthetic");
            checks.Add(("automatic mode clears the previous selected overview", !panel.OverviewText.Contains("~20.0")));
            var called = 0;
            panel.AutoRequested += () => called++;
            typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(panel.AutoButton, [EventArgs.Empty]);
            checks.Add(("panel auto control dispatches once", called == 1));
            checks.Add(("panel is resizable and taskbar accessible", panel.FormBorderStyle == FormBorderStyle.Sizable && panel.ShowInTaskbar));
            var args = new FormClosingEventArgs(CloseReason.UserClosing, false);
            typeof(Form).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [args]);
            checks.Add(("panel user close hides without disposal", args.Cancel && !panel.IsDisposed));
        }
        RunRoute(checks);
        RunFiles(checks, now);
        return checks;
    }

    private static void RunRoute(List<(string Name, bool Passed)> checks)
    {
        using var route = new CodexIpcActiveThreadMonitor(); // Never Start: no real pipe.
        void Call(string method, params object?[] args) => typeof(CodexIpcActiveThreadMonitor)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(route, args);
        void Follow(string id, string client, bool following) => Call("ProcessFrame", JsonSerializer.SerializeToUtf8Bytes(new
        { type = "broadcast", method = "thread-stream-following-changed", sourceClientId = client,
            @params = new { conversationId = id, hostId = "local", following } }));
        const string a = "00000000-0000-0000-0000-000000000001";
        const string b = "00000000-0000-0000-0000-000000000002";
        Call("MarkConnected");
        Follow(a, "synthetic-window-a", true);
        checks.Add(("single IPC route is retained only as a candidate", route.GetStatus().ThreadId == a));
        Follow(b, "synthetic-window-b", true);
        checks.Add(("multiple IPC routes cannot select the latest broadcaster", route.GetStatus().ThreadId is null));
        Follow(b, "synthetic-window-b", false);
        Follow(a, "synthetic-window-a", false);
        checks.Add(("empty connected IPC route clears its thread", route.GetStatus() is { ThreadId: null, IsConnected: true }));
        Follow(a, "synthetic-window-a", true);
        Call("MarkDisconnected", new object?[] { null });
        checks.Add(("disconnect clears previous route and subscriptions", route.GetStatus() is { ThreadId: null, ActiveWindowCount: 0, IsConnected: false }));
    }

    private static void RunResponseIdentity(List<(string Name, bool Passed)> checks)
    {
        var tracker = new TpsTracker();
        tracker.BeginTurn("synthetic-a", true, 0, false);
        tracker.ObserveResponseOutput("response:a", 20, "100:80:20:120");
        tracker.ObserveResponseOutput(null, 20, "100:80:20:120");
        tracker.ObserveResponseOutput("response:b", 20, "200:160:40:240");
        tracker.ObserveResponseOutput(null, 20, "200:160:40:240");
        tracker.ObserveResponseOutput("response:a", 20, "100:80:20:120");
        checks.Add(("equal token vectors in distinct responses remain distinct, paired carriers and replays do not", tracker.TurnOutputTokens == 40));
        tracker.BeginTurn("synthetic-b", true, 0, false);
        tracker.ObserveResponseOutput(null, 20, "100:80:20:120");
        tracker.ObserveResponseOutput("response:a", 20, "100:80:20:120");
        tracker.ObserveResponseOutput(null, 20, "100:80:20:120");
        checks.Add(("explicit response replaces its provisional legacy checkpoint without doubling", tracker.TurnOutputTokens == 20));
        tracker.ObserveResponseOutput("response:b", 20, "100:80:20:120");
        checks.Add(("distinct explicit response identities never collapse solely by equal counts", tracker.TurnOutputTokens == 40));
        tracker.BeginTurn("synthetic-c", true, 0, false);
        tracker.ObserveResponseOutput(null, 20);
        checks.Add(("unidentified legacy usage cannot fabricate a per-response sum", tracker.TurnOutputTokens == 0));
        tracker.ObserveResponseOutput(null, 20, "100:80:20:120");
        tracker.PreferExplicitResponses();
        tracker.ObserveResponseOutput("response:a", 20, "110:80:20:130");
        tracker.ObserveResponseOutput(null, 20, "100:80:20:120");
        checks.Add(("explicit carrier supersedes mismatched legacy checkpoints without a doubled numerator", tracker.TurnOutputTokens == 20));
    }

    private static void RunFiles(List<(string Name, bool Passed)> checks, DateTime now)
    {
        var root = Path.Combine(Path.GetTempPath(), "statusbar-monitoring-synthetic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var a = "00000000-0000-0000-0000-000000000011";
        var b = "00000000-0000-0000-0000-000000000012";
        string FileFor(string id) => Path.Combine(root, "rollout-" + id + ".jsonl");
        try
        {
            foreach (var id in new[] { a, b }) File.WriteAllText(FileFor(id),
                JsonSerializer.Serialize(new { type = "session_meta", payload = new { id, originator = "Codex Desktop" } }) + "\n"
                + Event(now.AddSeconds(-4), "task_started", new { turn_id = "turn-a" })
                + Usage(now.AddSeconds(-3), id, 100, 80, 20));
            using var monitor = new TokenLogMonitor(root, Path.Combine(root, "missing.sqlite"));
            checks.Add(("automatic polling never chooses the most recently changed log", monitor.Poll() is null));
            monitor.PinActiveSession = true;
            monitor.PreferredThreadId = a;
            var first = monitor.Poll();
            checks.Add(("manual selection reads only the exact selected identity", first?.ThreadId == a && first.TotalTokens == 120));
            monitor.PreferredThreadId = b;
            checks.Add(("changing a locked selection switches readers", monitor.Poll()?.ThreadId == b));
            monitor.PinActiveSession = false;
            monitor.PreferredThreadId = null;
            checks.Add(("returning to automatic clears old selection instead of SQLite fallback", monitor.Poll() is null && monitor.ActiveThreadId is null));
            monitor.PreferredThreadId = a;
            monitor.PinActiveSession = true;
            _ = monitor.Poll();
            var originalMtime = File.GetLastWriteTimeUtc(FileFor(a));
            File.AppendAllText(FileFor(a), Usage(now, a, 150, 100, 30));
            File.SetLastWriteTimeUtc(FileFor(a), originalMtime);
            var second = monitor.Poll();
            checks.Add(("new usage updates counters despite unchanged filesystem mtime", second?.TotalTokens == 180));
            checks.Add(("new usage updates the event timestamp independently of mtime", second?.UpdatedAtUtc == now && second.LastUsageAtUtc == now));
            checks.Add(("cached input is a subset rather than an added total", second?.TotalTokens == second?.InputTokens + second?.OutputTokens));
            var disposed = false;
            monitor.Dispose();
            disposed = monitor.Poll() is null;
            checks.Add(("closed monitor cannot publish a late snapshot", disposed));
        }
        finally
        {
            var canonical = Path.GetFullPath(root);
            if (canonical.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(canonical).StartsWith("statusbar-monitoring-synthetic-", StringComparison.Ordinal))
                Directory.Delete(canonical, true);
        }
    }

    private static string Usage(DateTime time, string id, int input, int cached, int output) => JsonSerializer.Serialize(new
    { timestamp = time.ToString("O"), type = "token_usage_record", payload = new { thread_id = id, turn_id = "turn-a",
        thread_token_usage = new { input_tokens = input, cached_input_tokens = cached, output_tokens = output, total_tokens = input + output } } }) + "\n";
    private static string Event(DateTime time, string type, object extra)
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(extra))!;
        payload["type"] = JsonSerializer.SerializeToElement(type);
        return JsonSerializer.Serialize(new { timestamp = time.ToString("O"), type = "event_msg", payload }) + "\n";
    }
}
