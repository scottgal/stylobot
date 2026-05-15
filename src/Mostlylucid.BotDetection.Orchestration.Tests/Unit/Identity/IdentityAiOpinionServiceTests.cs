using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Drives <see cref="IdentityAiOpinionService"/> against a real SQLite store and a
///     stub-registered ILlmProvider implementation. The service resolves the provider via
///     reflection on the assembly-qualified name, so the stub must live in the
///     Mostlylucid.BotDetection.Llm assembly — which is too heavy to spin up here. We
///     instead pin the surface this slice owns: identity-disabled, fingerprint-not-found,
///     and no-llm-provider all return well-defined statuses without throwing. The happy
///     path is exercised end-to-end in the running Demo and surfaced via the
///     X-StyloBot-AiOpinion-Status header.
/// </summary>
public sealed class IdentityAiOpinionServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteFingerprintStore _store;

    public IdentityAiOpinionServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-ai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions { Enabled = true }
        });
        var layout = IdentityVectorLayout.DefaultV1();
        _store = new SqliteFingerprintStore(NullLogger<SqliteFingerprintStore>.Instance, options, layout);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task RunAsync_IdentityDisabled_ReturnsIdentityDisabledStatus()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions { Enabled = false }
        });
        var sp = new ServiceCollection().BuildServiceProvider();
        var service = new IdentityAiOpinionService(
            NullLogger<IdentityAiOpinionService>.Instance, _store, sp, options);

        var result = await service.RunAsync("any-fp", CancellationToken.None);

        Assert.Equal(IdentityAiOpinionStatus.IdentityDisabled, result.Status);
        Assert.Null(result.BotProbability);
    }

    [Fact]
    public async Task RunAsync_FingerprintNotFound_ReturnsNotFoundStatus()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions { Enabled = true }
        });
        var sp = new ServiceCollection().BuildServiceProvider();
        var service = new IdentityAiOpinionService(
            NullLogger<IdentityAiOpinionService>.Instance, _store, sp, options);

        var result = await service.RunAsync("nonexistent-fp", CancellationToken.None);

        Assert.Equal(IdentityAiOpinionStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task RunAsync_NoLlmProvider_ReturnsNoProviderStatusAndDoesNotChangeFingerprint()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions { Enabled = true }
        });
        await _store.EnsureInitialisedAsync();
        var fp = MakeFingerprint("test-fp", _store.Layout.Dimension);
        await _store.InsertFingerprintAsync(fp, "sig-test");

        // Empty service provider — no ILlmProvider registered.
        var sp = new ServiceCollection().BuildServiceProvider();
        var service = new IdentityAiOpinionService(
            NullLogger<IdentityAiOpinionService>.Instance, _store, sp, options);

        var result = await service.RunAsync("test-fp", CancellationToken.None);

        Assert.Equal(IdentityAiOpinionStatus.NoLlmProvider, result.Status);
        Assert.NotNull(result.ErrorDetail);

        // Cached score must remain untouched when no provider can answer.
        var unchanged = await _store.GetFingerprintAsync("test-fp");
        Assert.NotNull(unchanged);
        Assert.Null(unchanged!.CachedScoreUpdatedAt);
        Assert.Equal(0.0, unchanged.CachedBotProbability);
    }

    private static Fingerprint MakeFingerprint(string id, int dim)
    {
        var vec = new float[dim];
        vec[0] = 1.0f;
        var weights = new float[dim];
        Array.Fill(weights, 1.0f);
        var now = DateTime.UtcNow;
        return new Fingerprint
        {
            FingerprintId = id,
            Centroid = vec,
            CentroidMaturity = 1,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 5,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = 0.8,
            ArchetypeOrigin = null,
            InferredClientType = "test-client",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now,
            CachedBotProbability = 0.0,
            CachedRiskBand = null,
            CachedScoreUpdatedAt = null
        };
    }
}
