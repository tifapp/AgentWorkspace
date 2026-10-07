param([Parameter(Mandatory)][string]$App,[Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
if(!(Test-Path -LiteralPath $App)){throw "App not found: $App"}
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$owned=Start-Process -FilePath (Resolve-Path $App) -ArgumentList '--ui-preview' -PassThru -WindowStyle Hidden
try {
 $window=$null
 for($i=0;$i -lt 80;$i++){Start-Sleep -Milliseconds 250;$owned.Refresh();if($owned.MainWindowHandle -ne [IntPtr]::Zero){$window=[System.Windows.Automation.AutomationElement]::FromHandle($owned.MainWindowHandle);break}}
 if(!$window){throw 'Preview window did not become available.'}
 function FindId([string]$id,[System.Windows.Automation.AutomationElement]$scope=$window){$scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
 function RequireId([string]$id,[System.Windows.Automation.AutomationElement]$scope=$window){
  $clock=[System.Diagnostics.Stopwatch]::StartNew()
  do {
   $control=FindId $id $scope
   if($control){return $control}
   Start-Sleep -Milliseconds 75
  } while($clock.Elapsed.TotalSeconds -lt 5)
  throw "Missing UIA control: $id"
 }
 function InvokeId([string]$id,[System.Windows.Automation.AutomationElement]$scope=$window){(RequireId $id $scope).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()}
 function CompletedRowInView([string]$childId) {
  $list=RequireId 'WorkList'
  $scroll=$list.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
  if($scroll.Current.VerticallyScrollable){$scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll,100)}
  $row=RequireId 'TaskRow_preview-completed' $list
  $child=RequireId $childId $row
  $clock=[System.Diagnostics.Stopwatch]::StartNew()
  do {
   $bounds=$child.Current.BoundingRectangle
   if($bounds.Width -gt 0 -and $bounds.Height -gt 0){return $row}
   Start-Sleep -Milliseconds 75
  } while($clock.Elapsed.TotalSeconds -lt 5)
  throw "Completed sample control did not lay out: $childId"
 }
 function FindDialogClose([System.Windows.Automation.AutomationElement]$dialog){
  $close=FindId 'CloseButton' $dialog
  if($close){return $close}
  $condition=[System.Windows.Automation.AndCondition]::new(
   [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Close'),
   [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button))
  return $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
 }
 function FindSettingsDialog {
  $title=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Project settings · UI preview'))
  if(!$title){return $null}
  $candidate=$title
  $walker=[System.Windows.Automation.TreeWalker]::ControlViewWalker
  while($candidate -and !$candidate.Equals($window)){
   $close=FindDialogClose $candidate
   if($close){return $candidate}
   $candidate=$walker.GetParent($candidate)
  }
  return $null
 }
 function WaitSettingsDialog([bool]$open){
  $clock=[System.Diagnostics.Stopwatch]::StartNew()
  do {
   $dialog=FindSettingsDialog
   if([bool]$dialog -eq $open){return $dialog}
   Start-Sleep -Milliseconds 75
  } while($clock.Elapsed.TotalSeconds -lt 5)
  if($open){throw 'Settings dialog did not appear.'}
  throw 'Settings dialog did not close.'
 }
 foreach($id in @('WorkList','TaskRow_preview-completed','TaskRow_preview-child','TaskRow_preview-running','TaskRow_preview-waiting','TaskRow_preview-failed','TaskRow_preview-private','TaskRow_preview-stale','TaskRow_preview-unknown','PreviewUpdate','PreviewReset','Settings','TaskPrompt','StartTask')){RequireId $id | Out-Null}
 if((FindId 'NavWorkspace') -or (FindId 'NavDecisions')){throw 'Unexpected navigation sidebar.'}
 if((RequireId 'StartTask').Current.IsEnabled){throw 'Preview task mutation is enabled.'}
 $completed=CompletedRowInView 'CodexReport'
 $expander=RequireId 'CodexReport' $completed
 $expander.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
 $completed=CompletedRowInView 'OpenFollowUp'
 InvokeId 'OpenFollowUp' $completed
 $completed=CompletedRowInView 'FollowUpPrompt'
 $follow=RequireId 'FollowUpPrompt' $completed
 $follow.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Keep follow-up draft')
 $draft=RequireId 'TaskPrompt';$draft.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Keep task draft')
 InvokeId 'PreviewUpdate';Start-Sleep -Milliseconds 200
 if((RequireId 'TaskPrompt').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'Keep task draft'){throw 'Task draft was lost after update.'}
 $completed=CompletedRowInView 'FollowUpPrompt'
 if((RequireId 'FollowUpPrompt' $completed).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'Keep follow-up draft'){throw 'Follow-up draft was lost.'}
 if((RequireId 'CodexReport' $completed).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded){throw 'Result expansion was lost.'}
 if((RequireId 'SendFollowUp' $completed).Current.IsEnabled){throw 'Preview follow-up mutation is enabled.'}
 foreach($pass in 1..2){
  InvokeId 'Settings'
  $dialog=WaitSettingsDialog $true
  (FindDialogClose $dialog).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  WaitSettingsDialog $false | Out-Null
 }
 & (Join-Path $PSScriptRoot 'inspect-ui.ps1') -TargetProcessId $owned.Id -Screenshot (Join-Path $Output 'wide.png') -Width 1040 -Height 760 | Out-Null
 $completed=CompletedRowInView 'TaskDetails'
 InvokeId 'TaskDetails' $completed
 & (Join-Path $PSScriptRoot 'inspect-ui.ps1') -TargetProcessId $owned.Id -Screenshot (Join-Path $Output 'narrow.png') -Width 640 -Height 760 | Out-Null
 if(!(FindId 'BackToTasks')){throw 'Detail Back is missing.'}
 $bounds=$window.Current.BoundingRectangle
 $visible=$window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
 foreach($element in $visible){$c=$element.Current;if(!$c.IsOffscreen -and $c.BoundingRectangle.Width -gt 0 -and $c.BoundingRectangle.Right -gt $bounds.Right+3){throw "Horizontal overflow: $($c.AutomationId) $($c.Name)"}}
 InvokeId 'BackToTasks';Start-Sleep -Milliseconds 200
 RequireId 'WorkList' | Out-Null
 InvokeId 'PreviewReset';Start-Sleep -Milliseconds 200
 RequireId 'WorkList' | Out-Null
 Write-Output 'Preview UIA smoke checks passed. Review screenshots for visual overflow.'
} finally {if($owned -and !$owned.HasExited){Stop-Process -Id $owned.Id -Force}}
