using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Test.Services;

public class SignatureConvergenceServiceTests
{
    #region Helpers

    private static BotDetectionOptions DefaultOptions() => new()
    {
        SignatureConvergence =
        {
            Enabled = true,
            EvaluationIntervalSeconds = 15,
            MinSignaturesForMerge = 2,
            MergeScoreThreshold = 0.6,
            SplitDivergenceThreshold = 0.4,
            MinEvaluationsBeforeSplit = 3,
            MaxFamilies = 500,
            TemporalWeight = 0.3,
            BehavioralWeight = 0.3,
            BotProbabilityWeight = 0.4,
            TemporalProximityWindowSeconds = 300
        }
    };

    private static SignatureCoordinator CreateCoordinator(BotDetectionOptions? opts = null)
    {
        opts ??= DefaultOptions();
        return new SignatureCoordinator(
            NullLogger<SignatureCoordinator>.Instance,
            Options.Create(opts));
    }

    private static SignatureConvergenceService CreateService(
        SignatureCoordinator? coordinator = null,
        ISessionVectorSearch? vectorSearch = null)
    {
        var opts = DefaultOptions();
        coordinator ??= CreateCoordinator(opts);
        return new SignatureConvergenceService(
            NullLogger<SignatureConvergenceService>.Instance,
            Options.Create(opts),
            coordinator,
            loadSensor: null,
            vectorSearch: vectorSearch);
    }

    /// <summary>Build a normalised float vector of given dimension with a seed value.</summary>
    private static float[] MakeVector(int dims, float seed)
    {
        var v = new float[dims];
        for (var i = 0; i < dims; i++)
            v[i] = seed + i * 0.001f;
        // Normalise so cosine similarity == dot product
        var mag = (float)Math.Sqrt(v.Sum(x => x * x));
        for (var i = 0; i < dims; i++)
            v[i] /= mag;
        return v;
    }

    private static Mock<ISessionVectorSearch> MakeSearchMock(
        IReadOnlyList<(float[] Vector, SessionVectorMetadata Metadata)> snapshot,
        Func<float[], IReadOnlyList<SessionVectorMatch>> findSimilar)
    {
        var mock = new Mock<ISessionVectorSearch>();
        mock.Setup(s => s.GetAllVectorsSnapshot()).Returns(snapshot);
        mock.Setup(s => s.FindSimilarAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<float>()))
            .Returns<float[], int, float>((v, k, min) => Task.FromResult(findSimilar(v)));
        return mock;
    }

    private static SessionVectorMetadata Meta(string sig, bool isBot = true, double botProb = 0.9) =>
        new() { Signature = sig, IsBot = isBot, BotProbability = botProb, Timestamp = DateTimeOffset.UtcNow };

    /// <summary>
    ///     Seed the IP index by recording a request with the given ipHash via RecordRequestAsync.
    /// </summary>
    private static async Task SeedIpAsync(SignatureCoordinator coordinator, string sig, string ipHash)
    {
        await coordinator.RecordRequestAsync(
            sig, Guid.NewGuid().ToString(), "/", 0.9,
            new Dictionary<string, object>(),
            new HashSet<string> { "test" },
            ipHash: ipHash);
    }

    #endregion

    // -----------------------------------------------------------------------
    // Null vector search: no-op
    // -----------------------------------------------------------------------

    [Fact]
    public void RunEvaluation_NoVectorSearch_DoesNotThrow()
    {
        var svc = CreateService(vectorSearch: null);
        var ex = Record.Exception(() => svc.RunEvaluation());
        Assert.Null(ex);
    }

    [Fact]
    public void RunEvaluation_NoVectorSearch_NoFamiliesCreated()
    {
        var coordinator = CreateCoordinator();
        var svc = CreateService(coordinator, vectorSearch: null);
        svc.RunEvaluation();
        Assert.Empty(coordinator.GetAllFamilies());
    }

    // -----------------------------------------------------------------------
    // Empty / tiny snapshot: no-op
    // -----------------------------------------------------------------------

    [Fact]
    public void RunEvaluation_EmptySnapshot_NoFamiliesCreated()
    {
        var coordinator = CreateCoordinator();
        var mock = MakeSearchMock([], _ => Array.Empty<SessionVectorMatch>());
        var svc = CreateService(coordinator, mock.Object);
        svc.RunEvaluation();
        Assert.Empty(coordinator.GetAllFamilies());
    }

    [Fact]
    public void RunEvaluation_SingleVectorSnapshot_NoFamiliesCreated()
    {
        var coordinator = CreateCoordinator();
        var vec = MakeVector(129, 0.5f);
        var snapshot = new List<(float[], SessionVectorMetadata)> { (vec, Meta("sig-a")) };
        // FindSimilarAsync always returns the query signature itself -- filtered by the service
        var mock = MakeSearchMock(snapshot, _ => [new SessionVectorMatch("sig-a", 1.0f)]);
        var svc = CreateService(coordinator, mock.Object);
        svc.RunEvaluation();
        Assert.Empty(coordinator.GetAllFamilies());
    }

    // -----------------------------------------------------------------------
    // New family creation via vector similarity
    // -----------------------------------------------------------------------

    [Fact]
    public void RunEvaluation_TwoHighSimilaritySignatures_CreateFamily()
    {
        var coordinator = CreateCoordinator();
        var vecA = MakeVector(129, 0.1f);
        var vecB = MakeVector(129, 0.101f);

        var snapshot = new List<(float[], SessionVectorMetadata)>
        {
            (vecA, Meta("sig-a")),
            (vecB, Meta("sig-b"))
        };

        // sig-a's search returns sig-b at 0.80 similarity; sig-b returns sig-a at same
        var mock = MakeSearchMock(snapshot, v =>
        {
            // Identify which vector was queried by its first element
            if (Math.Abs(v[0] - vecA[0]) < 1e-6f)
                return [new SessionVectorMatch("sig-b", 0.80f)];
            return [new SessionVectorMatch("sig-a", 0.80f)];
        });

        var svc = CreateService(coordinator, mock.Object);
        svc.RunEvaluation();

        var families = coordinator.GetAllFamilies();
        Assert.Single(families);
        var family = families[0];
        Assert.Equal(FamilyFormationReason.VectorSimilarity, family.FormationReason);
        Assert.Contains("sig-a", family.MemberSignatures.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("sig-b", family.MemberSignatures.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(0.80f, family.MergeConfidence, precision: 3);
    }

    [Fact]
    public void RunEvaluation_SearchCalledWithMinSimilarity075()
    {
        // Two vectors needed to pass the snapshot.Count < 2 guard.
        var coordinator = CreateCoordinator();
        var vecA = MakeVector(129, 0.1f);
        var vecB = MakeVector(129, 0.5f);
        var snapshot = new List<(float[], SessionVectorMetadata)>
        {
            (vecA, Meta("sig-a")),
            (vecB, Meta("sig-b"))
        };

        // Both return empty -- we're testing the minSimilarity arg, not the result
        var mock = MakeSearchMock(snapshot, _ => Array.Empty<SessionVectorMatch>());

        var svc = CreateService(coordinator, mock.Object);
        svc.RunEvaluation();

        mock.Verify(s => s.FindSimilarAsync(It.IsAny<float[]>(), It.IsAny<int>(), 0.75f), Times.AtLeastOnce);
        Assert.Empty(coordinator.GetAllFamilies());
    }

    // -----------------------------------------------------------------------
    // Deduplication: same pair not processed twice
    // -----------------------------------------------------------------------

    [Fact]
    public void RunEvaluation_BothVectorsReturnEachOther_OnlyOneFamilyCreated()
    {
        var coordinator = CreateCoordinator();
        var vecA = MakeVector(129, 0.1f);
        var vecB = MakeVector(129, 0.101f);

        var snapshot = new List<(float[], SessionVectorMetadata)>
        {
            (vecA, Meta("sig-a")),
            (vecB, Meta("sig-b"))
        };

        var mock = MakeSearchMock(snapshot, v =>
            Math.Abs(v[0] - vecA[0]) < 1e-6f
                ? [(new SessionVectorMatch("sig-b", 0.85f))]
                : [(new SessionVectorMatch("sig-a", 0.85f))]);

        var svc = CreateService(coordinator, mock.Object);
        svc.RunEvaluation();

        Assert.Single(coordinator.GetAllFamilies());
    }

    // -----------------------------------------------------------------------
    // Extend existing family
    // -----------------------------------------------------------------------

    [Fact]
    public void RunEvaluation_ThirdSignatureSimilarToFamilyMember_ExtendedNotDuplicated()
    {
        var coordinator = CreateCoordinator();

        // Pre-seed a family with sig-a and sig-b
        var existingFamily = new SignatureFamily
        {
            FamilyId = "aabb",
            CanonicalSignature = "sig-a",
            MemberSignatures = SignatureFamily.CreateMemberSet(["sig-a", "sig-b"]),
            CreatedUtc = DateTime.UtcNow,
            LastEvaluatedUtc = DateTime.UtcNow,
            FormationReason = FamilyFormationReason.TemporalProximity,
            MergeConfidence = 0.7,
            EvaluationCount = 1
        };
        coordinator.RegisterFamily(existingFamily);

        var vecA = MakeVector(129, 0.1f);
        var vecC = MakeVector(129, 0.102f);

        var snapshot = new List<(float[], SessionVectorMetadata)>
        {
            (vecA, Meta("sig-a")),
            (vecC, Meta("sig-c"))
        };

        // sig-a is similar to sig-c
        var mock = MakeSearchMock(snapshot, v =>
            Math.Abs(v[0] - vecA[0]) < 1e-6f
                ? [new SessionVectorMatch("sig-c", 0.82f)]
                : [new SessionVectorMatch("sig-a", 0.82f)]);

        var svc = CreateService(coordinator, mock.Object);
        svc.RunEvaluation();

        // Should still be one family, with sig-c added to it
        var families = coordinator.GetAllFamilies();
        Assert.Single(families);
        Assert.Contains("sig-c", families[0].MemberSignatures.Keys, StringComparer.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // Skip already-co-located pairs
    // -----------------------------------------------------------------------

    [Fact]
    public void RunEvaluation_BothAlreadyInSameFamily_NoNewFamily()
    {
        var coordinator = CreateCoordinator();

        var existing = new SignatureFamily
        {
            FamilyId = "ccdd",
            CanonicalSignature = "sig-a",
            MemberSignatures = SignatureFamily.CreateMemberSet(["sig-a", "sig-b"]),
            CreatedUtc = DateTime.UtcNow,
            LastEvaluatedUtc = DateTime.UtcNow,
            FormationReason = FamilyFormationReason.BehavioralSimilarity,
            MergeConfidence = 0.8,
            EvaluationCount = 1
        };
        coordinator.RegisterFamily(existing);

        var vecA = MakeVector(129, 0.1f);
        var vecB = MakeVector(129, 0.101f);
        var snapshot = new List<(float[], SessionVectorMetadata)>
        {
            (vecA, Meta("sig-a")),
            (vecB, Meta("sig-b"))
        };

        var mock = MakeSearchMock(snapshot, v =>
            Math.Abs(v[0] - vecA[0]) < 1e-6f
                ? [new SessionVectorMatch("sig-b", 0.90f)]
                : [new SessionVectorMatch("sig-a", 0.90f)]);

        var svc = CreateService(coordinator, mock.Object);
        svc.RunEvaluation();

        // Still exactly one family - no duplicate created
        Assert.Single(coordinator.GetAllFamilies());
    }

    // -----------------------------------------------------------------------
    // Identity rotation events
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunEvaluation_DifferentIpHashes_RotationEventRecorded()
    {
        var coordinator = CreateCoordinator();

        // Put each signature under a different IP hash
        await SeedIpAsync(coordinator, "sig-a", "ip-hash-1");
        await SeedIpAsync(coordinator, "sig-b", "ip-hash-2");


        var vecA = MakeVector(129, 0.1f);
        var vecB = MakeVector(129, 0.101f);
        var snapshot = new List<(float[], SessionVectorMetadata)>
        {
            (vecA, Meta("sig-a")),
            (vecB, Meta("sig-b"))
        };

        var mock = MakeSearchMock(snapshot, v =>
            Math.Abs(v[0] - vecA[0]) < 1e-6f
                ? [new SessionVectorMatch("sig-b", 0.80f)]
                : [new SessionVectorMatch("sig-a", 0.80f)]);

        var svc = CreateService(coordinator, mock.Object);
        svc.RunEvaluation();

        var rotations = coordinator.GetRotationEvents();
        Assert.Single(rotations);
        var ev = rotations[0];
        Assert.Equal("sig-a", ev.CanonicalSignature, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("sig-b", ev.RotatedSignature, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(0.80f, ev.VectorSimilarity, precision: 3);
        Assert.NotEqual(ev.PreviousIpHash, ev.NewIpHash);
    }

    [Fact]
    public async Task RunEvaluation_SameIpHash_NoRotationEvent()
    {
        var coordinator = CreateCoordinator();

        // Both signatures under the same IP hash
        await SeedIpAsync(coordinator, "sig-a", "ip-hash-same");
        await SeedIpAsync(coordinator, "sig-b", "ip-hash-same");


        var vecA = MakeVector(129, 0.1f);
        var vecB = MakeVector(129, 0.101f);
        var snapshot = new List<(float[], SessionVectorMetadata)>
        {
            (vecA, Meta("sig-a")),
            (vecB, Meta("sig-b"))
        };

        var mock = MakeSearchMock(snapshot, v =>
            Math.Abs(v[0] - vecA[0]) < 1e-6f
                ? [new SessionVectorMatch("sig-b", 0.80f)]
                : [new SessionVectorMatch("sig-a", 0.80f)]);

        var svc = CreateService(coordinator, mock.Object);
        svc.RunEvaluation();

        // Family still created, but no rotation event
        Assert.Single(coordinator.GetAllFamilies());
        Assert.Empty(coordinator.GetRotationEvents());
    }

    [Fact]
    public async Task RunEvaluation_UnknownIpHash_NoRotationEvent()
    {
        var coordinator = CreateCoordinator();
        // Only seed sig-a's IP; sig-b has no IP in the index
        await SeedIpAsync(coordinator, "sig-a", "ip-hash-1");

        var vecA = MakeVector(129, 0.1f);
        var vecB = MakeVector(129, 0.101f);
        var snapshot = new List<(float[], SessionVectorMetadata)>
        {
            (vecA, Meta("sig-a")),
            (vecB, Meta("sig-b"))
        };

        var mock = MakeSearchMock(snapshot, v =>
            Math.Abs(v[0] - vecA[0]) < 1e-6f
                ? [new SessionVectorMatch("sig-b", 0.80f)]
                : []);

        var svc = CreateService(coordinator, mock.Object);
        svc.RunEvaluation();

        Assert.Empty(coordinator.GetRotationEvents());
    }

    // -----------------------------------------------------------------------
    // Rotation event ring buffer
    // -----------------------------------------------------------------------

    [Fact]
    public void RecordRotationEvent_ExceedsMax_OldestEvicted()
    {
        var coordinator = CreateCoordinator();
        for (var i = 0; i < 110; i++)
        {
            coordinator.RecordRotationEvent(new IdentityRotationEvent
            {
                CanonicalSignature = $"sig-{i}",
                RotatedSignature = $"sig-{i + 1000}",
                VectorSimilarity = 0.8f,
                PreviousIpHash = $"ip-{i}",
                NewIpHash = $"ip-{i + 1000}",
                DetectedUtc = DateTime.UtcNow
            });
        }

        var events = coordinator.GetRotationEvents();
        Assert.True(events.Count <= 100,
            $"Expected at most 100 rotation events, got {events.Count}");
    }

    // -----------------------------------------------------------------------
    // FamilyFormationReason.VectorSimilarity is a valid enum value
    // -----------------------------------------------------------------------

    [Fact]
    public void FamilyFormationReason_VectorSimilarity_IsDefined()
    {
        Assert.True(Enum.IsDefined(FamilyFormationReason.VectorSimilarity));
    }
}
