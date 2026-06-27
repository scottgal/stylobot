using System;
using System.IO;
using System.Threading;
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
///     Pins the post-split contract: <see cref="LlmResultSignalRCallback"/>
///     is DESCRIPTION-ONLY. The LLM never writes a name through the SignalR
///     callback. (The slot-aware <c>UpdateLlmNameAsync</c> path is the LLM
///     coordinator's writeback channel; this callback is purely for
///     broadcasting descriptions to dashboard listeners.) The matcher
///     (FingerprintNameComposer) owns the InducedName slot; the resolver
///     picks <c>given ?? llm ?? induced</c> at read time. A regression that
///     re-introduces a name-write here would resurrect the parasite that let
///     the LLM's contextual inference clobber the composer's authoritative
///     name (the staging "stylobot" bug).
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
    public async Task LLM_callback_never_writes_to_induced_slot_when_blank()
    {
        // The SignalR callback is not a name writer at all. Even on a fingerprint
        // whose InducedName is empty (composer hadn't run yet), the callback must
        // leave the store row untouched. Composer's later write is the ONLY induced
        // source.
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        await store.InsertFingerprintAsync(NewFingerprint("fp-blank", dim), primarySignature: "sig-blank");

        var callback = NewCallback();
        await callback.OnSignatureDescriptionAsync(
            "sig-blank", name: "CustomBot-9000", description: "what the LLM inferred");

        var fp = await store.GetFingerprintAsync("fp-blank");
        Assert.NotNull(fp);
        Assert.True(string.IsNullOrEmpty(fp!.InducedName),
            $"Expected blank InducedName (callback is not a writer); got '{fp.InducedName}'");
        Assert.True(string.IsNullOrEmpty(fp.LlmName),
            $"Expected blank LlmName (callback writes description only); got '{fp.LlmName}'");
    }

    [Fact]
    public async Task LLM_callback_does_not_overwrite_matcher_set_induced_name()
    {
        // Belt-and-braces: also pin the original parasite path. With a
        // matcher-set induced name already on the row, the LLM callback must
        // leave it alone (description is the only side effect).
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        await store.InsertFingerprintAsync(NewFingerprint("fp-human", dim), primarySignature: "sig-human");
        await store.GetFingerprintAsync("fp-human"); // warm LFU dict
        await store.UpdateInducedNameAsync(
            "fp-human", "Chrome 149", DateTime.UtcNow, CancellationToken.None);

        var callback = NewCallback();
        await callback.OnSignatureDescriptionAsync(
            "sig-human",
            name: "stylobot",
            description: "Browsing /dashboard on stylobot.net");

        var fp = await store.GetFingerprintAsync("fp-human");
        Assert.NotNull(fp);
        Assert.Equal("Chrome 149", fp!.InducedName);
        // Resolver picks given ?? llm ?? induced -- with no Given/Llm slot
        // written, the matcher's induced wins.
        Assert.Equal("Chrome 149", FingerprintNameResolver.Resolve(fp));
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

    private static LlmResultSignalRCallback NewCallback()
    {
        var hub = new Mock<IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub>>();
        var clients = new Mock<IHubClients<IStyloBotDashboardHub>>();
        var allClient = new Mock<IStyloBotDashboardHub>();
        clients.Setup(c => c.All).Returns(allClient.Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(allClient.Object);
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        return new LlmResultSignalRCallback(
            NullLogger<LlmResultSignalRCallback>.Instance,
            hub.Object,
            new SignatureAggregateCache(new StyloBotDashboardOptions()));
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
