using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Serializes <see cref="BotDetectionOptions"/> and per-detector defaults to JSON for
///     the dashboard's Configuration tab. Discovers sections by reflecting the public
///     properties of <see cref="BotDetectionOptions"/>: complex sub-options classes
///     become their own section (so a 4000-line options class doesn't render as one
///     wall of JSON), leaf properties (scalars, enums, primitive collections) all live
///     under the synthetic "root" section.
///     <para>
///         Secrets — anything whose property name matches
///         <c>(?i)(secret|password|api[_-]?key|license[_-]?key|token|hash[_-]?key)</c>
///         — are masked to <c>"***"</c> via a contract resolver modifier so a new
///         sensitive field can't ship without redaction.
///     </para>
/// </summary>
public static class EffectiveConfigSerializer
{
    public const string RootSectionId = "root";

    // Matches property names ending in any of these tokens (case-insensitive). The
    // suffix anchor catches both compound names (apiKey, signatureHashKey, accessToken)
    // and the bare property names that appear on ApiKey entries (Key, Secret). Using
    // a suffix anchor avoids over-masking neutral identifiers like "keyName" or
    // "keystoneServer".
    private static readonly Regex SecretNameRegex =
        new("(?i)(secret|password|token|key)$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions RedactedOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { ApplySecretRedaction }
        },
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    ///     Enumerate the sections that should appear in the dashboard rail. The order
    ///     puts the synthetic <c>root</c> bucket first, then complex sections sorted
    ///     by display name so the rail is alphabetised.
    /// </summary>
    public static IReadOnlyList<ConfigSectionInfo> DiscoverSections()
    {
        var list = new List<ConfigSectionInfo>
        {
            new(RootSectionId, "Root settings", null)
        };

        foreach (var prop in typeof(BotDetectionOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
            if (IsLeafType(prop.PropertyType)) continue;
            if (prop.GetCustomAttribute<ObsoleteAttribute>() is not null) continue;

            list.Add(new ConfigSectionInfo(prop.Name, prop.Name, FormatTypeName(prop.PropertyType)));
        }

        return list
            .Take(1)
            .Concat(list.Skip(1).OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Serialize a single section by id. Returns null for an unknown id.</summary>
    public static string? SerializeSection(BotDetectionOptions options, string sectionId)
    {
        if (string.Equals(sectionId, RootSectionId, StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(BuildRootBucket(options), RedactedOptions);

        var prop = typeof(BotDetectionOptions)
            .GetProperty(sectionId, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop is null || !prop.CanRead || IsLeafType(prop.PropertyType)) return null;

        var value = prop.GetValue(options);
        return JsonSerializer.Serialize(value, prop.PropertyType, RedactedOptions);
    }

    /// <summary>Serialize the full options object. Used by the download button.</summary>
    public static string SerializeFull(BotDetectionOptions options) =>
        JsonSerializer.Serialize(options, RedactedOptions);

    /// <summary>Serialize the effective <see cref="DetectorDefaults"/> for a single detector.</summary>
    public static string SerializeDetectorDefaults(DetectorDefaults defaults) =>
        JsonSerializer.Serialize(defaults, RedactedOptions);

    /// <summary>
    ///     Build a dictionary holding just the leaf-typed properties of
    ///     <see cref="BotDetectionOptions"/>. Used as the "root" section payload so
    ///     the user can see top-level posture (BlockDetectedBots, BotThreshold, etc.)
    ///     without scrolling past 30 nested objects.
    /// </summary>
    private static IDictionary<string, object?> BuildRootBucket(BotDetectionOptions options)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in typeof(BotDetectionOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
            if (!IsLeafType(prop.PropertyType)) continue;
            if (prop.GetCustomAttribute<ObsoleteAttribute>() is not null) continue;

            var key = JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
            var value = prop.GetValue(options);

            // The contract-resolver redaction modifier only sees strongly-typed
            // properties. Once we hand the serializer a Dictionary<string, object?>
            // the type info is "object" and the modifier never fires. So apply the
            // mask here, at the moment we still know the underlying property name.
            if (value is not null
                && SecretNameRegex.IsMatch(prop.Name)
                && IsSensitiveValueType(prop.PropertyType))
                value = "***";

            dict[key] = value;
        }
        return dict;
    }

    /// <summary>
    ///     A "leaf" property goes in the root bucket; anything else gets its own section.
    ///     Leaves: primitives, strings, enums, <c>DateTime</c>/<c>TimeSpan</c>/<c>Guid</c>,
    ///     plus collections whose element is itself a leaf (string lists, int arrays, etc.).
    /// </summary>
    private static bool IsLeafType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t.IsPrimitive) return true;
        if (t.IsEnum) return true;
        if (t == typeof(string) || t == typeof(decimal) ||
            t == typeof(DateTime) || t == typeof(DateTimeOffset) ||
            t == typeof(TimeSpan) || t == typeof(Guid)) return true;

        if (typeof(IDictionary).IsAssignableFrom(t)) return IsLeafDictionary(t);
        if (typeof(IEnumerable).IsAssignableFrom(t))
        {
            var elem = GetEnumerableElement(t);
            return elem is not null && IsLeafType(elem);
        }
        return false;
    }

    private static bool IsLeafDictionary(Type t)
    {
        var args = t.IsGenericType ? t.GetGenericArguments() : null;
        if (args is null || args.Length != 2) return true; // non-generic dict: treat as leaf-ish
        return IsLeafType(args[0]) && IsLeafType(args[1]);
    }

    /// <summary>Render a Type as a human-friendly tooltip string (no `n suffixes).</summary>
    private static string FormatTypeName(Type t)
    {
        if (!t.IsGenericType) return t.Name;
        var name = t.Name;
        var tick = name.IndexOf('`');
        if (tick > 0) name = name[..tick];
        var args = string.Join(", ", t.GetGenericArguments().Select(FormatTypeName));
        return $"{name}<{args}>";
    }

    private static Type? GetEnumerableElement(Type t)
    {
        if (t.IsArray) return t.GetElementType();
        var iface = t.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return iface?.GetGenericArguments()[0];
    }

    private static void ApplySecretRedaction(JsonTypeInfo ti)
    {
        foreach (var prop in ti.Properties)
        {
            // prop.Name has already been through the naming policy. Match against the
            // underlying member name as a defensive belt-and-braces.
            var underlying = (prop.AttributeProvider as MemberInfo)?.Name ?? prop.Name;
            if (!SecretNameRegex.IsMatch(prop.Name) && !SecretNameRegex.IsMatch(underlying))
                continue;

            // Only mask types that could actually hold a credential. Boolean flags
            // like RequireApiKey end in "Key" but are not secrets - rendering them
            // as "***" loses information for no security gain.
            if (!IsSensitiveValueType(prop.PropertyType))
                continue;

            prop.CustomConverter = MaskingConverters.ForType(prop.PropertyType);
        }
    }

    private static bool IsSensitiveValueType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t == typeof(string) || t == typeof(byte[]) || t == typeof(Guid);
    }
}

/// <summary>
///     Metadata for a config section shown in the dashboard rail. <see cref="Id"/> is
///     the URL-safe identifier used in API paths; <see cref="DisplayName"/> is what
///     renders to the operator.
/// </summary>
public sealed record ConfigSectionInfo(string Id, string DisplayName, string? TypeName = null);

/// <summary>Caches <c>MaskingConverter&lt;T&gt;</c> instances per closed property type.</summary>
internal static class MaskingConverters
{
    private static readonly ConcurrentDictionary<Type, JsonConverter> Cache = new();

    public static JsonConverter ForType(Type t)
        => Cache.GetOrAdd(t, type =>
        {
            var ctype = typeof(MaskingConverter<>).MakeGenericType(type);
            return (JsonConverter)Activator.CreateInstance(ctype)!;
        });
}

/// <summary>
///     Replaces the serialized value of a sensitive property with the literal
///     <c>"***"</c>. Reads pass through to the default reader because the dashboard
///     never round-trips redacted JSON back into options.
/// </summary>
internal sealed class MaskingConverter<T> : JsonConverter<T>
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        reader.Skip();
        return default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue("***");
}
