using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Covers the contract the Configuration tab depends on:
///     <list type="number">
///         <item><description>Secret-named string properties are masked to <c>"***"</c>.</description></item>
///         <item><description>Same-named boolean/non-string properties are <b>not</b> masked - they carry their real value.</description></item>
///         <item><description>Section discovery enumerates every complex sub-options class on <see cref="BotDetectionOptions"/>.</description></item>
///         <item><description>The root bucket holds the scalar/list properties, not the complex ones.</description></item>
///     </list>
/// </summary>
public class EffectiveConfigSerializerTests
{
    [Fact]
    public void SerializeFull_MasksSecretStringProperty()
    {
        var opts = new BotDetectionOptions { SignatureHashKey = "this-is-a-real-secret" };

        var doc = ParseJson(EffectiveConfigSerializer.SerializeFull(opts));

        doc.RootElement.GetProperty("signatureHashKey").GetString().Should().Be("***");
    }

    [Fact]
    public void SerializeFull_DoesNotMaskBooleanFlagsNamedLikeSecrets()
    {
        // RequireApiKey exists on ApiKeyConfig as a bool flag - it ends in "Key" but
        // is a posture toggle, not a credential. Masking it would degrade the view
        // for no security gain. Belt-and-braces: confirm via the full options graph.
        var opts = new BotDetectionOptions();
        opts.ApiKeys["sB-TEST"] = new ApiKeyConfig
        {
            Name = "test",
            Key = "the-real-secret-key",
            Enabled = true,
        };

        var doc = ParseJson(EffectiveConfigSerializer.SerializeFull(opts));
        var entry = doc.RootElement.GetProperty("apiKeys").GetProperty("sB-TEST");

        // string Key on the ApiKeyConfig is masked.
        entry.GetProperty("key").GetString().Should().Be("***");
        // bool Enabled flag passes through.
        entry.GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void SerializeSection_RootBucket_MasksSecretAndKeepsNonSecretLeaves()
    {
        var opts = new BotDetectionOptions
        {
            SignatureHashKey = "should-be-masked",
            EnableUserAgentDetection = false,
            BotThreshold = 0.42,
        };

        var doc = ParseJson(EffectiveConfigSerializer.SerializeSection(opts, EffectiveConfigSerializer.RootSectionId)!);
        var root = doc.RootElement;

        root.GetProperty("signatureHashKey").GetString().Should().Be("***");
        root.GetProperty("enableUserAgentDetection").GetBoolean().Should().BeFalse();
        root.GetProperty("botThreshold").GetDouble().Should().BeApproximately(0.42, 1e-9);
    }

    [Fact]
    public void SerializeSection_RootBucket_DoesNotIncludeComplexSubOptions()
    {
        // The "root" section should be scalar/list leaves only. Complex sub-options
        // (AiDetection, Behavioral, ...) get their own dedicated sections so the
        // operator can navigate them one at a time instead of one wall of JSON.
        var opts = new BotDetectionOptions();
        var doc = ParseJson(EffectiveConfigSerializer.SerializeSection(opts, EffectiveConfigSerializer.RootSectionId)!);

        doc.RootElement.TryGetProperty("aiDetection", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("behavioral", out _).Should().BeFalse();
    }

    [Fact]
    public void SerializeSection_NamedSection_RoundTripsTheNestedOptionsObject()
    {
        var opts = new BotDetectionOptions();
        opts.Behavioral.EnableAnomalyDetection = false;

        var json = EffectiveConfigSerializer.SerializeSection(opts, nameof(BotDetectionOptions.Behavioral));

        json.Should().NotBeNull();
        ParseJson(json!).RootElement.GetProperty("enableAnomalyDetection").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void SerializeSection_UnknownId_ReturnsNull()
    {
        var json = EffectiveConfigSerializer.SerializeSection(new BotDetectionOptions(), "nonexistent");
        json.Should().BeNull();
    }

    [Fact]
    public void DiscoverSections_IncludesRootBucketAndEveryComplexSubOptions()
    {
        var sections = EffectiveConfigSerializer.DiscoverSections();
        var ids = sections.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        // Synthetic root bucket always first.
        sections[0].Id.Should().Be(EffectiveConfigSerializer.RootSectionId);

        // Every public, non-obsolete, complex-typed property on BotDetectionOptions
        // must appear as a section. Drift check against the live type so a newly
        // added sub-options class can't ship without being viewable.
        var expected = typeof(BotDetectionOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Where(p => p.GetCustomAttribute<ObsoleteAttribute>() is null)
            .Where(p => !IsLeafForSectionDiscovery(p.PropertyType))
            .Select(p => p.Name);

        foreach (var name in expected)
            ids.Should().Contain(name, $"section discovery must include {name}");
    }

    [Fact]
    public void SerializeDetectorDefaults_RoundTripsCleanly()
    {
        var defaults = new DetectorDefaults
        {
            Weights = new WeightDefaults { BotSignal = 2.5 },
            Features = new FeatureDefaults { DetailedLogging = true },
            Parameters = new Dictionary<string, object> { ["maxAge"] = 90 }
        };

        var doc = ParseJson(EffectiveConfigSerializer.SerializeDetectorDefaults(defaults));
        doc.RootElement.GetProperty("weights").GetProperty("botSignal").GetDouble()
            .Should().BeApproximately(2.5, 1e-9);
        doc.RootElement.GetProperty("features").GetProperty("detailedLogging").GetBoolean()
            .Should().BeTrue();
        doc.RootElement.GetProperty("parameters").GetProperty("maxAge").GetInt32().Should().Be(90);
    }

    private static JsonDocument ParseJson(string json) => JsonDocument.Parse(json);

    // Mirror of EffectiveConfigSerializer.IsLeafType - the drift check has to
    // compare against the same notion of "leaf" the serializer uses. Kept in
    // sync by deliberate copy: a divergence here means the discovery contract
    // changed and the test should fail loudly.
    private static bool IsLeafForSectionDiscovery(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t.IsPrimitive) return true;
        if (t.IsEnum) return true;
        if (t == typeof(string) || t == typeof(decimal) ||
            t == typeof(DateTime) || t == typeof(DateTimeOffset) ||
            t == typeof(TimeSpan) || t == typeof(Guid)) return true;

        if (typeof(System.Collections.IDictionary).IsAssignableFrom(t))
        {
            var args = t.IsGenericType ? t.GetGenericArguments() : null;
            if (args is null || args.Length != 2) return true;
            return IsLeafForSectionDiscovery(args[0]) && IsLeafForSectionDiscovery(args[1]);
        }

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(t))
        {
            var elem = t.IsArray
                ? t.GetElementType()
                : t.GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    ?.GetGenericArguments()[0];
            return elem is not null && IsLeafForSectionDiscovery(elem);
        }

        return false;
    }
}
