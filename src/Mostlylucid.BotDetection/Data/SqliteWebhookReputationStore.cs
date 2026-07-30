using Microsoft.Data.Sqlite;
using Mostlylucid.BotDetection.Definitions.Webhooks;
using Mostlylucid.BotDetection.Privacy;
using Mostlylucid.BotDetection.Reputation;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     SQLite-backed <see cref="IWebhookEndpointReputation"/>: per (receiver-endpoint,
///     source-IP) request counts and 2xx/4xx delivery outcomes. No in-memory dictionary
///     is the source of truth — every read queries SQLite directly, matching the
///     zero-in-memory-persistence rule for anything that matters. The table is small
///     (bounded by distinct endpoint/IP pairs, not per-request), so a query-per-call is
///     cheap enough not to need a cache.
///
///     Zero-PII: the raw source IP passed into every method never touches SQL. It is
///     hashed internally (<see cref="HashIp"/>) before it is used in any upsert or
///     lookup, and the persisted <c>ip</c> column holds only that hash — mirrors the
///     CLAUDE.md rule "Raw IP/UA only in-memory, never persisted". Same IP always
///     produces the same hash, so dominance/verified-record correlation across calls
///     is unaffected; the hash is one-way, so the raw IP cannot be recovered from the
///     stored row.
///
///     The hash is KEYED HMAC-SHA256 via the shared <see cref="PiiHasher"/> (the same
///     signing-key-driven hasher used for every other PII signature in the product,
///     e.g. request/geo signatures) — NOT a plain SHA-256. A low-entropy value like an
///     IPv4 address is brute-forceable end-to-end against an unkeyed hash (the whole
///     /0-/32 space is a few billion SHA-256 calls); keying it with a secret,
///     per-deployment HMAC key closes that gap the same way the rest of the codebase
///     already does for IP/UA hashing.
/// </summary>
public sealed class SqliteWebhookReputationStore : IWebhookEndpointReputation
{
    private readonly string _connectionString;
    private readonly WebhookCatalog _catalog;
    private readonly PiiHasher _hasher;
    private readonly object _initLock = new();
    private bool _initialised;

    public SqliteWebhookReputationStore(string dbPath, WebhookCatalog catalog, PiiHasher hasher)
    {
        _catalog = catalog;
        _hasher = hasher;
        var dir = Path.GetDirectoryName(dbPath);
        StoreDbDirectory.EnsureExists(dir);
        _connectionString = $"Data Source={dbPath}";
    }

    /// <summary>
    ///     One-way KEYED hash of a raw source IP (via <see cref="PiiHasher.HashIp"/>) so
    ///     it never reaches SQL in cleartext and is not brute-forceable without the
    ///     signing key. Deterministic per (key, IP) pair, which is all the
    ///     dominance/verified-record correlation in this store requires.
    /// </summary>
    private string HashIp(string ip) => _hasher.HashIp(ip);

    private void EnsureInitialised()
    {
        if (_initialised) return;
        lock (_initLock)
        {
            if (_initialised) return;

            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                pragma.ExecuteNonQuery();
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS webhook_endpoint_ip (
                    endpoint    TEXT NOT NULL,
                    ip          TEXT NOT NULL,
                    req_count   INTEGER NOT NULL DEFAULT 0,
                    status_2xx  INTEGER NOT NULL DEFAULT 0,
                    status_4xx  INTEGER NOT NULL DEFAULT 0,
                    first_seen  TEXT NOT NULL,
                    last_seen   TEXT NOT NULL,
                    PRIMARY KEY (endpoint, ip)
                )
                """;
            cmd.ExecuteNonQuery();

            _initialised = true;
        }
    }

    public void RecordRequest(string endpoint, string ip)
    {
        var ipHash = HashIp(ip);
        EnsureInitialised();
        var now = DateTime.UtcNow.ToString("O");

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO webhook_endpoint_ip (endpoint, ip, req_count, status_2xx, status_4xx, first_seen, last_seen)
            VALUES (@endpoint, @ip, 1, 0, 0, @now, @now)
            ON CONFLICT(endpoint, ip) DO UPDATE SET
                req_count = req_count + 1,
                last_seen = @now
            """;
        cmd.Parameters.AddWithValue("@endpoint", endpoint);
        cmd.Parameters.AddWithValue("@ip", ipHash);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    public void RecordOutcome(string endpoint, string ip, int statusCode)
    {
        // 5xx (and anything outside 2xx/4xx) is neutral: a receiver outage must not
        // demote a legitimate sender's track record, so neither counter is touched
        // and no connection is even opened.
        var is2xx = statusCode is >= 200 and < 300;
        var is4xx = statusCode is >= 400 and < 500;
        if (!is2xx && !is4xx) return;

        var ipHash = HashIp(ip);
        EnsureInitialised();
        var now = DateTime.UtcNow.ToString("O");

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO webhook_endpoint_ip (endpoint, ip, req_count, status_2xx, status_4xx, first_seen, last_seen)
            VALUES (@endpoint, @ip, 0, {(is2xx ? 1 : 0)}, {(is4xx ? 1 : 0)}, @now, @now)
            ON CONFLICT(endpoint, ip) DO UPDATE SET
                status_2xx = status_2xx + {(is2xx ? 1 : 0)},
                status_4xx = status_4xx + {(is4xx ? 1 : 0)},
                last_seen = @now
            """;
        cmd.Parameters.AddWithValue("@endpoint", endpoint);
        cmd.Parameters.AddWithValue("@ip", ipHash);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    public bool IsDominantIp(string endpoint, string ip)
    {
        var ipHash = HashIp(ip);
        EnsureInitialised();

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        long ipCount;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT req_count FROM webhook_endpoint_ip WHERE endpoint = @endpoint AND ip = @ip";
            cmd.Parameters.AddWithValue("@endpoint", endpoint);
            cmd.Parameters.AddWithValue("@ip", ipHash);
            var result = cmd.ExecuteScalar();
            if (result is null) return false;
            ipCount = Convert.ToInt64(result);
        }

        if (ipCount < _catalog.DominanceMinCount) return false;

        long total;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT SUM(req_count) FROM webhook_endpoint_ip WHERE endpoint = @endpoint";
            cmd.Parameters.AddWithValue("@endpoint", endpoint);
            var result = cmd.ExecuteScalar();
            total = result is null or DBNull ? 0 : Convert.ToInt64(result);
        }

        if (total <= 0) return false;

        var share = (double)ipCount / total;
        return share >= _catalog.DominanceMinShare;
    }

    public bool HasVerifiedRecord(string endpoint, string ip)
    {
        var ipHash = HashIp(ip);
        EnsureInitialised();

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT status_2xx, status_4xx FROM webhook_endpoint_ip
             WHERE endpoint = @endpoint AND ip = @ip
            """;
        cmd.Parameters.AddWithValue("@endpoint", endpoint);
        cmd.Parameters.AddWithValue("@ip", ipHash);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return false;

        var status2xx = reader.GetInt64(0);
        var status4xx = reader.GetInt64(1);
        return status2xx >= _catalog.VerifiedMin2xx && status2xx > status4xx;
    }
}
