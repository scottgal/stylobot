using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Mostlylucid.BotDetection.Policies.Signals;

/// <summary>
///     Walks an assembly looking for the canonical <c>SignalKeys</c> class
///     (<c>Mostlylucid.BotDetection.Models.SignalKeys</c>) plus any partials,
///     and enumerates every <c>public const string</c> field on it. Each
///     entry pairs the field's reflected name (used to key XML doc lookup)
///     with the constant's value (used as the signal key).
///
///     <para>
///     The reflector is the only place in the catalogue that hard-codes the
///     <c>SignalKeys</c> type name. Detectors that want their signals to show
///     up in the editor simply add a <c>public const string</c> on
///     <c>SignalKeys</c> -- no curation step, no registration call.
///     </para>
/// </summary>
public static class SignalKeysReflector
{
    /// <summary>
    ///     Fully-qualified name of the canonical SignalKeys class. Match by
    ///     full name so a contributor pack cannot accidentally shadow the
    ///     vocabulary with its own type.
    /// </summary>
    public const string SignalKeysFullName = "Mostlylucid.BotDetection.Models.SignalKeys";

    /// <summary>One reflected entry per <c>public const string</c> field on the SignalKeys class.</summary>
    /// <param name="FieldName">The C# field name (e.g. <c>RiskJustification</c>) -- used for XML doc lookup.</param>
    /// <param name="KeyValue">The string the constant holds (e.g. <c>risk.justification</c>). The catalogue's primary key.</param>
    /// <param name="DeclaringType">The reflected type's full name. Carried into the descriptor for diagnostics.</param>
    public readonly record struct ReflectedSignal(string FieldName, string KeyValue, string DeclaringType);

    /// <summary>
    ///     Enumerate every <c>public const string</c> field on every type
    ///     in <paramref name="assembly"/> whose full name equals
    ///     <see cref="SignalKeysFullName"/>. Empty strings are skipped because
    ///     they cannot be a valid signal key.
    /// </summary>
    // The reflector is the one component in the catalogue that legitimately needs
    // type-name reflection. The set of consts on SignalKeys is open-ended and
    // contributor-extended; there's no way to source-generate this without
    // becoming the gating bottleneck the catalogue exists to remove.
    [RequiresUnreferencedCode("Reflects over the public const string fields of SignalKeys; if the type is trimmed, the catalogue ships empty.")]
    public static IEnumerable<ReflectedSignal> Reflect(Assembly assembly)
    {
        foreach (var type in SafeGetTypes(assembly))
        {
            if (type is null) continue;
            if (!string.Equals(type.FullName, SignalKeysFullName, StringComparison.Ordinal))
                continue;

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!field.IsLiteral || field.IsInitOnly) continue;
                if (field.FieldType != typeof(string)) continue;

                var value = field.GetRawConstantValue() as string;
                if (string.IsNullOrEmpty(value)) continue;

                yield return new ReflectedSignal(field.Name, value, type.FullName ?? SignalKeysFullName);
            }
        }
    }

    private static IEnumerable<Type?> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types may fail to load (missing optional deps); the SignalKeys
            // class is always present in the BotDetection assembly so this branch
            // is defensive belt-and-braces.
            return ex.Types;
        }
    }
}
