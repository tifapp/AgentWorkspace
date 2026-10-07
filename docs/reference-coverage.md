# Reference coverage: Windows/Codex

The supplied 85-case Agent Workflow Edge Cases catalogue and Task Map States images are design references. Their labels are not Windows test results. The family table summarizes current source and verification. The original per-case ID/index mapping from 0d8a7e9:docs/reference-coverage.md is retained in the historical design appendix below; those IDs are catalogue labels, not executed Windows tests. Catalogue SHA-256: `609cbbb720bd3a717758e69c8e59694a9278a6f8d6f6dc98635fbb5e50980fcf`. Task-map reference SHA-256: `bf7fe884e25557b433ae08289ef9e28412b5634778dc7f8425486c6769bde4c1`.

| Reference families | Current Windows/Codex adaptation | Verification boundary |
| --- | --- | --- |
| OV, DP: overlapping work and decisions | Private clones, canonical Git profiles, typed file/resource claims, expected-old publication, retained conflict notice/free-form response, exact scoped release decision. Local pinned modules and conditional SMB lock proof. | Earlier real Git/decision receipts are historical. Exact archived 0d8a7e9 native suite passed 71/71; receipt pins tested source and DLL hashes. Unmanaged writers are outside mediation. |
| AW, ST, AK: waits, debt, requests and lifecycle | `InteractiveWork` implements durable scoped steering, clarification, peer request/ack/handoff, cancelable waits, followups and obligations. Map/work lineage and incomplete debt survive restart. Conflict response is owed before publishing a deferred candidate. | No claim of transparent mid-command suspension of arbitrary native processes. Exact archived 0d8a7e9 native suite passed 71/71; design case IDs do not count as executed tests. |
| PR, LF: processes, ports, profiles and recovery | Owned Job Objects and previews; typed claims; machine schema-2 journal; project schema-3 backup/migration; canonical main/linked/bare Git; conditional SMB; Hyper-V SDK/deployment with exact VM receipt/shutdown and no host fallback. | SDK and adapter proof uses injected fixtures. No actual VM provisioned here; `vmcompute` unavailable. Historical retirement process/port tests validate their old binary only. |
| DL: delivery and phone | Local notices, quiet hours, tray navigation and close guard are implemented. | Phone relay, pairing, phone notifications/control and other platforms remain deferred. |
| HS: host and update | Codex CLI 0.160.0 only; compatible App/CLI framed broker; signed MSIX drain/hash/protocol/schema/rollback source. | No other host parity. Production certificate, signed installation and live update unverified. |

## Task-map scenes

The desktop renders a draggable, zoomable canvas with explicit dependency/related/followup links, citations, task status and attempt lineage. Draft edits and explicit graph authorization are separate. A start call launches ready roots and durably queues authorized dependents; an active map can add another dependency-closed subset while preserving earlier pending selections. The task detail retains prompt, transcript, exact conflict, candidate diff and evidence. Scoped inbox and acknowledgement controls expose actual `InteractiveWork` records. Native UIA controls and bounds were verified wide and narrow in preview. Both current-session and retained 3fbb screenshots are blank, so visual rendering remains unverified.

The external WinForms capture fixture d014 verified retained HWND/PID, UIA text, exact 100 x 80 crop and hotkey cleanup. The authenticated synthetic Codex drafting candidate 772 verified foreground title, three suggestions and three nodes without real user-window/screenshot transmission. See [validation report](validation-report.md). Historical reference mappings and retirement receipts do not substitute for final 0d8a7e9 source-exact acceptance.

Phone work is deferred and explicitly excluded; no mobile app, relay, pairing, phone notifications or phone control is in this release.

## Historical design appendix: catalogue case order

This is the original 85-case ID and scenario mapping from 0d8a7e9:docs/reference-coverage.md, in its original row order. The index is the row position in that historical table. Its old Windows status and evidence columns described an earlier release and are not current verification claims. The family table above and the 71/71 source-exact receipt define the current verification boundary. No catalogue ID by itself denotes an executed test.

| Index | Catalogue ID | Historical scenario |
| ---: | --- | --- |
| 1 | OV-1 | Two agents edit the same file at once |
| 2 | OV-2 | Unclaimed edit grants silently |
| 3 | OV-3 | Directory claim covers children, not siblings |
| 4 | OV-4 | Case-variant paths |
| 5 | OV-5 | Sensitive path stays denied |
| 6 | OV-6 | Node guard and Swift Kit disagree |
| 7 | OV-7 | Whole-tree git command with a peer writing |
| 8 | OV-8 | Claim guard cannot read the registry |
| 9 | OV-9 | Peer touches the file after the first conflict was judged |
| 10 | OV-10 | Two agents in a shared worktree commit |
| 11 | OV-11 | Hand-widened claim |
| 12 | DP-1 | Force with an assumption |
| 13 | DP-2 | Force with no bound task |
| 14 | DP-3 | Force turns out wrong |
| 15 | DP-4 | Ask leaves the question with the holder |
| 16 | DP-5 | Review reaches a person |
| 17 | DP-6 | Invalid disposition |
| 18 | DP-7 | Holder leaves before the judgment |
| 19 | DP-8 | Conflict rejected outright |
| 20 | DP-9 | Judge the same conflict twice |
| 21 | AW-1 | Await on an action |
| 22 | AW-2 | Await on a peer |
| 23 | AW-3 | Await too many |
| 24 | AW-4 | Await expires |
| 25 | AW-5 | Owner of a required await disappears |
| 26 | AW-6 | Optional await at checkout |
| 27 | AW-7 | Required await at checkout |
| 28 | AW-8 | Mail while yielded |
| 29 | AW-9 | Await across two conditions |
| 30 | ST-1 | User presses Stop with debt |
| 31 | ST-2 | Agent ends its own turn with an unpaid judgment |
| 32 | ST-3 | Stop gate loops forever |
| 33 | ST-4 | Stop gate with only an await open |
| 34 | ST-5 | Map-harness task finishes unpaid |
| 35 | ST-6 | Owed commitment at release |
| 36 | ST-7 | Helper releases while parent owes |
| 37 | AK-1 | Ask to a task that is not checked in |
| 38 | AK-2 | Holder dies mid-request |
| 39 | AK-3 | Requester goes away |
| 40 | AK-4 | Commitment handed off |
| 41 | AK-5 | Ask past its due time |
| 42 | AK-6 | Uninvolved agent closes the request |
| 43 | AK-7 | Fulfil with no note |
| 44 | AK-8 | Two askers, one holder |
| 45 | PR-1 | pkill of another task's process |
| 46 | PR-2 | Claim below a process id |
| 47 | PR-3 | Recycled pid |
| 48 | PR-4 | Cancel targets the app or its ancestors |
| 49 | PR-5 | Another user's process |
| 50 | PR-6 | Entry with no start time |
| 51 | PR-7 | Two agents validate on one build dir |
| 52 | PR-8 | Bundle held by another task |
| 53 | PR-9 | Port claimed or listening |
| 54 | PR-10 | Long-running child |
| 55 | PR-11 | Two agents want the same simulator |
| 56 | PR-12 | Remove another task's worktree |
| 57 | PR-13 | Detached HEAD |
| 58 | PR-14 | Run installer from a feature branch |
| 59 | LF-1 | Owner process dies |
| 60 | LF-2 | Entry impersonation |
| 61 | LF-3 | Remote caller through agentd |
| 62 | LF-4 | Corrupt registry file |
| 63 | LF-5 | Retried check-in |
| 64 | LF-6 | Malformed session id |
| 65 | LF-7 | Daemon down |
| 66 | LF-8 | Clock skew on expiry |
| 67 | DL-1 | Broadcast while two tasks are live |
| 68 | DL-2 | Mail needing acknowledgement |
| 69 | DL-3 | Wrong session gets mail |
| 70 | DL-4 | Mac locked or idle while an action waits |
| 71 | DL-5 | Stale presence sample |
| 72 | DL-6 | Several actions while away |
| 73 | DL-7 | Action closed while away |
| 74 | DL-8 | Action arrives as the user returns |
| 75 | DL-9 | Phone command unsigned or forged |
| 76 | DL-10 | Old signed command replayed |
| 77 | DL-11 | Phone prompt that cannot start |
| 78 | DL-12 | Phone away from the Mac |
| 79 | DL-13 | No iCloud account on the phone |
| 80 | HS-1 | Claude Code, Cursor and off-map speak the same words |
| 81 | HS-2 | Old vocabulary |
| 82 | HS-3 | Needed opportunity |
| 83 | HS-4 | Worker teaching drift |
| 84 | HS-5 | Third-party CLI spike |
| 85 | HS-6 | Updater with an agent running |
