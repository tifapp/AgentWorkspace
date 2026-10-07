# Mediation and recovery contract

Agent-os supports **Codex CLI 0.160.0 on Windows x64**. Its app-server integration uses experimental dynamic tools and is deliberately version gated. It preserves the user's model and reasoning preference and authenticated account. It does not import user hooks, plugins, MCP configuration or resource-checkin instructions. Other host versions require validation before admission.

## Supported effects

| Surface | Enforced behavior |
| --- | --- |
| Source | Each work unit receives a committed private Git clone without hard links. The user's checkout and uncommitted files are separate. |
| PowerShell files and tests | Managed commands and final validation run in a Windows AppContainer with no network capabilities and a private workspace grant. A whitelist supplies the environment. The native Codex working directory is separate and read-only; native shell tools are disabled. |
| Private Git | A trusted adapter offers status, diff, add, commit and log. It resets executable Git configuration, ignores global/system configuration, disables hooks and monitors, and rejects links and external object stores before processing private files. Direct sandboxed Git fails explicitly because Git for Windows cannot open the null device in this AppContainer. |
| Shared publication | After admission, the runtime rereads current path identities, applies the exact candidate delta to a private index, validates the combined tree and changes agent-os/integrated with expected-old compare-and-swap. A stale candidate stays private. Up to two automatic Codex revisions are allowed. |
| Machine coordination | All supported entry points use one per-Windows-user journal and an exclusive update handle. Adapters derive identities. Same-resource waits are ordered; independent work units keep progressing. Live ownership uses PID plus process creation time, with no time-based expiry. |
| Processes | Managed commands and trusted runtime commands enter Windows Job Objects before execution is released. Ordinary descendants stop on completion, cancellation or runtime death. Failed containment has no unrestricted fallback. |
| Preview ports | A runtime-owned loopback listener serves a bounded, immutable source snapshot as plain text. An occupied preferred port selects another available port. Completion and cancellation close the exact listener. Dot files, links and oversized content are excluded. |
| Shared database | The runtime records validation evidence in its own shared SQLite ledger using serialized transactions and content identity. Equivalent exact records deduplicate. This is not an adapter for arbitrary user databases. |
| Authority | Release decisions are for one local tag and exact tested commit. Candidate, evidence, choice and consequences are prepared first. Equivalent requests reuse scope. Approval rechecks evidence and history. No remote push or deployment is granted. |

Ownership release is not evidence of effect success. Project history separately records private, waiting, completed, failed, stale, canceled and unknown outcomes. The machine journal records unknown outcomes after an owner exit and never replays an uncertain effect.

The CLI can launch into an already open desktop runtime through a current-user-only named pipe. Stable launch request identities deduplicate retries, including after restart. A launch cannot change the owning runtime's validation policy. Different projects retain independent runtimes and share the same machine coordinator. The machine diagnostic journal retains its most recent 512 outcomes; durable project evidence is separate.

## Scope and limitations

This is a managed workflow runtime, not a general Windows reference monitor. A regular AppContainer retains access to Windows-granted platform resources, including its package profile and some operating-system caches. Tests prove refusal of an outside project write and a direct connection to an actual listening socket. They do not establish absence of every possible Windows vulnerability or side channel.

Unmanaged programs, arbitrary native SDKs, remote services, deployment, network projects, symbolic links, hard links, junction aliases, submodules, bare repositories and linked Git worktrees are unsupported mediation surfaces. The product does not silently run an unsupported command with broader permissions. PowerShell validation such as `& .\Validate.ps1` is supported; `dotnet test` is not advertised as a verified managed command in this release.

Coordination covers current-user agent-os processes. It does not coordinate other Windows accounts or arbitrary external writers. An external edit to the integrated ref is detected by revalidation and compare-and-swap. Readers observe complete Git commits, not an atomic change to the user's live checkout. Different paths can have semantic dependencies; combined validation remains necessary.

Parking is an asynchronous wait at a managed effect boundary. Independent work units continue while a tool is pending. Native Codex tool continuation and transparent suspension of an arbitrary running shell halfway through its effects are not claimed. Opaque commands conservatively own their entire private workspace. Adapter acquisition has a fixed direction: publication may acquire the evidence ledger, and ledger operations never acquire publication ownership.

## Recovery

Project state is durable schema-1 JSON, written with atomic replacement and a flush. Malformed state and machine journals fail closed. An exclusive .git/agent-os.runtime.lock handle owns each project; the surviving filename does not indicate a live owner. Ownership is not reclaimed just because a clock deadline passed.

On restart, saved publication intent is reconciled against Git. A matching commit or tag is recovered as completed without repeating it. An interrupted host without a confirmed effect is unknown. Private output stays available. Cleanup targets only owned clones and does not follow reparse points. Old sandbox profiles and temporary credential copies have owner records and are recovered after owner death; an inaccessible owner is not assumed dead. Private files may retain an obsolete sandbox SID until cleanup.

Cancellation removes waits and stops owned commands and previews. Restart retains the exact project, custom data root, task lineage, decisions and evidence. Validation records identify the complete commit/tree, shared base, command, sandbox environment version, source cleanliness and log hash. Sudden power loss beyond Windows and filesystem durability is not guaranteed.

Evidence also records the actual Codex model, runtime assembly SHA-256 and the exact command environment fingerprint. A crash between native sandbox-profile creation and its ownership-record write can leave an unused profile; this narrow creation window is not proven recoverable. It cannot authorize a writer without the later process/job setup.

## Resource-checkin retirement

The retirement installer requires matching successful desktop, regression and comparison evidence. It refuses other stored legacy tasks, including expired records, drains the installation task, holds the legacy registry lock, freezes old clients with an unsupported schema, archives the skill and replaces global instructions. No live legacy entry is erased to proceed. If host permissions forbid moving the installed directory, it preserves those permissions and archives a copy; the original remains discoverable but its execution is disabled by policy and registry schema. The archive and receipt provide a reversible migration record.

Replacement applies to the supported managed surfaces above. It does not turn unsupported external operations into protected work. See [retirement evidence](retirement.md) and [the product objective](product-objective.md).
