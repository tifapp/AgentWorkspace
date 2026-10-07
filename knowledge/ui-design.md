# Compact UI design findings

The desktop uses WinUI 3 native controls and the app's XamlControlsResources. The compact shell defaults to 1040×760 and has a small project header, a single scrollable task tree, and a bottom composer. Settings and exact release decision review are on demand. The header lists every pending decision; each related task also shows a review action. At 1000 effective pixels and above, details can appear beside the tree; below that, details replace the tree with a Back control. The task tree uses actual parent-child controls and a thin connector line.

ProjectRuntime exposes StartAsync(task, autoIntegrate, parentId), ReviseAsync, IntegrateAsync, Cancel, CleanupAsync, RequestReleaseAsync, DecideAsync, and artifact paths. It has no steering, clarification, or citation UI APIs. A follow-up starts a new child work unit through parentId; it is not a continuation of the same host thread. Model and effort selectors are omitted because the desktop runtime does not expose a setter for them. The transcript action opens the persisted transcript when available; no live file polling is used.

RuntimeUiCommands is a thin UI command adapter; it forwards supported actions to ProjectRuntime without changing runtime semantics. Rows are keyed by WorkUnit.Id and reconciled into parent panels. Snapshot updates patch each row's text, status, result and available controls while keeping reply drafts and expanded reports. Details are created on demand; their heading and status are patched during timer updates. A Refresh details action reloads full evidence and activity. New task and follow-up prompts clear only after StartAsync succeeds. Release review shows the exact candidate, destination, scope, diff, changed files and matching validation evidence before calling DecideAsync.

scripts/ValidateCompactUi.ps1 checks source structure and automation IDs with PowerShell only. It is not a build, a runtime test, or rendered UI verification. A native SDK build and desktop inspection are still required outside this managed task.


