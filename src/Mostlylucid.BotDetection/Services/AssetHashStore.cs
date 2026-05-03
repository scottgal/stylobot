using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     SQLite-backed store of static asset content fingerprints (ETag or Last-Modified+Length).
///     Detects when a fingerprint changes between requests — indicating a deploy happened.
///     On change, marks the asset path stale in <see cref="CentroidSequenceStore"/> so
///     <c>ContentSequenceContributor</c> suppresses false-positive divergence scoring.
/// </summary>
public sealed class AssetHashStore : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly CentroidSequenceStore _centroidStore;
    private readonly ILogger<AssetHashStore> _logger;

    // Persistent connection kept open so that shared in-memory SQLite databases survive
    // between individual operation connections.
    private SqliteConnection? _anchor;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // In-memory index: path → time of last detected change.
    // Populated from DB on startup; updated in-memory immediately on change.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentChanges = new();
    private static readonly TimeSpan RecentChangeWindow = TimeSpan.FromHours(24);
    private readonly Timer _evictionTimer;

    public AssetHashStore(
        string connectionString,
        CentroidSequenceStore centroidStore,
        ILogger<AssetHashStore> logger)
    {
        _connectionString = connectionString;
        _centroidStore = centroidStore;
        _logger = logger;
        _evictionTimer = new Timer(_ => EvictExpiredChanges(), null,
            TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    /// <summary>Create the asset_hashes table and load recent change timestamps into memory.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Keep one connection open for the lifetime of the store so shared in-memory
        // databases are not destroyed when individual operation connections close.
        _anchor = new SqliteConnection(_connectionString);
        await _anchor.OpenAsync(ct);

        await using var cmd = _anchor.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS asset_hashes (
                path         TEXT PRIMARY KEY,
                hash         TEXT NOT NULL,
                changed_at   TEXT,
                last_seen    TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        await LoadRecentChangesAsync(_anchor, ct);
    }

    /// <summary>
    ///     Record the content fingerprint for a static asset path.
    ///     Returns true if the fingerprint changed since last recorded; false on first record or unchanged.
    ///     On change, marks the path stale in <see cref="CentroidSequenceStore"/>.
    /// </summary>
    public async Task<bool> RecordHashAsync(string path, string hash, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            // Read existing hash
            string? existingHash = null;
            await using (var readCmd = conn.CreateCommand())
            {
                readCmd.CommandText = "SELECT hash FROM asset_hashes WHERE path = @path;";
                readCmd.Parameters.AddWithValue("@path", path);
                var result = await readCmd.ExecuteScalarAsync(ct);
                if (result is string s) existingHash = s;
            }

            var now = DateTimeOffset.UtcNow;
            var changed = existingHash != null && existingHash != hash;

            // Upsert: preserve existing changed_at unless we just detected a new change
            await using var upsertCmd = conn.CreateCommand();
            upsertCmd.CommandText = """
                INSERT INTO asset_hashes (path, hash, changed_at, last_seen)
                VALUES (@path, @hash, @changedAt, @lastSeen)
                ON CONFLICT(path) DO UPDATE SET
                    hash       = excluded.hash,
                    changed_at = COALESCE(excluded.changed_at, asset_hashes.changed_at),
                    last_seen  = excluded.last_seen;
                """;
            upsertCmd.Parameters.AddWithValue("@path", path);
            upsertCmd.Parameters.AddWithValue("@hash", hash);
            upsertCmd.Parameters.AddWithValue("@changedAt",
                changed ? now.ToString("O") : (object)DBNull.Value);
            upsertCmd.Parameters.AddWithValue("@lastSeen", now.ToString("O"));
            await upsertCmd.ExecuteNonQueryAsync(ct);

            if (changed)
            {
                _recentChanges[path] = now;
                _centroidStore.MarkEndpointStale(path);
                _logger.LogInformation(
                    "Asset content changed: {Path} (fingerprint: {Old} → {New})",
                    path, existingHash, hash);
            }

            return changed;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    ///     Returns true when a fingerprint change for this path was detected within the last 24 hours.
    ///     Used by ContentSequenceContributor to write <c>asset.content_changed</c> to the blackboard.
    /// </summary>
    public bool IsRecentlyChanged(string path)
    {
        if (!_recentChanges.TryGetValue(path, out var changedAt))
            return false;
        return DateTimeOffset.UtcNow - changedAt < RecentChangeWindow;
    }

    private async Task LoadRecentChangesAsync(SqliteConnection conn, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - RecentChangeWindow;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, changed_at FROM asset_hashes WHERE changed_at IS NOT NULL;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var p = reader.GetString(0);
            if (DateTimeOffset.TryParse(reader.GetString(1), out var changedAt) && changedAt > cutoff)
                _recentChanges[p] = changedAt;
        }
        _logger.LogDebug("AssetHashStore loaded {Count} recent changes from DB", _recentChanges.Count);
    }

    private void EvictExpiredChanges()
    {
        var cutoff = DateTimeOffset.UtcNow - RecentChangeWindow;
        foreach (var key in _recentChanges.Keys)
        {
            if (_recentChanges.TryGetValue(key, out var changedAt) && changedAt < cutoff)
                _recentChanges.TryRemove(key, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _evictionTimer.Dispose();
        _writeLock.Dispose();
        if (_anchor is not null)
            await _anchor.DisposeAsync();
    }
}
