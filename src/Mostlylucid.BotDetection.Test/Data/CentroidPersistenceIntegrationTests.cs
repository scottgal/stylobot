using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Data;

/// <summary>
///     Integration tests for Task C: verifies that the three centroid stores are wired as
///     the REAL default (WriteBehindLfuStore) in the standard path, and swapped to their
///     Null variants under AddBotDetectionInMemory.
/// </summary>
public sealed class CentroidPersistenceIntegrationTests : IAsyncLifetime
{
    // Temp dirs created per test class (one per logical path)
    private string _dbDirStandard = null!;
    private string _dbDirInMemory = null!;
    private string _dbDirSlim     = null!;

    public Task InitializeAsync()
    {
        _dbDirStandard = Path.Combine(Path.GetTempPath(), $"cpi_std_{Guid.NewGuid():N}");
        _dbDirInMemory = Path.Combine(Path.GetTempPath(), $"cpi_mem_{Guid.NewGuid():N}");
        _dbDirSlim     = Path.Combine(Path.GetTempPath(), $"cpi_slim_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbDirStandard);
        Directory.CreateDirectory(_dbDirInMemory);
        Directory.CreateDirectory(_dbDirSlim);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        await Task.Delay(200);
        foreach (var dir in new[] { _dbDirStandard, _dbDirInMemory, _dbDirSlim })
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* cleanup best-effort */ }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IServiceProvider BuildStandardProvider(string dbDir, Action<BotDetectionOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddBotDetection(o =>
        {
            o.DatabasePath = Path.Combine(dbDir, "botdetection.db");
            configure?.Invoke(o);
        });
        return services.BuildServiceProvider();
    }

    private static IServiceProvider BuildInMemoryProvider(string dbDir)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddBotDetectionInMemory(o =>
        {
            // DatabasePath is forced empty by AddBotDetectionInMemory; keeping this
            // call as documentation that we'd have set it otherwise.
        });
        return services.BuildServiceProvider();
    }

    private static async Task<bool> RowExistsInDbAsync(string dbFile, string table, string signatureId)
    {
        if (!File.Exists(dbFile)) return false;
        var connString = $"Data Source={dbFile};Mode=ReadOnly";
        await using var conn = new SqliteConnection(connString);
        try { await conn.OpenAsync(); }
        catch { return false; }
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE signature_id = @sig";
        cmd.Parameters.AddWithValue("@sig", signatureId);
        try
        {
            var result = await cmd.ExecuteScalarAsync();
            return result is long l && l > 0;
        }
        catch { return false; }
    }

    private static async Task<bool> PollUntilRowAsync(string dbFile, string table, string sig, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await RowExistsInDbAsync(dbFile, table, sig)) return true;
            await Task.Delay(100);
        }
        return false;
    }

    // ── (a) Standard path: ISignatureCentroidStore resolves to SqliteSignatureCentroidStore ──

    [Fact]
    public void Standard_ISignatureCentroidStore_ResolvesToSqliteStore()
    {
        var provider = BuildStandardProvider(_dbDirStandard);
        var store    = provider.GetRequiredService<ISignatureCentroidStore>();

        Assert.IsType<SqliteSignatureCentroidStore>(store);
    }

    [Fact]
    public void Standard_ISessionCentroidStore_ResolvesToSqliteStore()
    {
        var provider = BuildStandardProvider(_dbDirStandard);
        var store    = provider.GetRequiredService<ISessionCentroidStore>();

        Assert.IsType<SqliteSessionCentroidStore>(store);
    }

    [Fact]
    public void Standard_IIntentCentroidStore_ResolvesToSqliteStore()
    {
        var provider = BuildStandardProvider(_dbDirStandard);
        var store    = provider.GetRequiredService<IIntentCentroidStore>();

        Assert.IsType<SqliteIntentCentroidStore>(store);
    }

    // ── (a) durability: RecordSignature -> drain -> row visible in fresh SQLite connection ──

    [Fact]
    public async Task Standard_RecordSignature_PersistsToDurableTier()
    {
        var provider = BuildStandardProvider(_dbDirStandard);
        var store    = provider.GetRequiredService<ISignatureCentroidStore>();
        var sqlStore = Assert.IsType<SqliteSignatureCentroidStore>(store);

        // Schema must exist before drain writes.
        await sqlStore.InitializeAsync();

        var sig    = $"test-sig-{Guid.NewGuid():N}";
        var vector = new float[] { 0.1f, 0.5f, 0.9f };
        sqlStore.RecordSignature(sig, vector, wasBot: true, confidence: 0.85);

        // Drain interval is 500ms; poll up to 2s.
        var dbFile = Path.Combine(_dbDirStandard, "signature_centroids.db");
        var persisted = await PollUntilRowAsync(dbFile, "signature_centroids", sig, TimeSpan.FromSeconds(2));

        Assert.True(persisted, "Row was not flushed to signature_centroids.db within 2s");
    }

    // ── (b) In-memory path: all three centroid stores resolve to Null variants ──

    [Fact]
    public void InMemory_ISignatureCentroidStore_ResolvesToNullStore()
    {
        var provider = BuildInMemoryProvider(_dbDirInMemory);
        var store    = provider.GetRequiredService<ISignatureCentroidStore>();

        Assert.IsType<NullSignatureCentroidStore>(store);
    }

    [Fact]
    public void InMemory_ISessionCentroidStore_ResolvesToNullStore()
    {
        var provider = BuildInMemoryProvider(_dbDirInMemory);
        var store    = provider.GetRequiredService<ISessionCentroidStore>();

        Assert.IsType<NullSessionCentroidStore>(store);
    }

    [Fact]
    public void InMemory_IIntentCentroidStore_ResolvesToNullStore()
    {
        var provider = BuildInMemoryProvider(_dbDirInMemory);
        var store    = provider.GetRequiredService<IIntentCentroidStore>();

        Assert.IsType<NullIntentCentroidStore>(store);
    }

    // ── (c) Concrete store resolved from DI writes through to durable tier ──

    [Fact]
    public async Task Standard_ConcreteStoreFromDI_WritesThrough()
    {
        var provider = BuildStandardProvider(_dbDirSlim);

        // Resolve the CONCRETE class (not the interface) as some callers do for warm-up.
        var concrete = provider.GetRequiredService<SqliteSignatureCentroidStore>();
        await concrete.InitializeAsync();

        var sig = $"slim-sig-{Guid.NewGuid():N}";
        concrete.RecordSignature(sig, new float[] { 0.3f, 0.6f }, wasBot: false, confidence: 0.2);

        var dbFile    = Path.Combine(_dbDirSlim, "signature_centroids.db");
        var persisted = await PollUntilRowAsync(dbFile, "signature_centroids", sig, TimeSpan.FromSeconds(2));

        Assert.True(persisted, "Concrete store from DI did not write through to durable tier");
    }
}
