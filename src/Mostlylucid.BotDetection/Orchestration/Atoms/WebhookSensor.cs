using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Definitions.Webhooks;
using Mostlylucid.BotDetection.Helpers;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Reputation;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Recognizes inbound webhook deliveries (Stripe, GitHub, Shopify, Slack, ...) by
///     behavioural shape (POST + JSON + a known signature/event header) so a genuine
///     webhook delivery through the gateway is NOT throttled/blocked -- WITHOUT a bypass.
///     The fix is in detection: the score is made correct, never skipped.
/// </summary>
/// <remarks>
///     <para>
///         The signature header only NAMES the claimed provider and establishes the
///         request SHAPE -- it is trivially spoofable by any client, so it never grants
///         recognition on its own. A request earns the strong low-threat contribution
///         ONLY when the shape is corroborated by an IP-BASED signal: the source IP is
///         the endpoint's learned dominant sender, the source IP has a verified (2xx-heavy)
///         delivery track record, or the source IP falls in the named provider's published
///         range. A forged provider header from a fresh IP with no track record gets NO
///         score reduction and is scored normally (the spoof guard = the load-bearing
///         negative test).
///     </para>
///     <para>
///         Deliberately NOT a <see cref="DetectionContribution.VerifiedGoodBot"/>
///         (that early-exits / skips analysis) -- this raises a strong negative
///         confidence-delta contribution so detection still runs on every request
///         and the archetype only changes the SCORE. Priority 7, Wave 0.
///     </para>
/// </remarks>
public sealed class WebhookSensor : DetectorAtomBase
{
    private readonly ILogger<WebhookSensor> _logger;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly WebhookCatalog _catalog;
    private readonly IWebhookEndpointReputation? _reputation;

    public WebhookSensor(
        ILogger<WebhookSensor> logger,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor,
        WebhookCatalog? catalog = null,
        IWebhookEndpointReputation? reputation = null)
        : base(name: "Webhook", category: "Webhook")
    {
        _logger = logger;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
        _catalog = catalog ?? WebhookCatalog.Default;
        _reputation = reputation;
    }

    public override int Priority => 7;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    private double CorroboratedConfidenceDelta =>
        _configProvider.GetParameter(Name, "corroborated_confidence_delta", _catalog.CorroboratedConfidenceDelta);

    private double CorroboratedWeight =>
        _configProvider.GetParameter(Name, "corroborated_weight", _catalog.CorroboratedWeight);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var http = _httpContextAccessor.HttpContext;
        if (http is null) return Task.FromResult(None());

        var req = http.Request;
        if (!HttpMethods.IsPost(req.Method)) return Task.FromResult(None());

        var isJson = (req.ContentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase)
                  || (req.ContentType ?? "").Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);

        // shape = POST + json + a KNOWN webhook signature header present
        var provider = _catalog.Providers.FirstOrDefault(p => req.Headers.ContainsKey(p.SignatureHeader));
        var hasSignatureHeader = provider is not null
            || _catalog.SignatureHeaders.Any(h => req.Headers.ContainsKey(h));
        var isShape = isJson && hasSignatureHeader;
        if (!isShape) return Task.FromResult(None());

        sink.Raise(SignalKeys.WebhookShape, sessionId);

        var endpoint = req.Path.Value ?? "/";
        http.Items["sb.webhook.endpoint"] = endpoint;

        // Resolved CLIENT ip, not peer/proxy -- shared with IpAtom via ClientIpResolver.
        var ip = ClientIpResolver.Resolve(http);

        // Learning signal: every webhook-SHAPED request is observed, even when it is not
        // (yet) recognized -- this is what lets a genuine sender earn dominant-ip/verified
        // status over time instead of being trusted on the header alone.
        _reputation?.RecordRequest(endpoint, ip);

        // IP-based corroborators (the reputation store learns dominant/verified over time;
        // a null store -- no Task-3 registration -- means no IP corroboration is available).
        var dominant = _reputation?.IsDominantIp(endpoint, ip) ?? false;
        var verified = _reputation?.HasVerifiedRecord(endpoint, ip) ?? false;
        // A provider's PUBLISHED ip-range match also corroborates (not spoofable like the
        // header); ranges are seeded empty for now, so this stays false until filled --
        // cold-start relies on dominant/verified.
        var publishedRange = provider is not null && provider.IpRanges.Any(r => IpInCidr(ip, r));
        var namedProvider = provider is not null; // NAMES the provider; does NOT grant trust on its own

        // SPOOF GUARD: the signature header (shape) is spoofable, so trust requires an
        // IP-based corroborator -- never the header alone.
        var corroborated = dominant || verified || publishedRange;
        if (!corroborated)
        {
            // Shape-only (incl. a forged provider header from a fresh IP): observed for
            // learning, but NOT recognized -- scored normally by the rest of the pipeline.
            // This is the no-bypass proof.
            _logger.LogDebug(
                "Webhook: shape matched on {Endpoint} ({Provider}) but no IP-based corroborator for {Ip} -- not lowering score",
                endpoint, provider?.Name ?? "unnamed", PrivacyHelper.MaskIp(ip));
            return Task.FromResult(None());
        }

        sink.Raise($"{SignalKeys.WebhookDetected}:true", sessionId);
        if (namedProvider) sink.Raise($"{SignalKeys.WebhookProvider}:{provider!.Name}", sessionId);
        if (dominant) sink.Raise(SignalKeys.WebhookIpDominant, sessionId);
        if (verified) sink.Raise(SignalKeys.WebhookVerifiedRecord, sessionId);
        sink.Raise($"{SignalKeys.WebhookEndpoint}:{endpoint}", sessionId);

        var reason = namedProvider ? "provider" : dominant ? "dominant-ip" : "verified-record";
        _logger.LogInformation(
            "Webhook: {Provider} corroborated at {Endpoint} ({Reason}) -- low-threat, detection still runs",
            provider?.Name ?? "receiver", endpoint, reason);

        // Strong negative confidence delta = biases the aggregate toward low-threat.
        // NOT an early exit -> the request is still scored, logged and learned from.
        return Task.FromResult(Single(new DetectionContribution
        {
            DetectorName = Name,
            Category = Category,
            ConfidenceDelta = CorroboratedConfidenceDelta,
            Weight = CorroboratedWeight,
            Reason = $"Webhook {provider?.Name ?? "receiver"}: shape corroborated ({reason})",
            BotType = BotType.GoodBot.ToString(),
            BotName = provider?.Name ?? "Webhook"
        }));
    }

    /// <summary>CIDR containment check for a provider's published source-IP ranges.</summary>
    // TODO: CIDR match when provider ip_ranges are seeded (all seed catalogs ship [] today).
    private static bool IpInCidr(string ip, string cidr) => false;
}
