using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     GuardAtom (per Taxonomy.md) that verifies bot identity claims via
///     published CIDR ranges, FCrDNS, and the "honest bot" pattern (UA
///     domain matches client rDNS). Priority 4 -- runs after fast-path
///     reputation, before UserAgent.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>VerifiedBotContributor</c>. Three verification tiers preserved:
///         known-bot registry match, honest-bot rDNS confirmation, and
///         spoofed-bot rejection.
///     </para>
///     <para>
///         Emitted signals are bot names (public catalog values),
///         verification methods, and booleans -- safe as Model-2 hints.
///         The reverse-DNS <see cref="BoundedCache{TKey, TValue}"/> lives on
///         the atom instance (singleton), functionally equivalent to the
///         legacy static field but testable in isolation.
///     </para>
///     <para>
///         The legacy contributor left NO <c>verifiedbot.checked</c> signal
///         when the UA didn't match a known-bot pattern -- so a human Chrome
///         visitor left no verifiedbot.* trace on their detection row. This
///         atom preserves that behaviour: <c>verifiedbot.ran</c> is raised
///         unconditionally as the ledger entry, but the checked / name /
///         method / confirmed / spoofed signals are only raised when the
///         registry actually matched a bot pattern.
///     </para>
/// </remarks>
public sealed partial class VerifiedBotAtom : DetectorAtomBase
{
    [GeneratedRegex(@"https?://([a-zA-Z0-9][-a-zA-Z0-9]*(?:\.[a-zA-Z0-9][-a-zA-Z0-9]*)+)", RegexOptions.Compiled)]
    private static partial Regex UaDomainRegex();

    [GeneratedRegex(@"^([A-Za-z][\w-]+)(?:/[\d.]|[ (])", RegexOptions.Compiled)]
    private static partial Regex BotNameRegex();

    private readonly ILogger<VerifiedBotAtom> _logger;
    private readonly VerifiedBotRegistry _registry;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    private readonly BoundedCache<string, string?> _rdnsCache =
        new(maxSize: 5_000, defaultTtl: TimeSpan.FromMinutes(30));

    public VerifiedBotAtom(
        ILogger<VerifiedBotAtom> logger,
        VerifiedBotRegistry registry,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "VerifiedBot", category: "VerifiedBot")
    {
        _logger = logger;
        _registry = registry;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 4;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    private double SpoofedUaConfidence => _configProvider.GetParameter(Name, "spoofed_ua_confidence", 0.85);
    private double HonestBotConfidence => _configProvider.GetParameter(Name, "honest_bot_confidence", 0.3);
    private double RdnsMismatchConfidence => _configProvider.GetParameter(Name, "rdns_mismatch_confidence", 0.25);
    private double WeightBase => _configProvider.GetDefaults(Name).Weights.Base;
    private double WeightVerified => _configProvider.GetDefaults(Name).Weights.Verified;

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return None();

        var userAgent = context.Request.Headers.UserAgent.ToString();
        var clientIp = context.Connection.RemoteIpAddress?.ToString();

        _logger.LogDebug(
            "VerifiedBotAtom invoked: ua-prefix={UaPrefix} ip-present={HasIp}",
            string.IsNullOrEmpty(userAgent) ? "<empty>" : userAgent[..Math.Min(60, userAgent.Length)],
            !string.IsNullOrEmpty(clientIp));

        var botName = _registry.MatchBotUserAgent(userAgent);
        if (botName is null)
        {
            var honest = await CheckHonestBotAsync(sink, sessionId, userAgent, clientIp, ct).ConfigureAwait(false);
            if (honest is not null) return Single(honest);

            // Intentionally silent when UA does not match a known-bot pattern
            // -- humans should leave NO verifiedbot.* trace on the row.
            return Single(DetectionContribution.Info(Name, Category, "No known bot UA pattern"));
        }

        var result = await _registry.VerifyBotAsync(userAgent, clientIp).ConfigureAwait(false);
        if (result is null) return None();

        sink.Raise($"{SignalKeys.VerifiedBotChecked}:true", sessionId);
        sink.Raise($"{SignalKeys.VerifiedBotName}:{result.BotName}", sessionId);
        sink.Raise($"{SignalKeys.VerifiedBotMethod}:{result.VerificationMethod}", sessionId);

        if (result.IsVerified)
        {
            sink.Raise($"{SignalKeys.VerifiedBotConfirmed}:true", sessionId);
            sink.Raise($"{SignalKeys.VerifiedBotSpoofed}:false", sessionId);
            _logger.LogInformation("Verified bot: {BotName} via {Method}",
                result.BotName, result.VerificationMethod);

            return Single(DetectionContribution.VerifiedGoodBot(
                    Name,
                    $"Verified {result.BotName} via {result.VerificationMethod}",
                    result.BotName)
                with
                {
                    Weight = WeightVerified
                });
        }

        // "none" = we verified against NOTHING: no published IP ranges loaded AND no
        // rDNS channel (or the rDNS check was transiently unavailable, which the
        // registry maps to "none"). A MISSING expected signal is not evidence of
        // spoofing -- do NOT raise verifiedbot.spoofed and do NOT brand the claim.
        // Leave it unverified so the score stays uncertain (confidence impacted by
        // the ABSENT signal), never falsely hostile. Verifying against none is invalid.
        if (string.Equals(result.VerificationMethod, "none", StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "VerifiedBot: {BotName} UA claim could not be verified (no channel / lookup unavailable) -- not spoofed",
                result.BotName);
            return Single(DetectionContribution.Info(Name, Category,
                $"{result.BotName} UA claim unverified: no published ranges and rDNS unavailable"));
        }

        // Genuine spoof: a deterministic reference WAS present and the IP failed it
        // (ip_range with ranges loaded + IP not in them, or a resolved rDNS mismatch).
        sink.Raise($"{SignalKeys.VerifiedBotConfirmed}:false", sessionId);
        sink.Raise($"{SignalKeys.VerifiedBotSpoofed}:true", sessionId);
        _logger.LogWarning(
            "Spoofed bot UA: claims {BotName} but IP doesn't verify via {Method}",
            result.BotName, result.VerificationMethod);

        return Single(new DetectionContribution
        {
            DetectorName = Name,
            Category = Category,
            ConfidenceDelta = SpoofedUaConfidence,
            Weight = _configProvider.GetDefaults(Name).Weights.BotSignal,
            Reason = $"Spoofed UA: claims to be {result.BotName} but IP doesn't verify via {result.VerificationMethod}",
            BotType = BotType.Scraper.ToString(),
            BotName = $"Spoofed-{result.BotName}"
        });
    }

    /// <summary>
    ///     Honest bot: UA carries a URL / FQDN AND the client's reverse DNS
    ///     resolves to a hostname within that domain. Weaker than a fully
    ///     verified bot (no forward DNS confirmation) but a transparent
    ///     operator signal.
    /// </summary>
    private async Task<DetectionContribution?> CheckHonestBotAsync(
        SignalSink sink,
        string sessionId,
        string? userAgent,
        string? clientIp,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(userAgent) || string.IsNullOrEmpty(clientIp))
            return null;

        var domainMatch = UaDomainRegex().Match(userAgent);
        if (!domainMatch.Success) return null;

        var uaDomain = domainMatch.Groups[1].Value.ToLowerInvariant();
        var nameMatch = BotNameRegex().Match(userAgent);
        var honestBotName = nameMatch.Success ? nameMatch.Groups[1].Value : null;

        var rdnsHostname = await GetReverseDnsAsync(clientIp, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(rdnsHostname)) return null;

        rdnsHostname = rdnsHostname.TrimEnd('.').ToLowerInvariant();
        var matches = rdnsHostname == uaDomain
            || rdnsHostname.EndsWith("." + uaDomain, StringComparison.OrdinalIgnoreCase);

        if (!matches)
        {
            _logger.LogDebug(
                "rDNS mismatch: UA claims {Domain} but rDNS is {Hostname}",
                uaDomain, rdnsHostname);

            sink.Raise($"{SignalKeys.VerifiedBotRdnsMismatch}:true", sessionId);

            return new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = RdnsMismatchConfidence,
                Weight = WeightBase * 0.4,
                Reason = $"rDNS mismatch: UA claims {uaDomain} but rDNS is {rdnsHostname}",
                BotType = BotType.Unknown.ToString()
            };
        }

        _logger.LogInformation(
            "Honest bot detected: {BotName} from {Domain} (rDNS: {Hostname})",
            honestBotName ?? "Unknown", uaDomain, rdnsHostname);

        return new DetectionContribution
        {
            DetectorName = Name,
            Category = Category,
            ConfidenceDelta = HonestBotConfidence,
            Weight = WeightBase * 0.8,
            Reason = $"Honest bot: UA claims {honestBotName ?? uaDomain} and rDNS confirms ({rdnsHostname})",
            BotType = BotType.GoodBot.ToString()
        };
    }

    private async Task<string?> GetReverseDnsAsync(string ipAddress, CancellationToken ct)
    {
        if (_rdnsCache.TryGet(ipAddress, out var cached)) return cached;

        string? hostname = null;
        try
        {
            if (IPAddress.TryParse(ipAddress, out _))
            {
                var entry = await Dns.GetHostEntryAsync(ipAddress).ConfigureAwait(false);
                if (entry.HostName != ipAddress && !IPAddress.TryParse(entry.HostName, out _))
                    hostname = entry.HostName;
            }
        }
        catch
        {
            // DNS failures are normal (no PTR record)
        }

        _rdnsCache.Set(ipAddress, hostname);
        return hostname;
    }
}
