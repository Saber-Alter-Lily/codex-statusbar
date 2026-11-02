# CodexStatusbar — hardening fork

[简体中文](README.zh-CN.md)

A Windows companion status strip for Codex Desktop, derived from
[dongyue-19/codex-statusbar](https://github.com/dongyue-19/codex-statusbar).
It displays token totals, cache-hit information and a model-stage speed estimate.
The expanded details and headless output also show reported time to first token (TTFT).

**Source changes only: this fork has not been built or runtime-tested.** There is no
security certification or verified binary release. Upstream test results and performance
claims must not be treated as validation of this fork.

## Recent-session monitoring (source implementation)

The tray now includes a recent-session list for up to eight independently tracked
conversations, showing a local short identifier, model-stage TPS estimate, reported
TTFT and state. It discovers the selected conversation and log changes observed since
startup, **not all historical conversations**. Speeds are never added together; stale
or missing data is not presented as live speed. This UI and its synthetic C# tests have
not yet been run. See the [phase-two report](docs/MULTI_SESSION_PHASE2.zh-CN.md).

## What changed

- Windows startup is off by default and requires explicit consent. An upstream
  automatically enabled preference is not consent; migration removes the existing
  same-name startup registration until you explicitly enable it again.
- Automatic appearance follows Windows; manual dark/light overrides remain available.
  The theme feature no longer reads Codex configuration.
- Normal debug diagnostics redact conversation identifiers, titles, file paths and
  free-form errors. Free-form lifecycle messages are suppressed.
- IPC diagnostics do not dump raw message bodies. The Node IPC probe reports only
  fixed-category counts and writes no file unless explicitly requested.
- An explicitly selected sessions directory does not fall back to another profile's
  database. Recovered rollout paths must pass root-containment and reparse-point checks.
- Live-inference developer probes require `--allow-live-inference`. They are not
  read-only: they can use inherited credentials, send prompts, incur charges and save content.
- Missing or failed required build checks block publication unless checks are
  explicitly skipped.

See the [Chinese audit and change report](docs/HARDENING_PHASE1.zh-CN.md) for scope,
verification and remaining work.

## Build and use

Windows and a compatible .NET SDK are required for building the Windows application;
the project targets .NET 10. Review the source and use an isolated test profile before
allowing the application to access personal sessions. Build instructions describe
future validation; they were **not executed during this review**.

```powershell
pwsh -File build.ps1
```

The build script runs application checks and publishes an executable. `-SkipTest`
explicitly bypasses those checks and produces an unverified build. Do not confuse
successful publication with a security assessment.

After independently building and validating the application, launching
`CodexStatusbar.exe` opens its tray/overlay workflow. The tray provides appearance,
position and startup controls. Do not run this fork alongside the upstream application:
they share startup-registration and application-state conventions.

| Option | Purpose |
| --- | --- |
| `--no-overlay` | Headless monitoring with terminal metrics; still accesses session data. |
| `--duration SECONDS` | Bound headless monitoring duration. |
| `--sessions DIRECTORY` | Select a sessions root; use an isolated synthetic dataset for testing. |
| `--settings FILE` | Select an absolute settings-file path. |
| `--debug` | Enable redacted diagnostics; review any output before sharing. |
| `--background` | Start in the background/tray workflow. |
| `--install-startup` | Explicitly enable per-user Windows startup. |
| `--uninstall-startup` | Disable per-user Windows startup. |
| `--startup-status` | Inspect startup state; its output can include local executable paths. |
| `--self-test fixtures` | Run application self-tests after building; not run in this review. |
| `--ipc-probe` | Inspect redacted local IPC connection/resolution status. |

The application intentionally reads Codex session metadata and usage records to provide
its metrics. It is not a sandbox or a privacy boundary against another process running
as the same user. Specialized developer probes beyond normal debug/IPC diagnostics
may still expose sensitive data; do not run or share their output casually.

## Metric semantics

- **TPS is a model-stage estimate**, not measured per-token streaming throughput.
  It divides reported output tokens by model-stage timing windows, including reasoning.
  Some records require inferred stage boundaries, which may include preparation or waiting.
  The compact strip retains its `tok/s` unit; the expanded name explains the estimate.
- **TTFT is an upstream-reported value**, not a locally inferred first-token event.
  Missing or invalid values remain unknown, not zero; starting a new turn clears the
  previous turn's latency. The details row identifies reported and missing values.
- Token and cache metrics depend on the records available to the application. Missing
  or incomplete data must not be interpreted as an independently measured result.

## Verification and limitations

Source-only regression guards, JavaScript syntax checks and diff whitespace checks passed.
No .NET build, C# self-test, Windows UI, registry migration or live IPC test has run.
The new TTFT detail row still needs visual and behavioral validation.

Selected and background readers now have bounded read windows; outstanding work includes
whole-process resource limits and behavioral validation of unusually large session logs,
audit of additional developer probes, filesystem race handling and release-chain review.
Normal diagnostic redaction does not sanitize previously generated files or repository history.

These README files were rewritten without copying upstream runtime transcripts,
conversation titles or identifiers, local user paths, process identifiers or usage samples.
They are not evidence that every other repository file or historical revision is sanitized.

## Attribution and license

Upstream: [dongyue-19/codex-statusbar](https://github.com/dongyue-19/codex-statusbar).
Copyright (c) 2026 CodexStatusbar contributors.

The [MIT license](LICENSE), [codex-token-overlay MIT license](LICENSE-codex-token-overlay)
and [third-party notices](THIRD-PARTY-NOTICES.md) remain intact. The latter dependency's
copyright is (c) 2026 soleillevant0125. This fork does not imply upstream endorsement.
