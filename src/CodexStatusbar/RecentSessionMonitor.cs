using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexStatusbar;

internal sealed record RecentSessionMetric(
    string ThreadId, bool IsCurrent, double? Tps, double? TtftMs,
    long TotalTokens, string State, DateTime LastWriteUtc, bool Readable)
{
    // Local UI only. Never send this row to debug diagnostics or persist it.
    public string MenuText => $"{OverlayPresentationBuilder.ShortThreadId(ThreadId)}{(IsCurrent ? " [当前]" : "")}"
        + $" · 估算 {MetricFormat.FormatTpsFloat(Tps)} tok/s"
        + $" · TTFT {(TtftMs is double value ? value.ToString("0.##", CultureInfo.InvariantCulture) + " ms" : "--") }"
        + $" · {State}";
}

/// <summary>
/// A bounded, memory-only set of root Desktop sessions observed since startup. No directory scan,
/// no database lookup, no shared tracker, no combined TPS. Caller owns the single filesystem watcher.
/// Poll is single-consumer; ObservePath is safe for filesystem callbacks.
/// </summary>
internal sealed class RecentSessionMonitor
{
    internal const int MaximumSessions = 8;
    internal const int MaximumPendingPaths = 64;
    internal const int MaximumReadBytes = 1024 * 1024;
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetainFor = TimeSpan.FromMinutes(10);
    private readonly string _root;
    private readonly Func<DateTime> _clock;
    private readonly object _queueLock = new();
    private readonly Queue<string> _pending = new();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastPoll = DateTime.MinValue;
    private int _cursor;
    public bool DiscoveryIncomplete { get; private set; }
    public long Revision { get; private set; }
    public IReadOnlyList<RecentSessionMetric> Latest { get; private set; } = [];

    private sealed class Entry(string id, string path, RolloutReader reader, DateTime seen)
    {
        public string Id { get; } = id;
        public string Path { get; } = path;
        public RolloutReader Reader { get; } = reader;
        public DateTime LastSeen { get; set; } = seen;
        public DateTime LastWrite { get; set; } = DateTime.MinValue;
        public bool Readable { get; set; }
    }

    public RecentSessionMonitor(string root, Func<DateTime>? clock = null)
    {
        _root = root;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public void ObservePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_queueLock)
        {
            if (_queued.Contains(path)) return;
            if (_pending.Count >= MaximumPendingPaths)
            {
                DiscoveryIncomplete = true; // No unbounded recovery scan on watcher overflow.
                return;
            }
            _queued.Add(path);
            _pending.Enqueue(path);
        }
    }

    public void MarkDiscoveryIncomplete() { lock (_queueLock) DiscoveryIncomplete = true; }

    public IReadOnlyList<RecentSessionMetric> Poll(string? currentPath, string? currentThread)
    {
        var now = _clock();
        if (now - _lastPoll < PollInterval) return Latest;
        _lastPoll = now;
        ObservePath(currentPath);
        string? changed = null;
        lock (_queueLock)
        {
            if (_pending.TryDequeue(out changed)) _queued.Remove(changed);
        }
        // At most one event path and one existing session per second, including initialization.
        if (changed is not null) ReadPath(changed, currentThread, now);
        var existing = _entries.Values.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
        if (existing.Length > 0)
        {
            var entry = existing[_cursor++ % existing.Length];
            if (_cursor == int.MaxValue) _cursor = 0;
            if (!string.Equals(entry.Path, changed, StringComparison.OrdinalIgnoreCase))
                ReadPath(entry.Path, currentThread, now);
        }
        foreach (var entry in _entries.Values.ToArray())
        {
            if (!string.Equals(entry.Id, currentThread, StringComparison.OrdinalIgnoreCase)
                && now - entry.LastSeen > RetainFor) _entries.Remove(entry.Id);
        }
        var rows = _entries.Values.Select(entry => ToMetric(entry, currentThread, now))
            .OrderByDescending(row => row.IsCurrent).ThenByDescending(row => row.LastWriteUtc).ToArray();
        if (!Latest.SequenceEqual(rows)) { Latest = rows; Revision++; }
        return Latest;
    }

    private void ReadPath(string path, string? currentThread, DateTime now)
    {
        if (!RolloutPathPolicy.IsAllowed(_root, path) || !TryReadDesktopId(path, out var id))
        {
            foreach (var stale in _entries.Values.Where(entry => entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                stale.Readable = false;
            return;
        }
        // File identity owns attribution: rotation at the same path must retire the old thread.
        foreach (var previous in _entries.Values.Where(entry => entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase)
            && !entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToArray())
            _entries.Remove(previous.Id);
        try
        {
            var write = File.GetLastWriteTimeUtc(path);
            if (!_entries.ContainsKey(id) && !id.Equals(currentThread, StringComparison.OrdinalIgnoreCase)
                && now - write > RetainFor) return; // Old queued events must not resurrect expired history.
            if (!_entries.TryGetValue(id, out var entry) || !entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                // A changed file may be older than an already observed rollout for this thread.
                if (entry is not null && write < entry.LastWrite) return;
                if (entry is null && _entries.Count >= MaximumSessions)
                {
                    var victim = _entries.Values.Where(item => !item.Id.Equals(currentThread, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(item => item.LastSeen).FirstOrDefault();
                    if (victim is null) return;
                    _entries.Remove(victim.Id);
                }
                entry = new Entry(id, path, new RolloutReader(path, maxTailBytes: MaximumReadBytes), now);
                _entries[id] = entry;
                entry.Reader.Initialize();
            }
            else if (entry.Reader.TryUpdate() == RolloutUpdateResult.NeedsReinitialize)
            {
                entry.Reader.Initialize();
            }
            if (write != entry.LastWrite || id.Equals(currentThread, StringComparison.OrdinalIgnoreCase))
                entry.LastSeen = now;
            entry.LastWrite = write;
            entry.Readable = entry.Reader.IsInitialized && entry.Reader.HeadIdentity == id;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or ArgumentException)
        {
            if (_entries.TryGetValue(id, out var entry)) entry.Readable = false;
        }
    }

    private static RecentSessionMetric ToMetric(Entry entry, string? currentThread, DateTime now)
    {
        var tracker = entry.Reader.State.Tps;
        var stale = now - entry.LastWrite > StaleAfter;
        var state = !entry.Readable ? "不可读"
            : !entry.Reader.State.HasRolloutUsage ? "数据不足"
            : stale ? "无新记录（历史值）"
            : tracker.Streaming && tracker.DisplayTps is null ? "等待本轮数据"
            : tracker.Streaming ? "生成中（已观测）" : "已结束/空闲（保留值）";
        return new RecentSessionMetric(entry.Id, entry.Id.Equals(currentThread, StringComparison.OrdinalIgnoreCase),
            entry.Readable && !stale ? tracker.DisplayTps : null,
            entry.Readable ? tracker.TtftMs : null,
            entry.Readable ? entry.Reader.State.TotalTokens : 0, state, entry.LastWrite, entry.Readable);
    }

    internal static bool TryReadDesktopId(string path, out string id)
    {
        id = string.Empty;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[64 * 1024];
            var count = stream.Read(buffer, 0, buffer.Length);
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
            if (newline < 0) return false; // Incomplete/oversized metadata: retry on a later write.
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, newline).TrimStart('\uFEFF'));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || type.GetString() != "session_meta"
                || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("originator", out var origin) || origin.ValueKind != JsonValueKind.String
                || !string.Equals(origin.GetString(), "Codex Desktop", StringComparison.OrdinalIgnoreCase)
                || !payload.TryGetProperty("id", out var identity) || identity.ValueKind != JsonValueKind.String
                || !Guid.TryParse(identity.GetString(), out _)) return false;
            id = identity.GetString()!;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
