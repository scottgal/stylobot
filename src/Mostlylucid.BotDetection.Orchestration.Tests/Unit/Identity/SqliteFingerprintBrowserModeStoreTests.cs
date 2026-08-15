using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Identity.BrowserModes;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Drives <see cref="SqliteFingerprintBrowserModeStore"/> against a temp
///     fingerprints.db. The point of these tests is the contract for
///     composite-spec step 2 — schema lands idempotently, the seed migration
///     mirrors every existing fingerprint into one synthetic <c>unknown</c>
///     mode row, upserts round-trip, and the LFU cache slot is invalidated
///     by writes so subsequent reads see the new state.
/// </summary>
public sealed class SqliteFingerprintBrowserModeStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteFingerprintStore _parent;
    private readonly SqliteFingerprintBrowserModeStore _modeStore;
    private readonly IOptions<BotDetectionOptions> _options;

    public SqliteFingerprintBrowserModeStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-mode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions
            {
                Enabled = true,
                Engine = new IdentityEngineOptions { PreferSqliteVec = false }
            }
        });
        var layout = IdentityVectorLayout.DefaultV1();
        _parent = new SqliteFingerprintStore(NullLogger<SqliteFingerprintStore>.Instance, _options, layout);
        _modeStore = new SqliteFingerprintBrowserModeStore(
            _parent, _options, NullLogger<SqliteFingerprintBrowserModeStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task GetModesAsync_UnknownFingerprint_ReturnsEmpty()
    {
        await _parent.EnsureInitialisedAsync();
        var rows = await _modeStore.GetModesAsync("does-not-exist");
        Assert.Empty(rows);
    }

    [Fact]
    public async Task SchemaInit_SeedsUnknownModeForEveryExistingFingerprint()
    {
        await _parent.EnsureInitialisedAsync();

        var dim = _parent.Layout.Dimension;
        await _parent.InsertFingerprintAsync(
            IdentityTestHelpers.MakeFingerprint("fp-pre", IdentityTestHelpers.MakeUnitVector(dim, seed: 7)), "sig-pre");

        // Re-run the seed (idempotent) so any seeding semantics are exercised
        // on a row that already exists in the parent table.
        await SeedAgainAsync();

        var rows = await _modeStore.GetModesAsync("fp-pre");
        Assert.Single(rows);
        Assert.Equal("unknown", rows[0].ModeId);
        Assert.Equal(_parent.Layout.Dimension, rows[0].Centroid.Length);
    }

    [Fact]
    public async Task SchemaInit_RerunningSeed_IsIdempotent()
    {
        await _parent.EnsureInitialisedAsync();
        var dim = _parent.Layout.Dimension;
        await _parent.InsertFingerprintAsync(
            IdentityTestHelpers.MakeFingerprint("fp-1", IdentityTestHelpers.MakeUnitVector(dim, seed: 1)), "sig-1");

        await SeedAgainAsync();
        await SeedAgainAsync();
        await SeedAgainAsync();

        var rows = await _modeStore.GetModesAsync("fp-1");
        Assert.Single(rows);
    }

    [Fact]
    public async Task DeleteModeAsync_RemovesRowAndInvalidatesCache()
    {
        await _parent.EnsureInitialisedAsync();
        var dim = _parent.Layout.Dimension;
        await _parent.InsertFingerprintAsync(
            IdentityTestHelpers.MakeFingerprint("fp-del", IdentityTestHelpers.MakeUnitVector(dim, seed: 5)), "sig-del");
        await SeedAgainAsync();

        var weights = new float[dim];
        for (var i = 0; i < dim; i++) weights[i] = 1.0f;
        await _modeStore.UpsertModeAsync(new FingerprintBrowserMode
        {
            FingerprintId = "fp-del",
            ModeId = "xhr",
            Centroid = IdentityTestHelpers.MakeUnitVector(dim, seed: 13),
            CentroidMaturity = 1,
            Weights = weights,
            ObservationCount = 1,
            FirstSeen = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow,
        });

        var beforeDelete = await _modeStore.GetModesAsync("fp-del");
        Assert.Contains(beforeDelete, m => m.ModeId == "xhr");

        await _modeStore.DeleteModeAsync("fp-del", "xhr");

        var afterDelete = await _modeStore.GetModesAsync("fp-del");
        Assert.DoesNotContain(afterDelete, m => m.ModeId == "xhr");
        // unknown mode remains from the seed.
        Assert.Contains(afterDelete, m => m.ModeId == "unknown");
    }

    [Fact]
    public async Task ModeObservation_is_memory_only_no_rows_no_scope_persistence()
    {
        // Phase B (write-path grain redesign): the mode observation feed is MEMORY-ONLY —
        // no durable row, ever; the scope (domain/host) had the row as its home and
        // retires with it. Mode resolution continues in the matcher; mode transitions
        // become fold-time mutations.
        await _parent.EnsureInitialisedAsync();
        var dim = _parent.Layout.Dimension;
        await _parent.InsertFingerprintAsync(
            IdentityTestHelpers.MakeFingerprint("fp-mode-scope", IdentityTestHelpers.MakeUnitVector(dim, seed: 42)),
            "sig-mode-scope");

        var scope = new RequestScope("acme.com", "www.acme.com");
        await _modeStore.RecordModeObservationAsync(
            scope, "fp-mode-scope", "navigation", new float[dim], ct: CancellationToken.None);

        var rows = await _modeStore.ListUnabsorbedModeObservationsAsync(
            maxRows: 100, CancellationToken.None);
        Assert.DoesNotContain(rows, r => r.FingerprintId == "fp-mode-scope" && r.ModeId == "navigation");
    }

    private async Task SeedAgainAsync()
    {
        await _parent.EnsureInitialisedAsync();
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(_tempDir, "fingerprints.db")}");
        await conn.OpenAsync();
        await IdentitySchema.SeedFingerprintModesAsync(conn);
    }
}