using System.ComponentModel;
using System.Runtime.InteropServices;
using AgentOS.Core.Coordination;
using Microsoft.Win32.SafeHandles;

namespace AgentOS.Core.Adapters;

internal sealed class WindowsResourceAdapter : IResourceAdapter
{
    public CanonicalResource Resolve(ResourceRequest request)
    {
        try { return Canonical(request); }
        catch (Exception error) when (error is IOException or Win32Exception or NotSupportedException or UnauthorizedAccessException)
        {
            throw new ResourceValidationException(error.Message, error);
        }
    }

    public void Revalidate(CanonicalResource resource)
    {
        try { RevalidateCore(resource); }
        catch (Exception error) when (error is IOException or Win32Exception or NotSupportedException or UnauthorizedAccessException)
        {
            throw new ResourceValidationException(error.Message, error);
        }
    }

    public bool Overlaps(CanonicalResource left, CanonicalResource right)
    {
        if (left.Path is null || right.Path is null)
            return left.Key == right.Key;
        if (left.Identity == right.Identity)
            return true;
        return left.Path == right.Path ||
            (left.Directory && Descendant(right.Path, left.Path)) ||
            (right.Directory && Descendant(left.Path, right.Path));
    }

    private static bool Descendant(string child, string parent) =>
        child.StartsWith(parent.EndsWith('\\') ? parent : parent + "\\", StringComparison.OrdinalIgnoreCase);

    private static CanonicalResource Canonical(ResourceRequest request)
    {
        if (request is null || request.Mode is not ("read" or "write"))
            throw new ArgumentException("Mode must be read or write.");
        if (request.Resource is null)
            throw new ArgumentException("Resource required.");
        if (request.Resource.StartsWith("id:", StringComparison.Ordinal))
        {
            var key = request.Resource[3..];
            if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl))
                throw new ArgumentException("Invalid id resource.");
            return new CanonicalResource("id:" + key, request.Mode, null, null, false);
        }
        if (!request.Resource.StartsWith("file:", StringComparison.Ordinal))
            throw new ArgumentException("Resource must begin file: or id:.");
        var raw = request.Resource[5..];
        if (raw.Length == 0 || raw.Any(char.IsControl))
            throw new ArgumentException("Invalid file resource.");
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(raw) ||
            raw.StartsWith(@"\\", StringComparison.Ordinal))
            throw new NotSupportedException("Only fully qualified local Windows paths are supported.");

        var full = Path.GetFullPath(raw);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse path is ambiguous.");
        }
        var directory = Directory.Exists(full);
        if (!directory && !File.Exists(full))
            throw new IOException("Resource path must exist.");

        using var handle = Open(full);
        var buffer = new char[32768];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var final = new string(buffer, 0, (int)length);
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Network paths require a verified adapter.");
        if (final.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            final = final[4..];
        final = Path.TrimEndingDirectorySeparator(final).ToUpperInvariant();

        if (!GetFileInformationByHandle(handle, out var info) ||
            (info.IndexHigh == 0 && info.IndexLow == 0))
            throw new IOException("Stable file identity unavailable.");
        if (!directory && info.Links != 1)
            throw new NotSupportedException("Hard-linked files require verified containment.");
        if (directory)
            VerifyDirectory(full);
        var identity = $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}";
        return new CanonicalResource("file:" + final, request.Mode, final, identity, directory);
    }
    private static void VerifyDirectory(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var count = 0;
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (++count > 10000)
                    throw new NotSupportedException("Directory too large for bounded identity validation.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new NotSupportedException("Directory contains a reparse path.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    using var handle = Open(entry);
                    if (!GetFileInformationByHandle(handle, out var info) || info.Links != 1)
                        throw new NotSupportedException("Directory contains a hard-linked or unverified file.");
                }
            }
        }
    }

    private static void RevalidateCore(CanonicalResource resource)
    {
        if (resource.Path is null)
            return;
        var latest = Canonical(new ResourceRequest("file:" + resource.Path, resource.Mode));
        if (latest.Key != resource.Key || latest.Identity != resource.Identity ||
            latest.Directory != resource.Directory)
            throw new IOException("Queued resource identity changed.");
    }

    private static SafeFileHandle Open(string path)
    {
        var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, char[] path, uint size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
}
