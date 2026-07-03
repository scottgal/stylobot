using VYaml.Annotations;

namespace Mostlylucid.BotDetection.SiteProfiles;

/// <summary>
///     A per-stack honeypot config layer. Selected per request by
///     <see cref="ISiteProfileResolver"/> based on the request's <c>Host</c>
///     header. Modulates the honeypot subsystem ONLY -- detection still
///     runs the full pipeline on every request regardless of profile.
/// </summary>
/// <remarks>
///     <para>
///         A profile carries three lists into the honeypot decision:
///         <list type="bullet">
///             <item><see cref="HoneypotProfile.FrameworkPaths"/> -- paths
///                 that ARE real on this stack (e.g. <c>/wp-login.php</c> on
///                 WordPress). The honeypot exempt store consults this in
///                 addition to operator-supplied <c>ExemptPaths</c>. Skips
///                 Tier 2 elevation only; Tier 1 paths are still trapped.</item>
///             <item><see cref="HoneypotProfile.AdditionalTier1"/> -- paths
///                 promoted to Tier 1 honeypot status for THIS stack
///                 (framework-specific dotfiles, build artefacts, debug
///                 endpoints that should never be web-served).</item>
///             <item><see cref="HoneypotProfile.ElevatedTier2"/> -- ambiguous
///                 paths that are scanner-shaped on this stack but not in
///                 the global catalog.</item>
///         </list>
///     </para>
///     <para>
///         Profiles also surface <see cref="SuggestedPacks"/> as a cross-sell
///         in the dashboard -- "you're running WordPress, the wordpress
///         simulation pack would let us serve plausible fake responses".
///         Cosmetic only; the pack ecosystem is orthogonal to profiles.
///     </para>
/// </remarks>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class SiteProfile
{
    /// <summary>Stable identifier (e.g. <c>"wordpress"</c>, <c>"aspnet"</c>).</summary>
    public string Id { get; set; } = "";

    /// <summary>Human-readable name surfaced in the dashboard.</summary>
    public string Name { get; set; } = "";

    /// <summary>Honeypot-subsystem modulations for this profile.</summary>
    public HoneypotProfile Honeypot { get; set; } = new();

    /// <summary>Bot identifiers expected to be normal on this stack (display hint).</summary>
    public List<string> ExpectedBots { get; set; } = new();

    /// <summary>Suggested simulation packs for cross-sell display in the dashboard.</summary>
    public List<SuggestedPack> SuggestedPacks { get; set; } = new();

    /// <summary>
    ///     Optional per-site threshold overlay. Null (the default) means "inherit
    ///     from the next level up". Consumed by
    ///     <see cref="IEffectivePolicyResolver"/>, which merges global →
    ///     domain-profile → host-profile per-field.
    /// </summary>
    public SiteThresholdOverrides? Thresholds { get; set; }
}

/// <summary>Honeypot subsystem modulations carried by a site profile.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class HoneypotProfile
{
    /// <summary>
    ///     Paths that ARE legitimate on this stack. Added to the honeypot
    ///     exempt list so Tier 2 honeypot elevation is skipped. Tier 1 paths
    ///     are never exempted regardless. Detection still runs.
    /// </summary>
    public List<string> FrameworkPaths { get; set; } = new();

    /// <summary>Paths promoted to Tier 1 honeypot status on this stack.</summary>
    public List<string> AdditionalTier1 { get; set; } = new();

    /// <summary>Paths added as Tier 2 honeypot suggestions on this stack.</summary>
    public List<string> ElevatedTier2 { get; set; } = new();
}

/// <summary>Pack suggestion attached to a profile.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class SuggestedPack
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary><c>"foss"</c> or <c>"commercial"</c>.</summary>
    public string Tier { get; set; } = "foss";
}
