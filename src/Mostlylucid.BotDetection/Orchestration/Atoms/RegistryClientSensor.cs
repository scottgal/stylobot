using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Definitions.RegistryClients;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Recognizes legitimate container-registry / OCI Distribution Spec clients
///     (docker, containerd, podman, skopeo, buildkit, oras, Helm OCI, ...) so a
///     real <c>docker pull</c> through the gateway is NOT tarpitted -- WITHOUT a
///     bypass. The fix is in detection: the score is made correct, never skipped.
/// </summary>
/// <remarks>
///     <para>
///         The UA family is only a hint. A request earns the strong low-threat
///         contribution ONLY when the UA family is corroborated by the registry
///         v2 protocol behaviour (an OCI <c>/v2/</c> path, or a registry manifest
///         <c>Accept</c> media type). A scraper spoofing <c>docker/24</c> that is
///         not actually doing the registry protocol gets NO score reduction and
///         is scored normally (the spoof guard = the negative test).
///     </para>
///     <para>
///         Deliberately NOT a <see cref="DetectionContribution.VerifiedGoodBot"/>
///         (that early-exits / skips analysis) - this raises a strong negative
///         confidence-delta contribution so detection still runs on every request
///         and the archetype only changes the SCORE. Priority 8, Wave 0, before
///         AiScraper (9) and UserAgent (10). See
///         docs/architecture/registry-client-archetype.md.
///     </para>
/// </remarks>
public sealed class RegistryClientSensor : DetectorAtomBase
{
    private readonly ILogger<RegistryClientSensor> _logger;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly RegistryClientCatalog _catalog;
    private readonly IRegistryClientCorroborationTracker? _tracker;

    public RegistryClientSensor(
        ILogger<RegistryClientSensor> logger,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor,
        RegistryClientCatalog? catalog = null,
        IRegistryClientCorroborationTracker? tracker = null)
        : base(name: "RegistryClient", category: "RegistryClient")
    {
        _logger = logger;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
        _catalog = catalog ?? RegistryClientCatalog.Default;
        _tracker = tracker;
    }

    public override int Priority => 8;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    private double CorroboratedConfidenceDelta =>
        _configProvider.GetParameter(Name, "corroborated_confidence_delta", _catalog.CorroboratedConfidenceDelta);

    private double CorroboratedWeight =>
        _configProvider.GetParameter(Name, "corroborated_weight", _catalog.CorroboratedWeight);

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return None();

        var request = context.Request;
        var ua = request.Headers.UserAgent.ToString();
        var path = request.Path.Value ?? string.Empty;
        var signature = sink.ReadHint(SignalKeys.PrimarySignature);

        // --- Behavioural truth (OCI Distribution Spec) ---
        var step = RegistryStep(path);          // ping / manifest / blob / upload / tags / v2 / null
        var isOciV2 = step is not null;
        var hasRegistryAccept = HasRegistryAccept(request.Headers.Accept);
        var hasBearer = request.Headers.Authorization.ToString()
            .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

        // --- UA-family hint ---
        var family = MatchFamily(ua, isOciV2);

        // Harbor's own management/auth REST API (/api/v2.0/*) is NOT the OCI Distribution
        // Spec: real docker/skopeo/containerd clients never call it directly, so there is
        // no safe UA family to key on here (any curl/script hitting a JSON API would
        // otherwise qualify - an allowlist in disguise, which CLAUDE.md forbids). Instead
        // an identity EARNS low-threat treatment on this surface only by having proven
        // real corroborated /v2/ OCI behaviour recently (see MarkCorroboratedAsync below) -
        // genuine behavioural correlation scoped to that one fingerprint, never leaking
        // to unrelated traffic sharing the same IP/UA. See docs/architecture/
        // registry-client-archetype.md.
        if (family is null && !isOciV2 && IsHarborManagementApiPath(path))
        {
            if (_tracker is not null && !string.IsNullOrEmpty(signature)
                && _tracker.IsRecentlyCorroborated(signature))
            {
                sink.Raise(SignalKeys.RegistryV2Ran, sessionId);
                sink.Raise($"{SignalKeys.RegistryClientDetected}:true", sessionId);
                sink.Raise($"{SignalKeys.RegistryClientName}:{InheritedTrustClientName}", sessionId);
                sink.Raise(SignalKeys.RegistryClientInheritedTrust, sessionId);
                _logger.LogInformation(
                    "RegistryClient: Harbor management API call on {Path} inherits trust from this " +
                    "fingerprint's recent corroborated /v2/ OCI behaviour -- low-threat, detection still runs",
                    path);
                return Single(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = CorroboratedConfidenceDelta,
                    Weight = CorroboratedWeight,
                    Reason = "Harbor management API call inherits trust from this fingerprint's recent " +
                        "corroborated /v2/ OCI registry-client behaviour",
                    BotType = BotType.RegistryClient.ToString(),
                    BotName = InheritedTrustClientName
                });
            }
            return None();
        }

        // Nothing registry-shaped at all -> stay out of the way.
        if (family is null && !isOciV2) return None();

        // The sensor looked at a registry-relevant request (observability + negative test).
        sink.Raise(SignalKeys.RegistryV2Ran, sessionId);
        if (step is not null) sink.Raise($"{SignalKeys.RegistryV2Step}:{step}", sessionId);
        if (hasRegistryAccept) sink.Raise(SignalKeys.RegistryAcceptManifest, sessionId);
        if (hasBearer) sink.Raise(SignalKeys.RegistryAuthBearer, sessionId);

        // Corroboration: a named family AND behaviour that proves the registry protocol.
        // Bearer alone is not enough (every API uses bearer); the /v2/ path or the
        // registry manifest Accept media is the behavioural proof.
        var corroborated = family is not null && (isOciV2 || hasRegistryAccept);

        if (!corroborated)
        {
            // UA claims a registry client but isn't doing the protocol -> spoof-suspect.
            // No score reduction; it is scored normally by the rest of the pipeline.
            if (family is not null)
            {
                sink.Raise(SignalKeys.RegistryUaOnly, sessionId);
                _logger.LogDebug(
                    "RegistryClient: UA claims {Client} but no registry v2 behaviour on {Path} -- not lowering score",
                    family.ClientName, path);
            }
            return None();
        }

        var name = family!.ClientName;
        var version = ExtractVersion(ua, family.VersionToken ?? family.Pattern.TrimEnd('/'));
        var display = string.IsNullOrEmpty(version) ? name : $"{name} {version}";

        sink.Raise($"{SignalKeys.RegistryClientDetected}:true", sessionId);
        sink.Raise($"{SignalKeys.RegistryClientName}:{name}", sessionId);
        if (!string.IsNullOrEmpty(version))
            sink.Raise($"{SignalKeys.RegistryClientVersion}:{version}", sessionId);

        _logger.LogInformation(
            "RegistryClient: {Client} corroborated by registry v2 protocol ({Step}) -- low-threat, detection still runs",
            display, step ?? (hasRegistryAccept ? "accept-media" : "?"));

        // Real OCI corroboration earned - let this fingerprint inherit trust onto its
        // own Harbor management-API calls for a short bounded window (see
        // RegistryClientCorroborationTracker; never set by a UA claim or by the
        // /api/v2.0 request itself).
        if (_tracker is not null && !string.IsNullOrEmpty(signature))
            await _tracker.MarkCorroboratedAsync(signature).ConfigureAwait(false);

        // Strong negative confidence delta = biases the aggregate toward low-threat.
        // NOT an early exit -> the request is still scored, logged and learned from.
        return Single(new DetectionContribution
        {
            DetectorName = Name,
            Category = Category,
            ConfidenceDelta = CorroboratedConfidenceDelta,
            Weight = CorroboratedWeight,
            Reason = $"Registry client {display}: OCI/Docker v2 protocol corroborated ({step ?? "accept-media"})",
            BotType = BotType.RegistryClient.ToString(),
            BotName = display
        });
    }

    /// <summary>Finds the UA family. <c>requires_oci_path</c> families only match on an OCI /v2/ path.</summary>
    private RegistryClientFamily? MatchFamily(string ua, bool isOciV2)
    {
        if (string.IsNullOrEmpty(ua)) return null;
        foreach (var f in _catalog.Families)
        {
            if (!ua.Contains(f.Pattern, StringComparison.OrdinalIgnoreCase)) continue;
            if (f.RequiresOciPath && !isOciV2) continue;
            return f;
        }
        return null;
    }

    private bool HasRegistryAccept(string acceptHeader)
    {
        if (string.IsNullOrEmpty(acceptHeader)) return false;
        foreach (var media in _catalog.AcceptMedia)
            if (acceptHeader.Contains(media, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    ///     Classifies the OCI Distribution Spec path shape. Returns the step name
    ///     or null when the path is not a registry v2 path. These are protocol
    ///     identifiers from the OCI spec (structural), not a curated list.
    /// </summary>
    private static string? RegistryStep(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (path.Equals("/v2", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/v2/", StringComparison.OrdinalIgnoreCase)) return "ping";
        if (!path.StartsWith("/v2/", StringComparison.OrdinalIgnoreCase)) return null;
        if (path.Contains("/manifests/", StringComparison.OrdinalIgnoreCase)) return "manifest";
        if (path.Contains("/blobs/uploads", StringComparison.OrdinalIgnoreCase)) return "upload";
        if (path.Contains("/blobs/", StringComparison.OrdinalIgnoreCase)) return "blob";
        if (path.EndsWith("/tags/list", StringComparison.OrdinalIgnoreCase)) return "tags";
        return "v2";
    }

    /// <summary>Display name used on the inherited-trust contribution (no single UA to name).</summary>
    private const string InheritedTrustClientName = "Registry client (management API)";

    /// <summary>
    ///     Harbor's own versioned management/auth REST API (project, robot-account,
    ///     quota, replication endpoints, etc.) - a distinct, documented surface from the
    ///     OCI Distribution Spec under <c>/v2/</c>. Structural path-shape check, not a
    ///     curated list.
    /// </summary>
    private static bool IsHarborManagementApiPath(string path)
        => path.StartsWith("/api/v2.0/", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/api/v2.0", StringComparison.OrdinalIgnoreCase);

    /// <summary>Extracts a dotted-numeric version following <c>&lt;token&gt;/</c> in the UA (no regex, AOT-safe).</summary>
    private static string? ExtractVersion(string ua, string token)
    {
        if (string.IsNullOrEmpty(ua) || string.IsNullOrEmpty(token)) return null;
        var needle = token + "/";
        var i = ua.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var start = i + needle.Length;
        if (start < ua.Length && (ua[start] == 'v' || ua[start] == 'V')) start++;
        var end = start;
        while (end < ua.Length && (char.IsDigit(ua[end]) || ua[end] == '.')) end++;
        var version = ua.Substring(start, end - start).Trim('.');
        return version.Length > 0 && char.IsDigit(version[0]) ? version : null;
    }
}
