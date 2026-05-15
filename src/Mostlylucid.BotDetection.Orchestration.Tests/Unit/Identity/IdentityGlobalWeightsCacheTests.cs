using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

public sealed class IdentityGlobalWeightsCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteFingerprintStore _store;
    private readonly IdentityGlobalWeightsCache _cache;

    public IdentityGlobalWeightsCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-globalw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions { Enabled = true }
        });
        var layout = IdentityVectorLayout.DefaultV1();
        _store = new SqliteFingerprintStore(NullLogger<SqliteFingerprintStore>.Instance, options, layout);
        _cache = new IdentityGlobalWeightsCache(
            NullLogger<IdentityGlobalWeightsCache>.Instance, _store, options);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task RefreshAsync_NoCalibrationRow_ReturnsFalse()
    {
        await _store.EnsureInitialisedAsync();
        var loaded = await _cache.RefreshAsync(CancellationToken.None);
        Assert.False(loaded);
        Assert.Null(_cache.Current);
    }

    [Fact]
    public async Task RefreshAsync_WithStoredWeights_LoadsAndPersists()
    {
        var stored = new float[] { 0.5f, 1.0f, 1.5f };
        await _store.UpsertGlobalWeightsAsync(stored, samplesUsed: 10, clustersUsed: 2, archetypesUsed: 5);

        var loaded = await _cache.RefreshAsync(CancellationToken.None);

        Assert.True(loaded);
        Assert.NotNull(_cache.Current);
        Assert.Equal(stored, _cache.Current);
    }

    [Fact]
    public async Task Compose_WithoutGlobalWeights_ReturnsPerFpUnchanged()
    {
        await _store.EnsureInitialisedAsync();
        var perFp = new float[] { 1.0f, 1.0f, 1.0f };

        var composed = _cache.Compose(perFp);

        Assert.Same(perFp, composed);
    }

    [Fact]
    public async Task Compose_WithGlobalWeights_MultipliesPointwise()
    {
        await _store.UpsertGlobalWeightsAsync(new[] { 2.0f, 0.5f, 4.0f }, 10, 2, 5);
        await _cache.RefreshAsync(CancellationToken.None);

        var composed = _cache.Compose(new[] { 1.0f, 2.0f, 0.5f });

        Assert.Equal(new[] { 2.0f, 1.0f, 2.0f }, composed);
    }

    [Fact]
    public async Task Compose_ShapeMismatch_FallsBackToPerFp()
    {
        await _store.UpsertGlobalWeightsAsync(new[] { 1.0f, 1.0f }, 10, 2, 5);
        await _cache.RefreshAsync(CancellationToken.None);

        var perFp = new float[] { 1.0f, 1.0f, 1.0f }; // length 3 vs cached length 2
        var composed = _cache.Compose(perFp);

        // Shape mismatch is treated as missing global weights — return per-fp unchanged so
        // a stale layout migration in flight doesn't poison live matching.
        Assert.Same(perFp, composed);
    }
}
