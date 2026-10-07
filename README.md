# Agent OS for Windows

Agent OS is a Windows x64 workspace for authenticated Codex CLI 0.160.0. The WinUI app and .NET 10 runtime run concurrent tasks in private Git clones, validate combined candidates, publish to `agent-os/integrated`, and record decisions and recovery evidence. The supported agent host is Codex. Phone features and other platforms or agent hosts are outside this release.

## Run and build

Open a committed Git project in the app, configure a PowerShell validation command that fails on invalid output, then start Codex tasks. Setup reports prerequisites and execution coverage. Uncommitted checkout changes are not task inputs. Completed means a validated candidate was integrated into `agent-os/integrated`, not your checked-out branch.

The next portable build is side by side at `artifacts/release/agent-os-integrated-win-x64/AgentOS.exe`; its ZIP is `artifacts/agent-os-integrated-win-x64.zip`. The compatible CLI is `artifacts/release/agent-os-integrated-win-x64/cli/AgentOS.Cli.exe`. Extract the whole ZIP before launch. These are outputs of `scripts/build.ps1 -Publish`, not a claim publication already ran. The earlier `artifacts/release/agent-os-retirement-win-x64` installation and receipts are historical and must not be overwritten. Do not use its schema-1 CLI against current schema-3 project state or schema-2 machine journal.

```powershell
.\scripts\bootstrap.ps1
.\scripts\build.ps1 -Test -Publish
& .\artifacts\release\agent-os-integrated-win-x64\cli\AgentOS.Cli.exe doctor
& .\artifacts\release\agent-os-integrated-win-x64\cli\AgentOS.Cli.exe run 'C:\project' '& .\Validate.ps1' 'Describe the task'
```

Requirements: Windows x64 build 19041 or newer, Git for Windows, authenticated Codex CLI 0.160.0, and the pinned .NET 10 SDK for building. Run `codex login` in your terminal. Real tasks consume account usage. The `-Test` suite requires a dedicated artifact-root coordinator and runs from compiled `Tests.exe` or `dotnet run`; DLL-only child invocation breaks fixtures. Native acceptance is separate from AppContainer mediated SDK work.

## Operating workflow

The app offers task history, exact conflicts and retained candidates, a draggable map canvas with dependencies and citations, capture review, scoped interactions, external-effect review, local notifications, tray navigation and a close guard. Map save and selected ready-task start are separate. The configurable global hotkey captures the retained external foreground HWND before Agent OS takes focus; UIA text and screenshot review are bounded. Screenshot transmission to the isolated drafting microagent requires explicit opt-in and crop review. Its title derives from the foreground window, it returns exactly three suggestions and at most eight generated nodes; users can add more than eight manually. Generation fills a draft and never starts tasks.

Accepted context bytes are hashed and immutable; the runtime injects them into untrusted `additionalContext` only for explicitly started work. Interactions include steering, clarifications, peer handoff and acknowledgement, waits, obligations and followup proposals. Active maps permit partial starts of ready nodes; history retains lineage and debt. CLI and desktop share the owning project runtime through a bounded same-user framed broker. See [desktop controls](docs/desktop-interactions.md) and [guarantees](docs/guarantees.md).

Git sources may be main, linked or bare when canonical common/object identity and local pinned modules validate. Network/SMB Git sources require an actual exclusive-lock probe. Native SDK work requires an explicitly configured available Hyper-V profile with a separate nonadministrator worker and exact VM shutdown receipt; there is no host fallback. GitHub, PostgreSQL and deployment adapters require destination-specific configuration, scoped approval and reconciliation. No live external mutation, VM provisioning or signed install is evidenced here.

Production MSIX updates require signing and machine prerequisites. The unsigned portable folder is a diagnostic/local distribution path, not a production signed package. See [signed updates](docs/signed-update.md).

## Evidence and boundaries

Current native App build 73d: zero warnings and errors; only TaskMaps ordering and fixture code changed by 0d, leaving UI source unchanged. UI Automation controls and bounds were checked at wide and narrow preview sizes. Screenshots from this session and retained 3fbb baseline are blank, so visual rendering is unverified. Owned external WinForms capture fixture d014 passed HWND/PID identity, text, exact 100 x 80 crop, and hotkey conflict/cleanup. Authenticated synthetic Codex drafting candidate 772 passed a foreground-derived title, three suggestions and three nodes without sending a real user window or screenshot. Final root-native regression on the exact archived `0d8a7e9bc9ead47b21b3f2495483ee7c0f134613` source passed 71/71 through compiled `Tests.exe`; see `artifacts/integration-final-regression/results.json` and `artifacts/integration-native-acceptance-evidence.json`. The results source field inherited parent checkout 9d; the receipt pins the tested archive and DLL hashes. Historical retirement receipts validate earlier binaries only. Structural source checks are not runtime acceptance.

The resource-checkin retirement archive and receipt are historical; this documentation does not rerun retirement or change global Codex instructions. Supported managed effects do not protect arbitrary unmanaged host programs. See [product objective](docs/product-objective.md), [reference coverage](docs/reference-coverage.md), and [integration status](docs/integration-status.md).
