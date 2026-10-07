param([string]$App = 'artifacts/release/agent-os-retirement-win-x64/AgentOS.exe', [switch]$Live, [string]$Output = 'artifacts/verification/desktop', [switch]$Resume)
$ErrorActionPreference='Stop'
if(!$Live) { throw 'Use -Live to run real Codex tasks through the desktop UI. This consumes your normal Codex usage.' }
$repo=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repo
$outputPath=[IO.Path]::GetFullPath((Join-Path $repo $Output))
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$sdk=Join-Path $repo '.tools/dotnet/dotnet.exe'
$cli=Join-Path $repo 'src/AgentOS.Cli/bin/Release/net10.0-windows10.0.19041.0/AgentOS.Cli.dll'
if($Resume) {
 $previous=Get-Content -LiteralPath (Join-Path $outputPath 'result.json') -Raw | ConvertFrom-Json
 if(!@($previous.checks | Where-Object { $_ -like 'Canceled an app-launched Codex task*' }).Count) { throw 'Resume requires the desktop workflow through cancellation to have completed.' }
 $project=$previous.project; $data=$previous.data
} else {
 $project=(& $sdk $cli practice (Join-Path $outputPath 'projects') | Select-Object -Last 1)
 if($LASTEXITCODE -ne 0) { throw 'Could not create the real test project.' }
 $data=Join-Path $outputPath 'state'
}
$arguments='--project "'+$project+'" --data-root "'+$data+'"'
$appPath=(Resolve-Path -LiteralPath $App).Path
$process=Start-Process -FilePath $appPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
$checks=[Collections.Generic.List[string]]::new()
if($Resume) { foreach($check in $previous.checks) { $checks.Add($check) } }
function Wait-Until([scriptblock]$Condition,[int]$Seconds=30,[switch]$AllowExit) {
 $deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
 while(!( & $Condition )) {
  $process.Refresh()
  if(!$AllowExit -and $process.HasExited) { throw "Desktop exited before the expected state (code $($process.ExitCode))." }
  if([DateTime]::UtcNow -gt $deadline) { throw 'Timed out waiting for desktop workflow state.' }
  Start-Sleep -Milliseconds 200
 }
}
function Window {
 $process.Refresh()
 if($process.HasExited) { throw "Desktop exited unexpectedly: $($process.ExitCode)" }
 $handle=$process.MainWindowHandle
 if($handle -eq [IntPtr]::Zero) { return $null }
 return [Windows.Automation.AutomationElement]::FromHandle($handle)
}
function Control([string]$Id) {
 $w=Window
 if(!$w) { return $null }
 return $w.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
}
function Click([string]$Id) {
 Wait-Until { $c=Control $Id; $c -and $c.Current.IsEnabled }
 (Control $Id).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function State {
 foreach($file in (Get-ChildItem -LiteralPath $data -Recurse -Filter state.json -ErrorAction SilentlyContinue)) {
  $saved=Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
  if($saved.ProjectPath -eq $project) { return $saved }
 }
 return $null
}
function Shot([string]$Name,[int]$Width=1280,[int]$Height=900) { & "$PSScriptRoot/inspect-ui.ps1" -TargetProcessId $process.Id -Screenshot (Join-Path $outputPath $Name) -Width $Width -Height $Height }
try {
 if(!$Resume) {
 Wait-Until { $s=State; $s -and (Control 'SaveSetup') }
 Shot '01-setup.png'
 Click 'SaveSetup'; Wait-Until { Control 'RunWalkthrough' }
 $checks.Add('Opened a real local Git project and saved validation through the app.')
 Click 'RunWalkthrough'
 Wait-Until { $s=State; $s.Work.Count -ge 3 -and ($s.Work.Status -contains 'Running') } 60
 Shot '02-running.png'
 Wait-Until {
  $s=State
  if(@($s.Work | Where-Object { $_.Status -in 'Failed','Unknown','Canceled' }).Count) { throw ('A work unit failed: '+(($s.Work | Where-Object { $_.Status -in 'Failed','Unknown','Canceled' }).Detail -join '; ')) }
  $s.Work.Count -ge 4 -and $s.Work[-1].Status -in 'Completed','Stale'
 } 360
 $s=State
 if(@($s.Work | Where-Object Status -eq 'Completed').Count -ne 3 -or @($s.Work | Where-Object Status -eq 'Stale').Count -ne 1) { throw ('Unexpected Codex outcomes: '+($s.Work.Status -join ', ')) }
 $checks.Add('Three real Codex work units: contention, stale refusal, independent integration, then a successful Codex revision.')
 Shot '03-results.png'
 $revision=$s.Work | Where-Object { $_.ParentId -and $_.Status -eq 'Completed' } | Select-Object -First 1
 if(!$revision.CodexReport) { throw 'The successful Codex revision has no retained final report.' }
 $rows=(Control 'WorkList').FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
 $revisionRow=$rows | Where-Object { $_.Current.Name.StartsWith('Revision') } | Select-Object -First 1
 if(!$revisionRow) { throw 'The task map did not expose the revision relationship.' }
 $revisionRow.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
 Wait-Until { (Control 'OriginalTask') -and (Control 'CodexReport') }
 Shot '03a-task-thread.png'
 Click 'OriginalTask'; Wait-Until { Control 'RevisionTask' }
 Click 'RevisionTask'; Wait-Until { Control 'OriginalTask' }
 $checks.Add('Task map exposes revision lineage, preserves the stale original, and retains the actual Codex report.')
 Click 'NavDecisions'; Wait-Until { Control 'ApproveDecision' }
 Shot '04-decision.png'
 Click 'ApproveDecision'; Wait-Until { (State).Decisions[0].Status -eq 'Completed' }
 $decision=(State).Decisions[0]
 $tag=& git -C $project rev-parse $decision.Destination
 if($tag -ne $decision.Candidate) { throw 'The release tag does not match the approved candidate.' }
 $checks.Add('Reviewed and approved one exact local release; verified the actual Git tag.')
 Shot '05-approved.png'
 Click 'NavEvidence'; Shot '06-evidence.png'
 Click 'NavWorkspace'; Shot '07-narrow.png' 900 760
 $checks.Add('Inspected work, decisions, evidence, and a 900 by 760 layout.')
 (Control 'TaskPrompt').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('Read README.md, then run a PowerShell command that sleeps for 60 seconds. Do not edit files. This task is for cancellation verification.')
 Click 'StartTask'; Wait-Until { $s=State; $s.Work[-1].Status -eq 'Running' } 60
 Click 'CancelTask'; Wait-Until { (State).Work[-1].Status -eq 'Canceled' } 30
 $checks.Add('Canceled an app-launched Codex task and observed the saved Canceled outcome.')
 Shot '08-canceled.png' 1280 900
 $process.CloseMainWindow() | Out-Null
 Wait-Until { $process.Refresh(); $process.HasExited } 30 -AllowExit
 $process=Start-Process -FilePath $appPath -WindowStyle Hidden -PassThru
 }
 if($Resume) {
  Wait-Until { Control 'TaskPrompt' } 30
  $process.CloseMainWindow() | Out-Null
  Wait-Until { $process.Refresh(); $process.HasExited } 30 -AllowExit
  $process=Start-Process -FilePath $appPath -WindowStyle Hidden -PassThru
 }
 Wait-Until { Control 'TaskPrompt' } 30
 Wait-Until { $s=State; $s.Decisions[0].Status -eq 'Completed' -and $s.Work[-1].Status -eq 'Canceled' } 30
 Click 'NavDecisions'
 Wait-Until { (Window).FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'Completed')) } 30
 Shot '09-recovered.png'
 $checks.Add('Restarted the desktop app without arguments and verified the project, evidence location, release decision and cancellation were retained.')
 $settingsText=(& git -C $project show 'agent-os/integrated:settings.json') -join "`n"
 $settingsResult=$settingsText | ConvertFrom-Json
 if($settingsResult.retries -ne 2 -or $settingsResult.cancellation -ne $true) { throw 'The integrated settings do not satisfy both work units.' }
 $readme=(& git -C $project show 'agent-os/integrated:README.md') -join "`n"
 if($readme -notmatch 'Local validation') { throw 'The independent documentation change is absent.' }
 $checks.Add('Verified actual combined Git contents: retries 2, cancellation true, and the independent documentation section.')
 [ordered]@{passed=$true;at=[DateTimeOffset]::UtcNow.ToString('o');project=$project;data=$data;app=$appPath;appSha256=(Get-FileHash -LiteralPath $appPath -Algorithm SHA256).Hash;uiSha256=(Get-FileHash -LiteralPath (Join-Path (Split-Path $appPath) 'AgentOS.dll') -Algorithm SHA256).Hash;coreSha256=(Get-FileHash -LiteralPath (Join-Path (Split-Path $appPath) 'AgentOS.Core.dll') -Algorithm SHA256).Hash;checks=$checks;state=(State)} | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $outputPath 'result.json') -Encoding UTF8
 Write-Output 'Desktop workflow passed.'
} catch {
 [ordered]@{passed=$false;error=$_.ToString();checks=$checks;project=$project;data=$data} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $outputPath 'result.json') -Encoding UTF8
 throw
} finally {
 if(!$process.HasExited) { $process.CloseMainWindow() | Out-Null }
}
