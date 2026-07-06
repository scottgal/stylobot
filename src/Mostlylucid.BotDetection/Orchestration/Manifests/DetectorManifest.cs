using VYaml.Annotations;

namespace Mostlylucid.BotDetection.Orchestration.Manifests;

/// <summary>
/// Declarative detector manifest loaded from YAML.
/// Defines signal contracts for dynamic composition.
/// Maps BotDetection detectors to the unified taxonomy.
/// </summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class DetectorManifest
{
    public string Name { get; set; } = "";
    public int Priority { get; set; } = 50;
    public bool Enabled { get; set; } = true;
    public string? Description { get; set; }

    public SignalScope Scope { get; set; } = new();
    public TaxonomyConfig Taxonomy { get; set; } = new();
    public TriggerConfig Triggers { get; set; } = new();
    public EmissionConfig Emits { get; set; } = new();
    public ListenConfig Listens { get; set; } = new();
    public EscalationConfig? Escalation { get; set; }
    public LaneConfig Lane { get; set; } = new();
    public BudgetConfig? Budget { get; set; }
    public ConfigBindings Config { get; set; } = new();
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Default parameter values - no magic numbers!
    /// These are the baseline defaults loaded from YAML.
    /// Can be overridden via appsettings.json at runtime.
    /// </summary>
    public DetectorDefaults Defaults { get; set; } = new();
}

/// <summary>Signal scope context (three-level hierarchy from ephemeral patterns).</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class SignalScope
{
    public string Sink { get; set; } = "botdetection";
    public string Coordinator { get; set; } = "detection";
    public string Atom { get; set; } = "";
}

/// <summary>Taxonomy classification for the detector.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class TaxonomyConfig
{
    public string Kind { get; set; } = "sensor";
    public string Determinism { get; set; } = "deterministic";
    public string Persistence { get; set; } = "ephemeral";

    /// <summary>
    /// Optional concern grouping (identity, security, privacy, network, priors, ...).
    /// Distinct from <see cref="SignalScope.Atom"/>: many detectors can share a concern
    /// while each remains its own atom. Empty means no explicit concern.
    /// </summary>
    public string Concern { get; set; } = "";
}

/// <summary>Trigger conditions - when should this detector run?</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class TriggerConfig
{
    public List<SignalRequirement> Requires { get; set; } = new();
    public List<string> Signals { get; set; } = new();
    public List<string> SkipWhen { get; set; } = new();
}

/// <summary>Signal requirement with optional condition.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class SignalRequirement
{
    public string Signal { get; set; } = "";
    public string? Condition { get; set; }
    public object? Value { get; set; }
}

/// <summary>Signal emissions - what signals does this detector produce?</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class EmissionConfig
{
    public List<string> OnStart { get; set; } = new();
    public List<SignalDefinition> OnComplete { get; set; } = new();
    public List<string> OnFailure { get; set; } = new();
    public List<ConditionalSignal> Conditional { get; set; } = new();
}

/// <summary>Signal definition with type and metadata.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public partial class SignalDefinition
{
    public string Key { get; set; } = "";
    public string Type { get; set; } = "string";
    public string? Description { get; set; }
    public double[]? ConfidenceRange { get; set; }
}

/// <summary>Conditional signal emitted based on runtime conditions.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class ConditionalSignal
{
    public string Key { get; set; } = "";
    public string Type { get; set; } = "string";
    public string? Description { get; set; }
    public double[]? ConfidenceRange { get; set; }
    public string? When { get; set; }
}

/// <summary>Dependencies - signals this detector reads from other detectors.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class ListenConfig
{
    public List<string> Required { get; set; } = new();
    public List<string> Optional { get; set; } = new();
}

/// <summary>Escalation rules - when to defer to more powerful processing.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class EscalationConfig
{
    public Dictionary<string, EscalationRule> Targets { get; set; } = new();
}

/// <summary>Escalation rule with conditions.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class EscalationRule
{
    public List<EscalationCondition> When { get; set; } = new();
    public List<EscalationCondition> SkipWhen { get; set; } = new();
    public string? Description { get; set; }
}

/// <summary>Escalation condition.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class EscalationCondition
{
    public string Signal { get; set; } = "";
    public object? Value { get; set; }
    public string? Condition { get; set; }
}

/// <summary>Lane configuration for concurrency control.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class LaneConfig
{
    public string Name { get; set; } = "fast";
    public int MaxConcurrency { get; set; } = 8;
    public int Priority { get; set; } = 0;
}

/// <summary>Budget constraints for the detector.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class BudgetConfig
{
    public string? MaxDuration { get; set; }
    public int? MaxTokens { get; set; }
    public decimal? MaxCost { get; set; }
}

/// <summary>Configuration bindings.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class ConfigBindings
{
    public List<ConfigBinding> Bindings { get; set; } = new();
}

/// <summary>Single configuration binding.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class ConfigBinding
{
    public string ConfigKey { get; set; } = "";
    public bool SkipIfFalse { get; set; }
    public string? MapsTo { get; set; }
}

/// <summary>Pipeline manifest - assemblage of detectors.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class PipelineManifest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int Priority { get; set; } = 50;
    public bool Enabled { get; set; } = true;
    public List<string> Required { get; set; } = new();
    public List<string> Conditional { get; set; } = new();
    public string ExecutionMode { get; set; } = "parallel";
    public string? Timeout { get; set; }
    public Dictionary<string, LaneConfig> Lanes { get; set; } = new();
    public List<string> Tags { get; set; } = new();
}

/// <summary>
/// Default parameter values for a detector.
/// These come from YAML and can be overridden via appsettings.json.
/// Key principle: NO MAGIC NUMBERS in code - all configurable values here.
/// </summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class DetectorDefaults
{
    public WeightDefaults Weights { get; set; } = new();
    public ConfidenceDefaults Confidence { get; set; } = new();
    public TimingDefaults Timing { get; set; } = new();
    public FeatureDefaults Features { get; set; } = new();
    public Dictionary<string, object> Parameters { get; set; } = new();
}

/// <summary>Weight configuration for detector scoring.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class WeightDefaults
{
    public double Base { get; set; } = 1.0;
    public double BotSignal { get; set; } = 1.5;
    public double HumanSignal { get; set; } = 1.0;
    public double Verified { get; set; } = 2.0;
    public double EarlyExit { get; set; } = 3.0;
}

/// <summary>Confidence threshold configuration.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class ConfidenceDefaults
{
    public double Neutral { get; set; } = 0.0;
    public double BotDetected { get; set; } = 0.3;
    public double HumanIndicated { get; set; } = -0.1;
    public double StrongSignal { get; set; } = 0.7;
    public double HighThreshold { get; set; } = 0.8;
    public double LowThreshold { get; set; } = 0.3;
    public double EscalationThreshold { get; set; } = 0.5;
}

/// <summary>Timing and rate configuration.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class TimingDefaults
{
    public int TimeoutMs { get; set; } = 100;
    public int CacheRefreshSec { get; set; } = 3600;
    public int RateWindowSec { get; set; } = 60;
    public int MaxRequestsPerWindow { get; set; } = 100;
}

/// <summary>Feature flags for detector behavior.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class FeatureDefaults
{
    public bool DetailedLogging { get; set; } = false;
    public bool EnableCache { get; set; } = true;
    public bool CanEarlyExit { get; set; } = false;
    public bool CanEscalate { get; set; } = false;
}

/// <summary>
/// Signal contract extracted from manifest for LLM consumption.
/// Not YAML-loaded - constructed in code from manifests; keeps required+init.
/// </summary>
public sealed class DetectorSignalContract
{
    public required string DetectorName { get; init; }
    public required string Kind { get; init; }
    public required IReadOnlySet<string> Emits { get; init; }
    public required IReadOnlySet<string> Listens { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }

    public static DetectorSignalContract FromManifest(DetectorManifest manifest)
    {
        var emits = new HashSet<string>();
        emits.UnionWith(manifest.Emits.OnStart);
        emits.UnionWith(manifest.Emits.OnComplete.Select(s => s.Key));
        emits.UnionWith(manifest.Emits.OnFailure);
        emits.UnionWith(manifest.Emits.Conditional.Select(s => s.Key));

        var listens = new HashSet<string>();
        listens.UnionWith(manifest.Listens.Required);
        listens.UnionWith(manifest.Listens.Optional);
        listens.UnionWith(manifest.Triggers.Requires.Select(r => r.Signal));

        return new DetectorSignalContract
        {
            DetectorName = manifest.Name,
            Kind = manifest.Taxonomy.Kind,
            Emits = emits,
            Listens = listens,
            Tags = manifest.Tags
        };
    }

    public string ToDescription()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## {DetectorName} ({Kind})");
        sb.AppendLine();
        sb.AppendLine("**Emits:**");
        foreach (var signal in Emits.OrderBy(s => s))
            sb.AppendLine($"  - {signal}");
        sb.AppendLine();
        sb.AppendLine("**Listens:**");
        foreach (var signal in Listens.OrderBy(s => s))
            sb.AppendLine($"  - {signal}");
        if (Tags.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Tags:** {string.Join(", ", Tags)}");
        }
        sb.AppendLine();
        return sb.ToString();
    }
}
