namespace Mostlylucid.BotDetection.Setup;

public enum ResourcePresence
{
    Fresh,
    Stale,
    Missing
}

public record ResourceStatus(
    string Name,
    string Description,
    ResourcePresence Presence,
    string? Path,
    string? Detail = null);

public interface ISetupResource
{
    string Name { get; }
    string Description { get; }
    Task<ResourceStatus> CheckAsync(CancellationToken ct = default);
    Task DownloadAsync(IProgress<string>? progress, CancellationToken ct = default);
}
