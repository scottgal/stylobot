using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>
///     Read-only proxy over <c>/api/v1/labels</c>. Write paths (UpsertAsync / RemoveAsync)
///     throw <see cref="NotSupportedException"/> - the remote viewer is read-only; the
///     dashboard's "label this signature" button is hidden in remote mode.
/// </summary>
internal sealed class RemoteSignatureLabelStore : ISignatureLabelStore
{
    private readonly GatewayApiClient _api;

    public RemoteSignatureLabelStore(GatewayApiClient api) => _api = api;

    public async Task<SignatureLabel?> GetLatestAsync(string signature, CancellationToken ct = default)
        => await _api.GetEnvelopeAsync<SignatureLabel>($"/api/v1/labels/{Uri.EscapeDataString(signature)}", ct);

    public async Task<IReadOnlyList<SignatureLabel>> ListSinceAsync(DateTime? since, int limit, CancellationToken ct = default)
    {
        var path = since.HasValue
            ? $"/api/v1/labels?since={Uri.EscapeDataString(since.Value.ToString("o"))}&limit={limit}"
            : $"/api/v1/labels?limit={limit}";
        return await _api.GetEnvelopeListAsync<SignatureLabel>(path, ct);
    }

    public async Task<IReadOnlyDictionary<SignatureLabelKind, int>> GetCountsAsync(CancellationToken ct = default)
    {
        var counts = await _api.GetEnvelopeAsync<Dictionary<SignatureLabelKind, int>>(
            "/api/v1/labels/counts", ct);
        return counts ?? new Dictionary<SignatureLabelKind, int>();
    }

    public Task<SignatureLabel> UpsertAsync(SignatureLabel label, CancellationToken ct = default)
        => throw new NotSupportedException("Label writes are not supported against a remote gateway.");

    public Task RemoveAsync(string signature, string labeledBy, CancellationToken ct = default)
        => throw new NotSupportedException("Label writes are not supported against a remote gateway.");
}
