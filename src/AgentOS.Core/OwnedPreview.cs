using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AgentOS.Core;

internal sealed class OwnedPreview : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public OwnedPreview(string workspace, int preferredPort)
    {
        workspace = SafePaths.Project(workspace); PrivateGit.Prepare(workspace);
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(workspace, file).Replace('\\', '/');
            if (relative.Split('/').Any(segment => segment.StartsWith('.')) || new FileInfo(file).Length > 65536) continue;
            var bytes = File.ReadAllBytes(file); total += bytes.Length;
            if (total > 8 * 1024 * 1024 || _files.Count >= 2000) throw new IOException("Preview exceeds its bounded source snapshot.");
            _files[relative] = bytes;
        }
        _listener = new TcpListener(IPAddress.Loopback, preferredPort); _listener.Server.ExclusiveAddressUse = true;
        try { _listener.Start(); }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
        { _listener.Stop(); _listener = new TcpListener(IPAddress.Loopback, 0); _listener.Server.ExclusiveAddressUse = true; _listener.Start(); }
        _server = Serve(SafePaths.Project(workspace));
    }
    private async Task Serve(string workspace)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    using var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    var request = await reader.ReadLineAsync(timeout.Token) ?? "";
                    var parts = request.Split(' '); var path = parts.Length == 3 && parts[0] == "GET" ? Uri.UnescapeDataString(parts[1].Split('?')[0]).TrimStart('/') : "";
                    if (path.Length == 0) path = "README.md";
                    var allowed = _files.TryGetValue(path, out var content);
                    var body = content ?? Encoding.UTF8.GetBytes("Preview file unavailable.");
                    var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + (allowed ? "200 OK" : "404 Not Found") + "\r\nContent-Type: text/plain; charset=utf-8\r\nX-Content-Type-Options: nosniff\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, timeout.Token); await stream.WriteAsync(body, timeout.Token);
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or ArgumentException) { }
            }
        }
        catch (Exception e) when (_stop.IsCancellationRequested && e is OperationCanceledException or SocketException) { }
    }
    public async ValueTask DisposeAsync()
    { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; _stop.Cancel(); _listener.Stop(); await _server; _stop.Dispose(); }
}
