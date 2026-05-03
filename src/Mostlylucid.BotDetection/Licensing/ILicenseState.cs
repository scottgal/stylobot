namespace Mostlylucid.BotDetection.Licensing;

/// <summary>
/// Represents the current state of a StyloBot license (FOSS or commercial).
/// Determines whether learning services are frozen and whether action policies are forced to log-only mode.
/// </summary>
public interface ILicenseState
{
    /// <summary>
    /// License is valid and not expired.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// License expired but within the 30-day grace window (once per account).
    /// </summary>
    bool IsInGrace { get; }

    /// <summary>
    /// True when !IsActive. Learning services skip all write operations.
    /// </summary>
    bool LearningFrozen { get; }

    /// <summary>
    /// True when expired and past grace. All action policies are forced to log-only.
    /// </summary>
    bool LogOnly { get; }

    DateTimeOffset? ExpiresAt { get; }
    DateTimeOffset? GraceEndsAt { get; }
}

/// <summary>
/// FOSS implementation: no license needed, always active.
/// </summary>
internal sealed class FossLicenseState : ILicenseState
{
    public bool IsActive => true;
    public bool IsInGrace => false;
    public bool LearningFrozen => false;
    public bool LogOnly => false;
    public DateTimeOffset? ExpiresAt => null;
    public DateTimeOffset? GraceEndsAt => null;
}
