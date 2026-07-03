using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Identity.BrowserModes;

namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>
///     Read-only proxy over <c>/api/v1/fingerprints/{id}/browser-modes</c>.
///     Backs the composite-spec step 7 Modes panel on a remote-mode dashboard
///     host so it can render per-mode rows without direct DB access.
///
///     Only the read methods are wired — the write surface (Upsert / Delete /
///     RecordModeObservation / ListUnabsorbedModeObservations /
///     AbsorbModeObservations / ListFingerprintIdsWithModes) belongs to the
///     gateway's persistence and absorption pipeline and would be meaningless
///     called from a dashboard host. They throw <see cref="NotSupportedException"/>
///     with a message naming the right caller so future remote-mode adapters
///     don't accidentally route a write through HTTP.
/// </summary>
internal sealed class RemoteFingerprintBrowserModeStore : IFingerprintBrowserModeStore
{
    private readonly GatewayApiClient _api;

    public RemoteFingerprintBrowserModeStore(GatewayApiClient api) => _api = api;

    public async Task<IReadOnlyList<FingerprintBrowserMode>> GetModesAsync(
        string fingerprintId, CancellationToken ct = default)
        => await _api.GetEnvelopeListAsync<FingerprintBrowserMode>(
            $"/api/v1/fingerprints/{Uri.EscapeDataString(fingerprintId)}/browser-modes", ct);

    public async Task<FingerprintBrowserMode?> GetModeAsync(
        string fingerprintId, string modeId, CancellationToken ct = default)
    {
        var rows = await GetModesAsync(fingerprintId, ct);
        for (var i = 0; i < rows.Count; i++)
            if (string.Equals(rows[i].ModeId, modeId, StringComparison.OrdinalIgnoreCase))
                return rows[i];
        return null;
    }

    public Task UpsertModeAsync(FingerprintBrowserMode mode, CancellationToken ct = default)
        => throw new NotSupportedException(
            "UpsertModeAsync is gateway-only — the dashboard host must not write per-mode centroids over REST.");

    public Task DeleteModeAsync(string fingerprintId, string modeId, CancellationToken ct = default)
        => throw new NotSupportedException(
            "DeleteModeAsync is gateway-only — the dashboard host must not delete mode rows over REST.");

    public Task RecordModeObservationAsync(
        RequestScope scope,
        string fingerprintId, string modeId, float[] vector,
        string? uaFamily = null,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            "RecordModeObservationAsync is gateway-only — the matcher's append-only log is local to the detection gateway.");

    public Task<IReadOnlyList<UnabsorbedModeObservation>> ListUnabsorbedModeObservationsAsync(
        int maxRows, CancellationToken ct = default)
        => throw new NotSupportedException(
            "ListUnabsorbedModeObservationsAsync is gateway-only — only the drainer reads the unabsorbed log.");

    public Task AbsorbModeObservationsAsync(
        FingerprintBrowserMode updated, IReadOnlyList<long> observationIds, CancellationToken ct = default)
        => throw new NotSupportedException(
            "AbsorbModeObservationsAsync is gateway-only — only the drainer commits batched EWMA folds.");

    public Task<IReadOnlyList<string>> ListFingerprintIdsWithModesAsync(
        int maxRows, CancellationToken ct = default)
        => throw new NotSupportedException(
            "ListFingerprintIdsWithModesAsync is gateway-only — only the rollup recompute sweeps fingerprints with modes.");
}