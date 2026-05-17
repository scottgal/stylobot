using System.Runtime.CompilerServices;
using Mostlylucid.BotDetection.Compliance;
using Mostlylucid.BotDetection.Definitions.BotPatterns;
using Mostlylucid.BotDetection.Definitions.VendorHomeHosts;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.SimulationPacks;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection;

/// <summary>
///     Wires VYaml's <see cref="GeneratedResolver"/> ahead of <see cref="StandardResolver"/>
///     so the source-generated <c>[YamlObject]</c> formatters are discovered at runtime,
///     and force-registers every formatter so AOT trimming doesn't strip the
///     <c>__RegisterVYamlFormatter</c> entry points before <c>GeneratedResolver</c>'s
///     reflection lookup runs.
///
///     <para>
///     Why explicit registration: <see cref="GeneratedResolver"/> uses
///     <c>type.GetMethod("__RegisterVYamlFormatter")</c> to discover the per-type formatter
///     entry points. Under Native AOT, the trimmer treats those methods as unreachable
///     (the call is reflective, not a static reference), strips them, and the lookup
///     returns null - the resolver then throws "is not registered" the first time anything
///     tries to deserialize. Calling each entry point directly from this module initializer
///     keeps them as static call sites, which the trimmer must preserve.
///     </para>
///
///     <para>
///     Uses <see cref="ModuleInitializerAttribute"/> so the wiring happens before any
///     consumer touches <see cref="YamlSerializer"/>.
///     </para>
/// </summary>
internal static class VYamlBootstrap
{
    [ModuleInitializer]
    internal static void Init()
    {
        // Compliance pack
        CompliancePack.__RegisterVYamlFormatter();
        RetentionPolicy.__RegisterVYamlFormatter();
        AnonymizationPolicy.__RegisterVYamlFormatter();
        DsarPolicy.__RegisterVYamlFormatter();
        AuditPolicy.__RegisterVYamlFormatter();

        // Bot patterns
        BotPatternFile.__RegisterVYamlFormatter();
        BotPatternEntry.__RegisterVYamlFormatter();

        // Vendor-home hosts (UserAgentDiscriminator skiplist)
        VendorHomeHostsFile.__RegisterVYamlFormatter();

        // Identity archetypes
        IdentityArchetypeYaml.__RegisterVYamlFormatter();
        IdentityArchetypeDimensionYaml.__RegisterVYamlFormatter();

        // UA profiles
        UaProfileFile.__RegisterVYamlFormatter();
        UaProfileEntry.__RegisterVYamlFormatter();
        CentroidDimensionEntry.__RegisterVYamlFormatter();

        // Simulation packs
        SimulationPack.__RegisterVYamlFormatter();
        PackHoneypotPath.__RegisterVYamlFormatter();
        PackResponseTemplate.__RegisterVYamlFormatter();
        PackResponseHints.__RegisterVYamlFormatter();
        PackCveModule.__RegisterVYamlFormatter();
        PackTimingProfile.__RegisterVYamlFormatter();

        // Detector / pipeline manifests
        DetectorManifest.__RegisterVYamlFormatter();
        SignalScope.__RegisterVYamlFormatter();
        TaxonomyConfig.__RegisterVYamlFormatter();
        TriggerConfig.__RegisterVYamlFormatter();
        SignalRequirement.__RegisterVYamlFormatter();
        EmissionConfig.__RegisterVYamlFormatter();
        SignalDefinition.__RegisterVYamlFormatter();
        ConditionalSignal.__RegisterVYamlFormatter();
        ListenConfig.__RegisterVYamlFormatter();
        EscalationConfig.__RegisterVYamlFormatter();
        EscalationRule.__RegisterVYamlFormatter();
        EscalationCondition.__RegisterVYamlFormatter();
        LaneConfig.__RegisterVYamlFormatter();
        BudgetConfig.__RegisterVYamlFormatter();
        ConfigBindings.__RegisterVYamlFormatter();
        ConfigBinding.__RegisterVYamlFormatter();
        DetectorDefaults.__RegisterVYamlFormatter();
        WeightDefaults.__RegisterVYamlFormatter();
        ConfidenceDefaults.__RegisterVYamlFormatter();
        TimingDefaults.__RegisterVYamlFormatter();
        FeatureDefaults.__RegisterVYamlFormatter();
        PipelineManifest.__RegisterVYamlFormatter();

        // Pre-register the collection-formatter closed generics our YAML models use.
        // VYaml's StandardResolver constructs ListFormatter<T> / DictionaryFormatter<K,V>
        // via Activator.CreateInstance + Type.MakeGenericType, which fails under Native
        // AOT (no runtime code generation). Registering each closed generic explicitly
        // makes the AOT compiler emit the concrete formatter type so the lookup short-
        // circuits before StandardResolver tries the reflective path.
        GeneratedResolver.Register(new ListFormatter<BotPatternEntry>());
        GeneratedResolver.Register(new ListFormatter<UaProfileEntry>());
        GeneratedResolver.Register(new ListFormatter<SignalRequirement>());
        GeneratedResolver.Register(new ListFormatter<SignalDefinition>());
        GeneratedResolver.Register(new ListFormatter<ConditionalSignal>());
        GeneratedResolver.Register(new ListFormatter<EscalationCondition>());
        GeneratedResolver.Register(new ListFormatter<ConfigBinding>());
        GeneratedResolver.Register(new ListFormatter<PackHoneypotPath>());
        GeneratedResolver.Register(new ListFormatter<PackResponseTemplate>());
        GeneratedResolver.Register(new ListFormatter<PackCveModule>());
        GeneratedResolver.Register(new DictionaryFormatter<string, CentroidDimensionEntry>());
        GeneratedResolver.Register(new DictionaryFormatter<string, IdentityArchetypeDimensionYaml>());
        GeneratedResolver.Register(new DictionaryFormatter<string, EscalationRule>());
        GeneratedResolver.Register(new DictionaryFormatter<string, LaneConfig>());
        GeneratedResolver.Register(new DictionaryFormatter<string, object>());
        GeneratedResolver.Register(new DictionaryFormatter<string, string>());

        YamlSerializer.DefaultOptions = new YamlSerializerOptions
        {
            Resolver = CompositeResolver.Create(
                new IYamlFormatterResolver[]
                {
                    GeneratedResolver.Instance,
                    StandardResolver.Instance,
                })
        };
    }
}
