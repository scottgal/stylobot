using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.MonitoringPacks;

namespace Mostlylucid.BotDetection.UI.Services;

public sealed class SqliteMetricSnapshotStore : IMetricSnapshotStore
{
    private readonly SqliteConnection? _sharedConn;
    private readonly string? _connectionString;
    private readonly ILogger<SqliteMetricSnapshotStore> _logger;

    public SqliteMetricSnapshotStore(SqliteConnection conn, ILogger<SqliteMetricSnapshotStore> logger)
    {
        _sharedConn = conn;
        _logger = logger;
    }

    public SqliteMetricSnapshotStore(string connectionString, ILogger<SqliteMetricSnapshotStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        var shouldDispose = _sharedConn == null;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = Data.Schema.UiSchemaLoader.Load("metric_snapshots");
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (shouldDispose)
                await conn.DisposeAsync();
        }
    }

    public async Task WriteSnapshotsAsync(IEnumerable<MetricSnapshot> snapshots, CancellationToken ct = default)
    {
        var list = snapshots.ToList();
        if (list.Count == 0) return;

        var conn = await GetConnectionAsync(ct);
        var shouldDispose = _sharedConn == null;
        try
        {
            await using var tx = conn.BeginTransaction();
            try
            {
                foreach (var snap in list)
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO metric_snapshots (bucket_time, pack_id, meter_name, instrument, tags, value, value_type)
                        VALUES (@bt, @pid, @mn, @inst, @tags, @val, @vt)
                        """;
                    cmd.Parameters.AddWithValue("@bt", snap.BucketTime.ToString("O"));
                    cmd.Parameters.AddWithValue("@pid", snap.PackId);
                    cmd.Parameters.AddWithValue("@mn", snap.MeterName);
                    cmd.Parameters.AddWithValue("@inst", snap.Instrument);
                    cmd.Parameters.AddWithValue("@tags", (object?)snap.Tags ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@val", snap.Value);
                    cmd.Parameters.AddWithValue("@vt", snap.ValueType);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
        finally
        {
            if (shouldDispose)
                await conn.DisposeAsync();
        }
    }

    public async Task<List<MetricSnapshot>> GetTimeSeriesAsync(
        string packId, string instrument, DateTime start, DateTime end, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        var shouldDispose = _sharedConn == null;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT bucket_time, pack_id, meter_name, instrument, tags, value, value_type
                FROM metric_snapshots
                WHERE pack_id = @pid AND instrument = @inst
                  AND bucket_time >= @start AND bucket_time <= @end
                ORDER BY bucket_time ASC
                """;
            cmd.Parameters.AddWithValue("@pid", packId);
            cmd.Parameters.AddWithValue("@inst", instrument);
            cmd.Parameters.AddWithValue("@start", start.ToString("O"));
            cmd.Parameters.AddWithValue("@end", end.ToString("O"));

            var results = new List<MetricSnapshot>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                results.Add(ReadRow(reader));
            return results;
        }
        finally
        {
            if (shouldDispose)
                await conn.DisposeAsync();
        }
    }

    public async Task<List<MetricSnapshot>> GetLatestSnapshotsAsync(string packId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        var shouldDispose = _sharedConn == null;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT bucket_time, pack_id, meter_name, instrument, tags, value, value_type
                FROM metric_snapshots
                WHERE pack_id = @pid
                  AND bucket_time = (SELECT MAX(bucket_time) FROM metric_snapshots WHERE pack_id = @pid)
                ORDER BY instrument ASC
                """;
            cmd.Parameters.AddWithValue("@pid", packId);

            var results = new List<MetricSnapshot>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                results.Add(ReadRow(reader));
            return results;
        }
        finally
        {
            if (shouldDispose)
                await conn.DisposeAsync();
        }
    }

    public async Task<int> PruneOldSnapshotsAsync(DateTime cutoff, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        var shouldDispose = _sharedConn == null;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM metric_snapshots WHERE bucket_time < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
            return await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (shouldDispose)
                await conn.DisposeAsync();
        }
    }

    private static MetricSnapshot ReadRow(SqliteDataReader r) => new()
    {
        BucketTime = DateTime.Parse(r.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind),
        PackId = r.GetString(1),
        MeterName = r.GetString(2),
        Instrument = r.GetString(3),
        Tags = r.IsDBNull(4) ? null : r.GetString(4),
        Value = r.GetDouble(5),
        ValueType = r.GetString(6)
    };

    private async Task<SqliteConnection> GetConnectionAsync(CancellationToken ct)
    {
        if (_sharedConn != null) return _sharedConn;
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
