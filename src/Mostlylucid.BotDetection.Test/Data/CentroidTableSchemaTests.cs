using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Data;

public class CentroidTableSchemaTests : IAsyncLifetime, IDisposable
{
    // SqliteSessionStore resolves DatabasePath as a directory and appends sessions.db,
    // so we use a temp directory rather than a temp file path.
    private readonly string _dbDir;
    private SqliteSessionStore? _store;

    public CentroidTableSchemaTests()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"centroid_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbDir);
    }

    public async Task InitializeAsync()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_dbDir, "sessions.db")
        });
        _store = new SqliteSessionStore(
            NullLogger<SqliteSessionStore>.Instance,
            options);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store != null) await _store.DisposeAsync();
    }

    [Theory]
    [InlineData("signature_centroids")]
    [InlineData("session_centroids")]
    [InlineData("intent_centroids")]
    public async Task Table_ExistsAfterInit(string tableName)
    {
        // Use the connection string the store actually used.
        var connectionString = _store!.PersistenceConnectionString!;
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}'";
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(1, count);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dbDir, recursive: true); } catch { }
    }
}
