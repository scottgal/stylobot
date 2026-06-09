using FluentAssertions;
using Mostlylucid.BotDetection.AspNetPack.Inventory;
using Mostlylucid.BotDetection.AspNetPack.Services;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Coverage for the <see cref="AspNetPackHubPageBuilder"/> that powers the
///     <c>/dashboard/aspnet-hub</c> page and the
///     <c>GET /api/v1/packs/aspnet/summary</c> JSON endpoint (B2 of the Pack
///     Metrics + Cohesive Views plan). Pure unit tests against the builder
///     rather than the <c>WebApplicationFactory&lt;Program&gt;</c> dance:
///     the builder is pure logic over <see cref="IMeterStream"/> + an
///     optional <see cref="IAspNetEndpointInventory"/>, and a manual fake of
///     each covers every relevant path.
/// </summary>
public sealed class AspNetPackHubPageBuilderTests
{
    // ---------- 1. Builder returns view model with non-empty tiles when stream + inventory populated. ----------

    [Fact]
    public async Task Builder_returns_populated_view_model_when_stream_and_inventory_have_data()
    {
        var stream = new FakeMeterStream();
        stream.AddCounter("stylobot_aspnet_observations_received_total",
            namespacePrefix: "aspnet", current: 4_711, values: new double[] { 1, 2, 3 });
        stream.AddCounter("stylobot_aspnet_inferred_endpoints_total",
            namespacePrefix: "aspnet", current: 38, values: new double[] { 1, 1, 1 });

        var inventory = new FakeAspNetEndpointInventory(100);

        var builder = new AspNetPackHubPageBuilder(stream, inventory);
        var vm = await builder.BuildAsync(default);

        vm.Title.Should().Be("ASP.NET Pack");
        vm.OverallHealthBand.Should().Be(HealthBand.Good);

        // 4 tiles when both observations + inferred meters are present.
        vm.TopTiles.Should().HaveCount(4);
        vm.TopTiles[0].Title.Should().Be("Endpoints");
        vm.TopTiles[0].Value.Should().Be("100");
        vm.TopTiles[0].HealthBand.Should().Be(HealthBand.Good);

        vm.TopTiles[1].Title.Should().Be("Meters");
        vm.TopTiles[1].Value.Should().Be("2");

        vm.TopTiles[2].Title.Should().Be("Observations / 15m");
        // 1 + 2 + 3 = 6
        vm.TopTiles[2].Value.Should().Be("6");

        vm.TopTiles[3].Title.Should().Be("Inferred endpoints / 15m");
        vm.TopTiles[3].Value.Should().Be("3");

        vm.IngestTrend.Should().NotBeNull();
        // 4711 is under 10k so FormatNumber returns it integer-shaped, no K/M suffix.
        vm.IngestTrend!.Value.Should().Be("4711");
        vm.IngestTrend.AxisLabels.Should().Equal("-60m", "-30m", "now");

        vm.MeterTable.Rows.Should().HaveCount(2);
        vm.MeterTable.Rows.Select(r => r.Name).Should().Contain(
            "stylobot_aspnet_observations_received_total");
        vm.MeterTable.Rows.Select(r => r.Name).Should().Contain(
            "stylobot_aspnet_inferred_endpoints_total");
    }

    // ---------- 2. Builder degrades gracefully when inventory is null. ----------

    [Fact]
    public async Task Builder_endpoint_tile_shows_em_dash_when_inventory_is_null()
    {
        var stream = new FakeMeterStream();
        stream.AddCounter("stylobot_aspnet_observations_received_total",
            namespacePrefix: "aspnet", current: 5, values: new double[] { 1, 1, 1 });

        var builder = new AspNetPackHubPageBuilder(stream, inventory: null);
        var vm = await builder.BuildAsync(default);

        var endpointsTile = vm.TopTiles[0];
        endpointsTile.Title.Should().Be("Endpoints");
        endpointsTile.Value.Should().Be("—");
        endpointsTile.HealthBand.Should().Be(HealthBand.Caution);
    }

    // ---------- 3. Builder degrades gracefully when meter stream is empty. ----------

    [Fact]
    public async Task Builder_renders_placeholder_trend_when_meter_stream_is_empty()
    {
        var stream = new FakeMeterStream(); // no meters
        var inventory = new FakeAspNetEndpointInventory(7);

        var builder = new AspNetPackHubPageBuilder(stream, inventory);
        var vm = await builder.BuildAsync(default);

        vm.MeterTable.Rows.Should().BeEmpty();
        vm.MeterTable.EmptyMessage.Should().Be("No ASP.NET pack meters observed yet.");

        vm.IngestTrend.Should().NotBeNull();
        vm.IngestTrend!.Value.Should().Be("—");
        vm.IngestTrend.HealthBand.Should().Be(HealthBand.Caution);
        vm.IngestTrend.Subtitle.Should().Be("Pack telemetry not yet observed");
    }

    // ---------- 4. OverallHealthBand = Good when both inventory AND meters are populated. ----------

    [Fact]
    public async Task OverallHealthBand_is_Good_when_inventory_and_meters_both_populated()
    {
        var stream = new FakeMeterStream();
        stream.AddCounter("stylobot_aspnet_observations_received_total",
            namespacePrefix: "aspnet", current: 1, values: new double[] { 1 });

        var inventory = new FakeAspNetEndpointInventory(3);

        var builder = new AspNetPackHubPageBuilder(stream, inventory);
        var vm = await builder.BuildAsync(default);

        vm.OverallHealthBand.Should().Be(HealthBand.Good);
    }

    // ---------- 5. OverallHealthBand = Caution when one of them is empty. ----------

    [Fact]
    public async Task OverallHealthBand_is_Caution_when_only_inventory_populated()
    {
        var stream = new FakeMeterStream(); // no meters
        var inventory = new FakeAspNetEndpointInventory(7);

        var builder = new AspNetPackHubPageBuilder(stream, inventory);
        var vm = await builder.BuildAsync(default);

        vm.OverallHealthBand.Should().Be(HealthBand.Caution);
    }

    [Fact]
    public async Task OverallHealthBand_is_Caution_when_only_meters_populated()
    {
        var stream = new FakeMeterStream();
        stream.AddCounter("stylobot_aspnet_observations_received_total",
            namespacePrefix: "aspnet", current: 1, values: new double[] { 1 });

        // Inventory present but with zero endpoints -- treated as "empty" for
        // health purposes.
        var inventory = new FakeAspNetEndpointInventory(0);

        var builder = new AspNetPackHubPageBuilder(stream, inventory);
        var vm = await builder.BuildAsync(default);

        vm.OverallHealthBand.Should().Be(HealthBand.Caution);
    }

    // ---------- 6. OverallHealthBand = Warning when both are empty. ----------

    [Fact]
    public async Task OverallHealthBand_is_Warning_when_both_inventory_and_meters_empty()
    {
        var stream = new FakeMeterStream();
        var inventory = new FakeAspNetEndpointInventory(0);

        var builder = new AspNetPackHubPageBuilder(stream, inventory);
        var vm = await builder.BuildAsync(default);

        vm.OverallHealthBand.Should().Be(HealthBand.Warning);
    }

    // ---------- 7. BuildSummaryAsync returns matching numbers. ----------

    [Fact]
    public async Task BuildSummaryAsync_returns_same_numbers_as_view_model_tiles()
    {
        var stream = new FakeMeterStream();
        stream.AddCounter("stylobot_aspnet_observations_received_total",
            namespacePrefix: "aspnet", current: 4_711, values: new double[] { 1, 2, 3 });
        stream.AddCounter("stylobot_aspnet_inferred_endpoints_total",
            namespacePrefix: "aspnet", current: 38, values: new double[] { 1, 1, 1 });

        var inventory = new FakeAspNetEndpointInventory(100);

        var builder = new AspNetPackHubPageBuilder(stream, inventory);
        var summary = await builder.BuildSummaryAsync(default);

        summary.EndpointCount.Should().Be(100);
        summary.MeterFamilyCount.Should().Be(2);
        summary.ObservationsLast15m.Should().Be(6d);             // 1 + 2 + 3
        summary.InferredEndpointsLast15m.Should().Be(3d);        // 1 + 1 + 1
        summary.HealthBand.Should().Be(HealthBand.Good);
        summary.ComputedAtUtc.Should().BeAfter(DateTimeOffset.MinValue);
    }

    // ---------- 8. Filter by aspnet namespace correctly excludes non-aspnet meters. ----------

    [Fact]
    public async Task Aspnet_namespace_filter_excludes_unrelated_meters()
    {
        var stream = new FakeMeterStream();
        // AspNet-namespaced meter -- belongs in the table.
        stream.AddCounter("aspnet.requests_total",
            namespacePrefix: "aspnet", current: 1, values: new double[] { 1 });
        // Policy-namespaced meter -- must be excluded even though the gateway
        // is publishing it (it shows up on the global Insights page, not here).
        stream.AddCounter("policy.eval_ms",
            namespacePrefix: "policy", current: 1, values: new double[] { 1 });

        var builder = new AspNetPackHubPageBuilder(stream, new FakeAspNetEndpointInventory(1));
        var vm = await builder.BuildAsync(default);

        vm.MeterTable.Rows.Should().ContainSingle();
        vm.MeterTable.Rows[0].Name.Should().Be("aspnet.requests_total");
    }

    // ============================================================
    // FakeMeterStream -- manual stub. Mirrors the InsightsPageBuilderTests
    // helper so the two test files stay diff-friendly when patterns
    // converge.
    // ============================================================
    private sealed class FakeMeterStream : IMeterStream
    {
        public List<MeterCatalogEntry> Catalog { get; } = new();
        public Dictionary<string, MeterTimeSeries> Series { get; } = new();

        public void AddCounter(
            string name,
            string namespacePrefix = "aspnet",
            double current = 0,
            double[]? values = null)
        {
            Catalog.Add(new MeterCatalogEntry(name, namespacePrefix, MeterKind.Counter, null, null));
            Series[name] = new MeterTimeSeries(
                name, MeterKind.Counter,
                Buckets: Array.Empty<DateTimeOffset>(),
                Values: values ?? Array.Empty<double>(),
                Current: current,
                P50: null,
                P99: null);
        }

        public Task<IReadOnlyList<MeterCatalogEntry>> ListAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MeterCatalogEntry>>(Catalog);

        public Task<MeterTimeSeries?> GetAsync(string meterName, TimeSpan window, int buckets, CancellationToken ct)
            => Task.FromResult(Series.TryGetValue(meterName, out var ts) ? ts : null);
    }

    /// <summary>
    ///     Fake inventory that returns a fixed count of synthetic endpoints.
    ///     The endpoint descriptors themselves are placeholders -- the builder
    ///     only ever calls <c>All().Count</c>, so the descriptor shape is
    ///     irrelevant beyond compilation.
    /// </summary>
    private sealed class FakeAspNetEndpointInventory : IAspNetEndpointInventory
    {
        private readonly IReadOnlyList<AspNetEndpointDescriptor> _endpoints;

        public FakeAspNetEndpointInventory(int count)
        {
            var list = new List<AspNetEndpointDescriptor>(count);
            for (var i = 0; i < count; i++)
            {
                list.Add(new AspNetEndpointDescriptor(
                    Method: "GET",
                    RoutePattern: $"/test/{i}",
                    DisplayName: $"Test {i}",
                    Controller: "",
                    Action: "",
                    Authorize: Array.Empty<string>()));
            }
            _endpoints = list;
        }

        public IReadOnlyList<AspNetEndpointDescriptor> All() => _endpoints;
    }
}
