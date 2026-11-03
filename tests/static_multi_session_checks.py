"""Source-only checks for phase 2; never runs/imports the application or reads user files."""
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src/CodexStatusbar"
def read(name): return (SRC / name).read_text(encoding="utf-8")
checks = 0
def check(condition, description):
    global checks
    assert condition, description
    checks += 1
recent = read("RecentSessionMonitor.cs")
for token in ["MaximumSessions = 8", "MaximumPendingPaths = 64", "MaximumReadBytes = 1024 * 1024",
              "PollInterval = TimeSpan.FromSeconds(1)", "StaleAfter = TimeSpan.FromSeconds(15)",
              "RetainFor = TimeSpan.FromMinutes(10)"]:
    check(token in recent, "resource/time bound " + token)
check("Dictionary<string, Entry>" in recent and "new RolloutReader(path, maxTailBytes: MaximumReadBytes, sessionRoot: _root)" in recent,
      "thread-keyed independent bounded readers")
check("Directory.Enumerate" not in recent and "StateDatabase" not in recent, "no multi-session scan/DB discovery")
check("File.Write" not in recent and "Console." not in recent and "DebugDiagnostics" not in recent,
      "no persistence or diagnostic emission of multi-session identities")
check(".Sum(" not in recent, "no combined TPS/tokens")
check("!entry.Id.Equals(id" in recent and "_entries.Remove(previous.Id)" in recent, "same-path replacement retires old thread")
check("entry.Readable && !stale ? tracker.DisplayTps : null" in recent, "stale/unreadable speed not live")
check("now - write > RetainFor" in recent, "expired backlog cannot resurrect old history")
check("new byte[64 * 1024]" in recent and "Guid.TryParse" in recent, "bounded authoritative metadata identity")
program = read("Program.cs")
check("new RolloutReader(path, maxTailBytes: TpsRules.TailReadBytes, sessionRoot: _sessionRoot)" in program, "selected reader tail is also bounded")
check("RecentSessionMonitor.TryReadDesktopId(path, out _)" in program, "selected metadata read is bounded")
check("RecentSessions.ObservePath(path)" in program, "watcher events feed bounded discovery")
check("_watcher.Error += (_, _) => RecentSessions.MarkDiscoveryIncomplete()" in program, "watcher overflow visible")
ui = read("OverlayContext.cs")
check("UpdateRecentSessionMenu(task.Result.RecentSessions" in ui, "UI consumes completed background snapshots")
check("ReferenceEquals(monitor, _monitor)" in ui, "stopped monitor cannot publish stale results")
check("非全部历史" in ui and "发现队列溢出" in ui, "coverage limits visible")
check("UpdateRecentSessionMenu([], false)" in ui, "shutdown clears list")
reader = read("RolloutReader.cs")
check("int? maxTailBytes = null" in reader and "var window = (long)ReadBudget" in reader, "reader accepts cap")
check("Offset = length - _pending.Length" not in reader and "Offset = length;" in reader, "pending prefix not reread")
check("State = new RolloutStreamState();" in reader.split("private void ResetState()", 1)[1], "reseed resets private state")
check("tail-window-limit" in reader and "oversized-line" in reader, "bounded-read gaps explicit")
selftest = read("SelfTest.cs")
check("RecentSessionSelfTest.Evaluate()" in selftest and "BoundedReaderSelfTest.Evaluate()" in selftest, "synthetic suites wired")
for name in ["RecentSessionSelfTest.cs", "BoundedReaderSelfTest.cs"]:
    code = read(name)
    check("GetTempPath()" in code and "Directory.Delete(" in code and "SessionPathResolver" not in code,
          "synthetic temporary files only: " + name)
print(f"PASS: {checks} phase-2 source guards (not compilation or runtime tests)")
