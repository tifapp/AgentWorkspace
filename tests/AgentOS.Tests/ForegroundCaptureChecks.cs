using AgentOS.Core;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;

namespace AgentOS.Tests;

public static class ForegroundCaptureChecks
{
 private static int _count;
 public static async Task RunAsync(string root)
 {
  _count=0; Check(!string.IsNullOrWhiteSpace(root),"Missing artifacts root.");
  CheckCropAndPngLimits(); CheckVirtualScreenMath();
  using(var bad=new CaptureHotKey(0))Check(bad.Status==HotKeyStatus.Unavailable,"Invalid hotkey target registered.");
  if(Environment.GetEnvironmentVariable("AGENTOS_OWNED_CAPTURE_FIXTURE")=="1")await RunNativeAsync(root);
  else Console.WriteLine("SKIP foreground live subfixture (set AGENTOS_OWNED_CAPTURE_FIXTURE=1)");
  Console.WriteLine($"PASS foreground capture checks ({_count} assertions)");
 }
 private static void Check(bool condition,string message){if(!condition)throw new Exception(message);_count++;}
 private static void CheckCropAndPngLimits()
 {
  var identity=new ForegroundIdentity(0,0,DateTimeOffset.UnixEpoch,"fixture","fixture");
  var bounds=new CaptureRect(-1920,0,200,100);
  var context=new ForegroundContext(Guid.NewGuid(),DateTimeOffset.UtcNow,identity,"",new byte[]{1},bounds);
  foreach(var crop in new[]{new CaptureRect(int.MaxValue,0,1,1),new CaptureRect(0,int.MaxValue,1,1),new CaptureRect(0,0,int.MaxValue,1),new CaptureRect(0,0,1,int.MaxValue),new CaptureRect(-1,0,1,1),new CaptureRect(0,0,0,1)})
  {
   try{context.CropScreenshot(crop);throw new Exception("Invalid crop accepted.");}
   catch(ArgumentOutOfRangeException){_count++;}
  }
  var edge=new ForegroundContext(Guid.NewGuid(),DateTimeOffset.UtcNow,identity,"",new byte[]{1},new CaptureRect(int.MaxValue,0,200,100));
  try{edge.CropScreenshot(new CaptureRect(1,0,1,1));throw new Exception("Absolute crop origin overflow accepted.");}catch(ArgumentOutOfRangeException){_count++;}
  try{_ = new ForegroundContext(Guid.NewGuid(),DateTimeOffset.UtcNow,identity,"",new byte[8_000_001],bounds);throw new Exception("Oversized PNG accepted.");}
  catch(ArgumentOutOfRangeException){_count++;}
  try{_ = new ForegroundContext(Guid.NewGuid(),DateTimeOffset.UtcNow,identity,new string('x',16001),null,null);throw new Exception("Oversized text accepted.");}
  catch(ArgumentOutOfRangeException){_count++;}
  var copy=context.ScreenshotPng!;copy[0]=99;Check(context.ScreenshotPng![0]==1,"PNG bytes were mutable from preview.");
 }
 private static void CheckVirtualScreenMath()
 {
  bool Fits(CaptureRect rect,CaptureRect screen)=>ForegroundCapture.FitsVirtualScreen(rect,screen);
  var virtualScreen=new CaptureRect(-1920,-1080,3840,2160);
  Check(Fits(new(-1919,-100,200,100),virtualScreen),"Negative monitor coordinates rejected.");
  Check(Fits(new(-1920,-1080,3840,2160),virtualScreen),"Virtual screen edge rejected.");
  Check(!Fits(new(-1921,0,10,10),virtualScreen),"Outside left edge accepted.");
  Check(!Fits(new(1900,0,100,10),virtualScreen),"Outside right edge accepted.");
  Check(!Fits(new(int.MaxValue,0,int.MaxValue,10),virtualScreen),"Overflowing bounds accepted.");
  Check(!Fits(new(0,0,0,10),virtualScreen),"Empty bounds accepted.");
 }
 private static async Task RunNativeAsync(string root)
 {
  Check(OperatingSystem.IsWindows(),"Native fixture requires Windows.");
  Directory.CreateDirectory(root);
  var ready=Path.Combine(root,"fixture.ready");
  var script=Path.Combine(root,"fixture.ps1");
  if(File.Exists(ready))File.Delete(ready);
  await File.WriteAllTextAsync(script,"""
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$form = [System.Windows.Forms.Form]::new()
$form.Text = 'AgentOS owned capture fixture'
$form.StartPosition = 'Manual'
$form.Location = [System.Drawing.Point]::new(80,80)
$form.Size = [System.Drawing.Size]::new(300,220)
$form.TopMost = $true
$label = [System.Windows.Forms.Label]::new()
$label.Text = 'AgentOS visible fixture label'
$label.AutoSize = $true
$label.Location = [System.Drawing.Point]::new(20,20)
$form.Controls.Add($label)
$form.Add_Shown({ $form.Activate(); [IO.File]::WriteAllText($env:AGENTOS_FIXTURE_READY, "$PID,$($form.Handle.ToInt64())") })
[System.Windows.Forms.Application]::Run($form)
""");
  var previous=GetForegroundWindow();
  var start=new ProcessStartInfo("powershell.exe"){UseShellExecute=false};
  start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-STA");
  start.ArgumentList.Add("-File");start.ArgumentList.Add(script);
  start.Environment["AGENTOS_FIXTURE_READY"]=ready;
  using var child=Process.Start(start)??throw new Exception("Fixture process failed to start.");
  nint hwnd=0;
  try
  {
   var timer=Stopwatch.StartNew();
   while(!File.Exists(ready)&&!child.HasExited&&timer.Elapsed<TimeSpan.FromSeconds(10))await Task.Delay(50);
   Check(File.Exists(ready),"Fixture did not report its window.");
   var parts=(await File.ReadAllTextAsync(ready)).Split(',');
   Check(parts.Length==2&&int.Parse(parts[0])==child.Id,"Fixture PID handshake mismatch.");
   hwnd=(nint)long.Parse(parts[1]);
   uint pid=0;
   Check(hwnd!=0&&GetWindowThreadProcessId(hwnd,out pid)!=0&&pid==child.Id,"Fixture HWND/PID mismatch.");
   timer.Restart();
   while(GetForegroundWindow()!=hwnd&&timer.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(50);
   Check(GetForegroundWindow()==hwnd,"Owned fixture is not foreground.");
   using(var tracker=new ForegroundCapture())
   {
    Check(tracker.LastExternalHandle==hwnd,"Retained window is not owned fixture.");
    var result=tracker.CaptureLastExternal(new CaptureRect(20,20,100,80));
    Check(result.Status==CaptureStatus.Captured,"Fixture capture failed: "+result.Status+" "+result.Detail);
    var context=result.Context!;
    Check(context.Window.Handle==hwnd&&context.Window.ProcessId==pid,"Capture fell back to another window.");
    Check(context.ScreenshotBounds is {Width:100,Height:80},"Crop bounds differ from 100x80.");
    var png=context.ScreenshotPng!;
    Check(png.Length>=24&&png.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),"Capture is not PNG.");
    Check(Encoding.ASCII.GetString(png,12,4)=="IHDR"&&BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16,4))==100&&BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20,4))==80,"PNG IHDR crop is not 100x80.");
    Check(context.Window.Title.Contains("AgentOS owned capture fixture"),"Fixture title missing.");
    Check(context.VisibleText.Contains("AgentOS visible fixture label")||result.Detail=="Visible text unavailable.","Visible label replaced by unrelated text.");
    Check(ShowWindow(hwnd,0),"Owned fixture was not visible before hiding.");
    var hidden=tracker.CaptureLastExternal();
    Check(hidden.Status==CaptureStatus.Protected&&hidden.Context is null,"Hidden owned foreground fell back to another window.");
   }
   CheckHotKeyLifecycle();
  }
  finally
  {
   if(hwnd!=0&&GetForegroundWindow()==hwnd&&previous!=0)SetForegroundWindow(previous);
   if(!child.HasExited)
   {
    child.Kill();
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try{await child.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){}
   }
  }
 }
 private static void CheckHotKeyLifecycle()
 {
  var hwnd=CreateWindowEx(0,"STATIC","AgentOS hotkey fixture",0,0,0,1,1,0,0,0,0);
  Check(hwnd!=0,"Hotkey fixture window creation failed.");
  try
  {
   using(var first=new CaptureHotKey(hwnd,0xA605,0x0003,0x87))
   {
    Check(first.Status==HotKeyStatus.Registered,"Ctrl+Alt+F24 registration failed.");
    using var conflict=new CaptureHotKey(hwnd,0xA605,0x0003,0x87);
    Check(conflict.Status==HotKeyStatus.Conflict,"Duplicate hotkey conflict missed.");
   }
   using var rebound=new CaptureHotKey(hwnd,0xA605,0x0003,0x87);
   Check(rebound.Status==HotKeyStatus.Registered,"Disposed hotkey did not rebind.");
  }
  finally{DestroyWindow(hwnd);}
 }
 [DllImport("user32.dll")]private static extern bool ShowWindow(nint hwnd,int command);
 [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
 [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint hwnd);
 [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
 [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern nint CreateWindowEx(uint exStyle,string className,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
 [DllImport("user32.dll")]private static extern bool DestroyWindow(nint hwnd);
}








