# Desktop interaction controls

In a live desktop session, **Settings > Advanced** opens **Maps**, **Capture context into map**, **Work interactions**, and **External effects**. Task menus also open interactions scoped to that task. These controls are absent from `--ui-preview`; preview creates no capture controller, global hook, project runtime, or effect action.

**Capture context into map** opens the real capture review. An in-app or tray request tries to bring the retained external window to the foreground for a bounded period before capture. The core capture service rechecks the retained window identity and foreground state. If activation fails, capture reports why and takes no screenshot. The global shortcut captures before Agent OS activates. The capture review lets the user inspect and redact text and image, choose a project, generate context suggestions through its isolated microagent, edit the draft, save, and separately confirm starting selected tasks. Suggestion buttons only fill fields for review. Saving a draft does not start work.

**Maps** lists saved titles and statuses. Select a map to inspect its graph, task list, dependency links, citation hashes, and task attempt IDs. Draft title, task prompt, acceptance criteria, selection flags, and links can be edited and saved explicitly. Active or closed maps cannot be saved; canvas browsing remains available, and link changes on them are discarded. **Start selected ready tasks** is a separate action that requires saved edits and complete dependencies. Capture citations on an existing saved draft are immutable in the current runtime.

**Work interactions** reads the durable project journal. Pending clarifications can be answered, peer requests acknowledged or handed to an active task, required obligations resolved, and waits inspected or canceled. Resource waits also offer a resolution field. Followup proposals appear with **Accept and start followup**; they are never submitted automatically. The current public runtime has no followup rejection method, so leaving a proposal pending is the available decline behavior.

A reply to an active task is sent as steering to its current turn. A reply to a completed task starts a child followup through the runtime and preserves parent and attempt lineage. The completed task menu has no same-goal revision action. Other terminal states cannot use this reply composer.

**External effects** opens the separate review window through `ExternalEffectsDialog.OpenAsync`. Preparing, approving, executing, or reconciling an effect requires its own explicit action and runtime configuration. This source wiring used no live external credentials or effects.

## Verification

`ValidateContextMaps.ps1` and `ValidateCompactUi.ps1` passed source checks. Native compilation was attempted through the installed `dotnet.exe`, but this managed environment exposed no SDK directory and produced no build output or App binary, so compilation and rendered UI behavior remain unverified. The tray and in-app capture fallback also need a real Windows foreground test with an owned external window.
