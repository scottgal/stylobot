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
///     Inline, DNS-free companion to <see cref="VerifiedBotContributor"/>.
///     Catches UA-claim impersonators on the FIRST request when the claimed bot
///     publishes IP ranges (Bingbot, Googlebot, Amazonbot, OpenAI's GPTBot, etc.).
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>VerifiedBotInlineContributor</c>. Uses
///         <see cref="VerifiedBotRegistry.VerifyBotInline"/> which performs the
///         IP-range half of the check only (O(n) CIDR lookup, no network I/O).
///         Returns:
///         <list type="bullet">
///             <item><c>null</c> when the UA doesn't match a known bot pattern OR
///                   when the bot's only verification channel is FCrDNS -- defer
///                   to the async path in those cases.</item>
///             <item>A positive result when the IP is in the published range --
///                   raises a verified-good contribution + friendly.ip_verified
///                   so the composer's hostile-pin carve-out can suppress a
///                   poisoned reputation cache.</item>
///             <item>A negative result -- the impersonator case. Emits a strong
///                   bot contribution + verified_bot.spoofed on the sink.</item>
///         </list>
///     </para>
///     <para>
///         Wave 0, priority 4. Same priority as the legacy contributor so
///         downstream classifiers see the inline verdict before any
///         friendly-pin decision is composed.
///     </para>
/// </remarks>
public sealed class VerifiedBotInlineAtom : DetectorAtomBase
{
    private readonly ILogger<VerifiedBotInlineAtom> _logger;
    private readonly VerifiedBotRegistry _registry;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public VerifiedBotInlineAtom(
        ILogger<VerifiedBotInlineAtom> logger,
        VerifiedBotRegistry registry,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "VerifiedBotInline", category: "VerifiedBotInline")
    {
        _logger = logger;
        _registry = registry;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 4;

    private double SpoofedUaConfidence =>
        _configProvider.GetParameter(Name, "spoofed_ua_confidence", 0.85);

    private double VerifiedWeight =>
        _configProvider.GetParameter(Name, "verified_weight", 3.0);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null)
            return Task.FromResult(None());

        var ua = context.Request.Headers.UserAgent.ToString();
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;

        var result = _registry.VerifyBotInline(ua, ip);
        if (result is null)
        {
            // Don't raise verified_bot.checked here -- humans with no bot UA
            // would gain a noise marker that downstream aggregators could
            // misread. Also don't raise a friendly-pin-suppressing signal:
            // the bot may yet be verifiable via the async FCrDNS path.
            return Task.FromResult(None());
        }

        if (result.IsVerified)
        {
            // friendly.ip_verified is the signal the SignatureRiskVerdictComposer
            // reads to (a) fire the friendly-pin and (b) suppress a ConfirmedBad
            // hostile-pin if the UA reputation cache had been poisoned.
            sink.Raise(SignalKeys.VerifiedBotChecked, sessionId);
            sink.Raise($"{SignalKeys.VerifiedBotName}:{result.BotName}", sessionId);
            sink.Raise($"{SignalKeys.VerifiedBotMethod}:ip_range", sessionId);
            sink.Raise(SignalKeys.VerifiedBotConfirmed, sessionId);
            sink.Raise(SignalKeys.FriendlyIpVerified, sessionId);

            _logger.LogInformation(
                "VerifiedBotInline: {BotName} verified via published IP range", result.BotName);

            var verifiedContribution = DetectionContribution.VerifiedGoodBot(
                Name,
                $"Verified {result.BotName} via published IP range",
                result.BotName);
            return Task.FromResult(Single(verifiedContribution with { Weight = VerifiedWeight }));
        }

        // Spoofed: ranges loaded, IP not in any of them, no FCrDNS fallback.
        // friendly.ip_verified is INTENTIONALLY NOT raised here -- absence is
        // the "this is a UA-claim impersonator" signal. verified_bot.spoofed
        // is the explicit positive marker.
        sink.Raise(SignalKeys.VerifiedBotChecked, sessionId);
        sink.Raise($"{SignalKeys.VerifiedBotName}:{result.BotName}", sessionId);
        sink.Raise($"{SignalKeys.VerifiedBotMethod}:ip_range", sessionId);
        sink.Raise(SignalKeys.VerifiedBotSpoofed, sessionId);

        _logger.LogWarning(
            "VerifiedBotInline: spoofed UA -- claims {BotName} but IP not in published range",
            result.BotName);

        return Task.FromResult(Single(new DetectionContribution
        {
            DetectorName = Name,
            Category = "VerifiedBotInline",
            ConfidenceDelta = SpoofedUaConfidence,
            Weight = VerifiedWeight,
            Reason = $"Spoofed UA: claims to be {result.BotName} but IP not in published range",
            BotType = BotType.Scraper.ToString(),
            BotName = $"Spoofed-{result.BotName}"
        }));
    }
}
