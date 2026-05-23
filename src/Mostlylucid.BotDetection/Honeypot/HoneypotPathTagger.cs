using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Honeypot;

/// <summary>
///     Pre-detection middleware that classifies the request path against
///     the honeypot catalog and writes the result to
///     <see cref="HttpContext.Items"/> before the detection pipeline runs.
/// </summary>
/// <remarks>
///     <para>
///         Why pre-detection: the orchestrator can short-circuit Wave 0 on
///         confident cached verdicts (FastPathReputation, SignatureVerdictGate
///         Skip) so the Wave 0 <see cref="HoneypotLinkContributor"/> doesn't
///         always run. Tagging here ensures the holodeck action policy +
///         dashboard query layer can see the honeypot match even on the
///         skip path.
///     </para>
///     <para>
///         Keys written on match:
///         <list type="bullet">
///             <item><c>Honeypot.Tier</c> -- <see cref="HoneypotTier"/> (Always or Probable)</item>
///             <item><c>Honeypot.MatchedPattern</c> -- catalog entry that matched</item>
///             <item><c>Honeypot.NormalizedPath</c> -- decoded/lowercased path used for matching</item>
///             <item><c>Holodeck.IsHoneypotPath</c> -- legacy bool flag for back-compat with the ApiHolodeck coordinator</item>
///             <item><c>Holodeck.MatchedPath</c> -- legacy alias for MatchedPattern</item>
///         </list>
///     </para>
/// </remarks>
public sealed class HoneypotPathTagger
{
    public const string ItemKeyTier = "Honeypot.Tier";
    public const string ItemKeyMatchedPattern = "Honeypot.MatchedPattern";
    public const string ItemKeyNormalizedPath = "Honeypot.NormalizedPath";

    // Legacy aliases for back-compat with code that read the old keys set by
    // Mostlylucid.BotDetection.ApiHolodeck.Middleware.HoneypotPathTagger.
    public const string LegacyItemKeyIsHoneypotPath = "Holodeck.IsHoneypotPath";
    public const string LegacyItemKeyMatchedPath = "Holodeck.MatchedPath";

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<HoneypotDetectionOptions> _options;
    private readonly IHoneypotExemptStore _exemptStore;

    public HoneypotPathTagger(
        RequestDelegate next,
        IOptionsMonitor<HoneypotDetectionOptions> options,
        IHoneypotExemptStore exemptStore)
    {
        _next = next;
        _options = options;
        _exemptStore = exemptStore;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (!_options.CurrentValue.Enabled) return _next(context);

        var raw = context.Request.Path.Value;
        if (string.IsNullOrEmpty(raw)) return _next(context);

        var normalized = HoneypotPathDefinitions.NormalizePath(raw);
        var tier = HoneypotPathDefinitions.Classify(normalized, out var matched);

        // Honour additional operator-supplied paths if the catalog missed.
        if (tier == HoneypotTier.None)
        {
            var extras = _options.CurrentValue.AdditionalPaths;
            if (extras is { Count: > 0 })
            {
                foreach (var p in extras)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    if (MatchesAdditional(normalized, p))
                    {
                        tier = HoneypotTier.Probable;
                        matched = p;
                        break;
                    }
                }
            }
        }

        if (tier == HoneypotTier.None) return _next(context);

        // Tier 2 is exempt-able; Tier 1 is not.
        if (tier == HoneypotTier.Probable && _exemptStore.IsExempt(normalized))
            return _next(context);

        context.Items[ItemKeyTier] = tier;
        context.Items[ItemKeyMatchedPattern] = matched!;
        context.Items[ItemKeyNormalizedPath] = normalized;
        context.Items[LegacyItemKeyIsHoneypotPath] = true;
        context.Items[LegacyItemKeyMatchedPath] = matched!;

        return _next(context);
    }

    private static bool MatchesAdditional(string path, string pattern)
    {
        if (pattern.Length > 1 && pattern[^1] == '*')
        {
            var prefix = pattern.AsSpan(0, pattern.Length - 1);
            return path.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        if (path.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return true;

        return path.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)
               && path.Length > pattern.Length
               && path[pattern.Length] == '/';
    }
}
