using Mostlylucid.BotDetection.Orchestration.Manifests;

namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>
///     Read-only proxy over <c>/api/v1/config/manifests</c>. Pure async;
///     <see cref="IConfigEditorService"/> is async so the dashboard middleware awaits
///     the HTTP round-trip instead of blocking thread-pool threads.
///
///     The write surface (PUT override / DELETE revert) is gateway-local and 503s on
///     the remote viewer because the concrete <c>ConfigEditorService</c> isn't
///     registered alongside.
/// </summary>
internal sealed class RemoteConfigEditorService : IConfigEditorService
{
    private readonly GatewayApiClient _api;

    public RemoteConfigEditorService(GatewayApiClient api) => _api = api;

    public async Task<IReadOnlyList<DetectorManifestSummary>> ListManifestsAsync(CancellationToken ct = default)
        => await _api.GetEnvelopeListAsync<DetectorManifestSummary>("/api/v1/config/manifests", ct);

    public async Task<DetectorManifestDocument?> GetManifestAsync(string slug, CancellationToken ct = default)
        => await _api.GetEnvelopeAsync<DetectorManifestDocument>(
            $"/api/v1/config/manifests/{Uri.EscapeDataString(slug)}", ct);
}
