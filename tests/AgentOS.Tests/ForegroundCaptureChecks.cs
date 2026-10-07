using AgentOS.Core;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AgentOS.Tests;

// Set AGENTOS_FOREGROUND_CHECKS=1 for native checks against this process's fixture only.
public static class ForegroundCaptureChecks
{
 [ModuleInitializer]
 public static void RunWhenRequested()
 {
  CheckCropAndPngLimits();
  CheckVirtualScreenMath();
  if(Environment.GetEnvironmentVariable("AGENTOS_FOREGROUND_CHECKS")=="1")RunNative();
 }
 private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
 private static void CheckCropAndPngLimits()
 {
  var identity=new ForegroundIdentity(0,0,DateTimeOffset.UnixEpoch,"fixture","fixture");
  var bounds=new CaptureRect(-1920,0,200,100);
  var context=new ForegroundContext(Guid.NewGuid(),DateTimeOffset.UtcNow,identity,"",new byte[]{1},bounds);
  foreach(var crop in new[]{new CaptureRect(int.MaxValue,0,1,1),new CaptureRect(0,int.MaxValue,1,1),new CaptureRect(0,0,int.MaxValue,1),new CaptureRect(0,0,1,int.MaxValue),new CaptureRect(-1,0,1,1),new CaptureRect(0,0,0,1)})
  {
   try{context.CropScreenshot(crop);throw new Exception("Invalid crop accepted.");}
   catch(ArgumentOutOfRangeException){}
  }
  var edge=new ForegroundContext(Guid.NewGuid(),DateTimeOffset.UtcNow,identity,"",new byte[]{1},new CaptureRect(int.MaxValue,0,200,100));
  try{edge.CropScreenshot(new CaptureRect(1,0,1,1));throw new Exception("Absolute crop origin overflow accepted.");}catch(ArgumentOutOfRangeException){}
  try{_ = new ForegroundContext(Guid.NewGuid(),DateTimeOffset.UtcNow,identity,"",new byte[8_000_001],bounds);throw new Exception("Oversized PNG accepted.");}
  catch(ArgumentOutOfRangeException){}
  try{_ = new ForegroundContext(Guid.NewGuid(),DateTimeOffset.UtcNow,identity,new string('x',16001),null,null);throw new Exception("Oversized text accepted.");}
  catch(ArgumentOutOfRangeException){}
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
 private static void RunNative()
 {
  if(!OperatingSystem.IsWindows())return;
  var hwnd=CreateWindowEx(0,"STATIC","AgentOS foreground fixture",0x10CF0000,120,120,320,240,0,0,0,0);
  Check(hwnd!=0,"Fixture window creation failed.");
  try
  {
   var read=typeof(ForegroundCapture).GetMethod("ReadStamp",BindingFlags.NonPublic|BindingFlags.Static)!;
   var same=typeof(ForegroundCapture).GetMethod("SameWindow",BindingFlags.NonPublic|BindingFlags.Static)!;
   var stamp=read.Invoke(null,[hwnd]);Check(stamp is not null,"Owned window identity unavailable.");
   Check((bool)same.Invoke(null,[stamp])!,"Owned window identity changed unexpectedly.");
   using(var bad=new CaptureHotKey(0))Check(bad.Status==HotKeyStatus.Unavailable,"Invalid hotkey target registered.");
   using(var first=new CaptureHotKey(hwnd,0xA605,0x0007,0x87))
   {
    Check(first.Status==HotKeyStatus.Registered,"Owned hotkey failed to register.");
    using var conflict=new CaptureHotKey(hwnd,0xA605,0x0007,0x87);
    Check(conflict.Status==HotKeyStatus.Conflict,"Duplicate hotkey conflict was missed.");
   }
   using(var again=new CaptureHotKey(hwnd,0xA605,0x0007,0x87))Check(again.Status==HotKeyStatus.Registered,"Hotkey registration was not cleaned up.");
   using(var tracker=new ForegroundCapture())
   {
    typeof(ForegroundCapture).GetField("_last",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(tracker,stamp);
    Check(tracker.LastExternalHandle==hwnd,"Fixture identity not retained.");
    var ctor=stamp!.GetType().GetConstructors(BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance).Single(c=>c.GetParameters().Length==6);
    var stale=ctor.Invoke([hwnd,uint.MaxValue,DateTimeOffset.UnixEpoch,0u,"STATIC","AgentOS foreground fixture"]);
    typeof(ForegroundCapture).GetField("_last",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(tracker,stale);
    Check(tracker.CaptureLastExternal().Status==CaptureStatus.Unavailable,"Unreadable identity did not fail unavailable.");
    typeof(ForegroundCapture).GetField("_last",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(tracker,stamp);
    typeof(ForegroundCapture).GetMethod("Forget",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(tracker,[hwnd]);
    Check(tracker.LastExternalHandle==0,"Exact destroy event did not clear retained identity.");
   }
   Check(DestroyWindow(hwnd),"Fixture destruction failed.");hwnd=0;
   Check(!(bool)same.Invoke(null,[stamp])!,"Destroyed window retained its identity.");
  }
  finally{if(hwnd!=0)DestroyWindow(hwnd);}
  Console.WriteLine("PASS foreground capture checks (owned fixture; live external capture unverified)");
 }
 [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern nint CreateWindowEx(uint exStyle,string className,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
 [DllImport("user32.dll")]private static extern bool DestroyWindow(nint hwnd);
}
