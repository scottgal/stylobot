using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Data;

/// <summary>
///     Tests the signature merge-history forwarding table + ResolveSignatureAsync.
///     The contract that protects /signature/{id} dashboard URLs from 404-ing when a
///     featured signature (Googlebot, GPTBot, etc.) gets merged into another id.
/// </summary>
public class SignatureMergeResolveTests : IAsyncLifetime
{
    private SqliteSessionStore _store = null!;
    private string _dbDir = null!;

    public async Task InitializeAsync()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"sig-merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbDir);
        var dbFilePath = Path.Combine(_dbDir, "botdetection.db");
        var opts = Options.Create(new BotDetectionOptions { DatabasePath = dbFilePath });
        _store = new SqliteSessionStore(NullLogger<SqliteSessionStore>.Instance, opts);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dbDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Resolve_NoMergeRow_ReturnsRequestedIdUnchanged()
    {
        var result = await _store.ResolveSignatureAsync("sig-never-merged");
        Assert.Equal("sig-never-merged", result);
    }

    [Fact]
    public async Task Resolve_AfterMerge_ReturnsNewId()
    {
        await _store.RecordSignatureMergeAsync("sig-old", "sig-new", "convergence");
        var result = await _store.ResolveSignatureAsync("sig-old");
        Assert.Equal("sig-new", result);
    }

    [Fact]
    public async Task Resolve_FollowsMergeChain()
    {
        // sig-a → sig-b → sig-c. Requesting sig-a should walk to sig-c.
        await _store.RecordSignatureMergeAsync("sig-a", "sig-b", "convergence");
        await _store.RecordSignatureMergeAsync("sig-b", "sig-c", "manual");

        Assert.Equal("sig-c", await _store.ResolveSignatureAsync("sig-a"));
        Assert.Equal("sig-c", await _store.ResolveSignatureAsync("sig-b"));
        Assert.Equal("sig-c", await _store.ResolveSignatureAsync("sig-c"));
    }

    [Fact]
    public async Task Record_RejectsCycleCreatingMerge()
    {
        // A→B exists; trying to record B→A would create A→B→A.
        await _store.RecordSignatureMergeAsync("sig-a", "sig-b", "convergence");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.RecordSignatureMergeAsync("sig-b", "sig-a", "manual"));
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Record_RejectsSelfMerge()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.RecordSignatureMergeAsync("sig-x", "sig-x", "manual"));
    }

    [Fact]
    public async Task Record_RejectsEmptyIds()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.RecordSignatureMergeAsync("", "sig-x", "manual"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.RecordSignatureMergeAsync("sig-x", "", "manual"));
    }

    [Fact]
    public async Task Record_IsIdempotentAndUpdatesOnConflict()
    {
        // First merge: sig-a → sig-b.
        await _store.RecordSignatureMergeAsync("sig-a", "sig-b", "convergence");
        // Operator manually re-targets sig-a → sig-c. Should overwrite, not throw.
        await _store.RecordSignatureMergeAsync("sig-a", "sig-c", "manual");
        Assert.Equal("sig-c", await _store.ResolveSignatureAsync("sig-a"));
    }

    [Fact]
    public async Task Resolve_EmptyInput_ReturnsUnchanged()
    {
        Assert.Equal("", await _store.ResolveSignatureAsync(""));
    }
}
