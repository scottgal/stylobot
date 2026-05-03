namespace Mostlylucid.BotDetection.Models;

/// <summary>
///     Configuration for response-time PII masking actions ("mask-pii", "strip-pii").
///     Disabled by default and only applied when explicitly enabled.
/// </summary>
public sealed class ResponsePiiMaskingOptions
{
    /// <summary>
    ///     Global feature flag for response PII masking.
    ///     Default: false (no response mutation, even if action policy marker is present).
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    ///     When enabled, auto-apply "mask-pii" to allowed-through malicious traffic above configured thresholds.
    ///     Only evaluated when <see cref="Enabled" /> is true.
    ///     Default: true.
    /// </summary>
    public bool AutoApplyForHighConfidenceMalicious { get; set; } = true;

    /// <summary>
    ///     Minimum bot probability required for auto-apply.
    ///     Range: 0.0-1.0. Default: 0.90.
    /// </summary>
    public double AutoApplyBotProbabilityThreshold { get; set; } = 0.90;

    /// <summary>
    ///     Minimum confidence required for auto-apply.
    ///     Range: 0.0-1.0. Default: 0.75.
    /// </summary>
    public double AutoApplyConfidenceThreshold { get; set; } = 0.75;
}
