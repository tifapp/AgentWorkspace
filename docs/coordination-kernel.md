# Coordination kernel: approved end state and acceptance plan

**Status:** approved design, unimplemented. Baseline main eae2297, 2026-10-07. The requester integrates validated commits into original main. No replacement, migration, installation, or acceptance test is claimed here. See [current guarantees](guarantees.md), [reference coverage](reference-coverage.md), and [knowledge decision](../knowledge/coordination-kernel.md).

## Product and authority boundary

Agent OS becomes a general per-user Windows task and coordination runtime independent of Git. Any task may exist without repository, project, folder, command, or agent session. An Action is a durable obligation; separate attempts are bounded executions by one or more participants. Attempt failure does not close Action. Users manage intentions/outcomes; agents work; runtime mediates authority/coordination. Task tracking, command execution, and effect mediation have distinct APIs and UI states. Context links are optional. Git is an optional workspace strategy for isolation/diff/validation/integration, never prerequisite, authority, or silent checkout capture. Other adapters cover confined processes, files, local services/ports, validation, private Git/publication refs, and shared SQLite fixtures. Undelegated network/credential/external effects are rejected. Keep current confinement until equivalent evidence exists; native tools never bypass it.

Use a modular monolith: one small per-user authority service owns all writes and short transactions in one trusted DB; separate workers run commands, delivery, exports, previews. Desktop closure need not terminate service. No arbitrary plugin platform or distributed microservices. CLI, desktop, scheduler, and authenticated host tools share one versioned boundary.

## Exact public projection

```typescript
type Resource = `file:${string}` | `id:${string}`;
type Address = string;
type Registry = {version:1;generation:number;entries:Entry[];messages:Message[];actions:Action[]};
type Entry = {id:string;holds:Record<Resource,'read'|'write'>;seenAt:string};
type Message = {id:string;from:Address;to:Address;text:string;replyTo?:string;createdAt:string};
type Action = {id:string;owner?:Address;text:string;state:'open'|'closed';createdAt:string};
```

A minimal populated version 1 projection (the export is read-only and nonauthoritative):

```kernel-projection-example
{
  "version": 1,
  "generation": 1,
  "entries": [
    {
      "id": "11111111-1111-4111-8111-111111111111",
      "holds": {"file:C:/work/note.txt": "write"},
      "seenAt": "2026-10-07T12:00:00Z"
    }
  ],
  "messages": [
    {
      "id": "22222222-2222-4222-8222-222222222222",
      "from": "11111111-1111-4111-8111-111111111111",
      "to": "group:33333333-3333-4333-8333-333333333333",
      "text": "Ready?",
      "createdAt": "2026-10-07T12:00:01Z"
    }
  ],
  "actions": [
    {
      "id": "44444444-4444-4444-8444-444444444444",
      "owner": "11111111-1111-4111-8111-111111111111",
      "text": "Review note",
      "state": "open",
      "createdAt": "2026-10-07T12:00:02Z"
    }
  ]
}
```
read/write and open/closed are literal values. IDs are UUIDs; times UTC RFC3339. Each restart creates a new incarnation. Generation increments exactly once per committed transaction changing public projection; private-only commits do not. Holds are currently admitted ownership only, never waiting/future claims. Owner is assignee. Messages/replies are immutable separate objects. Assignment/reassignment requires explicit authority. Closure atomically stores succeeded/failed/canceled/dropped, explanation/artifacts outside projection. Addresses are user, system, bare UUID participant IDs, group:<id>, agents and * as separate addresses. Stable IDs/groups live in identity. Group/broadcast recipients are send-time immutable snapshots. Typed records keyed by object ID carry attachments, structured replies, dependencies, provenance, control; text alone is never control. Postcommit temp replacement exports read-only JSON; stale/missing export is nonauthoritative.


## Trusted store, schema, and atomic operations

Use one SQLite DB under a **new per-user machine coordination directory distinct from existing stores**. Freeze path and DDL before coding. Require WAL, FKs, bounded busy timeout, durable explicit transactions, optimistic revisions, unique content-bound idempotency, conditional assignment. Unsupported versions/corruption fail closed with actionable diagnostics, no reset/replay. Reject old stores, explicitly select fresh workspace, preserve old files, no import/dual-write.

| Logical table/key | Mandatory fields and constraints |
| --- | --- |
| meta/key | schema version, generation, incarnation, settings |
| identities/address; groups/UUID; group_members/pair | stable principals, membership revision |
| participants/UUID; sessions/UUID | identity, host/thread, incarnation, token digest, PID+creation, heartbeat/suspicion/lifecycle |
| contexts/UUID; projects/UUID | optional Action/project/folder/repo links, canonical identity |
| actions/UUID | requester, intended/current assignee, text/state/revision, lineage/deadline/outcome FK |
| action_links/pair; obligations/UUID | dependency/related/followup, required flag, assignee, debt/state |
| attempts/UUID; attempt_participants/pair | Action FK, ordinal, participants, profile, state/times/unknown/artifacts |
| messages/UUID; requests/UUID; replies/UUID | immutable envelope/replyTo, shape/authorized responders, validated payload |
| recipient_snapshots/pair; deliveries/UUID; outbox/UUID | send-time membership, ack/retry/deadline/deadletter/offset/intent |
| resources/key; admission_queue/UUID; holds/pair | canonical identity, FIFO sequence/bundle/mode, live owner |
| waits/UUID; wait_terms/pair | any/all, required/optional, condition/reference, deadline/wake revision/cancel |
| intents/UUID; approvals/UUID; conflicts/UUID | delegation/scope/preconditions, actor/kind/resource/candidate/destination/evidence digest/use |
| events/UUID; outcomes/UUID; receipts/UUID; artifacts/UUID | append-only causation, validation, terminal/external evidence, digest/retention |
| idempotency/caller+key | operation kind, content digest, result/revision; changed content rejected |
| export_jobs/UUID | projected generation/digest/error; no authority |

FKs/uniqueness prevent duplicate outcomes, accepted replies, effects, incompatible active holds. Authenticate, authorize, check revision/preconditions, bind idempotency in one transaction. Commit message+outbox, Action+outcome, approval consumption+intent+events, admission bundle, wait transition atomically. Workers consume committed intents. Crash before commit leaves no mutation; after commit may retry delivery without exactly-once external claim. Lost external responses become unknown until evidence reconciliation. Consistent SQLite backups need schema/hash and artifact manifests. Define retention times, protected evidence, deletion criteria before cutover; missing evidence invalidates bound approvals.

## Module ownership and service operations

| Owner | Records | Versioned service/CLI operations |
| --- | --- | --- |
| Identity/runtime | stable IDs/groups, incarnations, authenticated sessions, PID+creation/heartbeat/tokens, host/thread/projects/execution | identity inspect; session register/heartbeat/end; context inspect |
| Messaging | immutable send/reply, text/choice/number/time/reference request shapes, recipient snapshot, ack/retry/expiry/deadletter/offset | message send/list/ack; request create/reply/inspect; inbox |
| Scheduler | admission, prerequisites, any/all waits, yield, deadlines/reminders, cancel/recovery | resource request/release/inspect; wait create/cancel/inspect; action create/assign/claim/release/reassign/close |
| Policy | intent/delegation, force/ask/review judgments, exact scope/preconditions, approvals/reconcile | delegate; effect prepare/approve/reconcile; conflict respond/resolve/abandon/escalate |
| Evidence | append-only events/outcomes/receipts/causation/validation/artifacts | outcome; event/receipt/artifact inspect; export |
| Presentation | labels/appearance/relationships/progress/status/rendering | task/map/inbox views; all mutations via service |

Every client has same authentication, revisions, idempotency. Host tools cannot impersonate peers, fabricate holds/requests, or control another participant process. Incompatible API versions fail closed. Reads expose revision and export freshness; mutations return committed revision and receipt/unknown handle. Errors distinguish denied, stale, waiting, unknown, corrupt.


## Invariants and recovery

1. Action existence needs no context. Execution failure never closes Action. Authorized closure records terminal outcome/explanation and satisfies required debt or explicitly records canceled/dropped debt.
2. Canonical Windows identities, aliases, and directory bounds determine conflicts. Compatible readers coexist; exclusive writers block conflicts. Competing scopes are FIFO; independent scopes progress; bundles admit all-or-none. Queued claims are never projected holds.
3. Resource release does not imply prerequisite success. Durable waits observe resource, Action outcome, reply, lifecycle, deadline transactionally. Wake revalidates owner, candidate, evidence, delegation, authorization.
4. Heartbeat miss is suspicion only. Never release while writer/descendant may live. Authenticate lifecycle and verify PID+creation. Unknown liveness blocks conflict.
5. Requests validate responder identity and shape. Duplicate sends/replies are content-idempotent or rejected. Disconnect, expiry, ack retry, deadletter, restart, offsets preserve truth; no exactly-once claim.
6. Required followups/commitments are debt; cancel records outstanding debt. Any/all transitions atomic. Cycle detection cancels pending admission only when proven safe; otherwise report and preserve holds. Never automatically choose newest victim. Long waits visible; routine contention needs no human question.
7. Force requires authority, assumption, current preconditions; it cannot override incompatible live writer. Ask routes to holder; review to person. Approval binds actor/kind/canonical resource/candidate+destination/evidence digest/precondition revision. Changed scope needs new approval; consume once atomically unless explicit reuse policy.
8. Crash/lost external response yields unknown; reconcile evidence before retry, never blind replay. Preserve private workspaces, validation, expected-old Git publication, bounded revision, owned cancellation, reconciliation.
9. Export nonauthoritative; corruption/version mismatch fail closed. Append-only causation and consistent backups/artifacts required.

## Baseline field and concept mapping

| Current field/concept | Owner/API | Required implementation and executed evidence |
| --- | --- | --- |
| ProjectState schema 3; machine journal schema 2 | Store/inspect | new DB, old stores preserved/rejected; archive/refusal tests |
| WorkUnit Id/Task/Title/Status/Detail | Attempt identity linked to distinct Action.Id; presentation/attempt inspect and action inspect | preserve WorkUnit.Id as execution identity; separate durable obligation/display and explicit outcome; non-Git restart slice |
| WorkUnit Workspace/BaseCommit/CandidateCommit/IntegratedCommit/PendingCommit/ChangedPaths/Diff | Attempt+Git/attempt inspect | optional workspace/candidate refs, retained diff, expected-old publish/recovery |
| WorkUnit ThreadId/HostVersion/HostModel/ExecutionProfile/CreatedAt/UpdatedAt | Identity+attempt/session inspect | incarnation/host/profile provenance, lifecycle checks |
| WorkUnit Evidence/ContextRefs/ModulePins/PublishedPathObjects | Evidence+context/artifact inspect | hashes/provenance/optional context/modules/publication receipts |
| TaskMap edges/citations/selected/pending | Action links+presentation/map | dependency/related/followup, explicit starts, graph/UI regression |
| InteractiveWork messages/waits/obligations | Messaging+scheduler/request/wait/inbox | typed durable reply/debt/handoff/continuation |
| ConflictNotice/HumanDecision | Policy+evidence/conflict/effect | owed response, exact cause/candidate/decision scope, retained deferred ref |
| MachineCoordinator claims/process owner | Scheduler+identity/resource/session | canonical scope/FIFO bundles/PID+creation/descendants |
| PrivateGit/validation/release | Git adapter+evidence/effect | clone/modules/validation/expected-old publication |
| AppContainer/Hyper-V/external effects | Execution adapters+policy/effect | confinement/receipts/shutdown/scoped review; fixtures and separate live gates |
| Capture/context drafting | Presentation+evidence/task/context | opt-in crop/hashes/untrusted input/explicit Start task |
| Desktop compact UI/transcript/diff/maps/citations/inbox | Presentation/views | generic tasks and actual inbox/outcomes/waits/deps/delivery/recovery; UI proof |
| Notices/tray/close guard/update | Delivery+presentation+lifecycle | service survives desktop close; notice/update/rollback checks |

WorkUnit.Id remains an execution identity linked to distinct Action.Id; WorkUnit becomes an execution record linked to Action; release decisions become policy tied to messages; history becomes evidence. Old files are archived, never imported. Historical receipts establish only old behavior.


## Vertical delivery stages and gates

Each stage must be usable and independently reviewable. Keep existing runtime authoritative until the final cutover; use isolated replacement stores/workspaces during development. No live dual authority.

| Stage | Deliverable | Completion gate |
| --- | --- | --- |
| 0. Handoff/reread/baseline | Record eae2297 source, installed/old state, current suite, fixture and historical evidence; classify each guarantee | Reproducible manifest, baseline tests and gaps; no implied new acceptance |
| 1. Generic service slice | New store/service; non-Git Action create/assign; attempt; typed question/reply; terminal outcome/evidence; restart | Same revision/auth/idempotency path from CLI and host; restart, crash and no-Git scenario passes |
| 2. Git preservation | Existing private workspace, validation, release decisions, expected-old publication and recovery through service | Full current Git release/conflict/cleanup/candidate regressions pass, no lost feature |
| 3. Coordination completion | Messaging, obligations, scheduler, admission, force/ask/review, approvals, reconciliation | Race/retry/cycle/crash/auth/unknown matrix below passes |
| 4. Adapters and desktop | File/process/port/local-service/shared SQLite adapters and generic task UI; actual inbox/outcomes/waits/deps/delivery/recovery | Two-context workflows, compact UI/capture/conflict continuation/maps/citations/diff/transcript/evidence checks pass |
| 5. Documentation/evidence | DDL/API/commands, field and 85-case mapping, manifests, backup/retention, fixture/live boundaries | Schema/command/mapping/link validation and source-exact receipts |
| 6. Global cutover | Separate installation rehearsal, authority transition, retirement/rollback readiness | Cutover gate below; no active legacy owner or dual authority |

## Verification matrix

Run the **current suite first**, recording exact source/binaries, commands, results, and environment. Then execute these new tests against the replacement binary, distinguishing scripted fixtures from real Codex and live external/VM/SMB evidence:

| Area | Required cases and observed evidence |
| --- | --- |
| Admission | assignment races; readers/writer; all-or-none bundles; directory child/sibling; Windows aliases/case; FIFO competing and independent progress; held vs queued projection |
| Messaging | typed shape/auth; duplicate send/reply; group/broadcast snapshot; disconnected recipient; ack/retry/expiry/deadletter/offset/restart |
| Scheduler | dependencies with every terminal outcome; reassignment; required followups/debt; any/all; cancel; cycle safe/unsafe; stale wake; prolonged wait |
| Policy | exact approval binding; forged/replayed response; changed scope/evidence; duplicate or lost response; force assumption/preconditions/live writer |
| Transactions | crash before/after commit, outbox, approval, external mutation, export; content-bound idempotency and optimistic revision |
| Lifecycle/store | PID reuse, live descendants, heartbeat suspicion, clock skew, unsupported version/corruption, stale/missing export, unknown outcome, consistent backup/artifact loss |
| Preservation | candidate, release, bounded revision, validation, owned cleanup, compact UI, capture, conflict response/continuation, maps/citations/transcript/evidence |
| Old state/cutover | old-format preservation/rejection, active legacy refusal, retirement, transition lock, rollback without simultaneous authorities |

Run real Codex workflows in **two contexts**, including non-Git conflicting and independent work, and exercise actual desktop requests/waits/outcomes/recovery. Compare baseline and replacement effort, interruptions, correctness, and elapsed time with method and sample sizes. Fixture-only external, VM, and SMB guarantees remain **unproven live** until separate environment-specific runs. Existing conflict continuity has 23 scenarios passed across runs plus five related regressions using scripted transport; that does not prove live host transport. UI revision f0f467e is historical. Title revision eae2297 had structural checks only; C# suite unrun at that revision. Historical retirement receipt applies only to earlier runtime.

## Final cutover and rollback gate

Cutover is separate from feature acceptance. Rehearse isolated replacement/baseline and installation before changing installed state. Active legacy tasks must finish or explicitly relinquish; timeout alone is insufficient. Stop legacy admission, take transition lock, verify no remaining writers, archive old registry/skills/instructions/binaries preserving bytes and manifests, then activate fresh new authority. Global instructions must cover every supported context exactly and explicitly say how coordination works outside their scope. Never run two live authorities.

Rollback first stops new admission/authority, resolves live writers and unknown external outcomes, then restores compatible archived old authority only if its preconditions hold. Never simultaneously reactivate old and new. Prior retirement receipt covers only prior runtime. Excluded: macOS, phone, other hosts, and unseen v14/v15 wire compatibility.



## 85-case reference catalogue mapping

These design case IDs from [reference coverage](reference-coverage.md) are not executed replacement tests. Each row maps the supplied scenario to owner, service API and target assertion. N means no new-kernel executed evidence. Stages 3-5 require a source-exact receipt per applicable row; excluded cases need a Windows analogue or explicit out-of-scope result, never a pass.

| ID | Supplied scenario | Owner/API and target assertion | New evidence |
| --- | --- | --- | --- |
| OV-1 | Two agents edit the same file at once | Scheduler+policy / resource request, effect prepare; canonical admission and private candidate conflict/release | N |
| OV-2 | Unclaimed edit grants silently | Scheduler+policy / resource request, effect prepare; canonical admission and private candidate conflict/release | N |
| OV-3 | Directory claim covers children, not siblings | Scheduler+policy / resource request, effect prepare; canonical admission and private candidate conflict/release | N |
| OV-4 | Case-variant paths | Scheduler+policy / resource request, effect prepare; canonical admission and private candidate conflict/release | N |
| OV-5 | Sensitive path stays denied | Policy+adapter / effect prepare; deny sensitive path without delegation | N |
| OV-6 | Node guard and Swift Kit disagree | Scheduler+policy / resource request, effect prepare; canonical admission and private candidate conflict/release | N |
| OV-7 | Whole-tree git command with a peer writing | Scheduler+policy / resource request, effect prepare; canonical admission and private candidate conflict/release | N |
| OV-8 | Claim guard cannot read the registry | Scheduler+policy / resource request, effect prepare; canonical admission and private candidate conflict/release | N |
| OV-9 | Peer touches the file after the first conflict was judged | Scheduler+policy / resource request, effect prepare; canonical admission and private candidate conflict/release | N |
| OV-10 | Two agents in a shared worktree commit | Scheduler+policy / resource request, effect prepare; canonical admission and private candidate conflict/release | N |
| OV-11 | Hand-widened claim | Identity+policy / resource request; reject forged/widened claim | N |
| DP-1 | Force with an assumption | Policy / conflict respond, delegate, effect approve; actor, scope, preconditions and recorded judgment | N |
| DP-2 | Force with no bound task | Policy / conflict respond, delegate, effect approve; actor, scope, preconditions and recorded judgment | N |
| DP-3 | Force turns out wrong | Policy / conflict respond, delegate, effect approve; actor, scope, preconditions and recorded judgment | N |
| DP-4 | Ask leaves the question with the holder | Policy / conflict respond, delegate, effect approve; actor, scope, preconditions and recorded judgment | N |
| DP-5 | Review reaches a person | Policy / conflict respond, delegate, effect approve; actor, scope, preconditions and recorded judgment | N |
| DP-6 | Invalid disposition | Policy / conflict respond, delegate, effect approve; actor, scope, preconditions and recorded judgment | N |
| DP-7 | Holder leaves before the judgment | Policy / conflict respond, delegate, effect approve; actor, scope, preconditions and recorded judgment | N |
| DP-8 | Conflict rejected outright | Policy / conflict respond, delegate, effect approve; actor, scope, preconditions and recorded judgment | N |
| DP-9 | Judge the same conflict twice | Policy / conflict respond, delegate, effect approve; actor, scope, preconditions and recorded judgment | N |
| AW-1 | Await on an action | Scheduler+messaging / wait create, request reply; durable condition, deadline and wake revalidation | N |
| AW-2 | Await on a peer | Scheduler+messaging / wait create, request reply; durable condition, deadline and wake revalidation | N |
| AW-3 | Await too many | Scheduler+messaging / wait create, request reply; durable condition, deadline and wake revalidation | N |
| AW-4 | Await expires | Scheduler+messaging / wait create, request reply; durable condition, deadline and wake revalidation | N |
| AW-5 | Owner of a required await disappears | Scheduler+messaging / wait create, request reply; durable condition, deadline and wake revalidation | N |
| AW-6 | Optional await at checkout | Scheduler+messaging / wait create, request reply; durable condition, deadline and wake revalidation | N |
| AW-7 | Required await at checkout | Scheduler+messaging / wait create, request reply; durable condition, deadline and wake revalidation | N |
| AW-8 | Mail while yielded | Scheduler+messaging / wait create, request reply; durable condition, deadline and wake revalidation | N |
| AW-9 | Await across two conditions | Scheduler+messaging / wait create, request reply; durable condition, deadline and wake revalidation | N |
| ST-1 | User presses Stop with debt | Scheduler+evidence / action close, wait cancel; required debt gate and terminal/cancel record | N |
| ST-2 | Agent ends its own turn with an unpaid judgment | Scheduler+evidence / action close, wait cancel; required debt gate and terminal/cancel record | N |
| ST-3 | Stop gate loops forever | Scheduler+evidence / action close, wait cancel; required debt gate and terminal/cancel record | N |
| ST-4 | Stop gate with only an await open | Scheduler+evidence / action close, wait cancel; required debt gate and terminal/cancel record | N |
| ST-5 | Map-harness task finishes unpaid | Scheduler+evidence / action close, wait cancel; required debt gate and terminal/cancel record | N |
| ST-6 | Owed commitment at release | Scheduler+evidence / action close, wait cancel; required debt gate and terminal/cancel record | N |
| ST-7 | Helper releases while parent owes | Scheduler+evidence / action close, wait cancel; required debt gate and terminal/cancel record | N |
| AK-1 | Ask to a task that is not checked in | Messaging+scheduler / request create/reply, action reassign; authorized reply, handoff, deadline and debt | N |
| AK-2 | Holder dies mid-request | Messaging+scheduler / request create/reply, action reassign; authorized reply, handoff, deadline and debt | N |
| AK-3 | Requester goes away | Messaging+scheduler / request create/reply, action reassign; authorized reply, handoff, deadline and debt | N |
| AK-4 | Commitment handed off | Messaging+scheduler / request create/reply, action reassign; authorized reply, handoff, deadline and debt | N |
| AK-5 | Ask past its due time | Messaging+scheduler / request create/reply, action reassign; authorized reply, handoff, deadline and debt | N |
| AK-6 | Uninvolved agent closes the request | Messaging+scheduler / request create/reply, action reassign; authorized reply, handoff, deadline and debt | N |
| AK-7 | Fulfil with no note | Messaging+scheduler / request create/reply, action reassign; authorized reply, handoff, deadline and debt | N |
| AK-8 | Two askers, one holder | Messaging+scheduler / request create/reply, action reassign; authorized reply, handoff, deadline and debt | N |
| PR-1 | pkill of another task's process | Identity+scheduler+adapter / session inspect, resource request, effect prepare; owned lifecycle/confined effect | N |
| PR-2 | Claim below a process id | Identity+scheduler+adapter / session inspect, resource request, effect prepare; owned lifecycle/confined effect | N |
| PR-3 | Recycled pid | Identity / session inspect; PID+creation proof, unknown blocked | N |
| PR-4 | Cancel targets the app or its ancestors | Identity+scheduler+adapter / session inspect, resource request, effect prepare; owned lifecycle/confined effect | N |
| PR-5 | Another user's process | Identity+scheduler+adapter / session inspect, resource request, effect prepare; owned lifecycle/confined effect | N |
| PR-6 | Entry with no start time | Identity / session inspect; PID+creation proof, unknown blocked | N |
| PR-7 | Two agents validate on one build dir | Identity+scheduler+adapter / session inspect, resource request, effect prepare; owned lifecycle/confined effect | N |
| PR-8 | Bundle held by another task | Identity+scheduler+adapter / session inspect, resource request, effect prepare; owned lifecycle/confined effect | N |
| PR-9 | Port claimed or listening | Identity+scheduler+adapter / session inspect, resource request, effect prepare; owned lifecycle/confined effect | N |
| PR-10 | Long-running child | Identity+scheduler+adapter / session inspect, resource request, effect prepare; owned lifecycle/confined effect | N |
| PR-11 | Two agents want the same simulator | Identity+scheduler+adapter / session inspect, resource request, effect prepare; owned lifecycle/confined effect | N |
| PR-12 | Remove another task's worktree | Identity+scheduler+adapter / session inspect, resource request, effect prepare; owned lifecycle/confined effect | N |
| PR-13 | Detached HEAD | Identity+scheduler+adapter / session inspect, resource request, effect prepare; owned lifecycle/confined effect | N |
| PR-14 | Run installer from a feature branch | Identity+scheduler+adapter / session inspect, resource request, effect prepare; owned lifecycle/confined effect | N |
| LF-1 | Owner process dies | Identity+store / session heartbeat, resource inspect; authenticated lifecycle and fail-closed recovery | N |
| LF-2 | Entry impersonation | Identity+policy / resource request; reject forged/widened claim | N |
| LF-3 | Remote caller through agentd | Identity+store / session heartbeat, resource inspect; authenticated lifecycle and fail-closed recovery | N |
| LF-4 | Corrupt registry file | Store / inspect; corrupt DB fails closed without reset | N |
| LF-5 | Retried check-in | Store / session register; content-bound idempotency rejects changed retry | N |
| LF-6 | Malformed session id | Identity+store / session heartbeat, resource inspect; authenticated lifecycle and fail-closed recovery | N |
| LF-7 | Daemon down | Identity+store / session heartbeat, resource inspect; authenticated lifecycle and fail-closed recovery | N |
| LF-8 | Clock skew on expiry | Scheduler / wait inspect; clock skew cannot release live hold | N |
| DL-1 | Broadcast while two tasks are live | Messaging+presentation / message send/ack, inbox; recipient snapshot and durable delivery/recovery | N |
| DL-2 | Mail needing acknowledgement | Messaging+presentation / message send/ack, inbox; recipient snapshot and durable delivery/recovery | N |
| DL-3 | Wrong session gets mail | Messaging+presentation / message send/ack, inbox; recipient snapshot and durable delivery/recovery | N |
| DL-4 | Mac locked or idle while an action waits | Scope / inspect; phone or Mac specifics excluded; document Windows analogue if applicable | N |
| DL-5 | Stale presence sample | Messaging+presentation / message send/ack, inbox; recipient snapshot and durable delivery/recovery | N |
| DL-6 | Several actions while away | Messaging+presentation / message send/ack, inbox; recipient snapshot and durable delivery/recovery | N |
| DL-7 | Action closed while away | Messaging+presentation / message send/ack, inbox; recipient snapshot and durable delivery/recovery | N |
| DL-8 | Action arrives as the user returns | Messaging+presentation / message send/ack, inbox; recipient snapshot and durable delivery/recovery | N |
| DL-9 | Phone command unsigned or forged | Scope / inspect; phone or Mac specifics excluded; document Windows analogue if applicable | N |
| DL-10 | Old signed command replayed | Scope / inspect; phone or Mac specifics excluded; document Windows analogue if applicable | N |
| DL-11 | Phone prompt that cannot start | Scope / inspect; phone or Mac specifics excluded; document Windows analogue if applicable | N |
| DL-12 | Phone away from the Mac | Scope / inspect; phone or Mac specifics excluded; document Windows analogue if applicable | N |
| DL-13 | No iCloud account on the phone | Scope / inspect; phone or Mac specifics excluded; document Windows analogue if applicable | N |
| HS-1 | Claude Code, Cursor and off-map speak the same words | Scope / API version; other hosts excluded, no parity claim | N |
| HS-2 | Old vocabulary | Identity+presentation+lifecycle / API version, task view; Codex boundary and update discipline | N |
| HS-3 | Needed opportunity | Identity+presentation+lifecycle / API version, task view; Codex boundary and update discipline | N |
| HS-4 | Worker teaching drift | Identity+presentation+lifecycle / API version, task view; Codex boundary and update discipline | N |
| HS-5 | Third-party CLI spike | Scope / API version; other hosts excluded, no parity claim | N |
| HS-6 | Updater with an agent running | Lifecycle / update inspect; drain/rollback without dual authority | N |

Catalogue rows: 85. Completion requires case-by-case evidence ledger with command, revision, result, receipt and limits. Mapping/link checks prove document completeness only.

## Documentation completion gate

Run `powershell -NoProfile -File scripts/ValidateKernelDocs.ps1` for document consistency while requirements are planned. Future feature acceptance and cutover require `powershell -NoProfile -File scripts/ValidateKernelDocs.ps1 -RequireComplete`, which fails until every in-scope requirement has a passing source-exact receipt and every exclusion has a rationale. The [versioned acceptance catalogue](kernel-acceptance.json) distinguishes these states. Service operations in this plan are planned API descriptions, not executable CLI commands. Current executable CLI verbs are defined in [the current CLI source](../src/AgentOS.Cli/Program.cs).

A current legacy CLI example is:

```text
agent-os status <project>
```

Before broader implementation, freeze the concrete SQLite path, DDL and migrations, service request/response schemas, and CLI grammar against this contract. The current gate checks document structure, historical case meanings, the fixed semantic checklist, baseline field names, local links and anchors, exact public type declarations, the populated JSON example, current CLI verb names, and any claimed executed receipt fields and artifact hashes. It checks executable examples against verbs, not argument syntax. Later service and behavioral tests must establish runtime acceptance.
