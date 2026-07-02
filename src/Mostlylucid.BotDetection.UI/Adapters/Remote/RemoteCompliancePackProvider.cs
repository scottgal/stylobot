using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Compliance;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>
///     Remote-mode <see cref="ICompliancePackProvider"/>. The available packs are
///     static embedded reference data (identical on every host — loaded locally, not
///     state); only the ACTIVE pack selection is state, and that lives on the gateway.
///     This resolves the active pack from <c>GET /api/v1/compliance/status</c> and the
///     embedded set, so an upstream dashboard host shows compliance without owning any
///     compliance store or connection (feedback_upstream_owns_no_stylobot_state).
///     <para>
///     The active id is served from a short-lived cache refreshed in the background
///     (the interface getter is synchronous): a coalescing window over the gateway
///     read, not owned state. The gateway stays the authority.
///     </para>
/// </summary>
internal sealed class RemoteCompliancePackProvider : ICompliancePackProvider
{
    private const long RefreshTtlMs = 30_000;

    private readonly GatewayApiClient _api;
    private readonly ILogger<RemoteCompliancePackProvider> _logger;
    private readonly IReadOnlyList<CompliancePack> _packs;
    private volatile CompliancePack _active;
    private long _lastFetchTicks = long.MinValue;
    private int _refreshing;

    public RemoteCompliancePackProvider(GatewayApiClient api, ILogger<RemoteCompliancePackProvider> logger)
    {
        _api = api;
        _logger = logger;
        _packs = CompliancePackLoader.LoadEmbeddedPacks(logger);
        _active = _packs.FirstOrDefault(p => p.Id == "balanced-default")
                  ?? _packs.FirstOrDefault()
                  ?? new CompliancePack { Id = "default", Name = "Default" };
        _ = RefreshAsync();
    }

    public CompliancePack ActivePack
    {
        get
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _lastFetchTicks) > RefreshTtlMs)
                _ = RefreshAsync();
            return _active;
        }
    }

    public IReadOnlyList<CompliancePack> AvailablePacks => _packs;

    public void SetActivePack(string packId)
    {
        // The upstream site is a VIEWER. Changing the active compliance pack is an
        // operator write and belongs on the gateway (the enforcement component owns
        // the selection); it is not applied here. Config / commercial hot-reload on
        // the gateway is the write path.
        _logger.LogWarning(
            "SetActivePack({PackId}) ignored on the remote dashboard host — the active pack is owned by the gateway",
            packId);
    }

    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            var status = await _api.GetEnvelopeAsync<ComplianceStatusDto>("/api/v1/compliance/status");
            Interlocked.Exchange(ref _lastFetchTicks, Environment.TickCount64);
            if (!string.IsNullOrEmpty(status?.ActivePackId))
            {
                var pack = _packs.FirstOrDefault(p => p.Id == status!.ActivePackId);
                if (pack is not null) _active = pack;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Remote compliance status fetch failed; keeping cached active pack");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }
}