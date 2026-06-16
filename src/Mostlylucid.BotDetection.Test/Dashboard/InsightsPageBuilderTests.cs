using FluentAssertions;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Coverage for the <see cref="InsightsPageBuilder"/> that powers the
///     <c>/dashboard/insights</c> page (B1 of the Pack Metrics + Cohesive Views
///     plan). Direct unit tests against the builder rather than the
///     <c>WebApplicationFactory&lt;Program&gt;</c> dance: the builder is pure
///     logic over <see cref="IMeterStream"/>, so a manual fake stream covers
///     every filter / sort / facet / window code path without needing the
///     dashboard middleware spun up.
/// </summary>
public sealed class InsightsPageBuilderTests
{
    // ---------- 1. Default render: sorted by name ----------

    [Fact]
    public async Task Default_request_renders_all_meters_sorted_by_name()
    {
        var stream = new FakeMeterStream();
        stream.AddCounter("c.three");
        stream.AddCounter("a.one");
        stream.AddCounter("b.two");

        var vm = await new InsightsPageBuilder().BuildAsync(
            stream, kind: null, @namespace: null, query: null,
            sort: null, window: null, buckets: null, ct: default);

        vm.Table.Rows.Select(r => r.Name).Should().Equal("a.one", "b.two", "c.three");
        vm.FilteredMeterCount.Should().Be(3);
        vm.TotalMeterCount.Should().Be(3);
    }

    // ---------- 2. q filter ----------

    [Fact]
    public async Task Query_filter_narrows_results()
    {
        var stream = new FakeMeterStream();
        stream.AddCounter("c.three");
        stream.AddCounter("a.one");
        stream.AddCounter("b.two");

        var vm = await new InsightsPageBuilder().BuildAsync(
            stream, kind: null, @namespace: null, query: "one",
            sort: null, window: null, buckets: null, ct: default);

        vm.Table.Rows.Should().ContainSingle();
        vm.Table.Rows[0].Name.Should().Be("a.one");
    }

    // ---------- 3. kind filter ----------

    [Fact]
    public async Task Kind_filter_narrows_results()
    {
        var stream = new FakeMeterStream();
        stream.Add("a", MeterKind.Counter);
        stream.Add("b", MeterKind.Gauge);
        stream.Add("c", MeterKind.Histogram);

        var vm = await new InsightsPageBuilder().BuildAsync(
            stream, kind: "gauge", @namespace: null, query: null,
            sort: null, window: null, buckets: null, ct: default);

        vm.Table.Rows.Should().ContainSingle();
        vm.Table.Rows[0].Name.Should().Be("b");
    }

    // ---------- 4. namespace filter ----------

    [Fact]
    public async Task Namespace_filter_narrows_results()
    {
        var stream = new FakeMeterStream();
        stream.AddWithNamespace("aspnet_requests_total",     "aspnet");
        stream.AddWithNamespace("aspnet_responses_total",    "aspnet");
        stream.AddWithNamespace("gateway_observations_total","gateway");

        var vm = await new InsightsPageBuilder().BuildAsync(
            stream, kind: null, @namespace: "aspnet", query: null,
            sort: null, window: null, buckets: null, ct: default);

        vm.Table.Rows.Select(r => r.Name).Should().BeEquivalentTo(
            new[] { "aspnet_requests_total", "aspnet_responses_total" });
    }

    // ---------- 5. sort=current_desc ----------

    [Fact]
    public async Task Sort_current_desc_orders_by_current_value()
    {
        var stream = new FakeMeterStream();
        stream.AddCounter("alpha", current: 10);
        stream.AddCounter("beta",  current: 100);
        stream.AddCounter("gamma", current: 50);

        var vm = await new InsightsPageBuilder().BuildAsync(
            stream, kind: null, @namespace: null, query: null,
            sort: "current_desc", window: null, buckets: null, ct: default);

        vm.Table.Rows.Select(r => r.Name).Should().Equal("beta", "gamma", "alpha");
    }

    // ---------- 6. window parsing ----------

    [Theory]
    [InlineData("5m",       5)]
    [InlineData("15m",     15)]
    [InlineData("1h",      60)]
    [InlineData(null,      15)]
    [InlineData("garbage", 15)]
    public async Task Window_parsing_resolves_to_correct_timespan(string? input, int expectedMinutes)
    {
        var stream = new FakeMeterStream();
        stream.AddCounter("a");

        await new InsightsPageBuilder().BuildAsync(
            stream, kind: null, @namespace: null, query: null,
            sort: null, window: input, buckets: null, ct: default);

        stream.LastWindow.Should().NotBeNull();
        stream.LastWindow!.Value.TotalMinutes.Should().Be(expectedMinutes);
    }

    // ---------- 7. empty catalog ----------

    [Fact]
    public async Task Empty_catalog_renders_no_meters_published_message()
    {
        var stream = new FakeMeterStream();

        var vm = await new InsightsPageBuilder().BuildAsync(
            stream, kind: null, @namespace: null, query: null,
            sort: null, window: null, buckets: null, ct: default);

        vm.TotalMeterCount.Should().Be(0);
        vm.Table.Rows.Should().BeEmpty();
        vm.Table.EmptyMessage.Should().Contain("No meters published yet");
    }

    // ---------- 8. filter excludes everything ----------

    [Fact]
    public async Task Filter_excluding_everything_renders_no_match_message()
    {
        var stream = new FakeMeterStream();
        stream.AddCounter("a");
        stream.AddCounter("b");

        var vm = await new InsightsPageBuilder().BuildAsync(
            stream, kind: null, @namespace: null, query: "no_such_meter",
            sort: null, window: null, buckets: null, ct: default);

        vm.TotalMeterCount.Should().Be(2);
        vm.FilteredMeterCount.Should().Be(0);
        vm.Table.EmptyMessage.Should().Be("No meters match the current filter.");
    }

    // ---------- 9. caption "N of M" ----------

    [Fact]
    public async Task Filtered_caption_reports_filtered_over_total()
    {
        var stream = new FakeMeterStream();
        for (var i = 0; i < 7; i++)
            stream.AddCounter($"alpha_{i}");
        stream.AddCounter("beta_zero");
        stream.AddCounter("beta_one");
        stream.AddCounter("beta_two");

        var vm = await new InsightsPageBuilder().BuildAsync(
            stream, kind: null, @namespace: null, query: "beta",
            sort: null, window: null, buckets: null, ct: default);

        vm.TotalMeterCount.Should().Be(10);
        vm.FilteredMeterCount.Should().Be(3);
        vm.Table.Caption.Should().Be("3 of 10 meters shown");
    }

    // ---------- 10. kind facet counts ----------

    [Fact]
    public async Task Kind_facets_count_meters_per_kind()
    {
        var stream = new FakeMeterStream();
        stream.Add("c1", MeterKind.Counter);
        stream.Add("c2", MeterKind.Counter);
        stream.Add("c3", MeterKind.Counter);
        stream.Add("c4", MeterKind.Counter);
        stream.Add("g1", MeterKind.Gauge);
        stream.Add("g2", MeterKind.Gauge);

        var vm = await new InsightsPageBuilder().BuildAsync(
            stream, kind: null, @namespace: null, query: null,
            sort: null, window: null, buckets: null, ct: default);

        var counter = vm.KindFacets.Single(f => f.Value == "Counter");
        counter.Count.Should().Be(4);
        var gauge = vm.KindFacets.Single(f => f.Value == "Gauge");
        gauge.Count.Should().Be(2);
        // Selected reflects current filter (null here)
        vm.KindFacets.Should().OnlyContain(f => !f.Selected);
    }

    // ---------- Bonus: kind selection reflected ----------

    [Fact]
    public async Task Kind_facet_marked_selected_when_filter_matches()
    {
        var stream = new FakeMeterStream();
        stream.Add("c", MeterKind.Counter);
        stream.Add("g", MeterKind.Gauge);

        var vm = await new InsightsPageBuilder().BuildAsync(
            stream, kind: "Counter", @namespace: null, query: null,
            sort: null, window: null, buckets: null, ct: default);

        vm.KindFacets.Single(f => f.Value == "Counter").Selected.Should().BeTrue();
        vm.KindFacets.Single(f => f.Value == "Gauge").Selected.Should().BeFalse();
    }

    // ---------- Bonus: drill href round-trips meter name ----------

    [Fact]
    public async Task Row_drill_href_round_trips_meter_name()
    {
        // Default-mount fallback path: no resolver, prefix /stylobot.
        var stream = new FakeMeterStream();
        stream.AddCounter("stylobot.aspnet.observations.received");

        var vm = await new InsightsPageBuilder().BuildAsync(
            stream, kind: null, @namespace: null, query: null,
            sort: null, window: null, buckets: null, ct: default);

        vm.Table.Rows.Should().ContainSingle();
        vm.Table.Rows[0].DrillHref.Should().Be(
            "/stylobot/insights/stylobot.aspnet.observations.received");
    }

    // ---------- Custom mount -> drill follows configured BasePath ----------

    [Fact]
    public async Task Row_drill_href_resolves_against_custom_dashboard_mount()
    {
        // The Trailblazor demo mounts the dashboard at /_stylobot. The
        // resolver-aware builder threads the configured BasePath onto
        // every per-meter drill href so the row link actually resolves
        // instead of 404'ing into the default /dashboard path.
        var stream = new FakeMeterStream();
        stream.AddCounter("stylobot.aspnet.observations.received");
        var links = new Mostlylucid.BotDetection.UI.Services.DashboardLinkResolver(
            new Mostlylucid.BotDetection.UI.Configuration.StyloBotDashboardOptions
            {
                BasePath = "/_stylobot"
            });

        var vm = await new InsightsPageBuilder(links).BuildAsync(
            stream, kind: null, @namespace: null, query: null,
            sort: null, window: null, buckets: null, ct: default);

        vm.Table.Rows.Should().ContainSingle();
        vm.Table.Rows[0].DrillHref.Should().Be(
            "/_stylobot/insights/stylobot.aspnet.observations.received");
    }

    // ============================================================
    // FakeMeterStream -- manual stub. Records the window passed to
    // GetAsync so the parsing test can assert on it.
    // ============================================================
    private sealed class FakeMeterStream : IMeterStream
    {
        public List<MeterCatalogEntry> Catalog { get; } = new();
        public Dictionary<string, MeterTimeSeries> Series { get; } = new();
        public TimeSpan? LastWindow { get; private set; }

        public void Add(string name, MeterKind kind, string? unit = null, string? description = null)
        {
            var ns = name.Split('_', '.').FirstOrDefault() ?? string.Empty;
            Catalog.Add(new MeterCatalogEntry(name, ns, kind, unit, description));
        }

        public void AddCounter(string name, double current = 0)
        {
            Add(name, MeterKind.Counter);
            if (current != 0)
                Series[name] = new MeterTimeSeries(
                    name, MeterKind.Counter,
                    Buckets: Array.Empty<DateTimeOffset>(),
                    Values: new double[] { current },
                    Current: current,
                    P50: null,
                    P99: null);
        }

        public void AddWithNamespace(string name, string ns)
            => Catalog.Add(new MeterCatalogEntry(name, ns, MeterKind.Counter, null, null));

        public Task<IReadOnlyList<MeterCatalogEntry>> ListAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MeterCatalogEntry>>(Catalog);

        public Task<MeterTimeSeries?> GetAsync(string meterName, TimeSpan window, int buckets, CancellationToken ct)
        {
            LastWindow = window;
            return Task.FromResult(Series.TryGetValue(meterName, out var ts) ? ts : null);
        }
    }
}
