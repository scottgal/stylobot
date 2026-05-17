using Mostlylucid.BotDetection.Identity;

namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>
///     Read-only proxy over <c>/api/v1/fingerprints</c>. Backs the Identities tab in
///     remote mode.
/// </summary>
internal sealed class RemoteFingerprintReader : IFingerprintReader
{
    private readonly GatewayApiClient _api;

    public RemoteFingerprintReader(GatewayApiClient api) => _api = api;

    public async Task<IReadOnlyList<Fingerprint>> ListFingerprintsAsync(CancellationToken ct = default)
    {
        var list = await _api.GetEnvelopeAsync<List<Fingerprint>>("/api/v1/fingerprints", ct);
        return list ?? new List<Fingerprint>();
    }

    public async Task<Fingerprint?> GetFingerprintAsync(string fingerprintId, CancellationToken ct = default)
        => await _api.GetEnvelopeAsync<Fingerprint>(
            $"/api/v1/fingerprints/{Uri.EscapeDataString(fingerprintId)}", ct);

    public async Task<IReadOnlyDictionary<string, int>> GetUnabsorbedObservationCountsAsync(CancellationToken ct = default)
    {
        var counts = await _api.GetEnvelopeAsync<Dictionary<string, int>>(
            "/api/v1/fingerprints/unabsorbed-counts", ct);
        return counts ?? new Dictionary<string, int>();
    }

    public async Task<int> GetUnabsorbedObservationCountAsync(string fingerprintId, CancellationToken ct = default)
        => await _api.GetEnvelopeAsync<int>(
            $"/api/v1/fingerprints/{Uri.EscapeDataString(fingerprintId)}/unabsorbed-count", ct);
}
