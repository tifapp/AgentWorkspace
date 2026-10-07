namespace AgentOS.Core.Coordination;

internal sealed record CanonicalResource(string Key, string Mode, string? Path, string? Identity, bool Directory);

internal interface IResourceAdapter
{
    CanonicalResource Resolve(ResourceRequest request);
    void Revalidate(CanonicalResource resource);
    bool Overlaps(CanonicalResource left, CanonicalResource right);
}

internal sealed class ResourceValidationException : IOException
{
    internal ResourceValidationException(string message, Exception? cause = null) : base(message, cause) { }
}
