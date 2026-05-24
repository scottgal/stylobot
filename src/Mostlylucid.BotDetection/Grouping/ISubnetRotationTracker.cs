namespace Mostlylucid.BotDetection.Grouping;

/// <summary>
///     Counter that decides when a <c>/24</c> subnet looks like
///     "one actor rotating IPs / UAs" vs ordinary shared-NAT traffic.
/// </summary>
public interface ISubnetRotationTracker
{
    /// <summary>
    ///     Record a bot-shaped signature observation. Returns the
    ///     formed rotation group if the subnet has crossed both
    ///     thresholds; null when below or when the row failed the
    ///     UA-diversity / country-constancy check.
    /// </summary>
    SubnetRotationGroup? RecordAndCheck(
        string subnet,
        string signature,
        string? uaFamily,
        string? countryCode,
        int minSignatures,
        int minUaFamilies);

    /// <summary>Diagnostic snapshot for the dashboard / tests.</summary>
    RotationSnapshot? Peek(string subnet);
}

/// <summary>
///     Result of crossing the rotation thresholds. Surfaced to the
///     dashboard as the collapse-reason chip.
/// </summary>
public sealed record SubnetRotationGroup(
    int SignatureCount,
    int UaFamilyCount,
    string? Country);

public sealed record RotationSnapshot(
    int SignatureCount,
    int UaFamilyCount,
    int CountryCount);
