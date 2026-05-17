using Mostlylucid.BotDetection.Orchestration.Manifests;

namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>
///     Read-only proxy over <c>/api/v1/config/manifests</c>. The dashboard's Configuration
///     tab uses this to list + view manifests; the write surface (PUT override /
///     DELETE revert) is gateway-local and 503s on the remote viewer because the
///     concrete <c>ConfigEditorService</c> isn't registered alongside.
///
///     Reads happen on synchronous code paths in the middleware (BuildConfigurationModel),
///     so each call blocks on the gateway round-trip. Could be cached later if needed.
/// </summary>
internal sealed class RemoteConfigEditorService : IConfigEditorService
{
    private readonly GatewayApiClient _api;

    public RemoteConfigEditorService(GatewayApiClient api) => _api = api;

    public IReadOnlyList<DetectorManifestSummary> ListManifests()
    {
        var list = _api.GetEnvelopeAsync<List<DetectorManifestSummary>>("/api/v1/config/manifests")
            .GetAwaiter().GetResult();
        return list ?? new List<DetectorManifestSummary>();
    }

    public DetectorManifestDocument? GetManifest(string slug)
    {
        return _api.GetEnvelopeAsync<DetectorManifestDocument>(
            $"/api/v1/config/manifests/{Uri.EscapeDataString(slug)}")
            .GetAwaiter().GetResult();
    }
}
