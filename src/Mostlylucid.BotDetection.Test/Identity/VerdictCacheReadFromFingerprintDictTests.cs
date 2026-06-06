using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Task 1 of the verdict-cache un-drift plan: pin the contract that a verdict
///     written on the request path is visible to the next L1 lookup without waiting
///     for <c>FingerprintDriftService</c> to tick. Same-visitor bursts (HTML + the
///     CSS/JS/asset fetches that follow within ~1 second) must hit the cache.
///
///     Both tests are expected to fail at compile time on the
///     <c>RecordVerdictAsync</c> call until Task 2 lands the implementation.
/// </summary>
public class VerdictCacheReadFromFingerprintDictTests : IDisposable
{
    private readonly string _tempDir;

    public VerdictCacheReadFromFingerprintDictTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-verdict-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<(SqliteFingerprintStore Store, IOptions<BotDetectionOptions> Options)> NewStoreAsync()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions { Enabled = true }
        });
        var layout = IdentityVectorLayout.DefaultV1();
        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance,
            options,
            layout);
        await store.EnsureInitialisedAsync();
        return (store, options);
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
            InferredTypeChangedAt = now
        };
    }

    [Fact]
    public async Task RecordVerdict_FollowedByLookup_ReturnsVerdictWithoutDriftServiceTick()
    {
        var (store, options) = await NewStoreAsync();
        var lookup = new IdentityVerdictLookup(
            NullLogger<IdentityVerdictLookup>.Instance,
            store,
            options);

        var primarySig = "test-primary-sig-1";
        var fpId = "test-fp-1";
        var dim = store.Layout.Dimension;
        await store.InsertFingerprintAsync(NewFingerprint(fpId, dim), primarySig, CancellationToken.None);

        var before = await lookup.TryGetAsync(primarySig);
        before.Should().BeNull("a freshly-allocated fingerprint has no cached verdict yet");

        await store.RecordVerdictAsync(fpId, botProbability: 0.12, riskBand: "Low", CancellationToken.None);

        var burst = await lookup.TryGetAsync(primarySig);
        burst.Should().NotBeNull("the verdict written one millisecond ago must be visible to the next L1 lookup");
        burst!.BotProbability.Should().BeApproximately(0.12, 1e-6,
            "first-ever verdict write is a direct assignment, not an EWMA blend");
        burst.FingerprintId.Should().Be(fpId);
    }

    [Fact]
    public async Task RecordVerdict_TwiceWithDifferentProbabilities_ExposesEwmaBlend()
    {
        var (store, options) = await NewStoreAsync();
        var primarySig = "test-primary-sig-2";
        var fpId = "test-fp-2";
        var dim = store.Layout.Dimension;
        await store.InsertFingerprintAsync(NewFingerprint(fpId, dim), primarySig, CancellationToken.None);

        await store.RecordVerdictAsync(fpId, 0.10, "Low", CancellationToken.None);
        await store.RecordVerdictAsync(fpId, 0.90, "VeryHigh", CancellationToken.None);

        var lookup = new IdentityVerdictLookup(
            NullLogger<IdentityVerdictLookup>.Instance,
            store,
            options);

        var verdict = await lookup.TryGetAsync(primarySig);
        verdict.Should().NotBeNull();
        verdict!.BotProbability.Should().BeInRange(0.10, 0.90,
            "EWMA blend must land between the two writes, not overwrite to the second value");
        verdict.BotProbability.Should().NotBe(0.90,
            "direct overwrite would write 0.90 verbatim; EWMA must dampen the swing");
    }
}