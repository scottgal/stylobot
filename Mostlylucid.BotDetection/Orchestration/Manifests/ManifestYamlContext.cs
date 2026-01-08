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
public partial class ManifestYamlContext : StaticContext
{
}
