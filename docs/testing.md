# Testing the integrated Windows source

Use the pinned .NET 10 SDK on a native Windows host for compilation and runtime acceptance. `scripts/build.ps1 -Test -Publish` builds App and CLI, runs the executable integration suite and publishes the integrated side-by-side portable folder/ZIP. It does not install MSIX or alter the historical retirement bundle. `scripts/source-manifest.ps1` hashes nonignored source and records Git HEAD; HEAD alone does not identify uncommitted tested content.

```powershell
.\scripts\build.ps1 -Test -Publish
.\scripts\ValidateCompactUi.ps1
.\scripts\source-manifest.ps1
```

The source validation gate and `ValidateCompactUi.ps1` check structure only. They do not compile, launch, exercise native UI, or prove rendering. Native root verification is separate from AppContainer-mediated commands. The exact archived `0d8a7e9bc9ead47b21b3f2495483ee7c0f134613` source passed 71/71 through compiled `Tests.exe` with the archive as cwd. Results: `artifacts/integration-final-regression/results.json`; source and DLL hashes: `artifacts/integration-native-acceptance-evidence.json`. The results JSON source field inherited parent checkout 9d and does not identify the tested archive.

Tests requiring machine coordination must inject an explicit isolated artifact-root `MachineCoordinator`. Do not use the user's default journal as a fixture: the schema-2 upgrade and active owners can conflict with an installed schema-1 CLI. Run the suite with compiled `Tests.exe` or `dotnet run --project tests/AgentOS.Tests -c Release -- <artifact-root>`. DLL-only invocation is invalid for child-process fixtures. Keep each result JSON with its source manifest and exact binary identity.

The suite exercises real Git repositories, PowerShell and process trees, plus injected SDK/external adapter fixtures. Passing injected tests does not prove a real Hyper-V VM, live GitHub, PostgreSQL or deployment effect. This host has no `vmcompute` or signing certificate. Native SDK commands in product require an available frozen Hyper-V profile, nonadministrator worker, exact collector receipt and shutdown proof; no host fallback exists.

For desktop controls, native App build 73d completed with zero warnings and errors. UI Automation located controls and checked bounds at wide and narrow preview sizes. Both current-session and retained 3fbb baseline screenshots are blank; do not report visual rendering as passed. The owned external WinForms fixture at `artifacts/capture-fixture/evidence.json` verified HWND/PID identity, UIA text, exact 100 x 80 crop and hotkey conflict/cleanup. Authenticated synthetic Codex drafting evidence at `artifacts/context-live/candidate-evidence.json` verified a foreground-derived title, three suggestions and three nodes, without transmitting a real user window or screenshot.

Historical retirement integration, desktop, comparison, migration and receipt artifacts remain useful only for the binaries they identify. Do not rerun retirement or an installer to test this source. See [validation report](validation-report.md) and [guarantees](guarantees.md).
