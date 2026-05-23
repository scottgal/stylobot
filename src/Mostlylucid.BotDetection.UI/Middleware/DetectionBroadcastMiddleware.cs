using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Middleware;

/// <summary>
///     Middleware that broadcasts REAL detection results to the SignalR dashboard hub.
///     Must run AFTER BotDetectionMiddleware to access the detection results.
/// </summary>
public partial class DetectionBroadcastMiddleware
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> CountryCodePropertyCache = new();

    /// <summary>
    ///     Signal key prefixes allowed through to dashboard (non-PII).
    ///     Stored as a FrozenSet for O(1) prefix lookup: extract everything up to and
    ///     including the first '.' from the signal key, then test set membership.
    ///     This replaces the O(n×m) Any(StartsWith) scan on every detection event.
    /// </summary>
    private static readonly System.Collections.Frozen.FrozenSet<string> AllowedSignalPrefixSet =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(
        [
            "ua.", "header.", "client.", "geo.", "ip.", "behavioral.",
            "detection.", "request.", "h2.", "tls.", "tcp.", "h3.",
            "cluster.", "reputation.", "honeypot.", "similarity.",
            "attack.", "ato.", "intent.", "heuristic.", "referrer.",
            "privacy.",
            // Risk dimension -- carries risk.justification (human-readable band
            // reason) and risk.friendly_pin_trace (fired/skipped/not-applicable
            // diagnostic for the friendly-bot pin). Both are classifier outputs
            // with no PII, but neither was reaching dashboard_detections.important_signals
            // because the prefix wasn't whitelisted -- audit rows had empty
            // justification + null trace so operators couldn't see why a
            // known-friendly UA was banded VeryHigh.
            "risk.",
            // Friendly-bot vendor IP verification signals (true / false / absent).
            "friendly."
        ], StringComparer.OrdinalIgnoreCase);

    private static bool IsAllowedSignal(string key)
    {
        var dot = key.IndexOf('.');
        if (dot < 0) return false;
        return AllowedSignalPrefixSet.Contains(key[..(dot + 1)]);
    }

    /// <summary>Signal keys that must never reach the dashboard.</summary>
    private static readonly HashSet<string> BlockedSignalKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ua.raw", "ip.address", "client_ip", "ip_address",
        "email", "phone", "session_id", "cookie", "authorization"
    };

    /// <summary>Maximum number of signals forwarded to dashboard per detection.</summary>
    private const int MaxSignalsPerDetection = 80;

    /// <summary>Maximum accepted length for X-Bot-Detection-Signals header (16 KB).</summary>
    private const int MaxUpstreamSignalsHeaderLength = 16_384;

    [System.Text.RegularExpressions.GeneratedRegex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}")]
    private static partial System.Text.RegularExpressions.Regex EmailPattern();
    private readonly ILogger<DetectionBroadcastMiddleware> _logger;
    private readonly RequestDelegate _next;

    public DetectionBroadcastMiddleware(
        RequestDelegate next,
        ILogger<DetectionBroadcastMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hubContext,
        IDashboardEventStore eventStore,
        IOptions<BotDetectionOptions> optionsAccessor,
        VisitorListCache visitorListCache,
        SignatureAggregateCache signatureAggregateCache,
        Mostlylucid.BotDetection.Orchestration.Telemetry.IDetectionEventPublisher detectionEventPublisher,
        IOptions<Mostlylucid.BotDetection.Dashboard.DetectionRecordOptions>? recordOptionsAccessor = null,
        SignatureDescriptionService? signatureDescriptionService = null)
    {
        // Call next middleware first (so detection runs)
        await _next(context);

        // After response, broadcast detection result if available
        try
        {
            // Fast path: upstream-trusted lightweight result (no AggregatedEvidence)
            if (!context.Items.ContainsKey(BotDetectionMiddleware.AggregatedEvidenceKey) &&
                context.Items.TryGetValue(BotDetectionMiddleware.BotDetectionResultKey, out var upstreamObj) &&
                upstreamObj is BotDetectionResult upstreamResult)
            {
                var detection = BuildDetectionFromUpstream(context, upstreamResult);

                // When configured, local-network traffic (loopback, RFC1918, link-local) is excluded
                // from BOTH the event store and the live broadcast: local pings like /admin/alive,
                // docker-internal health checks, and same-host curl probes should never count as
                // observed bot/human traffic in dashboards or aggregates.
                var options = optionsAccessor.Value;
                var excludeLocal = options.ExcludeLocalIpFromBroadcast
                                   && IsLocalIp(context.Connection.RemoteIpAddress);
                if (excludeLocal) return;

                var updatedSignature = await StoreDetectionAndSignatureAsync(context, detection, eventStore);
                signatureAggregateCache.UpdateFromDetection(detection);
                visitorListCache.Upsert(detection);

                // Beacon-only: signal which widgets need refreshing, never send full payloads
                await hubContext.Clients.All.BroadcastInvalidation("signature");
                await hubContext.Clients.All.BroadcastInvalidation("summary");

                // Invalidate threats widget for high-threat or honeypot detections
                if (detection.ThreatScore is > 0.3 || detection.Action == "simulation-pack")
                    await hubContext.Clients.All.BroadcastInvalidation("threats");

                // Fan out to any out-of-process event publisher (commercial Redis → separate
                // UI container). No-op in FOSS. Fire-and-forget: must not block the response.
                _ = PublishEventAsync(detectionEventPublisher, detection, context);

                // Lightweight attack arc for world map visualization (only data payload we send)
                if (detection.IsBot && !string.IsNullOrEmpty(detection.CountryCode)
                                    && detection.CountryCode != "XX" && detection.CountryCode != "LOCAL")
                    await hubContext.Clients.All.BroadcastAttackArc(detection.CountryCode, detection.RiskBand ?? "Low");

                _logger.LogDebug(
                    "Broadcast upstream detection: {Path} sig={Signature} prob={Probability:F2} hits={HitCount}",
                    detection.Path,
                    detection.PrimarySignature?[..Math.Min(8, detection.PrimarySignature.Length)],
                    detection.BotProbability,
                    updatedSignature.HitCount);
                return;
            }

            if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evidenceObj) &&
                evidenceObj is AggregatedEvidence evidence)
            {
                // Skip static assets with zero confidence - these are served by static file
                // middleware with no meaningful detection and pollute reputation history
                if (evidence.Confidence == 0 && evidence.TotalProcessingTimeMs < 0.5)
                {
                    _logger.LogDebug("Skipping broadcast for zero-confidence static asset: {Path}", context.Request.Path);
                    return;
                }

                var detection = BuildDetectionFromEvidence(context, evidence, recordOptionsAccessor?.Value);

                // Same local-IP exclusion as the upstream-result path above: drop the entire
                // detection (no event-store write, no cache update, no broadcast) when the source
                // is on a private/loopback network. Prevents /admin/alive style health-check
                // pings from polluting dashboard counts.
                var options = optionsAccessor.Value;
                var excludeLocal = options.ExcludeLocalIpFromBroadcast
                                   && IsLocalIp(context.Connection.RemoteIpAddress);
                if (excludeLocal) return;

                var updatedSignature = await StoreDetectionAndSignatureAsync(context, detection, eventStore);
                signatureAggregateCache.UpdateFromDetection(detection);
                visitorListCache.Upsert(detection);
                await hubContext.Clients.All.BroadcastInvalidation("signature");
                await hubContext.Clients.All.BroadcastInvalidation("summary");

                // Invalidate threats widget for high-threat or honeypot detections
                if (detection.ThreatScore is > 0.3 || detection.Action == "simulation-pack")
                    await hubContext.Clients.All.BroadcastInvalidation("threats");

                // Fan out to any out-of-process event publisher (commercial Redis → separate
                // UI container). No-op in FOSS. Fire-and-forget: must not block the response.
                _ = PublishEventAsync(detectionEventPublisher, detection, context, evidence);

                // Lightweight attack arc for world map visualization
                if (detection.IsBot && !string.IsNullOrEmpty(detection.CountryCode)
                                    && detection.CountryCode != "XX" && detection.CountryCode != "LOCAL")
                    await hubContext.Clients.All.BroadcastAttackArc(detection.CountryCode, detection.RiskBand ?? "Low");

                // Feed signature to SignatureDescriptionService for name/description synthesis.
                // Humans flow through the same pipeline — naming is no longer a bot-only concern.
                if (signatureDescriptionService != null &&
                    !string.IsNullOrEmpty(detection.PrimarySignature) && evidence.Signals is { Count: > 0 })
                {
                    var nullableSignals = evidence.Signals.ToDictionary(
                        s => s.Key, s => (object?)s.Value);
                    signatureDescriptionService.TrackSignature(detection.PrimarySignature, nullableSignals);
                }

                _logger.LogDebug(
                    "Broadcast detection: {Path} sig={Signature} prob={Probability:F2} hits={HitCount}",
                    detection.Path,
                    detection.PrimarySignature?[..Math.Min(8, detection.PrimarySignature.Length)],
                    detection.BotProbability,
                    updatedSignature.HitCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast detection");
        }
    }

    // ─── Shared storage: ONE path for detection + signature ───────────────

    /// <summary>
    ///     Single method that stores both detection and signature to the event store.
    ///     Every code path calls this - no duplication.
    /// </summary>
    private async Task<DashboardSignatureEvent> StoreDetectionAndSignatureAsync(
        HttpContext context,
        DashboardDetectionEvent detection,
        IDashboardEventStore eventStore)
    {
        await eventStore.AddDetectionAsync(detection);

        var factors = ParseSignatureFactors(context);
        var signature = new DashboardSignatureEvent
        {
            SignatureId = Guid.NewGuid().ToString("N")[..12],
            Timestamp = DateTime.UtcNow,
            PrimarySignature = detection.PrimarySignature ?? detection.RequestId,
            IpSignature = factors.IpSig,
            UaSignature = factors.UaSig,
            ClientSideSignature = factors.ClientSig,
            FactorCount = factors.FactorCount,
            RiskBand = detection.RiskBand,
            HitCount = 1, // DB increments on conflict
            IsKnownBot = detection.IsBot,
            BotName = detection.BotName,
            BotProbability = detection.BotProbability,
            Confidence = detection.Confidence,
            ProcessingTimeMs = detection.ProcessingTimeMs,
            BotType = detection.BotType,
            Action = detection.Action,
            LastPath = detection.Path,
            Narrative = detection.Narrative,
            Description = detection.Description,
            TopReasons = detection.TopReasons?.ToList(),
            ThreatScore = detection.ThreatScore,
            ThreatBand = detection.ThreatBand,
            RiskJustification = detection.RiskJustification,
        };

        return await eventStore.AddSignatureAsync(signature);
    }

    // ─── Detection builders ──────────────────────────────────────────────

    /// <summary>
    ///     Build a DashboardDetectionEvent from full AggregatedEvidence (local detection path).
    ///     When <paramref name="recordOptions"/> is provided the Domain, Referer, ReferrerHost and
    ///     UaDeviceClass analytics fields are populated according to the capture flags; Domain is
    ///     always set regardless of flags as it is the multi-domain partition key.
    /// </summary>
    private DashboardDetectionEvent BuildDetectionFromEvidence(
        HttpContext context,
        AggregatedEvidence evidence,
        Mostlylucid.BotDetection.Dashboard.DetectionRecordOptions? recordOptions = null)
    {
        var sigValue = ResolvePrimarySignature(context);
        var countryCode = ResolveCountryCode(context, evidence.Signals);

        var topReasons = evidence.Contributions
            .Where(c => !string.IsNullOrEmpty(c.Reason))
            .OrderByDescending(c => Math.Abs(c.ConfidenceDelta * c.Weight))
            .Take(5)
            .Select(c => c.Reason!)
            .ToList();

        var detectorContributions = evidence.Contributions
            .GroupBy(c => c.DetectorName)
            .ToDictionary(
                g => g.Key,
                g => new DashboardDetectorContribution
                {
                    ConfidenceDelta = g.Sum(c => c.ConfidenceDelta),
                    Contribution = g.Sum(c => c.ConfidenceDelta * c.Weight),
                    Reason = string.Join("; ", g.Select(c => c.Reason).Where(r => !string.IsNullOrEmpty(r))),
                    ExecutionTimeMs = g.Sum(c => c.ProcessingTimeMs),
                    Priority = g.First().Priority
                });

        var importantSignals = BuildImportantSignals(context, evidence.Signals, ref countryCode);

        // Analytics capture: Domain is the multi-domain partition key; always populated.
        // Referer/ReferrerHost/UaDeviceClass require the corresponding DetectionRecordOptions
        // hooks to be set (wired by the commercial analytics capture plugin at startup).
        var rawReferer = context.Request.Headers.Referer.ToString();
        var captureReferer = recordOptions?.IncludeReferer == true && !string.IsNullOrEmpty(rawReferer);
        var derivedReferrerHost = (captureReferer && recordOptions?.DeriveReferrerHost is not null)
            ? recordOptions.DeriveReferrerHost(rawReferer)
            : null;
        // Pass the raw User-Agent string (not ua.family) so device class can distinguish
        // iOS Safari from Desktop Safari via OS tokens (iPhone, Android, Mobile).
        var rawUa = context.Request.Headers.UserAgent.ToString();
        var derivedUaDeviceClass = recordOptions?.DeriveUaDeviceClass is not null
            ? recordOptions.DeriveUaDeviceClass(rawUa)
            : null;

        var detection = new DashboardDetectionEvent
        {
            RequestId = context.TraceIdentifier,
            Timestamp = DateTime.UtcNow,
            IsBot = evidence.BotProbability > 0.5,
            BotProbability = evidence.BotProbability,
            Confidence = evidence.Confidence,
            RiskBand = evidence.RiskBand.ToString(),
            BotType = evidence.PrimaryBotType?.ToString(),
            BotName = evidence.PrimaryBotName,
            Action = evidence.PolicyAction?.ToString() ?? evidence.TriggeredActionPolicyName ?? "Allow",
            PolicyName = evidence.PolicyName ?? "Default",
            Method = context.Request.Method,
            Path = context.Request.Path.Value ?? "/",
            StatusCode = context.Response.StatusCode,
            ProcessingTimeMs = evidence.TotalProcessingTimeMs,
            PrimarySignature = sigValue,
            CountryCode = countryCode,
            UserAgent = evidence.BotProbability > 0.5 ? SanitizeUserAgent(context.Request.Headers.UserAgent.ToString()) : null,
            UserAgentRaw = Mostlylucid.BotDetection.Privacy.UaPiiStripper.Strip(context.Request.Headers.UserAgent.ToString()),
            TopReasons = topReasons,
            DetectorContributions = detectorContributions.Count > 0 ? detectorContributions : null,
            ImportantSignals = importantSignals,
            ThreatScore = evidence.ThreatScore > 0 ? evidence.ThreatScore : null,
            ThreatBand = evidence.ThreatBand != Orchestration.ThreatBand.None
                ? evidence.ThreatBand.ToString() : null,
            RiskJustification = evidence.RiskJustification,
            Domain = context.Request.Host.Host?.ToLowerInvariant(),
            Referer = captureReferer ? rawReferer : null,
            ReferrerHost = derivedReferrerHost,
            UaDeviceClass = derivedUaDeviceClass,
        };

        return detection with { Narrative = DetectionNarrativeBuilder.Build(detection) };
    }

    /// <summary>
    ///     Build a DashboardDetectionEvent from upstream-trusted BotDetectionResult (no AggregatedEvidence).
    /// </summary>
    private DashboardDetectionEvent BuildDetectionFromUpstream(HttpContext context, BotDetectionResult result)
    {
        var sigValue = ResolvePrimarySignature(context);
        var botProbability = result.ConfidenceScore; // Legacy field: actually holds bot probability
        var riskBand = botProbability switch
        {
            >= 0.85 => "VeryHigh",
            >= 0.7 => "High",
            >= 0.4 => "Medium",
            >= 0.2 => "Low",
            _ => "VeryLow"
        };

        var detectionConfidence = botProbability;
        Dictionary<string, DashboardDetectorContribution>? detectorContributions = null;

        if (context.Items.TryGetValue(BotDetectionMiddleware.DetectionConfidenceKey, out var confObj) &&
            confObj is double parsedConf)
            detectionConfidence = parsedConf;
        else if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evidenceObj) &&
                 evidenceObj is AggregatedEvidence upstreamEvidence)
        {
            detectionConfidence = upstreamEvidence.Confidence;

            // Extract detector contributions from evidence when available
            var contributions = upstreamEvidence.Contributions
                .GroupBy(c => c.DetectorName)
                .ToDictionary(
                    g => g.Key,
                    g => new DashboardDetectorContribution
                    {
                        ConfidenceDelta = g.Sum(c => c.ConfidenceDelta),
                        Contribution = g.Sum(c => c.ConfidenceDelta * c.Weight),
                        Reason = string.Join("; ", g.Select(c => c.Reason).Where(r => !string.IsNullOrEmpty(r))),
                        ExecutionTimeMs = g.Sum(c => c.ProcessingTimeMs),
                        Priority = g.First().Priority
                    });
            if (contributions.Count > 0)
                detectorContributions = contributions;
        }

        double upstreamProcessingMs = 0;
        if (context.Request.Headers.TryGetValue("X-Bot-Detection-ProcessingMs", out var procHeader))
            double.TryParse(procHeader.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out upstreamProcessingMs);

        var topReasons = result.Reasons
            .Where(r => !string.IsNullOrEmpty(r.Detail))
            .Take(5)
            .Select(r => r.Detail!)
            .ToList();

        var upstreamCountry = context.Request.Headers["X-Bot-Detection-Country"].FirstOrDefault()
                              ?? ResolveCountryFromHeaders(context);

        var botType = result.BotType?.ToString();

        var importantSignals = ParseUpstreamSignals(context);
        EnrichProtocol(context, importantSignals);

        var dashboardOptions = context.RequestServices.GetService<IOptions<StyloBotDashboardOptions>>()?.Value;
        if (dashboardOptions?.EnrichHumanSignals == true)
            EnrichFromRequest(context, importantSignals, ref upstreamCountry);

        var detection = new DashboardDetectionEvent
        {
            RequestId = context.TraceIdentifier,
            Timestamp = DateTime.UtcNow,
            IsBot = result.IsBot,
            BotProbability = botProbability,
            Confidence = detectionConfidence,
            RiskBand = riskBand,
            BotType = botType,
            BotName = result.BotName,
            Action = context.Request.Headers["X-Bot-Detection-Action"].FirstOrDefault() ?? "Allow",
            PolicyName = "upstream",
            Method = context.Request.Method,
            Path = context.Request.Path.Value ?? "/",
            StatusCode = context.Response.StatusCode,
            ProcessingTimeMs = upstreamProcessingMs,
            PrimarySignature = sigValue,
            CountryCode = upstreamCountry,
            UserAgent = result.IsBot ? SanitizeUserAgent(context.Request.Headers.UserAgent.ToString()) : null,
            UserAgentRaw = Mostlylucid.BotDetection.Privacy.UaPiiStripper.Strip(context.Request.Headers.UserAgent.ToString()),
            TopReasons = topReasons,
            DetectorContributions = detectorContributions,
            ImportantSignals = importantSignals,
            ThreatScore = importantSignals.TryGetValue("intent.threat_score", out var tsObj)
                && tsObj is double tsVal ? tsVal : null,
            ThreatBand = importantSignals.TryGetValue("intent.threat_band", out var tbObj)
                ? tbObj?.ToString() : null,
            // Domain is unconditional for the upstream path too (multi-domain partition key)
            Domain = context.Request.Host.Host?.ToLowerInvariant(),
        };

        return detection with { Narrative = DetectionNarrativeBuilder.Build(detection) };
    }

    // ─── Shared helpers (used by all paths) ──────────────────────────────

    /// <summary>
    ///     Parse signature factors from HttpContext.Items - ONE place, not three.
    /// </summary>
    private record SignatureFactors(string? IpSig, string? UaSig, string? ClientSig, int FactorCount);

    private static SignatureFactors ParseSignatureFactors(HttpContext context)
    {
        if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evObj)
            && evObj is AggregatedEvidence ev
            && ev.Signals.TryGetValue(SignalKeys.SignatureMultifactor, out var multiObj)
            && multiObj is Mostlylucid.BotDetection.Dashboard.MultiFactorSignatures mfs)
        {
            var factorCount = 0;
            if (!string.IsNullOrEmpty(mfs.IpSignature)) factorCount++;
            if (!string.IsNullOrEmpty(mfs.UaSignature)) factorCount++;
            if (!string.IsNullOrEmpty(mfs.ClientSideSignature)) factorCount++;
            if (!string.IsNullOrEmpty(mfs.PluginSignature)) factorCount++;
            if (!string.IsNullOrEmpty(mfs.IpSubnetSignature)) factorCount++;
            if (!string.IsNullOrEmpty(mfs.GeoSignature)) factorCount++;
            return new SignatureFactors(
                mfs.IpSignature,
                mfs.UaSignature,
                mfs.ClientSideSignature,
                Math.Max(1, factorCount));
        }

        return new SignatureFactors(null, null, null, 1);
    }

    /// <summary>
    ///     Resolve the primary signature from HttpContext, falling back to hash-based generation.
    /// </summary>
    private string ResolvePrimarySignature(HttpContext context)
    {
        if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evObj)
            && evObj is AggregatedEvidence ev)
        {
            if (ev.Signals.TryGetValue(SignalKeys.PrimarySignature, out var sig)
                && sig is string primary && !string.IsNullOrEmpty(primary))
                return primary;

            if (ev.Signals.TryGetValue(SignalKeys.SignatureMultifactor, out var multiObj)
                && multiObj is Mostlylucid.BotDetection.Dashboard.MultiFactorSignatures mfs
                && !string.IsNullOrEmpty(mfs.PrimarySignature))
                return mfs.PrimarySignature;
        }

        return GenerateFallbackSignature(context);
    }

    /// <summary>
    ///     Resolve country code from evidence signals, GeoLocation context, and headers.
    /// </summary>
    private static string? ResolveCountryCode(HttpContext context, IReadOnlyDictionary<string, object>? signals)
    {
        // From detection signals
        if (signals != null &&
            signals.TryGetValue("geo.country_code", out var ccObj) &&
            ccObj is string cc && cc != "LOCAL")
            return cc;

        // From GeoLocation middleware context
        if (context.Items.TryGetValue("GeoLocation", out var geoLocObj) && geoLocObj != null)
        {
            var countryProp = CountryCodePropertyCache.GetOrAdd(
                geoLocObj.GetType(), t => t.GetProperty("CountryCode"));
            if (countryProp?.GetValue(geoLocObj) is string geoCC && !string.IsNullOrEmpty(geoCC) && geoCC != "LOCAL")
                return geoCC;
        }

        // From upstream headers
        return ResolveCountryFromHeaders(context);
    }

    /// <summary>
    ///     Build filtered non-PII signals dictionary from evidence signals.
    /// </summary>
    private static Dictionary<string, object> BuildImportantSignals(
        HttpContext context,
        IReadOnlyDictionary<string, object>? signals,
        ref string? countryCode)
    {
        var importantSignals = new Dictionary<string, object>();
        if (signals is { Count: > 0 })
        {
            importantSignals = signals
                .Where(s => IsAllowedSignal(s.Key) && !BlockedSignalKeys.Contains(s.Key))
                .Take(MaxSignalsPerDetection)
                .ToDictionary(s => s.Key, s => s.Value);
        }

        EnrichProtocol(context, importantSignals);

        var dashboardOptions = context.RequestServices.GetService<IOptions<StyloBotDashboardOptions>>()?.Value;
        if (dashboardOptions?.EnrichHumanSignals == true)
            EnrichFromRequest(context, importantSignals, ref countryCode);

        return importantSignals;
    }

    /// <summary>
    ///     Parse upstream gateway signals from X-Bot-Detection-Signals header.
    /// </summary>
    private Dictionary<string, object> ParseUpstreamSignals(HttpContext context)
    {
        var importantSignals = new Dictionary<string, object>();
        var upstreamSignalsHeader = context.Request.Headers["X-Bot-Detection-Signals"].FirstOrDefault();
        if (string.IsNullOrEmpty(upstreamSignalsHeader) || upstreamSignalsHeader.Length > MaxUpstreamSignalsHeaderLength)
            return importantSignals;

        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(upstreamSignalsHeader);
            if (parsed != null)
            {
                var count = 0;
                foreach (var kvp in parsed)
                {
                    if (count >= MaxSignalsPerDetection) break;
                    if (BlockedSignalKeys.Contains(kvp.Key)) continue;
                    if (!IsAllowedSignal(kvp.Key)) continue;

                    object value = kvp.Value.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.String => kvp.Value.GetString()!,
                        System.Text.Json.JsonValueKind.Number => kvp.Value.TryGetInt64(out var l) ? l : kvp.Value.GetDouble(),
                        System.Text.Json.JsonValueKind.True => true,
                        System.Text.Json.JsonValueKind.False => false,
                        _ => kvp.Value.ToString()
                    };
                    importantSignals[kvp.Key] = value;
                    count++;
                }
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to deserialize upstream signals header");
        }

        return importantSignals;
    }

    // ─── Static utilities ────────────────────────────────────────────────

    internal static bool IsLocalIp(IPAddress? ip) => Mostlylucid.BotDetection.Helpers.NetworkHelper.IsLocalIp(ip);

    private static string? ResolveCountryFromHeaders(HttpContext context)
    {
        var country = context.Request.Headers["X-Country"].FirstOrDefault()
                      ?? context.Request.Headers["CF-IPCountry"].FirstOrDefault();

        if (!string.IsNullOrEmpty(country) && country != "XX" && country != "LOCAL")
            return country;

        if (context.Items.TryGetValue("GeoDetection.CountryCode", out var geoCtx) &&
            geoCtx is string geoCountry && !string.IsNullOrEmpty(geoCountry) && geoCountry != "LOCAL")
            return geoCountry;

        return null;
    }

    private static string? SanitizeUserAgent(string? ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return null;
        return EmailPattern().Replace(ua, "[email-redacted]");
    }

    private string GenerateFallbackSignature(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var ua = context.Request.Headers.UserAgent.ToString();
        var combined = $"{ip}:{ua}";

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static void EnrichProtocol(HttpContext context, Dictionary<string, object> signals)
    {
        // Prefer forwarded client protocol from the edge proxy. Behind a CF tunnel /
        // Caddy / similar, context.Request.Protocol always reads as the upstream's
        // connection to the gateway (HTTP/1.1 by default), NOT the browser's actual
        // protocol to the edge. Header names checked in priority order:
        //   - Sb-Http-Version       : Cloudflare Transform Rule (preferred, avoids
        //                             X- prefix rejection some CF setups hit)
        //   - X-Client-HTTP-Version : older Transform Rule recipe
        //   - X-Forwarded-Proto-Version : some operator setups normalise to this
        //   - X-Client-Protocol     : Caddy convention
        // Fall through to Request.Protocol (the cloudflared/Caddy -> Kestrel hop)
        // when none of those are present.
        var protocol = context.Request.Headers["Sb-Http-Version"].FirstOrDefault()
                       ?? context.Request.Headers["X-Client-HTTP-Version"].FirstOrDefault()
                       ?? context.Request.Headers["X-Forwarded-Proto-Version"].FirstOrDefault()
                       ?? context.Request.Headers["X-Client-Protocol"].FirstOrDefault()
                       ?? context.Request.Protocol;
        signals.TryAdd("request.protocol", protocol);

        // Additional CF Transform Rule signals -- all free-tier accessible. See
        // docs/cloudflare-tunnel-setup.md for the rule recipe. Each is optional;
        // origin without CF (or without the rules deployed) just doesn't see them.
        var tlsVersion = context.Request.Headers["X-Client-TLS-Version"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(tlsVersion))
        {
            signals.TryAdd("tls.version", tlsVersion);
            signals.TryAdd("tls.Version", tlsVersion); // signature card reads either case
        }
        var tlsCipher = context.Request.Headers["X-Client-TLS-Cipher"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(tlsCipher))
            signals.TryAdd("tls.cipher", tlsCipher);
        var tlsExtSha1 = context.Request.Headers["X-Client-TLS-Ext-Sha1"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(tlsExtSha1))
            signals.TryAdd("tls.client_extensions_sha1", tlsExtSha1);
        var asnHeader = context.Request.Headers["X-Client-ASN"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(asnHeader))
            signals.TryAdd("ip.asn", asnHeader);

        if (!signals.ContainsKey("h3.protocol") && !signals.ContainsKey("h3.version") &&
            !signals.ContainsKey("h2.fingerprint") && !signals.ContainsKey("h2.settings_hash") &&
            !signals.ContainsKey("h2.protocol"))
        {
            if (protocol.Contains("3", StringComparison.Ordinal))
                signals.TryAdd("h3.protocol", "h3");
            else if (protocol.Contains("2", StringComparison.Ordinal))
                signals.TryAdd("h2.protocol", "h2");
        }

        // Capture Accept-Encoding for compression type signal
        var acceptEncoding = context.Request.Headers.AcceptEncoding.ToString();
        if (!string.IsNullOrEmpty(acceptEncoding))
            signals.TryAdd("request.accept_encoding", acceptEncoding);

        // Extract referrer domain (non-PII: host only, no path/query)
        var referrer = context.Request.Headers.Referer.ToString();
        if (!string.IsNullOrEmpty(referrer))
        {
            try
            {
                var uri = new Uri(referrer);
                signals.TryAdd("referrer.domain", uri.Host);
            }
            catch
            {
                // Malformed referrer -- store raw
                signals.TryAdd("referrer.domain", referrer);
            }
        }
    }

    private static void EnrichFromRequest(HttpContext context, Dictionary<string, object> signals, ref string? countryCode)
    {
        if (!signals.ContainsKey("ua.browser") && !signals.ContainsKey("ua.family"))
        {
            var ua = context.Request.Headers.UserAgent.ToString();
            if (!string.IsNullOrEmpty(ua))
            {
                var (browser, version) = ParseBrowserFromUa(ua);
                if (browser != null)
                {
                    signals.TryAdd("ua.browser", browser);
                    if (version != null)
                        signals.TryAdd("ua.browser_version", version);
                }
            }
        }

        if (countryCode == null &&
            context.Items.TryGetValue("GeoLocation", out var geoLocObj) && geoLocObj != null)
        {
            var countryProp = CountryCodePropertyCache.GetOrAdd(
                geoLocObj.GetType(), t => t.GetProperty("CountryCode"));
            if (countryProp?.GetValue(geoLocObj) is string geoCC && !string.IsNullOrEmpty(geoCC) && geoCC != "LOCAL")
                countryCode = geoCC;
        }

        if (countryCode == null)
            countryCode = ResolveCountryFromHeaders(context);
    }

    private static (string? Browser, string? Version) ParseBrowserFromUa(string ua)
    {
        var (family, version) = Mostlylucid.BotDetection.Helpers.UserAgentParser.Parse(ua);
        // Return null for bot/tool UAs and "Other" to preserve the original behavior
        // where bots returned (null, null) and only browsers populated signals
        if (family is "Other" or "curl" or "Python" or "Go" or "Java" or "Node.js" or "wget"
            or "Googlebot" or "Bingbot" or "GPTBot" or "ClaudeBot")
            return (null, null);
        return (family, version);
    }

    /// <summary>
    ///     Projects the in-process <c>DashboardDetectionEvent</c> into the transport-friendly
    ///     <c>DetectionEvent</c> record and hands it to the registered publisher. Catches and
    ///     logs any failure - publish errors must never affect request processing.
    /// </summary>
    private async Task PublishEventAsync(
        Mostlylucid.BotDetection.Orchestration.Telemetry.IDetectionEventPublisher publisher,
        DashboardDetectionEvent detection,
        HttpContext context,
        AggregatedEvidence? evidence = null)
    {
        try
        {
            var topReasons = evidence?.Contributions
                .Where(c => !string.IsNullOrWhiteSpace(c.Reason))
                .OrderByDescending(c => Math.Abs(c.ConfidenceDelta * c.Weight))
                .Take(5)
                .Select(c => c.Reason!)
                .ToList();

            var detectorContribs = evidence?.Contributions
                .Where(c => !string.IsNullOrWhiteSpace(c.DetectorName))
                .GroupBy(c => c.DetectorName!)
                .ToDictionary(g => g.Key, g => g.Sum(c => c.ConfidenceDelta * c.Weight));

            var evt = new Mostlylucid.BotDetection.Orchestration.Telemetry.DetectionEvent
            {
                Timestamp = detection.Timestamp,
                RequestId = context.TraceIdentifier,
                Signature = detection.PrimarySignature ?? "",
                Path = detection.Path,
                Method = detection.Method,
                StatusCode = detection.StatusCode,
                IsBot = detection.IsBot,
                BotProbability = detection.BotProbability,
                Confidence = detection.Confidence,
                RiskBand = detection.RiskBand,
                ThreatBand = detection.ThreatBand,
                Action = detection.Action,
                BotName = detection.BotName,
                BotType = detection.BotType,
                CountryCode = detection.CountryCode,
                ProcessingTimeMs = evidence?.TotalProcessingTimeMs ?? 0,
                DetectorContributions = detectorContribs,
                TopReasons = topReasons,
                GatewayId = Environment.GetEnvironmentVariable("STYLOBOT_GATEWAY_ID")
                            ?? Environment.MachineName
            };
            await publisher.PublishAsync(evt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Detection event publisher {Name} threw - dropping event", publisher.Name);
        }
    }
}
