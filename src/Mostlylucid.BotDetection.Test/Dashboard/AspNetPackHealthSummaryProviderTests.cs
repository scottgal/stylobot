using FluentAssertions;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Coverage for <see cref="AspNetPackHealthSummaryProvider"/> -- the C1
///     adapter that shapes <see cref="AspNetPackSummary"/> into an
///     <see cref="StatTileViewModel"/> for the dashboard overview row.
/// </summary>
public sealed class AspNetPackHealthSummaryProviderTests
{
    // ---------- 1. Builder absent -> provider returns null (surface not enabled). ----------

    [Fact]
    public async Task BuildTileAsync_returns_null_when_builder_is_null()
    {
        var provider = new AspNetPackHealthSummaryProvider(builder: null);

        var tile = await provider.BuildTileAsync(default);

        tile.Should().BeNull();
    }

    // ---------- 2. EndpointCount present -> tile carries thousands-separated value. ----------

    [Fact]
    public async Task BuildTileAsync_returns_tile_with_EndpointCount_when_builder_is_present()
    {
        var builder = new FakeAspNetPackHubBuilder(new AspNetPackSummary(
            EndpointCount: 132,
            MeterFamilyCount: 9,
            ObservationsLast15m: 0,
            InferredEndpointsLast15m: null,
            HealthBand: HealthBand.Good,
            ComputedAtUtc: new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero)));

        var provider = new AspNetPackHealthSummaryProvider(builder);

        var tile = await provider.BuildTileAsync(default);

        tile.Should().NotBeNull();
        tile!.Title.Should().Be("ASP.NET Pack");
        tile.Value.Should().Be("132");
        tile.Unit.Should().Be("endpoints");
        tile.HealthBand.Should().Be(HealthBand.Good);
        // Default-mount fallback: provider with no IDashboardLinkResolver
        // prefixes /stylobot. The new mount-aware path is covered by
        // BuildTileAsync_resolves_drill_href_against_dashboard_link_resolver.
        tile.DrillHref.Should().Be("/stylobot" + AspNetPackHealthSummaryProvider.DrillSubPath);
        tile.DrillLabel.Should().Be("View pack");
    }

    // ---------- 3. EndpointCount null -> graceful em-dash. ----------

    [Fact]
    public async Task BuildTileAsync_renders_em_dash_when_EndpointCount_is_null()
    {
        var builder = new FakeAspNetPackHubBuilder(new AspNetPackSummary(
            EndpointCount: null,
            MeterFamilyCount: 0,
            ObservationsLast15m: null,
            InferredEndpointsLast15m: null,
            HealthBand: HealthBand.Caution,
            ComputedAtUtc: new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero)));

        var provider = new AspNetPackHealthSummaryProvider(builder);

        var tile = await provider.BuildTileAsync(default);

        tile.Should().NotBeNull();
        tile!.Value.Should().Be("-");
    }

    // ---------- 4. Custom dashboard mount -> drill href follows the resolver. ----------

    [Fact]
    public async Task BuildTileAsync_resolves_drill_href_against_dashboard_link_resolver()
    {
        // ASP.NET Trailblazor demo mounts the dashboard at /_stylobot, not
        // the default /stylobot. The tile must follow the configured mount;
        // hard-coding the prefix here is what produced the production 404 on
        // /dashboard/aspnet-hub before the resolver shipped.
        var builder = new FakeAspNetPackHubBuilder(new AspNetPackSummary(
            EndpointCount: 42,
            MeterFamilyCount: 1,
            ObservationsLast15m: 0,
            InferredEndpointsLast15m: null,
            HealthBand: HealthBand.Good,
            ComputedAtUtc: new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero)));
        var links = new DashboardLinkResolver(new StyloBotDashboardOptions
        {
            BasePath = "/_stylobot"
        });
        var provider = new AspNetPackHealthSummaryProvider(builder, links: links);

        var tile = await provider.BuildTileAsync(default);

        tile.Should().NotBeNull();
        tile!.DrillHref.Should().Be("/_stylobot/aspnet-hub");
    }

    // ============================================================
    // FakeAspNetPackHubBuilder -- minimal IAspNetPackHubBuilder stub.
    // The provider only calls BuildSummaryAsync, so BuildAsync throws.
    // ============================================================

    private sealed class FakeAspNetPackHubBuilder : IAspNetPackHubBuilder
    {
        private readonly AspNetPackSummary _summary;

        public FakeAspNetPackHubBuilder(AspNetPackSummary summary)
        {
            _summary = summary;
        }

        public Task<AspNetPackHubViewModel> BuildAsync(CancellationToken ct)
            => throw new NotSupportedException("AspNetPackHealthSummaryProvider only calls BuildSummaryAsync.");

        public Task<AspNetPackSummary> BuildSummaryAsync(CancellationToken ct)
            => Task.FromResult(_summary);
    }
}
