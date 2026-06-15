using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     For fediverse User-Agents (Mastodon, Pleroma, Misskey, etc., which carry a
///     <c>+https://instance/</c> URL in the UA), extracts the instance domain and
///     asks <see cref="IFediverseDomainVerifier"/> whether that domain hosts real
///     ActivityPub software via a NodeInfo lookup. Sets
///     <see cref="SignalKeys.FriendlyDomainVerified"/> with the result.
///
///     This is the non-UA cross-corroboration the friendly-pin gate requires for
///     traffic that cannot be IP-range verified (every fediverse instance runs on
///     arbitrary cloud IPs, so the Commercial GoodBotIpRange index has nothing to
///     match against). The pin gate accepts either FriendlyIpVerified=true OR
///     FriendlyDomainVerified=true as sufficient corroboration.
///
///     The verifier itself maintains a 24h positive / 1h negative cache so the
///     hot path is a single dictionary lookup -- only first-encounter domains
///     pay the outbound HTTPS cost.
///
///     <para>
///     Per the 2026-06-15 claim-verify-trust gap analysis (Gap #3), NodeInfo on
///     its own is insufficient: anyone can put <c>+https://mastodon.social/</c>
///     in their UA from any IP and NodeInfo will confirm the instance hosts
///     Mastodon, so <see cref="SignalKeys.FriendlyDomainVerified"/>=<c>true</c>
///     would fire for a spoofer. The forward-DNS step below binds the claim to
///     the request by resolving the instance domain's A/AAAA records and
///     comparing the client IP against the resolved set; the result is emitted
///     as <see cref="SignalKeys.VerifiedBotForwardDnsMatched"/> and, on a
///     positive match, <see cref="SignalKeys.VerifiedBotMethod"/>=<c>forward_dns</c>.
///     Results are cached per instance hostname (5 min TTL) to keep subsequent
///     requests off the resolver.
///     </para>
/// </summary>
public sealed partial class FediverseDomainContributor : ContributingDetectorBase
{
    // Canonical fediverse UA shape:
    //   "http.rb/5.2.0 (Mastodon/4.2.10; +https://mastodon.social/)"
    //   "Pleroma 2.6.0; https://pleroma.example/"
    //   "Misskey/13.14.0 (https://misskey.io/)"
    //   "Akkoma 3.10.4; +https://akkoma.example/"
    //
    // The "+" prefix is conventional but not universal (Misskey omits it). Match
    // either form, but require https:// and a hostname with at least one dot so
    // "+http://localhost/" never qualifies even when the rest of the UA looks
    // fediverse-shaped.
    [GeneratedRegex(@"\+?https://([a-zA-Z0-9][-a-zA-Z0-9]*(?:\.[a-zA-Z0-9][-a-zA-Z0-9]*)+)/?", RegexOptions.Compiled)]
    private static partial Regex InstanceUrlRegex();

    // Quick fediverse UA prefilter -- if the UA doesn't mention any of these
    // platforms we skip the verifier entirely. Matches anywhere in the string
    // (case-insensitive). Keep in sync with FediverseSoftware in the verifier.
    [GeneratedRegex(@"(?i)\b(Mastodon|Pleroma|Misskey|Akkoma|Firefish|Iceshrimp|GoToSocial|PeerTube|Lemmy|kbin|mbin|Friendica|Hubzilla|Pixelfed|WriteFreely|Bookwyrm|Funkwhale|Sharkey|Calckey)\b", RegexOptions.Compiled)]
    private static partial Regex FediverseUaPrefilterRegex();

    private readonly IFediverseDomainVerifier _verifier;
    private readonly IDnsResolver _dnsResolver;
    private readonly ILogger<FediverseDomainContributor> _logger;

    // Forward-DNS result cache, keyed by instance hostname only. The result
    // doesn't depend on the asking client, so any subsequent request for the
    // same instance reuses the resolution. Short TTL (5 min) so legitimate DNS
    // changes propagate quickly -- this is a defence-in-depth signal, not a
    // long-term store. Use BoundedCache to follow the convention established by
    // FediverseDomainVerifier.Cache and VerifiedBotContributor.RdnsCache (max
    // size + TTL + LRU eviction), NOT a raw ConcurrentDictionary, per the
    // no-in-memory-stores rule.
    private static readonly BoundedCache<string, IReadOnlyList<IPAddress>> ForwardDnsCache =
        new(maxSize: 5_000, defaultTtl: TimeSpan.FromMinutes(5));

    public FediverseDomainContributor(
        IFediverseDomainVerifier verifier,
        IDnsResolver dnsResolver,
        ILogger<FediverseDomainContributor> logger)
    {
        _verifier = verifier;
        _dnsResolver = dnsResolver;
        _logger = logger;
    }

    public override string Name => "FediverseDomain";

    // Runs in the first wave alongside the other identity-verification contributors
    // (VerifiedBot is priority 4). Both write friendly-corroboration signals that
    // DetermineRiskBand reads later -- order between them doesn't matter.
    public override int Priority => 5;

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var ua = state.UserAgent;
        if (string.IsNullOrEmpty(ua)) return None();
        if (!FediverseUaPrefilterRegex().IsMatch(ua)) return None();

        var match = InstanceUrlRegex().Match(ua);
        if (!match.Success) return None();

        var domain = match.Groups[1].Value;
        var verified = await _verifier.VerifyAsync(domain, cancellationToken).ConfigureAwait(false);

        // null = SSRF guard rejected the domain (treat as "no signal", same as
        // FriendlyIpVerified absent). true/false get written as the corroborating
        // signal that DetermineRiskBand reads at the friendly-pin gate.
        if (verified is null) return None();

        state.WriteSignal(SignalKeys.FriendlyDomainVerified, verified.Value);
        _logger.LogDebug("Fediverse domain {Domain} verification={Verified}", domain, verified);

        // Forward-DNS confirmation: resolve the claimed instance domain's A/AAAA
        // records and check the client IP is in the result set. NodeInfo alone
        // proves the instance hosts ActivityPub software, but anyone can put
        // "+https://mastodon.social/" in their UA from any IP -- NodeInfo will
        // still confirm because mastodon.social really does host Mastodon, so
        // FriendlyDomainVerified=true fires for the spoofer. Forward DNS binds
        // the claim to the request: if the client IP is not in the resolved
        // address set for the instance domain, the claim is unverifiable.
        //
        // Per 2026-06-15 claim-verify-trust gap analysis (Gap #3): "An attacker
        // can claim mastodon.social from any IP and FriendlyDomainVerified=true
        // still fires."
        //
        // Prefer the authoritative SignalKeys.UserAgentBotInstance (published
        // by UserAgentContributor via the shared discriminator extractor) so
        // both contributors agree on exactly the same instance string. Fall
        // back to the regex match if that signal isn't present -- still safe
        // because UserAgentContributor uses the same UserAgentDiscriminator
        // helper, so the values match.
        var claimedInstance = state.GetSignal<string>(SignalKeys.UserAgentBotInstance);
        if (string.IsNullOrEmpty(claimedInstance)) claimedInstance = domain;
        await TryForwardDnsConfirmAsync(state, claimedInstance, cancellationToken).ConfigureAwait(false);

        // Contributor pattern returns a DetectionContribution so the row shows up in
        // the dashboard trace; the actual signal-driven gate is in DetermineRiskBand.
        // Negative contribution = "this is friendlier than baseline" -- mirrors how
        // VerifiedBotContributor surfaces honest-bot evidence.
        var contribution = verified.Value
            ? new DetectionContribution
            {
                DetectorName = Name,
                Category = "FediverseDomain",
                ConfidenceDelta = -0.4,
                Weight = 1.0,
                Reason = $"Fediverse instance verified via NodeInfo: {domain}"
            }
            : new DetectionContribution
            {
                DetectorName = Name,
                Category = "FediverseDomain",
                ConfidenceDelta = 0.3,
                Weight = 1.0,
                Reason = $"UA claims fediverse but NodeInfo lookup failed for {domain} (likely spoofed)"
            };
        return Single(contribution);
    }

    /// <summary>
    ///     Resolve the claimed instance domain and compare the result set against
    ///     the request's client IP. Writes <see cref="SignalKeys.VerifiedBotForwardDnsMatched"/>
    ///     when the lookup completes, and (on success) <see cref="SignalKeys.VerifiedBotMethod"/>
    ///     = <c>"forward_dns"</c> so the dashboard can render the verification
    ///     method that produced the bind.
    ///
    ///     <para>
    ///     Result cache is keyed by instance hostname (5-min TTL); the asking
    ///     client doesn't affect the lookup so subsequent requests for the same
    ///     instance share the resolved set. Lookup errors (SocketException,
    ///     OperationCanceledException) write <see cref="SignalKeys.VerifiedBotForwardDnsError"/>
    ///     with the exception type so absence-of-signal stays distinguishable
    ///     from a failed lookup.
    ///     </para>
    /// </summary>
    private async ValueTask TryForwardDnsConfirmAsync(
        BlackboardState state, string domain, CancellationToken cancellationToken)
    {
        var clientIp = state.ClientIp;
        if (string.IsNullOrEmpty(clientIp)) return;
        if (!IPAddress.TryParse(clientIp, out var clientAddress)) return;

        IReadOnlyList<IPAddress>? resolved;
        if (!ForwardDnsCache.TryGet(domain, out resolved) || resolved is null)
        {
            try
            {
                resolved = await _dnsResolver.ResolveAsync(domain, cancellationToken).ConfigureAwait(false);
                ForwardDnsCache.Set(domain, resolved);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                state.WriteSignal(SignalKeys.VerifiedBotForwardDnsError, ex.GetType().Name);
                _logger.LogDebug(
                    ex, "Forward-DNS lookup for fediverse instance {Domain} failed", domain);
                return;
            }
        }

        var matched = false;
        for (var i = 0; i < resolved.Count; i++)
        {
            if (resolved[i].Equals(clientAddress))
            {
                matched = true;
                break;
            }
        }

        state.WriteSignal(SignalKeys.VerifiedBotForwardDnsMatched, matched);
        if (matched)
        {
            // Only write the method on a positive match. On a mismatch the
            // claim is broken, not corroborated by a different method, so
            // VerifiedBotMethod stays unwritten and the absence is the signal.
            state.WriteSignal(SignalKeys.VerifiedBotMethod, "forward_dns");
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