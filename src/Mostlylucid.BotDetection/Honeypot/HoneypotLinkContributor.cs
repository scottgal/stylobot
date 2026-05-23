using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Honeypot;

/// <summary>
///     Wave 0 contributor that emits a strong honeypot signal when the
///     request path matches the curated catalog in
///     <see cref="HoneypotPathDefinitions"/>.
/// </summary>
/// <remarks>
///     <para>
///         The pre-detection <see cref="HoneypotPathTagger"/> middleware
///         normally writes the classification to <see cref="HttpContext.Items"/>
///         before the orchestrator runs, so this contributor's primary path
///         is "read the tagger's result". When the tagger has been bypassed
///         (e.g. a custom host that hasn't wired the middleware) this
///         contributor falls back to running the classifier itself so the
///         signal still fires.
///     </para>
///     <para>
///         Emits:
///         <list type="bullet">
///             <item><c>honeypot.tier1_hit</c> + VerifiedBot 0.95 for <see cref="HoneypotTier.Always"/> matches</item>
///             <item><c>honeypot.tier2_hit</c> + Bot 0.75 for <see cref="HoneypotTier.Probable"/> matches</item>
///         </list>
///         Both contributions carry <c>honeypot.matched_pattern</c> +
///         <c>honeypot.normalized_path</c> in signals so the dashboard query
///         layer can aggregate by path without re-parsing.
///     </para>
/// </remarks>
public sealed class HoneypotLinkContributor : ContributingDetectorBase
{
    public const string SignalTier1Hit = "honeypot.tier1_hit";
    public const string SignalTier2Hit = "honeypot.tier2_hit";
    public const string SignalMatchedPattern = "honeypot.matched_pattern";
    public const string SignalNormalizedPath = "honeypot.normalized_path";
    public const string SignalTier = "honeypot.tier";

    private readonly ILogger<HoneypotLinkContributor> _logger;
    private readonly IOptionsMonitor<HoneypotDetectionOptions> _options;
    private readonly IHoneypotExemptStore _exemptStore;

    public HoneypotLinkContributor(
        ILogger<HoneypotLinkContributor> logger,
        IOptionsMonitor<HoneypotDetectionOptions> options,
        IHoneypotExemptStore exemptStore)
    {
        _logger = logger;
        _options = options;
        _exemptStore = exemptStore;
    }

    public override string Name => "HoneypotLink";

    /// <summary>Wave 0, run before reputation-based shortcuts so a fresh honeypot hit still scores.</summary>
    public override int Priority => 5;

    public override TimeSpan ExecutionTimeout => TimeSpan.FromMilliseconds(10);

    public override bool IsOptional => false;

    /// <summary>Empty -- always run in Wave 0.</summary>
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        if (!_options.CurrentValue.Enabled) return Task.FromResult(None());

        var http = state.HttpContext;
        if (http == null) return Task.FromResult(None());

        // Fast path: tagger already classified -- skip re-matching.
        if (http.Items[HoneypotPathTagger.ItemKeyTier] is HoneypotTier tier
            && tier != HoneypotTier.None)
        {
            var pattern = http.Items[HoneypotPathTagger.ItemKeyMatchedPattern] as string ?? "?";
            var normalized = http.Items[HoneypotPathTagger.ItemKeyNormalizedPath] as string
                             ?? HoneypotPathDefinitions.NormalizePath(http.Request.Path.Value ?? "");
            return Task.FromResult(BuildContribution(tier, pattern, normalized));
        }

        // Fallback path: tagger wasn't wired, run classifier here.
        var raw = http.Request.Path.Value;
        if (string.IsNullOrEmpty(raw)) return Task.FromResult(None());

        var path = HoneypotPathDefinitions.NormalizePath(raw);
        var matchedTier = HoneypotPathDefinitions.Classify(path, out var matched);

        if (matchedTier == HoneypotTier.None)
        {
            // Operator-supplied additional paths -- only matter when the
            // catalog missed, so check here on the cold path.
            foreach (var p in _options.CurrentValue.AdditionalPaths)
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

        if (matchedTier == HoneypotTier.Probable && _exemptStore.IsExempt(path))
            return Task.FromResult(None());

        return Task.FromResult(BuildContribution(matchedTier, matched ?? "?", path));
    }

    private IReadOnlyList<DetectionContribution> BuildContribution(
        HoneypotTier tier,
        string matchedPattern,
        string normalizedPath)
    {
        var signals = ImmutableDictionary.CreateBuilder<string, object>();
        signals.Add(SignalMatchedPattern, matchedPattern);
        signals.Add(SignalNormalizedPath, normalizedPath);
        signals.Add(SignalTier, (int)tier);

        if (tier == HoneypotTier.Always)
        {
            signals.Add(SignalTier1Hit, true);
            _logger.LogWarning(
                "Tier 1 honeypot hit: {Path} matched {Pattern}",
                normalizedPath, matchedPattern);

            return
            [
                DetectionContribution.VerifiedBot(
                    Name,
                    $"Accessed always-honeypot path {normalizedPath} (pattern: {matchedPattern})",
                    nameof(BotType.Scraper))
                    with
                    {
                        Category = "HoneypotTrap",
                        ConfidenceDelta = 0.95,
                        Weight = 2.0,
                        Signals = signals.ToImmutable()
                    }
            ];
        }

        // Tier.Probable
        signals.Add(SignalTier2Hit, true);
        _logger.LogInformation(
            "Tier 2 honeypot hit: {Path} matched {Pattern}",
            normalizedPath, matchedPattern);

        return
        [
            DetectionContribution.Bot(
                Name,
                "HoneypotTrap",
                0.75,
                $"Accessed probable-honeypot path {normalizedPath} (pattern: {matchedPattern})",
                1.5,
                nameof(BotType.Scraper))
                with
                {
                    Signals = signals.ToImmutable()
                }
        ];
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
