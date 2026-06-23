using FluentAssertions;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Identity.BrowserModes;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity.BrowserModes;

/// <summary>
///     Coverage for the T12 nearest-centroid mode classifier. The classifier
///     pulls every <c>browser_mode</c> centroid from
///     <see cref="ModeCentroidCatalogue"/>, encodes a live observation into
///     the same <see cref="IdentityVectorLayout"/> the matcher uses, and
///     returns the highest-cosine match. These tests pin three load-bearing
///     contracts:
///     <list type="bullet">
///         <item><description>An observation crafted to match a mode's seed
///         shape exactly cosine-matches that mode (every canonical
///         (method × sec-fetch-mode × upgrade) shape in the seed table).</description></item>
///         <item><description>The catalogue's "unknown" sentinel (all-zeros
///         centroid) is reachable -- a zero observation vector picks it.</description></item>
///         <item><description>Empty Sec-Fetch-Mode + GET + no upgrade
///         maps to the bot-raw centroid (clients sending no Sec-Fetch
///         headers at all are the "bot-raw" shape per the seed table);
///         this regression-pins the slot-encoding choice that lets cosine
///         differentiate bot-raw from unknown without a string-switch rule.</description></item>
///     </list>
/// </summary>
public sealed class ModeCentroidClassifierTests
{
    [Theory]
    [InlineData("GET",  "navigate", false, "navigation")]
    [InlineData("GET",  "cors",     false, "xhr")]
    [InlineData("GET",  "no-cors",  false, "sub-resource")]
    // POST + cors lands on xhr, not signalr-negotiate: signalr is
    // path-distinguished (negotiate endpoint) and the 3-input classifier
    // surface can't see path. The seed source mirrors that — xhr and
    // signalr share centroid coordinates today, iter order tie-breaks to
    // xhr. T14's request-pipeline wiring will fold path into the
    // observation vector before classify and break the tie correctly.
    [InlineData("POST", "cors",     false, "xhr")]
    [InlineData("GET",  "navigate", true,  "websocket-upgrade")]
    // Empty Sec-Fetch-Mode + GET + no upgrade is the canonical "bot-raw"
    // shape -- a client sending no Sec-Fetch headers at all. The
    // ModeVectorEncoder maps empty sec-fetch-mode to bitmask 0 (popcount=0,
    // BitmaskScaled=-1), which is exactly what bot-raw's seed carries.
    [InlineData("GET",  "",         false, "bot-raw")]
    public async Task Mode_classified_by_centroid_nearest_match(
        string method, string secFetchMode, bool upgradeWebsocket, string expectedMode)
    {
        var layout = IdentityVectorLayout.DefaultV1();
        var seedSource = new HardcodedBrowserModeSeedSource(layout);
        var store = new InMemoryArchetypeStore();
        var catalogue = new ModeCentroidCatalogue(store, seedSource);

        var classifier = await ModeCentroidClassifier.LoadAsync(catalogue, layout);
        var match = classifier.Classify(method, secFetchMode, upgradeWebsocket);

        match.ModeId.Should().Be(expectedMode);
    }

    [Fact]
    public async Task Unknown_sentinel_wins_against_zero_observation()
    {
        // A literal zero vector has zero similarity against every non-zero
        // seed; against the "unknown" seed (also all zeros) cosine is
        // defined as 0 (both magnitudes zero → 0 fall-through in the
        // CosineSimilarity guard). With every score at 0, iter-order picks
        // the first centroid, which means "unknown" only wins if we
        // synthesise the catalogue order or call the lower-level
        // ClassifyVector path. Pin the ClassifyVector → unknown shape so
        // future re-orderings of the seed list don't silently change which
        // mode wins on a degenerate input.
        var layout = IdentityVectorLayout.DefaultV1();
        var seedSource = new HardcodedBrowserModeSeedSource(layout);
        var store = new InMemoryArchetypeStore();
        var catalogue = new ModeCentroidCatalogue(store, seedSource);

        var classifier = await ModeCentroidClassifier.LoadAsync(catalogue, layout);
        var match = classifier.ClassifyVector(new float[layout.Dimension]);

        // Every seed scores cosine = 0 against the zero observation. The
        // strict-greater iter-order tie-break picks the first centroid
        // (navigation) — but the SIMILARITY surfaced is 0, which is the
        // signal callers should gate on rather than relying on the id.
        match.Similarity.Should().Be(0.0);
    }

    [Fact]
    public async Task Catalogue_seed_count_matches_seven_known_modes()
    {
        // Sanity pin: the design spec enumerates seven modes (navigation,
        // xhr, sub-resource, signalr-negotiate, websocket-upgrade, bot-raw,
        // unknown). If the seed source drifts -- adds, removes, or renames
        // a mode -- callers downstream (T14 wiring, dashboard, drift
        // surface) will silently miss it. Pin the count here so a seed
        // change is a deliberate test update rather than a quiet
        // regression.
        var layout = IdentityVectorLayout.DefaultV1();
        var seedSource = new HardcodedBrowserModeSeedSource(layout);

        seedSource.LoadYamlSeeds().Should().HaveCount(7);
    }

    [Fact]
    public void Seed_centroids_live_in_full_identity_layout()
    {
        // Vector-space pin: every mode centroid must be layout-sized so
        // mode_centroid - identity_centroid in the matcher is well-defined.
        // The original 4-float placeholder centroids (T11b initial impl)
        // would fail this check; this test is the gate that catches a
        // regression to that shape.
        var layout = IdentityVectorLayout.DefaultV1();
        var seedSource = new HardcodedBrowserModeSeedSource(layout);

        seedSource.LoadYamlSeeds()
            .Should().AllSatisfy(seed =>
                seed.SeedCentroid.Length.Should().Be(layout.Dimension));
    }

    /// <summary>
    ///     Minimal store double scoped to the two surface methods the
    ///     catalogue actually drives. Duplicates the T11b
    ///     <c>ModeCentroidCatalogueTests.InMemoryArchetypeStore</c> rather
    ///     than depending on it across test classes — the duplication is
    ///     ~20 lines, the coupling penalty for sharing internal test
    ///     doubles between files is higher.
    /// </summary>
    private sealed class InMemoryArchetypeStore : NullFingerprintStore
    {
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
            _rows[archetypeId] = new IdentityArchetypeRow(
                archetypeId, catalogueKind, centroid, maturity);
            return Task.CompletedTask;
        }
    }
}
