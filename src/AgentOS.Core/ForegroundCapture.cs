using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
namespace AgentOS.Core;
public enum CaptureStatus { Captured, NoExternalWindow, Closed, Minimized, Protected, Elevated, Unavailable, TooLarge }
public sealed record ForegroundIdentity(nint Handle, uint ProcessId, DateTimeOffset ProcessCreated, string App, string Title);
public sealed record CaptureRect(int X,int Y,int Width,int Height);
public sealed record CaptureResult(CaptureStatus Status, ForegroundContext? Context=null, string? Detail=null);
public sealed class ForegroundContext
{
 public Guid Id {get;} public DateTimeOffset CapturedAt {get;} public ForegroundIdentity Window {get;} public string VisibleText {get;} private readonly byte[]? _screenshotPng; public byte[]? ScreenshotPng => _screenshotPng?.ToArray(); public CaptureRect? ScreenshotBounds {get;}
 public string ManualTitle => (string.IsNullOrWhiteSpace(Window.Title) ? Window.App : Window.Title).Length <= 120 ? (string.IsNullOrWhiteSpace(Window.Title) ? Window.App : Window.Title) : (string.IsNullOrWhiteSpace(Window.Title) ? Window.App : Window.Title)[..120];
 public ForegroundContext(Guid id,DateTimeOffset at,ForegroundIdentity window,string text,byte[]? png,CaptureRect? bounds)
 { if(text.Length>16000||png?.Length>8_000_000) throw new ArgumentOutOfRangeException(); Id=id;CapturedAt=at;Window=window;VisibleText=text;_screenshotPng=png?.ToArray();ScreenshotBounds=bounds; }
 public ForegroundContext WithoutScreenshot()=>new(Id,CapturedAt,Window,VisibleText,null,null);
 public ForegroundContext CropScreenshot(CaptureRect crop)
 {if(_screenshotPng is null||ScreenshotBounds is null||crop.X<0||crop.Y<0||crop.Width<1||crop.Height<1||(long)crop.X+crop.Width>ScreenshotBounds.Width||(long)crop.Y+crop.Height>ScreenshotBounds.Height||(long)ScreenshotBounds.X+crop.X>int.MaxValue||(long)ScreenshotBounds.Y+crop.Y>int.MaxValue)throw new ArgumentOutOfRangeException(nameof(crop));var bytes=ForegroundCapture.CropPng(_screenshotPng,crop);return new(Id,CapturedAt,Window,VisibleText,bytes,new CaptureRect(checked(ScreenshotBounds.X+crop.X),checked(ScreenshotBounds.Y+crop.Y),crop.Width,crop.Height));}
}
public sealed class ForegroundCapture:IDisposable
{
 private readonly uint _own=(uint)Environment.ProcessId;
 private readonly WinEventProc _callback;
 private readonly object _identityLock=new();
 private WindowStamp? _last;
 private nint _foregroundHook,_destroyHook;
 private bool _disposed;
 private sealed record WindowStamp(nint Handle,uint Pid,DateTimeOffset Created,uint ThreadId,string ClassName,string Title);
 public ForegroundCapture()
 {
  _callback=(_,eventType,hwnd,objectId,childId,_,_)=>
  {
   if(objectId!=0||childId!=0)return;
   if(eventType==3)Track(hwnd);
   else if(eventType==0x8001)Forget(hwnd);
  };
  _foregroundHook=SetWinEventHook(3,3,0,_callback,0,0,0);
  if(_foregroundHook==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
  _destroyHook=SetWinEventHook(0x8001,0x8001,0,_callback,0,0,0);
  if(_destroyHook==0){UnhookWinEvent(_foregroundHook);throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
  Track(GetForegroundWindow());
 }
 private void Track(nint hwnd)
 {
  var stamp=ReadStamp(hwnd);
  if(stamp is null||stamp.Pid==_own)return;
  lock(_identityLock)if(!_disposed)_last=stamp;
 }
 private void Forget(nint hwnd){lock(_identityLock)if(_last?.Handle==hwnd)_last=null;}
 private static WindowStamp? ReadStamp(nint hwnd)
 {
  if(hwnd==0||!IsWindow(hwnd))return null;
  var thread=GetWindowThreadProcessId(hwnd,out var pid);
  if(pid==0||thread==0)return null;
  try
  {
   using var process=Process.GetProcessById(checked((int)pid));
   var created=new DateTimeOffset(process.StartTime.ToUniversalTime(),TimeSpan.Zero);
   var className=new StringBuilder(256);
   if(GetClassName(hwnd,className,className.Capacity)==0)return null;
   if(!IsWindow(hwnd)||GetWindowThreadProcessId(hwnd,out var again)!=thread||again!=pid)return null;
   var title=Title(hwnd);
   if(!IsWindow(hwnd)||GetWindowThreadProcessId(hwnd,out again)!=thread||again!=pid)return null;
   return new(hwnd,pid,created,thread,className.ToString(),title);
  }
  catch{return null;}
 }
 private static bool SameWindow(WindowStamp expected)=>ReadStamp(expected.Handle) is { } now&&now==expected;
 private WindowStamp? CurrentStamp(){lock(_identityLock)return _last;}
 private bool StillCurrent(WindowStamp expected)
 {
  lock(_identityLock)if(_disposed||!ReferenceEquals(_last,expected))return false;
  return SameWindow(expected);
 }
 public nint LastExternalHandle=>CurrentStamp()?.Handle??0;
 public CaptureResult CaptureLastExternal(CaptureRect? crop=null)
 {
  ObjectDisposedException.ThrowIf(_disposed,this);
  var stamp=CurrentStamp();
  if(stamp is null)return new(CaptureStatus.NoExternalWindow);
  var hwnd=stamp.Handle;
  if(!IsWindow(hwnd))return new(CaptureStatus.Closed);
  if(!StillCurrent(stamp))return new(CaptureStatus.Unavailable);
  if(IsIconic(hwnd))return new(CaptureStatus.Minimized);
  if(!IsWindowVisible(hwnd)||GetForegroundWindow()!=hwnd||IsCloaked(hwnd))return new(CaptureStatus.Protected);
  try
  {
   using var process=Process.GetProcessById(checked((int)stamp.Pid));
   if(IsElevated(process.Handle)&&!IsElevated(GetCurrentProcess()))return new(CaptureStatus.Elevated);
   if(!StillCurrent(stamp))return new(CaptureStatus.Unavailable);
   var identity=new ForegroundIdentity(hwnd,stamp.Pid,stamp.Created,process.ProcessName,stamp.Title);
   Rect rect;
   var prior=SetThreadDpiAwarenessContext(new nint(-4));
   try{if(prior==0||!GetWindowRect(hwnd,out rect))return new(CaptureStatus.Unavailable);}
   finally{if(prior!=0)SetThreadDpiAwarenessContext(prior);}
   var w=(long)rect.Right-rect.Left;var h=(long)rect.Bottom-rect.Top;
   if(w<1||h<1)return new(CaptureStatus.Unavailable);
   if(w>4096||h>4096||w*h>8_000_000)return new(CaptureStatus.TooLarge);
   if(!StillCurrent(stamp)||!IsUnobscured(hwnd,rect))return new(CaptureStatus.Protected);
   var (visibleText,textAvailable)=AccessibleText(hwnd,rect);
   if(!StillCurrent(stamp)||!IsUnobscured(hwnd,rect))return new(CaptureStatus.Unavailable);
   var png=CapturePng(stamp,rect,(int)w,(int)h,crop);
   if(png is null)return new(CaptureStatus.Protected);
   if(!StillCurrent(stamp)||!IsUnobscured(hwnd,rect))return new(CaptureStatus.Unavailable);
   return new(CaptureStatus.Captured,new ForegroundContext(Guid.NewGuid(),DateTimeOffset.UtcNow,identity,visibleText,png.Value.Png,png.Value.Bounds),textAvailable?null:"Visible text unavailable.");
  }
  catch(System.ComponentModel.Win32Exception e){return new(CaptureStatus.Unavailable,null,e.Message);}
  catch(UnauthorizedAccessException e){return new(CaptureStatus.Protected,null,e.Message);}
  catch(ArgumentException){return new(CaptureStatus.Closed);}
  catch(InvalidOperationException){return new(CaptureStatus.Closed);}
  catch(IOException e){return new(CaptureStatus.Unavailable,null,e.Message);}
  catch(COMException e){return new(CaptureStatus.Protected,null,e.Message);}
 }
 private static bool IsElevated(nint process){if(!OpenProcessToken(process,8,out var token))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());try{if(!GetTokenInformation(token,20,out var elevated,4,out _))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());return elevated!=0;}finally{CloseHandle(token);}}
 [DllImport("advapi32.dll",SetLastError=true)]private static extern bool OpenProcessToken(nint process,uint access,out nint token);
 [DllImport("advapi32.dll",SetLastError=true)]private static extern bool GetTokenInformation(nint token,int infoClass,out int elevation,int size,out int returned);
 [DllImport("kernel32.dll")]private static extern nint GetCurrentProcess();
 [DllImport("kernel32.dll")]private static extern bool CloseHandle(nint handle);
 private static string Title(nint hwnd){var b=new StringBuilder(Math.Clamp(GetWindowTextLength(hwnd),0,512)+1);GetWindowText(hwnd,b,b.Capacity);return b.ToString();}
 private static readonly object _automationLock=new();
 private static Task<(string Text,bool Available)>? _automationWorker;
 private static DateTimeOffset _automationNextAttempt;
 private static (string Text,bool Available) AccessibleText(nint hwnd,Rect windowRect)
 {
  Task<(string Text,bool Available)> task;
  lock(_automationLock)
  {
   if(_automationWorker is {IsCompleted:false}||DateTimeOffset.UtcNow<_automationNextAttempt)return ("",false);
   task=Task.Run(()=>QueryAutomationText(hwnd,windowRect));
   _automationWorker=task;
  }
  try
  {
   if(!task.Wait(TimeSpan.FromSeconds(2)))
   {
    lock(_automationLock)_automationNextAttempt=DateTimeOffset.UtcNow.AddSeconds(10);
    return ("",false);
   }
   return task.Result;
  }
  catch{return ("",false);}
  finally{lock(_automationLock)if(task.IsCompleted&&ReferenceEquals(_automationWorker,task))_automationWorker=null;}
 }
 private static (string Text,bool Available) QueryAutomationText(nint hwnd,Rect windowRect)
 {
  try
  {
   var type=Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"),throwOnError:true)!;
   var automation=(IUIAutomation)Activator.CreateInstance(type)!;
   automation.ElementFromHandle(hwnd,out var root);
   if(root is null)return ("",false);
   automation.GetControlViewWalker(out var walker);
   if(walker is null)return ("",false);
   var queue=new Queue<IUIAutomationElement>();queue.Enqueue(root);
   var text=new StringBuilder();var count=0;
   while(queue.Count>0&&count++<300&&text.Length<16000)
   {
    var element=queue.Dequeue();
    try
    {
     if(element.GetCurrentPropertyValue(30019,out var password)!=0||password is not bool isPassword||isPassword)return ("",false);
     if(element.GetCurrentPropertyValue(30022,out var offscreen)!=0||offscreen is not bool isOffscreen)return ("",false);
     if(isOffscreen)continue;
     if(element.GetCurrentPropertyValue(30001,out var rectangle)!=0||!VisibleAutomationRect(rectangle,windowRect))continue;
     if(element.GetCurrentPropertyValue(30005,out var name)!=0)return ("",false);
     if(name is string value&&!string.IsNullOrWhiteSpace(value))text.AppendLine(value[..Math.Min(value.Length,500)]);
     walker.GetFirstChildElement(element,out var child);
     for(var siblings=0;child is not null&&siblings<100;siblings++)
     {
      if(queue.Count<300)queue.Enqueue(child);
      walker.GetNextSiblingElement(child,out var next);child=next;
     }
    }
    catch(COMException){return ("",false);}
   }
   if(queue.Count>0)return ("",false);
   return (text.ToString()[..Math.Min(text.Length,16000)],true);
  }
  catch{return ("",false);}
 }
 private static bool VisibleAutomationRect(object? value,Rect window)
 {
  if(value is not Array rectangle||rectangle.Length<4)return false;
  try
  {
   var x=Convert.ToDouble(rectangle.GetValue(0));var y=Convert.ToDouble(rectangle.GetValue(1));
   var width=Convert.ToDouble(rectangle.GetValue(2));var height=Convert.ToDouble(rectangle.GetValue(3));
   return double.IsFinite(x)&&double.IsFinite(y)&&double.IsFinite(width)&&double.IsFinite(height)&&width>0&&height>0&&
    x<window.Right&&x+width>window.Left&&y<window.Bottom&&y+height>window.Top;
  }
  catch{return false;}
 }
 [ComImport,Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
 private interface IUIAutomation
 {
  void CompareElements();void CompareRuntimeIds();void GetRootElement();
  void ElementFromHandle(nint hwnd,out IUIAutomationElement element);
  void ElementFromPoint();void GetFocusedElement();void GetRootElementBuildCache();void ElementFromHandleBuildCache();void ElementFromPointBuildCache();void GetFocusedElementBuildCache();void CreateTreeWalker();
  void GetControlViewWalker(out IUIAutomationTreeWalker walker);
 }
 [ComImport,Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
 private interface IUIAutomationElement
 {
  void SetFocus();void GetRuntimeId();void FindFirst();void FindAll();void FindFirstBuildCache();void FindAllBuildCache();void BuildUpdatedCache();
  [PreserveSig]int GetCurrentPropertyValue(int propertyId,[MarshalAs(UnmanagedType.Struct)]out object value);
 }
 [ComImport,Guid("4042c624-389c-4afc-a630-9df854a541fc"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
 private interface IUIAutomationTreeWalker
 {
  void GetParentElement();
  void GetFirstChildElement(IUIAutomationElement element,out IUIAutomationElement? child);
  void GetLastChildElement();
  void GetNextSiblingElement(IUIAutomationElement element,out IUIAutomationElement? sibling);
 }
 public void Dispose(){lock(_identityLock){if(_disposed)return;_disposed=true;_last=null;}if(_foregroundHook!=0)UnhookWinEvent(_foregroundHook);if(_destroyHook!=0)UnhookWinEvent(_destroyHook);_foregroundHook=_destroyHook=0;}
 private delegate void WinEventProc(nint hook,uint eventType,nint hwnd,int objectId,int childId,uint thread,uint time);
 [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
 [DllImport("user32.dll",SetLastError=true)]private static extern nint SetWinEventHook(uint min,uint max,nint module,WinEventProc callback,uint process,uint thread,uint flags);
 [DllImport("user32.dll")]private static extern bool UnhookWinEvent(nint hook);
 [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
 [DllImport("user32.dll")]private static extern bool IsWindow(nint hwnd);
 [DllImport("user32.dll")]private static extern bool IsIconic(nint hwnd);
 [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint hwnd);
 [DllImport("user32.dll")]private static extern nint WindowFromPoint(Point point);
 [DllImport("user32.dll")]private static extern nint GetAncestor(nint hwnd,uint flags);
 [DllImport("user32.dll")]private static extern nint GetWindow(nint hwnd,uint command);
 [DllImport("user32.dll",SetLastError=true)]private static extern bool GetWindowDisplayAffinity(nint hwnd,out uint affinity);
 [DllImport("user32.dll")]private static extern int GetSystemMetrics(int index);
 [DllImport("dwmapi.dll")]private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)]out bool enabled);
 private static long WindowExtendedStyle(nint hwnd)=>IntPtr.Size==8?GetWindowLongPtr(hwnd,-20).ToInt64():GetWindowLong(hwnd,-20);
 [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW",SetLastError=true)]private static extern nint GetWindowLongPtr(nint hwnd,int index);
 [DllImport("user32.dll",EntryPoint="GetWindowLongW",SetLastError=true)]private static extern int GetWindowLong(nint hwnd,int index);
 [DllImport("dwmapi.dll")]private static extern int DwmGetWindowAttribute(nint hwnd,int attribute,out int value,int size);
 [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;public Point(int x,int y){X=x;Y=y;}}
 private static bool IsCloaked(nint hwnd)=>DwmGetWindowAttribute(hwnd,14,out var cloaked,4)==0&&cloaked!=0;
 internal static bool FitsVirtualScreen(CaptureRect rect,CaptureRect virtualScreen)
 {
  if(rect.Width<1||rect.Height<1||virtualScreen.Width<1||virtualScreen.Height<1)return false;
  return (long)rect.X>=virtualScreen.X&&(long)rect.Y>=virtualScreen.Y&&
   (long)rect.X+rect.Width<=(long)virtualScreen.X+virtualScreen.Width&&
   (long)rect.Y+rect.Height<=(long)virtualScreen.Y+virtualScreen.Height;
 }
 private static bool IsUnobscured(nint hwnd,Rect rect)
 {
  var prior=SetThreadDpiAwarenessContext(new nint(-4));
  if(prior==0)return false;
  try
  {
   var bounds=new CaptureRect(rect.Left,rect.Top,checked((int)((long)rect.Right-rect.Left)),checked((int)((long)rect.Bottom-rect.Top)));
   var virtualScreen=new CaptureRect(GetSystemMetrics(76),GetSystemMetrics(77),GetSystemMetrics(78),GetSystemMetrics(79));
   if(!FitsVirtualScreen(bounds,virtualScreen))return false;
   if(!GetWindowRect(hwnd,out var current)||current.Left!=rect.Left||current.Top!=rect.Top||current.Right!=rect.Right||current.Bottom!=rect.Bottom)return false;
   if(GetWindowDisplayAffinity(hwnd,out var affinity)){if(affinity!=0)return false;}
   else{if(DwmIsCompositionEnabled(out var composing)!=0||!composing||(WindowExtendedStyle(hwnd)&0x80000)!=0)return false;}
   for(var above=GetWindow(hwnd,3);above!=0;above=GetWindow(above,3))
    if(IsWindowVisible(above)&&!IsIconic(above)&&!IsCloaked(above)){if(!GetWindowRect(above,out var covered)||covered.Left<rect.Right&&covered.Right>rect.Left&&covered.Top<rect.Bottom&&covered.Bottom>rect.Top)return false;}
   for(long y=rect.Top;y<rect.Bottom;y+=16)for(long x=rect.Left;x<rect.Right;x+=16)
   {
    var point=new Point((int)Math.Min(x+8,(long)rect.Right-1),(int)Math.Min(y+8,(long)rect.Bottom-1));
    if(MonitorFromPoint(point,0)==0||GetAncestor(WindowFromPoint(point),2)!=hwnd)return false;
   }
   return GetForegroundWindow()==hwnd;
  }
  finally{SetThreadDpiAwarenessContext(prior);}
 }
 [DllImport("user32.dll")]private static extern nint MonitorFromPoint(Point point,uint flags);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(nint hwnd,StringBuilder name,int max);
 [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowTextLength(nint hwnd);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(nint hwnd,StringBuilder title,int max);
 [DllImport("user32.dll")]private static extern bool GetWindowRect(nint hwnd,out Rect rect);
 [DllImport("user32.dll")]private static extern nint SetThreadDpiAwarenessContext(nint context);
 internal static byte[] CropPng(byte[] source,CaptureRect crop)
 {var input=Path.Combine(Path.GetTempPath(),"agent-os-source-"+Guid.NewGuid().ToString("N")+".png");var output=Path.Combine(Path.GetTempPath(),"agent-os-crop-"+Guid.NewGuid().ToString("N")+".png");try{File.WriteAllBytes(input,source);var startup=new GdiplusStartupInput{Version=1};if(GdiplusStartup(out var token,ref startup,0)!=0)throw new IOException("PNG processing is unavailable.");try{if(GdipLoadImageFromFile(input,out var image)!=0)throw new IOException("Captured PNG is invalid.");try{if(GdipCloneBitmapAreaI(crop.X,crop.Y,crop.Width,crop.Height,0x26200A,image,out var clipped)!=0)throw new IOException("PNG crop failed.");try{var encoder=new Guid("557cf406-1a04-11d3-9a73-0000f81ef32e");if(GdipSaveImageToFile(clipped,output,ref encoder,0)!=0)throw new IOException("PNG encoding failed.");}finally{GdipDisposeImage(clipped);}}finally{GdipDisposeImage(image);}}finally{GdiplusShutdown(token);}var bytes=File.ReadAllBytes(output);if(bytes.Length>8_000_000)throw new IOException("Cropped PNG is too large.");return bytes;}finally{if(File.Exists(input))File.Delete(input);if(File.Exists(output))File.Delete(output);}}
 private (byte[] Png,CaptureRect Bounds)? CapturePng(WindowStamp stamp,Rect rect,int width,int height,CaptureRect? crop)
 {
  var b=crop??new CaptureRect(0,0,width,height);
  if(b.X<0||b.Y<0||b.Width<1||b.Height<1||(long)b.X+b.Width>width||(long)b.Y+b.Height>height||(long)rect.Left+b.X>int.MaxValue||(long)rect.Top+b.Y>int.MaxValue)throw new ArgumentOutOfRangeException(nameof(crop));
  var prior=SetThreadDpiAwarenessContext(new nint(-4));
  if(prior==0)return null;
  try
  {
   var screen=GetDC(0);if(screen==0)return null;
   var dc=CreateCompatibleDC(screen);var bmp=CreateCompatibleBitmap(screen,width,height);
   if(dc==0||bmp==0){if(bmp!=0)DeleteObject(bmp);if(dc!=0)DeleteDC(dc);ReleaseDC(0,screen);return null;}
   var old=SelectObject(dc,bmp);
   try
   {
    if(!StillCurrent(stamp)||GetForegroundWindow()!=stamp.Handle||!IsUnobscured(stamp.Handle,rect)||!BitBlt(dc,0,0,width,height,screen,rect.Left,rect.Top,0x00CC0020))return null;
    nint selected=bmp,cropped=0,cropDc=0,cropOld=0;
    try
    {
     if(crop is not null)
     {
      cropDc=CreateCompatibleDC(screen);cropped=CreateCompatibleBitmap(screen,b.Width,b.Height);
      if(cropDc==0||cropped==0)return null;
      cropOld=SelectObject(cropDc,cropped);
      if(!BitBlt(cropDc,0,0,b.Width,b.Height,dc,b.X,b.Y,0x00CC0020))return null;
      selected=cropped;
     }
     var path=Path.Combine(Path.GetTempPath(),"agent-os-capture-"+Guid.NewGuid().ToString("N")+".png");
     try
     {
      var input=new GdiplusStartupInput{Version=1};
      if(GdiplusStartup(out var token,ref input,0)!=0)return null;
      try
      {
       if(GdipCreateBitmapFromHBITMAP(selected,0,out var image)!=0)return null;
       try{var encoder=new Guid("557cf406-1a04-11d3-9a73-0000f81ef32e");if(GdipSaveImageToFile(image,path,ref encoder,0)!=0)return null;}
       finally{GdipDisposeImage(image);}
      }
      finally{GdiplusShutdown(token);}
      var bytes=File.ReadAllBytes(path);
      return bytes.Length<=8_000_000&&StillCurrent(stamp)?(bytes,new CaptureRect(checked(rect.Left+b.X),checked(rect.Top+b.Y),b.Width,b.Height)):null;
     }
     finally{if(File.Exists(path))File.Delete(path);}
    }
    finally{if(cropOld!=0)SelectObject(cropDc,cropOld);if(cropped!=0)DeleteObject(cropped);if(cropDc!=0)DeleteDC(cropDc);}
   }
   finally{SelectObject(dc,old);DeleteObject(bmp);DeleteDC(dc);ReleaseDC(0,screen);}
  }
  finally{SetThreadDpiAwarenessContext(prior);}
 }
 [StructLayout(LayoutKind.Sequential)]private struct GdiplusStartupInput{public uint Version;public nint Callback;public int SuppressBackgroundThread,SuppressExternalCodecs;}
 [DllImport("user32.dll")]private static extern nint GetDC(nint hwnd);
 [DllImport("user32.dll")]private static extern int ReleaseDC(nint hwnd,nint dc);
 [DllImport("gdi32.dll")]private static extern nint CreateCompatibleDC(nint dc);
 [DllImport("gdi32.dll")]private static extern nint CreateCompatibleBitmap(nint dc,int w,int h);
 [DllImport("gdi32.dll")]private static extern nint SelectObject(nint dc,nint obj);
 [DllImport("gdi32.dll")]private static extern bool DeleteObject(nint obj);
 [DllImport("gdi32.dll")]private static extern bool DeleteDC(nint dc);
 [DllImport("gdi32.dll")]private static extern bool BitBlt(nint dest,int x,int y,int w,int h,nint src,int sx,int sy,uint op);
 [DllImport("gdiplus.dll")]private static extern int GdiplusStartup(out nint token,ref GdiplusStartupInput input,nint output);
 [DllImport("gdiplus.dll")]private static extern void GdiplusShutdown(nint token);
 [DllImport("gdiplus.dll")]private static extern int GdipCreateBitmapFromHBITMAP(nint bitmap,nint palette,out nint image);
 [DllImport("gdiplus.dll",CharSet=CharSet.Unicode)]private static extern int GdipSaveImageToFile(nint image,string path,ref Guid encoder,nint parameters);
 [DllImport("gdiplus.dll")]private static extern int GdipDisposeImage(nint image);
 [DllImport("gdiplus.dll",CharSet=CharSet.Unicode)]private static extern int GdipLoadImageFromFile(string path,out nint image);
 [DllImport("gdiplus.dll")]private static extern int GdipCloneBitmapAreaI(int x,int y,int width,int height,int format,nint image,out nint clone);
}
public enum HotKeyStatus{Registered,Conflict,Unavailable}
public sealed class CaptureHotKey:IDisposable
{private readonly nint _window;private readonly int _id;private bool _registered;public HotKeyStatus Status{get;}
 public CaptureHotKey(nint messageWindow,int id=0xA605,uint modifiers=0x0003,uint virtualKey=0x20){_window=messageWindow;_id=id;if(messageWindow==0||!IsWindow(messageWindow)){Status=HotKeyStatus.Unavailable;return;}_registered=RegisterHotKey(messageWindow,id,modifiers|0x4000,virtualKey);Status=_registered?HotKeyStatus.Registered:Marshal.GetLastWin32Error()==1409?HotKeyStatus.Conflict:HotKeyStatus.Unavailable;}
 public void Dispose(){if(_registered){UnregisterHotKey(_window,_id);_registered=false;}}
 [DllImport("user32.dll")]private static extern bool IsWindow(nint hwnd);
 [DllImport("user32.dll",SetLastError=true)]private static extern bool RegisterHotKey(nint hwnd,int id,uint modifiers,uint key);
 [DllImport("user32.dll")]private static extern bool UnregisterHotKey(nint hwnd,int id);
}










