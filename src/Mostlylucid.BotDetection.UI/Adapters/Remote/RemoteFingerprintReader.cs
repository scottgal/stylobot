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
        => await _api.GetEnvelopeListAsync<Fingerprint>("/api/v1/fingerprints", ct);

    public async Task<Fingerprint?> GetFingerprintAsync(string fingerprintId, CancellationToken ct = default)
        => await _api.GetEnvelopeAsync<Fingerprint>(
            $"/api/v1/fingerprints/{Uri.EscapeDataString(fingerprintId)}", ct);

    public async Task<string?> LookupFingerprintIdAsync(string primarySignature, CancellationToken ct = default)
        => await _api.GetEnvelopeAsync<string>(
            $"/api/v1/fingerprints/lookup/{Uri.EscapeDataString(primarySignature)}", ct);

    public async Task<string?> LookupSignatureForFingerprintAsync(string fingerprintId, CancellationToken ct = default)
        => await _api.GetEnvelopeAsync<string>(
            $"/api/v1/fingerprints/lookup-by-id/{Uri.EscapeDataString(fingerprintId)}", ct);

    public async Task<IReadOnlyDictionary<string, int>> GetUnabsorbedObservationCountsAsync(CancellationToken ct = default)
    {
        var counts = await _api.GetEnvelopeAsync<Dictionary<string, int>>(
            "/api/v1/fingerprints/unabsorbed-counts", ct);
        return counts ?? new Dictionary<string, int>();
    }

    public async Task<int> GetUnabsorbedObservationCountAsync(string fingerprintId, CancellationToken ct = default)
        => await _api.GetEnvelopeAsync<int>(
            $"/api/v1/fingerprints/{Uri.EscapeDataString(fingerprintId)}/unabsorbed-count", ct);

    public async Task<IReadOnlyList<NearestFingerprint>> GetNearestForSignatureAsync(
        string primarySignature, int k, CancellationToken ct = default)
        => await _api.GetEnvelopeListAsync<NearestFingerprint>(
            $"/api/v1/fingerprints/nearest/{Uri.EscapeDataString(primarySignature)}?k={k}", ct);

    public async Task<IReadOnlyList<RootHistoryEntry>> GetRootHistoryAsync(
        string fingerprintId, int limit = 20, CancellationToken ct = default)
        => await _api.GetEnvelopeListAsync<RootHistoryEntry>(
            $"/api/v1/fingerprints/{Uri.EscapeDataString(fingerprintId)}/root-history?limit={limit}", ct);
}
