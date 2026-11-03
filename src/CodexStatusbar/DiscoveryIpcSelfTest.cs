using System.IO.Pipes;

namespace CodexStatusbar;

/// <summary>Pure policy tests, invented directory entries and a randomly named synthetic pipe only.</summary>
internal static class DiscoveryIpcSelfTest
{
    public static IReadOnlyList<(string Name, bool Passed)> Evaluate()
    {
        var checks = new List<(string Name, bool Passed)>();
        RunCache(checks);
        RunBudgets(checks);
        RunIdentity(checks);
        RunDisposedMonitor(checks);
        return checks;
    }

    private static void RunCache(List<(string Name, bool Passed)> checks)
    {
        long now = 0;
        var searches = 0;
        var discovery = new RolloutDiscovery((_, _, _) => { searches++; return new(null, false, 0, 0); }, () => now);
        const string id = "synthetic-thread-1";
        discovery.BeginPoll();
        discovery.Resolve(id, true, default);
        now = 10_000; // Even when time advances, a second lookup within this poll cannot scan.
        discovery.Resolve("synthetic-thread-2", true, default);
        checks.Add(("discovery performs at most one recursive search per poll", searches == 1));

        var retry = new RolloutDiscovery((_, _, _) => { searches++; return new(null, false, 0, 0); }, () => now);
        now = 0; searches = 0;
        retry.BeginPoll(); retry.Resolve(id, true, default);
        var exponential = searches == 1;
        var expectedSearches = 1;
        long due = 0;
        foreach (var delay in new long[] { 2000, 4000, 8000, 16000, 32000, 60000, 60000 })
        {
            due += delay;
            now = due - 1;
            retry.BeginPoll(); retry.Resolve(id, true, default);
            exponential &= searches == expectedSearches;
            now = due;
            retry.BeginPoll(); retry.Resolve(id, true, default);
            exponential &= searches == ++expectedSearches;
        }
        checks.Add(("missing rollout retries exponentially and caps at sixty seconds", exponential));

        now = due + 1000;
        retry.ObservePath("unrelated.jsonl");
        retry.BeginPoll(); retry.Resolve(id, true, default);
        checks.Add(("unrelated watcher event preserves a negative cache", searches == expectedSearches));
        retry.ObservePath("rollout-" + id + ".jsonl");
        retry.BeginPoll(); retry.Resolve(id, true, default);
        checks.Add(("relevant watcher event invalidates the missing-thread cache", searches == ++expectedSearches));
        now += 100;
        retry.ObservePath("rollout-" + id + ".jsonl");
        retry.BeginPoll(); retry.Resolve(id, true, default);
        checks.Add(("event storms still respect the global search cooldown", searches == expectedSearches));
        now += 1000;
        retry.BeginPoll(); retry.Resolve(id, true, default);
        checks.Add(("invalidated miss is retried after the cooldown", searches == ++expectedSearches));

        var before = searches;
        retry.Remember(id, true, "accepted-synthetic-path");
        retry.BeginPoll();
        var path = retry.Resolve(id, true, default);
        checks.Add(("validated watcher/database path bypasses recursive search", path == "accepted-synthetic-path" && searches == before));
        retry.MarkUnavailable(id, true);
        retry.BeginPoll();
        checks.Add(("failed reader invalidates positive cache and backs off", retry.Resolve(id, true, default) is null && searches == before));
        for (var index = 0; index < 1000; index++) retry.Remember("synthetic-" + index, true, "accepted-synthetic-path");
        checks.Add(("discovery cache has a fixed thread-count cap", retry.CachedThreadCount == RolloutDiscovery.MaximumCachedThreads));
        retry.BeginPoll();
        checks.Add(("malformed thread identifiers cannot start a search", retry.Resolve("../../x", true, default) is null && searches == before));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledLookup = false;
        try { retry.BeginPoll(); retry.Resolve(id, true, cancelled.Token); }
        catch (OperationCanceledException) { cancelledLookup = true; }
        checks.Add(("cancelled discovery does no filesystem work", cancelledLookup && searches == before));
    }

    private static void RunBudgets(List<(string Name, bool Passed)> checks)
    {
        const string id = "synthetic-thread";
        var moves = 0;
        IEnumerable<RolloutSearchEntry> ManyFiles(string _)
        {
            for (var index = 0; index < 10000; index++)
            { moves++; yield return new("irrelevant-" + index, FileAttributes.Normal); }
        }
        var result = BoundedRolloutSearch.Find("synthetic-root", id, _ => true, default,
            new(MaximumEntries: 7), ManyFiles, () => 0, _ => true);
        checks.Add(("scan counts nonmatching entries against its budget", result.BudgetReached && result.Entries == 7 && moves == 7));

        IEnumerable<RolloutSearchEntry> WideTree(string directory)
        {
            for (var index = 0; index < 100; index++) yield return new(directory + "/" + index, FileAttributes.Directory);
        }
        result = BoundedRolloutSearch.Find("synthetic-root", id, _ => true, default,
            new(MaximumDirectories: 4), WideTree, () => 0, _ => true);
        checks.Add(("directory queue and visited directories obey one global cap", result.BudgetReached && result.Directories <= 4));

        IEnumerable<RolloutSearchEntry> DeepTree(string directory) => [new(directory + "/child", FileAttributes.Directory)];
        result = BoundedRolloutSearch.Find("synthetic-root", id, _ => true, default,
            new(MaximumDepth: 2), DeepTree, () => 0, _ => true);
        checks.Add(("scan does not descend beyond the depth cap", result.BudgetReached && result.Directories == 3));

        var accepted = 0;
        IEnumerable<RolloutSearchEntry> Matches(string directory) => Enumerable.Range(0, 100)
            .Select(index => new RolloutSearchEntry(directory + index + id + ".jsonl", FileAttributes.Normal));
        result = BoundedRolloutSearch.Find("synthetic-root", id, _ => { accepted++; return false; }, default,
            new(MaximumCandidates: 3), Matches, () => 0, _ => true);
        checks.Add(("candidate header checks have their own budget", result.BudgetReached && accepted == 3));

        long time = 0;
        IEnumerable<RolloutSearchEntry> Timed(string _)
        {
            for (var index = 0; index < 100; index++)
            { time += 10; yield return new("irrelevant", FileAttributes.Normal); }
        }
        result = BoundedRolloutSearch.Find("synthetic-root", id, _ => true, default,
            new(MaximumMilliseconds: 25), Timed, () => time, _ => true);
        checks.Add(("time budget is checked again after each enumeration operation", result.BudgetReached && result.Entries == 3));

        var enumerated = 0;
        IEnumerable<RolloutSearchEntry> Links(string directory)
        {
            enumerated++;
            yield return new(directory + "/junction", FileAttributes.Directory | FileAttributes.ReparsePoint);
        }
        result = BoundedRolloutSearch.Find("synthetic-root", id, _ => true, default,
            enumerate: Links, milliseconds: () => 0, safeDirectory: _ => true);
        checks.Add(("recursive discovery never follows a reparse-point entry", enumerated == 1 && result.Directories == 1));

        var iteratorDisposed = false;
        using var cancellation = new CancellationTokenSource();
        IEnumerable<RolloutSearchEntry> Cancelling(string _)
        {
            try
            {
                cancellation.Cancel();
                yield return new("irrelevant", FileAttributes.Normal);
            }
            finally { iteratorDisposed = true; }
        }
        var interrupted = false;
        try
        {
            BoundedRolloutSearch.Find("synthetic-root", id, _ => true, cancellation.Token,
                enumerate: Cancelling, milliseconds: () => 0, safeDirectory: _ => true);
        }
        catch (OperationCanceledException) { interrupted = true; }
        checks.Add(("in-flight cancellation stops discovery and disposes its enumerator", interrupted && iteratorDisposed));
    }

    private static void RunIdentity(List<(string Name, bool Passed)> checks)
    {
        var official = CodexIpcServerIdentity.OfficialPackageFamily;
        checks.Add(("pipe identity accepts its exact OS-verified Codex owner", CodexIpcServerIdentity.IsAcceptedServer(17, 17, official, null)));
        checks.Add(("pipe PID cannot be substituted with a different verified Codex PID", !CodexIpcServerIdentity.IsAcceptedServer(17, 18, official, null)));
        checks.Add(("missing pipe PID fails closed", !CodexIpcServerIdentity.IsAcceptedServer(0, 0, official, null)));
        checks.Add(("unknown package and lookalike WindowsApps path fail closed", !CodexIpcServerIdentity.IsAcceptedServer(17, 17, null,
            @"C:\Temp\WindowsApps\OpenAI.Codex_fake\ChatGPT.exe")));
        checks.Add(("Codex-looking package with a different publisher is rejected", !CodexIpcServerIdentity.IsAcceptedServer(17, 17,
            "OpenAI.Codex_differentpublisher", null)));
        checks.Add(("ChatGPT Desktop package is not Codex IPC", !CodexIpcServerIdentity.IsAcceptedServer(17, 17,
            "OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0", null)));
        if (OperatingSystem.IsWindows())
        {
            // A random name, owned by this self-test process. No real Codex process, pipe, profile,
            // credential or message is inspected. Production authentication must reject this peer.
            try { checks.Add(("synthetic non-Codex pipe rejected before initialize bytes", RejectSyntheticPipeAsync().GetAwaiter().GetResult())); }
            catch { checks.Add(("synthetic non-Codex pipe rejected before initialize bytes", false)); }
        }
    }

    private static async Task<bool> RejectSyntheticPipeAsync()
    {
        var name = "codex-statusbar-synthetic-" + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(2000, deadline.Token).ConfigureAwait(false);
        await connected.ConfigureAwait(false);
        if (CodexIpcServerIdentity.ReadServerProcessId(client) != (uint)Environment.ProcessId) return false;
        var rejected = false;
        try
        {
            using var unexpected = await CodexIpcActiveThreadMonitor.AuthenticateAndInitializeAsync(client, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException) { rejected = true; }
        client.Dispose();
        var buffer = new byte[1];
        try { return rejected && await server.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false) == 0; }
        catch (IOException) { return rejected; } // Windows may report peer close as broken-pipe.
    }

    private static void RunDisposedMonitor(List<(string Name, bool Passed)> checks)
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-statusbar-disposed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var id = Guid.NewGuid().ToString();
            var fixture = Path.Combine(directory, "rollout-" + id + ".jsonl");
            File.WriteAllText(fixture, "{}\n");
            var result = BoundedRolloutSearch.Find(directory, id, path => path == fixture, default,
                new(MaximumMilliseconds: 5000));
            checks.Add(("bounded production directory enumerator finds only a synthetic local fixture",
                result.Path == fixture && !result.BudgetReached && result.Entries == 1));
            using var monitor = new TokenLogMonitor(directory, Path.Combine(directory, "missing.sqlite"));
            monitor.Dispose();
            monitor.Dispose();
            checks.Add(("disposed rollout monitor cancels work and rejects later polls", monitor.Poll() is null));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
