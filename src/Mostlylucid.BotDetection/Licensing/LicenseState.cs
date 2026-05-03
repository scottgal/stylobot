namespace Mostlylucid.BotDetection.Licensing;

/// <summary>
///     Thread-safe mutable holder for the current license state snapshot.
///     Updated by LicenseStateRefreshService every 60 seconds.
///     Registered as ILicenseState singleton when Licensing.Token is configured.
/// </summary>
internal sealed class LicenseState : ILicenseState
{
    private volatile LicenseStateSnapshot _snapshot = LicenseStateSnapshot.Foss;

    public bool IsActive => _snapshot.IsActive;
    public bool IsInGrace => _snapshot.IsInGrace;
    public bool LearningFrozen => _snapshot.LearningFrozen;
    public bool LogOnly => _snapshot.LogOnly;
    public DateTimeOffset? ExpiresAt => _snapshot.ExpiresAt;
    public DateTimeOffset? GraceEndsAt => _snapshot.GraceEndsAt;

    internal void Update(LicenseStateSnapshot snapshot) => _snapshot = snapshot;
}
