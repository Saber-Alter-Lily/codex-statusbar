"""Source-only regression guards. Does not import/run upstream code or read user data."""
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
def read(name): return (ROOT / name).read_text(encoding="utf-8")
SRC = "src/CodexStatusbar/"
checks = 0
def check(condition, description):
    global checks
    assert condition, description
    checks += 1
startup = read(SRC + "StartupManager.cs")
check("var desired = configured && startWithWindows;" in startup, "startup defaults off")
check("settings.StartupConfigured && settings.StartupOptInConfirmed" in startup, "legacy enable is not consent")
check("settings.StartupOptInConfirmed = enabled;" in startup, "explicit toggle records consent")
check(not (ROOT / SRC / "CodexThemeSource.cs").exists(), "configuration reader removed")
context = read(SRC + "OverlayContext.cs")
check("CodexThemeSource" not in context and "跟随 Windows" in context, "OS/manual theme")
program = read(SRC + "Program.cs")
check('"recv " + Encoding.UTF8.GetString(payload)' not in program, "no raw IPC observer")
check("IsAllowedRolloutPath(record.RolloutPath)" in program, "DB paths checked")
check("FileAttributes.ReparsePoint" in program, "reparse points rejected")
check("Model-stage TPS estimate (includes reasoning; not per-token)" in program, "terminal semantics")
check("DebugDiagnostics.FormatTtft(snapshot?.TtftMs)" in program, "terminal TTFT")
db = read(SRC + "StateDatabase.cs")
resolver = db.split("public static string ResolvePathForSessionsRoot", 1)[1].split("return ResolvePath();", 1)[0]
check("File.Exists" not in resolver, "missing isolated DB cannot trigger fallback")
diagnostics = read(SRC + "DebugDiagnostics.cs")
for label in ["Current thread:", "Title:", "Turn:", "Rollout path:", "Startup command:", "Warnings:"]:
    check('"' + label + '"' in diagnostics.split("SensitiveLabels =", 1)[1], "redacted " + label)
check("lifecycle event (details redacted)" in diagnostics, "freeform events suppressed")
probe = read("tools/probe_codex_ipc.mjs")
check('fs.openSync(logPath, "wx", 0o600)' in probe, "explicit exclusive private log")
check('arg("--log", null)' in probe, "no default IPC output file")
check("msg: m" not in probe and "body.slice" not in probe and "params=${" not in probe, "no raw IPC dump")
for name in ["probe_appserver.mjs", "groundtruth_tps.mjs"]:
    code = read("tools/" + name)
    check(code.index('argv.includes("--allow-live-inference")') < code.index("spawn(CODEX_BIN"), "inference opt-in " + name)
check('throw "self-test binary not found' in read("build.ps1"), "missing test fails closed")
check('throw "fixture checks failed' in read("build.ps1"), "failed fixture check blocks publish")
print(f"PASS: {checks} source-only guards (not a build or runtime test)")
