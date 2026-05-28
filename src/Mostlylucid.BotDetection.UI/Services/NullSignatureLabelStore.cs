namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Sqlite-free no-op signature label store. Operator-set labels (the
///     "rename this signature" admin surface) are a write-side feature.
///     Commercial gateways register this so the FOSS Sqlite TryAdd never
///     wins. Reads return null/empty -- the dashboard falls back to the
///     algorithmic name (Country + UA family + role) for any row.
/// </summary>
public sealed class NullSignatureLabelStore : ISignatureLabelStore
{
    public Task<SignatureLabel> UpsertAsync(SignatureLabel label, CancellationToken ct = default)
        => Task.FromResult(label);

    public Task<SignatureLabel?> GetLatestAsync(string signature, CancellationToken ct = default)
        => Task.FromResult<SignatureLabel?>(null);

    public Task<IReadOnlyList<SignatureLabel>> ListSinceAsync(DateTime? since, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SignatureLabel>>(Array.Empty<SignatureLabel>());

    public Task RemoveAsync(string signature, string labeledBy, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyDictionary<SignatureLabelKind, int>> GetCountsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<SignatureLabelKind, int>>(
            new Dictionary<SignatureLabelKind, int>());
}
