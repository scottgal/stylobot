using FluentAssertions;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Coverage for <see cref="DashboardOverviewPackHealthRowBuilder"/> -- the
///     C1 row composer. The builder is pure logic over an enumeration of
///     <see cref="IPackHealthSummaryProvider"/>, so manual stubs cover every
///     relevant path: ordering, null-skip, throw-skip, OperationCanceled
///     propagation, and the empty-list case.
/// </summary>
public sealed class DashboardOverviewPackHealthRowBuilderTests
{
    // ---------- 1. Providers visited by ascending Order. ----------

    [Fact]
    public async Task Providers_are_ordered_by_Order_ascending()
    {
        // Registered out-of-order on purpose: the row builder must sort by
        // Order, NOT trust DI's registration order.
        var providers = new IPackHealthSummaryProvider[]
        {
            new StubProvider(order: 300, tileTitle: "third"),
            new StubProvider(order: 100, tileTitle: "first"),
            new StubProvider(order: 200, tileTitle: "second"),
        };

        var builder = new DashboardOverviewPackHealthRowBuilder(providers);

        var result = await builder.BuildAsync(default);

        result.Tiles.Should().HaveCount(3);
        result.Tiles[0].Title.Should().Be("first");
        result.Tiles[1].Title.Should().Be("second");
        result.Tiles[2].Title.Should().Be("third");
    }

    // ---------- 2. Null tile is silently skipped (surface not enabled). ----------

    [Fact]
    public async Task Provider_that_returns_null_is_skipped()
    {
        var providers = new IPackHealthSummaryProvider[]
        {
            new StubProvider(order: 100, tile: null), // surface not enabled
            new StubProvider(order: 200, tileTitle: "present"),
        };

        var builder = new DashboardOverviewPackHealthRowBuilder(providers);

        var result = await builder.BuildAsync(default);

        result.Tiles.Should().HaveCount(1);
        result.Tiles[0].Title.Should().Be("present");
    }

    // ---------- 3. A throwing provider does not take down the row. ----------

    [Fact]
    public async Task Provider_that_throws_does_not_take_down_the_row()
    {
        var providers = new IPackHealthSummaryProvider[]
        {
            new ThrowingProvider(order: 100, new InvalidOperationException("boom")),
            new StubProvider(order: 200, tileTitle: "survivor"),
        };

        var builder = new DashboardOverviewPackHealthRowBuilder(providers);

        var result = await builder.BuildAsync(default);

        result.Tiles.Should().HaveCount(1);
        result.Tiles[0].Title.Should().Be("survivor");
    }

    // ---------- 4. OperationCanceledException PROPAGATES (cancellation never swallowed). ----------

    [Fact]
    public async Task OperationCanceledException_propagates_unswallowed()
    {
        var providers = new IPackHealthSummaryProvider[]
        {
            new ThrowingProvider(order: 100, new OperationCanceledException()),
            new StubProvider(order: 200, tileTitle: "never-reached"),
        };

        var builder = new DashboardOverviewPackHealthRowBuilder(providers);

        var act = async () => await builder.BuildAsync(default);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---------- 5. Empty provider list -> empty tile list. ----------

    [Fact]
    public async Task Empty_provider_list_yields_empty_tile_list()
    {
        var builder = new DashboardOverviewPackHealthRowBuilder(
            Array.Empty<IPackHealthSummaryProvider>());

        var result = await builder.BuildAsync(default);

        result.Tiles.Should().BeEmpty();
    }

    // ============================================================
    // Stubs
    // ============================================================

    /// <summary>
    ///     Manual <see cref="IPackHealthSummaryProvider"/> stub. Two ctors:
    ///     one builds a tile from a title (the common case), the other accepts
    ///     a raw tile-or-null so tests can express "this surface is absent".
    /// </summary>
    private sealed class StubProvider : IPackHealthSummaryProvider
    {
        private readonly StatTileViewModel? _tile;

        public StubProvider(int order, string tileTitle)
        {
            Order = order;
            _tile = new StatTileViewModel(Title: tileTitle, Value: "0");
        }

        public StubProvider(int order, StatTileViewModel? tile)
        {
            Order = order;
            _tile = tile;
        }

        public int Order { get; }

        public Task<StatTileViewModel?> BuildTileAsync(CancellationToken ct)
            => Task.FromResult(_tile);
    }

    /// <summary>
    ///     Provider stub that throws on tile build. Used to assert both
    ///     "row survives a misbehaving provider" and "cancellation
    ///     propagates" -- the two paths share a tiny class so the test
    ///     setup stays compact.
    /// </summary>
    private sealed class ThrowingProvider : IPackHealthSummaryProvider
    {
        private readonly Exception _toThrow;

        public ThrowingProvider(int order, Exception toThrow)
        {
            Order = order;
            _toThrow = toThrow;
        }

        public int Order { get; }

        public Task<StatTileViewModel?> BuildTileAsync(CancellationToken ct)
            => throw _toThrow;
    }
}
