using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Integration pin for the IMMUTABLE archetype anchor vs MUTABLE current
///     label contract from the identity / mode / archetype spec.
///     <see cref="Fingerprint.ArchetypeOrigin"/> is set once at insert and is
///     never overwritten by any subsequent write -- it is the historical
///     first-pin anchor that the drift surface (Identity T18/T19) projects
///     against. <see cref="Fingerprint.InferredClientType"/> is the current
///     nearest-archetype label and drifts as the centroid moves.
///     Without this pin, a future refactor that adds <c>archetype_origin</c>
///     to an UPDATE SET clause would silently destroy the drift story (origin
///     would always equal current, so the diff would always be zero).
/// </summary>
public class ArchetypeAnchorImmutableTests : IDisposable
{
    private readonly string _tempDir;

    public ArchetypeAnchorImmutableTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-archetype-anchor-{Guid.NewGuid():N}");
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

    private static Fingerprint NewFingerprint(string id, int dim, string archetypeOrigin)
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
            ArchetypeOrigin = archetypeOrigin,
            // At insert, the live label matches the origin -- the absorption /
            // calibration loop is what eventually drifts the live label away.
            InferredClientType = archetypeOrigin,
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now,
            ClaimStatus = "unverified",
            TrustObservations = 0,
        };
    }

    [Fact]
    public async Task Origin_archetype_is_set_at_insert_and_never_overwritten()
    {
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        const string fpId = "fp-anchor";

        await store.InsertFingerprintAsync(
            NewFingerprint(fpId, dim, archetypeOrigin: "chrome-desktop"),
            primarySignature: "sig-anchor",
            CancellationToken.None);

        var initial = await store.GetFingerprintAsync(fpId);
        initial.Should().NotBeNull();
        initial!.ArchetypeOrigin.Should().Be("chrome-desktop",
            "the first-pin anchor is the archetype the fingerprint started at");
        initial.InferredClientType.Should().Be("chrome-desktop",
            "the live label equals the origin until drift moves it");

        // Drive an absorption that flips the live label -- this is the same
        // entry point the FingerprintAbsorptionService uses when a fingerprint's
        // centroid has drifted close enough to a different archetype that the
        // calibration pass picks a new nearest neighbour.
        var newCentroid = new float[dim];
        var newWeights = new float[dim];
        Array.Fill(newWeights, 1.0f);
        await store.AbsorbObservationAsync(
            observationId: -1, // no real observation row -- the fp UPDATE is what we're pinning
            fingerprintId: fpId,
            newCentroid: newCentroid,
            newMaturity: 2,
            newWeights: newWeights,
            newInferredClientType: "googlebot",
            newInferredTypeConfidence: 0.92,
            inferredTypeChanged: true,
            ct: CancellationToken.None);

        var drifted = await store.GetFingerprintAsync(fpId);
        drifted.Should().NotBeNull();
        drifted!.ArchetypeOrigin.Should().Be("chrome-desktop",
            "archetype_origin is history; never overwritten by absorption or any other path");
        drifted.InferredClientType.Should().Be("googlebot",
            "the current label reflects post-drift nearest-archetype");
    }
}
