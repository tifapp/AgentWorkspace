using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentOS.Core;

internal static class PrivateGit
{
    public static async Task Checkout(string workspace, string commit)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await Commands.Git(workspace, "checkout", "--detach", commit);
            if (result.ExitCode == 0) return;
            if (attempt >= 4 || !result.Error.Contains("logs/HEAD", StringComparison.Ordinal) || !result.Error.Contains("Permission denied", StringComparison.Ordinal)) { result.Checked(); return; }
            // Git for Windows maps transient sharing violations on its private reflog to EACCES.
            // Retry only this idempotent private checkout; shared publication is never replayed.
            await Task.Delay(50 * (attempt + 1));
        }
    }
    // No project-controlled includes, filters, monitors, remotes or hooks enter trusted Git.
    public static void Prepare(string workspace)
    {
        SafePaths.Project(workspace);
        CheckTree(new DirectoryInfo(workspace));
        foreach (var relative in new[] { "objects/info/alternates", "commondir", "gitdir" })
            if (File.Exists(Path.Combine(workspace, ".git", relative.Replace('/', Path.DirectorySeparatorChar))))
                throw new IOException("Private Git operations refuse references to external Git storage.");
        var config = Path.Combine(workspace, ".git", "config");
        File.WriteAllText(config, "[core]\nrepositoryformatversion = 0\nfilemode = false\nbare = false\nlogallrefupdates = true\nignorecase = true\nsymlinks = false\nfsmonitor = false\n[user]\nname = agent-os\nemail = agent-os@localhost\n");
    }
    private static void CheckTree(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Private Git operations refuse symbolic links and junctions.");
            if (entry is DirectoryInfo child) CheckTree(child);
            else
            {
                using var handle = File.OpenHandle(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (!GetFileInformationByHandle(handle, out var identity)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if (identity.Links != 1) throw new IOException("Private Git operations refuse hard-linked files.");
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileIdentity
    { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIdentity identity);
}
