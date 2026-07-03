namespace Mostlylucid.BotDetection.Domains;

/// <summary>Well-known HttpContext.Items keys used across the detection pipeline.</summary>
public static class HttpContextItemKeys
{
    public const string Domain = "BotDetection:Domain";
    public const string Host = "BotDetection:Host";
    public const string RequestScope = "BotDetection:RequestScope";

    /// <summary>
    ///     Cached <see cref="SiteProfiles.EffectiveThresholds"/> for the current
    ///     request, stamped by
    ///     <see cref="SiteProfiles.IEffectivePolicyResolver.ResolveThresholds"/>.
    /// </summary>
    public const string EffectiveThresholds = "BotDetection:EffectiveThresholds";
}