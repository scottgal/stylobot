using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     A fingerprint approval record with locked dimensions and audit trail.
///     Locked dimensions act as a behavioral contract: if the client drifts from
///     locked values (country, UA, IP range), the approval is void.
/// </summary>
public sealed record ApprovalRecord
{
    public required string Signature { get; init; }
    public required string Justification { get; init; }
    public required string ApprovedBy { get; init; }
    public required DateTimeOffset ApprovedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public string? RevokedBy { get; init; }

    /// <summary>
    ///     Signal key -> expected value. Each locked dimension is checked against
    ///     the live blackboard on every request. Mismatch = approval void.
    ///     Example: { "ip.country_code": "US", "ua.family": "Python-Requests" }
    /// </summary>
    public IReadOnlyDictionary<string, string> LockedDimensions { get; init; } =
        new Dictionary<string, string>();

    public bool IsActive => RevokedAt is null && (ExpiresAt is null || DateTimeOffset.UtcNow < ExpiresAt);
}

/// <summary>
///     A one-time approval token sent via X-SB-Approval-Id header.
///     The operator enters this token in the dashboard to approve the associated fingerprint.
/// </summary>
public sealed record ApprovalToken
{
    public required string Token { get; init; }
    public required string Signature { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public bool Consumed { get; init; }
}

/// <summary>
///     Persistent store for fingerprint approvals with locked dimensions.
///     Provides one-time approval tokens (sent via response header) and
///     approval records (created via dashboard form).
/// </summary>
public interface IFingerprintApprovalStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<ApprovalRecord> UpsertAsync(ApprovalRecord record, CancellationToken ct = default);
    Task<ApprovalRecord?> GetAsync(string signature, CancellationToken ct = default);
    Task<IReadOnlyList<ApprovalRecord>> ListRecentAsync(int limit = 50, CancellationToken ct = default);
    Task RevokeAsync(string signature, string revokedBy, CancellationToken ct = default);

    /// <summary>Generate a one-time approval token tied to a fingerprint signature.</summary>
    Task<string> GenerateApprovalTokenAsync(string signature, CancellationToken ct = default);

    /// <summary>Consume a token and return the associated signature. Single-use.</summary>
    Task<string?> ConsumeApprovalTokenAsync(string token, CancellationToken ct = default);
}

/// <summary>
///     SQLite-backed fingerprint approval store. Zero dependencies, file on disk.
///     Follows the same patterns as SqliteSessionStore (WAL mode, SemaphoreSlim write lock).
/// </summary>
public sealed class SqliteFingerprintApprovalStore : IFingerprintApprovalStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _connectionString;
    private readonly ILogger<SqliteFingerprintApprovalStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    // Read-through cache: avoids SQLite round-trip on every request (approvals rarely change)
    private readonly Services.BoundedCache<string, ApprovalRecord?> _readCache = new(maxSize: 1_000, defaultTtl: TimeSpan.FromMinutes(5));

    public SqliteFingerprintApprovalStore(
        ILogger<SqliteFingerprintApprovalStore> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        var basePath = Path.GetDirectoryName(
            options.Value.DatabasePath ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db"))
            ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(basePath);
        var dbPath = Path.Combine(basePath, "approvals.db");
        _connectionString = $"Data Source={dbPath};Cache=Shared";
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA cache_size=-2000;

            CREATE TABLE IF NOT EXISTS fingerprint_approvals (
                signature TEXT PRIMARY KEY,
                locked_dimensions_json TEXT NOT NULL DEFAULT '{}',
                justification TEXT NOT NULL,
                approved_by TEXT NOT NULL,
                approved_at TEXT NOT NULL,
                expires_at TEXT,
                revoked_at TEXT,
                revoked_by TEXT
            );

            CREATE TABLE IF NOT EXISTS approval_tokens (
                token TEXT PRIMARY KEY,
                signature TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                consumed INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_tokens_signature ON approval_tokens(signature);
            CREATE INDEX IF NOT EXISTS idx_tokens_expires ON approval_tokens(expires_at);
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        _initialized = true;
        _logger.LogInformation("Fingerprint approval store initialized at {ConnectionString}", _connectionString);
    }

    public async Task<ApprovalRecord> UpsertAsync(ApprovalRecord record, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO fingerprint_approvals (signature, locked_dimensions_json, justification, approved_by, approved_at, expires_at, revoked_at, revoked_by)
                VALUES (@sig, @dims, @just, @by, @at, @exp, NULL, NULL)
                ON CONFLICT(signature) DO UPDATE SET
                    locked_dimensions_json = @dims,
                    justification = @just,
                    approved_by = @by,
                    approved_at = @at,
                    expires_at = @exp,
                    revoked_at = NULL,
                    revoked_by = NULL
                """;
            cmd.Parameters.AddWithValue("@sig", record.Signature);
            cmd.Parameters.AddWithValue("@dims", JsonSerializer.Serialize(record.LockedDimensions, JsonOptions));
            cmd.Parameters.AddWithValue("@just", record.Justification);
            cmd.Parameters.AddWithValue("@by", record.ApprovedBy);
            cmd.Parameters.AddWithValue("@at", record.ApprovedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@exp", record.ExpiresAt?.ToString("O") ?? (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);

            _readCache.Remove(record.Signature); // Invalidate cache on write

            _logger.LogInformation("Fingerprint approved: {Signature} by {By} with {DimCount} locked dimensions",
                record.Signature, record.ApprovedBy, record.LockedDimensions.Count);

            return record;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<ApprovalRecord?> GetAsync(string signature, CancellationToken ct = default)
    {
        // Read-through cache: avoids SQLite round-trip on every request
        if (_readCache.TryGet(signature, out var cached)) return cached;

        await InitializeAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM fingerprint_approvals WHERE signature = @sig";
        cmd.Parameters.AddWithValue("@sig", signature);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var record = await reader.ReadAsync(ct) ? ReadApproval(reader) : null;

        _readCache.Set(signature, record);
        return record;
    }

    public async Task<IReadOnlyList<ApprovalRecord>> ListRecentAsync(int limit = 50, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM fingerprint_approvals ORDER BY approved_at DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<ApprovalRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadApproval(reader));

        return results;
    }

    public async Task RevokeAsync(string signature, string revokedBy, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE fingerprint_approvals
                SET revoked_at = @at, revoked_by = @by
                WHERE signature = @sig AND revoked_at IS NULL
                """;
            cmd.Parameters.AddWithValue("@sig", signature);
            cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@by", revokedBy);
            await cmd.ExecuteNonQueryAsync(ct);
            _readCache.Remove(signature); // Invalidate cache on revoke

            _logger.LogInformation("Fingerprint approval revoked: {Signature} by {By}", signature, revokedBy);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<string> GenerateApprovalTokenAsync(string signature, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var token = Guid.NewGuid().ToString("N");

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            // Clean up old tokens for this signature first
            await using var cleanCmd = conn.CreateCommand();
            cleanCmd.CommandText = "DELETE FROM approval_tokens WHERE signature = @sig OR expires_at < @now";
            cleanCmd.Parameters.AddWithValue("@sig", signature);
            cleanCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            await cleanCmd.ExecuteNonQueryAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO approval_tokens (token, signature, created_at, expires_at, consumed)
                VALUES (@token, @sig, @created, @expires, 0)
                """;
            cmd.Parameters.AddWithValue("@token", token);
            cmd.Parameters.AddWithValue("@sig", signature);
            cmd.Parameters.AddWithValue("@created", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@expires", DateTimeOffset.UtcNow.AddHours(24).ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);

            return token;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<string?> ConsumeApprovalTokenAsync(string token, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            // Find and consume in one transaction
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE approval_tokens SET consumed = 1
                WHERE token = @token AND consumed = 0 AND expires_at > @now
                RETURNING signature
                """;
            cmd.Parameters.AddWithValue("@token", token);
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));

            var result = await cmd.ExecuteScalarAsync(ct);
            return result as string;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static ApprovalRecord ReadApproval(SqliteDataReader reader)
    {
        var dimsJson = reader.GetString(reader.GetOrdinal("locked_dimensions_json"));
        var dims = JsonSerializer.Deserialize<Dictionary<string, string>>(dimsJson, JsonOptions)
                   ?? new Dictionary<string, string>();

        return new ApprovalRecord
        {
            Signature = reader.GetString(reader.GetOrdinal("signature")),
            LockedDimensions = dims,
            Justification = reader.GetString(reader.GetOrdinal("justification")),
            ApprovedBy = reader.GetString(reader.GetOrdinal("approved_by")),
            ApprovedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("approved_at"))),
            ExpiresAt = reader.IsDBNull(reader.GetOrdinal("expires_at"))
                ? null
                : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("expires_at"))),
            RevokedAt = reader.IsDBNull(reader.GetOrdinal("revoked_at"))
                ? null
                : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("revoked_at"))),
            RevokedBy = reader.IsDBNull(reader.GetOrdinal("revoked_by"))
                ? null
                : reader.GetString(reader.GetOrdinal("revoked_by"))
        };
    }

    public async ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
    }
}

/// <summary>
///     Utility for checking locked dimension constraints, including CIDR range matching.
/// </summary>
public static class LockedDimensionChecker
{
    /// <summary>
    ///     Checks all locked dimensions against live signal values.
    ///     Returns (allMatch, mismatches) where mismatches lists the dimension keys that failed.
    /// </summary>
    public static (bool AllMatch, IReadOnlyList<string> Mismatches) Check(
        IReadOnlyDictionary<string, string> lockedDimensions,
        Func<string, object?> getSignal)
    {
        var mismatches = new List<string>();

        foreach (var (key, expected) in lockedDimensions)
        {
            var actual = getSignal(key);
            if (actual is null)
            {
                mismatches.Add(key);
                continue;
            }

            var actualStr = actual.ToString() ?? "";

            // Special handling for CIDR ranges (ip.cidr key)
            if (key.EndsWith(".cidr", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsInCidrRange(actualStr, expected))
                    mismatches.Add(key);
            }
            else if (!string.Equals(actualStr, expected, StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add(key);
            }
        }

        return (mismatches.Count == 0, mismatches);
    }

    private static bool IsInCidrRange(string ipStr, string cidr)
    {
        try
        {
            if (!IPAddress.TryParse(ipStr, out var ip)) return false;

            var slashIndex = cidr.IndexOf('/');
            if (slashIndex < 0) return string.Equals(ipStr, cidr, StringComparison.OrdinalIgnoreCase);

            if (!IPAddress.TryParse(cidr[..slashIndex], out var network)) return false;
            if (!int.TryParse(cidr[(slashIndex + 1)..], out var prefixLen)) return false;

            var ipBytes = ip.GetAddressBytes();
            var netBytes = network.GetAddressBytes();
            if (ipBytes.Length != netBytes.Length) return false;

            var fullBytes = prefixLen / 8;
            var remainingBits = prefixLen % 8;

            for (var i = 0; i < fullBytes && i < ipBytes.Length; i++)
                if (ipBytes[i] != netBytes[i]) return false;

            if (remainingBits > 0 && fullBytes < ipBytes.Length)
            {
                var mask = (byte)(0xFF << (8 - remainingBits));
                if ((ipBytes[fullBytes] & mask) != (netBytes[fullBytes] & mask)) return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
