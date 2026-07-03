using VYaml.Annotations;

namespace Mostlylucid.BotDetection.SiteProfiles;

/// <summary>
///     Per-site nullable threshold overlay. Attached to a <see cref="SiteProfile"/>
///     via <see cref="SiteProfile.Thresholds"/>. Each field is nullable so the
///     resolver walks global → domain-profile → host-profile and lets the deepest
///     non-null value win per field. Fields map 1:1 to properties on
///     <see cref="Models.BotDetectionOptions"/> (top-level or nested); the
///     field names here are copied verbatim from that class rather than invented.
/// </summary>
/// <remarks>
///     <para>
///         The task specified a triad of "BotThreshold / ChallengeBand /
///         AllowBand", but <c>BotDetectionOptions</c> does not carry
///         <c>AllowBand</c> or <c>ChallengeBand</c> literal fields. The nearest
///         semantic equivalents on the existing options graph are:
///         <list type="bullet">
///             <item><see cref="BotThreshold"/> — direct match; the "counts as
///                 a bot" cut. Still on <c>BotDetectionOptions</c> though
///                 marked obsolete in favour of the per-policy field.</item>
///             <item><see cref="HumanCeiling"/> — probability below this is
///                 classified Human. Nearest equivalent to an "allow band"
///                 boundary. Sits on
///                 <c>BotDetectionOptions.Classification</c>.</item>
///             <item><see cref="BotFloor"/> — probability at or above this is
///                 classified Bot. The classification-side companion to
///                 <see cref="BotThreshold"/>. Sits on
///                 <c>BotDetectionOptions.Classification</c>.</item>
///         </list>
///     </para>
///     <para>
///         This type only STORES the overlay. Consumers migrate to reading
///         <see cref="EffectiveThresholds"/> off
///         <c>HttpContext.Items[HttpContextItemKeys.EffectiveThresholds]</c>
///         in follow-up work; detection code still reads
///         <c>IOptionsMonitor&lt;BotDetectionOptions&gt;</c> today.
///     </para>
/// </remarks>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class SiteThresholdOverrides
{
    /// <summary>
    ///     Overrides <c>BotDetectionOptions.BotThreshold</c>. Null = inherit
    ///     from the next level up (domain profile → global).
    /// </summary>
    public double? BotThreshold { get; set; }

    /// <summary>
    ///     Overrides <c>BotDetectionOptions.Classification.HumanCeiling</c>.
    ///     Null = inherit. Below this probability, a request is classified
    ///     Human ("allow band" boundary).
    /// </summary>
    public double? HumanCeiling { get; set; }

    /// <summary>
    ///     Overrides <c>BotDetectionOptions.Classification.BotFloor</c>.
    ///     Null = inherit. At or above this probability, a request is
    ///     classified Bot.
    /// </summary>
    public double? BotFloor { get; set; }
}