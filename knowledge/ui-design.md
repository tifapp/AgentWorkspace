# Compact UI design findings

The WinUI 3 shell defaults to 1040×760 with a wrapping project header, one task map, and a bottom composer. The project name opens settings; Settings and pending decisions remain in the header; Maps is available in advanced settings. At narrow widths, details replace the map and hide the new task composer. Escape and Back return to the map. Task rows reuse controls by work ID, preserving drafts and expanded reports while snapshots patch status and text. Completed rows show a short result in the expander header; Reply opens a follow-up composer. Follow-ups create child work units through StartAsync(task, autoIntegrate, parentId).

--ui-preview builds a deterministic, read-only presentation fixture from PreviewData and calls the same ApplySnapshot path as real state. It never opens a runtime or saves settings. PreviewUpdate changes sample status and report text while reusing rows; PreviewReset toggles the empty map. Mutation controls are disabled or unavailable. The fixture includes completed and child, running, waiting, failed, private, stale, unknown, and pending decision states.

The active task detail transcript reads persisted Codex JSONL while its expander is open. It extracts completed agentMessage text, limits input and output, and tolerates concurrent writes and invalid partial JSON. It does not interpret event lines as steer acknowledgments. The runtime has no steering, clarification, citation, model, or effort setter exposed to this UI.

RuntimeUiCommands forwards supported actions to ProjectRuntime without changing runtime semantics. A follow-up is a new child work unit, not a continuation of the same host thread. Release review shows exact candidate, destination, scope, diff, changed files, and matching validation evidence.

scripts/ValidateCompactUi.ps1 is a PowerShell source structure check. scripts/test-compact-ui.ps1 launches a test-owned preview process, uses UI Automation, and captures wide and narrow screenshots through inspect-ui.ps1 when a built executable is available. Native SDK compilation and desktop capture remain deferred to the build reviewer outside managed tools.

