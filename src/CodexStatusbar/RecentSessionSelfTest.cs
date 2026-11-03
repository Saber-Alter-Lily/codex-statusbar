using System.Text.Json;

namespace CodexStatusbar;

/// <summary>Future runtime checks using newly created synthetic files only, never a real profile.</summary>
internal static class RecentSessionSelfTest
{
    public static IReadOnlyList<(string Name, bool Passed)> Evaluate()
    {
        var checks = new List<(string Name, bool Passed)>();
        var root = Path.Combine(Path.GetTempPath(), "statusbar-multi-synthetic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var now = DateTime.UtcNow;
        var monitor = new RecentSessionMonitor(root, () => now);
        var a = "00000000-0000-0000-0000-000000000001";
        var b = "00000000-0000-0000-0000-000000000002";
        var pathA = Path.Combine(root, "a.jsonl");
        var pathB = Path.Combine(root, "b.jsonl");
        try
        {
            Write(pathA, Session(a, 20, 111), now);
            Write(pathB, Session(b, 70, 222), now);
            checks.Add(("regular in-root file accepted", RolloutPathPolicy.IsAllowed(root, pathA)));
            checks.Add(("outside-root traversal rejected", !RolloutPathPolicy.IsAllowed(root,
                Path.Combine(root, "..", "outside-synthetic.jsonl"))));
            monitor.ObservePath(pathA);
            monitor.ObservePath(pathB);
            monitor.Poll(pathA, a);
            now += TimeSpan.FromSeconds(1);
            var rows = monitor.Poll(pathA, a);
            checks.Add(("two conversations have independent output counters", rows.Count == 2
                && rows.Single(row => row.ThreadId == a).TotalTokens == 30
                && rows.Single(row => row.ThreadId == b).TotalTokens == 80));
            checks.Add(("two conversations have independent speed and TTFT", rows.Count == 2
                && rows.Single(row => row.ThreadId == a).LastTurnTps == 20
                && rows.Single(row => row.ThreadId == b).LastTurnTps == 70
                && rows.All(row => row.Tps is null)
                && rows.Single(row => row.ThreadId == a).TtftMs == 111
                && rows.Single(row => row.ThreadId == b).TtftMs == 222));
            checks.Add(("current row is marked without merging totals", rows.Count(row => row.IsCurrent) == 1
                && rows[0].ThreadId == a));
            monitor.ObservePath(pathA);
            monitor.ObservePath(pathA);
            now += TimeSpan.FromSeconds(1);
            checks.Add(("duplicate observations / windows do not duplicate a thread", monitor.Poll(pathA, a).Count == 2));
            var revision = monitor.Revision;
            monitor.Poll(pathB, b);
            checks.Add(("polling is throttled", monitor.Revision == revision));

            // A new turn clears only its own TTFT; the other thread retains its completed turn.
            File.AppendAllText(pathA, Event("task_started", new { turn_id = "next-synthetic-turn", started_at = 3000 }));
            File.SetLastWriteTimeUtc(pathA, now);
            monitor.ObservePath(pathA);
            now += TimeSpan.FromSeconds(1);
            rows = monitor.Poll(pathB, b);
            checks.Add(("turn reset is thread-local", rows.Single(row => row.ThreadId == a).TtftMs is null
                && rows.Single(row => row.ThreadId == b).TtftMs == 222));

            now += TimeSpan.FromSeconds(20);
            rows = monitor.Poll(null, null);
            checks.Add(("stale rows do not masquerade as live TPS", rows.All(row => row.Tps is null)));
            File.Delete(pathA);
            monitor.ObservePath(pathA);
            now += TimeSpan.FromSeconds(1);
            rows = monitor.Poll(null, null);
            checks.Add(("unreadable file does not retain an apparent live speed", rows.Single(row => row.ThreadId == a) is { Readable: false, Tps: null }));

            var replacement = "00000000-0000-0000-0000-000000000003";
            Write(pathA, Session(replacement, 5, 333), now);
            monitor.ObservePath(pathA);
            now += TimeSpan.FromSeconds(1);
            rows = monitor.Poll(null, null);
            checks.Add(("same-path replacement retires the old thread identity", rows.All(row => row.ThreadId != a)
                && rows.Any(row => row.ThreadId == replacement)));

            var invalid = Path.Combine(root, "not-desktop.jsonl");
            Write(invalid, "{\"type\":\"session_meta\",\"payload\":{\"id\":\"00000000-0000-0000-0000-000000000003\",\"originator\":\"synthetic-cli\"}}\n", now);
            monitor.ObservePath(invalid);
            now += TimeSpan.FromSeconds(1);
            checks.Add(("non-Desktop root excluded", monitor.Poll(null, null).Count == 2));
            for (var index = 4; index < 24; index++)
            {
                var id = $"00000000-0000-0000-0000-{index:D12}";
                var path = Path.Combine(root, $"synthetic-{index}.jsonl");
                Write(path, Session(id, index, index), now);
                monitor.ObservePath(path);
                now += TimeSpan.FromSeconds(1);
                monitor.Poll(pathB, b);
            }
            checks.Add(("capacity is bounded and current conversation retained", monitor.Latest.Count <= RecentSessionMonitor.MaximumSessions
                && monitor.Latest.Any(row => row.ThreadId == b)));
            for (var index = 0; index <= RecentSessionMonitor.MaximumPendingPaths; index++)
                monitor.ObservePath(Path.Combine(root, $"not-created-{index}.jsonl"));
            checks.Add(("event backlog overflow is surfaced instead of rescanning", monitor.DiscoveryIncomplete));
            var expiring = new RecentSessionMonitor(root, () => now);
            Write(pathB, Session(b, 70, 222), now);
            expiring.ObservePath(pathB);
            expiring.Poll(null, null);
            now += TimeSpan.FromMinutes(11);
            checks.Add(("inactive sessions expire", expiring.Poll(null, null).Count == 0));
            expiring.ObservePath(pathB);
            now += TimeSpan.FromSeconds(1);
            checks.Add(("old queued events do not resurrect expired history", expiring.Poll(null, null).Count == 0));
            var fresh = new RecentSessionMonitor(root, () => now);
            checks.Add(("restart does not enumerate historical files", fresh.Poll(null, null).Count == 0));
        }
        finally { Directory.Delete(root, true); }
        return checks;
    }

    private static void Write(string path, string contents, DateTime now)
    {
        File.WriteAllText(path, contents);
        File.SetLastWriteTimeUtc(path, now);
    }

    private static string Session(string id, int output, int ttft) =>
        JsonSerializer.Serialize(new { type = "session_meta", payload = new { id, originator = "Codex Desktop" } }) + "\n"
        + Event("task_started", new { turn_id = "synthetic-turn", started_at = 1000 })
        + Event("item_completed", new { turn_id = "synthetic-turn", item = new { type = "AgentMessage", id = "synthetic-item" }, started_at_ms = 1000, completed_at_ms = 2000 })
        + JsonSerializer.Serialize(new { type = "token_usage_record", payload = new {
            thread_id = id, turn_id = "synthetic-turn", response_id = "synthetic-response",
            usage = new { input_tokens = 10, output_tokens = output, total_tokens = 10 + output },
            thread_token_usage = new { input_tokens = 10, output_tokens = output, total_tokens = 10 + output }
        } }) + "\n"
        + Event("task_complete", new { turn_id = "synthetic-turn", duration_ms = 1000, time_to_first_token_ms = ttft });

    private static string Event(string type, object values)
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;
        payload["type"] = JsonSerializer.SerializeToElement(type);
        return JsonSerializer.Serialize(new { type = "event_msg", payload }) + "\n";
    }
}
