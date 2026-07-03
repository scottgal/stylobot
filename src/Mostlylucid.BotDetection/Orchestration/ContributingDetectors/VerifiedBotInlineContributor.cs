using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Inline, DNS-free companion to <see cref="VerifiedBotContributor"/>.
///     Catches UA-claim impersonators on the FIRST request when the claimed bot
///     publishes IP ranges (Bingbot, Googlebot, Amazonbot, OpenAI's GPTBot, etc.).
/// </summary>
/// <remarks>
///     <para>
///         Live trigger: signature <c>9z3avO7sKTd7NAYY896Yog</c> on
///         stylo.bot -- User-Agent claimed Amazonbot, client IP was
///         <c>85.121.245.x</c> (Hong Kong residential, NOT in Amazon's
///         published range). The dashboard tagged the actor "Amazonbot /
///         GoodBot" because the heavier <see cref="VerifiedBotContributor"/>
///         only runs in the background-enrichment path (rDNS is too slow
///         inline) so the inline pipeline never saw the IP-vs-claim mismatch.
///     </para>
///     <para>
///         This contributor uses <see cref="VerifiedBotRegistry.VerifyBotInline"/>
///         which only performs the <em>IP-range</em> half of the check (O(n) CIDR
///         lookup, no network I/O). It returns:
///         <list type="bullet">
///             <item><c>null</c> when the UA doesn't match a known bot pattern OR
///                   when the bot's only verification channel is FCrDNS -- defer
///                   to the async path in those cases.</item>
///             <item>A positive result when the IP is in the published range --
///                   real Bingbot from Microsoft Azure lands here. Returns a
///                   verified-good contribution and writes
///                   <see cref="SignalKeys.FriendlyIpVerified"/> = true so the
///                   composer's hostile-pin carve-out can suppress a poisoned
///                   reputation cache (see commit <c>a52c2f28</c>).</item>
///             <item>A negative result when ranges loaded and IP wasn't in any --
///                   the impersonator case. Returns a strong bot contribution and
///                   writes <see cref="SignalKeys.FriendlyIpVerified"/> = false +
///                   <see cref="SignalKeys.VerifiedBotSpoofed"/> = true.</item>
///         </list>
///     </para>
///     <para>
///         Wave 0, priority 4 -- same priority as the original contributor so
///         downstream classifiers see the inline verdict before any
///         friendly-pin decision is composed.
///     </para>
/// </remarks>
public sealed class VerifiedBotInlineContributor : ConfiguredContributorBase
{
    private readonly ILogger<VerifiedBotInlineContributor> _logger;
    private readonly VerifiedBotRegistry _registry;

    public VerifiedBotInlineContributor(
        ILogger<VerifiedBotInlineContributor> logger,
        VerifiedBotRegistry registry,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
        _registry = registry;
    }

    public override string Name => "VerifiedBotInline";
    public override int Priority => Manifest?.Priority ?? 4;
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    private double SpoofedUaConfidence => GetParam("spoofed_ua_confidence", 0.85);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var ua = state.UserAgent;
        var ip = state.ClientIp;

        var result = _registry.VerifyBotInline(ua, ip);
        if (result is null)
        {
            // Don't write VerifiedBotChecked here -- humans with no bot UA
            // would gain a noise marker that downstream aggregators could
            // misread. Also don't write a friendly-pin-suppressing signal:
            // the bot may yet be verifiable via the async FCrDNS path.
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(
                Array.Empty<DetectionContribution>());
        }

        if (result.IsVerified)
        {
            // FriendlyIpVerified=true is the signal the SignatureRiskVerdictComposer
            // reads to (a) fire the friendly-pin and (b) suppress a ConfirmedBad
            // hostile-pin if the UA reputation cache had been poisoned (Bug J
            // carve-out, commit a52c2f28).
            state.WriteSignals([
                new(SignalKeys.VerifiedBotChecked, true),
                new(SignalKeys.VerifiedBotName, result.BotName),
                new(SignalKeys.VerifiedBotMethod, "ip_range"),
                new(SignalKeys.VerifiedBotConfirmed, true),
                new(SignalKeys.FriendlyIpVerified, true)
            ]);

            _logger.LogInformation(
                "VerifiedBotInline: {BotName} verified via published IP range", result.BotName);

            return Task.FromResult<IReadOnlyList<DetectionContribution>>(new[]
            {
                DetectionContribution.VerifiedGoodBot(
                    Name,
                    $"Verified {result.BotName} via published IP range",
                    result.BotName) with { Weight = WeightVerified }
            });
        }

        // Spoofed: ranges loaded, IP not in any of them, no FCrDNS fallback.
        // FriendlyIpVerified=false is the explicit "this is a UA-claim impersonator"
        // signal. Composer reads it and suppresses any friendly-pin candidate.
        state.WriteSignals([
            new(SignalKeys.VerifiedBotChecked, true),
            new(SignalKeys.VerifiedBotName, result.BotName),
            new(SignalKeys.VerifiedBotMethod, "ip_range"),
            new(SignalKeys.VerifiedBotConfirmed, false),
            new(SignalKeys.VerifiedBotSpoofed, true),
            new(SignalKeys.FriendlyIpVerified, false)
        ]);

        _logger.LogWarning(
            "VerifiedBotInline: spoofed UA -- claims {BotName} but IP not in published range",
            result.BotName);

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(new[]
        {
            StrongBotContribution(
                "VerifiedBotInline",
                $"Spoofed UA: claims to be {result.BotName} but IP not in published range",
                botType: BotType.Scraper.ToString(),
                botName: $"Spoofed-{result.BotName}") with { ConfidenceDelta = SpoofedUaConfidence }
        });
    }
}