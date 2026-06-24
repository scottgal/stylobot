using System;
using System.IO;
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
///     property of the NAME, not of one composer. Any caller writing a
///     display name through <see cref="IFingerprintStore.UpdateDisplayNameForSignatureAsync"/>
///     (LLM callbacks per task #115, operator labels, legacy imports) must
///     be blocked from landing a banned shape. A regression that allows a
///     banned shape to land via this entry point silently reduces the
///     trichotomy contract to a polite suggestion.
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
    public async Task UpdateDisplayNameForSignatureAsync_rejects_banned_shapes(string candidate, bool shouldLand)
    {
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        const string fpId = "fp-contract";
        const string sig  = "sig-contract";
        await store.InsertFingerprintAsync(NewFingerprint(fpId, dim), primarySignature: sig);

        await store.UpdateDisplayNameForSignatureAsync(sig, candidate, DateTime.UtcNow, source: "test");
        var fp = await store.GetFingerprintAsync(fpId);

        fp.Should().NotBeNull();
        if (shouldLand)
        {
            fp!.DisplayName.Should().Be(candidate,
                "allowed shape must land verbatim through the signature-keyed write");
        }
        else
        {
            fp!.DisplayName.Should().NotBe(candidate,
                "banned shape must be rejected and normalised to Unknown <hex>");
            fp.DisplayName.Should().StartWith("Unknown ",
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
        await store.UpdateDisplayNameForSignatureAsync(
            "sig-counter", "Chrome (privacy-aware)", DateTime.UtcNow, source: "test");

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
        await store.UpdateDisplayNameForSignatureAsync(
            "sig-clean", "Bingbot", DateTime.UtcNow, source: "test");

        store.BannedShapeRejectionsCount.Should().Be(before,
            "allowed shapes must NOT tick the counter");
    }

    // ── Extended T24a coverage: every write path must call the gate ────────────
    //
    // T24 staging discovered banned-shape rows persisted on disk
    // ("Chrome Desktop (missing client hints)", "Chrome Desktop (header drift) (3c2a33b1)").
    // T5a's gate covered UpdateDisplayNameForSignatureAsync ONLY; the matcher's
    // direct UpdateDisplayNameAsync call and the row's initial
    // InsertFingerprintAsync allocation bypassed it. T24a extends the gate to
    // every path that writes display_name, so the trichotomy contract can no
    // longer be silently violated by which entry point the caller picked.

    [Theory]
    [InlineData("Bingbot",                                     true)]
    [InlineData("Chrome",                                      true)]
    [InlineData("Unknown abcdef12",                            true)]
    [InlineData("Chrome Desktop (missing client hints)",       false)]
    [InlineData("Chrome Desktop (header drift) (3c2a33b1)",    false)]
    [InlineData("Mac Chrome 149 w/ uBlock GB",                 false)]
    public async Task UpdateDisplayNameAsync_rejects_banned_shapes_via_normaliser(string candidate, bool shouldLand)
    {
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        const string fpId = "abc123def456-update";
        await store.InsertFingerprintAsync(NewFingerprint(fpId, dim), primarySignature: "sig-update");

        await store.UpdateDisplayNameAsync(fpId, candidate, DateTime.UtcNow, source: "test");
        var fp = await store.GetFingerprintAsync(fpId);

        fp.Should().NotBeNull();
        if (shouldLand)
        {
            fp!.DisplayName.Should().Be(candidate,
                "allowed shape must land verbatim through the direct id-keyed write");
        }
        else
        {
            fp!.DisplayName.Should().NotBe(candidate,
                "banned shape via UpdateDisplayNameAsync must be normalised at the store layer");
            fp.DisplayName.Should().StartWith("Unknown ",
                "banned shape must be normalised to the priority-4 fallback");
        }
    }

    [Fact]
    public async Task UpdateDisplayNameAsync_banned_shape_writes_increment_the_rejection_counter()
    {
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        const string fpId = "fp-update-counter";
        await store.InsertFingerprintAsync(NewFingerprint(fpId, dim), primarySignature: "sig-update-counter");

        var before = store.BannedShapeRejectionsCount;
        await store.UpdateDisplayNameAsync(
            fpId, "Chrome (privacy-aware)", DateTime.UtcNow, source: "test");

        store.BannedShapeRejectionsCount.Should().Be(before + 1,
            "every banned-shape write must tick the counter regardless of entry point");
    }

    [Fact]
    public async Task UpdateDisplayNameAsync_allows_empty_string_clears()
    {
        // The absorption service writes string.Empty to clear a stale name after
        // an archetype flip (bot -> human and similar). Empty is NOT a banned
        // shape -- it's an explicit reset -- and must pass through without
        // incrementing the rejection counter.
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        const string fpId = "fp-clear";
        await store.InsertFingerprintAsync(NewFingerprint(fpId, dim), primarySignature: "sig-clear");
        await store.UpdateDisplayNameAsync(fpId, "Bingbot", DateTime.UtcNow, source: "seed");

        var before = store.BannedShapeRejectionsCount;
        await store.UpdateDisplayNameAsync(fpId, string.Empty, DateTime.UtcNow, source: "absorption");

        var fp = await store.GetFingerprintAsync(fpId);
        fp!.DisplayName.Should().Be(string.Empty,
            "empty string is an explicit clear, not a banned-shape rejection");
        store.BannedShapeRejectionsCount.Should().Be(before,
            "empty-string clears must NOT tick the rejection counter");
    }

    [Theory]
    [InlineData("Bingbot",                                     true)]
    [InlineData("Chrome Desktop (missing client hints)",       false)]
    [InlineData("Chrome Desktop (header drift) (3c2a33b1)",    false)]
    public async Task InsertFingerprintAsync_rejects_banned_shapes_via_normaliser(string candidate, bool shouldLand)
    {
        // The matcher seeds display_name on the brand-new fingerprint row
        // (verifiedbot path AND new-allocation path); a banned shape from the
        // composer at allocation time must not land on disk row 1.
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        var fpId = $"abcdef0123456-{Guid.NewGuid():N}";

        var fp = NewFingerprint(fpId, dim);
        var seeded = fp with { DisplayName = candidate, DisplayNameUpdatedAt = DateTime.UtcNow };
        await store.InsertFingerprintAsync(seeded, primarySignature: $"sig-{fpId}");

        var stored = await store.GetFingerprintAsync(fpId);
        stored.Should().NotBeNull();
        if (shouldLand)
        {
            stored!.DisplayName.Should().Be(candidate,
                "allowed shape must land verbatim at row allocation");
        }
        else
        {
            stored!.DisplayName.Should().NotBe(candidate,
                "banned shape at insert time must be normalised at the store layer");
            stored.DisplayName.Should().StartWith("Unknown ",
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
            DisplayName = "Chrome Desktop (missing client hints)",
            DisplayNameUpdatedAt = DateTime.UtcNow,
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
