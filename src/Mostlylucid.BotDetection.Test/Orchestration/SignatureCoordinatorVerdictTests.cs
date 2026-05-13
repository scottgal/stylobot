using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration;

public class SignatureCoordinatorVerdictTests
{
    private static SignatureCoordinator CreateCoordinator()
    {
        var opts = new BotDetectionOptions();
        return new SignatureCoordinator(
            NullLogger<SignatureCoordinator>.Instance,
            Options.Create(opts));
    }

    private static Task RecordAsync(
        SignatureCoordinator coord,
        string signature,
        double botProbability,
        string path = "/",
        IReadOnlyDictionary<string, object>? signals = null)
    {
        return coord.RecordRequestAsync(
            signature: signature,
            requestId: Guid.NewGuid().ToString("N"),
            path: path,
            botProbability: botProbability,
            signals: signals ?? new Dictionary<string, object>(),
            detectorsRan: new HashSet<string> { "Heuristic" });
    }

    private static async Task<SignatureVerdict?> WaitForVerdictAsync(
        SignatureCoordinator coord, string signature, int minRequests)
    {
        SignatureVerdict? verdict = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            verdict = await coord.TryGetVerdictAsync(signature);
            if (verdict is not null && verdict.RequestCount >= minRequests)
                return verdict;
            await Task.Delay(20);
        }
        return verdict;
    }

    [Fact]
    public async Task TryGetVerdictAsync_UnknownSignature_ReturnsNull()
    {
        await using var coord = CreateCoordinator();
        var v = await coord.TryGetVerdictAsync("never-seen");
        Assert.Null(v);
    }

    [Fact]
    public async Task TryGetVerdictAsync_AfterRecordRequest_ReturnsSnapshot()
    {
        await using var coord = CreateCoordinator();
        await RecordAsync(coord, "sig-known", 0.42, "/api/x");

        // The coordinator processes updates via a KeyedSequentialAtom; allow a brief
        // window for the in-flight update to complete before reading the snapshot.
        SignatureVerdict? verdict = null;
        for (var attempt = 0; attempt < 50 && verdict is null; attempt++)
        {
            verdict = await coord.TryGetVerdictAsync("sig-known");
            if (verdict is null || verdict.RequestCount == 0)
            {
                verdict = null;
                await Task.Delay(20);
            }
        }

        Assert.NotNull(verdict);
        Assert.Equal("sig-known", verdict!.SignatureId);
        Assert.Equal(1, verdict.RequestCount);
        Assert.InRange(verdict.BotProbability, 0.0, 1.0);
        Assert.True(verdict.LastSeenUtc != default);
    }

    [Fact]
    public async Task TryGetVerdictAsync_MultipleRequests_ReflectsLatestAggregate()
    {
        await using var coord = CreateCoordinator();
        await RecordAsync(coord, "sig-multi", 0.1, "/");
        await RecordAsync(coord, "sig-multi", 0.2, "/api");
        await RecordAsync(coord, "sig-multi", 0.15, "/api/x");

        var verdict = await WaitForVerdictAsync(coord, "sig-multi", minRequests: 3);

        Assert.NotNull(verdict);
        Assert.Equal(3, verdict!.RequestCount);
    }

    [Fact]
    public async Task TryGetVerdictAsync_HighBotProbabilityWithSamples_ClassifiesAboveUnknown()
    {
        await using var coord = CreateCoordinator();
        // Push enough samples to clear the confidence < 0.3 floor (RequestCount/10 >= 0.3).
        for (var i = 0; i < 4; i++)
            await RecordAsync(coord, "sig-bot", botProbability: 0.92, path: "/api/scan");

        var verdict = await WaitForVerdictAsync(coord, "sig-bot", minRequests: 4);

        Assert.NotNull(verdict);
        Assert.NotEqual(RiskBand.Unknown, verdict!.RiskBand);
        Assert.True(verdict.RiskBand >= RiskBand.High,
            $"Expected band >= High for prob {verdict.BotProbability:F2}, got {verdict.RiskBand}");
    }

    [Fact]
    public async Task TryGetVerdictAsync_SurfacesLatestThreatScoreFromSignals()
    {
        await using var coord = CreateCoordinator();
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.IntentThreatScore] = 0.75
        };

        for (var i = 0; i < 4; i++)
            await RecordAsync(coord, "sig-threat", botProbability: 0.4, path: "/login", signals: signals);

        var verdict = await WaitForVerdictAsync(coord, "sig-threat", minRequests: 4);

        Assert.NotNull(verdict);
        Assert.Equal(0.75, verdict!.ThreatScore, precision: 3);
        Assert.True(verdict.RiskBand >= RiskBand.High,
            $"High threat score should escalate the band; got {verdict.RiskBand}");
    }

    [Fact]
    public async Task TryGetVerdictAsync_LowProbabilityNoThreat_StaysLowOrVeryLow()
    {
        await using var coord = CreateCoordinator();
        for (var i = 0; i < 6; i++)
            await RecordAsync(coord, "sig-human", botProbability: 0.05, path: "/");

        var verdict = await WaitForVerdictAsync(coord, "sig-human", minRequests: 6);

        Assert.NotNull(verdict);
        Assert.True(verdict!.RiskBand <= RiskBand.Low,
            $"Expected band <= Low for clean human signature, got {verdict.RiskBand}");
        Assert.Equal(0.0, verdict.ThreatScore);
    }

    [Fact]
    public async Task TryGetVerdictAsync_FallsThroughToCanonicalSignatureWhenSignatureIsFamilyMember()
    {
        await using var coord = CreateCoordinator();

        // Build up a confident verdict on the canonical signature.
        for (var i = 0; i < 6; i++)
            await RecordAsync(coord, "sig-canonical", botProbability: 0.9, path: "/api/scan");

        var canonical = await WaitForVerdictAsync(coord, "sig-canonical", minRequests: 6);
        Assert.NotNull(canonical);

        // Register a family with two members. The "sig-rotated" signature has never been
        // observed by the coordinator on its own — only as a family sibling.
        coord.RegisterFamily(new SignatureFamily
        {
            FamilyId = "fam-1",
            CanonicalSignature = "sig-canonical",
            MemberSignatures = SignatureFamily.CreateMemberSet(new[] { "sig-canonical", "sig-rotated" }),
            CreatedUtc = DateTime.UtcNow,
            LastEvaluatedUtc = DateTime.UtcNow,
            FormationReason = FamilyFormationReason.VectorSimilarity
        });

        var inheritedVerdict = await coord.TryGetVerdictAsync("sig-rotated");

        Assert.NotNull(inheritedVerdict);
        Assert.Equal("sig-rotated", inheritedVerdict!.SignatureId); // reported under the requested ID
        Assert.Equal(canonical.BotProbability, inheritedVerdict.BotProbability);
        Assert.Equal(canonical.RequestCount, inheritedVerdict.RequestCount);
        Assert.True(inheritedVerdict.RiskBand >= RiskBand.High,
            $"Inherited verdict should carry the canonical's high-risk band; got {inheritedVerdict.RiskBand}");
    }

    [Fact]
    public async Task TryGetVerdictAsync_DoesNotFallThroughForCanonicalSelf()
    {
        await using var coord = CreateCoordinator();

        // Register a family but never record any traffic. The canonical signature itself
        // has no atom in the sliding window, so the lookup must NOT loop back to itself.
        coord.RegisterFamily(new SignatureFamily
        {
            FamilyId = "fam-empty",
            CanonicalSignature = "sig-canon-empty",
            MemberSignatures = SignatureFamily.CreateMemberSet(new[] { "sig-canon-empty", "sig-rot-empty" }),
            CreatedUtc = DateTime.UtcNow,
            LastEvaluatedUtc = DateTime.UtcNow,
            FormationReason = FamilyFormationReason.VectorSimilarity
        });

        var v1 = await coord.TryGetVerdictAsync("sig-canon-empty");
        var v2 = await coord.TryGetVerdictAsync("sig-rot-empty");

        Assert.Null(v1);
        Assert.Null(v2);
    }
}
