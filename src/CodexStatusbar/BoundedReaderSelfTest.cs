using System.Text;

namespace CodexStatusbar;

/// <summary>Synthetic temporary files only; never resolves the user's Codex home.</summary>
internal static class BoundedReaderSelfTest
{
    public static IReadOnlyList<(string Name, bool Passed)> Evaluate()
    {
        var checks = new List<(string Name, bool Passed)>();
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "codex-statusbar-synthetic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = System.IO.Path.Combine(directory, "synthetic.jsonl");
            var meta = "{\"type\":\"session_meta\",\"payload\":{\"id\":\"synthetic-a\",\"originator\":\"Codex Desktop\"}}\n";
            var usage = Usage(7, 3);
            var split = usage.Length / 2;
            File.WriteAllText(path, meta + usage[..split], new UTF8Encoding(false));
            var reader = new RolloutReader(path, maxTailBytes: 1024);
            reader.Initialize();
            checks.Add(("partial line advances read offset without inventing usage",
                reader.Offset == new FileInfo(path).Length && !reader.State.HasCumulativeUsage));
            File.AppendAllText(path, usage[split..] + "\n", new UTF8Encoding(false));
            reader.TryUpdate();
            checks.Add(("partial line is completed once without duplicate-prefix corruption",
                reader.State.TotalTokens == 10 && reader.State.LineCount == 2));
            checks.Add(("no append is unchanged", reader.TryUpdate() == RolloutUpdateResult.Unchanged));

            // The first chunk ends with a nested brace but is not yet a complete JSON document.
            File.WriteAllText(path, meta + usage[..^1], new UTF8Encoding(false));
            reader.Initialize();
            File.AppendAllText(path, "}\n", new UTF8Encoding(false));
            reader.TryUpdate();
            checks.Add(("nested closing brace does not consume an incomplete record",
                reader.State.TotalTokens == 10));

            File.WriteAllText(path, meta + string.Concat(Enumerable.Repeat("{}\n", 2048)),
                new UTF8Encoding(false));
            reader.Initialize();
            checks.Add(("missing usage recovery respects tail cap",
                reader.ParsedBytes <= 1024 && !reader.TailWasExtended
                && !reader.State.HasCumulativeUsage && reader.State.Tps.DisplayTps is null
                && reader.State.Warnings.Contains("tail-window-limit")));

            File.WriteAllText(path, meta + new string('x', 4096), new UTF8Encoding(false));
            reader.Initialize();
            checks.Add(("oversized unterminated tail is bounded", reader.ParsedBytes <= 1024));
            File.AppendAllText(path, "discarded continuation\n" + usage + "\n", new UTF8Encoding(false));
            reader.TryUpdate();
            checks.Add(("after oversized line only next complete record is parsed",
                reader.State.TotalTokens == 10));

            File.WriteAllText(path, "[]\n{\"type\":\"event_msg\",\"payload\":[]}\n" + usage + "\n",
                new UTF8Encoding(false));
            reader.Initialize();
            checks.Add(("malformed shapes do not prevent later valid usage",
                reader.State.TotalTokens == 10));

            // Reinitialize must also clear private dedup and carrier state, not just public totals.
            reader.Initialize();
            checks.Add(("reseed with identical records is not suppressed as duplicate",
                reader.State.TotalTokens == 10));
            File.WriteAllText(path, "{\"type\":\"session_meta\",\"payload\":{\"id\":\"synthetic-b\"}}\n",
                new UTF8Encoding(false));
            reader.Initialize();
            checks.Add(("replacement clears old counters and metadata",
                reader.State.TotalTokens == 0 && reader.State.SessionMetaId == "synthetic-b"
                && reader.State.Carrier == "none"));

            File.AppendAllText(path, new string('x', 2048), new UTF8Encoding(false));
            checks.Add(("large append requests bounded reinitialization",
                reader.TryUpdate() == RolloutUpdateResult.NeedsReinitialize));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        return checks;
    }

    private static string Usage(int input, int output) =>
        "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":"
        + "{\"input_tokens\":" + input + ",\"cached_input_tokens\":0,\"output_tokens\":" + output
        + ",\"reasoning_output_tokens\":0,\"total_tokens\":" + (input + output) + "}}}}";
}
