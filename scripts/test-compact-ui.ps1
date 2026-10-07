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
 function RequireId([string]$id,[System.Windows.Automation.AutomationElement]$scope=$window){$control=FindId $id $scope;if(!$control){throw "Missing UIA control: $id"};return $control}
 function InvokeId([string]$id,[System.Windows.Automation.AutomationElement]$scope=$window){(RequireId $id $scope).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()}
 foreach($id in @('WorkList','TaskRow_preview-completed','TaskRow_preview-child','TaskRow_preview-running','TaskRow_preview-waiting','TaskRow_preview-failed','TaskRow_preview-private','TaskRow_preview-stale','TaskRow_preview-unknown','PreviewUpdate','PreviewReset','Settings','TaskPrompt','StartTask')){RequireId $id | Out-Null}
 if((FindId 'NavWorkspace') -or (FindId 'NavDecisions')){throw 'Unexpected navigation sidebar.'}
 if((RequireId 'StartTask').Current.IsEnabled){throw 'Preview task mutation is enabled.'}
 $completed=RequireId 'TaskRow_preview-completed'
 $expander=RequireId 'CodexReport' $completed
 $expander.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
 InvokeId 'OpenFollowUp' $completed
 $follow=RequireId 'FollowUpPrompt' $completed
 $follow.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Keep follow-up draft')
 $draft=RequireId 'TaskPrompt';$draft.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Keep task draft')
 InvokeId 'PreviewUpdate';Start-Sleep -Milliseconds 200
 if((RequireId 'TaskPrompt').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'Keep task draft'){throw 'Task draft was lost after update.'}
 $completed=RequireId 'TaskRow_preview-completed'
 if((RequireId 'FollowUpPrompt' $completed).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'Keep follow-up draft'){throw 'Follow-up draft was lost.'}
 if((RequireId 'CodexReport' $completed).GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded){throw 'Result expansion was lost.'}
 if((RequireId 'SendFollowUp' $completed).Current.IsEnabled){throw 'Preview follow-up mutation is enabled.'}
 foreach($pass in 1..2){
  InvokeId 'Settings';Start-Sleep -Milliseconds 200
  $close=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Close'))
  if(!$close){throw 'Settings close button missing.'}
  $close.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();Start-Sleep -Milliseconds 200
 }
 & (Join-Path $PSScriptRoot 'inspect-ui.ps1') -TargetProcessId $owned.Id -Screenshot (Join-Path $Output 'wide.png') -Width 1040 -Height 760 | Out-Null
 InvokeId 'TaskDetails' $completed
 & (Join-Path $PSScriptRoot 'inspect-ui.ps1') -TargetProcessId $owned.Id -Screenshot (Join-Path $Output 'narrow.png') -Width 640 -Height 760 | Out-Null
 if(!(FindId 'BackToTasks')){throw 'Detail Back is missing.'}
 $bounds=$window.Current.BoundingRectangle
 $visible=$window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
 foreach($element in $visible){$c=$element.Current;if(!$c.IsOffscreen -and $c.BoundingRectangle.Width -gt 0 -and $c.BoundingRectangle.Right -gt $bounds.Right+3){throw "Horizontal overflow: $($c.AutomationId) $($c.Name)"}}
 InvokeId 'BackToTasks';Start-Sleep -Milliseconds 200
 if(!(FindId 'WorkList')){throw 'Task map did not return.'}
 InvokeId 'PreviewReset';Start-Sleep -Milliseconds 200
 if(!(FindId 'WorkList')){throw 'Empty state lost task map.'}
 Write-Output 'Preview UIA smoke checks passed. Review screenshots for visual overflow.'
} finally {if($owned -and !$owned.HasExited){Stop-Process -Id $owned.Id -Force}}
