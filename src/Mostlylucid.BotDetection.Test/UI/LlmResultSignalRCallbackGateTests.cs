using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Pins the LAST-FALLBACK gate on the LFU LLM namer. The matcher's
///     FingerprintNameComposer is the authoritative writer for Fingerprint.DisplayName
///     -- catalog-canonical for known bots, composed "Chrome 149 / macOS"-style for
///     humans. The LLM may only WRITE A NAME when the composer didn't ("the LLM is
///     the last fallback, not a parallel writer"). Without this gate, a hot human
///     fingerprint on stylobot.net got its composer-set "Chrome 149 / macOS" name
///     clobbered with "stylobot" because the LLM inferred it from contextual signals
///     -- that's the staging bug that motivated the gate.
///     Description application is independent of the name decision -- the narrative
///     caption is purely additive and always applies.
/// </summary>
public class LlmResultSignalRCallbackGateTests : IDisposable
{
    private readonly string _tempDir;

    public LlmResultSignalRCallbackGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-llm-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task LLM_does_not_overwrite_composer_set_DisplayName()
    {
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        await store.InsertFingerprintAsync(NewFingerprint("fp-human", dim), primarySignature: "sig-human");
        await store.UpdateDisplayNameAsync(
            "fp-human", "Chrome 149 / macOS", DateTime.UtcNow, source: "matcher");

        var callback = NewCallback(store);
        await callback.OnSignatureDescriptionAsync(
            "sig-human",
            name: "stylobot",               // <-- LLM's contextual guess (must NOT land)
            description: "Browsing /dashboard on stylobot.net");

        var fp = await store.GetFingerprintAsync("fp-human");
        Assert.NotNull(fp);
        Assert.Equal("Chrome 149 / macOS", fp!.DisplayName);
    }

    [Fact]
    public async Task LLM_writes_name_when_DisplayName_is_blank()
    {
        // Composer didn't run / couldn't name the row -> the LLM IS the source.
        // Pinning the gate's positive branch so a regression that always-skips
        // would fail loud here.
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        await store.InsertFingerprintAsync(NewFingerprint("fp-blank", dim), primarySignature: "sig-blank");

        var callback = NewCallback(store);
        await callback.OnSignatureDescriptionAsync(
            "sig-blank", name: "CustomBot-9000", description: "fills the gap");

        var fp = await store.GetFingerprintAsync("fp-blank");
        Assert.NotNull(fp);
        Assert.Equal("CustomBot-9000", fp!.DisplayName);
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

    private static LlmResultSignalRCallback NewCallback(IFingerprintStore store)
    {
        // The hub is fired-and-forgotten by the callback (invalidation beacon only,
        // no payload). A no-op Moq satisfies the interface; the callback never reads
        // anything back from it. The SignatureAggregateCache.ApplyDescription side
        // effect needs a real cache instance though -- pass minimal options.
        var hub = new Mock<IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub>>();
        var clients = new Mock<IHubClients<IStyloBotDashboardHub>>();
        var allClient = new Mock<IStyloBotDashboardHub>();
        clients.Setup(c => c.All).Returns(allClient.Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(allClient.Object);
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        return new LlmResultSignalRCallback(
            NullLogger<LlmResultSignalRCallback>.Instance,
            hub.Object,
            new SignatureAggregateCache(new StyloBotDashboardOptions()),
            store);
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
