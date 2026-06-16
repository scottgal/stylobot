using FluentAssertions;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Coverage for <see cref="MeterStreamHealthSummaryProvider"/> -- the C1
///     adapter that surfaces "N meters currently published" alongside the
///     policy + AspNet tiles. Always returns a tile (IMeterStream is on every
///     host); health band derives from the catalog size.
/// </summary>
public sealed class MeterStreamHealthSummaryProviderTests
{
    // ---------- 1. Populated catalog -> tile shows count + Good band. ----------

    [Fact]
    public async Task BuildTileAsync_returns_tile_with_catalog_count_and_Good_band()
    {
        var stream = new FakeMeterStream();
        for (var i = 0; i < 9; i++)
        {
            stream.Add(new MeterCatalogEntry(
                Name: $"meter_{i}",
                Namespace: "test",
                Kind: MeterKind.Counter,
                Unit: null,
                Description: null));
        }

        var provider = new MeterStreamHealthSummaryProvider(stream);

        var tile = await provider.BuildTileAsync(default);

        tile.Should().NotBeNull();
        tile!.Title.Should().Be("Metrics");
        tile.Value.Should().Be("9");
        tile.Unit.Should().Be("meters");
        tile.HealthBand.Should().Be(HealthBand.Good);
        // Default-mount fallback: provider with no IDashboardLinkResolver
        // prefixes /stylobot. The new mount-aware path is covered by
        // BuildTileAsync_resolves_drill_href_against_dashboard_link_resolver.
        tile.DrillHref.Should().Be("/stylobot" + MeterStreamHealthSummaryProvider.DrillSubPath);
        tile.DrillLabel.Should().Be("Open insights");
    }

    // ---------- 2. Empty catalog -> Caution band. ----------

    [Fact]
    public async Task BuildTileAsync_returns_Caution_band_when_catalog_is_empty()
    {
        var stream = new FakeMeterStream();
        var provider = new MeterStreamHealthSummaryProvider(stream);

        var tile = await provider.BuildTileAsync(default);

        tile.Should().NotBeNull();
        tile!.Value.Should().Be("0");
        tile.HealthBand.Should().Be(HealthBand.Caution);
    }

    // ---------- 3. Any entry -> Good band (not just the 9-entry case). ----------

    [Fact]
    public async Task BuildTileAsync_returns_Good_band_when_catalog_has_at_least_one_entry()
    {
        var stream = new FakeMeterStream();
        stream.Add(new MeterCatalogEntry(
            Name: "single",
            Namespace: "test",
            Kind: MeterKind.Gauge,
            Unit: null,
            Description: null));

        var provider = new MeterStreamHealthSummaryProvider(stream);

        var tile = await provider.BuildTileAsync(default);

        tile.Should().NotBeNull();
        tile!.HealthBand.Should().Be(HealthBand.Good);
    }

    // ---------- 4. Custom dashboard mount -> drill href follows the resolver. ----------

    [Fact]
    public async Task BuildTileAsync_resolves_drill_href_against_dashboard_link_resolver()
    {
        // Same per-mount story as the other tile providers: a /_stylobot
        // mount must produce /_stylobot/insights, not /dashboard/insights.
        var stream = new FakeMeterStream();
        stream.Add(new MeterCatalogEntry(
            Name: "meter_0",
            Namespace: "test",
            Kind: MeterKind.Counter,
            Unit: null,
            Description: null));
        var links = new DashboardLinkResolver(new StyloBotDashboardOptions
        {
            BasePath = "/_stylobot"
        });
        var provider = new MeterStreamHealthSummaryProvider(stream, links: links);

        var tile = await provider.BuildTileAsync(default);

        tile.Should().NotBeNull();
        tile!.DrillHref.Should().Be("/_stylobot/insights");
    }

    // ============================================================
    // FakeMeterStream -- list-only stub. The provider doesn't call
    // GetAsync, so the time-series surface throws to catch a future
    // regression that wires the wrong path.
    // ============================================================

    private sealed class FakeMeterStream : IMeterStream
    {
        private readonly List<MeterCatalogEntry> _entries = new();

        public void Add(MeterCatalogEntry entry) => _entries.Add(entry);

        public Task<IReadOnlyList<MeterCatalogEntry>> ListAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MeterCatalogEntry>>(_entries);

        public Task<MeterTimeSeries?> GetAsync(string meterName, TimeSpan window, int buckets, CancellationToken ct)
            => throw new NotSupportedException("MeterStreamHealthSummaryProvider should only call ListAsync.");
    }
}
