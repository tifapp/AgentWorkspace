param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$paths = @{
    main = Join-Path $repo 'src/AgentOS.App/MainWindow.cs'
    row = Join-Path $repo 'src/AgentOS.App/TaskRow.cs'
    adapter = Join-Path $repo 'src/AgentOS.App/RuntimeUiCommands.cs'
    preview = Join-Path $repo 'src/AgentOS.App/PreviewData.cs'
    transcript = Join-Path $repo 'src/AgentOS.App/TranscriptReader.cs'
}
foreach ($path in $paths.Values) { if (!(Test-Path -LiteralPath $path)) { throw "Missing compact UI source: $path" } }
$main = Get-Content -LiteralPath $paths.main -Raw
$row = Get-Content -LiteralPath $paths.row -Raw
$adapter = Get-Content -LiteralPath $paths.adapter -Raw
function Require([string]$text,[string]$pattern,[string]$label) {
    if ($text -notmatch $pattern) { throw "Missing $label" }
}
function Exclude([string]$text,[string]$pattern,[string]$label) {
    if ($text -match $pattern) { throw "Unexpected $label" }
}
Exclude $main 'RequestedTheme\s*=\s*ElementTheme\.Light|ShowPage\(|NavWorkspace|NavDecisions|NavEvidence|NavSetup' 'old theme or page navigation'
Require $main 'new Windows\.Graphics\.SizeInt32\(1040,760\)' 'default compact size'
Require $main 'Dictionary<string,\s*TaskRow>' 'stable task row dictionary'
Require $main 'rows\.TryGetValue' 'row reuse'
Require $main 'Reconcile\(tree,' 'task reconciliation'
Require $main 'rows\[work\.Id\]\.Children' 'parent-child nesting'
Require $main 'Commands\.Start\(value,auto\.IsChecked==true,parentId\)' 'actual child follow-up'
Require $adapter 'runtime\.StartAsync\(prompt, autoIntegrate, parentId: parentId\)' 'runtime child dispatch'
foreach ($id in @('TaskPrompt','ProjectPath','ValidationCommand','StartTask','SaveSetup','ApproveDecision','RejectDecision','PendingDecisions','WorkList')) {
    Require $main ('"' + $id + '"') "$id automation ID"
}
foreach ($id in @('CancelTask','CodexReport','SendFollowUp','TaskDecision')) {
    Require $row ('"' + $id + '"') "$id task automation ID"
}
Require $row 'reportExpander' 'expandable result'
Require $row 'ClearReply\(' 'successful follow-up draft clearing'
Require $main 'using Microsoft\.UI\.Xaml\.Automation\.Peers;' 'automation peer import'
Require $row 'using Microsoft\.UI\.Xaml\.Automation\.Peers;' 'row automation peer import'
Require $main 'ApplySnapshot\(ProjectState state\)' 'shared snapshot patch'
Require $main 'PreviewData\.Create' 'preview fixture path'
Require $main '"PreviewUpdate"' 'preview update control'
Require $main 'ShowPreviewSettings' 'preview settings guard'
Require $main 'TranscriptReader\.ReadAsync' 'transcript reader'
Require $row 'reportExpander\.Header=snippet' 'result preview header'
Require $row 'replyArea\.Visibility=Visibility\.Collapsed' 'collapsed follow-up composer'
Require $row 'KeyboardAccelerator.*VirtualKey\.Enter' 'follow-up keyboard send'
Require (Get-Content $paths.preview -Raw) 'preview-completed|preview-running|preview-unknown' 'preview status fixture'
Require (Get-Content $paths.transcript -Raw) 'FileShare\.ReadWrite' 'shared transcript read'
Write-Output 'Compact UI structural checks passed. This does not build or render the app.'

$maps = Get-Content -LiteralPath (Join-Path $repo 'src/AgentOS.Core/TaskMaps.cs') -Raw
$models = Get-Content -LiteralPath (Join-Path $repo 'src/AgentOS.Core/Models.cs') -Raw
$storage = Get-Content -LiteralPath (Join-Path $repo 'src/AgentOS.Core/Storage.cs') -Raw
$tests = Get-Content -LiteralPath (Join-Path $repo 'tests/AgentOS.Tests/Program.cs') -Raw
$cli = Get-Content -LiteralPath (Join-Path $repo 'src/AgentOS.Cli/Program.cs') -Raw
Require $main 'Action\("Maps",ShowMaps,"TaskMaps"\)' 'task-map entry in compact UI'
Require $main 'SaveMapDraft|Save reviewed draft' 'reviewed draft save'
Require $main 'StartSelectedMapTasksAsync' 'explicit map start control'
Require $models 'List<TaskMap> Maps' 'durable maps'
Require $storage 'schema1\.bak' 'schema-1 migration backup'
Require $maps 'Task dependency cycle' 'dependency cycle refusal'
Require $maps 'Selected && t.WorkId == null && TaskMapRules.DependenciesComplete' 'selected dependency admission'
Require $maps 'Captured citations cannot be replaced' 'citation immutability'
Require $cli 'case "map-save"' 'CLI map submission'
Require $tests 'Task maps remain drafts until selected dependencies are ready' 'map admission test'
Require $tests 'Old state keeps history and captured citations cannot be rewritten' 'migration and citation test'
Require $tests 'CLI broker uses the open project authority for maps' 'shared authority map test'
Require (Get-Content -LiteralPath (Join-Path $repo 'src/AgentOS.Core/ProjectBroker.cs') -Raw) 'case "map-start"' 'shared authority map start'
foreach($relative in @('docs/product-objective.md','docs/implementation-plan.md','docs/reference-coverage.md','docs/guarantees.md','knowledge/decisions.md')) {
 Require (Get-Content -LiteralPath (Join-Path $repo $relative) -Raw) 'Phone work is deferred and explicitly excluded' "$relative phone exclusion"
}
Write-Output 'Draft-map and phone-boundary structural checks passed. Native compilation and execution remain unverified.'

