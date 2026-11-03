using System.Text;

namespace CodexStatusbar;

/// <summary>Only synthetic strings and newly created temporary files; no desktop or IPC access.</summary>
internal static class SecurityPrivacySelfTest
{
    public static IReadOnlyList<(string Name, bool Passed)> Evaluate()
    {
        const string secret = "SYNTHETIC_PRIVATE_DO_NOT_EXPORT_7B4C";
        var checks = new List<(string Name, bool Passed)>();
        var root = Path.Combine(Path.GetTempPath(), "statusbar-privacy-synthetic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var capturedOut = new StringWriter();
        using var capturedError = new StringWriter();
        try
        {
            Console.SetOut(capturedOut);
            Console.SetError(capturedError);
            var snapshot = new TokenSnapshot(secret, secret + ".jsonl", 30, 10, 0, 20,
                0, 0, 0, DateTime.UnixEpoch, Title: secret, TurnId: secret,
                Source: secret, BaselineSource: secret, Warnings: [secret]);
            var block = DebugDiagnostics.Build(snapshot);
            checks.Add(("privacy: normal and headless snapshot block excludes private fields",
                !block.Contains(secret, StringComparison.Ordinal)));

            var lifecycle = default(DebugDiagnostics.LifecycleReport) with
            {
                Mode = "synthetic", SingleInstance = "synthetic", StartupRegistration = "disabled",
                StartupCommand = secret, WatcherState = "synthetic", Package = "synthetic",
                CodexVersion = "synthetic", DetectionRule = secret, WatcherError = secret
            };
            var lifecycleBlock = DebugDiagnostics.BuildLifecycle(lifecycle);
            checks.Add(("privacy: lifecycle command, detection and errors are redacted",
                !lifecycleBlock.Contains(secret, StringComparison.Ordinal)));

            var dock = ComposerDockSnapshot.Empty with
            {
                ElementName = secret, AutomationId = secret, ClassName = secret, Failure = secret
            };
            var positionBlock = DebugDiagnostics.BuildPosition(
                ManualAttachmentRules.DefaultMainAttachment, OverlayPositionMode.ComposerContextLeft,
                null, null, null, false, 96, "Light", "Windows", true,
                new DebugDiagnostics.DockedPositionReport(dock, 0, 0, 0, 0, 0, []));
            checks.Add(("privacy: synthetic UIA names and errors are redacted",
                !positionBlock.Contains(secret, StringComparison.Ordinal)));

            var performance = default(DebugDiagnostics.PositionPerformanceReport) with
            {
                AccurateSource = "synthetic", WatcherError = secret
            };
            var performanceBlock = DebugDiagnostics.BuildPositionPerformance(performance);
            checks.Add(("privacy: position watcher error is redacted",
                !performanceBlock.Contains(secret, StringComparison.Ordinal)));

            var log = Path.Combine(root, "synthetic-debug.log");
            using var diagnostics = new DebugDiagnostics(true, log);
            diagnostics.Write(snapshot);
            diagnostics.Event(secret);
            diagnostics.WriteLifecycle(lifecycleBlock);
            diagnostics.WritePosition(positionBlock);
            diagnostics.WritePositionPerformance(performanceBlock);
            diagnostics.WritePosition(secret);
            diagnostics.WriteLifecycle(secret);
            diagnostics.WritePositionPerformance(secret);
            diagnostics.FlushAsync().GetAwaiter().GetResult();
            checks.Add(("privacy: actual log including freeform Write bodies excludes fake secrets",
                !File.ReadAllText(log, Encoding.UTF8).Contains(secret, StringComparison.Ordinal)));

            // Test the real dispatch boundary, not just the redaction formatter. All former CLI
            // entrypoints must reject before observing a desktop, IPC, profile, or output file.
            string[] blocked = ["--ipc-probe", "--position-probe", "--render-probe", "--show-probe",
                "--hotkey-probe", "--theme-probe", "--attachment-probe", "--window-classification-probe",
                "--settings-probe", "--presentation-probe", "--layout-probe", "--interaction-probe",
                "--form-probe", "--window-probe", "--thread-switch-probe", "--probe"];
            var sentinel = Path.Combine(root, secret + ".json");
            File.WriteAllText(sentinel, secret, Encoding.UTF8);
            var nonexistentProfile = Path.Combine(root, "profile-that-must-not-be-opened");
            foreach (var command in blocked)
            {
                var handled = ProbeRunner.TryRun([command, sentinel, secret, secret], nonexistentProfile);
                checks.Add(($"privacy: {command} rejects without overwriting output or starting live probes",
                    handled && ProbeRunner.LastExitCode == 2
                    && File.ReadAllText(sentinel, Encoding.UTF8) == secret));
            }
            checks.Add(("privacy: uppercase probe flag is denied",
                ProbeRunner.TryRun(["--WINDOW-PROBE", sentinel], nonexistentProfile)
                && ProbeRunner.LastExitCode == 2));
            checks.Add(("privacy: misplaced probe flag cannot fall through to normal startup",
                ProbeRunner.TryRun(["--no-overlay", "--probe", sentinel], nonexistentProfile)
                && ProbeRunner.LastExitCode == 2));
            checks.Add(("privacy: unrecognized future probe name is denied",
                ProbeRunner.TryRun(["--synthetic-future-probe", sentinel], nonexistentProfile)
                && ProbeRunner.LastExitCode == 2));
            checks.Add(("privacy: synthetic self-test remains an allowed command",
                !ProbeRunner.IsBlockedDiagnosticArgument("--self-test")));
            checks.Add(("privacy: no profile was created by rejected diagnostic commands",
                !Directory.Exists(nonexistentProfile)));
            checks.Add(("privacy: command rejections and console diagnostics never echo fake secrets",
                !capturedOut.ToString().Contains(secret, StringComparison.Ordinal)
                && !capturedError.ToString().Contains(secret, StringComparison.Ordinal)));
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Directory.Delete(root, recursive: true);
        }
        return checks;
    }
}
