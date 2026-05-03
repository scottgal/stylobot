using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     SQLite-backed signature label store for FOSS. Labels persist across restarts.
///     Commercial overrides with PostgreSQL via TryAddSingleton.
/// </summary>
public sealed class SqliteSignatureLabelStore : ISignatureLabelStore, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteSignatureLabelStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    public SqliteSignatureLabelStore(
        ILogger<SqliteSignatureLabelStore> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        var basePath = Path.GetDirectoryName(
            options.Value.DatabasePath ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db"))
            ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(basePath);
        var dbPath = Path.Combine(basePath, "labels.db");
        _connectionString = $"Data Source={dbPath};Cache=Shared";
    }

    private async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;

            CREATE TABLE IF NOT EXISTS labels (
                signature TEXT NOT NULL,
                kind INTEGER NOT NULL,
                confidence REAL NOT NULL DEFAULT 1.0,
                labeled_by TEXT NOT NULL,
                labeled_at TEXT NOT NULL,
                note TEXT,
                PRIMARY KEY (signature, labeled_by)
            );

            CREATE INDEX IF NOT EXISTS idx_labels_signature ON labels(signature);
            CREATE INDEX IF NOT EXISTS idx_labels_at ON labels(labeled_at DESC);
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        _initialized = true;
        _logger.LogInformation("SQLite label store initialized");
    }

    public async Task<SignatureLabel> UpsertAsync(SignatureLabel label, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO labels (signature, kind, confidence, labeled_by, labeled_at, note)
                VALUES (@sig, @kind, @conf, @by, @at, @note)
                ON CONFLICT(signature, labeled_by) DO UPDATE SET
                    kind = @kind, confidence = @conf, labeled_at = @at, note = @note
                """;
            cmd.Parameters.AddWithValue("@sig", label.Signature);
            cmd.Parameters.AddWithValue("@kind", (int)label.Kind);
            cmd.Parameters.AddWithValue("@conf", label.Confidence);
            cmd.Parameters.AddWithValue("@by", label.LabeledBy);
            cmd.Parameters.AddWithValue("@at", label.LabeledAt.ToString("O"));
            cmd.Parameters.AddWithValue("@note", (object?)label.Note ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);

            return label;
        }
        finally { _writeLock.Release(); }
    }

    public async Task<SignatureLabel?> GetLatestAsync(string signature, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM labels WHERE signature = @sig ORDER BY labeled_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@sig", signature);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadLabel(reader) : null;
    }

    public async Task<IReadOnlyList<SignatureLabel>> ListSinceAsync(DateTime? since, int limit, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = since.HasValue
            ? "SELECT * FROM labels WHERE labeled_at >= @since ORDER BY labeled_at DESC LIMIT @limit"
            : "SELECT * FROM labels ORDER BY labeled_at DESC LIMIT @limit";
        if (since.HasValue) cmd.Parameters.AddWithValue("@since", since.Value.ToString("O"));
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<SignatureLabel>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) results.Add(ReadLabel(reader));
        return results;
    }

    public async Task RemoveAsync(string signature, string labeledBy, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM labels WHERE signature = @sig AND labeled_by = @by";
            cmd.Parameters.AddWithValue("@sig", signature);
            cmd.Parameters.AddWithValue("@by", labeledBy);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<IReadOnlyDictionary<SignatureLabelKind, int>> GetCountsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT kind, COUNT(*) FROM labels GROUP BY kind";

        var counts = new Dictionary<SignatureLabelKind, int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            counts[(SignatureLabelKind)reader.GetInt32(0)] = reader.GetInt32(1);
        return counts;
    }

    private static SignatureLabel ReadLabel(SqliteDataReader reader) => new()
    {
        Signature = reader.GetString(reader.GetOrdinal("signature")),
        Kind = (SignatureLabelKind)reader.GetInt32(reader.GetOrdinal("kind")),
        Confidence = reader.GetDouble(reader.GetOrdinal("confidence")),
        LabeledBy = reader.GetString(reader.GetOrdinal("labeled_by")),
        LabeledAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("labeled_at"))),
        Note = reader.IsDBNull(reader.GetOrdinal("note")) ? null : reader.GetString(reader.GetOrdinal("note"))
    };

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
