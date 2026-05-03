using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models.Auth;

namespace Mostlylucid.BotDetection.UI.Services.Auth;

public sealed class StyloBotUserStore :
    IUserStore<StyloBotUser>,
    IUserPasswordStore<StyloBotUser>,
    IUserEmailStore<StyloBotUser>,
    IUserSecurityStampStore<StyloBotUser>,
    IUserTwoFactorStore<StyloBotUser>,
    IUserAuthenticatorKeyStore<StyloBotUser>,
    IUserTwoFactorRecoveryCodeStore<StyloBotUser>,
    IUserLockoutStore<StyloBotUser>
{
    private readonly string _connectionString;
    private readonly ILogger<StyloBotUserStore> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public StyloBotUserStore(
        ILogger<StyloBotUserStore> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _connectionString = DashboardDbPath.GetConnectionString(options.Value);
    }

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;

                CREATE TABLE IF NOT EXISTS dashboard_users (
                    id TEXT PRIMARY KEY,
                    username TEXT NOT NULL,
                    normalized_username TEXT NOT NULL,
                    email TEXT NOT NULL,
                    normalized_email TEXT NOT NULL,
                    email_confirmed INTEGER NOT NULL DEFAULT 0,
                    password_hash TEXT,
                    security_stamp TEXT NOT NULL DEFAULT '',
                    concurrency_stamp TEXT NOT NULL DEFAULT '',
                    phone_number TEXT,
                    phone_number_confirmed INTEGER NOT NULL DEFAULT 0,
                    two_factor_enabled INTEGER NOT NULL DEFAULT 0,
                    lockout_end TEXT,
                    lockout_enabled INTEGER NOT NULL DEFAULT 0,
                    access_failed_count INTEGER NOT NULL DEFAULT 0,
                    authenticator_key TEXT,
                    recovery_codes TEXT
                );

                CREATE UNIQUE INDEX IF NOT EXISTS idx_users_norm_username ON dashboard_users(normalized_username);
                CREATE UNIQUE INDEX IF NOT EXISTS idx_users_norm_email ON dashboard_users(normalized_email);

                CREATE TABLE IF NOT EXISTS dashboard_api_keys (
                    id TEXT PRIMARY KEY,
                    key_hash TEXT NOT NULL UNIQUE,
                    name TEXT NOT NULL,
                    scope TEXT NOT NULL DEFAULT 'read',
                    created_at TEXT NOT NULL,
                    last_used_at TEXT,
                    expires_at TEXT,
                    created_by TEXT
                );
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<int> GetUserCountAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dashboard_users";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    // ── IUserStore ──────────────────────────────────────────────────────────

    public async Task<IdentityResult> CreateAsync(StyloBotUser user, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dashboard_users (
                id, username, normalized_username, email, normalized_email, email_confirmed,
                password_hash, security_stamp, concurrency_stamp,
                phone_number, phone_number_confirmed, two_factor_enabled,
                lockout_enabled, access_failed_count
            ) VALUES (
                $id, $un, $nun, $em, $nem, $ec,
                $ph, $ss, $cs,
                $pn, $pnc, $tfe,
                $le, $afc
            )
            """;
        BindUser(cmd, user);
        await cmd.ExecuteNonQueryAsync(ct);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(StyloBotUser user, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dashboard_users SET
                username = $un, normalized_username = $nun,
                email = $em, normalized_email = $nem, email_confirmed = $ec,
                password_hash = $ph, security_stamp = $ss, concurrency_stamp = $cs,
                phone_number = $pn, phone_number_confirmed = $pnc,
                two_factor_enabled = $tfe, lockout_end = $loe,
                lockout_enabled = $le, access_failed_count = $afc,
                authenticator_key = $ak, recovery_codes = $rc
            WHERE id = $id
            """;
        BindUser(cmd, user);
        cmd.Parameters.AddWithValue("$loe", user.LockoutEnd.HasValue
            ? user.LockoutEnd.Value.ToString("O")
            : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$ak", user.AuthenticatorKey ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$rc", user.RecoveryCodesJson ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> DeleteAsync(StyloBotUser user, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM dashboard_users WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", user.Id);
        await cmd.ExecuteNonQueryAsync(ct);
        return IdentityResult.Success;
    }

    public async Task<StyloBotUser?> FindByIdAsync(string userId, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        return await QuerySingleAsync("WHERE id = $v", userId, ct);
    }

    public async Task<StyloBotUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        return await QuerySingleAsync("WHERE normalized_username = $v", normalizedUserName, ct);
    }

    public Task<string> GetUserIdAsync(StyloBotUser user, CancellationToken ct) =>
        Task.FromResult(user.Id);

    public Task<string?> GetUserNameAsync(StyloBotUser user, CancellationToken ct) =>
        Task.FromResult(user.UserName);

    public Task SetUserNameAsync(StyloBotUser user, string? userName, CancellationToken ct)
    {
        user.UserName = userName;
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedUserNameAsync(StyloBotUser user, CancellationToken ct) =>
        Task.FromResult(user.NormalizedUserName);

    public Task SetNormalizedUserNameAsync(StyloBotUser user, string? normalizedName, CancellationToken ct)
    {
        user.NormalizedUserName = normalizedName;
        return Task.CompletedTask;
    }

    // ── IUserPasswordStore ───────────────────────────────────────────────────

    public Task SetPasswordHashAsync(StyloBotUser user, string? passwordHash, CancellationToken ct)
    {
        user.PasswordHash = passwordHash;
        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordHashAsync(StyloBotUser user, CancellationToken ct) =>
        Task.FromResult(user.PasswordHash);

    public Task<bool> HasPasswordAsync(StyloBotUser user, CancellationToken ct) =>
        Task.FromResult(user.PasswordHash != null);

    // ── IUserEmailStore ──────────────────────────────────────────────────────

    public Task SetEmailAsync(StyloBotUser user, string? email, CancellationToken ct)
    {
        user.Email = email;
        return Task.CompletedTask;
    }

    public Task<string?> GetEmailAsync(StyloBotUser user, CancellationToken ct) =>
        Task.FromResult(user.Email);

    public Task<bool> GetEmailConfirmedAsync(StyloBotUser user, CancellationToken ct) =>
        Task.FromResult(user.EmailConfirmed);

    public Task SetEmailConfirmedAsync(StyloBotUser user, bool confirmed, CancellationToken ct)
    {
        user.EmailConfirmed = confirmed;
        return Task.CompletedTask;
    }

    public async Task<StyloBotUser?> FindByEmailAsync(string normalizedEmail, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        return await QuerySingleAsync("WHERE normalized_email = $v", normalizedEmail, ct);
    }

    public Task<string?> GetNormalizedEmailAsync(StyloBotUser user, CancellationToken ct) =>
        Task.FromResult(user.NormalizedEmail);

    public Task SetNormalizedEmailAsync(StyloBotUser user, string? normalizedEmail, CancellationToken ct)
    {
        user.NormalizedEmail = normalizedEmail;
        return Task.CompletedTask;
    }

    // ── IUserSecurityStampStore ──────────────────────────────────────────────

    public Task SetSecurityStampAsync(StyloBotUser user, string stamp, CancellationToken ct)
    {
        user.SecurityStamp = stamp;
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(StyloBotUser user, CancellationToken ct) =>
        Task.FromResult(user.SecurityStamp);

    // ── IUserTwoFactorStore ──────────────────────────────────────────────────

    public Task SetTwoFactorEnabledAsync(StyloBotUser user, bool enabled, CancellationToken ct)
    {
        user.TwoFactorEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task<bool> GetTwoFactorEnabledAsync(StyloBotUser user, CancellationToken ct) =>
        Task.FromResult(user.TwoFactorEnabled);

    // ── IUserAuthenticatorKeyStore ───────────────────────────────────────────

    public Task SetAuthenticatorKeyAsync(StyloBotUser user, string key, CancellationToken ct)
    {
        user.AuthenticatorKey = key;
        return Task.CompletedTask;
    }

    public Task<string?> GetAuthenticatorKeyAsync(StyloBotUser user, CancellationToken ct) =>
        Task.FromResult(user.AuthenticatorKey);

    // ── IUserTwoFactorRecoveryCodeStore ──────────────────────────────────────

    public Task ReplaceCodesAsync(StyloBotUser user, IEnumerable<string> recoveryCodes, CancellationToken ct)
    {
        user.RecoveryCodesJson = JsonSerializer.Serialize(recoveryCodes.ToList());
        return Task.CompletedTask;
    }

    public Task<bool> RedeemCodeAsync(StyloBotUser user, string code, CancellationToken ct)
    {
        var codes = string.IsNullOrEmpty(user.RecoveryCodesJson)
            ? []
            : JsonSerializer.Deserialize<List<string>>(user.RecoveryCodesJson) ?? [];
        if (!codes.Remove(code)) return Task.FromResult(false);
        user.RecoveryCodesJson = JsonSerializer.Serialize(codes);
        return Task.FromResult(true);
    }

    public Task<int> CountCodesAsync(StyloBotUser user, CancellationToken ct)
    {
        var count = string.IsNullOrEmpty(user.RecoveryCodesJson)
            ? 0
            : JsonSerializer.Deserialize<List<string>>(user.RecoveryCodesJson)?.Count ?? 0;
        return Task.FromResult(count);
    }

    // ── IUserLockoutStore ────────────────────────────────────────────────────

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(StyloBotUser user, CancellationToken ct) =>
        Task.FromResult(user.LockoutEnd);

    public Task SetLockoutEndDateAsync(StyloBotUser user, DateTimeOffset? lockoutEnd, CancellationToken ct)
    {
        user.LockoutEnd = lockoutEnd;
        return Task.CompletedTask;
    }

    public Task<int> IncrementAccessFailedCountAsync(StyloBotUser user, CancellationToken ct)
    {
        user.AccessFailedCount++;
        return Task.FromResult(user.AccessFailedCount);
    }

    public Task ResetAccessFailedCountAsync(StyloBotUser user, CancellationToken ct)
    {
        user.AccessFailedCount = 0;
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(StyloBotUser user, CancellationToken ct) =>
        Task.FromResult(user.AccessFailedCount);

    public Task<bool> GetLockoutEnabledAsync(StyloBotUser user, CancellationToken ct) =>
        Task.FromResult(user.LockoutEnabled);

    public Task SetLockoutEnabledAsync(StyloBotUser user, bool enabled, CancellationToken ct)
    {
        user.LockoutEnabled = enabled;
        return Task.CompletedTask;
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose() { }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<StyloBotUser?> QuerySingleAsync(string whereClause, string paramValue, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, username, normalized_username, email, normalized_email, email_confirmed,
                   password_hash, security_stamp, concurrency_stamp,
                   phone_number, phone_number_confirmed, two_factor_enabled,
                   lockout_end, lockout_enabled, access_failed_count,
                   authenticator_key, recovery_codes
            FROM dashboard_users {whereClause}
            """;
        cmd.Parameters.AddWithValue("$v", paramValue);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapUser(reader) : null;
    }

    private static void BindUser(SqliteCommand cmd, StyloBotUser user)
    {
        cmd.Parameters.AddWithValue("$id", user.Id);
        cmd.Parameters.AddWithValue("$un", user.UserName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$nun", user.NormalizedUserName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$em", user.Email ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$nem", user.NormalizedEmail ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$ec", user.EmailConfirmed ? 1 : 0);
        cmd.Parameters.AddWithValue("$ph", user.PasswordHash ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$ss", user.SecurityStamp ?? string.Empty);
        cmd.Parameters.AddWithValue("$cs", user.ConcurrencyStamp ?? Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("$pn", user.PhoneNumber ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$pnc", user.PhoneNumberConfirmed ? 1 : 0);
        cmd.Parameters.AddWithValue("$tfe", user.TwoFactorEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$le", user.LockoutEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$afc", user.AccessFailedCount);
    }

    private static StyloBotUser MapUser(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        UserName = r.IsDBNull(1) ? null : r.GetString(1),
        NormalizedUserName = r.IsDBNull(2) ? null : r.GetString(2),
        Email = r.IsDBNull(3) ? null : r.GetString(3),
        NormalizedEmail = r.IsDBNull(4) ? null : r.GetString(4),
        EmailConfirmed = r.GetInt32(5) == 1,
        PasswordHash = r.IsDBNull(6) ? null : r.GetString(6),
        SecurityStamp = r.IsDBNull(7) ? null : r.GetString(7),
        ConcurrencyStamp = r.IsDBNull(8) ? null : r.GetString(8),
        PhoneNumber = r.IsDBNull(9) ? null : r.GetString(9),
        PhoneNumberConfirmed = r.GetInt32(10) == 1,
        TwoFactorEnabled = r.GetInt32(11) == 1,
        LockoutEnd = r.IsDBNull(12) ? null : DateTimeOffset.Parse(r.GetString(12)),
        LockoutEnabled = r.GetInt32(13) == 1,
        AccessFailedCount = r.GetInt32(14),
        AuthenticatorKey = r.IsDBNull(15) ? null : r.GetString(15),
        RecoveryCodesJson = r.IsDBNull(16) ? null : r.GetString(16),
    };
}
