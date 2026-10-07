using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentOS.Core;

internal static partial class PrivateGit
{
    public static async Task Checkout(string workspace, string commit)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await Commands.Git(workspace, "-c", "core.symlinks=true", "-c", "submodule.recurse=false", "checkout", "--detach", commit);
            if (result.ExitCode == 0) return;
            if ((result.Error.Contains("symbolic link",StringComparison.OrdinalIgnoreCase)||result.Error.Contains("symlink",StringComparison.OrdinalIgnoreCase))&&(result.Error.Contains("Permission denied",StringComparison.OrdinalIgnoreCase)||result.Error.Contains("privilege",StringComparison.OrdinalIgnoreCase)))throw new IOException("Windows symlink capability unavailable: "+result.Error);
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
        CheckPrivateTree(workspace);
        foreach (var relative in new[] { "objects/info/alternates", "commondir", "gitdir" })
            if (File.Exists(Path.Combine(workspace, ".git", relative.Replace('/', Path.DirectorySeparatorChar))))
                throw new IOException("Private Git operations refuse references to external Git storage.");
        var config = Path.Combine(workspace, ".git", "config");
        File.WriteAllText(config, "[core]\nrepositoryformatversion = 0\nfilemode = false\nbare = false\nlogallrefupdates = true\nignorecase = true\nsymlinks = true\nfsmonitor = false\n[user]\nname = agent-os\nemail = agent-os@localhost\n");
    }
    public static void CheckPrivateTree(string workspace)
    {
        var profile=GitProjectProfile.Inspect(workspace).GetAwaiter().GetResult();
        if(profile.Kind!="main"||!GitProjectProfile.SamePath(profile.CommonDirectory,Path.Combine(profile.SourcePath,".git")))throw new IOException("Private workspace is not an isolated local clone.");
        var listing=Commands.Git(workspace,"ls-files","--stage","-z").GetAwaiter().GetResult().Checked();
        var links=listing.Split('\0',StringSplitOptions.RemoveEmptyEntries).Where(x=>x.StartsWith("120000 ",StringComparison.Ordinal)).Select(x=>x[(x.IndexOf('\t')+1)..].Replace('/',Path.DirectorySeparatorChar)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modules=listing.Split('\0',StringSplitOptions.RemoveEmptyEntries).Where(x=>x.StartsWith("160000 ",StringComparison.Ordinal)).Select(x=>x[(x.IndexOf('\t')+1)..].Replace('/',Path.DirectorySeparatorChar)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        CheckTree(new DirectoryInfo(workspace),workspace,links,modules);
    }
    private static void CheckTree(DirectoryInfo directory,string root,HashSet<string> links,HashSet<string> modules)
    {
        foreach(var entry in directory.EnumerateFileSystemInfos())
        {
            var relative=Path.GetRelativePath(root,entry.FullName);
            if((entry.Attributes&FileAttributes.ReparsePoint)!=0)
            {if(!links.Contains(relative)||entry.LinkTarget==null)throw new IOException("Private workspace link is not a tracked symbolic link.");CheckLink(root,entry.FullName,entry.LinkTarget);continue;}
            if(entry is DirectoryInfo child){if(modules.Contains(relative)){CheckPrivateTree(child.FullName);continue;}CheckTree(child,root,links,modules);}
            else{using var handle=File.OpenHandle(entry.FullName,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);if(!GetFileInformationByHandle(handle,out var identity))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());if(identity.Links!=1)throw new IOException("Private Git operations refuse hard-linked files.");}
        }
    }    [StructLayout(LayoutKind.Sequential)] private struct FileIdentity
    { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIdentity identity);
}
