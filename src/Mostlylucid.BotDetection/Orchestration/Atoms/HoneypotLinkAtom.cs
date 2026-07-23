using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Honeypot;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     GuardAtom (per Taxonomy.md) that emits a strong honeypot signal when
///     the request path matches the curated catalog in
///     <see cref="HoneypotPathDefinitions"/>. Native
///     <see cref="IDetectorAtom"/> replacement for
///     <c>HoneypotLinkContributor</c>. Priority 5, Wave 0.
/// </summary>
/// <remarks>
///     <para>
///         The pre-detection <see cref="HoneypotPathTagger"/> middleware
///         normally writes the classification to <see cref="HttpContext.Items"/>
///         before detection runs, so this atom's primary path is "read the
///         tagger's result". When the tagger has been bypassed (custom host
///         that hasn't wired the middleware) this atom falls back to running
///         the classifier itself.
///     </para>
///     <para>
///         Raises <c>honeypot.tier1_hit</c> + VerifiedBot 0.95 for
///         <see cref="HoneypotTier.Always"/> matches, and
///         <c>honeypot.tier2_hit</c> + Bot 0.75 for
///         <see cref="HoneypotTier.Probable"/>. Both contributions surface
///         <c>honeypot.matched_pattern</c> + <c>honeypot.normalized_path</c>
///         on the sink so downstream aggregation can group by path without
///         re-parsing.
///     </para>
/// </remarks>
public sealed class HoneypotLinkAtom : DetectorAtomBase
{
    public const string SignalTier1Hit = "honeypot.tier1_hit";
    public const string SignalTier2Hit = "honeypot.tier2_hit";
    public const string SignalMatchedPattern = "honeypot.matched_pattern";
    public const string SignalNormalizedPath = "honeypot.normalized_path";
    public const string SignalTier = "honeypot.tier";

    /// <summary>Threat score published on a Tier 1 hit. Theft-of-secrets intent.</summary>
    public const double Tier1ThreatScore = 0.90;

    /// <summary>Threat score on a Tier 2 hit. Probable-scanner intent.</summary>
    public const double Tier2ThreatScore = 0.60;

    private readonly ILogger<HoneypotLinkAtom> _logger;
    private readonly IOptions<HoneypotDetectionOptions> _options;
    private readonly IHoneypotExemptStore _exemptStore;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HoneypotLinkAtom(
        ILogger<HoneypotLinkAtom> logger,
        IOptions<HoneypotDetectionOptions> options,
        IHoneypotExemptStore exemptStore,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "HoneypotLink", category: "Honeypot")
    {
        _logger = logger;
        _options = options;
        _exemptStore = exemptStore;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>Wave 0, before reputation-based shortcuts so a fresh honeypot hit still scores.</summary>
    public override int Priority => 5;

    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        if (!_options.Value.Enabled) return Task.FromResult(None());

        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        // Fast path: HoneypotPathTagger middleware already classified.
        if (context.Items[HoneypotPathTagger.ItemKeyTier] is HoneypotTier tier
            && tier != HoneypotTier.None)
        {
            var pattern = context.Items[HoneypotPathTagger.ItemKeyMatchedPattern] as string ?? "?";
            var normalized = context.Items[HoneypotPathTagger.ItemKeyNormalizedPath] as string
                             ?? HoneypotPathDefinitions.NormalizePath(context.Request.Path.Value ?? "");
            return Task.FromResult(BuildContribution(sink, sessionId, tier, pattern, normalized));
        }

        // Fallback: tagger not wired — run classifier here.
        var raw = context.Request.Path.Value;
        if (string.IsNullOrEmpty(raw)) return Task.FromResult(None());

        var path = HoneypotPathDefinitions.NormalizePath(raw);
        var matchedTier = HoneypotPathDefinitions.Classify(path, out var matched);

        if (matchedTier == HoneypotTier.None)
        {
            foreach (var p in _options.Value.AdditionalPaths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (AdditionalMatches(path, p))
                {
                    matchedTier = HoneypotTier.Probable;
                    matched = p;
                    break;
                }
            }
        }

        if (matchedTier == HoneypotTier.None) return Task.FromResult(None());

        if (matchedTier == HoneypotTier.Probable && _exemptStore.IsExempt(path, context))
            return Task.FromResult(None());

        return Task.FromResult(BuildContribution(sink, sessionId, matchedTier, matched ?? "?", path));
    }

    private IReadOnlyList<DetectionContribution> BuildContribution(
        SignalSink sink,
        string sessionId,
        HoneypotTier tier,
        string matchedPattern,
        string normalizedPath)
    {
        sink.Raise($"{SignalMatchedPattern}:{matchedPattern}", sessionId);
        sink.Raise($"{SignalNormalizedPath}:{normalizedPath}", sessionId);
        sink.Raise($"{SignalTier}:{(int)tier}", sessionId);

        if (tier == HoneypotTier.Always)
        {
            sink.Raise($"{SignalTier1Hit}:true", sessionId);
            sink.Raise($"{SignalKeys.IntentThreatScore}:{Tier1ThreatScore.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
            _logger.LogWarning(
                "Tier 1 honeypot hit: {Path} matched {Pattern}",
                normalizedPath, matchedPattern);

            return Single(DetectionContribution.VerifiedBot(
                Name,
                $"Tier 1 honeypot hit: {normalizedPath} matched {matchedPattern}",
                nameof(BotType.Scraper))
                with
                {
                    Category = "HoneypotTrap",
                    ConfidenceDelta = 0.95,
                    Weight = 2.0
                });
        }

        sink.Raise($"{SignalTier2Hit}:true", sessionId);
        sink.Raise($"{SignalKeys.IntentThreatScore}:{Tier2ThreatScore.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        _logger.LogInformation(
            "Tier 2 honeypot hit: {Path} matched {Pattern}",
            normalizedPath, matchedPattern);

        return Single(DetectionContribution.Bot(
            Name,
            "HoneypotTrap",
            0.75,
            $"Tier 2 honeypot hit: {normalizedPath} matched {matchedPattern}",
            1.5,
            nameof(BotType.Scraper)));
    }

    private static bool AdditionalMatches(string path, string pattern)
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
