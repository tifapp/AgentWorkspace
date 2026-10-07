param([string]$Screenshot = 'artifacts/desktop.png', [string]$Invoke = '', [string]$SetControl = '', [string]$Value = '', [switch]$Tree, [int]$TargetProcessId = 0, [int]$Width = 0, [int]$Height = 0)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing,System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WindowProbe {
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int mode);
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd,int x,int y,int width,int height,bool repaint);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
 public struct Rect { public int Left,Top,Right,Bottom; }
}
'@
$process=if($TargetProcessId) { Get-Process -Id $TargetProcessId } else { Get-Process AgentOS -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1 }
if(!$process -or $process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'The agent-os window is not ready.' }
if($Width -gt 0 -and $Height -gt 0) { [WindowProbe]::MoveWindow($process.MainWindowHandle,30,30,$Width,$Height,$true) | Out-Null }
$window=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
if($SetControl) {
 $control=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$SetControl))
 if(!$control) { throw "Control not found: $SetControl" }
 $control.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
}
if($Invoke) {
 $control=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$Invoke))
 if(!$control) { throw "Control not found: $Invoke" }
 $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
if($Tree) {
 $all=$window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
 foreach($element in $all) { $c=$element.Current; if($c.Name -or $c.AutomationId) { [pscustomobject]@{Type=$c.ControlType.ProgrammaticName;Id=$c.AutomationId;Name=$c.Name;Enabled=$c.IsEnabled;Offscreen=$c.IsOffscreen} } }
}
if($Screenshot) {
 [WindowProbe]::ShowWindow($process.MainWindowHandle,9) | Out-Null
 [WindowProbe]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
 Start-Sleep -Milliseconds 400
 $rect=New-Object WindowProbe+Rect
 [WindowProbe]::GetWindowRect($process.MainWindowHandle,[ref]$rect) | Out-Null
 $bitmap=New-Object Drawing.Bitmap ($rect.Right-$rect.Left),($rect.Bottom-$rect.Top)
 $graphics=[Drawing.Graphics]::FromImage($bitmap)
 $graphics.CopyFromScreen($rect.Left,$rect.Top,0,0,$bitmap.Size)
 $target=[IO.Path]::GetFullPath($Screenshot)
 [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
 $bitmap.Save($target,[Drawing.Imaging.ImageFormat]::Png)
 $graphics.Dispose(); $bitmap.Dispose()
 Write-Output "Screenshot: $target"
}
