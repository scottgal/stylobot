using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    public async Task UpsertModeAsync_RoundTrips()
    {
        await _parent.EnsureInitialisedAsync();
        var dim = _parent.Layout.Dimension;
        await _parent.InsertFingerprintAsync(
            IdentityTestHelpers.MakeFingerprint("fp-rt", IdentityTestHelpers.MakeUnitVector(dim, seed: 3)), "sig-rt");
        await SeedAgainAsync();

        var centroid = IdentityTestHelpers.MakeUnitVector(dim, seed: 9);
        var weights = new float[dim];
        for (var i = 0; i < dim; i++) weights[i] = 1.0f;

        var now = DateTime.UtcNow;
        var mode = new FingerprintBrowserMode
        {
            FingerprintId = "fp-rt",
            ModeId = "xhr",
            Centroid = centroid,
            CentroidMaturity = 12,
            Weights = weights,
            ObservationCount = 28,
            FirstSeen = now.AddMinutes(-5),
            LastSeen = now,
            InferredArchetype = "chrome-xhr",
            InferredConfidence = 0.87,
        };
        await _modeStore.UpsertModeAsync(mode);

        var fetched = await _modeStore.GetModeAsync("fp-rt", "xhr");
        Assert.NotNull(fetched);
        Assert.Equal("xhr", fetched!.ModeId);
        Assert.Equal(12, fetched.CentroidMaturity);
        Assert.Equal(28, fetched.ObservationCount);
        Assert.Equal("chrome-xhr", fetched.InferredArchetype);
        Assert.Equal(0.87, fetched.InferredConfidence!.Value, precision: 6);
        Assert.Equal(dim, fetched.Centroid.Length);
        for (var i = 0; i < dim; i++)
            Assert.Equal(centroid[i], fetched.Centroid[i], precision: 5);
    }

    [Fact]
    public async Task UpsertModeAsync_ExistingMode_UpdatesAndInvalidatesCache()
    {
        await _parent.EnsureInitialisedAsync();
        var dim = _parent.Layout.Dimension;
        await _parent.InsertFingerprintAsync(
            IdentityTestHelpers.MakeFingerprint("fp-up", IdentityTestHelpers.MakeUnitVector(dim, seed: 4)), "sig-up");
        await SeedAgainAsync();

        var weights = new float[dim];
        for (var i = 0; i < dim; i++) weights[i] = 1.0f;

        var firstSeen = DateTime.UtcNow.AddHours(-1);
        var initial = new FingerprintBrowserMode
        {
            FingerprintId = "fp-up",
            ModeId = "navigation",
            Centroid = IdentityTestHelpers.MakeUnitVector(dim, seed: 11),
            CentroidMaturity = 1,
            Weights = weights,
            ObservationCount = 1,
            FirstSeen = firstSeen,
            LastSeen = firstSeen,
            InferredArchetype = null,
            InferredConfidence = null,
        };
        await _modeStore.UpsertModeAsync(initial);

        // Pre-warm the LFU cache for fp-up.
        var preUpsert = await _modeStore.GetModesAsync("fp-up");
        Assert.Contains(preUpsert, m => m.ModeId == "navigation" && m.ObservationCount == 1);

        // Second upsert with new observation_count + later last_seen.
        var laterSeen = DateTime.UtcNow;
        var updated = initial with
        {
            ObservationCount = 7,
            LastSeen = laterSeen,
            InferredArchetype = "chrome-desktop",
            InferredConfidence = 0.93,
        };
        await _modeStore.UpsertModeAsync(updated);

        // Cache slot for fp-up was invalidated by the upsert; this read
        // hits the DB and sees the new state.
        var postUpsert = await _modeStore.GetModesAsync("fp-up");
        var nav = postUpsert.Single(m => m.ModeId == "navigation");
        Assert.Equal(7, nav.ObservationCount);
        Assert.Equal("chrome-desktop", nav.InferredArchetype);
        Assert.Equal(0.93, nav.InferredConfidence!.Value, precision: 6);
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

    private async Task SeedAgainAsync()
    {
        await _parent.EnsureInitialisedAsync();
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(_tempDir, "fingerprints.db")}");
        await conn.OpenAsync();
        await IdentitySchema.SeedFingerprintModesAsync(conn);
    }
}