using Microsoft.UI.Xaml;

namespace AgentOS.App;

public partial class App : Application
{
    private Window? _window;
    private CaptureController? _capture;
    private NotificationController? _notifications;
    public App()
    {
        UnhandledException += (_, e) =>
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentOS");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app-errors.log"), DateTimeOffset.UtcNow + " " + e.Exception + "\n");
        };
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        if (!Environment.GetCommandLineArgs().Contains("--ui-preview"))
        {
            _capture = new CaptureController(_window);
            ((MainWindow)_window).Capture = _capture;
            try { _notifications = new NotificationController((MainWindow)_window, _capture); _notifications.NavigateNotice = ((MainWindow)_window).NavigateNotice; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) { ((MainWindow)_window).ShowNotificationStatus("Notification history is unavailable."); }
        }
        _window.Activate();
    }
}




