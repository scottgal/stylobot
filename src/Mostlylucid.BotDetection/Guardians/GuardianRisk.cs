namespace Mostlylucid.BotDetection.Guardians;

/// <summary>
///     Shared risk-band helpers for data guardians that feed
///     <see cref="Storage.DecisionNecessity"/> scores. Extracted from the
///     old <c>VectorCompactionService.RiskBandToRisk</c> private method so
///     every guardian that ranks by risk can share the same mapping without
///     duplicating the table.
/// </summary>
internal static class GuardianRisk
{
    /// <summary>
    ///     Maps a stored RiskBand string to a threat weight in [0, 1] for the
    ///     eviction score. Unknown band (null or unrecognised) returns 0, which
    ///     causes the score to fall back to bot-probability only.
    /// </summary>
    public static double RiskBandToRisk(string? riskBand) => riskBand?.ToLowerInvariant() switch
    {
        "verylow"  => 0.05,
        "low"      => 0.15,
        "elevated" => 0.50,
        "medium"   => 0.50,
        "high"     => 0.85,
        "veryhigh" => 1.00,
        "verified" => 0.90,
        _          => 0.0
    };
}
