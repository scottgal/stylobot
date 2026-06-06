namespace Mostlylucid.BotDetection.Models;

/// <summary>
///     Marks a configuration / options property as holding sensitive material
///     (an API key, bypass token, signing secret, private key, license token,
///     proxy credential). The dashboard config serializer and any future
///     telemetry / export surface must replace the value with the redaction
///     sentinel before writing it to a caller-visible payload.
///     <para>
///     Applies to scalars (string, byte[], Guid) AND collections of those
///     (List&lt;string&gt;, Dictionary&lt;string, string&gt;), unlike the legacy
///     name-suffix regex which only caught singular string-typed leaves.
///     </para>
///     <para>
///     Why a marker over a name convention: regex name matching missed
///     <c>ApiBypassKeys</c> (plural) and <c>SecurityToolsOptions.ApiKeys</c>
///     because <c>key$</c> doesn't match <c>keys$</c>. A marker attribute
///     makes the intent explicit and survives field renames, type changes,
///     and refactors. The serializer still honours the regex as a defence-in-
///     depth fallback, but <c>[Secret]</c> is the canonical way to declare
///     "this must never be serialised in cleartext".
///     </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class SecretAttribute : Attribute
{
}
