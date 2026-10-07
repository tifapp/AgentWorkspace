# Testing agent-os

Run `scripts/build.ps1 -Test -Publish` on a Windows x64 machine. The executable integration suite uses real Git repositories, PowerShell commands, Windows Job Objects, and durable runtime state. Its internal script fixture is not a selectable application host and makes no model calls. Exit code 0 means all assertions passed. Failed assertions include retained paths and errors in `results.json`.

The important checks cover private file/index separation; actual contended integration and stale refusal; independent combined validation; failed tests and changed validation inputs; scoped decisions and non-repetition; queued cancellation; descendant cleanup; unknown host outcomes; exclusive runtime ownership; a killed runtime with a surviving child; lost integration acknowledgement; changed evidence; the demonstrated lack of arbitrary-shell confinement; automatic stale revision; external ref races; Unicode; and corrupt state.

For real Codex behavior, run either the app's **Run concurrency check** on a newly created practice project or:

```powershell
.\.tools\dotnet\dotnet.exe run --project src/AgentOS.Cli -- walkthrough artifacts/live artifacts/live-state
```

For repeatable desktop validation against the published executable:

```powershell
.\scripts\test-desktop.ps1 -Live
```

The desktop check uses Windows UI Automation to save setup, launch three real Codex tasks, observe completed and stale candidates, inspect a revision, approve a scoped release, verify its Git tag, view evidence at wide/narrow window sizes, cancel work, and restart. It needs an interactive desktop and authenticated Codex. It consumes normal Codex account usage. It saves its screenshots, exact app/core hashes, runtime state, checks and errors under `artifacts/verification/desktop`. Close other verification windows first; the script targets the process it launches.

If a desktop automation interruption occurs after its cancellation check finishes, `-Resume -Output <same-output-folder>` verifies restart and final contents against the retained project without launching more model work. Keep the earlier report when recording checks performed across multiple builds.

`scripts/source-manifest.ps1` records SHA-256 hashes of all nonignored repository files and the starting Git HEAD. This includes uncommitted implementation, so the starting commit alone is never described as the code tested. Build and test artifacts are ignored by Git but retained locally. The final validation report points to these artifacts and states platform limits.

To reproduce crash handling in isolation, the suite launches its own runtime child, waits for a real descendant PID file, kills only the runtime parent, observes that the descendant is gone, and reopens the durable state. The lost-response test saves a publication intent around an actual completed ref update and proves that recovery identifies the existing commit rather than creating another.

The boundary test intentionally writes from an internal unconstrained fixture process outside its private clone into its own disposable test project's checkout. This negative control proves that process lifecycle containment alone does not establish a security sandbox. The shipped ManagedCodexHost instead uses the AppContainer boundary. Positive controls prove an ordinary WMI writer can create the disposable file, while the managed command cannot use that route. Other checks prove outside-source writes and a connection to an actual listening socket are denied, child writers stop, and Unicode output survives.

The current 34-check suite also covers machine-wide same-resource waits and independent resources, dead-owner recovery, trusted-command descendants, hardlink rejection, real shared SQLite transactions, occupied preview ports and exact cleanup, broker launch deduplication through restart, policy preservation and custom data-root discovery. Final results are under `artifacts/retirement/tests-final-acceptance`.

`scripts/compare-coordination.ps1` compares matching no-op child operations with manual registry commands, the automated legacy wrapper and runtime-derived admission. It measures coordination overhead only. `scripts/test-retirement.ps1 -Output <fresh-artifact-folder>` copies the still-protected legacy installation into a disposable fixture, verifies refusal while other work exists, then verifies frozen legacy clients. It does not change the real installation. Actual retirement is evidence gated by `scripts/retire-resource-checkin.ps1`; see the receipt before attempting a migration or rollback.

Two Windows persistence regressions are also covered: a concurrent state reader that omits delete sharing must not break an atomic save, and a failed output-persistence callback must stop its producer promptly. The reader regression failed with `UnauthorizedAccessException` before the bounded replace retry was added. The same fix is exercised by the desktop check's continuous state reads.

The supplied edge-case catalogue and task-map images are mapped in [reference-coverage.md](reference-coverage.md). Additional executable tests cover directory replacement versus a conflicting child and an independent prefix sibling, case-variant paths, concurrent equivalent decisions, cleanup beside a live peer, cancellation while a decision is pending, retained decisions after restart, repeated stale revisions reaching their cap, invalid parent/cleanup identities, and binary/Unicode rename publication. The directory test exposed a false conflict in Git 2.30's three-way index merge; the fixed runtime applies an exact candidate delta after checking preconditions. Desktop checks now navigate between a stale parent and completed revision, retain the actual Codex report, and restart without command-line arguments.
