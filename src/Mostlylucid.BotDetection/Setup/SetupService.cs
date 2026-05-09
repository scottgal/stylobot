namespace Mostlylucid.BotDetection.Setup;

public class SetupService(IEnumerable<ISetupResource> resources)
{
    private readonly IReadOnlyList<ISetupResource> _resources = resources.ToList();

    public async Task<IReadOnlyList<ResourceStatus>> CheckAllAsync(CancellationToken ct = default)
    {
        var tasks = _resources.Select(r => r.CheckAsync(ct));
        return await Task.WhenAll(tasks);
    }

    public async Task DownloadMissingAsync(IProgress<string>? progress, bool force, CancellationToken ct = default)
    {
        foreach (var resource in _resources)
        {
            var status = await resource.CheckAsync(ct);
            if (status.Presence == ResourcePresence.Fresh && !force)
                continue;

            progress?.Report($"Downloading {resource.Name}...");
            await resource.DownloadAsync(progress, ct);
        }
    }
}
