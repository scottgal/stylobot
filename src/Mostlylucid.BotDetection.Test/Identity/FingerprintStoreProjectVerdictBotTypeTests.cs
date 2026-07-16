using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Regression guard for the verdict read-through's CATALOGUE bot-type projection.
///     <para>
///     The dashboard's Internal-exclusion (<c>SignatureAggregateCache.GetCounts</c>
///     matches <c>verdict.BotType == "Internal"</c>) and its ai/search/tools filters
///     need the catalogue botType vocabulary (<c>Internal</c>, <c>SearchEngine</c>,
///     <c>AiBot</c>, <c>Tool</c>, <c>GoodBot</c>, ...). Before this fix
///     <c>ProjectVerdict</c> derived <c>ResolvedVerdict.BotType</c> from
///     <c>InferredClientType</c> (bot/suspicious/human/archetype), which never produces
///     that vocabulary -- so the Internal chip counted zero and self-traffic leaked into
///     the human/bot stats. This exercises the REAL write path
///     (<see cref="SqliteFingerprintStore.RecordVerdictWriteBehind(string, double, string?)"/>)
///     and the REAL read path
///     (<see cref="SqliteFingerprintStore.GetResolvedVerdictsBySignaturesAsync"/>), unlike
///     <c>InternalStatExclusionTests</c> which injects "Internal" directly (false confidence).
///     </para>
/// </summary>
public class FingerprintStoreProjectVerdictBotTypeTests : IDisposable
{
    private readonly string _tempDir;
    private static readonly int Dim = IdentityVectorLayout.DefaultV1().Dimension;

    public FingerprintStoreProjectVerdictBotTypeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fp-projectverdict-{Guid.NewGuid():N}");
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

    private static Fingerprint NewFingerprint(string id)
    {
        var now = DateTime.UtcNow;
        var weights = new float[Dim];
        Array.Fill(weights, 1.0f);
        return new Fingerprint
        {
            FingerprintId = id,
            Centroid = new float[Dim],
            CentroidMaturity = 1,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 1,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = 0.8,
            // Identity axis is deliberately NON-catalogue so a regression that reverts
            // ProjectVerdict to InferredClientType would surface "chrome-desktop", not "Internal".
            InferredClientType = "chrome-desktop",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now
        };
    }

    [Fact]
    public async Task ProjectVerdict_emits_catalogue_BotType_written_through_the_real_write_path()
    {
        var store = await NewStoreAsync();
        const string primarySig = "sig-internal";
        const string fpId = "fp-internal";

        await store.InsertFingerprintAsync(NewFingerprint(fpId), primarySig, CancellationToken.None);
        // Make the fingerprint resident in the LFU dict (InsertFingerprintAsync does not
        // pre-populate it) so the write-behind hot path can blend + stamp the botType.
        await store.GetFingerprintAsync(fpId, CancellationToken.None);

        // REAL write path: BotType.Internal.ToString() == "Internal" is exactly the
        // catalogue vocabulary the dashboard matches on.
        store.RecordVerdictWriteBehind(fpId, botProbability: 0.02, botType: BotType.Internal.ToString());

        var verdicts = await store.GetResolvedVerdictsBySignaturesAsync(
            new[] { primarySig }, CancellationToken.None);

        Assert.True(verdicts.ContainsKey(primarySig));
        Assert.Equal("Internal", verdicts[primarySig].BotType);
    }

    [Fact]
    public async Task ProjectVerdict_emits_AiBot_catalogue_type_through_the_real_write_path()
    {
        var store = await NewStoreAsync();
        const string primarySig = "sig-ai";
        const string fpId = "fp-ai";

        await store.InsertFingerprintAsync(NewFingerprint(fpId), primarySig, CancellationToken.None);
        await store.GetFingerprintAsync(fpId, CancellationToken.None);

        store.RecordVerdictWriteBehind(fpId, botProbability: 0.95, botType: BotType.AiBot.ToString());

        var verdicts = await store.GetResolvedVerdictsBySignaturesAsync(
            new[] { primarySig }, CancellationToken.None);

        Assert.True(verdicts.ContainsKey(primarySig));
        Assert.Equal("AiBot", verdicts[primarySig].BotType);
    }

    [Fact]
    public async Task ProjectVerdict_maps_Unknown_and_missing_botType_to_null()
    {
        var store = await NewStoreAsync();
        const string primarySig = "sig-unknown";
        const string fpId = "fp-unknown";

        await store.InsertFingerprintAsync(NewFingerprint(fpId), primarySig, CancellationToken.None);
        await store.GetFingerprintAsync(fpId, CancellationToken.None);

        // No botType supplied on the write -> CachedBotType stays null -> BotType projects null,
        // so the dashboard falls through to entity-id / UA-family labels instead of a placeholder.
        store.RecordVerdictWriteBehind(fpId, botProbability: 0.30);

        var verdicts = await store.GetResolvedVerdictsBySignaturesAsync(
            new[] { primarySig }, CancellationToken.None);

        Assert.True(verdicts.ContainsKey(primarySig));
        Assert.Null(verdicts[primarySig].BotType);
    }
}
