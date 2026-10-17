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
///         Secrets are masked to <c>"***"</c> via two mechanisms: a contract-resolver
///         modifier handles strongly-typed sections (so a new sensitive field can't
///         ship without redaction), and a value-time guard inside <c>BuildRootBucket</c>
///         catches the synthetic root dictionary (where the modifier can't see the
///         original property names). See <see cref="SecretNameRegex"/> for the rule.
///     </para>
/// </summary>
public static class EffectiveConfigSerializer
{
    public const string RootSectionId = "root";

    // Matches property names ending in any of these tokens (case-insensitive,
    // with optional plural "s"). The suffix anchor catches both compound names
    // (apiKey, signatureHashKey, accessToken) and the bare property names that
    // appear on ApiKey entries (Key, Secret). The optional trailing s catches
    // ApiBypassKeys / ApiKeys / Tokens / Passwords / Secrets -- the original
    // regex missed all of those because key$ doesn't match keys$.
    //
    // Defence in depth: properties holding sensitive material should also be
    // marked with [Secret] (see SecretAttribute) which redacts regardless of
    // name. The regex catches author oversight; [Secret] is the explicit
    // contract.
    private static readonly Regex SecretNameRegex =
        new("(?i)(secret|password|token|key)s?$", RegexOptions.Compiled);

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
        var complex = new List<ConfigSectionInfo>();
        foreach (var prop in typeof(BotDetectionOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
            if (IsLeafType(prop.PropertyType)) continue;
            if (prop.GetCustomAttribute<ObsoleteAttribute>() is not null) continue;

            complex.Add(new ConfigSectionInfo(prop.Name, prop.Name, FormatTypeName(prop.PropertyType)));
        }
        complex.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        var result = new List<ConfigSectionInfo>(complex.Count + 1)
        {
            new(RootSectionId, "Root settings", null)
        };
        result.AddRange(complex);
        return result;
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
    ///     the user can see top-level posture (EnableTestMode, NonAiMaxProbability,
    ///     allow flags, etc.) without scrolling past 30 nested objects.
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
            if (value is not null && IsSecretProperty(prop))
                value = RedactValue(value, prop.PropertyType);

            dict[key] = value;
        }
        return dict;
    }

    /// <summary>
    ///     True when the property is sensitive: it carries <see cref="SecretAttribute"/>,
    ///     OR its name matches the suffix regex AND its type is a credential-bearing
    ///     scalar (string, byte[], Guid) or a collection of one. The name+type combination
    ///     was the original rule; the attribute is the explicit contract that survives
    ///     renames and catches the plural-collection case the regex used to miss.
    /// </summary>
    private static bool IsSecretProperty(PropertyInfo prop)
    {
        if (prop.GetCustomAttribute<SecretAttribute>() is not null) return true;
        if (!SecretNameRegex.IsMatch(prop.Name)) return false;
        return IsSensitiveValueType(prop.PropertyType)
               || IsSensitiveCollectionType(prop.PropertyType);
    }

    /// <summary>
    ///     Replace a sensitive value with a redaction sentinel. Scalars become
    ///     <c>"***"</c>. Collections become a same-shape collection of <c>"***"</c>
    ///     entries so the operator can still see "how many keys are configured"
    ///     without exposing what they are. Dictionary values follow the same rule.
    /// </summary>
    private static object RedactValue(object value, Type propertyType)
    {
        var t = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (IsSensitiveValueType(t)) return "***";
        if (value is IDictionary dict)
        {
            var masked = new Dictionary<string, string>(dict.Count, StringComparer.Ordinal);
            foreach (var key in dict.Keys) masked[key?.ToString() ?? ""] = "***";
            return masked;
        }
        if (value is IEnumerable enumerable && value is not string)
        {
            var list = new List<string>();
            foreach (var _ in enumerable) list.Add("***");
            return list;
        }
        return "***";
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
            var member = prop.AttributeProvider as MemberInfo;
            var hasSecretAttr = member?.GetCustomAttribute<SecretAttribute>() is not null;
            // prop.Name has already been through the naming policy. Match against the
            // underlying member name as a defensive belt-and-braces.
            var underlying = member?.Name ?? prop.Name;
            var nameMatches = SecretNameRegex.IsMatch(prop.Name) || SecretNameRegex.IsMatch(underlying);

            if (!hasSecretAttr && !nameMatches) continue;

            // [Secret] is the explicit contract: redact regardless of type. The
            // name-regex path is defence in depth and still requires a credential-
            // bearing type so that boolean flags ("RequireApiKey") don't get masked
            // to "***" -- that loses information for no security gain.
            if (!hasSecretAttr
                && !IsSensitiveValueType(prop.PropertyType)
                && !IsSensitiveCollectionType(prop.PropertyType))
                continue;

            prop.CustomConverter = MaskingConverters.ForType(prop.PropertyType);
        }
    }

    private static bool IsSensitiveValueType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t == typeof(string) || t == typeof(byte[]) || t == typeof(Guid);
    }

    /// <summary>
    ///     True when <paramref name="t"/> is a collection of sensitive scalars:
    ///     <c>List&lt;string&gt;</c>, <c>string[]</c>, <c>IEnumerable&lt;string&gt;</c>,
    ///     <c>Dictionary&lt;string, string&gt;</c>, etc. Catches the
    ///     <c>ApiBypassKeys</c> / <c>ApiKeys</c> shape that bypassed the
    ///     scalar-only regex check before this fix.
    /// </summary>
    private static bool IsSensitiveCollectionType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(string)) return false; // string is enumerable; don't recurse into chars
        if (typeof(IDictionary).IsAssignableFrom(t))
        {
            var args = t.IsGenericType ? t.GetGenericArguments() : null;
            // Mask dictionaries whose value type is sensitive scalar.
            return args is { Length: 2 } && IsSensitiveValueType(args[1]);
        }
        if (typeof(IEnumerable).IsAssignableFrom(t))
        {
            var elem = GetEnumerableElement(t);
            return elem is not null && IsSensitiveValueType(elem);
        }
        return false;
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
///     <c>"***"</c> for scalars, or with a same-shape collection of <c>"***"</c>
///     entries for lists / dictionaries. Reads pass through to the default reader
///     because the dashboard never round-trips redacted JSON back into options.
/// </summary>
internal sealed class MaskingConverter<T> : JsonConverter<T>
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        reader.Skip();
        return default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        // Collections become a same-shape mask so operators can see "n keys
        // configured" without exposing what they are.
        if (value is System.Collections.IDictionary dict)
        {
            writer.WriteStartObject();
            foreach (var key in dict.Keys)
                writer.WriteString(key?.ToString() ?? "", "***");
            writer.WriteEndObject();
            return;
        }
        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            writer.WriteStartArray();
            foreach (var _ in enumerable) writer.WriteStringValue("***");
            writer.WriteEndArray();
            return;
        }
        writer.WriteStringValue("***");
    }
}
