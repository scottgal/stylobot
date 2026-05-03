using Mostlylucid.BotDetection.Compliance;
using Mostlylucid.BotDetection.Definitions.BotPatterns;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.SimulationPacks;
using YamlDotNet.Serialization;

namespace Mostlylucid.BotDetection.Orchestration.Manifests;

/// <summary>
/// Static YAML serialization context for AOT-compatible manifest parsing.
/// Uses source generation to eliminate runtime reflection.
/// </summary>
[YamlStaticContext]
[YamlSerializable(typeof(DetectorManifest))]
[YamlSerializable(typeof(SignalScope))]
[YamlSerializable(typeof(TaxonomyConfig))]
[YamlSerializable(typeof(TriggerConfig))]
[YamlSerializable(typeof(SignalRequirement))]
[YamlSerializable(typeof(EmissionConfig))]
[YamlSerializable(typeof(SignalDefinition))]
[YamlSerializable(typeof(ConditionalSignal))]
[YamlSerializable(typeof(ListenConfig))]
[YamlSerializable(typeof(EscalationConfig))]
[YamlSerializable(typeof(EscalationRule))]
[YamlSerializable(typeof(EscalationCondition))]
[YamlSerializable(typeof(LaneConfig))]
[YamlSerializable(typeof(BudgetConfig))]
[YamlSerializable(typeof(ConfigBindings))]
[YamlSerializable(typeof(ConfigBinding))]
[YamlSerializable(typeof(DetectorDefaults))]
[YamlSerializable(typeof(WeightDefaults))]
[YamlSerializable(typeof(ConfidenceDefaults))]
[YamlSerializable(typeof(TimingDefaults))]
[YamlSerializable(typeof(FeatureDefaults))]
[YamlSerializable(typeof(PipelineManifest))]
[YamlSerializable(typeof(List<string>))]
[YamlSerializable(typeof(List<SignalRequirement>))]
[YamlSerializable(typeof(List<SignalDefinition>))]
[YamlSerializable(typeof(List<ConditionalSignal>))]
[YamlSerializable(typeof(List<EscalationCondition>))]
[YamlSerializable(typeof(List<ConfigBinding>))]
[YamlSerializable(typeof(Dictionary<string, object>))]
[YamlSerializable(typeof(Dictionary<string, EscalationRule>))]
[YamlSerializable(typeof(Dictionary<string, LaneConfig>))]
// BotPatterns YAML types
[YamlSerializable(typeof(BotPatternFile))]
[YamlSerializable(typeof(BotPatternEntry))]
[YamlSerializable(typeof(List<BotPatternEntry>))]
// UA profile YAML types
[YamlSerializable(typeof(UaProfileFile))]
[YamlSerializable(typeof(UaProfileEntry))]
[YamlSerializable(typeof(CentroidDimensionEntry))]
[YamlSerializable(typeof(List<UaProfileEntry>))]
[YamlSerializable(typeof(Dictionary<string, CentroidDimensionEntry>))]
// SimulationPack YAML types
[YamlSerializable(typeof(SimulationPack))]
[YamlSerializable(typeof(PackHoneypotPath))]
[YamlSerializable(typeof(PackResponseTemplate))]
[YamlSerializable(typeof(PackResponseHints))]
[YamlSerializable(typeof(PackCveModule))]
[YamlSerializable(typeof(PackTimingProfile))]
[YamlSerializable(typeof(List<PackHoneypotPath>))]
[YamlSerializable(typeof(List<PackResponseTemplate>))]
[YamlSerializable(typeof(List<PackCveModule>))]
[YamlSerializable(typeof(Dictionary<string, string>))]
// CompliancePack YAML types
[YamlSerializable(typeof(CompliancePack))]
[YamlSerializable(typeof(RetentionPolicy))]
[YamlSerializable(typeof(AnonymizationPolicy))]
[YamlSerializable(typeof(DsarPolicy))]
[YamlSerializable(typeof(AuditPolicy))]
public partial class ManifestYamlContext : StaticContext
{
}
