# Product objective: supersede resource-checkin

## Objective

**Agent-os should outperform and supersede the resource-checkin skill, making the skill obsolete for supported Codex work.**

The governing measure is valid autonomous progress with as few interruptions as possible. An agent should express its task and use familiar file, shell, test and Git actions. The runtime should derive and maintain the coordination needed for those effects. Consequential concurrency must remain understandable to the agent and the human.

The current release replaces the skill for supported managed Codex work on this Windows account. It adds machine coordination, enforced PowerShell isolation, scoped Git tools, owned process trees, previews and a shared evidence ledger. The global skill requirement has been retired with a recorded migration. General Windows and external-service mediation remains outside this scope.

## What replacement must accomplish

| Responsibility | Replacement acceptance criterion |
| --- | --- |
| Agent effort | Ordinary work requires no resource-checkin skill invocation, resource spelling, registry inspection, check-in, renew, handoff bookkeeping or checkout commands. Runtime ownership follows the actual work and its effects. Agents still make substantive decisions within their delegation. |
| Machine-wide coordination | A single authority coordinates supported resources across projects and concurrent Codex sessions, including entry points outside the desktop when they are advertised as supported. Coverage includes the skill's file and non-file resource responsibilities. A project-local Git gate alone cannot satisfy this objective. |
| Resource identity | Canonical identity handles case, directory boundaries, aliases and shared underlying objects as applicable. Process identity survives PID reuse. Names for external resources reflect the actual service and operation. Unsupported identities are explicit. |
| Effect mediation | Supported mutations cross an enforceable boundary before changing shared state. An omitted declaration cannot silently bypass coordination. Opaque shell effects require an adequate execution boundary or adapter; uncertainty cannot be presented as protected execution. |
| Independent progress | Uncontended work proceeds without negotiation. Only effects whose preconditions or authority prevent execution are held. Independent work continues within the task wherever host support permits and across other work units. |
| Waiting and revalidation | Waits have observable conditions and cancellation. Resumption checks current ownership, source versions, authority and other relevant preconditions. Waking up alone never authorizes a stale operation. Cycles, starvation and abandoned waits have bounded recovery behavior. |
| Lifecycle and recovery | Ownership lasts as long as relevant writers and resources remain live. Agent exits, detached descendants, runtime failure, restart and lost acknowledgements cannot silently free a live writer, replay an uncertain effect or report unconfirmed success. |
| Authority | Contention is resolved within existing delegation. A human decision is prepared with candidate, evidence, unresolved choice, consequences and narrow scope. Equivalent authorized operations reuse recorded scope; no permission is broadened to remove a wait. |
| Usability | Tasks expose completed, private, pending, failed, stale and unknown outcomes, together with consequential waits and recovery actions. Routine coordination is automatic; evidence remains inspectable. |
| Coverage and transition | Users can determine precisely which resources and entry points are mediated. Migration from the skill has one effective coordination authority, retains outstanding work, and supports recovery or rollback. |

The baseline already has useful capabilities: a machine-wide registry, per-resource intent, conflict reporting, atomic rejection of conflicting registry requests, identity/liveness rules and a foreground process wrapper that renews and checks out automatically. Its skill explicitly states that registry serialization and claims do not lock filesystem effects. Replacement comparisons must include the baseline's supported automation.

## Proving that agent-os outperforms the skill

Use matched workloads against the current resource-checkin implementation and agent-os. Record the baseline skill/scripts version, app and runtime source, Codex version/model/configuration, project revisions, validation commands and environment. Keep delegation and successful-result criteria equivalent. Use isolated disposable resources, repeat runs and vary execution order so startup effects and model variability are visible.

Measure:

- Valid completed work and elapsed time, including recovery and rework.
- Agent coordination commands, tool calls, tokens and time spent maintaining coordination.
- Routine human interruptions, separately from legitimate authority or product decisions.
- False conflicts and waits imposed on genuinely independent work.
- Uncontended overhead and contention wait duration, including tail latency and starvation.
- Lost updates, escaped effects on supported surfaces, stale resumptions, duplicate effects, stranded resources and incorrectly reported outcomes.

The replacement gate requires zero routine agent registry-maintenance actions and zero routine human coordination interventions in the supported workflows. It must demonstrate lower coordination effort and unnecessary interruption, without reducing valid progress or accepting safety regressions. Define comparison tolerances before running benchmarks, retain failures, and report sample counts and uncertainty. Fewer log messages, a nicer interface, or a single favorable run cannot establish superiority.

Required scenarios include:

1. Independent files and genuinely conflicting writes from at least two real Codex work units; directory/prefix boundaries and Windows path aliases.
2. A multi-file change and shared Git/index operations, with readers seeing only the advertised consistency guarantee.
3. An opaque shell command with multiple effects, including a child writer that survives its initiating agent.
4. An occupied port, task-owned process cancellation, shared validation resources and a shared database or equivalent non-file resource adapter.
5. A blocked effect alongside independent work; cancellation, stale wakeup, newly arriving contention and recovery from a wait cycle.
6. Agent and runtime death, restart, corrupt state, clock changes and successful publication with a lost response.
7. A scoped human decision while independent work continues, including repeated equivalent requests and changed candidate evidence.
8. Simultaneous sessions in different projects and migration with legacy work still active, demonstrating that overlapping writers cannot be admitted by separate authorities.

Use the supplied North Star, edge-case catalogue and task-map states to extend these scenarios. [Reference coverage](reference-coverage.md) distinguishes existing Windows evidence from future requirements.

## Engineering milestones

1. **Measure the baseline.** Add a repeatable comparison harness for skill-coordinated Codex and the existing agent-os runtime. Preserve both safety outcomes and coordination costs.
2. **Establish machine-wide ownership.** Define resource identity, authenticated task/process ownership, a single admission authority, and a migration protocol. Separate contention from delegation throughout.
3. **Mediate ordinary effects.** Establish the Windows/Codex execution boundary for file and opaque-shell workflows. Add process, port and validation-resource adapters, then a shared-service adapter. Prove each surface before claiming coverage.
4. **Schedule and recover.** Implement supported host suspension/wakeup, precondition revalidation, independent continuation, cancellation, cycle handling and durable effect reconciliation. If Codex lacks a required hook, record and resolve that integration dependency.
5. **Prove replacement and retire the skill dependency.** Run the paired acceptance suite, close coverage gaps, migrate active work and demonstrate routine Codex work without the skill installed or referenced in its instructions. Remove the dependency only after the replacement gate passes and the applicable installation instructions are explicitly updated.

## Current result and operating rule

The final runtime passed 34 integration checks and a real desktop workflow with conflicting and independent Codex tasks. Managed tools derive resource identities and require no check-in, renew, declaration or checkout. A matched 11-repetition coordination benchmark measured median admission/effect/release at 136.47 ms versus 222.09 ms for the legacy automated wrapper and 312.54 ms for manual registry commands. This establishes this workload's coordination overhead; whole-task throughput, model tokens and general performance superiority remain unmeasured.

Use agent-os for the supported managed scope. Native arbitrary SDKs, unmanaged writers, other Windows accounts and external services remain unsupported. The old unconstrained script-host escape test is retained as a negative control; it is not a selectable product host. See [current guarantees](guarantees.md), [retirement receipt and limitations](retirement.md) and [executed validation](validation-report.md).

## Approved platform boundary (2026-10-07)

This program is Windows/Codex only. Phone work is deferred and explicitly excluded: no mobile app, relay, pairing, phone notifications, or phone control is implemented or approved for this release. Future phone work needs a separate decision and acceptance evidence.
