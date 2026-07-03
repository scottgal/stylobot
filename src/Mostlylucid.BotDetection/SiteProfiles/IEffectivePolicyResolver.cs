using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.SiteProfiles;

/// <summary>
///     Resolves the <see cref="EffectiveThresholds"/> that apply to the current
///     request. Walks three levels, deepest non-null wins per field:
///     <list type="number">
///         <item><b>Global</b> — current values on <c>BotDetectionOptions</c>
///             / <c>ClassificationOptions</c>.</item>
///         <item><b>Domain profile</b> — profile whose
///             <c>SiteMapRule.Host</c> matches the request's eTLD+1
///             <see cref="RequestScope.Domain"/>, or whose
///             <c>SiteMapRule.Domain</c> literally equals that eTLD+1.</item>
///         <item><b>Host profile</b> — profile whose <c>SiteMapRule.Host</c>
///             matches the request's full lowercased host
///             <see cref="RequestScope.Host"/>. Peer lookup to the domain
///             profile, not nested under it — a per-host override wins over a
///             per-domain override for the fields it declares, and the two
///             cascade purely by per-field null-fill.</item>
///     </list>
///     Result cached on
///     <c>HttpContext.Items[HttpContextItemKeys.EffectiveThresholds]</c>.
/// </summary>
public interface IEffectivePolicyResolver
{
    /// <summary>
    ///     Resolve the thresholds that apply to this request. Idempotent per
    ///     request — a second call returns the cached value.
    /// </summary>
    EffectiveThresholds ResolveThresholds(HttpContext context);
}

internal sealed class EffectivePolicyResolver : IEffectivePolicyResolver
{
    private readonly ISiteProfileResolver _profiles;
    private readonly IOptionsMonitor<BotDetectionOptions> _options;
    private readonly DomainNormalizer _domainNormalizer;

    public EffectivePolicyResolver(
        ISiteProfileResolver profiles,
        IOptionsMonitor<BotDetectionOptions> options,
        DomainNormalizer domainNormalizer)
    {
        _profiles = profiles;
        _options = options;
        _domainNormalizer = domainNormalizer;
    }

    public EffectiveThresholds ResolveThresholds(HttpContext context)
    {
        if (context.Items.TryGetValue(HttpContextItemKeys.EffectiveThresholds, out var cached)
            && cached is EffectiveThresholds cachedThresholds)
            return cachedThresholds;

        // Level 0 — global. Read from IOptionsMonitor so live reloads take
        // effect on subsequent requests without a restart.
        var opts = _options.CurrentValue;
#pragma warning disable CS0618 // BotThreshold is obsolete on BotDetectionOptions but still the canonical field for this overlay level.
        double botThreshold = opts.BotThreshold;
#pragma warning restore CS0618
        double humanCeiling = opts.Classification.HumanCeiling;
        double botFloor = opts.Classification.BotFloor;

        // Level 1 — domain profile. Look up by the request's eTLD+1.
        var scope = _domainNormalizer.Resolve(context);
        var domainProfile = _profiles.ResolveByHost(scope.Domain);
        if (domainProfile?.Thresholds is { } domainOverrides)
        {
            if (domainOverrides.BotThreshold is { } db) botThreshold = db;
            if (domainOverrides.HumanCeiling is { } dh) humanCeiling = dh;
            if (domainOverrides.BotFloor is { } df) botFloor = df;
        }

        // Level 2 — host profile. Peer lookup against the full host. Domain
        // and host profiles cascade purely by per-field null-fill, so a host
        // profile that only declares BotThreshold still lets its domain
        // profile fill HumanCeiling / BotFloor. When scope.Host == scope.Domain
        // this collapses to a single lookup and behaves correctly.
        var hostProfile = !string.Equals(scope.Host, scope.Domain, StringComparison.OrdinalIgnoreCase)
            ? _profiles.ResolveByHost(scope.Host)
            : domainProfile;
        if (hostProfile is not null
            && !ReferenceEquals(hostProfile, domainProfile)
            && hostProfile.Thresholds is { } hostOverrides)
        {
            if (hostOverrides.BotThreshold is { } hb) botThreshold = hb;
            if (hostOverrides.HumanCeiling is { } hh) humanCeiling = hh;
            if (hostOverrides.BotFloor is { } hf) botFloor = hf;
        }

        var effective = new EffectiveThresholds(botThreshold, humanCeiling, botFloor);
        context.Items[HttpContextItemKeys.EffectiveThresholds] = effective;
        return effective;
    }
}