using Mostlylucid.BotDetection.Licensing;

namespace Mostlylucid.BotDetection.Test.Licensing;

public class LicenseStateTests
{
    [Fact]
    public void FossLicenseState_AlwaysActive_NeverFrozen()
    {
        ILicenseState state = new FossLicenseState();

        Assert.True(state.IsActive);
        Assert.False(state.IsInGrace);
        Assert.False(state.LearningFrozen);
        Assert.False(state.LogOnly);
        Assert.Null(state.ExpiresAt);
        Assert.Null(state.GraceEndsAt);
    }

    [Fact]
    public void Snapshot_ActiveLicense_NotFrozen()
    {
        var snapshot = LicenseStateSnapshot.Compute(
            expiresAt: DateTimeOffset.UtcNow.AddDays(30),
            graceEligible: true,
            graceStartedAt: null);

        Assert.True(snapshot.IsActive);
        Assert.False(snapshot.IsInGrace);
        Assert.False(snapshot.LearningFrozen);
        Assert.False(snapshot.LogOnly);
    }

    [Fact]
    public void Snapshot_ExpiredGraceEligible_InGrace_LearningFrozen_NotLogOnly()
    {
        var graceStartedAt = DateTimeOffset.UtcNow.AddDays(-5);
        var snapshot = LicenseStateSnapshot.Compute(
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1),
            graceEligible: true,
            graceStartedAt: graceStartedAt);

        Assert.False(snapshot.IsActive);
        Assert.True(snapshot.IsInGrace);
        Assert.True(snapshot.LearningFrozen);
        Assert.False(snapshot.LogOnly);
        Assert.NotNull(snapshot.GraceEndsAt);
    }

    [Fact]
    public void Snapshot_ExpiredGraceConsumed_LogOnly()
    {
        var snapshot = LicenseStateSnapshot.Compute(
            expiresAt: DateTimeOffset.UtcNow.AddDays(-40),
            graceEligible: true,
            graceStartedAt: DateTimeOffset.UtcNow.AddDays(-31));

        Assert.False(snapshot.IsActive);
        Assert.False(snapshot.IsInGrace);
        Assert.True(snapshot.LearningFrozen);
        Assert.True(snapshot.LogOnly);
    }

    [Fact]
    public void Snapshot_ExpiredNotGraceEligible_ImmediatelyLogOnly()
    {
        var snapshot = LicenseStateSnapshot.Compute(
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1),
            graceEligible: false,
            graceStartedAt: null);

        Assert.False(snapshot.IsActive);
        Assert.False(snapshot.IsInGrace);
        Assert.True(snapshot.LearningFrozen);
        Assert.True(snapshot.LogOnly);
    }

    [Fact]
    public void TokenParser_NullToken_ReturnsNull()
    {
        Assert.Null(LicenseTokenParser.TryParse(null));
        Assert.Null(LicenseTokenParser.TryParse(""));
        Assert.Null(LicenseTokenParser.TryParse("not.a.jwt"));
    }

    [Fact]
    public void TokenParser_ValidPayload_ExtractsExpAndGraceEligible()
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            exp = 9999999999L,
            grace_eligible = false
        });
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var fakeJwt = $"header.{encoded}.signature";

        var claims = LicenseTokenParser.TryParse(fakeJwt);

        Assert.NotNull(claims);
        Assert.False(claims!.GraceEligible);
        Assert.NotNull(claims.ExpiresAt);
    }

    [Fact]
    public void TokenParser_MissingGraceEligible_DefaultsToTrue()
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new { exp = 9999999999L });
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var fakeJwt = $"header.{encoded}.signature";

        var claims = LicenseTokenParser.TryParse(fakeJwt);

        Assert.NotNull(claims);
        Assert.True(claims!.GraceEligible);
    }

    [Fact]
    public void Snapshot_JustExpired_GraceNotStarted_StillLogOnly()
    {
        // Expired 1 minute ago, grace eligible but grace never started (service hasn't run yet)
        // Without a graceStartedAt, the system has no active grace period - falls to log-only
        var snapshot = LicenseStateSnapshot.Compute(
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            graceEligible: true,
            graceStartedAt: null);

        Assert.False(snapshot.IsActive);
        Assert.False(snapshot.IsInGrace);
        Assert.True(snapshot.LearningFrozen);
        Assert.True(snapshot.LogOnly);
    }

    [Fact]
    public void Snapshot_GraceStartedToday_IsInGrace_NotLogOnly()
    {
        // Grace started 2 hours ago - well within the 30-day window
        var graceStartedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var snapshot = LicenseStateSnapshot.Compute(
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1),
            graceEligible: true,
            graceStartedAt: graceStartedAt);

        Assert.False(snapshot.IsActive);
        Assert.True(snapshot.IsInGrace);
        Assert.True(snapshot.LearningFrozen);
        Assert.False(snapshot.LogOnly);
        Assert.NotNull(snapshot.GraceEndsAt);
        Assert.True(snapshot.GraceEndsAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Snapshot_GraceEndsAt_Is30DaysAfterGraceStart()
    {
        var graceStartedAt = DateTimeOffset.UtcNow.AddDays(-5);
        var snapshot = LicenseStateSnapshot.Compute(
            expiresAt: DateTimeOffset.UtcNow.AddDays(-5),
            graceEligible: true,
            graceStartedAt: graceStartedAt);

        Assert.NotNull(snapshot.GraceEndsAt);
        var expectedEnd = graceStartedAt.AddDays(30);
        Assert.Equal(expectedEnd, snapshot.GraceEndsAt!.Value, TimeSpan.FromSeconds(1));
    }
}
