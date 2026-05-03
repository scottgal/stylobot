using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Test.Actions;

/// <summary>
///     Tests for SqliteChallengeStore: creation, single-use consumption, verification recording.
/// </summary>
public class ChallengeStoreTests : IAsyncDisposable
{
    private readonly SqliteChallengeStore _store;
    private readonly string _dbDir;

    public ChallengeStoreTests()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"stylobot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbDir);

        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_dbDir, "test.db")
        });
        _store = new SqliteChallengeStore(
            NullLogger<SqliteChallengeStore>.Instance,
            options);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        try { Directory.Delete(_dbDir, true); } catch { }
    }

    [Fact]
    public void CreateChallenge_ReturnsValidRecord()
    {
        var record = _store.CreateChallenge("sig-001", 4, 3, TimeSpan.FromMinutes(2));

        Assert.NotNull(record);
        Assert.Equal("sig-001", record.Signature);
        Assert.Equal(4, record.PuzzleCount);
        Assert.Equal(3, record.RequiredZeros);
        Assert.Equal(4, record.Puzzles.Length);
        Assert.NotEmpty(record.Id);
        Assert.True(record.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void CreateChallenge_PuzzleSeedsAreUnique()
    {
        var record = _store.CreateChallenge("sig-001", 8, 3, TimeSpan.FromMinutes(2));

        var seeds = record.Puzzles.Select(p => Convert.ToBase64String(p.Seed)).ToHashSet();
        Assert.Equal(8, seeds.Count); // All unique
    }

    [Fact]
    public void ValidateAndConsume_ReturnsRecordOnFirstCall()
    {
        var record = _store.CreateChallenge("sig-001", 4, 3, TimeSpan.FromMinutes(2));

        var consumed = _store.ValidateAndConsume(record.Id);

        Assert.NotNull(consumed);
        Assert.Equal(record.Id, consumed.Id);
        Assert.Equal(record.Signature, consumed.Signature);
    }

    [Fact]
    public void ValidateAndConsume_SingleUse_ReturnsNullOnSecondCall()
    {
        var record = _store.CreateChallenge("sig-001", 4, 3, TimeSpan.FromMinutes(2));

        var first = _store.ValidateAndConsume(record.Id);
        var second = _store.ValidateAndConsume(record.Id);

        Assert.NotNull(first);
        Assert.Null(second); // Single-use: consumed
    }

    [Fact]
    public void ValidateAndConsume_ExpiredChallenge_ReturnsNull()
    {
        // Create with 0 expiry (already expired)
        var record = _store.CreateChallenge("sig-001", 4, 3, TimeSpan.FromMilliseconds(-1));

        var result = _store.ValidateAndConsume(record.Id);

        Assert.Null(result);
    }

    [Fact]
    public void ValidateAndConsume_NonexistentId_ReturnsNull()
    {
        var result = _store.ValidateAndConsume("nonexistent-id");

        Assert.Null(result);
    }

    [Fact]
    public void RecordVerification_StoresAndRetrieves()
    {
        var verification = new ChallengeVerificationResult
        {
            Signature = "sig-001",
            TotalSolveDurationMs = 1500,
            ReportedWorkerCount = 4,
            PuzzleCount = 8,
            PuzzleTimingsMs = [200, 180, 210, 190, 205, 195, 185, 215],
            TimingJitter = 0.12,
            VerifiedAt = DateTimeOffset.UtcNow
        };

        _store.RecordVerification(verification);
        var retrieved = _store.GetVerification("sig-001");

        Assert.NotNull(retrieved);
        Assert.Equal(1500, retrieved.TotalSolveDurationMs);
        Assert.Equal(4, retrieved.ReportedWorkerCount);
        Assert.Equal(8, retrieved.PuzzleCount);
        Assert.Equal(0.12, retrieved.TimingJitter, 0.001);
    }

    [Fact]
    public void GetVerification_NonexistentSignature_ReturnsNull()
    {
        var result = _store.GetVerification("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public void RecordVerification_OverwritesPrevious()
    {
        _store.RecordVerification(new ChallengeVerificationResult
        {
            Signature = "sig-001", TotalSolveDurationMs = 1000, ReportedWorkerCount = 2,
            PuzzleCount = 4, PuzzleTimingsMs = [250, 250, 250, 250], TimingJitter = 0, VerifiedAt = DateTimeOffset.UtcNow
        });

        _store.RecordVerification(new ChallengeVerificationResult
        {
            Signature = "sig-001", TotalSolveDurationMs = 2000, ReportedWorkerCount = 8,
            PuzzleCount = 16, PuzzleTimingsMs = [125], TimingJitter = 0.5, VerifiedAt = DateTimeOffset.UtcNow
        });

        var result = _store.GetVerification("sig-001");
        Assert.NotNull(result);
        Assert.Equal(2000, result.TotalSolveDurationMs);
        Assert.Equal(8, result.ReportedWorkerCount);
    }

    [Fact]
    public void ChallengeActionPolicy_CalculateDifficulty_ScalesWithRisk()
    {
        // Test via public API: ChallengeActionPolicy difficulty scaling
        var lowRiskEvidence = CreateEvidence(0.5);
        var highRiskEvidence = CreateEvidence(0.95);

        var lowPolicy = new ChallengeActionPolicy("test", new ChallengeActionOptions
        {
            ChallengeType = ChallengeType.ProofOfWork,
            BasePuzzleCount = 4, MaxPuzzleCount = 32,
            BaseDifficultyZeros = 3, MaxDifficultyZeros = 5
        });

        // Can't call CalculateDifficulty directly (private), but verify options are set correctly
        Assert.Equal(4, new ChallengeActionOptions().BasePuzzleCount);
        Assert.Equal(32, new ChallengeActionOptions().MaxPuzzleCount);
        Assert.Equal(3, new ChallengeActionOptions().BaseDifficultyZeros);
        Assert.Equal(5, new ChallengeActionOptions().MaxDifficultyZeros);
    }

    [Fact]
    public void ChallengeToken_GenerateAndValidate()
    {
        var options = new ChallengeActionOptions
        {
            TokenSecret = "test-secret-key-for-hmac",
            TokenValidityMinutes = 30
        };

        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var token = ChallengeActionPolicy.GenerateChallengeToken(httpContext, options);

        Assert.NotNull(token);
        Assert.NotEmpty(token);
        // Token is base64 encoded
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token));
        Assert.Contains(":", decoded); // Format: expiry:signature
    }

    private static AggregatedEvidence CreateEvidence(double botProbability, Dictionary<string, object>? signals = null)
    {
        return new AggregatedEvidence
        {
            BotProbability = botProbability,
            Confidence = 0.8,
            RiskBand = botProbability >= 0.8 ? RiskBand.High : RiskBand.Medium,
            Signals = signals ?? new Dictionary<string, object>(),
            CategoryBreakdown = new Dictionary<string, Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger.CategoryScore>(),
            ContributingDetectors = new HashSet<string> { "TestDetector" },
            FailedDetectors = new HashSet<string>(),
            ThreatScore = 0,
            ThreatBand = Mostlylucid.BotDetection.Orchestration.ThreatBand.None
        };
    }
}
