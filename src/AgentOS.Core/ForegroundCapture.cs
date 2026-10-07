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
 {if(_screenshotPng is null||ScreenshotBounds is null||crop.X<0||crop.Y<0||crop.Width<1||crop.Height<1||crop.X+crop.Width>ScreenshotBounds.Width||crop.Y+crop.Height>ScreenshotBounds.Height)throw new ArgumentOutOfRangeException(nameof(crop));var bytes=ForegroundCapture.CropPng(_screenshotPng,crop);return new(Id,CapturedAt,Window,VisibleText,bytes,new CaptureRect(ScreenshotBounds.X+crop.X,ScreenshotBounds.Y+crop.Y,crop.Width,crop.Height));}
}
public sealed class ForegroundCapture:IDisposable
{
 private readonly uint _own=(uint)Environment.ProcessId; private readonly WinEventProc _callback; private nint _hook,_last; private bool _disposed;
 public ForegroundCapture(){_callback=(_,_,hwnd,_,_,_,_)=>Track(hwnd);_hook=SetWinEventHook(3,3,0,_callback,0,0,0);if(_hook==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());Track(GetForegroundWindow());}
 private void Track(nint hwnd){if(hwnd==0||!IsWindow(hwnd))return;GetWindowThreadProcessId(hwnd,out var pid);if(pid!=0&&pid!=_own)Interlocked.Exchange(ref _last,hwnd);}
 public nint LastExternalHandle=>Interlocked.CompareExchange(ref _last,0,0);
 public CaptureResult CaptureLastExternal(CaptureRect? crop=null)
 {ObjectDisposedException.ThrowIf(_disposed,this);var hwnd=LastExternalHandle;if(hwnd==0)return new(CaptureStatus.NoExternalWindow);if(!IsWindow(hwnd))return new(CaptureStatus.Closed);if(IsIconic(hwnd))return new(CaptureStatus.Minimized);if(!IsWindowVisible(hwnd)||GetForegroundWindow()!=hwnd||IsCloaked(hwnd))return new(CaptureStatus.Protected);GetWindowThreadProcessId(hwnd,out var pid);if(pid==0||pid==_own)return new(CaptureStatus.Unavailable);
 try{using var process=Process.GetProcessById((int)pid);if(IsElevated(process.Handle)&&!IsElevated(GetCurrentProcess()))return new(CaptureStatus.Elevated);var created=new DateTimeOffset(process.StartTime.ToUniversalTime(),TimeSpan.Zero);var title=Title(hwnd);var identity=new ForegroundIdentity(hwnd,pid,created,process.ProcessName,title);if(!IsWindow(hwnd)||GetWindowThreadProcessId(hwnd,out var current)==0||current!=pid)return new(CaptureStatus.Closed);Rect rect;var prior=SetThreadDpiAwarenessContext(new nint(-4));try{if(!GetWindowRect(hwnd,out rect))return new(CaptureStatus.Unavailable);}finally{if(prior!=0)SetThreadDpiAwarenessContext(prior);}var w=rect.Right-rect.Left;var h=rect.Bottom-rect.Top;if(w<1||h<1)return new(CaptureStatus.Unavailable);if(w>4096||h>4096||(long)w*h>8_000_000)return new(CaptureStatus.TooLarge);if(!IsUnobscured(hwnd,rect))return new(CaptureStatus.Protected);var text=AccessibleText(hwnd);var png=CapturePng(hwnd,rect,w,h,crop);if(png is null)return new(CaptureStatus.Protected);return new(CaptureStatus.Captured,new ForegroundContext(Guid.NewGuid(),DateTimeOffset.UtcNow,identity,text,png.Value.Png,png.Value.Bounds));}
 catch(System.ComponentModel.Win32Exception e){return new(CaptureStatus.Protected,null,e.Message);}catch(UnauthorizedAccessException e){return new(CaptureStatus.Protected,null,e.Message);}catch(ArgumentException){return new(CaptureStatus.Closed);}catch(InvalidOperationException){return new(CaptureStatus.Closed);}catch(IOException e){return new(CaptureStatus.Unavailable,null,e.Message);}catch(COMException e){return new(CaptureStatus.Protected,null,e.Message);}}
 private static bool IsElevated(nint process){if(!OpenProcessToken(process,8,out var token))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());try{if(!GetTokenInformation(token,20,out var elevated,4,out _))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());return elevated!=0;}finally{CloseHandle(token);}}
 [DllImport("advapi32.dll",SetLastError=true)]private static extern bool OpenProcessToken(nint process,uint access,out nint token);
 [DllImport("advapi32.dll",SetLastError=true)]private static extern bool GetTokenInformation(nint token,int infoClass,out int elevation,int size,out int returned);
 [DllImport("kernel32.dll")]private static extern nint GetCurrentProcess();
 [DllImport("kernel32.dll")]private static extern bool CloseHandle(nint handle);
 private static string Title(nint hwnd){var b=new StringBuilder(Math.Clamp(GetWindowTextLength(hwnd),0,512)+1);GetWindowText(hwnd,b,b.Capacity);return b.ToString();}
 private static string AccessibleText(nint hwnd)
 {var task=Task.Run(()=>{try{var t=Type.GetTypeFromProgID("UIAutomationClient.CUIAutomation");if(t is null)return "";var a=Activator.CreateInstance(t)!;var root=t.InvokeMember("ElementFromHandle",System.Reflection.BindingFlags.InvokeMethod,null,a,[hwnd]);if(root is null)return "";var walker=t.InvokeMember("ControlViewWalker",System.Reflection.BindingFlags.GetProperty,null,a,null);var q=new Queue<object>();q.Enqueue(root);var b=new StringBuilder();for(var n=0;q.Count>0&&n<300&&b.Length<16000;n++){var e=q.Dequeue();try{var et=e.GetType();if(et.InvokeMember("CurrentIsPassword",System.Reflection.BindingFlags.GetProperty,null,e,null) is true||et.InvokeMember("CurrentIsOffscreen",System.Reflection.BindingFlags.GetProperty,null,e,null) is true)continue;var name=et.InvokeMember("CurrentName",System.Reflection.BindingFlags.GetProperty,null,e,null) as string;if(!string.IsNullOrWhiteSpace(name))b.AppendLine(name[..Math.Min(name.Length,500)]);if(walker is null)continue;var wt=walker.GetType();var child=wt.InvokeMember("GetFirstChildElement",System.Reflection.BindingFlags.InvokeMethod,null,walker,[e]);for(var i=0;child is not null&&i<100;i++){q.Enqueue(child);child=wt.InvokeMember("GetNextSiblingElement",System.Reflection.BindingFlags.InvokeMethod,null,walker,[child]);}}catch(COMException){}}return b.ToString()[..Math.Min(b.Length,16000)];}catch{return "";}});return task.Wait(TimeSpan.FromSeconds(2))?task.Result:"";}
 public void Dispose(){if(_disposed)return;_disposed=true;if(_hook!=0)UnhookWinEvent(_hook);_hook=0;}
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
 [DllImport("dwmapi.dll")]private static extern int DwmGetWindowAttribute(nint hwnd,int attribute,out int value,int size);
 [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;public Point(int x,int y){X=x;Y=y;}}
 private static bool IsCloaked(nint hwnd)=>DwmGetWindowAttribute(hwnd,14,out var cloaked,4)==0&&cloaked!=0;
 private static bool IsUnobscured(nint hwnd,Rect rect)
 {
  if(rect.Left<0||rect.Top<0||rect.Right>GetSystemMetrics(0)||rect.Bottom>GetSystemMetrics(1))return false;
  if(!GetWindowDisplayAffinity(hwnd,out var affinity)||affinity!=0)return false;
  for(var above=GetWindow(hwnd,3);above!=0;above=GetWindow(above,3))
   if(IsWindowVisible(above)&&!IsIconic(above)&&!IsCloaked(above)&&GetWindowRect(above,out var covered)&&covered.Left<rect.Right&&covered.Right>rect.Left&&covered.Top<rect.Bottom&&covered.Bottom>rect.Top)return false;
  for(var y=rect.Top+8;y<rect.Bottom;y+=16)for(var x=rect.Left+8;x<rect.Right;x+=16)
   if(GetAncestor(WindowFromPoint(new Point(x,y)),2)!=hwnd)return false;
  return GetForegroundWindow()==hwnd;
 }
 [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowTextLength(nint hwnd);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(nint hwnd,StringBuilder title,int max);
 [DllImport("user32.dll")]private static extern bool GetWindowRect(nint hwnd,out Rect rect);
 [DllImport("user32.dll")]private static extern nint SetThreadDpiAwarenessContext(nint context);
 internal static byte[] CropPng(byte[] source,CaptureRect crop)
 {var input=Path.Combine(Path.GetTempPath(),"agent-os-source-"+Guid.NewGuid().ToString("N")+".png");var output=Path.Combine(Path.GetTempPath(),"agent-os-crop-"+Guid.NewGuid().ToString("N")+".png");try{File.WriteAllBytes(input,source);var startup=new GdiplusStartupInput{Version=1};if(GdiplusStartup(out var token,ref startup,0)!=0)throw new IOException("PNG processing is unavailable.");try{if(GdipLoadImageFromFile(input,out var image)!=0)throw new IOException("Captured PNG is invalid.");try{if(GdipCloneBitmapAreaI(crop.X,crop.Y,crop.Width,crop.Height,0x26200A,image,out var clipped)!=0)throw new IOException("PNG crop failed.");try{var encoder=new Guid("557cf406-1a04-11d3-9a73-0000f81ef32e");if(GdipSaveImageToFile(clipped,output,ref encoder,0)!=0)throw new IOException("PNG encoding failed.");}finally{GdipDisposeImage(clipped);}}finally{GdipDisposeImage(image);}}finally{GdiplusShutdown(token);}var bytes=File.ReadAllBytes(output);if(bytes.Length>8_000_000)throw new IOException("Cropped PNG is too large.");return bytes;}finally{if(File.Exists(input))File.Delete(input);if(File.Exists(output))File.Delete(output);}}
 private static (byte[] Png,CaptureRect Bounds)? CapturePng(nint hwnd,Rect rect,int width,int height,CaptureRect? crop)
 {var b=crop??new CaptureRect(0,0,width,height);if(b.X<0||b.Y<0||b.Width<1||b.Height<1||b.X+b.Width>width||b.Y+b.Height>height)throw new ArgumentOutOfRangeException(nameof(crop));var screen=GetDC(0);if(screen==0)return null;var dc=CreateCompatibleDC(screen);var bmp=CreateCompatibleBitmap(screen,width,height);if(dc==0||bmp==0){if(bmp!=0)DeleteObject(bmp);if(dc!=0)DeleteDC(dc);ReleaseDC(0,screen);return null;}var old=SelectObject(dc,bmp);
 try{if(GetForegroundWindow()!=hwnd||!IsUnobscured(hwnd,rect)||!BitBlt(dc,0,0,width,height,screen,rect.Left,rect.Top,0x00CC0020))return null;nint selected=bmp,cropped=0,cropDc=0,cropOld=0;try{if(crop is not null){cropDc=CreateCompatibleDC(screen);cropped=CreateCompatibleBitmap(screen,b.Width,b.Height);if(cropDc==0||cropped==0)return null;cropOld=SelectObject(cropDc,cropped);if(!BitBlt(cropDc,0,0,b.Width,b.Height,dc,b.X,b.Y,0x00CC0020))return null;selected=cropped;}var path=Path.Combine(Path.GetTempPath(),"agent-os-capture-"+Guid.NewGuid().ToString("N")+".png");try{var input=new GdiplusStartupInput{Version=1};if(GdiplusStartup(out var token,ref input,0)!=0)return null;try{if(GdipCreateBitmapFromHBITMAP(selected,0,out var image)!=0)return null;try{var encoder=new Guid("557cf406-1a04-11d3-9a73-0000f81ef32e");if(GdipSaveImageToFile(image,path,ref encoder,0)!=0)return null;}finally{GdipDisposeImage(image);}}finally{GdiplusShutdown(token);}var bytes=File.ReadAllBytes(path);return bytes.Length<=8_000_000?(bytes,b):null;}finally{if(File.Exists(path))File.Delete(path);}}finally{if(cropOld!=0)SelectObject(cropDc,cropOld);if(cropped!=0)DeleteObject(cropped);if(cropDc!=0)DeleteDC(cropDc);}}finally{SelectObject(dc,old);DeleteObject(bmp);DeleteDC(dc);ReleaseDC(0,screen);}}
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





