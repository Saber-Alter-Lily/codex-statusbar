namespace CodexStatusbar;

/// <summary>Pure synthetic nodes and clocks; no UIA, windows, processes, files or user profiles.</summary>
internal static class ComposerDiscoverySelfTest
{
    private sealed class Node(string name)
    {
        public string Name { get; } = name;
        public Node? FirstChild { get; set; }
        public Node? NextSibling { get; set; }
        public Node? Parent { get; set; }
    }

    public static IReadOnlyList<(string Name, bool Passed)> Evaluate()
    {
        var checks = new List<(string Name, bool Passed)>();
        long now = 0;
        var root = new Node("root");
        Node? tail = null;
        for (var index = 0; index < ComposerDiscoveryBudget.MaximumNodes + 10; index++)
        {
            var child = new Node("wide-" + index) { Parent = root };
            if (tail is null) root.FirstChild = child;
            else tail.NextSibling = child;
            tail = child;
        }
        var budget = new ComposerDiscoveryBudget(() => now);
        var visits = 0;
        var result = Search(root, budget, node => { visits++; return node.Name == "absent"; });
        checks.Add(("wide siblings obey the shared node limit", result is null
            && visits == ComposerDiscoveryBudget.MaximumNodes && budget.NodesVisited == visits));

        var cycle = new Node("cycle");
        cycle.NextSibling = cycle;
        root.FirstChild = cycle;
        budget = new ComposerDiscoveryBudget(() => now);
        visits = 0;
        Search(root, budget, _ => { visits++; return false; });
        checks.Add(("cyclic sibling provider cannot loop forever", visits == ComposerDiscoveryBudget.MaximumNodes));

        root = new Node("root");
        tail = root;
        for (var depth = 1; depth <= ComposerDiscoveryBudget.MaximumDepth + 4; depth++)
        {
            var child = new Node("deep-" + depth) { Parent = tail };
            tail.FirstChild = child;
            tail = child;
        }
        budget = new ComposerDiscoveryBudget(() => now);
        visits = 0;
        Search(root, budget, _ => { visits++; return false; });
        checks.Add(("deep trees stop at the depth budget", visits == ComposerDiscoveryBudget.MaximumDepth));

        root = new Node("root");
        var composer = new Node("composer") { Parent = root };
        var editor = new Node("editor") { Parent = composer };
        root.FirstChild = composer;
        composer.FirstChild = editor;
        budget = new ComposerDiscoveryBudget(() => now);
        checks.Add(("normal match returns its actual parent", ReferenceEquals(
            Search(root, budget, node => node.Name == "editor"), composer)));

        budget = new ComposerDiscoveryBudget(() => now);
        visits = 0;
        result = Search(root, budget, _ =>
        {
            visits++;
            now += ComposerDiscoveryBudget.MaximumMilliseconds;
            return false;
        });
        checks.Add(("elapsed budget stops before inspecting another node", result is null && visits == 1));

        var currentTarget = true;
        budget = new ComposerDiscoveryBudget(() => now, () => currentTarget);
        result = Search(root, budget, _ => { currentTarget = false; return true; });
        checks.Add(("target changed during a read cannot return its stale match", result is null));

        long generation = 7;
        var capturedGeneration = generation;
        budget = new ComposerDiscoveryBudget(() => now, () => generation == capturedGeneration);
        var firstReadAllowed = budget.TryOperation();
        generation += 2; // Disable and enable again before an old provider read completes.
        checks.Add(("disable-enable cycle retires the previous read generation",
            firstReadAllowed && !budget.TryOperation()));

        budget = new ComposerDiscoveryBudget(() => now);
        var operations = 0;
        while (budget.TryOperation()) operations++;
        checks.Add(("provider-operation budget has a hard bound", operations == ComposerDiscoveryBudget.MaximumOperations));

        var retry = new ComposerDiscoveryRetry();
        now = 100;
        checks.Add(("new or changed target may be discovered immediately", retry.CanAttempt(now)));
        retry.RecordFailure(now);
        checks.Add(("frequent idle polling does not bypass a failed discovery deadline",
            Enumerable.Range(0, 10).All(index => !retry.CanAttempt(now + index * 100))));
        retry.Reset();
        var expectedDelays = new[] { 1000, 2000, 4000, 8000, 10000, 10000 };
        foreach (var delay in expectedDelays)
        {
            retry.RecordFailure(now);
            checks.Add(($"failure discovery waits {delay} ms", !retry.CanAttempt(now)
                && !retry.CanAttempt(now + delay - 1) && retry.CanAttempt(now + delay)));
            now += delay;
        }
        retry.RecordFailure(now);
        retry.Reset();
        checks.Add(("success or enable/target generation reset clears old backoff", retry.CanAttempt(now)));
        return checks;
    }

    private static Node? Search(Node root, ComposerDiscoveryBudget budget, Func<Node, bool> match) =>
        ComposerDiscoverySearch.FindParent(root, node => node.FirstChild, node => node.NextSibling,
            match, node => node.Parent, budget);
}
