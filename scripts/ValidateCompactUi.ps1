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
$cacheRelative = 'Microsoft/Windows/PowerShell/ModuleAnalysisCache'
$cachePath = Join-Path $repo $cacheRelative
if (Test-Path -LiteralPath $cachePath -PathType Leaf) { throw "Generated PowerShell module cache is a source input: $cacheRelative" }
$ignore = Get-Content -LiteralPath (Join-Path $repo '.gitignore') -Raw
Require $ignore '(?m)^/Microsoft/Windows/PowerShell/ModuleAnalysisCache\r?$' 'exact generated cache ignore rule'
$sandbox = Get-Content -LiteralPath (Join-Path $repo 'src/AgentOS.Core/ManagedSandbox.cs') -Raw
Require $sandbox '\["PSModuleAnalysisCachePath"\]\s*=\s*Path\.Combine\(temp,\s*"ModuleAnalysisCache"\)' 'private PowerShell module cache environment'
Exclude $main 'RequestedTheme\s*=\s*ElementTheme\.Light|ShowPage\(|NavWorkspace|NavDecisions|NavEvidence|NavSetup' 'old theme or page navigation'
Require $main 'new Windows\.Graphics\.SizeInt32\(1040,760\)' 'default compact size'
Require $main 'Dictionary<string,\s*TaskRow>' 'stable task row dictionary'
Require $main 'rows\.TryGetValue' 'row reuse'
Require $main 'Reconcile\(tree,' 'task reconciliation'
Require $main 'rows\[work\.Id\]\.Children' 'parent-child nesting'
# Inspect executable handler bodies. Quoted examples and comments cannot satisfy dispatch checks.
function Get-CSharpMethodBody([string]$source, [string]$signature, [string]$label) {
    $code = [regex]::Replace($source, '(?s)/\*.*?\*/|//[^\r\n]*|@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|''(?:\\.|[^''\\])*''', '')
    $match = [regex]::Match($code, $signature + '[^;{}]*?(?:\{|=>)')
    if (!$match.Success) { throw "Missing $label handler" }
    if ($match.Value.EndsWith('=>')) {
        $end = $code.IndexOf(';', $match.Index + $match.Length)
        if ($end -lt 0) { throw "Unclosed $label expression" }
        return $code.Substring($match.Index + $match.Length, $end - $match.Index - $match.Length)
    }
    $open = $code.IndexOf('{', $match.Index)
    $depth = 0
    for ($i = $open; $i -lt $code.Length; $i++) {
        if ($code[$i] -eq '{') { $depth++ }
        elseif ($code[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { return $code.Substring($open + 1, $i - $open - 1) }
        }
    }
    throw "Unclosed $label handler"
}
$followUp = Get-CSharpMethodBody $main 'SendFollowUp\s*\(\s*string\s+parentId\s*,\s*string\s+value\s*\)' 'follow-up'
$start = Get-CSharpMethodBody $adapter 'Start\s*\(\s*string\s+prompt\s*,\s*bool\s+autoIntegrate\b' 'runtime start'
Require $start 'runtime\.StartAsync\s*\(\s*prompt\s*,\s*autoIntegrate\b' 'runtime new task dispatch'
if ($followUp -match '\bCommands\.(?:SendSteering|ReplyAfterCompletion)\s*\(') {
    Require $followUp '\bIsActive\b' 'active task branch'
    Require $followUp '\bCommands\.SendSteering\s*\(\s*parentId\s*,\s*value\s*\)' 'active steering dispatch'
    Require $followUp '\bCommands\.ReplyAfterCompletion\s*\(\s*parentId\s*,\s*value\s*\)' 'completed reply dispatch'
    Exclude $followUp '\bCommands\.Start\s*\(\s*value\s*,\s*auto\.IsChecked\s*==\s*true\s*,\s*parentId\s*\)' 'unconditional child task dispatch'
    $steer = Get-CSharpMethodBody $adapter 'SendSteering\s*\(' 'runtime steering'
    $reply = Get-CSharpMethodBody $adapter 'ReplyAfterCompletion\s*\(' 'runtime completed reply'
    Require $steer 'runtime\.SendSteeringAsync\s*\(' 'runtime steering route'
    Require $reply 'runtime\.ReplyAfterCompletionAsync\s*\(' 'runtime completed reply route'
    $runtime = Get-Content -LiteralPath (Join-Path $repo 'src/AgentOS.Core/InteractiveWork.cs') -Raw
    $replyRuntime = Get-CSharpMethodBody $runtime 'ReplyAfterCompletionAsync\s*\(' 'completed reply lineage'
    Require $replyRuntime 'StartAsync\s*\(\s*text\s*,\s*parentId\s*:\s*workId\b' 'completed reply parent lineage'
} else {
    # Current integrated UI still uses the original child task route until its separate UI task lands.
    Require $followUp '\bCommands\.Start\s*\(\s*value\s*,\s*auto\.IsChecked\s*==\s*true\s*,\s*parentId\s*\)' 'transitional child follow-up'
    Require $start 'runtime\.StartAsync\s*\(\s*prompt\s*,\s*autoIntegrate\s*,\s*parentId\s*:\s*parentId\s*\)' 'runtime child dispatch'
}
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

