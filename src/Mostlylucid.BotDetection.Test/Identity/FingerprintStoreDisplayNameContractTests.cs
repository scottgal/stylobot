using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Pins the store-layer enforcement of the display-name contract.
///     <see cref="FingerprintNameComposerContract.IsAllowedShape"/> is a
///     property of the NAME, not of one composer. Every slot-aware name
///     updater on <see cref="IFingerprintStore"/>
///     (<see cref="IFingerprintStore.UpdateInducedNameAsync"/>,
///     <see cref="IFingerprintStore.UpdateLlmNameAsync"/>,
///     <see cref="IFingerprintStore.UpdateGivenNameAsync"/>) must block a
///     banned-shape candidate from landing.
///
///     Banned shapes are normalised to the priority-4 "Unknown &lt;hex&gt;"
///     fallback so the call's contract that *some* name lands is preserved,
///     and a counter (<see cref="IFingerprintStore.BannedShapeRejectionsCount"/>)
///     ticks up so a dashboard / OTel meter can read the rejection rate.
/// </summary>
public class FingerprintStoreDisplayNameContractTests : IDisposable
{
    private readonly string _tempDir;

    public FingerprintStoreDisplayNameContractTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-displayname-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("Bingbot",                                     true)]
    [InlineData("Chrome",                                      true)]
    [InlineData("Unknown 8c41b2bd",                            true)]
    [InlineData("Chrome Desktop (missing client hints)",       false)]
    [InlineData("Mac Chrome 149 w/ uBlock GB",                 false)]
    public async Task UpdateLlmNameAsync_rejects_banned_shapes(string candidate, bool shouldLand)
    {
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        const string fpId = "fp-contract";
        const string sig  = "sig-contract";
        await store.InsertFingerprintAsync(NewFingerprint(fpId, dim), primarySignature: sig);
        await store.GetFingerprintAsync(fpId); // warm LFU dict

        await store.UpdateLlmNameAsync(fpId, candidate, description: null, DateTime.UtcNow, CancellationToken.None);
        var fp = await store.GetFingerprintAsync(fpId);

        fp.Should().NotBeNull();
        if (shouldLand)
        {
            FingerprintNameResolver.Resolve(fp).Should().Be(candidate,
                "allowed shape must land verbatim through the LLM-slot write");
        }
        else
        {
            FingerprintNameResolver.Resolve(fp).Should().NotBe(candidate,
                "banned shape must be rejected and normalised to Unknown <hex>");
            FingerprintNameResolver.Resolve(fp).Should().StartWith("Unknown ",
                "banned shape must be normalised to the priority-4 fallback");
        }
    }

    [Fact]
    public async Task Banned_shape_writes_increment_the_rejection_counter()
    {
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        await store.InsertFingerprintAsync(NewFingerprint("fp-counter", dim), primarySignature: "sig-counter");

        var before = store.BannedShapeRejectionsCount;
        await store.UpdateLlmNameAsync(
            "fp-counter", "Chrome (privacy-aware)", description: null, DateTime.UtcNow, CancellationToken.None);

        store.BannedShapeRejectionsCount.Should().Be(before + 1,
            "every banned-shape write must tick the counter so a meter can read the rate");
    }

    [Fact]
    public async Task Allowed_shape_writes_do_not_increment_the_rejection_counter()
    {
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        await store.InsertFingerprintAsync(NewFingerprint("fp-clean", dim), primarySignature: "sig-clean");

        var before = store.BannedShapeRejectionsCount;
        await store.UpdateLlmNameAsync(
            "fp-clean", "Bingbot", description: null, DateTime.UtcNow, CancellationToken.None);

        store.BannedShapeRejectionsCount.Should().Be(before,
            "allowed shapes must NOT tick the counter");
    }

    // ── T24a coverage extended to the matcher's induced slot writer ────────────
    //
    // Pre-split T24 staging discovered banned-shape rows persisted on disk
    // ("Chrome Desktop (missing client hints)", "Chrome Desktop (header drift) (3c2a33b1)").
    // Post-split the matcher writes through UpdateInducedNameAsync; same gate must fire.

    [Theory]
    [InlineData("Bingbot",                                     true)]
    [InlineData("Chrome",                                      true)]
    [InlineData("Unknown abcdef12",                            true)]
    [InlineData("Chrome Desktop (missing client hints)",       false)]
    [InlineData("Chrome Desktop (header drift) (3c2a33b1)",    false)]
    [InlineData("Mac Chrome 149 w/ uBlock GB",                 false)]
    public async Task UpdateInducedNameAsync_rejects_banned_shapes_via_normaliser(string candidate, bool shouldLand)
    {
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        const string fpId = "abc123def456-update";
        await store.InsertFingerprintAsync(NewFingerprint(fpId, dim), primarySignature: "sig-update");
        await store.GetFingerprintAsync(fpId); // warm LFU dict

        await store.UpdateInducedNameAsync(fpId, candidate, DateTime.UtcNow, CancellationToken.None);
        var fp = await store.GetFingerprintAsync(fpId);

        fp.Should().NotBeNull();
        if (shouldLand)
        {
            FingerprintNameResolver.Resolve(fp).Should().Be(candidate,
                "allowed shape must land verbatim through the induced-slot write");
        }
        else
        {
            FingerprintNameResolver.Resolve(fp).Should().NotBe(candidate,
                "banned shape via UpdateInducedNameAsync must be normalised at the store layer");
            FingerprintNameResolver.Resolve(fp).Should().StartWith("Unknown ",
                "banned shape must be normalised to the priority-4 fallback");
        }
    }

    [Fact]
    public async Task UpdateInducedNameAsync_banned_shape_writes_increment_the_rejection_counter()
    {
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        const string fpId = "fp-update-counter";
        await store.InsertFingerprintAsync(NewFingerprint(fpId, dim), primarySignature: "sig-update-counter");

        var before = store.BannedShapeRejectionsCount;
        await store.UpdateInducedNameAsync(
            fpId, "Chrome (privacy-aware)", DateTime.UtcNow, CancellationToken.None);

        store.BannedShapeRejectionsCount.Should().Be(before + 1,
            "every banned-shape write must tick the counter regardless of slot");
    }

    [Theory]
    [InlineData("Bingbot",                                     true)]
    [InlineData("Chrome Desktop (missing client hints)",       false)]
    [InlineData("Chrome Desktop (header drift) (3c2a33b1)",    false)]
    public async Task InsertFingerprintAsync_rejects_banned_shapes_via_normaliser(string candidate, bool shouldLand)
    {
        // The matcher seeds InducedName on the brand-new fingerprint row
        // (verifiedbot path AND new-allocation path); a banned shape from the
        // composer at allocation time must not land on disk row 1.
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        var fpId = $"abcdef0123456-{Guid.NewGuid():N}";

        var fp = NewFingerprint(fpId, dim);
        var seeded = fp with { InducedName = candidate, InducedNameUpdatedAt = DateTime.UtcNow };
        await store.InsertFingerprintAsync(seeded, primarySignature: $"sig-{fpId}");

        var stored = await store.GetFingerprintAsync(fpId);
        stored.Should().NotBeNull();
        if (shouldLand)
        {
            FingerprintNameResolver.Resolve(stored).Should().Be(candidate,
                "allowed shape must land verbatim at row allocation");
        }
        else
        {
            FingerprintNameResolver.Resolve(stored).Should().NotBe(candidate,
                "banned shape at insert time must be normalised at the store layer");
            FingerprintNameResolver.Resolve(stored).Should().StartWith("Unknown ",
                "banned shape must be normalised to the priority-4 fallback");
        }
    }

    [Fact]
    public async Task InsertFingerprintAsync_banned_shape_writes_increment_the_rejection_counter()
    {
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        var fpId = $"fp-insert-counter-{Guid.NewGuid():N}";

        var fp = NewFingerprint(fpId, dim);
        var seeded = fp with
        {
            InducedName = "Chrome Desktop (missing client hints)",
            InducedNameUpdatedAt = DateTime.UtcNow,
        };

        var before = store.BannedShapeRejectionsCount;
        await store.InsertFingerprintAsync(seeded, primarySignature: $"sig-{fpId}");

        store.BannedShapeRejectionsCount.Should().Be(before + 1,
            "every banned-shape write at insert time must tick the counter");
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
}
