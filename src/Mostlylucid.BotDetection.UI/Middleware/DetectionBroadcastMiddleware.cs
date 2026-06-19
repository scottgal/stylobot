using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
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
    /// <summary>
    ///     Per-Type cache of a compiled <c>obj =&gt; ((SomeGeoLocationType)obj).CountryCode</c>
    ///     accessor. The dashboard layer doesn't build-time reference the GeoDetection
    ///     <c>GeoLocation</c> class (we accept any GeoLocation provider that exposes a
    ///     <c>CountryCode</c> property on its result object), so the historical implementation
    ///     called <c>Type.GetProperty("CountryCode")?.GetValue(obj)</c> on every request that
    ///     had a populated GeoLocation context item. Reflection invocation per request is
    ///     measurably slower than a one-time Expression-compiled delegate; this cache pays
    ///     the expression compile cost once per distinct type and amortises it across every
    ///     subsequent request. Returns null for a type that has no readable
    ///     <c>CountryCode</c> string property so the upstream-header fallback still kicks in.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, Func<object, string?>?> CountryCodeAccessorCache = new();

    private static Func<object, string?>? GetCountryCodeAccessor(Type t)
        => CountryCodeAccessorCache.GetOrAdd(t, static type =>
        {
            var prop = type.GetProperty("CountryCode");
            if (prop is null || !prop.CanRead || prop.PropertyType != typeof(string)) return null;
            var param = System.Linq.Expressions.Expression.Parameter(typeof(object), "o");
            var cast = System.Linq.Expressions.Expression.Convert(param, type);
            var access = System.Linq.Expressions.Expression.Property(cast, prop);
            return System.Linq.Expressions.Expression
                .Lambda<Func<object, string?>>(access, param)
                .Compile();
        });

    // Outbound broadcasts route through SignalRBroadcastConstrainer so the
    // detection-rate doesn't dictate the beacon rate. See that class for the
    // window semantics. Detection events here just call .Queue(...); the
    // helper handles the rate cap.

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
            "friendly.",
            // Verified-bot identity signals -- verifiedbot.checked / .confirmed /
            // .spoofed / .name / .method, written by VerifiedBotContributor after
            // IP-range or FCrDNS verification. Without this prefix the signals
            // were getting stripped before reaching dashboard_detections.important_signals,
            // so the signature detail panel never showed the verification status
            // category, and the regression test for "did the contributor actually
            // run on this detection" had no observable signal to assert against.
            "verifiedbot."
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

    // Caps the world-map attack-arc firehose to at most one arc per
    // AttackArcMinIntervalMs across the whole process. The arc is decorative;
    // every detection is still stored and counted regardless of whether its arc
    // is emitted. Unlike invalidation beacons it carries a payload, so it can't
    // go through the name-coalescing SignalRBroadcastConstrainer.
    private static long _lastArcTicks;

    private static bool TryReserveArcSlot(int minIntervalMs)
    {
        if (minIntervalMs <= 0) return true;
        var minTicks = TimeSpan.FromMilliseconds(minIntervalMs).Ticks;
        var now = DateTime.UtcNow.Ticks;
        var last = System.Threading.Interlocked.Read(ref _lastArcTicks);
        if (now - last < minTicks) return false;
        // Claim the slot; if another thread beat us to it, skip this arc.
        return System.Threading.Interlocked.CompareExchange(ref _lastArcTicks, now, last) == last;
    }

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
        IOptions<StyloBotDashboardOptions> dashboardOptionsAccessor,
        SignatureAggregateCache signatureAggregateCache,
        Mostlylucid.BotDetection.Orchestration.Telemetry.IDetectionEventPublisher detectionEventPublisher,
        IOptions<Mostlylucid.BotDetection.Dashboard.DetectionRecordOptions>? recordOptionsAccessor = null,
        SignatureDescriptionService? signatureDescriptionService = null)
    {
        // Build the detection from context.Items (populated by UseBotDetection earlier in
        // the pipeline) BEFORE proxying downstream. The in-memory cache update has to
        // land before _next so the downstream render -- which may turn around and hit
        // this same gateway's REST endpoints to read the cache -- sees the verdict for
        // the CURRENT request, not the previous one. DB persistence is fire-and-forget
        // AFTER _next: SQLite/Postgres can't take per-request synchronous writes on the
        // hot path. Per [[feedback_write_behind_lfu_facade]]: dict is truth, DB is
        // durability. The flush task captures detection by value (struct/record), so
        // post-response context disposal can't race it.
        DashboardDetectionEvent? detection = null;
        AggregatedEvidence? evidenceCapture = null;
        bool isUpstreamPath = false;
        bool excludeLocal = false;
        var dashOpts = dashboardOptionsAccessor.Value;
        var detOpts = optionsAccessor.Value;

        try
        {
            // Upstream-trusted fast path (no AggregatedEvidence, e.g. edge-headers hydration).
            if (!context.Items.ContainsKey(BotDetectionMiddleware.AggregatedEvidenceKey) &&
                context.Items.TryGetValue(BotDetectionMiddleware.BotDetectionResultKey, out var upstreamObj) &&
                upstreamObj is BotDetectionResult upstreamResult)
            {
                detection = BuildDetectionFromUpstream(context, upstreamResult);
                isUpstreamPath = true;
            }
            else if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evObj)
                     && evObj is AggregatedEvidence evidence)
            {
                // Static asset short-circuit: zero-confidence sub-millisecond rows would pollute reputation history.
                if (evidence.Confidence == 0 && evidence.TotalProcessingTimeMs < 0.5)
                {
                    _logger.LogDebug("Skipping broadcast for zero-confidence static asset: {Path}", context.Request.Path);
                }
                else
                {
                    detection = BuildDetectionFromEvidence(context, evidence, recordOptionsAccessor?.Value);
                    evidenceCapture = evidence;
                }
            }

            if (detection is not null)
            {
                // Local-IP infrastructure traffic exclusion (docker bridge / loopback). Visitor
                // traffic on local LANs still records because IsLocalIp matches only RFC1918 /
                // loopback / link-local; the public-edge visitor IP is preserved by
                // UseForwardedHeaders earlier in the pipeline.
                excludeLocal = detOpts.ExcludeLocalIpFromBroadcast
                               && IsLocalIp(context.Connection.RemoteIpAddress);

                if (!excludeLocal)
                {
                    // === SYNCHRONOUS CACHE UPDATE (microseconds; the write-through hot tier) ===
                    signatureAggregateCache.UpdateFromDetection(detection);

                    // === SIGNALR BEACONS (throttled by SignalRBroadcastConstrainer) ===
                    SignalRBroadcastConstrainer.Queue(hubContext, "signature", dashOpts.BroadcastMinIntervalMs);
                    SignalRBroadcastConstrainer.Queue(hubContext, "summary",   dashOpts.BroadcastMinIntervalMs);
                    if (detection.ThreatScore is > 0.3 || detection.Action == "simulation-pack")
                        SignalRBroadcastConstrainer.Queue(hubContext, "threats", dashOpts.BroadcastMinIntervalMs);

                    // === ATTACK ARC (best-effort, throttled) ===
                    if (detection.IsBot && !string.IsNullOrEmpty(detection.CountryCode)
                                        && detection.CountryCode != "XX" && detection.CountryCode != "LOCAL"
                                        && TryReserveArcSlot(dashOpts.AttackArcMinIntervalMs))
                        _ = hubContext.Clients.All.BroadcastAttackArc(detection.CountryCode, detection.RiskBand ?? "Low");
                }
            }
        }
        catch (Exception ex)
        {
            // Only broadcast-prep failures land here -- _next is OUTSIDE the try, so a
            // downstream endpoint exception is no longer mis-attributed to this stage
            // AND no longer causes a second _next(context) call (which is what produced
            // the "StatusCode cannot be set because the response has already started"
            // unhandled exception in the gateway log).
            _logger.LogWarning(ex, "Pre-proxy broadcast prep failed");
        }

        // === PROXY DOWNSTREAM (exactly once, regardless of prep outcome) ===
        // _next is invoked here so broadcast-prep failures above can never cause a double
        // _next call. Capture downstream exceptions and re-throw AFTER the write-behind
        // persist so that a throttle-tools 429 / YARP "response already started" throw
        // does not silently drop the audit row from dashboard_detections.
        Exception? downstreamException = null;
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            downstreamException = ex;
        }

        // Local-IP exclusion: cache update and broadcasts were already skipped in the
        // try block; skip write-behind persist too so loopback noise stays out of the DB.
        if (excludeLocal)
        {
            if (downstreamException is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(downstreamException);
            return;
        }

        // === POST-_NEXT REHYDRATION (in-process detection path) ===
        // When this middleware is wired BEFORE BotDetectionMiddleware (the
        // standard UseStyloBot ordering, so blocked requests are still recorded),
        // context.Items is empty on the pre-_next read above and `detection`
        // stays null. The detector populates it during _next, so retry the read
        // here. Upstream-trusted path already populated `detection`, so skip.
        if (detection is null)
        {
            try
            {
                if (!context.Items.ContainsKey(BotDetectionMiddleware.AggregatedEvidenceKey) &&
                    context.Items.TryGetValue(BotDetectionMiddleware.BotDetectionResultKey, out var upstreamObj) &&
                    upstreamObj is BotDetectionResult upstreamResult)
                {
                    detection = BuildDetectionFromUpstream(context, upstreamResult);
                    isUpstreamPath = true;
                }
                else if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evObj)
                         && evObj is AggregatedEvidence evidence)
                {
                    if (!(evidence.Confidence == 0 && evidence.TotalProcessingTimeMs < 0.5))
                    {
                        detection = BuildDetectionFromEvidence(context, evidence, recordOptionsAccessor?.Value);
                        evidenceCapture = evidence;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Post-_next broadcast rehydration failed");
            }
        }

        // === WRITE-BEHIND PERSIST (fire-and-forget; never blocks the request) ===
        // The dict is already correct via the synchronous update above; this batch
        // brings the DB up to date for durability and cold restore. Failures are
        // logged and swallowed -- the cache remains authoritative for in-memory state.
        if (detection is null)
        {
            if (downstreamException is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(downstreamException);
            return;
        }

        // Capture everything we need by value BEFORE spawning the task; context may
        // be disposed before the task runs.
        var detectionCapture = detection;
        var publisherCapture = detectionEventPublisher;
        var sigDescService = signatureDescriptionService;
        var signalsCapture = evidenceCapture?.Signals;
        var pathLog = isUpstreamPath ? "upstream" : "evidence";

        // Guard against double-persist when this middleware is wired into the pipeline
        // twice (host accidentally calls both UseStyloBot and UseStyloBotDashboard).
        if (context.Items.ContainsKey(DetectionStoredFlag)) return;
        context.Items[DetectionStoredFlag] = true;

        // Factors must be parsed BEFORE the fire-and-forget task because they read
        // HttpContext.Items, which the framework disposes after the request completes.
        var factorsCapture = ParseSignatureFactors(context);

        _ = Task.Run(async () =>
        {
            try
            {
                await PersistDetectionAndSignatureAsync(detectionCapture, factorsCapture, eventStore);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Detection persist (write-behind) failed: {Path} sig={Sig} -- cache stays authoritative",
                    detectionCapture.Path,
                    detectionCapture.PrimarySignature?[..Math.Min(8, detectionCapture.PrimarySignature.Length)]);
            }
            try { await PublishEventAsync(publisherCapture, detectionCapture, context: null, evidence: null); }
            catch (Exception ex) { _logger.LogDebug(ex, "Detection event publish failed"); }

            // Description service: feeds humans + bots into the name/description synthesizer.
            if (sigDescService is not null
                && !string.IsNullOrEmpty(detectionCapture.PrimarySignature)
                && signalsCapture is { Count: > 0 })
            {
                try
                {
                    var nullableSignals = signalsCapture.ToDictionary(s => s.Key, s => (object?)s.Value);
                    sigDescService.TrackSignature(detectionCapture.PrimarySignature, nullableSignals);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "TrackSignature failed"); }
            }
        });

        _logger.LogDebug(
            "Broadcast detection ({Path}): sig={Sig} prob={Prob:F2}",
            pathLog,
            detectionCapture.PrimarySignature?[..Math.Min(8, detectionCapture.PrimarySignature.Length)],
            detectionCapture.BotProbability);

        // Re-throw downstream exception AFTER the persist task is queued so the
        // audit row lands in dashboard_detections even when the proxy / endpoint
        // throws after starting the response (e.g. throttle-tools 429 + YARP).
        if (downstreamException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(downstreamException);
    }

    /// <summary>
    ///     Persist detection + signature rows. Called from the write-behind fire-and-forget
    ///     path -- never on the request hot path. Idempotent: callers guard against
    ///     double-persist via <see cref="DetectionStoredFlag"/>; this method just writes.
    /// </summary>
    private async Task PersistDetectionAndSignatureAsync(
        DashboardDetectionEvent detection,
        SignatureFactors factors,
        IDashboardEventStore eventStore)
    {
        await eventStore.AddDetectionAsync(detection);

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

        await eventStore.AddSignatureAsync(signature);
    }

    // ─── Shared storage: ONE path for detection + signature ───────────────

    /// <summary>Marker key on <see cref="HttpContext.Items"/> set the first time a
    /// detection has been persisted for the current request. Guards against the
    /// dashboard event store ever receiving two rows for one logical request --
    /// e.g. if a host accidentally wires <see cref="DetectionBroadcastMiddleware"/>
    /// twice via both <c>UseStyloBot</c> and <c>UseStyloBotDashboard</c>, or if an
    /// internal-render trick re-enters the pipeline for the same context. The
    /// Raw Requests panel previously showed every detection twice on the demo
    /// signature page; that was the symptom that motivated this guard.</summary>
    internal const string DetectionStoredFlag = "StyloBot.DetectionBroadcast.Stored";

    // (legacy StoreDetectionAndSignatureAsync removed -- the dedupe guard +
    // signature-event construction now live in InvokeAsync (synchronous flag set
    // before the fire-and-forget task is spawned) and PersistDetectionAndSignatureAsync
    // (the actual DB writes, called from the write-behind path).)

    // ─── Detection builders ──────────────────────────────────────────────

    /// <summary>
    ///     Build a DashboardDetectionEvent from full AggregatedEvidence (local detection path).
    ///     When <paramref name="recordOptions"/> is provided the Domain, Referer, ReferrerHost and
    ///     UaDeviceClass analytics fields are populated according to the capture flags; Domain is
    ///     always set regardless of flags as it is the multi-domain partition key.
    /// </summary>
    internal DashboardDetectionEvent BuildDetectionFromEvidence(
        HttpContext context,
        AggregatedEvidence evidence,
        Mostlylucid.BotDetection.Dashboard.DetectionRecordOptions? recordOptions = null)
    {
        var sigValue = ResolvePrimarySignature(context);
        var countryCode = ResolveCountryCode(context, evidence.Signals);

        // Single-pass aggregation. Previous version walked evidence.Contributions five
        // times: Where + OrderByDescending + Take + Select + ToList for top reasons,
        // then GroupBy + ToDictionary with four separate Sum() calls per group plus a
        // First() for Priority and a string.Join scanning the group again for the
        // reason concat. On a 25-contributor request that's >100 LINQ enumerator allocs
        // and ~6 passes over the list. The hot path runs on every request that hits the
        // dashboard broadcast layer; LINQ chains here meaningfully eat into the
        // microsecond budget the foundation wave just earned back.
        var (topReasons, detectorContributions) = AggregateContributionsAndTopReasons(evidence.Contributions);

        // Positive-signal fallback. Contribution.Reason is conventionally only populated
        // by detectors that pushed evidence TOWARD bot ("missing browser headers", "JA3
        // mismatch", etc.). Human visitors have no reason strings, so the dashboard's
        // "Detection Signals" panel previously rendered "No detection signals recorded"
        // for every human signature. That's wrong -- the pipeline IS observing signals
        // on every request; they just don't all carry Reason fields. When topReasons is
        // empty we synthesise a short positive narrative from the canonical signal dict
        // (matched archetype, UA family, headless/datacenter clean flags) so every
        // signature detail page renders SOMETHING the operator can read at a glance.
        // 2026-06-19 user feedback: "It is IMPOSSIBLE for a fingerprint to exist with
        // no detection signals!"
        if (topReasons.Count == 0)
            topReasons = SynthesizePositiveSignalSummary(evidence.Signals);

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
            // SINGLE canonicalisation point at the dashboard event boundary --
            // mirrors the same normalisation IFingerprintStore does on the
            // persistent write. Catalog-known names get the catalog's casing
            // ("googlebot" / "GOOGLEBOT" / regex-capture "Googlebot/2.1" all
            // become "Googlebot"). Unknown names (custom labels, fediverse
            // suffixes) pass through unchanged.
            BotName = Mostlylucid.BotDetection.Definitions.BotPatterns.BotPatternLoader.Default.FindCanonicalCasing(evidence.PrimaryBotName) ?? evidence.PrimaryBotName,
            Action = evidence.PolicyAction?.ToString() ?? evidence.TriggeredActionPolicyName ?? "Allow",
            PolicyName = evidence.PolicyName ?? "Default",
            Method = context.Request.Method,
            Path = context.Request.Path.Value ?? "/",
            StatusCode = context.Response.StatusCode,
            ProcessingTimeMs = evidence.TotalProcessingTimeMs,
            PrimarySignature = sigValue,
            EntityId = ResolveEntityId(context),
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
            IsVerifiedBot = ReadVerifiedBotConfirmed(evidence.Signals),
            Domain = context.Request.Host.Host?.ToLowerInvariant(),
            Referer = captureReferer ? rawReferer : null,
            ReferrerHost = derivedReferrerHost,
            UaDeviceClass = derivedUaDeviceClass,
            ResponseBytes = context.Response.ContentLength,
        };

        return detection with { Narrative = DetectionNarrativeBuilder.Build(detection) };
    }

    /// <summary>
    ///     Build a DashboardDetectionEvent from upstream-trusted BotDetectionResult (no AggregatedEvidence).
    /// </summary>
    internal DashboardDetectionEvent BuildDetectionFromUpstream(HttpContext context, BotDetectionResult result)
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
            // SINGLE canonicalisation point -- same call as the AggregatedEvidence
            // build path above so an upstream-trusted detection produces the same
            // canonical-cased BotName as the locally-orchestrated one.
            BotName = Mostlylucid.BotDetection.Definitions.BotPatterns.BotPatternLoader.Default.FindCanonicalCasing(result.BotName) ?? result.BotName,
            Action = context.Request.Headers["X-Bot-Detection-Action"].FirstOrDefault() ?? "Allow",
            PolicyName = "upstream",
            Method = context.Request.Method,
            Path = context.Request.Path.Value ?? "/",
            StatusCode = context.Response.StatusCode,
            ProcessingTimeMs = upstreamProcessingMs,
            PrimarySignature = sigValue,
            EntityId = ResolveEntityId(context),
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
            IsVerifiedBot = ReadVerifiedBotConfirmed(importantSignals),
            // Domain is unconditional for the upstream path too (multi-domain partition key)
            Domain = context.Request.Host.Host?.ToLowerInvariant(),
            ResponseBytes = context.Response.ContentLength,
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
            && evObj is AggregatedEvidence ev
            && Mostlylucid.BotDetection.Orchestration.SignatureLookup.PrimaryOrMultifactor(ev) is { } resolved)
            return resolved;

        return GenerateFallbackSignature(context);
    }

    /// <summary>
    ///     Resolve the durable entity id from HttpContext. Set by
    ///     <c>StyloBotForwardedHeadersMiddleware</c> (in-process gateway) or by
    ///     <c>StyloBotForwardedHeadersHydratorMiddleware</c> from the
    ///     <c>X-Bot-Detection-EntityId</c> edge header (remote-mode dashboard).
    ///     Null when the gateway couldn't resolve an entity for this request --
    ///     SbTopBots rows fall back to the signature URL in that case.
    /// </summary>
    private static string? ResolveEntityId(HttpContext context)
    {
        if (context.Items.TryGetValue(SignalKeys.EntityId, out var entityObj)
            && entityObj is string entityId
            && !string.IsNullOrEmpty(entityId))
            return entityId;
        return null;
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
            var accessor = GetCountryCodeAccessor(geoLocObj.GetType());
            if (accessor?.Invoke(geoLocObj) is string geoCC && !string.IsNullOrEmpty(geoCC) && geoCC != "LOCAL")
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
            // Source-gen JsonTypeInfo replaces the reflection-based deserialize call;
            // closes IL2026/IL3050 AoT warnings and removes a per-request reflection
            // metadata walk on the gateway-hydration edge path.
            var parsed = System.Text.Json.JsonSerializer.Deserialize(
                upstreamSignalsHeader,
                UpstreamSignalsJsonContext.Default.DictionaryStringJsonElement);
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

    /// <summary>
    ///     Reads the canonical verified-bot signal off the evidence's signal bag.
    ///     <c>VerifiedBotContributor</c> writes <c>verifiedbot.confirmed=true</c>
    ///     after IP-range / FCrDNS verification succeeds. We accept either a
    ///     boolean or its string representation ("true"/"True") since signal
    ///     payloads round-trip through dictionaries with mixed value types.
    /// </summary>
    private static bool ReadVerifiedBotConfirmed(IReadOnlyDictionary<string, object>? signals)
    {
        if (signals is null) return false;
        if (!signals.TryGetValue(Mostlylucid.BotDetection.Models.SignalKeys.VerifiedBotConfirmed, out var v) || v is null)
            return false;
        return v switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var parsed) && parsed,
            _ => false
        };
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
        // docs/REVERSE_PROXY_SIGNALS.md for the rule recipe. Each is optional;
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
            var accessor = GetCountryCodeAccessor(geoLocObj.GetType());
            if (accessor?.Invoke(geoLocObj) is string geoCC && !string.IsNullOrEmpty(geoCC) && geoCC != "LOCAL")
                countryCode = geoCC;
        }

        if (countryCode == null)
            countryCode = ResolveCountryFromHeaders(context);
    }

    /// <summary>
    ///     Build a 1-3 entry "positive narrative" list from canonical observation signals
    ///     when the detector-contribution pipeline left no Reason strings. The detection
    ///     signals panel on the signature detail page reads this list -- without
    ///     synthesis, every human visitor would render "No detection signals recorded"
    ///     because reasons are conventionally only set by bot-confirming contributors.
    ///     Pulls from signals the pipeline ALWAYS populates: matched archetype,
    ///     UA family + version + OS, headless / datacenter clean flags, verified-bot
    ///     state. Output is short scannable text intended for the same UL the
    ///     contribution-reason path produces, not prose.
    /// </summary>
    internal static List<string> SynthesizePositiveSignalSummary(IReadOnlyDictionary<string, object> signals)
    {
        var reasons = new List<string>(5);

        // Matched archetype is the highest-signal observation: it captures the
        // best-fit human/bot family centroid. Surface name + kind when present so
        // the operator sees both "what shape" and "what kind" without clicking
        // through to the archetype tab.
        if (signals.TryGetValue(SignalKeys.IdentityArchetypeName, out var archName) &&
            archName is string archNameStr && !string.IsNullOrEmpty(archNameStr))
        {
            if (signals.TryGetValue(SignalKeys.IdentityArchetypeKind, out var archKind) &&
                archKind is string archKindStr && !string.IsNullOrEmpty(archKindStr))
                reasons.Add($"Matched archetype: {archNameStr} ({archKindStr})");
            else
                reasons.Add($"Matched archetype: {archNameStr}");
        }

        // UA family + version + OS gives the operator the browser identity. The
        // dashboard's UA chip shows this too but having it in the signals list
        // means the operator can read the entire decision rationale without
        // glancing elsewhere.
        var family = signals.TryGetValue(SignalKeys.UserAgentFamily, out var f) ? f as string : null;
        var version = signals.TryGetValue(SignalKeys.UserAgentFamilyVersion, out var v) ? v as string : null;
        var os = signals.TryGetValue(SignalKeys.UserAgentOs, out var o) ? o as string : null;
        if (!string.IsNullOrEmpty(family) && family != "Other")
        {
            var uaLine = family;
            if (!string.IsNullOrEmpty(version)) uaLine += " " + version;
            if (!string.IsNullOrEmpty(os)) uaLine += " / " + os;
            reasons.Add($"UA: {uaLine}");
        }

        // Headless / datacenter / VPN / tor clean signals are the system's
        // canonical "this looks like a real human" lines. Only surface them when
        // POSITIVE (not flagged), so the panel stays a positive narrative and
        // negative signals continue to come from contributor Reason strings.
        if (signals.TryGetValue("headless.indicator", out var hl) && hl is string hlStr &&
            string.Equals(hlStr, "Clean", StringComparison.OrdinalIgnoreCase))
            reasons.Add("Headless indicator: Clean");

        if (signals.TryGetValue("network.is_datacenter", out var dc) && dc is bool dcb && !dcb)
            reasons.Add("Datacenter IP: Clean");

        // Verified-bot positive: the visitor claimed a bot identity and the
        // verifier (rDNS / IP range / nodeinfo) confirmed it. Worth surfacing
        // here when the rest of the signals are quiet.
        if (signals.TryGetValue(SignalKeys.VerifiedBotConfirmed, out var vb) && vb is bool vbb && vbb)
        {
            var method = signals.TryGetValue(SignalKeys.VerifiedBotMethod, out var m) ? m as string : null;
            reasons.Add(string.IsNullOrEmpty(method)
                ? "Verified bot: confirmed"
                : $"Verified bot: confirmed via {method}");
        }

        // Cap at 5 to match the contribution-Reason path's slice; the panel
        // renders a UL of up to 5 items.
        if (reasons.Count > 5) reasons.RemoveRange(5, reasons.Count - 5);
        return reasons;
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
    ///     Single-pass aggregation over <paramref name="contributions"/>. Returns the
    ///     top-5 reasons ranked by absolute weighted confidence delta, and a per-detector
    ///     summary keyed by detector name. Replaces a chain of LINQ Where + OrderByDescending
    ///     + Take + Select + GroupBy + ToDictionary that walked the contribution list
    ///     six times. One pass populates a small temp list of (reasons, weighted) plus a
    ///     dictionary keyed on detector name; a final sort + slice handles top-5.
    /// </summary>
    internal static (List<string> TopReasons, Dictionary<string, DashboardDetectorContribution> ByDetector)
        AggregateContributionsAndTopReasons(IReadOnlyList<DetectionContribution> contributions)
    {
        var byDetector = new Dictionary<string, DetectorAccumulator>(StringComparer.Ordinal);
        var reasonsBuffer = new List<(string Reason, double Score)>(contributions.Count);

        for (var i = 0; i < contributions.Count; i++)
        {
            var c = contributions[i];

            // Per-detector accumulator.
            var name = c.DetectorName;
            if (!string.IsNullOrEmpty(name))
            {
                ref var acc = ref System.Runtime.InteropServices.CollectionsMarshal
                    .GetValueRefOrAddDefault(byDetector, name, out var existed);
                if (!existed)
                    acc.Priority = c.Priority;
                acc.ConfidenceDelta += c.ConfidenceDelta;
                acc.Contribution += c.ConfidenceDelta * c.Weight;
                acc.ExecutionTimeMs += c.ProcessingTimeMs;
                if (!string.IsNullOrEmpty(c.Reason))
                    (acc.Reasons ??= new List<string>(2)).Add(c.Reason);
            }

            // Top-reasons feed.
            if (!string.IsNullOrEmpty(c.Reason))
                reasonsBuffer.Add((c.Reason, Math.Abs(c.ConfidenceDelta * c.Weight)));
        }

        // Sort reasons by abs(weighted delta) descending, then slice to 5. Sort on a
        // freshly built buffer rather than the input list so the matcher's contribution
        // ordering is preserved for everyone else who reads evidence.Contributions.
        reasonsBuffer.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        var topReasons = new List<string>(Math.Min(5, reasonsBuffer.Count));
        for (var i = 0; i < reasonsBuffer.Count && i < 5; i++)
            topReasons.Add(reasonsBuffer[i].Reason);

        // Materialise the per-detector dict into the final record-shaped dictionary.
        var byDetectorOut = new Dictionary<string, DashboardDetectorContribution>(
            byDetector.Count, StringComparer.Ordinal);
        foreach (var kv in byDetector)
        {
            var a = kv.Value;
            byDetectorOut[kv.Key] = new DashboardDetectorContribution
            {
                ConfidenceDelta = a.ConfidenceDelta,
                Contribution = a.Contribution,
                ExecutionTimeMs = a.ExecutionTimeMs,
                Priority = a.Priority,
                Reason = a.Reasons is null ? null : string.Join("; ", a.Reasons),
            };
        }

        return (topReasons, byDetectorOut);
    }

    private struct DetectorAccumulator
    {
        public double ConfidenceDelta;
        public double Contribution;
        public double ExecutionTimeMs;
        public int Priority;
        public List<string>? Reasons;
    }

    /// <summary>
    ///     Projects the in-process <c>DashboardDetectionEvent</c> into the transport-friendly
    ///     <c>DetectionEvent</c> record and hands it to the registered publisher. Catches and
    ///     logs any failure - publish errors must never affect request processing.
    /// </summary>
    private async Task PublishEventAsync(
        Mostlylucid.BotDetection.Orchestration.Telemetry.IDetectionEventPublisher publisher,
        DashboardDetectionEvent detection,
        HttpContext? context,
        AggregatedEvidence? evidence = null)
    {
        try
        {
            // Both topReasons and detector contributions were already aggregated when the
            // DashboardDetectionEvent was built in BuildDetectionFromEvidence. Reading them
            // off the detection object instead of recomputing from evidence.Contributions
            // saves a second LINQ chain (Where + OrderByDescending + Take + Select + ToList)
            // plus a second GroupBy + Sum pass per request.
            var topReasons = detection.TopReasons;
            Dictionary<string, double>? detectorContribs = null;
            if (detection.DetectorContributions is { Count: > 0 } dc)
            {
                detectorContribs = new Dictionary<string, double>(dc.Count, StringComparer.Ordinal);
                foreach (var kv in dc)
                    detectorContribs[kv.Key] = kv.Value.Contribution;
            }

            var evt = new Mostlylucid.BotDetection.Orchestration.Telemetry.DetectionEvent
            {
                Timestamp = detection.Timestamp,
                RequestId = context?.TraceIdentifier ?? detection.RequestId ?? string.Empty,
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
