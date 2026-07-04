using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     GuardAtom (per Taxonomy.md) that for fediverse User-Agents (Mastodon,
///     Pleroma, Misskey, etc., which carry a <c>+https://instance/</c> URL in
///     the UA) extracts the instance domain and asks
///     <see cref="IFediverseDomainVerifier"/> whether that domain hosts real
///     ActivityPub software via NodeInfo. Also performs a forward-DNS
///     confirmation binding the claim to the client IP.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>FediverseDomainContributor</c>. This is the non-UA
///         cross-corroboration the friendly-pin gate requires for traffic that
///         cannot be IP-range verified. Every fediverse instance runs on
///         arbitrary cloud IPs, so the commercial GoodBotIpRange index has
///         nothing to match against; NodeInfo + forward DNS give the same
///         claim-verify-trust binding on their own vector.
///     </para>
///     <para>
///         Priority 5 -- same wave slot as the legacy contributor,
///         Foundation-tier alongside the other identity-verification atoms
///         so DetermineRiskBand sees the corroboration.
///     </para>
///     <para>
///         Forward-DNS result cache stays instance-scoped on the atom (atom
///         is singleton so this is functionally equivalent to the legacy
///         static field, and testable in isolation). Keyed by hostname; 5-min
///         TTL matches the legacy behaviour.
///     </para>
/// </remarks>
public sealed partial class FediverseDomainAtom : DetectorAtomBase
{
    // Canonical fediverse UA shape (mirrors legacy regex):
    //   "http.rb/5.2.0 (Mastodon/4.2.10; +https://mastodon.social/)"
    //   "Pleroma 2.6.0; https://pleroma.example/"
    [GeneratedRegex(@"\+?https://([a-zA-Z0-9][-a-zA-Z0-9]*(?:\.[a-zA-Z0-9][-a-zA-Z0-9]*)+)/?", RegexOptions.Compiled)]
    private static partial Regex InstanceUrlRegex();

    [GeneratedRegex(@"(?i)\b(Mastodon|Pleroma|Misskey|Akkoma|Firefish|Iceshrimp|GoToSocial|PeerTube|Lemmy|kbin|mbin|Friendica|Hubzilla|Pixelfed|WriteFreely|Bookwyrm|Funkwhale|Sharkey|Calckey)\b", RegexOptions.Compiled)]
    private static partial Regex FediverseUaPrefilterRegex();

    private readonly IFediverseDomainVerifier _verifier;
    private readonly IDnsResolver _dnsResolver;
    private readonly ILogger<FediverseDomainAtom> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    // Instance-scoped forward-DNS cache (atom is singleton). Follows the
    // BoundedCache convention (LRU + TTL) from FediverseDomainVerifier.Cache
    // and VerifiedBotContributor.RdnsCache -- NOT a raw ConcurrentDictionary,
    // per the no-in-memory-stores rule.
    private readonly BoundedCache<string, IReadOnlyList<IPAddress>> _forwardDnsCache =
        new(maxSize: 5_000, defaultTtl: TimeSpan.FromMinutes(5));

    public FediverseDomainAtom(
        IFediverseDomainVerifier verifier,
        IDnsResolver dnsResolver,
        ILogger<FediverseDomainAtom> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "FediverseDomain", category: "FediverseDomain")
    {
        _verifier = verifier;
        _dnsResolver = dnsResolver;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 5;

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return None();

        var ua = context.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrEmpty(ua)) return None();
        if (!FediverseUaPrefilterRegex().IsMatch(ua)) return None();

        var match = InstanceUrlRegex().Match(ua);
        if (!match.Success) return None();

        var domain = match.Groups[1].Value;
        var verified = await _verifier.VerifyAsync(domain, ct).ConfigureAwait(false);

        // "ran" ledger entry -- absence means "not checked", presence + no
        // FriendlyDomainVerified means "checked, negative". SSRF-guard reject
        // is still "ran the verifier" for the coordinator's purposes.
        sink.Raise("fediverse.instance.checked", sessionId);

        // null = SSRF guard rejected the domain (no verdict, but we ran).
        if (verified is null) return None();

        // FriendlyDomainVerified is a bool the composer reads. Presence only
        // when true; absence when false (mirrors the friendly-pin absence-signal
        // shape used elsewhere).
        if (verified.Value)
            sink.Raise(SignalKeys.FriendlyDomainVerified, sessionId);

        _logger.LogDebug("Fediverse domain {Domain} verification={Verified}", domain, verified);

        // Forward-DNS confirmation binds the claim to the request IP.
        var claimedInstance = sink.ReadHint(SignalKeys.UserAgentBotInstance);
        if (string.IsNullOrEmpty(claimedInstance)) claimedInstance = domain;
        await TryForwardDnsConfirmAsync(sink, sessionId, context, claimedInstance, ct).ConfigureAwait(false);

        return Single(verified.Value
            ? new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = -0.4,
                Weight = 1.0,
                Reason = $"Fediverse instance verified via NodeInfo: {domain}"
            }
            : new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = 0.3,
                Weight = 1.0,
                Reason = $"UA claims fediverse but NodeInfo lookup failed for {domain} (likely spoofed)"
            });
    }

    /// <summary>
    ///     Resolve the claimed instance domain and compare against the client
    ///     IP. Raises <see cref="SignalKeys.VerifiedBotForwardDnsMatched"/>
    ///     when the lookup completes, and (on success)
    ///     <see cref="SignalKeys.VerifiedBotMethod"/> = <c>"forward_dns"</c>
    ///     so the dashboard renders the verification method.
    /// </summary>
    private async ValueTask TryForwardDnsConfirmAsync(
        SignalSink sink,
        string sessionId,
        HttpContext context,
        string domain,
        CancellationToken ct)
    {
        var clientIp = context.Connection.RemoteIpAddress;
        if (clientIp is null) return;

        // "ran" ledger for the DNS confirmation step -- distinct from the
        // NodeInfo verification. A downstream constrainer can see that DNS
        // was attempted (or cached) even if the match didn't happen.

        IReadOnlyList<IPAddress>? resolved;
        if (!_forwardDnsCache.TryGet(domain, out resolved) || resolved is null)
        {
            try
            {
                resolved = await _dnsResolver.ResolveAsync(domain, ct).ConfigureAwait(false);
                _forwardDnsCache.Set(domain, resolved);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                // Error type is not PII -- safe to hint. Absence still means "no lookup".
                sink.Raise($"{SignalKeys.VerifiedBotForwardDnsError}:{ex.GetType().Name}", sessionId);
                _logger.LogDebug(
                    ex, "Forward-DNS lookup for fediverse instance {Domain} failed", domain);
                return;
            }
        }

        var matched = false;
        for (var i = 0; i < resolved.Count; i++)
        {
            if (resolved[i].Equals(clientIp))
            {
                matched = true;
                break;
            }
        }

        if (matched)
            sink.Raise(SignalKeys.VerifiedBotForwardDnsMatched, sessionId);

        if (matched)
        {
            // Only raise the method on a positive match. On a mismatch the
            // claim is broken, not corroborated -- absence is the signal.
            sink.Raise($"{SignalKeys.VerifiedBotMethod}:forward_dns", sessionId);
            _logger.LogDebug(
                "Forward-DNS confirmed fediverse instance {Domain} for client {ClientIp}",
                domain, clientIp);
        }
        else
        {
            _logger.LogDebug(
                "Forward-DNS mismatch: client {ClientIp} not in resolved set for {Domain} (count={Count})",
                clientIp, domain, resolved.Count);
        }
    }
}
