using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Manifests;

/// <summary>
/// Base class for contributing detectors that read their configuration from YAML manifests.
/// Provides easy access to weights, confidence values, and parameters - NO MAGIC NUMBERS in code!
/// </summary>
/// <remarks>
/// Configuration hierarchy (highest precedence first):
/// 1. appsettings.json BotDetection:Detectors:{Name}:Defaults:*
/// 2. YAML manifest defaults (from *.detector.yaml)
/// 3. Built-in code defaults
///
/// Contributors should use the Config property to access all configurable values.
/// </remarks>
public abstract class ConfiguredContributorBase : ContributingDetectorBase
{
    private readonly IDetectorConfigProvider _configProvider;
    private DetectorDefaults? _cachedConfig;
    private DetectorManifest? _cachedManifest;

    protected ConfiguredContributorBase(IDetectorConfigProvider configProvider)
    {
        _configProvider = configProvider;
    }

    /// <summary>
    /// The manifest name to load configuration from.
    /// Override if your detector name doesn't match the manifest name + "Contributor".
    /// </summary>
    protected virtual string ManifestName => $"{Name}Contributor";

    /// <summary>
    /// Get the resolved configuration (YAML + appsettings overrides).
    /// </summary>
    protected DetectorDefaults Config => _cachedConfig ??= _configProvider.GetDefaults(ManifestName);

    /// <summary>
    /// Get the raw manifest (for signal contracts, triggers, etc.)
    /// </summary>
    protected DetectorManifest? Manifest => _cachedManifest ??= _configProvider.GetManifest(ManifestName);

    // ===== Weight Shortcuts =====

    /// <summary>Base weight for contributions.</summary>
    protected double WeightBase => Config.Weights.Base;

    /// <summary>Weight multiplier for bot signals.</summary>
    protected double WeightBotSignal => Config.Weights.BotSignal;

    /// <summary>Weight multiplier for human signals.</summary>
    protected double WeightHumanSignal => Config.Weights.HumanSignal;

    /// <summary>Weight multiplier for verified patterns.</summary>
    protected double WeightVerified => Config.Weights.Verified;

    // ===== Confidence Shortcuts =====

    /// <summary>Confidence delta when no signal detected.</summary>
    protected double ConfidenceNeutral => Config.Confidence.Neutral;

    /// <summary>Confidence delta when bot detected.</summary>
    protected double ConfidenceBotDetected => Config.Confidence.BotDetected;

    /// <summary>Confidence delta for human indicators.</summary>
    protected double ConfidenceHumanIndicated => Config.Confidence.HumanIndicated;

    /// <summary>Confidence delta for strong signals.</summary>
    protected double ConfidenceStrongSignal => Config.Confidence.StrongSignal;

    /// <summary>High confidence threshold.</summary>
    protected double ThresholdHigh => Config.Confidence.HighThreshold;

    /// <summary>Low confidence threshold.</summary>
    protected double ThresholdLow => Config.Confidence.LowThreshold;

    /// <summary>Escalation threshold.</summary>
    protected double ThresholdEscalation => Config.Confidence.EscalationThreshold;

    // ===== Timing Shortcuts =====

    /// <summary>Execution timeout from config.</summary>
    public override TimeSpan ExecutionTimeout =>
        TimeSpan.FromMilliseconds(Config.Timing.TimeoutMs);

    // ===== Feature Shortcuts =====

    /// <summary>Whether detailed logging is enabled.</summary>
    protected bool DetailedLogging => Config.Features.DetailedLogging;

    /// <summary>Whether caching is enabled.</summary>
    protected bool CacheEnabled => Config.Features.EnableCache;

    /// <summary>Whether this detector can trigger early exit.</summary>
    protected bool CanTriggerEarlyExit => Config.Features.CanEarlyExit;

    /// <summary>Whether this detector can escalate to AI.</summary>
    protected bool CanEscalateToAi => Config.Features.CanEscalate;

    // ===== Parameter Access =====

    /// <summary>
    /// Get a typed parameter from the manifest.
    /// </summary>
    protected T GetParam<T>(string name, T defaultValue)
    {
        return _configProvider.GetParameter(ManifestName, name, defaultValue);
    }

    /// <summary>
    /// Get a list parameter from the manifest.
    /// </summary>
    protected IReadOnlyList<string> GetStringListParam(string name)
    {
        if (Config.Parameters.TryGetValue(name, out var value))
        {
            if (value is IEnumerable<object> enumerable)
                return enumerable.Select(x => x?.ToString() ?? "").ToList();
            if (value is IEnumerable<string> strings)
                return strings.ToList();
        }
        return Array.Empty<string>();
    }

    // ===== Contribution Helpers with Config =====

    /// <summary>
    /// Create a bot contribution using configured weights and confidence.
    /// </summary>
    protected DetectionContribution BotContribution(
        string category,
        string reason,
        double? confidenceOverride = null,
        double? weightMultiplier = null,
        string? botType = null,
        string? botName = null)
    {
        return DetectionContribution.Bot(
            Name,
            category,
            confidenceOverride ?? ConfidenceBotDetected,
            reason,
            weight: WeightBase * (weightMultiplier ?? WeightBotSignal),
            botType: botType,
            botName: botName);
    }

    /// <summary>
    /// Create a strong bot contribution using configured weights.
    /// </summary>
    protected DetectionContribution StrongBotContribution(
        string category,
        string reason,
        string? botType = null,
        string? botName = null)
    {
        return DetectionContribution.Bot(
            Name,
            category,
            ConfidenceStrongSignal,
            reason,
            weight: WeightBase * WeightVerified,
            botType: botType,
            botName: botName);
    }

    /// <summary>
    /// Create a human indicator contribution using configured weights.
    /// </summary>
    protected DetectionContribution HumanContribution(
        string category,
        string reason,
        double? weightMultiplier = null)
    {
        return new DetectionContribution
        {
            DetectorName = Name,
            Category = category,
            ConfidenceDelta = ConfidenceHumanIndicated,
            Weight = WeightBase * (weightMultiplier ?? WeightHumanSignal),
            Reason = reason
        };
    }

    /// <summary>
    /// Create a neutral contribution (no strong signal either way).
    /// </summary>
    protected DetectionContribution NeutralContribution(string category, string reason)
    {
        return DetectionContribution.Info(Name, category, reason);
    }
}
