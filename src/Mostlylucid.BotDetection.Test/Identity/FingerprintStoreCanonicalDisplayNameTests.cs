using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Integration pin for the canonical-casing normalisation hook inside
///     <see cref="SqliteFingerprintStore.UpdateDisplayNameAsync"/> /
///     <see cref="SqliteFingerprintStore.UpdateDisplayNameForSignatureAsync"/>.
///     Whatever spelling a contributor or LLM-namer hands the store
///     ("googlebot" / "GOOGLEBOT") is folded to the BotPatternLoader catalog's
///     canonical casing BEFORE landing on the row. Unknown names (operator
///     labels, fediverse instance suffixes not in the catalog) pass through
///     untouched. End-to-end round-trip through the real SQLite file so a
///     regression that drops the hook fails loud against the actual store,
///     not just the unit-tested helper.
/// </summary>
public class FingerprintStoreCanonicalDisplayNameTests : IDisposable
{
    private readonly string _tempDir;

    public FingerprintStoreCanonicalDisplayNameTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-canon-displayname-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<SqliteFingerprintStore> NewStoreAsync()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions { Enabled = true }
        });
        var layout = IdentityVectorLayout.DefaultV1();
        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance, options, layout);
        await store.EnsureInitialisedAsync();
        return store;
    }

    private static Fingerprint NewFingerprint(string id, int dim)
    {
        var now = DateTime.UtcNow;
        var weights = new float[dim];
        Array.Fill(weights, 1.0f);
        return new Fingerprint
        {
            FingerprintId = id,
            Centroid = new float[dim],
            CentroidMaturity = 1,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 1,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = 0.8,
            InferredClientType = "chrome-desktop",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now,
            ClaimStatus = "unverified",
            TrustObservations = 0,
        };
    }

    [Theory]
    [InlineData("googlebot",  "Googlebot")]
    [InlineData("GOOGLEBOT",  "Googlebot")]
    [InlineData("Googlebot",  "Googlebot")]
    public async Task UpdateDisplayNameAsync_normalises_via_BotPatternLoader(string written, string expected)
    {
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        await store.InsertFingerprintAsync(NewFingerprint("fp-canon", dim), primarySignature: "sig-canon");

        await store.UpdateDisplayNameAsync("fp-canon", written, DateTime.UtcNow);

        var fp = await store.GetFingerprintAsync("fp-canon");
        Assert.NotNull(fp);
        Assert.Equal(expected, fp!.DisplayName);
    }

    [Fact]
    public async Task UpdateDisplayNameAsync_passes_unknown_name_through_unchanged()
    {
        // Operator-set labels and fediverse instance suffixes aren't in the catalog --
        // the normaliser must leave them alone. Round-trip through the store so a
        // regression that always-canonicalises (and accidentally Title-cases custom
        // names) fails here.
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        await store.InsertFingerprintAsync(NewFingerprint("fp-custom", dim), primarySignature: "sig-custom");

        await store.UpdateDisplayNameAsync("fp-custom", "Customer FX scraper", DateTime.UtcNow);

        var fp = await store.GetFingerprintAsync("fp-custom");
        Assert.NotNull(fp);
        Assert.Equal("Customer FX scraper", fp!.DisplayName);
    }

    [Fact]
    public async Task UpdateDisplayNameForSignatureAsync_also_normalises()
    {
        // The signature-keyed overload (used by LlmResultSignalRCallback) shares the
        // same write boundary -- pin that it also canonicalises so the LLM namer
        // can't sneak a casing variant in past the matcher path.
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        await store.InsertFingerprintAsync(NewFingerprint("fp-llm", dim), primarySignature: "sig-llm");

        await store.UpdateDisplayNameForSignatureAsync("sig-llm", "googlebot", DateTime.UtcNow);

        var fp = await store.GetFingerprintAsync("fp-llm");
        Assert.NotNull(fp);
        Assert.Equal("Googlebot", fp!.DisplayName);
    }
}