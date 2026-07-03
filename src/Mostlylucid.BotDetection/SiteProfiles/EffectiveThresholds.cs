namespace Mostlylucid.BotDetection.SiteProfiles;

/// <summary>
///     Flat, non-nullable snapshot of the thresholds that apply to the current
///     request after the global → domain-profile → host-profile overlay has
///     been resolved. Cached on
///     <c>HttpContext.Items[HttpContextItemKeys.EffectiveThresholds]</c> by
///     <see cref="IEffectivePolicyResolver.ResolveThresholds"/> once per request.
/// </summary>
/// <remarks>
///     Field names match the corresponding
///     <see cref="Models.BotDetectionOptions"/> properties they resolve from —
///     see <see cref="SiteThresholdOverrides"/> for the mapping and the
///     documented deviation from the "BotThreshold / AllowBand /
///     ChallengeBand" naming in the original spec (BotDetectionOptions doesn't
///     literally carry AllowBand / ChallengeBand fields).
/// </remarks>
public readonly record struct EffectiveThresholds(
    double BotThreshold,
    double HumanCeiling,
    double BotFloor);