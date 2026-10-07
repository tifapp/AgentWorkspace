using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOS.Core;

internal sealed class StateStore
{
    public string Root { get; }
    public string StatePath => Path.Combine(Root, "state.json");
    public StateStore(string root) { Root = root; Directory.CreateDirectory(root); }
    public ProjectState? Read()
    {
        if (!File.Exists(StatePath)) return null;
        var state = JsonSerializer.Deserialize<ProjectState>(File.ReadAllText(StatePath), JsonFormat.Options)
                    ?? throw new InvalidDataException("The saved project state is empty. Restore a known backup; it was not reset.");
        if (state.Schema is not (1 or 2)) throw new InvalidDataException($"Unsupported project state version {state.Schema}. No state was changed.");
        if (state.Schema == 1)
        {
            var backup = StatePath + ".schema1.bak";
            if (!File.Exists(backup)) File.Copy(StatePath, backup, false);
            state.Maps ??= []; state.Schema = 2;
        }
        if (state.Maps == null) throw new InvalidDataException("Task maps are malformed; state was not changed.");
        if (state.Work == null || state.Work.Any(x => x.PublishedPathObjects == null) || state.Conflicts == null || state.PeerMessages == null || state.InterruptRequests == null || state.Escalations == null) throw new InvalidDataException("Conflict protocol state is malformed; state was not changed.");
        try
        {
            foreach (var map in state.Maps) TaskMapRules.Validate(map);
            if (state.Maps.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != state.Maps.Count) throw new ArgumentException("Duplicate map identity.");
        }
        catch (Exception e) when (e is ArgumentException or NullReferenceException) { throw new InvalidDataException("Saved task maps are invalid; state was not changed.", e); }
        return state;
    }
    public void Save(ProjectState state)
    {
        state.Generation++;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonFormat.Options);
        var temp = StatePath + ".new";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(temp, StatePath, true); break; }
            catch (IOException e) when (attempt < 20 && (e.HResult & 0xFFFF) is 32 or 33)
            { Thread.Sleep(10); } // Brief Windows sharing violations from a diagnostic reader.
            catch (UnauthorizedAccessException) when (attempt < 20)
            { Thread.Sleep(10); } // MoveFileEx reports ERROR_ACCESS_DENIED for an open destination too.
        }
    }
    public static string Key(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..24];
    public static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

internal static class SafePaths
{
    public static string Project(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Choose an existing local Git project.");
        if (full.StartsWith(@"\\")) throw new IOException("Network projects are not supported. Choose a local disk.");
        for (DirectoryInfo? d = new(full); d != null; d = d.Parent)
            if ((d.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Project aliases and junctions are not supported. Open the real folder path.");
        return full;
    }
    public static void DeleteOwnedDirectory(string root, string target)
    {
        var expected = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(target);
        if (!full.StartsWith(expected, StringComparison.OrdinalIgnoreCase) || full == expected.TrimEnd('\\'))
            throw new IOException("Cleanup target is outside the owned workspaces.");
        if (!Directory.Exists(full)) return;
        DeleteTree(full);
    }
    private static void DeleteTree(string path)
    {
        var dir = new DirectoryInfo(path);
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) { dir.Delete(); return; }
        foreach (var file in dir.EnumerateFiles()) { file.IsReadOnly = false; file.Delete(); }
        foreach (var child in dir.EnumerateDirectories()) DeleteTree(child.FullName);
        dir.Delete();
    }
}
