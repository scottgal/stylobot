using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Pins the hot-path identity-verdict write-behind contract
///     (<see cref="SqliteFingerprintStore.RecordVerdictWriteBehind"/>), the fix for the
///     "confirmed bot classified as human" report on stylo.bot.
///
///     Root cause: the dashboard signature header reads <c>fp.CachedBotProbability</c> as the
///     SINGLE source of truth, but that field was only refreshed at a 30-min session-persistence
///     boundary (SessionAtom shift -> RecordVerdictAsync). A burst-bot (e.g. Bytespider firing a
///     handful of requests then leaving) never forms a session, so its cached score stayed at the
///     allocation-time 0.0 and the header rendered Human/VeryLow while every per-request verdict
///     was 100%/VeryHigh.
///
///     The fix records every request's verdict into the resident fingerprint at detection-end
///     (BotDetectionOrchestrator), write-behind, with NO per-request DB connection. These tests
///     pin: (1) a resident fingerprint's cached score converges to the live per-request score
///     without any session boundary; (2) repeat writes EWMA-blend rather than overwrite; (3) the
///     method is dict-only -- a fingerprint the matcher never resident-loaded this request is
///     skipped, never cold-loaded (the hot-path "no per-request DB connection" contract).
/// </summary>
public class IdentityVerdictWriteBehindHotPathTests : IDisposable
{
    private readonly string _tempDir;

    public IdentityVerdictWriteBehindHotPathTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-verdict-writebehind-{Guid.NewGuid():N}");
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
        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance,
            options,
            IdentityVectorLayout.DefaultV1());
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
            InferredTypeChangedAt = now
        };
    }

    [Fact]
    public async Task RecordVerdictWriteBehind_ResidentFingerprint_ConvergesToLiveScore_WithoutSessionBoundary()
    {
        var store = await NewStoreAsync();
        const string primarySig = "burst-bot-sig";
        const string fpId = "burst-bot-fp";
        var dim = store.Layout.Dimension;

        // Allocation: a freshly-matched fingerprint starts with cached_bot_probability = 0.0.
        await store.InsertFingerprintAsync(NewFingerprint(fpId, dim), primarySig, CancellationToken.None);

        // Mirror the matcher: it resident-loads the fingerprint into the dict during the request
        // (InsertFingerprintAsync deliberately does not), so at detection-end the row is resident
        // for the write-behind.
        var allocated = await store.GetFingerprintAsync(fpId);
        allocated.Should().NotBeNull();
        allocated!.CachedBotProbability.Should().Be(0.0,
            "a burst-bot that never formed a 30-min session kept the allocation-time score");

        // Detection-end hot-path write -- synchronous, no session, no RecordVerdictAsync.
        store.RecordVerdictWriteBehind(fpId, 0.97);

        var after = await store.GetFingerprintAsync(fpId);
        after!.CachedBotProbability.Should().BeApproximately(0.97, 1e-6,
            "first-ever write is a direct assignment, so the header's single source " +
            "(fp.CachedBotProbability) converges to the live 97% score without any session boundary");
    }

    [Fact]
    public async Task RecordVerdictWriteBehind_TwiceResident_ExposesEwmaBlend()
    {
        var store = await NewStoreAsync();
        const string primarySig = "blend-sig";
        const string fpId = "blend-fp";
        var dim = store.Layout.Dimension;
        await store.InsertFingerprintAsync(NewFingerprint(fpId, dim), primarySig, CancellationToken.None);
        await store.GetFingerprintAsync(fpId); // resident-load (mirrors the matcher)

        store.RecordVerdictWriteBehind(fpId, 0.10);
        store.RecordVerdictWriteBehind(fpId, 0.90);

        var after = await store.GetFingerprintAsync(fpId);
        after!.CachedBotProbability.Should().BeInRange(0.10, 0.90,
            "EWMA blend must land between the two writes, not overwrite to the second");
        after.CachedBotProbability.Should().NotBe(0.90,
            "direct overwrite would write 0.90 verbatim; EWMA must dampen the swing");
    }

    [Fact]
    public async Task RecordVerdictWriteBehind_NonResidentFingerprint_IsSkipped_NeverColdLoads()
    {
        var store = await NewStoreAsync();
        const string primarySig = "not-resident-sig";
        const string fpId = "not-resident-fp";
        var dim = store.Layout.Dimension;

        // Insert but do NOT resident-load: InsertFingerprintAsync leaves _fingerprintById empty.
        await store.InsertFingerprintAsync(NewFingerprint(fpId, dim), primarySig, CancellationToken.None);

        // Hot-path contract: dict-only. A fingerprint the matcher never resident-loaded this
        // request must be skipped, NOT cold-loaded from SQLite on the caller thread (the
        // "no per-request DB connection" rule). No throw, no write.
        store.RecordVerdictWriteBehind(fpId, 0.99);

        // The first read after the skipped write cold-loads from disk and still shows 0.0,
        // proving the write-behind did not silently open a connection to blend/persist.
        var loaded = await store.GetFingerprintAsync(fpId);
        loaded!.CachedBotProbability.Should().Be(0.0,
            "write-behind is dict-only; a non-resident fingerprint is skipped, never cold-loaded");
    }
}
