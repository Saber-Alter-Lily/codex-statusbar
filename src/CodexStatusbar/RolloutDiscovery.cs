namespace CodexStatusbar;

internal sealed record RolloutSearchBudget(
    int MaximumEntries = 2048,
    int MaximumDirectories = 128,
    int MaximumDepth = 12,
    int MaximumCandidates = 16,
    int MaximumMilliseconds = 100);

internal readonly record struct RolloutSearchEntry(string Path, FileAttributes Attributes);
internal sealed record RolloutSearchResult(string? Path, bool BudgetReached, int Entries, int Directories);

/// <summary>
/// Explicit, cooperative budgets apply to every enumerated entry, not only matching filenames.
/// Never follows reparse points. A single OS filesystem operation can still outlast the time budget;
/// cancellation/time checks run before and after it, without creating abandoned worker threads.
/// </summary>
internal static class BoundedRolloutSearch
{
    public static RolloutSearchResult Find(
        string root, string threadId, Func<string, bool> accept, CancellationToken cancellation,
        RolloutSearchBudget? budget = null,
        Func<string, IEnumerable<RolloutSearchEntry>>? enumerate = null,
        Func<long>? milliseconds = null, Func<string, bool>? safeDirectory = null)
    {
        budget ??= new RolloutSearchBudget();
        enumerate ??= Enumerate;
        milliseconds ??= () => Environment.TickCount64;
        safeDirectory ??= directory => RolloutPathPolicy.IsSafeDirectory(root, directory);
        var started = milliseconds();
        var entries = 0;
        var directories = 0;
        var candidates = 0;
        var incomplete = false;
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        string? selected = null;
        var newest = DateTime.MinValue;

        bool Expired()
        {
            cancellation.ThrowIfCancellationRequested();
            return milliseconds() - started >= budget.MaximumMilliseconds;
        }

        while (pending.Count > 0)
        {
            if (Expired() || directories >= budget.MaximumDirectories || entries >= budget.MaximumEntries)
                return new(selected, true, entries, directories);
            var (directory, depth) = pending.Pop();
            directories++;
            try
            {
                if (!safeDirectory(directory)) { incomplete = true; continue; }
                if (Expired()) return new(selected, true, entries, directories);
                using var iterator = enumerate(directory).GetEnumerator();
                while (true)
                {
                    if (Expired() || entries >= budget.MaximumEntries)
                        return new(selected, true, entries, directories);
                    if (!iterator.MoveNext()) break;
                    entries++;
                    if (Expired()) return new(selected, true, entries, directories);
                    var entry = iterator.Current;
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        if (depth >= budget.MaximumDepth || pending.Count + directories >= budget.MaximumDirectories)
                            incomplete = true;
                        else
                            pending.Push((entry.Path, depth + 1));
                        continue;
                    }
                    if (!entry.Path.EndsWith(threadId + ".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                    if (candidates >= budget.MaximumCandidates) return new(selected, true, entries, directories);
                    candidates++;
                    if (!accept(entry.Path)) continue;
                    if (Expired()) return new(selected, true, entries, directories);
                    var written = File.GetLastWriteTimeUtc(entry.Path);
                    if (selected is null || written > newest) { selected = entry.Path; newest = written; }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                incomplete = true;
            }
        }
        cancellation.ThrowIfCancellationRequested();
        return new(selected, incomplete, entries, directories);
    }

    private static IEnumerable<RolloutSearchEntry> Enumerate(string directory)
    {
        // Do not let Directory.EnumerateFiles perform recursion internally: those unseen entries
        // would escape our budgets. Recheck a queued directory in case it became a junction.
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) yield break;
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        }))
            yield return new(entry.FullName, entry.Attributes);
    }
}

/// <summary>
/// Bounded per-thread positive/negative cache. Missing/unreadable rollouts retry after 2, 4, 8, 16,
/// 32, then 60 seconds. Relevant watcher events invalidate a miss. A global one-second floor and
/// one-search-per-poll guard also apply to invalidation and rapid thread changes.
/// Single-consumer: watcher callbacks enqueue paths; only the monitor's Poll calls this class.
/// </summary>
internal sealed class RolloutDiscovery
{
    internal const int MaximumCachedThreads = 64;
    internal const long MinimumSearchIntervalMs = 1000;
    internal const long MaximumRetryMs = 60_000;
    private readonly Func<string, bool, CancellationToken, RolloutSearchResult> _search;
    private readonly Func<long> _clock;
    private readonly Dictionary<(string Id, bool Root), Entry> _cache = new();
    private bool _searchedThisPoll;
    private long _nextSearch = long.MinValue;
    private long _sequence;

    private sealed record Entry(string? Path, int Failures, long RetryAt, long Sequence, bool Incomplete);

    public RolloutDiscovery(Func<string, bool, CancellationToken, RolloutSearchResult> search,
        Func<long>? clock = null)
    {
        _search = search;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    internal int CachedThreadCount => _cache.Count;
    public bool DiscoveryIncomplete { get; private set; }
    public void BeginPoll() { _searchedThisPoll = false; DiscoveryIncomplete = false; }

    public string? Resolve(string threadId, bool requireDesktopRoot, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!IsPlausibleThreadId(threadId)) return null;
        var key = Key(threadId, requireDesktopRoot);
        _cache.TryGetValue(key, out var entry);
        DiscoveryIncomplete = entry?.Incomplete == true;
        if (entry?.Path is not null) return entry.Path;
        var now = _clock();
        if (_searchedThisPoll || now < _nextSearch || (entry is not null && now < entry.RetryAt)) return null;
        _searchedThisPoll = true;
        _nextSearch = now + MinimumSearchIntervalMs;
        var result = _search(threadId, requireDesktopRoot, cancellation);
        cancellation.ThrowIfCancellationRequested();
        DiscoveryIncomplete = result.BudgetReached;
        if (result.Path is not null)
            Store(key, new(result.Path, entry?.Failures ?? 0, 0, ++_sequence, result.BudgetReached));
        else
            StoreFailure(key, entry, result.BudgetReached);
        return result.Path;
    }

    public void MarkUnavailable(string threadId, bool requireDesktopRoot)
    {
        var key = Key(threadId, requireDesktopRoot);
        _cache.TryGetValue(key, out var entry);
        StoreFailure(key, entry, entry?.Incomplete == true);
    }

    public void ObservePath(string path)
    {
        foreach (var key in _cache.Keys.ToArray())
            if (path.EndsWith(key.Id + ".jsonl", StringComparison.OrdinalIgnoreCase)) _cache.Remove(key);
    }

    public void Remember(string threadId, bool requireDesktopRoot, string path) =>
        Store(Key(threadId, requireDesktopRoot), new(path, 0, 0, ++_sequence, false));

    private void StoreFailure((string Id, bool Root) key, Entry? previous, bool incomplete)
    {
        var failures = Math.Min(6, (previous?.Failures ?? 0) + 1);
        var delay = Math.Min(MaximumRetryMs, 2000L << (failures - 1));
        Store(key, new(null, failures, _clock() + delay, ++_sequence, incomplete));
    }

    private void Store((string Id, bool Root) key, Entry value)
    {
        if (!_cache.ContainsKey(key) && _cache.Count >= MaximumCachedThreads)
            _cache.Remove(_cache.MinBy(pair => pair.Value.Sequence).Key);
        _cache[key] = value;
    }

    private static (string Id, bool Root) Key(string id, bool requireRoot) => (id.ToUpperInvariant(), requireRoot);
    internal static bool IsPlausibleThreadId(string id) => id.Length is >= 8 and <= 64
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
