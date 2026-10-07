# Windows validation report

## Current retirement release — 2026-10-07

The final runtime is Core SHA-256 `FAD953C3941E958A51156756674402D02C1440F9AAC62A5165246631FC560FE7`. Desktop, CLI, test and Core build copies have the same hash. Environment: Windows 10 x64 build 19045; SDK 10.0.401/runtime 10.0.12; Windows App SDK 2.5.1; Git 2.30.1.windows.1; authenticated Codex CLI 0.160.0 with actual live model `gpt-6-sol`.

| Check | Result | Evidence under `artifacts/retirement/` |
| --- | --- | --- |
| Runtime integration | **34/34 passed** | `tests-final-acceptance/results.json` |
| Final packaged desktop | Passed setup, three real Codex tasks, conflict/stale refusal, independent integration, successful revision, exact tag approval, cancellation and restart | `desktop-final-acceptance/result.json`, screenshots 01–09 |
| Cross-session launch and retry | Passed current-user broker launch, policy preservation, retry deduplication through restart and custom data-root discovery | Final integration suite |
| Windows enforcement | Outside-source write and actual socket connection denied; WMI positive control succeeds outside sandbox and fails inside; owned descendants stopped; hardlinks rejected | Final integration suite and retained WMI evidence |
| Ports and shared database | Real occupied loopback port preserved, owned listener closed; real concurrent SQLite evidence writes deduplicated | Final integration suite |
| Baseline comparison | Passed preregistered median coordination gate: managed 136.47 ms, automated wrapper 222.09 ms, manual 312.54 ms over 11 repetitions | `comparison-final-acceptance/result.json` |
| Migration | Other legacy work preserved/refused; archived client frozen; installed client rejects without modifying registry | `migration-fixture/result.json`, `cutover-verification.json`, `retirement-receipt.json` |
| Post-cutover real Codex task | Completed and integrated using packaged CLI and isolated host without legacy maintenance | `post-cutover/work.json`, `post-cutover/result.json` |
| Portable package | Included self-contained desktop and CLI; ZIP integrity, extracted launch and exact file hashes recorded | `final-summary.json` |

`source-manifest.json` identifies the complete current nonignored source, including uncommitted files. Each actual validation records source/tree/base, command, model, runtime hash, exact environment fingerprint and output-log hash. Retained failures document the fixed missing SystemDrive environment, transient private Git reflog sharing violation and earlier sandbox/native Git experiments.

The old skill directory cannot be moved because host-managed permissions deny deletion. Its files remain discoverable. Retirement is operational: global instructions replaced, schema 99 freezes execution, and an archive copy/receipt retained. General Windows mediation, arbitrary native SDKs (`dotnet test` included), external services, other accounts, native arbitrary-shell continuation and whole-task performance superiority are not established. A tiny profile-creation crash window may leave an unused sandbox profile. See [guarantees](guarantees.md) and [retirement](retirement.md).

## Historical initial release

The following records describe the initial release, completed on 2026-10-06 Pacific time (2026-10-07 UTC). Its unconfined Codex execution adapter has been replaced by the managed host above; the earlier 23-check receipts do not identify the current release.

## Delivered workflow

The repository now contains a native WinUI 3 desktop application, a C# runtime and a diagnostic CLI. Codex is the only agent host. Setup opens a committed local Git project, checks prerequisites, records its validation command and explains boundary coverage. Tasks use ordinary Codex file, shell, test and local Git actions in private clones. The runtime captures candidates, validates combined trees, coordinates publication to `agent-os/integrated`, and returns stale work for Codex revision. The app shows real progress, diffs, validation, failures, scoped local release decisions, cancellation and retained results.

The portable executable is `artifacts/release/agent-os-win-x64/AgentOS.exe`; the complete folder is distributed in `artifacts/agent-os-win-x64.zip`. Setup and build instructions are in the repository README and included in the release.

## Environment and code identity

| Component | Tested version |
| --- | --- |
| Windows | Windows 10 x64, build 19045 |
| .NET SDK / runtime | 10.0.401 / 10.0.12 |
| Windows App SDK | 2.5.1, self-contained deployment |
| Git | 2.30.1.windows.1 |
| Codex | Authenticated CLI 0.160.0, user's configured model/account |

`artifacts/verification/source-manifest.json` records exact hashes of the implementation and build/test inputs, including uncommitted files. The original Git HEAD by itself does **not** identify the implementation. `artifacts/verification/final-summary.json` records final binaries, archive, source digest and check results.

Each work unit also records its base/candidate/integrated commit identities. Each validation records the combined tree, command, observed environment versions, environment fingerprint, time, exit code, source-cleanliness check and log hash.

## Checks performed

| Check | Result | Retained evidence |
| --- | --- | --- |
| Release build and portable publication | Passed; generated XAML and resource index included and compared with build output | `artifacts/publish.log`, final build log |
| Runtime integration suite | **23/23 passed**, including actual Git contention, directory boundaries, binary/Unicode changes and Windows process trees | `artifacts/verification/tests-final-23/results.json` |
| Real Codex runtime walkthrough | Passed: conflicting mutations, independent work, stale revision while a decision is pending | `artifacts/diagnostic-live.log`, `artifacts/diagnostic-state/` |
| Packaged desktop workflow | Passed on the final app/runtime: setup, three concurrent Codex work units, stale refusal, revision navigation, actual Codex reports, scoped approval, actual tag, cancellation and restart without arguments | `artifacts/verification/desktop-reference/result.json` |
| Final shared contents | Verified retries = 2, cancellation = true, and the independent README validation section | Same desktop report; actual Git project retained below |
| Visual inspection | Rendered setup, task threads, decisions and evidence at 1280×900 and 900×760; verified full multiline candidate/diff fields | Final desktop screenshots `01` through `09`, including `03a-task-thread.png`; prior multiline check in `desktop-final` |

The final desktop practice project is `artifacts/verification/desktop-reference/projects/coordination-20261006-231859-3f55e0`. Its final shared commit is `565d2006918e82e3ee1d7732f05a669f33e9bb14`. The approved historical release candidate is `7b552da4a75b1ae486247a2b0b6c52a09cea1f57`; its decision did not silently expand to the later revision. The original `main` checkout remained clean. Prior successful live runs remain under `desktop-final` and `diagnostic-state`.

The 23 integration checks cover private clones/indexes; same-file contention; combined validation of independent changes; failed tests; changed validation inputs; grant scope, rejection and deduplication; queued cancellation; child cleanup; unknown host completion; exclusive runtime ownership; runtime death and surviving children; lost publication acknowledgements; changed evidence; demonstrated shell bypass; automatic stale revision; external ref races; Unicode; corrupt state; concurrent state readers; failed output persistence; directory/file and case-variant conflicts; prefix siblings; pending decisions across stop, cleanup and restart; concurrent equivalent requests; the two-revision limit; invalid identities; and binary/Unicode rename preservation. Several checks assert multiple related conditions.

All 85 supplied edge cases and the task-map scenes are accounted for in [reference-coverage.md](reference-coverage.md), with explicit distinctions between exercised Windows adaptations, different mechanisms and missing capabilities. The legacy reference's covered labels were not reused as proof.

The final desktop and integration suite loaded the same runtime SHA-256: `4BC3A5B9507C614FEDF93A83C235A660BCD9755882CDBA329023DB9B47240DA6`. The tested WinUI assembly SHA-256 is `60CD74678FA6F6EBF4A6EE50F5A32AF8ACE7DE2218EB4421E6C58CDBD28E3CAE`. The release package is checked against these identities after publication.

## Failures found and fixed

- Portable publication initially omitted the app's compiled XAML and resource index. The project now explicitly publishes both, and packaging checks their hashes against build output.
- A relative data-root path produced invalid private workspace paths. Runtime storage roots are now absolute.
- A concurrent Windows state reader could make atomic replacement throw `UnauthorizedAccessException`. This was reproduced by a failing regression test, fixed with bounded retries, and verified both in the suite and under the desktop test's continuous reads.
- A failed progress-persistence callback could strand a live output producer. The process adapter now stops its owned job and drains readers before returning the failure. The regression test confirms prompt termination.
- WinUI single-line initialization truncated read-only multiline fields. The fields now enable multiline input before assigning normalized text; the actual candidate and destination were read back through UI Automation.
- The desktop test's restart step initially queried a zero window handle and then checked before project initialization finished. The harness now waits for the loaded workspace and a displayed completed decision. Restart verification resumed against the already completed live project, without repeating model work. The original workflow build hashes are in `workflow-build.json`; the final UI hashes and recovered state are in `result.json`. The runtime binary is the same across that final UI correction.
- The reference's directory-boundary case exposed false conflicts in Git 2.30's three-way index merge, including rejection of an independent prefix sibling. After touched-path preconditions pass, the runtime now applies the exact candidate delta to a private index of the latest tree. The directory, contention, binary and Unicode checks pass with the corrected path.
- Restart previously remembered only the project and could reopen a different history when a custom data root was used. It now retains both. The final live desktop run restarts without arguments and verifies the displayed decision and saved cancellation.

The earlier resumed desktop run described above is retained as development evidence. The final `desktop-reference` run completed the entire workflow in one pass on the final runtime/UI assemblies.

Text contrast was measured for the main colors: muted text/background 5.65:1, white/accent 6.20:1, and stale-state text/white 6.30:1. Native controls expose accessible names and automation patterns. A full screen-reader, high-contrast and multi-monitor DPI audit was not performed.

## Established enforcement and remaining limits

Within the runtime's own publication path, stale source preconditions, failed or changed validation, canceled queued work, and changed Git refs prevented publication. A release tag was absent before approval and matched the exact scoped candidate afterward. Ordinary owned descendants stopped on cancellation and runtime death. Known successful publication was recovered without a duplicate effect.

This does **not** establish a complete reference monitor. A test explicitly proved that an arbitrary same-user shell can write outside a private clone. Filesystem aliases/hard links, external processes/services, credentials, network, ports, databases and remote publication are not comprehensively mediated. Codex's own Windows sandbox and configured external tools are not independently certified by this product.

Parking applies to candidate publication; there is no native Codex tool-call suspension/wakeup scheduler. The authority example is a real local release tag, not a remote deployment. See `docs/guarantees.md` for the supported surfaces, read consistency, recovery rules and unsupported configurations.

No clean Windows VM, Windows 11, ARM64, signed installer, Store distribution or hostile-host confinement test was available in this run. The portable app was launched and exercised on the stated Windows machine. These are explicit validation limits, not passing guarantees.
