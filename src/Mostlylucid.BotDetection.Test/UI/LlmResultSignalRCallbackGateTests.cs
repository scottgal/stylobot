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
///     Pins the post-surgery contract: <see cref="LlmResultSignalRCallback"/>
///     is DESCRIPTION-ONLY. The LLM never writes a name. The matcher
///     (FingerprintNameComposer) is the sole writer of Fingerprint.DisplayName;
///     the LLM's name suggestion is intentionally ignored. Description is
///     purely additive narrative, no contention with the canonical name path.
///     A regression that re-introduces a name-write would resurrect the
///     parasite that let the LLM's contextual inference clobber the composer's
///     authoritative name (the staging "stylobot" bug).
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
    public async Task LLM_never_writes_a_name_even_when_existing_DisplayName_is_blank()
    {
        // The LLM is no longer a writer of names at all. Even on a fingerprint
        // whose DisplayName is empty (composer's "No User-Agent" terminal case
        // wasn't reached because the matcher hadn't run yet), the LLM callback
        // must leave the store row untouched. Composer's later write is the
        // ONLY name source.
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        await store.InsertFingerprintAsync(NewFingerprint("fp-blank", dim), primarySignature: "sig-blank");

        var callback = NewCallback();
        await callback.OnSignatureDescriptionAsync(
            "sig-blank", name: "CustomBot-9000", description: "what the LLM inferred");

        var fp = await store.GetFingerprintAsync("fp-blank");
        Assert.NotNull(fp);
        Assert.True(string.IsNullOrEmpty(fp!.DisplayName),
            $"Expected blank DisplayName (LLM is no longer a writer); got '{fp.DisplayName}'");
    }

    [Fact]
    public async Task LLM_does_not_overwrite_composer_set_DisplayName()
    {
        // Belt-and-braces: also pin the original parasite path. With a
        // composer-set name already on the row, the LLM callback must leave
        // it alone (description is the only side effect).
        //
        // Seed name is an allowed shape per FingerprintNameComposerContract --
        // T24a now gates UpdateDisplayNameAsync at the store boundary, so a
        // seed containing '/' or parentheses would be normalised to Unknown
        // <hex> and the assertion would be about the gate, not the LLM
        // no-overwrite contract this test exists to pin.
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        await store.InsertFingerprintAsync(NewFingerprint("fp-human", dim), primarySignature: "sig-human");
        await store.UpdateDisplayNameAsync(
            "fp-human", "Chrome 149", DateTime.UtcNow, source: "matcher");

        var callback = NewCallback();
        await callback.OnSignatureDescriptionAsync(
            "sig-human",
            name: "stylobot",
            description: "Browsing /dashboard on stylobot.net");

        var fp = await store.GetFingerprintAsync("fp-human");
        Assert.NotNull(fp);
        Assert.Equal("Chrome 149", fp!.DisplayName);
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
