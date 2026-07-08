using Microsoft.AspNetCore.SignalR;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Single change-detection surface for dashboard summary widgets. Per
///     <c>feedback_centralised_change_detection</c>: dashboard caches MUST
///     share ONE invalidation mechanism, not private per-builder TTLs /
///     warmups / private "is it stale yet" timers. Every read-surface bug
///     today (Top Bots, Visitors, YourDetection, signature detail) was the
///     same disconnect -- per-read-site patching is whack-a-mole; the fix
///     is upstream.
///
///     <para>
///         Producers call <see cref="BroadcastStale"/> when an upstream
///         cache / store / atom updates. Consumers subscribe to the named
///         surface key via SignalR (<see cref="IStyloBotDashboardHub.BroadcastInvalidation"/>)
///         and re-fetch the relevant partial via HTMX OOB swap. The
///         beacon name in <see cref="Surfaces"/> matches the
///         <c>data-sb-depends</c> value the consumer renders -- one
///         catalog of keys, one broadcaster, one set of subscribers.
///     </para>
///
///     <para>
///         Wraps <see cref="SignalRBroadcastConstrainer"/> for the actual
///         hub emission so this beacon does not introduce a parallel
///         broadcast path; the existing 10-second outbound rate cap still
///         coalesces bursts of stale events from the same surface into
///         one client wake-up.
///     </para>
/// </summary>
public sealed class DashboardFreshnessBeacon
{
    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> _hub;
    private readonly StyloBotDashboardOptions _options;

    public DashboardFreshnessBeacon(
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hub,
        StyloBotDashboardOptions options)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(options);
        _hub = hub;
        _options = options;
    }

    /// <summary>
    ///     Publish that <paramref name="surfaceKey"/> is stale; consumers
    ///     subscribed to that key over SignalR will re-fetch the relevant
    ///     partial. Routed through <see cref="SignalRBroadcastConstrainer"/>
    ///     so a burst of stale events from the same surface (e.g. a rule
    ///     re-save spike from the policy mutation API) coalesces to one
    ///     outbound beacon per <see cref="StyloBotDashboardOptions.BroadcastMinIntervalMs"/>
    ///     window.
    /// </summary>
    /// <param name="surfaceKey">
    ///     The canonical surface key. Use one of the constants on
    ///     <see cref="Surfaces"/> -- the catalog of keys MUST stay in one
    ///     place per <c>feedback_centralised_change_detection</c>, so add
    ///     new surfaces there, not as ad-hoc string literals at the call
    ///     site.
    /// </param>
    public void BroadcastStale(string surfaceKey)
    {
        if (string.IsNullOrWhiteSpace(surfaceKey)) return;
        SignalRBroadcastConstrainer.Queue(_hub, surfaceKey, _options.BroadcastMinIntervalMs);
    }

    /// <summary>
    ///     Canonical catalog of dashboard freshness surfaces. One constant
    ///     per surface so consumers in views (<c>data-sb-depends</c>) and
    ///     producers in services share the same string identity. Adding a
    ///     surface? Add it here first; bake the constant into the producer
    ///     + the view; do NOT pass an ad-hoc literal through
    ///     <see cref="BroadcastStale"/>.
    /// </summary>
    public static class Surfaces
    {
        // -------------------- Pack-health summaries (issue #122) ---------

        /// <summary>
        ///     Global policy stack summary tile (rule counts + decision
        ///     count). Producer: <see cref="PolicyStackSummaryCache"/> +
        ///     the <see cref="DashboardFreshnessBridge"/> that subscribes
        ///     to <c>IPolicyRuleStore.Changed</c>.
        /// </summary>
        public const string PolicyStackSummary = "policy-stack-summary";

        /// <summary>
        ///     ASP.NET pack hub summary (endpoint + meter family counts).
        ///     Producer: commercial AspNetPack bridge that ticks on the
        ///     same Tick10s the underlying meter snapshot rebuilds on.
        /// </summary>
        public const string AspNetPackHub = "aspnet-pack-hub";

        /// <summary>
        ///     Meter-stream catalog health (count of currently published
        ///     instruments). Producer: <see cref="DashboardFreshnessBridge"/>
        ///     subscribing to the schedule coordinator's Tick10s tick;
        ///     broadcasts only when the catalog size changes.
        /// </summary>
        public const string MeterStreamHealth = "meter-stream-health";

        // -------------------- Existing dashboard surfaces -----------------
        // Catalogued so the broadcaster + the dependent views share one
        // source of truth even for surfaces that pre-date the beacon.
        // Producers that already call SignalRBroadcastConstrainer.Queue
        // with these literals continue to interoperate -- the beacon just
        // makes the catalog discoverable.

        /// <summary>Top bots / visitors summary tile.</summary>
        public const string Summary = "summary";

        /// <summary>Country-rollup widget.</summary>
        public const string Countries = "countries";

        /// <summary>Endpoint hits widget.</summary>
        public const string Endpoints = "endpoints";

        /// <summary>Per-signature drill surface (top-bots tab + detail row).</summary>
        public const string Signature = "signature";

        /// <summary>User-agent rollup widget.</summary>
        public const string UserAgents = "useragents";

        /// <summary>Metrics widget (sparklines + meters).</summary>
        public const string Metrics = "metrics";

        /// <summary>Threats list widget.</summary>
        public const string Threats = "threats";

        /// <summary>Clusters widget (Leiden grouping).</summary>
        public const string Clusters = "clusters";

        /// <summary>Sessions list widget.</summary>
        public const string Sessions = "sessions";

        /// <summary>Identities list widget.</summary>
        public const string Identities = "identities";

        /// <summary>Visitors list widget.</summary>
        public const string Visitors = "visitors";

        /// <summary>Routes tab on the dashboard.</summary>
        public const string Routes = "routes";

        /// <summary>Threat-intel tab on the dashboard.</summary>
        public const string ThreatIntel = "threat-intel";

        /// <summary>
        ///     Traffic page's site-health widget (U3). Producer:
        ///     <see cref="DashboardFreshnessBridge"/>'s Tick10s arm, which
        ///     fires whenever the gateway's <c>DegradationStoreSampler</c>
        ///     could have persisted a new <c>DegradationSnapshot</c>. Consumers:
        ///     the <c>SbSiteHealthViewComponent</c>-rendered card carrying
        ///     <c>data-sb-depends="site-health"</c>.
        /// </summary>
        public const string SiteHealth = "site-health";

        /// <summary>
        ///     Web Bot Auth public-key registry widget. Producer:
        ///     <see cref="DashboardFreshnessBridge"/>'s arm subscribing to the
        ///     public-key registry's <c>PublicKeyRegistryRefreshedSignal</c>.
        ///     Consumers: the <c>SbPublicKeyRegistryViewComponent</c>-rendered
        ///     card carrying <c>data-sb-depends="public-keys"</c>.
        /// </summary>
        public const string PublicKeys = "public-keys";
    }
}
