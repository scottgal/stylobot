using FluentAssertions;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Identity.BrowserModes;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity.BrowserModes;

/// <summary>
///     Catalogue-level coverage for the DB-backed mode centroid surface
///     (T11b). The classifier (T12), DI wiring (T14), and YAML seed source
///     (T13) layer on top of this; the contract this file pins is:
///     <list type="bullet">
///         <item><description>Cold start: empty table -&gt; seed from
///         <see cref="IBrowserModeSeedSource"/>, persist into store,
///         return seeded rows.</description></item>
///         <item><description>Warm read: rows already exist -&gt; read
///         direct, no seed pass (so persisted drift wins over the YAML
///         prior on every subsequent boot).</description></item>
///         <item><description>Per-row write uses the catalogue's
///         <c>browser_mode</c> discriminator so classic identity
///         archetypes never get clobbered.</description></item>
///     </list>
/// </summary>
public sealed class ModeCentroidCatalogueTests
{
    [Fact]
    public async Task LoadAsync_seeds_from_yaml_when_table_is_empty()
    {
        var store = new InMemoryArchetypeStore();
        var seeds = new FakeSeedSource(new[]
        {
            new ModeSeed("navigation", new[] { 1f, 1f, 0f, 0f }),
            new ModeSeed("xhr",        new[] { 1f, 0f, 1f, 0f }),
        });
        var catalogue = new ModeCentroidCatalogue(store, seeds);

        var first = await catalogue.LoadAsync();

        first.Should().HaveCount(2);
        first.Select(r => r.ModeId).Should().BeEquivalentTo(new[] { "navigation", "xhr" });
        store.UpsertedKeys.Should().BeEquivalentTo(new[] { "navigation", "xhr" });
        store.UpsertedRows.Should().AllSatisfy(r =>
            r.CatalogueKind.Should().Be(ModeCentroidCatalogue.CatalogueKind));
    }

    [Fact]
    public async Task LoadAsync_reads_from_store_when_rows_already_exist_no_reseed()
    {
        var store = new InMemoryArchetypeStore();
        // Simulate a previously-drifted row already in the table.
        await store.UpsertCentroidAsync(
            "navigation",
            ModeCentroidCatalogue.CatalogueKind,
            new[] { 0.9f, 0.95f, 0.05f, 0f },
            maturity: 42.0);
        store.UpsertedKeys.Clear(); // reset write-tracking counter

        var seeds = new FakeSeedSource(new[]
        {
            new ModeSeed("navigation", new[] { 1f, 1f, 0f, 0f }),
        });
        var catalogue = new ModeCentroidCatalogue(store, seeds);

        var rows = await catalogue.LoadAsync();

        rows.Should().HaveCount(1);
        rows[0].ModeId.Should().Be("navigation");
        rows[0].Centroid.Should().Equal(0.9f, 0.95f, 0.05f, 0f);
        rows[0].Maturity.Should().Be(42.0);
        store.UpsertedKeys.Should().BeEmpty("no second seed pass when the table already has rows");
    }

    [Fact]
    public async Task LoadAsync_filters_to_browser_mode_catalogue_kind()
    {
        var store = new InMemoryArchetypeStore();
        // Pollute the same table with an unrelated identity archetype row to
        // confirm the catalogue's read filter excludes other kinds.
        await store.UpsertCentroidAsync(
            "chrome-desktop",
            catalogueKind: "identity",
            centroid: new[] { 0.5f, 0.5f, 0.5f },
            maturity: 7.0);

        var seeds = new FakeSeedSource(new[]
        {
            new ModeSeed("navigation", new[] { 1f, 1f, 0f, 0f }),
        });
        var catalogue = new ModeCentroidCatalogue(store, seeds);

        var rows = await catalogue.LoadAsync();

        rows.Should().HaveCount(1);
        rows[0].ModeId.Should().Be("navigation");
        // The identity-kind row is still in the store; the catalogue read
        // just doesn't see it.
        store.AllRows.Should().ContainKey("chrome-desktop");
    }

    private sealed class FakeSeedSource : IBrowserModeSeedSource
    {
        private readonly IReadOnlyList<ModeSeed> _seeds;
        public FakeSeedSource(IEnumerable<ModeSeed> seeds) => _seeds = seeds.ToList();
        public IReadOnlyList<ModeSeed> LoadYamlSeeds() => _seeds;
    }

    /// <summary>
    ///     Minimal store double scoped to the two surface methods T11b adds.
    ///     Inherits from <see cref="NullFingerprintStore"/> so every other
    ///     <see cref="IFingerprintStore"/> method is a safe no-op without us
    ///     having to stub thirty interface members.
    /// </summary>
    private sealed class InMemoryArchetypeStore : NullFingerprintStore
    {
        public List<string> UpsertedKeys { get; } = new();
        public List<IdentityArchetypeRow> UpsertedRows => _rows.Values.ToList();
        public IReadOnlyDictionary<string, IdentityArchetypeRow> AllRows => _rows;
        private readonly Dictionary<string, IdentityArchetypeRow> _rows = new();

        public override Task<IReadOnlyList<IdentityArchetypeRow>> GetByCatalogueKindAsync(
            string catalogueKind, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IdentityArchetypeRow>>(
                _rows.Values
                    .Where(r => string.Equals(r.CatalogueKind, catalogueKind, StringComparison.Ordinal))
                    .ToList());

        public override Task UpsertCentroidAsync(
            string archetypeId,
            string catalogueKind,
            float[] centroid,
            double maturity,
            CancellationToken ct = default)
        {
            UpsertedKeys.Add(archetypeId);
            _rows[archetypeId] = new IdentityArchetypeRow(
                archetypeId, catalogueKind, centroid, maturity);
            return Task.CompletedTask;
        }
    }
}
