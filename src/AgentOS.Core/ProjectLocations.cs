using System.Text.Json;

namespace AgentOS.Core;

internal static class ProjectLocations
{
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentOS", "project-locations");
    public static string? Find(string project)
    {
        var file = Path.Combine(Root, StateStore.Key(project) + ".json"); if (!File.Exists(file)) return null;
        var saved = JsonSerializer.Deserialize<Location>(File.ReadAllText(file), JsonFormat.Options) ?? throw new InvalidDataException("Saved project location is empty.");
        if (saved.Schema != 1 || !string.Equals(saved.Project, project, StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(saved.DataRoot)) throw new InvalidDataException("Saved project location is invalid.");
        return saved.DataRoot;
    }
    public static void Remember(string project, string dataRoot)
    {
        Directory.CreateDirectory(Root); var file = Path.Combine(Root, StateStore.Key(project) + ".json");
        File.WriteAllText(file + ".next", JsonSerializer.Serialize(new Location(1, project, dataRoot), JsonFormat.Options));
        File.Move(file + ".next", file, true);
    }
    private sealed record Location(int Schema, string Project, string DataRoot);
}
