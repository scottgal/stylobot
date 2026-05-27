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

    // No control-plane endpoint exposes the L1 key map yet, so remote-mode
    // dashboards skip the upstream-trust fallback and render the calibrating
    // placeholder instead of the centroid. Safe degradation, no broken section.
    public Task<string?> LookupFingerprintIdAsync(string primarySignature, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public async Task<IReadOnlyDictionary<string, int>> GetUnabsorbedObservationCountsAsync(CancellationToken ct = default)
    {
        var counts = await _api.GetEnvelopeAsync<Dictionary<string, int>>(
            "/api/v1/fingerprints/unabsorbed-counts", ct);
        return counts ?? new Dictionary<string, int>();
    }

    public async Task<int> GetUnabsorbedObservationCountAsync(string fingerprintId, CancellationToken ct = default)
        => await _api.GetEnvelopeAsync<int>(
            $"/api/v1/fingerprints/{Uri.EscapeDataString(fingerprintId)}/unabsorbed-count", ct);

    public Task<IReadOnlyList<NearestFingerprint>> GetNearestForSignatureAsync(
        string primarySignature, int k, CancellationToken ct = default)
        // The remote control-plane API does not yet expose a centroid-KNN endpoint;
        // when it does, this becomes another GetEnvelopeListAsync call. Returning
        // empty keeps the "Looks like" panel a no-op rather than a broken section
        // when the dashboard is pointed at a remote gateway.
        => Task.FromResult<IReadOnlyList<NearestFingerprint>>(Array.Empty<NearestFingerprint>());
}
