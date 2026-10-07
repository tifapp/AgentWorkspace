# Windows/Codex integration status, 2026-10-07 (historical source snapshot)

This table records an earlier source snapshot. Current desktop controls and their verification limits are documented in [desktop interaction controls](desktop-interactions.md). This source revision is a partial implementation of the approved program. Earlier retirement validation receipts describe an older binary. No .NET SDK or native execution was available through the managed workspace, so the new source has not been compiled, launched, or exercised against live Codex, GitHub, PostgreSQL, or deployment targets.

| Capability | Source state | Required verification or work |
| --- | --- | --- |
| Durable draft maps | Schema-2 maps separate from work units, schema-1 backup before migration, typed dependency/related/followup edges, cycle refusal, citation hash records, explicit selected-task start, and status recovery are in source. | Compile and run the new integration tests, reopen legacy state, and inspect migration backup. |
| Compact desktop review | The existing compact task list remains. A Maps dialog provides editable JSON and explicit save/start. | Desktop UI automation and accessible review. The requested canvas is still absent. |
| CLI shared authority | Map save/list/start use the existing same-user project pipe when the desktop owns the project, with local runtime fallback. | Compile and run named-pipe integration test. Add full inspect/reply/cancel/wait parity. |
| Foreground capture and microagent | Not implemented. | Native hotkey, external HWND retention, screenshot, accessibility text, redaction preview, isolated Codex generation, timeout and stale-result handling. |
| Steering and waits | Not implemented beyond existing publication waits and follow-up work units. | Durable turn delivery/ack, clarification and peer obligations, deadlines, cancellation, cycle/owner recovery. |
| Resource identities and external effects | Existing managed AppContainer/Git publication scope only. | Typed file/process/port/database/external identities and effect adapters with reconciliation. |
| VM, notifications, tray, signed updater | Not implemented. | Configured Windows backend and signing credentials, plus host validation. |
| Phone features | Explicitly excluded. | Separate future decision. |

`scripts/ValidateCompactUi.ps1` passes source structure checks on this revision. Those checks do not compile or run the native application. The new C# integration tests are present but unrun. Use the earlier release evidence only for the earlier release, not for these source changes.

