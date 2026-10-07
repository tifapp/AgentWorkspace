param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$paths = @{
    main = Join-Path $repo 'src/AgentOS.App/MainWindow.cs'
    row = Join-Path $repo 'src/AgentOS.App/TaskRow.cs'
    adapter = Join-Path $repo 'src/AgentOS.App/RuntimeUiCommands.cs'
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
Write-Output 'Compact UI structural checks passed. This does not build or render the app.'

