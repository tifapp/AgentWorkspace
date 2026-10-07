param([string]$Output='artifacts/retirement/comparison', [string]$Skill="$HOME/.codex/skills/resource-checkin")
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$destination=[IO.Path]::GetFullPath((Join-Path $repo $Output))
[IO.Directory]::CreateDirectory($destination)|Out-Null
$registry=Join-Path $Skill 'scripts/registry.mjs'
$wrapper=Join-Path $Skill 'scripts/run.mjs'
$test=Join-Path $repo 'tests/AgentOS.Tests/bin/Release/net10.0-windows10.0.19041.0/AgentOS.Tests.exe'
$previous=$env:RESOURCE_CHECKIN_REGISTRY
try {
 $env:RESOURCE_CHECKIN_REGISTRY=Join-Path $destination 'baseline-registry.md'
 # Preregistered coordination-only gate: same no-op effect, 11 repetitions, no admitted overlap,
 # zero agent maintenance operations; median overhead <= baseline. No full-task speed claim.
 $worker=Join-Path $destination 'noop.mjs'; [IO.File]::WriteAllText($worker,'process.exit(0);')
 $baseline=@(); $baselineWrapper=@()
 for($i=0;$i -lt 11;$i++) {
  $timer=[Diagnostics.Stopwatch]::StartNew()
  & node $registry checkin --agent benchmark --task noop --session "paired-$i" --key effect --write 'id:derived-fixture-resource' --on-conflict fail | Out-Null
  if($LASTEXITCODE -ne 0){throw 'Baseline admission failed'}
  & node $worker
  if($LASTEXITCODE -ne 0){throw 'Baseline effect failed'}
  & node $registry checkout --session "paired-$i" --key effect | Out-Null
  if($LASTEXITCODE -ne 0){throw 'Baseline release failed'}
  $baseline+=$timer.Elapsed.TotalMilliseconds
  $timer.Restart()
  $ErrorActionPreference='Continue'
  & node $wrapper --agent benchmark --task noop --write 'id:derived-fixture-resource' -- node $worker 2> (Join-Path $destination "wrapper-$i.log")
  $wrapperExit=$LASTEXITCODE; $ErrorActionPreference='Stop'
  if($wrapperExit -ne 0){throw 'Baseline automated wrapper failed'}
  $baselineWrapper+=$timer.Elapsed.TotalMilliseconds
 }
 $managed=@(((& $test coordination-benchmark (Join-Path $destination 'managed') $worker|Select-Object -Last 1)|ConvertFrom-Json)|ForEach-Object {[double]$_})
 if($LASTEXITCODE -ne 0){throw 'Managed admission failed'}
 function Median($values){$ordered=@($values|Sort-Object);return $ordered[[int][Math]::Floor($ordered.Count/2)]}
 $passed=(Median $managed) -le (Median $baseline) -and (Median $managed) -le (Median $baselineWrapper)
 [ordered]@{passed=$passed;scope='Coordination admission/release overhead only. No model-token or whole-task throughput claim.';repetitions=11;baselineManualMs=$baseline;baselineWrapperIncludingNoopChildMs=$baselineWrapper;managedMs=$managed;baselineMedianMs=(Median $baseline);wrapperMedianMs=(Median $baselineWrapper);managedMedianMs=(Median $managed);managedAgentRegistryActions=0;manualBaselineAgentRegistryActionsPerEffect=2;automatedBaselineAgentRegistryActions=0;automatedBaselineCallerResourceDeclarations=1;managedCallerResourceDeclarations=0;registrySha256=(Get-FileHash $registry).Hash;wrapperSha256=(Get-FileHash $wrapper).Hash;coreSha256=(Get-FileHash (Join-Path (Split-Path $test) 'AgentOS.Core.dll')).Hash;os=[Environment]::OSVersion.ToString();at=[DateTimeOffset]::UtcNow.ToString('o')}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $destination 'result.json') -Encoding UTF8
 if(!$passed){throw 'Preregistered coordination overhead gate failed'}
 Get-Content (Join-Path $destination 'result.json')
} finally {$env:RESOURCE_CHECKIN_REGISTRY=$previous}
