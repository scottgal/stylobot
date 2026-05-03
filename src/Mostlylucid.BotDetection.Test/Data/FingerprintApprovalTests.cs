using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Data;

/// <summary>
///     Tests for FingerprintApprovalStore and LockedDimensionChecker.
/// </summary>
public class FingerprintApprovalTests : IAsyncDisposable
{
    private readonly SqliteFingerprintApprovalStore _store;
    private readonly string _dbDir;

    public FingerprintApprovalTests()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"stylobot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dbDir);

        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_dbDir, "test.db")
        });
        _store = new SqliteFingerprintApprovalStore(
            NullLogger<SqliteFingerprintApprovalStore>.Instance,
            options);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        try { Directory.Delete(_dbDir, true); } catch { }
    }

    // ==========================================
    // Approval CRUD
    // ==========================================

    [Fact]
    public async Task UpsertAsync_CreatesApproval()
    {
        var record = new ApprovalRecord
        {
            Signature = "sig-001",
            Justification = "Partner X production scraper",
            ApprovedBy = "admin@test.com",
            ApprovedAt = DateTimeOffset.UtcNow,
            LockedDimensions = new Dictionary<string, string>
            {
                ["ip.country_code"] = "US",
                ["ua.family"] = "Python-Requests"
            }
        };

        var saved = await _store.UpsertAsync(record);

        Assert.Equal("sig-001", saved.Signature);
        Assert.Equal(2, saved.LockedDimensions.Count);
    }

    [Fact]
    public async Task GetAsync_ReturnsExistingApproval()
    {
        await _store.UpsertAsync(new ApprovalRecord
        {
            Signature = "sig-002", Justification = "test", ApprovedBy = "admin",
            ApprovedAt = DateTimeOffset.UtcNow
        });

        var result = await _store.GetAsync("sig-002");

        Assert.NotNull(result);
        Assert.Equal("sig-002", result.Signature);
        Assert.True(result.IsActive);
    }

    [Fact]
    public async Task GetAsync_NonexistentSignature_ReturnsNull()
    {
        var result = await _store.GetAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task RevokeAsync_SetsRevokedFields()
    {
        await _store.UpsertAsync(new ApprovalRecord
        {
            Signature = "sig-003", Justification = "test", ApprovedBy = "admin",
            ApprovedAt = DateTimeOffset.UtcNow
        });

        await _store.RevokeAsync("sig-003", "security-team");

        var result = await _store.GetAsync("sig-003");
        Assert.NotNull(result);
        Assert.False(result.IsActive);
        Assert.NotNull(result.RevokedAt);
        Assert.Equal("security-team", result.RevokedBy);
    }

    [Fact]
    public async Task UpsertAsync_ReapprovalClearsRevocation()
    {
        await _store.UpsertAsync(new ApprovalRecord
        {
            Signature = "sig-004", Justification = "v1", ApprovedBy = "admin",
            ApprovedAt = DateTimeOffset.UtcNow
        });
        await _store.RevokeAsync("sig-004", "security");

        // Re-approve
        await _store.UpsertAsync(new ApprovalRecord
        {
            Signature = "sig-004", Justification = "v2 - re-approved", ApprovedBy = "admin",
            ApprovedAt = DateTimeOffset.UtcNow
        });

        var result = await _store.GetAsync("sig-004");
        Assert.NotNull(result);
        Assert.True(result.IsActive);
        Assert.Null(result.RevokedAt);
        Assert.Equal("v2 - re-approved", result.Justification);
    }

    [Fact]
    public async Task ListRecentAsync_ReturnsOrderedList()
    {
        for (var i = 0; i < 5; i++)
        {
            await _store.UpsertAsync(new ApprovalRecord
            {
                Signature = $"sig-{i:D3}", Justification = $"test-{i}", ApprovedBy = "admin",
                ApprovedAt = DateTimeOffset.UtcNow.AddMinutes(-i)
            });
        }

        var list = await _store.ListRecentAsync(3);

        Assert.Equal(3, list.Count);
        Assert.Equal("sig-000", list[0].Signature); // Most recent first
    }

    [Fact]
    public async Task IsActive_ExpiredApproval_ReturnsFalse()
    {
        await _store.UpsertAsync(new ApprovalRecord
        {
            Signature = "sig-expired", Justification = "test", ApprovedBy = "admin",
            ApprovedAt = DateTimeOffset.UtcNow.AddHours(-2),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) // Already expired
        });

        var result = await _store.GetAsync("sig-expired");
        Assert.NotNull(result);
        Assert.False(result.IsActive);
    }

    // ==========================================
    // Approval Tokens
    // ==========================================

    [Fact]
    public async Task GenerateApprovalToken_ReturnsNonEmptyToken()
    {
        var token = await _store.GenerateApprovalTokenAsync("sig-token-001");

        Assert.NotNull(token);
        Assert.NotEmpty(token);
    }

    [Fact]
    public async Task ConsumeApprovalToken_ReturnsSignatureOnFirstUse()
    {
        var token = await _store.GenerateApprovalTokenAsync("sig-token-002");

        var signature = await _store.ConsumeApprovalTokenAsync(token);

        Assert.Equal("sig-token-002", signature);
    }

    [Fact]
    public async Task ConsumeApprovalToken_SingleUse_ReturnsNullOnSecondCall()
    {
        var token = await _store.GenerateApprovalTokenAsync("sig-token-003");

        var first = await _store.ConsumeApprovalTokenAsync(token);
        var second = await _store.ConsumeApprovalTokenAsync(token);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task ConsumeApprovalToken_NonexistentToken_ReturnsNull()
    {
        var result = await _store.ConsumeApprovalTokenAsync("fake-token");
        Assert.Null(result);
    }

    // ==========================================
    // Locked Dimension Checking
    // ==========================================

    [Fact]
    public void LockedDimensionChecker_AllMatch_ReturnsTrue()
    {
        var locked = new Dictionary<string, string>
        {
            ["ip.country_code"] = "US",
            ["ua.family"] = "Chrome"
        };

        var signals = new Dictionary<string, object>
        {
            ["ip.country_code"] = "US",
            ["ua.family"] = "Chrome"
        };

        var (allMatch, mismatches) = LockedDimensionChecker.Check(locked, key => signals.GetValueOrDefault(key));

        Assert.True(allMatch);
        Assert.Empty(mismatches);
    }

    [Fact]
    public void LockedDimensionChecker_Mismatch_ReturnsFalseWithKeys()
    {
        var locked = new Dictionary<string, string>
        {
            ["ip.country_code"] = "US",
            ["ua.family"] = "Chrome"
        };

        var signals = new Dictionary<string, object>
        {
            ["ip.country_code"] = "DE", // Mismatch!
            ["ua.family"] = "Chrome"
        };

        var (allMatch, mismatches) = LockedDimensionChecker.Check(locked, key => signals.GetValueOrDefault(key));

        Assert.False(allMatch);
        Assert.Single(mismatches);
        Assert.Contains("ip.country_code", mismatches);
    }

    [Fact]
    public void LockedDimensionChecker_MissingSignal_TreatedAsMismatch()
    {
        var locked = new Dictionary<string, string>
        {
            ["ip.country_code"] = "US"
        };

        var signals = new Dictionary<string, object>(); // Empty - no signal

        var (allMatch, mismatches) = LockedDimensionChecker.Check(locked, key => signals.GetValueOrDefault(key));

        Assert.False(allMatch);
        Assert.Single(mismatches);
    }

    [Fact]
    public void LockedDimensionChecker_CaseInsensitive()
    {
        var locked = new Dictionary<string, string>
        {
            ["ua.family"] = "chrome"
        };

        var signals = new Dictionary<string, object>
        {
            ["ua.family"] = "Chrome" // Different case
        };

        var (allMatch, _) = LockedDimensionChecker.Check(locked, key => signals.GetValueOrDefault(key));

        Assert.True(allMatch); // Case-insensitive comparison
    }

    [Fact]
    public void LockedDimensionChecker_CidrMatch_InRange()
    {
        var locked = new Dictionary<string, string>
        {
            ["ip.cidr"] = "10.0.0.0/8"
        };

        var signals = new Dictionary<string, object>
        {
            ["ip.cidr"] = "10.42.1.100" // In 10.0.0.0/8
        };

        var (allMatch, _) = LockedDimensionChecker.Check(locked, key => signals.GetValueOrDefault(key));

        Assert.True(allMatch);
    }

    [Fact]
    public void LockedDimensionChecker_CidrMatch_OutOfRange()
    {
        var locked = new Dictionary<string, string>
        {
            ["ip.cidr"] = "10.0.0.0/8"
        };

        var signals = new Dictionary<string, object>
        {
            ["ip.cidr"] = "192.168.1.1" // NOT in 10.0.0.0/8
        };

        var (allMatch, mismatches) = LockedDimensionChecker.Check(locked, key => signals.GetValueOrDefault(key));

        Assert.False(allMatch);
        Assert.Contains("ip.cidr", mismatches);
    }

    [Fact]
    public void LockedDimensionChecker_EmptyLockedDimensions_AlwaysMatch()
    {
        var locked = new Dictionary<string, string>();

        var (allMatch, mismatches) = LockedDimensionChecker.Check(locked, _ => null);

        Assert.True(allMatch);
        Assert.Empty(mismatches);
    }
}
