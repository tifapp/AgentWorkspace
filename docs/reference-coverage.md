# Reference coverage: Windows / Codex

The supplied **Agent Workflow Edge Cases.html** is a catalogue of legacy test names and design claims. Its covered labels are not Windows test results. This matrix records what was adapted, exercised, or remains outside this release. ?Exercised adaptation? proves only the stated Windows behavior, not the full legacy contract.

Catalogue: 85 cases; SHA-256 `609cbbb720bd3a717758e69c8e59694a9278a6f8d6f6dc98635fbb5e50980fcf`. Task-map reference SHA-256 `bf7fe884e25557b433ae08289ef9e28412b5634778dc7f8425486c6769bde4c1`.

## Executed evidence

Current retirement release: **34 integration checks passed** in `artifacts/retirement/tests-final-acceptance/results.json`; real managed Codex desktop workflow passed in `artifacts/retirement/desktop-final-acceptance/result.json`. These supersede the initial execution receipts listed below. Added Windows adaptations cover machine admission and owner recovery, command confinement and trusted-child cleanup, occupied ports, a real shared evidence database, hardlink rejection, cross-session broker launches and custom evidence discovery. The original matrix remains a historical mapping; its broader arbitrary-resource claims are not established by these additions.

- `tests/AgentOS.Tests/Program.cs`: 23 executable integration checks. New `Reference` cases cover directory boundaries/case, stop with pending decisions, peer cleanup, revision cap/parent state, and invalid identities. A binary/Unicode rename test protects the revised merge path.
- `artifacts/verification/tests-final-23/results.json`: final runtime results. Earlier directory failures remain in `reference-cases` and `reference-path-fix`; passing correction is in `reference-path-fixed`.
- `scripts/test-desktop.ps1 -Live`: real Codex sessions and UI Automation, including task-thread navigation, retained Codex report, and restart without arguments. Final results and screenshots: `artifacts/verification/desktop-reference/`.
- `docs/validation-report.md`: environment, limitations and source/artifact identity.

## Edge-case mapping

| Case | Scenario | Windows status | Evidence or boundary |
| --- | --- | --- | --- |
| OV-1 | Two agents edit the same file at once | Exercised adaptation | Private writes proceed; same-file publication waits, rechecks, and refuses stale input. Runtime contention test and live desktop walkthrough. |
| OV-2 | Unclaimed edit grants silently | Exercised adaptation | Private writes proceed; same-file publication waits, rechecks, and refuses stale input. Runtime contention test and live desktop walkthrough. |
| OV-3 | Directory claim covers children, not siblings | Exercised adaptation | Reference OV3/OV4 test: directory/file overlap, independent prefix sibling, and case-variant Windows file paths. |
| OV-4 | Case-variant paths | Exercised adaptation | Reference OV3/OV4 test: directory/file overlap, independent prefix sibling, and case-variant Windows file paths. |
| OV-5 | Sensitive path stays denied | Not implemented | No tool-level claims or sensitive-path reference monitor. |
| OV-6 | Node guard and Swift Kit disagree | Not implemented | No tool-level claims or sensitive-path reference monitor. |
| OV-7 | Whole-tree git command with a peer writing | Exercised adaptation | Private source/index test proves separate clones and indexes. No shared-worktree commit shim is shipped. |
| OV-8 | Claim guard cannot read the registry | Exercised adaptation | Corrupt durable state is refused and retained. The catalogue?s fail-open policy is not adopted for publication. |
| OV-9 | Peer touches the file after the first conflict was judged | Not implemented | No tool-level claims or sensitive-path reference monitor. |
| OV-10 | Two agents in a shared worktree commit | Exercised adaptation | Private source/index test proves separate clones and indexes. No shared-worktree commit shim is shipped. |
| OV-11 | Hand-widened claim | Not implemented | No tool-level claims or sensitive-path reference monitor. |
| DP-1 | Force with an assumption | Not implemented | No force/ask/disposition protocol. |
| DP-2 | Force with no bound task | Not implemented | No force/ask/disposition protocol. |
| DP-3 | Force turns out wrong | Exercised adaptation | No force-write operation; stale candidate stays private and captured. Ref is unchanged by rejected integration. |
| DP-4 | Ask leaves the question with the holder | Not implemented | No force/ask/disposition protocol. |
| DP-5 | Review reaches a person | Exercised adaptation | Scoped local release review in the desktop: tested candidate, evidence, destination, consequences and independent work. No generic peer review protocol. |
| DP-6 | Invalid disposition | Not implemented | No force/ask/disposition protocol. |
| DP-7 | Holder leaves before the judgment | Not implemented | No force/ask/disposition protocol. |
| DP-8 | Conflict rejected outright | Exercised adaptation | No force-write operation; stale candidate stays private and captured. Ref is unchanged by rejected integration. |
| DP-9 | Judge the same conflict twice | Exercised adaptation | Scoped authority test and concurrent same-candidate requests deduplicate persisted release decisions. No legacy disposition/check-in API. |
| AW-1 | Await on an action | Exercised adaptation | Publication queue, cancellation and restart tests. Pending release choice does not hold an agent turn or independent work. No generic await API. |
| AW-2 | Await on a peer | Not implemented | No generic await, delivery or expiry scheduler. |
| AW-3 | Await too many | Not implemented | No generic await, delivery or expiry scheduler. |
| AW-4 | Await expires | Not implemented | No generic await, delivery or expiry scheduler. |
| AW-5 | Owner of a required await disappears | Exercised adaptation | Publication queue, cancellation and restart tests. Pending release choice does not hold an agent turn or independent work. No generic await API. |
| AW-6 | Optional await at checkout | Not implemented | No generic await, delivery or expiry scheduler. |
| AW-7 | Required await at checkout | Not implemented | No generic await, delivery or expiry scheduler. |
| AW-8 | Mail while yielded | Not implemented | No generic await, delivery or expiry scheduler. |
| AW-9 | Await across two conditions | Not implemented | No generic await, delivery or expiry scheduler. |
| ST-1 | User presses Stop with debt | Exercised adaptation | Publication queue, cancellation and restart tests. Pending release choice does not hold an agent turn or independent work. No generic await API. |
| ST-2 | Agent ends its own turn with an unpaid judgment | Not implemented | No legacy turn-release debt protocol. |
| ST-3 | Stop gate loops forever | Exercised adaptation | Reference ST3/ST7 test forces three consecutive stale candidates: two revisions then stop; parent outcomes and lineage retained. |
| ST-4 | Stop gate with only an await open | Exercised adaptation | Publication queue, cancellation and restart tests. Pending release choice does not hold an agent turn or independent work. No generic await API. |
| ST-5 | Map-harness task finishes unpaid | Not implemented | No legacy turn-release debt protocol. |
| ST-6 | Owed commitment at release | Not implemented | No legacy turn-release debt protocol. |
| ST-7 | Helper releases while parent owes | Exercised adaptation | Reference ST3/ST7 test forces three consecutive stale candidates: two revisions then stop; parent outcomes and lineage retained. |
| AK-1 | Ask to a task that is not checked in | Exercised adaptation | Reference LF6 test rejects unknown/path-like parent and cleanup identities before creating records or deleting anything. |
| AK-2 | Holder dies mid-request | Not implemented | No inter-agent request/commitment protocol. |
| AK-3 | Requester goes away | Exercised adaptation | Reference ST1/AK3/PR12 test retains a pending release decision after workspace cleanup, peer cancellation and restart. |
| AK-4 | Commitment handed off | Not implemented | No inter-agent request/commitment protocol. |
| AK-5 | Ask past its due time | Not implemented | No inter-agent request/commitment protocol. |
| AK-6 | Uninvolved agent closes the request | Not implemented | No inter-agent request/commitment protocol. |
| AK-7 | Fulfil with no note | Not implemented | No inter-agent request/commitment protocol. |
| AK-8 | Two askers, one holder | Partial | Concurrent equivalent release requests deduplicate; no inter-agent ask/holder protocol or concurrent distinct-request test. |
| PR-1 | pkill of another task's process | Not implemented | No shared process/port/device adapter. |
| PR-2 | Claim below a process id | Not implemented | No shared process/port/device adapter. |
| PR-3 | Recycled pid | Different mechanism | Only live Job Object handles are stopped. No arbitrary/persisted PID cancellation API. Process death and descendant cleanup tested; PID reuse is not separately injected. |
| PR-4 | Cancel targets the app or its ancestors | Different mechanism | Only live Job Object handles are stopped. No arbitrary/persisted PID cancellation API. Process death and descendant cleanup tested; PID reuse is not separately injected. |
| PR-5 | Another user's process | Different mechanism | Only live Job Object handles are stopped. No arbitrary/persisted PID cancellation API. Process death and descendant cleanup tested; PID reuse is not separately injected. |
| PR-6 | Entry with no start time | Different mechanism | Only live Job Object handles are stopped. No arbitrary/persisted PID cancellation API. Process death and descendant cleanup tested; PID reuse is not separately injected. |
| PR-7 | Two agents validate on one build dir | Exercised adaptation | Every validation uses a unique fresh clone; tests record actual combined commit/tree and separate validation logs. External build directories remain outside enforcement. |
| PR-8 | Bundle held by another task | Not implemented | No shared process/port/device adapter. |
| PR-9 | Port claimed or listening | Not implemented | No shared process/port/device adapter. |
| PR-10 | Long-running child | Partial | Cancellation/runtime-death tests stop long-running descendants. Validation has a 15-minute limit; a full 15-minute expiry test was not run. |
| PR-11 | Two agents want the same simulator | Not implemented | No shared process/port/device adapter. |
| PR-12 | Remove another task's worktree | Exercised adaptation | Reference ST1/AK3/PR12 test cleans one owned clone and proves a live peer clone survives. Arbitrary external shell deletion is not mediated. |
| PR-13 | Detached HEAD | Different mechanism | Private clones deliberately use detached immutable bases; the runtime captures a commit and integrates with expected-old ref checks. No remote pushing. |
| PR-14 | Run installer from a feature branch | Excluded by scope | Portable build only; no automatic updater or installer mutation while running. |
| LF-1 | Owner process dies | Exercised adaptation | Exclusive project lock plus runtime crash/recovery tests; process death releases handles. A second runtime is refused, including with another data root. |
| LF-2 | Entry impersonation | Exercised adaptation | Exclusive project lock plus runtime crash/recovery tests; process death releases handles. A second runtime is refused, including with another data root. |
| LF-3 | Remote caller through agentd | Not implemented | No daemon/registry transport protocol. |
| LF-4 | Corrupt registry file | Exercised adaptation | Corrupt durable state is refused and retained. The catalogue?s fail-open policy is not adopted for publication. |
| LF-5 | Retried check-in | Exercised adaptation | Scoped authority test and concurrent same-candidate requests deduplicate persisted release decisions. No legacy disposition/check-in API. |
| LF-6 | Malformed session id | Exercised adaptation | Reference LF6 test rejects unknown/path-like parent and cleanup identities before creating records or deleting anything. |
| LF-7 | Daemon down | Not implemented | No daemon/registry transport protocol. |
| LF-8 | Clock skew on expiry | Different mechanism | No wall-clock owner expiry. The live exclusive handle owns the project. Suspend/resume was not exercised. |
| DL-1 | Broadcast while two tasks are live | Not implemented | No inbox, presence or phone integration. |
| DL-2 | Mail needing acknowledgement | Not implemented | No inbox, presence or phone integration. |
| DL-3 | Wrong session gets mail | Not implemented | No inbox, presence or phone integration. |
| DL-4 | Mac locked or idle while an action waits | Not implemented | No inbox, presence or phone integration. |
| DL-5 | Stale presence sample | Not implemented | No inbox, presence or phone integration. |
| DL-6 | Several actions while away | Not implemented | No inbox, presence or phone integration. |
| DL-7 | Action closed while away | Not implemented | No inbox, presence or phone integration. |
| DL-8 | Action arrives as the user returns | Not implemented | No inbox, presence or phone integration. |
| DL-9 | Phone command unsigned or forged | Not implemented | No inbox, presence or phone integration. |
| DL-10 | Old signed command replayed | Not implemented | No inbox, presence or phone integration. |
| DL-11 | Phone prompt that cannot start | Not implemented | No inbox, presence or phone integration. |
| DL-12 | Phone away from the Mac | Not implemented | No inbox, presence or phone integration. |
| DL-13 | No iCloud account on the phone | Not implemented | No inbox, presence or phone integration. |
| HS-1 | Claude Code, Cursor and off-map speak the same words | Excluded by scope | User explicitly requested Codex as the sole host. No Cursor or Claude integration or parity claim. |
| HS-2 | Old vocabulary | Excluded by scope | New schema-1 Windows runtime; no legacy macOS state import. |
| HS-3 | Needed opportunity | Partial | Actual revision children and their separate outcomes are shown in the task map. General proposed/required follow-up opportunities are not implemented. |
| HS-4 | Worker teaching drift | Different mechanism | The Codex adapter supplies a private-workspace prompt. No legacy claim-language teaching files or protocol. |
| HS-5 | Third-party CLI spike | Different mechanism | This is the supported authenticated Codex integration, using the user?s account and configuration. Tests do not install or update other agent CLIs. |
| HS-6 | Updater with an agent running | Excluded by scope | Portable build only; no automatic updater or installer mutation while running. |

## UI mapping

The **Task Map States (1).html** images were extracted and inspected locally. The WinUI interface adapts the task relationships, outcomes and progressive disclosure; its controls reflect actual runtime capabilities.

| Reference scenes | Implemented WinUI behavior |
| --- | --- |
| `task-map-compact`, `task-map-long-running`, `task-map-live-transcript` | Status, task prompt, current activity, cancellation, retained transcript. Codex only; no model/host switcher or false steer acknowledgement. |
| `task-map-sleeping-open`, `task-map-chips-awaits` | Waiting publication explains its condition, revalidation and independent work. No simulated general await chips. |
| `task-map-result-closed`, `task-map-result-open`, `task-map-failed` | Separate runtime outcome, expandable Codex report, source diff, validation evidence and diagnostics. An agent?s report alone does not mark publication complete. |
| `task-map-opportunity-children`, `task-map-thread-top` | Indented revision children, stable original task titles, parent/child navigation, separate historical outcomes. Proposed follow-ups remain a documented gap. |
| `task-map-check`, `task-map-chips-done-opportunities` | Pending scoped release is visible on its completed task. Pending authority does not relabel completed integration or block other work. |
| `map-notice-failed`, login/help bubbles | Native notices and prerequisite remedies; no dummy capture/phone/notification controls. |
| Long detail and collapsed results | Scrollable detail, expandable full prompt/report/diff; responsive controls at 1280?900 and 900?760. |

Full canvas dragging, live steering, clarification replies, arbitrary task linking, citing by drag-and-drop, capture hotkeys, phone delivery, suggested opportunity intake and required-follow-up aggregation are not implemented. They cannot be inferred from revision nesting.

## Approved platform boundary (2026-10-07)

This program is Windows/Codex only. Phone work is deferred and explicitly excluded: no mobile app, relay, pairing, phone notifications, or phone control is implemented or approved for this release. Future phone work needs a separate decision and acceptance evidence.
