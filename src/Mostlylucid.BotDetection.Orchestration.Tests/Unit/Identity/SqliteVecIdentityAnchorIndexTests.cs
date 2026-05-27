using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     The vec0 native binary isn't installed in CI, so this rig pins the contract that
///     <see cref="SqliteVecIdentityAnchorIndex"/> falls through to the brute-force engine
///     when sqlite-vec didn't load and returns byte-equivalent results. The actual vec0
///     KNN path is exercised in environments where the extension is installed
///     (operator-side; track via the store's IsVecAvailable flag in logs).
/// </summary>
public sealed class SqliteVecIdentityAnchorIndexTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteFingerprintStore _store;
    private readonly BruteForceIdentityAnchorIndex _brute;
    private readonly SqliteVecIdentityAnchorIndex _vec;

    public SqliteVecIdentityAnchorIndexTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-vec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            // Explicit PreferSqliteVec=false guarantees the brute-force fallback path runs
            // regardless of whether vec0 happens to be installed in the dev env.
            Identity = new IdentityOptions
            {
                Enabled = true,
                Engine = new IdentityEngineOptions { PreferSqliteVec = false }
            }
        });
        var layout = IdentityVectorLayout.DefaultV1();
        _store = new SqliteFingerprintStore(NullLogger<SqliteFingerprintStore>.Instance, options, layout);
        _brute = new BruteForceIdentityAnchorIndex(_store);
        _vec = new SqliteVecIdentityAnchorIndex(
            NullLogger<SqliteVecIdentityAnchorIndex>.Instance, _store, _brute);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task SearchAsync_VecExtensionUnavailable_FallsThroughToBruteForce()
    {
        await _store.EnsureInitialisedAsync();
        // PreferSqliteVec is false in the test options, so the store never attempts to
        // load vec0 — IsVecAvailable is deterministically false regardless of whether
        // the extension happens to live on the dev box's library path.
        Assert.False(_store.IsVecAvailable);

        var dim = _store.Layout.Dimension;
        var v1 = IdentityTestHelpers.MakeUnitVector(dim, seed: 1);
        var v2 = IdentityTestHelpers.MakeUnitVector(dim, seed: 2);
        await _store.InsertFingerprintAsync(IdentityTestHelpers.MakeFingerprint("fp-1", v1), "sig-1");
        await _store.InsertFingerprintAsync(IdentityTestHelpers.MakeFingerprint("fp-2", v2), "sig-2");

        var query = IdentityTestHelpers.MakeUnitVector(dim, seed: 1); // identical to fp-1
        var bruteResult = await _brute.SearchAsync(query, topK: 5, CancellationToken.None);
        var vecResult = await _vec.SearchAsync(query, topK: 5, CancellationToken.None);

        Assert.Equal(bruteResult.Count, vecResult.Count);
        for (var i = 0; i < bruteResult.Count; i++)
        {
            Assert.Equal(bruteResult[i].FingerprintId, vecResult[i].FingerprintId);
            Assert.Equal(bruteResult[i].CentroidScore, vecResult[i].CentroidScore, precision: 6);
        }
    }

    [Fact]
    public async Task SearchAsync_EmptyStore_ReturnsEmpty()
    {
        await _store.EnsureInitialisedAsync();
        var dim = _store.Layout.Dimension;
        var query = IdentityTestHelpers.MakeUnitVector(dim, seed: 1);
        var result = await _vec.SearchAsync(query, topK: 5, CancellationToken.None);
        Assert.Empty(result);
    }

}
