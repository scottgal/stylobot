using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Auto-migration for an identity vector-layout bump: a stored fingerprints.db at an
///     older layout must be wiped + re-seeded at the running layout rather than hard-failing
///     EnsureLayoutRowAsync ("Migrate before starting"). Centroids are re-learnable, so a bump
///     is a one-time warm-up restage, not a startup crash.
/// </summary>
public class IdentityLayoutMigrationTests
{
    private static BotDetectionOptions OptionsFor(string dir) => new()
    {
        DatabasePath = Path.Combine(dir, "botdetection.db")
    };

    [Fact]
    public async Task StaleLayoutDb_IsWipedAndReSeededAtCurrentLayout_NoThrow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sb-layout-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "fingerprints.db");
        try
        {
            // Seed a stale layout db (older version + a smaller dimension count).
            await using (var seed = new SqliteConnection($"Data Source={dbPath}"))
            {
                await seed.OpenAsync();
                await using var cmd = seed.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE identity_vector_layout (id INTEGER PRIMARY KEY, version INTEGER,
                        dimension INTEGER, layout_json TEXT, installed_at TEXT);
                    INSERT INTO identity_vector_layout (id, version, dimension, layout_json, installed_at)
                        VALUES (1, 1, 7, '[]', '2026-01-01T00:00:00Z');
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            var layout = IdentityVectorLayout.DefaultV1();
            var store = new SqliteFingerprintStore(
                NullLogger<SqliteFingerprintStore>.Instance,
                Options.Create(OptionsFor(dir)),
                layout);

            // Pre-migration this threw InvalidOperationException("Migrate before starting").
            await store.EnsureInitialisedAsync();

            // The db now carries the running layout.
            await using var check = new SqliteConnection($"Data Source={dbPath}");
            await check.OpenAsync();
            await using var read = check.CreateCommand();
            read.CommandText = "SELECT version, dimension FROM identity_vector_layout WHERE id = 1";
            await using var reader = await read.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(layout.Version, reader.GetInt32(0));
            Assert.Equal(layout.Dimension, reader.GetInt32(1));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    [Fact]
    public async Task FreshDb_InitialisesAtCurrentLayout_NoWipeNeeded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sb-layout-fresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteFingerprintStore(
                NullLogger<SqliteFingerprintStore>.Instance,
                Options.Create(OptionsFor(dir)),
                IdentityVectorLayout.DefaultV1());

            await store.EnsureInitialisedAsync();

            Assert.True(File.Exists(Path.Combine(dir, "fingerprints.db")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }
}