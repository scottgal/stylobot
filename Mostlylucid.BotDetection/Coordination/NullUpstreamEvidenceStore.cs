using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Coordination;

/// <summary>
/// Default no-op implementation. Headers carry all data; no external store needed.
/// </summary>
public sealed class NullUpstreamEvidenceStore : IUpstreamEvidenceStore
{
    public bool IsActive => false;

    public Task StoreAsync(string requestId, AggregatedEvidence evidence, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<AggregatedEvidence?> RetrieveAsync(string requestId, CancellationToken ct = default)
        => Task.FromResult<AggregatedEvidence?>(null);
}
