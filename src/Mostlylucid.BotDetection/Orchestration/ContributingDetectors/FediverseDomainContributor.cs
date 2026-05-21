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
    private readonly ILogger<FediverseDomainContributor> _logger;

    public FediverseDomainContributor(
        IFediverseDomainVerifier verifier,
        ILogger<FediverseDomainContributor> logger)
    {
        _verifier = verifier;
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
}
