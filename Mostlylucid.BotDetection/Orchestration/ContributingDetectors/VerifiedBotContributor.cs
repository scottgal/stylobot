using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Verifies bot identity claims using IP ranges, FCrDNS, and honest bot detection.
///     Runs early (priority 4) after FastPathReputation but before UserAgent.
///
///     Three verification tiers:
///     1. **Known bots** (Googlebot, Bingbot, etc.) — verified via published CIDR ranges or FCrDNS
///     2. **Honest bots** — UA contains a URL/domain AND client IP reverse-DNS matches that domain
///        These are bots that truthfully identify themselves (e.g., MostlylucidBot with matching rDNS)
///     3. **Spoofed bots** — UA claims to be a known bot but IP doesn't verify
///
///     Configuration loaded from: verifiedbot.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:VerifiedBotContributor:*
/// </summary>
public partial class VerifiedBotContributor : ConfiguredContributorBase
{
    // Regex to extract domain from UA URL patterns like (+https://example.com/bot) or (+http://example.com)
    [GeneratedRegex(@"https?://([a-zA-Z0-9][-a-zA-Z0-9]*(?:\.[a-zA-Z0-9][-a-zA-Z0-9]*)+)", RegexOptions.Compiled)]
    private static partial Regex UaDomainRegex();

    // Regex to extract bot name from UA like "BotName/1.0" or "BotName (compatible; ...)"
    [GeneratedRegex(@"^([A-Za-z][\w-]+)(?:/[\d.]|[ (])", RegexOptions.Compiled)]
    private static partial Regex BotNameRegex();

    // Cache reverse DNS results to avoid repeated lookups (TTL 30 min)
    private static readonly ConcurrentDictionary<string, (string? Hostname, DateTime Expiry)> RdnsCache = new();

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
    private double HonestBotConfidence => GetParam("honest_bot_confidence", 0.3);
    private double RdnsMismatchConfidence => GetParam("rdns_mismatch_confidence", 0.25);

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
            // Not a known bot — but check for "honest bot" pattern:
            // UA has a URL, and reverse DNS of client IP matches that domain
            var honestResult = await CheckHonestBot(state, userAgent, clientIp, cancellationToken);
            if (honestResult != null)
                return Single(honestResult);

            // Not a known bot and no honest bot signal
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

    /// <summary>
    ///     Check for "honest bot" pattern: UA contains a URL/FQDN and client IP's
    ///     reverse DNS resolves to a hostname within that domain.
    ///     This is a weaker signal than verified bot (no forward DNS confirmation)
    ///     but indicates the bot operator is being transparent about identity.
    /// </summary>
    private async Task<DetectionContribution?> CheckHonestBot(
        BlackboardState state, string? userAgent, string? clientIp, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(userAgent) || string.IsNullOrEmpty(clientIp))
            return null;

        // Extract domain from UA URL (e.g., "MostlylucidBot/1.0 (+https://mostlylucid.net)")
        var domainMatch = UaDomainRegex().Match(userAgent);
        if (!domainMatch.Success)
            return null;

        var uaDomain = domainMatch.Groups[1].Value.ToLowerInvariant();

        // Extract bot name from UA
        var nameMatch = BotNameRegex().Match(userAgent);
        var honestBotName = nameMatch.Success ? nameMatch.Groups[1].Value : null;

        // Do reverse DNS on the client IP
        var rdnsHostname = await GetReverseDns(clientIp, ct);
        if (string.IsNullOrEmpty(rdnsHostname))
            return null;

        // Check if rDNS hostname ends with the UA-claimed domain
        // e.g., rDNS: "bot.mostlylucid.net" matches UA domain "mostlylucid.net"
        rdnsHostname = rdnsHostname.TrimEnd('.').ToLowerInvariant();
        var domainMatch2 = rdnsHostname == uaDomain ||
                          rdnsHostname.EndsWith("." + uaDomain, StringComparison.OrdinalIgnoreCase);

        if (!domainMatch2)
        {
            // rDNS resolved to a DIFFERENT domain — weak mismatch signal
            // (rDNS can legitimately differ due to CDNs, shared hosting, etc.)
            _logger.LogDebug(
                "rDNS mismatch: UA claims {Domain} but rDNS is {Hostname}",
                uaDomain, rdnsHostname);

            state.WriteSignal(SignalKeys.VerifiedBotRdnsMismatch, true);

            return BotContribution(
                    "VerifiedBot",
                    $"rDNS mismatch: UA claims {uaDomain} but rDNS is {rdnsHostname}",
                    confidenceOverride: RdnsMismatchConfidence,
                    botType: BotType.Unknown.ToString(),
                    botName: honestBotName)
                with
                {
                    Weight = WeightBase * 0.4
                };
        }

        // Honest bot detected! UA domain matches reverse DNS
        _logger.LogInformation(
            "Honest bot detected: {BotName} from {Domain} (rDNS: {Hostname})",
            honestBotName ?? "Unknown", uaDomain, rdnsHostname);

        // Write signals
        return BotContribution(
                "VerifiedBot",
                $"Honest bot: UA claims {honestBotName ?? uaDomain} and rDNS confirms ({rdnsHostname})",
                confidenceOverride: HonestBotConfidence,
                botType: BotType.GoodBot.ToString(),
                botName: honestBotName ?? uaDomain)
            with
            {
                // Honest bots get moderate weight — they're still bots, just trustworthy ones
                Weight = WeightBase * 0.8
            };
    }

    private static async Task<string?> GetReverseDns(string ipAddress, CancellationToken ct)
    {
        // Check cache
        if (RdnsCache.TryGetValue(ipAddress, out var cached) && DateTime.UtcNow < cached.Expiry)
            return cached.Hostname;

        string? hostname = null;
        try
        {
            if (IPAddress.TryParse(ipAddress, out var addr))
            {
                var entry = await Dns.GetHostEntryAsync(ipAddress);
                // GetHostEntry returns the IP as hostname if no PTR record exists
                if (entry.HostName != ipAddress && !IPAddress.TryParse(entry.HostName, out _))
                    hostname = entry.HostName;
            }
        }
        catch
        {
            // DNS failures are normal (no PTR record)
        }

        // Cache for 30 minutes (including null results)
        if (RdnsCache.Count < 50_000)
            RdnsCache[ipAddress] = (hostname, DateTime.UtcNow.AddMinutes(30));

        return hostname;
    }
}
