using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using AgentOS.Core.Adapters;
using AgentOS.Core.Coordination;
using Microsoft.Win32.SafeHandles;

namespace AgentOS.Tests;

internal static class WindowsResourceAdapterChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test, string root)
    {
        await test("Windows resource adapter forced 8.3 aliases converge through service", () =>
        {
            var fixture = Fresh(root, "aliases");
            var drive = new DriveInfo(Path.GetPathRoot(fixture)!);
            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"EXCLUDED 8.3 aliases: fixture filesystem is {drive.DriveFormat}, not NTFS.");
                return Task.CompletedTask;
            }
            var directory = Path.Combine(fixture, "Descriptive Long Directory Name");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "Descriptive Long Filename.txt");
            File.WriteAllText(file, "owned fixture content");
            SetShortName(directory, "ALIASDIR");
            SetShortName(file, "ALIAS83.TXT");
            var shortPath = ShortPath(file);
            Check(!string.Equals(shortPath, file, StringComparison.OrdinalIgnoreCase), "8.3 path did not differ.");
            Check(shortPath.Contains("ALIASDIR", StringComparison.OrdinalIgnoreCase) &&
                  shortPath.Contains("ALIAS83.TXT", StringComparison.OrdinalIgnoreCase), "8.3 path lacks forced names.");
            Check(File.ReadAllText(shortPath) == File.ReadAllText(file), "Alias content differs.");
            var adapter = new WindowsResourceAdapter();
            var longResource = adapter.Resolve(new("file:" + file, "write"));
            var shortResource = adapter.Resolve(new("file:" + shortPath, "read"));
            Check(longResource.Key == shortResource.Key && longResource.Identity == shortResource.Identity,
                "Long and short paths did not converge on one canonical identity.");
            using var service = new CoordinationService(Fresh(root, "alias-store"));
            var a = service.Authenticate(service.Register("a"));
            var b = service.Authenticate(service.Register("b"));
            var c = service.Authenticate(service.Register("c"));
            var held = service.RequestResources(a, [new("file:" + file, "write")], "held");
            var generation = service.Snapshot().Generation;
            var waiting = service.RequestResources(b, [new("file:" + shortPath, "read"), new("id:free", "write")], "waiting");
            Check(waiting.State == "pending" && service.Snapshot().Generation == generation, "Alias bundle changed generation while waiting.");
            Check(service.Snapshot().Entries.Single(e => e.Id == b.Address).Holds.Count == 0, "Waiting bundle partially admitted.");
            Check(service.RequestResources(c, [new("id:free", "write")], "free").State == "pending", "Free claim escaped queued bundle.");
            service.ReleaseResources(a, held.Id, 1, "release");
            Check(service.ReadAdmission(b, waiting.Id).State == "admitted", "Alias waiter did not promote.");
            var longDirectory = adapter.Resolve(new("file:" + directory, "write"));
            var shortDirectory = adapter.Resolve(new("file:" + Path.GetDirectoryName(shortPath)!, "write"));
            Check(longDirectory.Key == shortDirectory.Key && longDirectory.Identity == shortDirectory.Identity &&
                  adapter.Overlaps(longDirectory, shortResource), "Short directory did not identify containing scope.");
            service.ReleaseResources(b, waiting.Id, 2, "release-alias-bundle");
            var directoryHold = service.RequestResources(a, [new("file:" + directory, "write")], "directory-hold");
            Check(directoryHold.State == "admitted", "Directory hold did not admit.");
            var directoryGeneration = service.Snapshot().Generation;
            var shortChild = service.RequestResources(c, [new("file:" + shortPath, "read")], "short-child");
            Check(shortChild.State == "pending" && service.Snapshot().Generation == directoryGeneration,
                "Short child escaped directory hold or changed generation.");
            return Task.CompletedTask;
        });
        await test("Windows resource adapter boundaries and hardlink revalidation", () =>
        {
            var fixture = Fresh(root, "hardlinks");
            var parent = Path.Combine(fixture, "Descriptive Parent Directory");
            var sibling = Path.Combine(fixture, "Descriptive Parent Directory 2");
            Directory.CreateDirectory(parent);
            Directory.CreateDirectory(sibling);
            var child = Path.Combine(parent, "child.txt");
            var other = Path.Combine(sibling, "other.txt");
            File.WriteAllText(child, "x");
            File.WriteAllText(other, "y");
            using var service = new CoordinationService(Fresh(root, "hardlink-store"));
            var a = service.Authenticate(service.Register("a"));
            var b = service.Authenticate(service.Register("b"));
            var held = service.RequestResources(a, [new("file:" + parent, "write")], "directory");
            var waiting = service.RequestResources(b, [new("file:" + child.ToUpperInvariant().Replace('\\', '/'), "read")], "child");
            Check(waiting.State == "pending", "Child path escaped directory scope.");
            Check(service.RequestResources(b, [new("file:" + other, "write")], "sibling").State == "admitted",
                "Sibling conflicted across directory boundary.");
            var hard = Path.Combine(sibling, "hard.txt");
            if (!CreateHardLink(hard, child, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Owned hardlink fixture creation failed.");
            Refuses<ResourceValidationException>(() => service.RequestResources(b, [new("file:" + hard, "write")], "hard"));
            Refuses<ResourceValidationException>(() => service.RequestResources(b, [new("file:" + sibling, "write")], "hard-directory"));
            service.ReleaseResources(a, held.Id, 1, "release");
            Check(service.ReadAdmission(b, waiting.Id).State == "suspended", "Changed link count did not suspend queued file.");
            return Task.CompletedTask;
        });
    }

    private static string Fresh(string root, string name)
    {
        var path = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Refuses<T>(System.Action work) where T : Exception
    {
        try { work(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void SetShortName(string path, string alias)
    {
        using var handle = CreateFile(path, 0x10000, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateFileW DELETE failed for owned fixture {path}.");
        if (!SetFileShortName(handle, alias))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"SetFileShortNameW failed for owned fixture {path} as {alias}.");
    }

    private static string ShortPath(string path)
    {
        var buffer = new StringBuilder(32768);
        var length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetShortPathNameW failed for owned fixture {path}.");
        return buffer.ToString();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "SetFileShortNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileShortName(SafeFileHandle handle, string alias);
    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint size);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string name, string existing, IntPtr reserved);
}


