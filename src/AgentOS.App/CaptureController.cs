using AgentOS.Core;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinRT.Interop;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AgentOS.App;

/// <summary>Owns the desktop capture entry point and the capture UI lifetime.</summary>
public sealed class CaptureController : IDisposable
{
    private const int HotkeyId = 0xA605, WmHotkey = 0x0312;
    private readonly Window _window;
    private readonly DispatcherQueue _queue;
    private readonly nint _hwnd;
    private readonly SubclassProc _procedure;
    private readonly ForegroundCapture _tracker;
    private CaptureHotKey _hotkey;
    private readonly CancellationTokenSource _closing = new();
    private CaptureDialog? _dialog;
    private readonly Dictionary<string, ProjectRuntime> _ownedRuntimes = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentOS", "capture.json");
    public HotKeyStatus HotkeyStatus => _hotkey.Status;
    public Func<Task>? OnMapSaved { get; set; }

    public CaptureController(Window window)
    {
        _window = window;
        _queue = DispatcherQueue.GetForCurrentThread();
        _hwnd = WindowNative.GetWindowHandle(window);
        _tracker = new ForegroundCapture();
        _procedure = WindowMessage;
        if (_hwnd == 0 || !SetWindowSubclass(_hwnd, _procedure, (nuint)HotkeyId, 0))
            throw new InvalidOperationException("The capture window could not attach its keyboard command.");
        var (modifiers, key) = ReadHotkey();
        _hotkey = new CaptureHotKey(_hwnd, HotkeyId, modifiers, key);
        window.Closed += (_, _) => Dispose();
        if (window.Content is FrameworkElement root)
        {
            var accel = new KeyboardAccelerator { Key = VirtualKey.M, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift };
            accel.Invoked += (sender, e) => { e.Handled = true; _ = OpenAsync(); };
            root.KeyboardAccelerators.Add(accel);
            // The existing header is a Grid whose right-hand child is its command stack.
            if (root is Grid grid && grid.Children.FirstOrDefault() is Grid header &&
                header.Children.LastOrDefault() is StackPanel commands)
            {
                var button = new Button { Content = "Capture context" };
                AutomationProperties.SetAutomationId(button, "CaptureContext");
                AutomationProperties.SetName(button, "Capture context from the last foreground application");
                button.Click += (_, _) => _ = OpenAsync();
                commands.Children.Add(button);
                var shortcut = new MenuFlyout();
                foreach (var option in new (string Name, uint Modifiers, uint Key)[] { ("Ctrl+Alt+Space", 0x0003, 0x20), ("Ctrl+Shift+Space", 0x0006, 0x20), ("Ctrl+Alt+M", 0x0003, 0x4D) })
                {
                    var item = new MenuFlyoutItem { Text = option.Name };
                    item.Click += (_, _) => { try { ConfigureHotkey(option.Modifiers, option.Key); ToolTipService.SetToolTip(button, _hotkey.Status == HotKeyStatus.Registered ? "Capture shortcut: " + option.Name : "Shortcut conflict. Use this button or Ctrl+Shift+M."); } catch (Exception e) { ToolTipService.SetToolTip(button, e.Message); } };
                    shortcut.Items.Add(item);
                }
                var shortcutButton = new Button { Content = "Shortcut", Flyout = shortcut };
                AutomationProperties.SetName(shortcutButton, "Configure capture shortcut");
                commands.Children.Add(shortcutButton);
                if (_hotkey.Status != HotKeyStatus.Registered) AutomationProperties.SetHelpText(shortcutButton, "Global shortcut unavailable or in use. Choose another shortcut; capture remains available through the button.");
                if (_hotkey.Status != HotKeyStatus.Registered)
                    ToolTipService.SetToolTip(button, _hotkey.Status == HotKeyStatus.Conflict
                        ? "Ctrl+Alt+Space is in use by another app. Use this button or Ctrl+Shift+M."
                        : "Global capture shortcut unavailable. Use this button or Ctrl+Shift+M.");
            }
        }
    }

    private (uint Modifiers, uint Key) ReadHotkey()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(_settingsPath));
                var root = json.RootElement;
                var modifiers = root.GetProperty("modifiers").GetUInt32();
                var key = root.GetProperty("virtualKey").GetUInt32();
                if (key is > 0 and < 256 && (modifiers & ~0x000F) == 0 && (modifiers & 0x0007) != 0)
                    return (modifiers, key);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        return (0x0003, 0x20);
    }

    public void ConfigureHotkey(uint modifiers, uint virtualKey)
    {
        if (virtualKey is 0 or > 255 || (modifiers & ~0x000F) != 0 || (modifiers & 0x0007) == 0)
            throw new ArgumentException("Choose a modified keyboard shortcut.");
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new { modifiers, virtualKey }));
        _hotkey.Dispose();
        _hotkey = new CaptureHotKey(_hwnd, HotkeyId, modifiers, virtualKey);
    }

    private nint WindowMessage(nint hwnd, uint message, nuint wparam, nint lparam, nuint id, nuint data)
    {
        if (message == WmHotkey && (int)wparam == HotkeyId)
        {
            _queue.TryEnqueue(() => { if (!_disposed) _ = OpenAsync(); });
            return 0;
        }
        return DefSubclassProc(hwnd, message, wparam, lparam);
    }

    public async Task OpenAsync()
    {
        if (_disposed || _dialog != null) return;
        var result = _tracker.CaptureLastExternal();
        var dialog = new CaptureDialog(result, SaveAsync, StartAsync, _closing.Token);
        _dialog = dialog;
        try { await dialog.ShowAsync(_window.Content.XamlRoot, _hotkey.Status); }
        finally { if (ReferenceEquals(_dialog, dialog)) _dialog = null; dialog.Dispose(); }
    }

    private async Task<TaskMap> SaveAsync(TaskMap draft, CancellationToken cancel)
    {
        var project = draft.ProjectPath;
        var saved = await ProjectClient.SaveMapAsync(project, draft, draft.Revision == 0 ? null : draft.Revision, cancel);
        if (saved is null)
        {
            try { if (!_ownedRuntimes.ContainsKey(project)) _ownedRuntimes[project] = await ProjectRuntime.OpenAsync(project); }
            catch (IOException)
            {
                // A runtime may own the project while its broker is busy; never take a second owner.
                saved = await ProjectClient.SaveMapAsync(project, draft, draft.Revision == 0 ? null : draft.Revision, cancel);
                if (saved is null) throw new IOException("The project is open but its task broker is busy. Retry saving in a moment.");
            }
            if (saved is null) saved = await ProjectClient.SaveMapAsync(project, draft, draft.Revision == 0 ? null : draft.Revision, cancel);
        }
        if (saved is null) throw new IOException("The project broker did not accept the draft.");
        if (OnMapSaved != null) await OnMapSaved();
        return saved;
    }

    private async Task<IReadOnlyList<string>> StartAsync(TaskMap map, CancellationToken cancel)
        => await ProjectClient.StartMapAsync(map.ProjectPath, map.Id, cancel)
           ?? throw new IOException("The project broker is busy. No task start was confirmed.");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _closing.Cancel();
        _dialog?.Dispose();
        _hotkey.Dispose();
        _tracker.Dispose();
        if (_hwnd != 0) RemoveWindowSubclass(_hwnd, _procedure, (nuint)HotkeyId);
        foreach (var runtime in _ownedRuntimes.Values) _ = runtime.DisposeAsync().AsTask();
        _closing.Dispose();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hwnd, uint message, nuint wparam, nint lparam, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wparam, nint lparam);
}






