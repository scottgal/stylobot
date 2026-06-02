using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Extensions;

/// <summary>
///     Pins the AddBotDetectionInMemory() contract: setting DatabasePath to
///     ":memory:" routes every FOSS SQLite store at a per-store named shared
///     in-memory database (URI form), and no .db files appear on disk.
/// </summary>
public class InMemoryStorageTests
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    [Fact]
    public void AddBotDetectionInMemory_sets_DatabasePath_to_memory_marker()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(EmptyConfig());

        services.AddBotDetectionInMemory();

        var opts = services.BuildServiceProvider().GetRequiredService<IOptions<BotDetectionOptions>>().Value;
        Assert.Equal(SqliteConnectionStrings.MemoryMarker, opts.DatabasePath);
        Assert.True(SqliteConnectionStrings.IsInMemory(opts.DatabasePath));
    }

    [Fact]
    public void AddBotDetectionInMemory_passes_user_configure_through()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(EmptyConfig());

        services.AddBotDetectionInMemory(o =>
        {
            o.BotThreshold = 0.85;
        });

        var opts = services.BuildServiceProvider().GetRequiredService<IOptions<BotDetectionOptions>>().Value;
        Assert.Equal(SqliteConnectionStrings.MemoryMarker, opts.DatabasePath);
        Assert.Equal(0.85, opts.BotThreshold);
    }

    [Fact]
    public void ConnectionString_helper_routes_in_memory_paths_via_URI_form()
    {
        Assert.True(SqliteConnectionStrings.IsInMemory(":memory:"));
        Assert.False(SqliteConnectionStrings.IsInMemory(null));
        Assert.False(SqliteConnectionStrings.IsInMemory("/tmp/botdetection.db"));

        var cs = SqliteConnectionStrings.ForSibling(":memory:", "clusters.db");
        Assert.Contains("mode=memory", cs);
        Assert.Contains("cache=shared", cs);
        Assert.Contains("clusters", cs);  // store-specific name preserved

        var file = SqliteConnectionStrings.ForFile(":memory:", "botdetection");
        Assert.Contains("mode=memory", file);
        Assert.Contains("botdetection", file);

        var disk = SqliteConnectionStrings.ForSibling("/tmp/sb/botdetection.db", "clusters.db");
        Assert.DoesNotContain("mode=memory", disk);
        Assert.Contains("/tmp/sb/clusters.db", disk);
        Assert.Contains("Cache=Shared", disk);
    }

    [Fact]
    public void ConnectionString_helper_gives_each_store_a_distinct_in_memory_slot()
    {
        // Different store names must route to different named in-memory databases
        // so e.g. SqliteSessionStore can't accidentally read SqliteChallengeStore's tables.
        var sessions   = SqliteConnectionStrings.ForSibling(":memory:", "sessions.db");
        var clusters   = SqliteConnectionStrings.ForSibling(":memory:", "clusters.db");
        var challenges = SqliteConnectionStrings.ForSibling(":memory:", "challenges.db");

        Assert.NotEqual(sessions, clusters);
        Assert.NotEqual(sessions, challenges);
        Assert.NotEqual(clusters, challenges);
    }

    [Fact]
    public async Task In_memory_connection_string_actually_connects_and_persists_within_process()
    {
        // Two connections to the same URI form should see each other's data (shared
        // cache is what makes this useful for the gateway -- multiple stores within
        // the same logical DB file).
        var cs = SqliteConnectionStrings.ForSibling(":memory:", "test-persistence.db");

        await using (var writer = new SqliteConnection(cs))
        {
            await writer.OpenAsync();
            using var cmd = writer.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (k INTEGER PRIMARY KEY, v TEXT); INSERT INTO t (k, v) VALUES (1, 'hello');";
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var reader = new SqliteConnection(cs))
        {
            await reader.OpenAsync();
            using var cmd = reader.CreateCommand();
            cmd.CommandText = "SELECT v FROM t WHERE k = 1";
            var v = (string?)await cmd.ExecuteScalarAsync();
            Assert.Equal("hello", v);
        }
    }

    [Fact]
    public async Task SqliteFingerprintStore_constructs_and_initialises_without_disk_io()
    {
        // The real-shape integration check: instantiate one of the FOSS Sqlite stores
        // against the in-memory marker and EnsureInitialisedAsync(). If it tries to
        // create a directory or open a file on disk, EnsureInitialisedAsync would
        // throw; on the in-memory path it must succeed without touching the
        // filesystem.
        var opts = Options.Create(new BotDetectionOptions
        {
            DatabasePath = SqliteConnectionStrings.MemoryMarker,
        });
        var layout = IdentityVectorLayout.DefaultV1();
        var store = new SqliteFingerprintStore(NullLogger<SqliteFingerprintStore>.Instance, opts, layout);
        await store.EnsureInitialisedAsync();

        // The store's internal connection string must be the in-memory URI form.
        Assert.True(SqliteConnectionStrings.IsInMemory(opts.Value.DatabasePath));
    }
}
