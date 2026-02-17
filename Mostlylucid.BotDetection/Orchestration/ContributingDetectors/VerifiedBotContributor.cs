using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Verifies bot identity claims using IP ranges and FCrDNS.
///     Runs early (priority 4) after FastPathReputation but before UserAgent.
///
///     UA strings like "Googlebot" are trivially spoofable. This detector
///     verifies the claim by checking:
///     1. Published CIDR ranges (Google, Bing, OpenAI, DuckDuckGo)
///     2. Forward-Confirmed reverse DNS (Applebot, YandexBot, etc.)
///
///     Results:
///     - Verified: UA claims bot AND IP matches → VerifiedGoodBot early exit
///     - Spoofed: UA claims bot BUT IP doesn't match → StrongBotContribution (suspicious)
///     - Not a bot claim: UA doesn't match any known bot → neutral Info
///
///     Configuration loaded from: verifiedbot.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:VerifiedBotContributor:*
/// </summary>
public class VerifiedBotContributor : ConfiguredContributorBase
{
    private readonly ILogger<VerifiedBotContributor> _logger;
    private readonly VerifiedBotRegistry _registry;

    public VerifiedBotContributor(
        ILogger<VerifiedBotContributor> logger,
        VerifiedBotRegistry registry,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
        _registry = registry;
    }

    public override string Name => "VerifiedBot";
    public override int Priority => Manifest?.Priority ?? 4;

    // No triggers - runs in first wave
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    // Config-driven parameters
    private double SpoofedUaConfidence => GetParam("spoofed_ua_confidence", 0.85);

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var userAgent = state.UserAgent;
        var clientIp = state.ClientIp;

        // Quick check: does the UA claim to be a known bot?
        var botName = _registry.MatchBotUserAgent(userAgent);
        if (botName == null)
        {
            // Not claiming to be a known bot — nothing to verify.
            // Don't write signals for the common case (99%+ of traffic).
            return Single(DetectionContribution.Info(
                Name, "VerifiedBot", "No known bot UA pattern"));
        }

        // UA claims to be a known bot - verify via IP/DNS
        var result = await _registry.VerifyBotAsync(userAgent, clientIp);
        if (result == null)
        {
            // Shouldn't happen since we matched above, but be safe
            return None();
        }

        state.WriteSignals([
            new(SignalKeys.VerifiedBotChecked, true),
            new(SignalKeys.VerifiedBotName, result.BotName),
            new(SignalKeys.VerifiedBotMethod, result.VerificationMethod),
            new(SignalKeys.VerifiedBotConfirmed, result.IsVerified),
            new(SignalKeys.VerifiedBotSpoofed, !result.IsVerified)
        ]);

        if (result.IsVerified)
        {
            _logger.LogInformation(
                "Verified bot: {BotName} via {Method}",
                result.BotName, result.VerificationMethod);

            // Verified good bot - early exit with VerifiedGoodBot verdict
            return Single(DetectionContribution.VerifiedGoodBot(
                    Name,
                    $"Verified {result.BotName} via {result.VerificationMethod}",
                    result.BotName)
                with
                {
                    Weight = WeightVerified
                });
        }

        // UA claims to be a known bot but IP doesn't verify - SPOOFED
        _logger.LogWarning(
            "Spoofed bot UA: claims {BotName} but IP doesn't verify via {Method}",
            result.BotName, result.VerificationMethod);

        return Single(StrongBotContribution(
                "VerifiedBot",
                $"Spoofed UA: claims to be {result.BotName} but IP doesn't verify via {result.VerificationMethod}",
                botType: BotType.Scraper.ToString(),
                botName: $"Spoofed-{result.BotName}")
            with
            {
                ConfidenceDelta = SpoofedUaConfidence
            });
    }
}
