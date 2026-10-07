# Validation report â€” integrated Windows source 0d8a7e9

## Current executed evidence

| Check | Result | Evidence |
| --- | --- | --- |
| Native App build 73d | Zero warnings and errors. | Root native build output for that candidate; this is compilation, not an installed package or visual pass. |
| Preview accessibility | UI Automation controls and bounds verified at wide and narrow sizes. | Current preview checks. Screenshots from current session and retained 3fbb baseline are blank; rendering remains unverified. |
| Owned external capture fixture d014 | HWND/PID identity, visible text, exact 100 x 80 crop, hotkey conflict and cleanup passed. | `artifacts/capture-fixture/evidence.json`. This uses an owned WinForms window, not arbitrary real user content. |
| Authenticated synthetic drafting candidate 772 | Foreground-derived title, exactly three suggestions, three generated nodes passed. | `artifacts/context-live/candidate-evidence.json`. No real user window or screenshot was sent; auth helper was restored. |
| Final integrated native regression | **71/71 passed** using compiled `Tests.exe` with exact archived source `0d8a7e9bc9ead47b21b3f2495483ee7c0f134613` as cwd. | `artifacts/integration-final-regression/results.json` and `artifacts/integration-native-acceptance-evidence.json`. |
| Structural source gates | `scripts/ValidateCompactUi.ps1` and source gate are structural checks. | They do not establish compilation or runtime behavior. |

The 71-check suite includes actual process crash/cleanup, typed admission, linked/bare Git and private submodule candidate imports, schema-1/2-to-3 migration with history backups and citations, immutable captured bytes, DAG partial start, injected SDK receipt/recovery, fake external adapters, notification schema-3/large-state behavior, AppContainer boundary with WMI/socket refusal, binary rename preservation and conflict UI protocol. SDK/adapter proof tests inject fixtures. Only TaskMaps ordering and test fixture code changed between App build 73d and 0d8a7e9; UI source was unchanged. No real VM was provisioned: `vmcompute` is unavailable. No live SMB source, GitHub, PostgreSQL, deployment, remote receipt, signing, MSIX installation or production update ran here. APIs for these paths are implemented and require the configuration in [guarantees](guarantees.md) and [signed update](signed-update.md).

## Source and state identity

The current integrated source is `0d8a7e9bc9ead47b21b3f2495483ee7c0f134613`. The final receipt pins the exact archived source and DLL hashes. The results JSON source field inherited parent checkout 9d; it does not identify the tested source. The tests build had zero errors and one fixture-only DbFake SourceColumn nullable warning. Exact-0d CLI build had zero warnings/errors; native PowerShell helper checks had zero parser errors and worker assignment 1. Project state is schema 3, with prior-schema backup/migration/history. Machine journal is schema 2, upgraded only through the guarded offline path. The installed historical schema-1 CLI is incompatible; use the compatible integrated CLI at `artifacts/release/agent-os-integrated-win-x64/cli/AgentOS.Cli.exe` after the side-by-side build. `scripts/build.ps1 -Publish` writes `artifacts/release/agent-os-integrated-win-x64` and `artifacts/agent-os-integrated-win-x64.zip`, leaving `artifacts/release/agent-os-retirement-win-x64` in place.

The old retirement release reports 34/34 integration checks, a real Codex desktop workflow, comparison, migration and receipt evidence under `artifacts/retirement`. Its binary SHA and old schema identify a historical release. The 3fbb archive's 46/46 and later intermediate 54/58 are also historical; neither substitutes for the final source-exact 71/71 run. The retirement archive/receipt remain historical; this report makes no retirement, installer, or global-instruction change.

## Interpretation

Current source implements Windows/Codex capture, maps, interactions, Git profiles, SDK isolation path, external effect adapters, notifications and signed update protocol. The table reports what was actually run. It does not claim arbitrary unmanaged host programs are protected, production signing succeeded, live external effects occurred, visual rendering passed, or phone support exists.
