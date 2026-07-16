using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Bot detection as a wave-orchestrated set of detector atoms.
///     Uses ephemeral's DetectorOrchestrator for wave-based execution,
///     SignalSink for coordination, and integrates with StyloFlow dashboard.
/// </summary>
/// <remarks>
///     **Architecture:**
///     ```
///     HttpRequest
///         ↓
///     RequestHydratorAtom (populates SignalSink)
///         ↓
///     DetectorOrchestrator (runs detector atoms in waves)
///         ↓
///     DetectionLedger (accumulates evidence)
///         ↓
///     EscalatorAtom (persists high-salience for learning)
///         ↓
///     Dashboard (real-time visualization)
///     ```
///
///     This atom-orchestrator approach means:
///     - Detectors are plug-and-play (register via DI)
///     - Configuration via YAML manifests
///     - Swappable storage (SQLite, Postgres, etc.)
///     - Real-time dashboard integration
/// </remarks>
public sealed class BotDetectionOrchestrator : IDisposable
{
    private readonly ILogger<BotDetectionOrchestrator> _logger;
    private readonly BotDetectionOptions _options;
    private readonly DetectionEngine _engine;
    private readonly IFingerprintStore _fingerprintStore;
    private readonly SignalSink _signalSink;

    public BotDetectionOrchestrator(
        DetectionEngine engine,
        IOptions<BotDetectionOptions> options,
        IFingerprintStore fingerprintStore,
        ILogger<BotDetectionOrchestrator> logger)
    {
        _engine = engine;
        _options = options.Value;
        _fingerprintStore = fingerprintStore;
        _logger = logger;

        // Per-request signal sink. This is the ONLY per-request allocation now: the
        // expensive DetectorOrchestrator + ~70-atom Register() wiring is built ONCE in the
        // singleton DetectionEngine (see DetectionEngine for the why -- it fixes the
        // per-request re-wiring that collapsed the latency tail under load). This class
        // stays AddScoped only so the sink's lifetime tracks the request scope.
        _signalSink = new SignalSink(
            maxCapacity: _options.MaxSignalCapacity,
            maxAge: TimeSpan.FromMinutes(_options.SignalRetentionMinutes));
    }

    /// <summary>
    ///     The shared signal sink for this detection session.
    /// </summary>
    public SignalSink SignalSink => _signalSink;

    /// <summary>
    ///     Runs bot detection on an HTTP request.
    /// </summary>
    public async Task<AggregatedEvidence> DetectAsync(
        HttpContext context,
        CancellationToken ct = default)
    {
        var sessionId = context.TraceIdentifier;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Step 1: Hydrate signals from HttpContext
            RequestHydratorAtom.HydrateFromContext(_signalSink, context, sessionId);

            // Step 2: Run detection through orchestrator
            var ledger = await _engine.DetectAsync(_signalSink, sessionId, ct);

            stopwatch.Stop();

            // Step 3: Convert ledger to AggregatedEvidence
            var evidence = ToAggregatedEvidence(ledger, stopwatch.Elapsed);

            // Step 4: Emit completion signal and hydrate risk signals so
            // downstream action policies (including the summary-path
            // EscalateToSessionActionPolicy) can pick them up when the
            // response arrives.
            _signalSink.Raise($"detection.completed:{evidence.BotProbability:F2}", sessionId);
            _signalSink.Raise("request.risk", evidence.BotProbability.ToString("F4"));
            _signalSink.Raise("request.honeypot", (evidence.CategoryBreakdown.ContainsKey("Honeypot")).ToString());

            // Step 5: Record this request's verdict into the identity headline score. The
            // dashboard signature header reads fp.CachedBotProbability as the single source of
            // truth; before this, that field was only refreshed at a 30-min session-persistence
            // boundary (SessionAtom shift -> RecordVerdictAsync), so a burst-bot that never forms
            // a session kept its allocation-time 0.0 and displayed as Human despite a 100% live
            // score. RecordVerdictWriteBehind blends dict-first (source of truth on the hot read
            // path) and persists via the shared name drainer -- NO per-request DB connection.
            // Dict-only + no-op when identity is disabled or the fingerprint id is absent.
            var identityFingerprintId = _signalSink.ReadHint(SignalKeys.IdentityFingerprintId);
            // Learning-suppressed requests (bypass key with DisableLearningWrites, or
            // impersonation) score normally but must NOT write back into the identity
            // headline. RecordVerdictWriteBehind is a learning write -- skip it. Detection
            // and the response header trail above are unaffected.
            if (!string.IsNullOrEmpty(identityFingerprintId) && !context.IsLearningSuppressedByApiKey())
                // PrimaryBotType is the CATALOGUE type (BotType enum: Internal / SearchEngine /
                // AiBot / Tool / GoodBot / ...). .ToString() yields exactly the vocabulary the
                // dashboard's Internal-exclusion + ai/search/tools filters match on, cached
                // alongside the score so the read-through projects the real type (not the
                // inferred_client_type identity axis). Null when no type was identified this
                // request, which preserves any prior stored type in the store.
                _fingerprintStore.RecordVerdictWriteBehind(
                    identityFingerprintId, evidence.BotProbability, evidence.PrimaryBotType?.ToString());

            // Session-scope promotion (was: SessionSignatureEscalatorAtom
            // fan-out into a per-signature coordinator cache) has moved out
            // of the orchestrator entirely. Escalators run in the action
            // policy pipeline against a response, decide whether to raise a
            // SessionSample into the shared SessionStore, and the
            // SessionAtom reacts to aggregate mutations off-thread.

            _logger.LogDebug(
                "Detection completed for {SessionId}: BotProbability={Prob:F2}, Confidence={Conf:F2}, Elapsed={Elapsed}ms",
                sessionId, evidence.BotProbability, evidence.Confidence, stopwatch.Elapsed.TotalMilliseconds);

            return evidence;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Detection cancelled for session {SessionId}", sessionId);
            _signalSink.Raise("detection.cancelled", sessionId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Detection failed for session {SessionId}", sessionId);
            _signalSink.Raise($"detection.error:{ex.GetType().Name}", sessionId);

            // Return uncertain result on error
            return new AggregatedEvidence
            {
                BotProbability = 0.5,
                Confidence = 0.0,
                RiskBand = RiskBand.Unknown,
                Signals = new Dictionary<string, object>
                {
                    ["error"] = ex.Message
                }
            };
        }
    }

    /// <summary>
    ///     Converts the detection ledger to domain-specific AggregatedEvidence.
    /// </summary>
    private AggregatedEvidence ToAggregatedEvidence(DetectionLedger ledger, TimeSpan elapsed)
    {
        // Use the canonical conversion -- the same one the BlackboardOrchestrator
        // path uses -- so threat score/band, the NonAiMaxProbability clamp, the
        // risk-verdict composition (HostilePin override / friendly-bot corroboration
        // / browser-attestation carve-out), the declared-bot override, AND the
        // verdict-honest name resolution are all applied. The previous hand-rolled
        // body dropped every one of those (ThreatBand was always None, a
        // confirmed-bad actor spoofing Googlebot read as GoodBot/Low, etc.).
        // Processing time is read from ledger.TotalProcessingTimeMs, which the
        // ephemeral orchestrator stamps during DetectAsync.
        _ = elapsed;
        return ledger.ToAggregatedEvidence(options: _options, sink: _signalSink);
    }


    public void Dispose()
    {
        _signalSink.ClearPattern("*");
        _logger.LogDebug("BotDetectionOrchestrator disposed");
    }
}

/// <summary>
///     Extension methods for registering BotDetectionOrchestrator in DI.
/// </summary>
public static class BotDetectionOrchestratorExtensions
{
    /// <summary>
    ///     Adds BotDetectionOrchestrator and related services for the atom-orchestrator architecture.
    /// </summary>
    public static IServiceCollection AddBotDetectionOrchestrator(
        this IServiceCollection services)
    {
        // The detection ENGINE (DetectorOrchestrator + atom wave/priority wiring) is a
        // process singleton, built ONCE. The per-request wrapper below stays scoped only
        // for the per-request SignalSink. This split fixes the tail-latency collapse under
        // load (the wrapper used to rebuild the engine + re-register ~70 atoms per request).
        services.TryAddSingleton<DetectionEngine>();

        // Per-request wrapper: owns the request's SignalSink, delegates detection to the
        // shared singleton engine.
        services.AddScoped<BotDetectionOrchestrator>();

        // TimeAtom takes a TimeProvider from DI (falls back to .System only if the
        // parameter is optional, which it isn't for the DI activator). Without this
        // registration the container throws "Unable to resolve service for type
        // System.TimeProvider" activating TimeAtom, crashing every host on the atom
        // path at boot. TryAdd so a test host binding FakeTimeProvider still wins.
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);

        // Enforcement gates -- extracted from BotDetectionMiddleware so the
        // atom-orchestrator middleware can enforce the same rules without
        // duplicating logic. See Mostlylucid.BotDetection.Enforcement.
        services.AddSingleton<Enforcement.LoadShedGate>();
        services.AddSingleton<Enforcement.PolicyDispatchGate>();
        services.AddSingleton<Enforcement.PostDetectionActionGate>();
        services.AddSingleton<Enforcement.BlockResponseGate>();
        services.AddSingleton<Enforcement.ResponsePiiMaskGate>();

        // Register the hydrator atom
        services.AddSingleton<IDetectorAtom, RequestHydratorAtom>();

        // Session store + atom are registered by BotDetectionModule; the
        // orchestrator no longer needs a per-signature coordinator cache.
        // Session promotion runs in the action policy pipeline via
        // EscalateToSessionActionPolicy; the SessionAtom reacts to
        // aggregate mutations off-thread.

        // Native detector atoms -- the migration target for the legacy
        // IContributingDetector implementations. Each atom is a
        // per-taxonomy-role IDetectorAtom that reads/writes the shared
        // SignalSink directly. Ordered by Priority (Wave 0 -> Wave N).
        services.AddNativeDetectorAtoms();

        return services;
    }

    /// <summary>
    ///     Registers all native <see cref="IDetectorAtom"/> implementations
    ///     that have been converted from the legacy
    ///     <c>IContributingDetector</c> contract.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only runs under the atom-orchestrator path (behind
    ///         <c>BotDetection:UseAtomOrchestrator</c>). Legacy contributors continue
    ///         to run under the blackboard path. Atoms and contributors are
    ///         additive today -- the same detection role can be represented
    ///         in both. Once every legacy contributor has a native atom, the
    ///         blackboard path retires and only the atom-orchestrator path remains.
    ///     </para>
    ///     <para>
    ///         Add newly-converted atoms here (grouped by taxonomy role for
    ///         readability, but the orchestrator sorts by Priority at
    ///         runtime).
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddNativeDetectorAtoms(
        this IServiceCollection services)
    {
        // SensorAtoms -- boundary / signal extractors
        services.AddDetectorAtom<SignatureAtom>();             // Priority 1  (Wave 0)
        services.AddDetectorAtom<HealthEndpointAtom>();        // Priority 2  (Wave 0)
        services.AddDetectorAtom<TransportProtocolAtom>();     // Priority 5  (Wave 0)
        services.AddDetectorAtom<TimeAtom>();                  // Priority 5  (Wave 0)
        services.AddDetectorAtom<FediverseDomainAtom>();       // Priority 5  (Wave 0)
        services.AddDetectorAtom<BrowserModeClassifierAtom>(); // Priority 6  (Wave 0)
        services.AddDetectorAtom<PiiQueryStringAtom>();        // Priority 8  (Wave 0)
        services.AddDetectorAtom<BehavioralWaveformAtom>();    // Priority 3  (Wave 0)
        services.AddDetectorAtom<ContentSequenceAtom>();       // Priority 6  (Wave 0)
        services.AddDetectorAtom<TcpIpFingerprintAtom>();      // Priority 11 (Wave 0)
        services.AddDetectorAtom<TlsFingerprintAtom>();        // Priority 11 (Wave 0)
        services.AddDetectorAtom<Http2FingerprintAtom>();      // Priority 13 (Wave 0)
        services.AddDetectorAtom<Http3FingerprintAtom>();      // Priority 14 (Wave 0)
        services.AddDetectorAtom<LlmAtom>();                   // Priority 55

        // ExtractorAtoms -- raw content -> semantic units
        services.AddDetectorAtom<IdentityVectorAtom>();        // Priority 5  (Wave 0)

        // GuardAtoms -- hard safety / policy gates
        services.AddDetectorAtom<FastPathReputationAtom>();    // Priority 3  (Wave 0)
        services.AddDetectorAtom<HoneypotLinkAtom>();          // Priority 5  (Wave 0)
        services.AddDetectorAtom<VerifiedBotInlineAtom>();     // Priority 4  (Wave 0)
        services.AddDetectorAtom<FingerprintPriorAtom>();      // Priority 4  (Wave 0)
        services.AddDetectorAtom<FingerprintMatchAtom>();      // Priority 6  (Wave 0)
        services.AddDetectorAtom<MultiLayerCorrelationAtom>(); // Priority 4  (Wave 0)
        services.AddDetectorAtom<VerifiedBotAtom>();           // Priority 4  (Wave 0)
        services.AddDetectorAtom<ThreatIntelAtom>();           // Priority 7  (Wave 0)
        services.AddDetectorAtom<HaxxorAtom>();                // Priority 7  (Wave 0)
        services.AddDetectorAtom<SecurityToolAtom>();          // Priority 8  (Wave 0)
        services.AddDetectorAtom<RegistryClientSensor>();      // Priority 8  (Wave 0)
        services.AddDetectorAtom<AiScraperAtom>();             // Priority 9  (Wave 0)
        services.AddDetectorAtom<CveProbeAtom>();              // Priority 11 (Wave 0)

        // ConstrainerAtoms -- validate + constrain proposals
        services.AddDetectorAtom<HeaderAtom>();                // Priority 10
        services.AddDetectorAtom<UserAgentAtom>();             // Priority 10
        services.AddDetectorAtom<IpAtom>();                    // Priority 12
        services.AddDetectorAtom<ResponseBehaviorAtom>();      // Priority 12
        services.AddDetectorAtom<CacheBehaviorAtom>();         // Priority 15
        services.AddDetectorAtom<ProjectHoneypotAtom>();       // Priority 15
        services.AddDetectorAtom<EndpointHistoryAtom>();       // Priority 6  (Wave 0)
        services.AddDetectorAtom<SessionModeResolverAtom>();   // Priority 15 (after Match@6, before Behavioral@20)
        services.AddDetectorAtom<BehavioralAtom>();            // Priority 20
        services.AddDetectorAtom<HealthEndpointReconAtom>();   // Priority 16
        services.AddDetectorAtom<GeoChangeAtom>();             // Priority 16
        services.AddDetectorAtom<ClientSideAtom>();            // Priority 18
        services.AddDetectorAtom<BrowserCharConsistencyAtom>(); // Priority 19
        services.AddDetectorAtom<CookieBehaviorAtom>();        // Priority 20
        services.AddDetectorAtom<HeaderCorrelationAtom>();     // Priority 21
        services.AddDetectorAtom<WebBotAuthApprovalAtom>();    // Priority 23
        services.AddDetectorAtom<FingerprintApprovalAtom>();   // Priority 24
        services.AddDetectorAtom<ChallengeVerificationAtom>(); // Priority 25
        services.AddDetectorAtom<PeriodicityAtom>();           // Priority 25
        services.AddDetectorAtom<ResourceWaterfallAtom>();     // Priority 22
        services.AddDetectorAtom<SessionVectorAtom>();         // Priority 30
        services.AddDetectorAtom<IdentityChangeAtom>();        // Priority 30
        services.AddDetectorAtom<ReactivePatternAtom>();       // Priority 32
        services.AddDetectorAtom<ClaimedIdentityAtom>();       // Priority 35
        services.AddDetectorAtom<StreamAbuseAtom>();           // Priority 35
        services.AddDetectorAtom<ClickFraudAtom>();            // Priority 38
        services.AddDetectorAtom<IntentAtom>();                // Priority 40
        services.AddDetectorAtom<PoolCollisionAtom>();         // Priority 55

        // ProposerAtoms -- probabilistic proposals
        services.AddDetectorAtom<HeuristicAtom>();             // Priority 50
        services.AddDetectorAtom<InconsistencyAtom>();         // Priority 50
        services.AddDetectorAtom<CveFingerprintAtom>();        // Priority 55
        services.AddDetectorAtom<SimilarityAtom>();            // Priority 60
        services.AddDetectorAtom<HeuristicLateAtom>();         // Priority 100
        services.AddDetectorAtom<AiAtom>();                    // Priority 100

        // RankerAtoms -- re-scoring / re-ordering
        services.AddDetectorAtom<VersionAgeAtom>();            // Priority 25
        services.AddDetectorAtom<AccountTakeoverAtom>();       // Priority 25
        services.AddDetectorAtom<ReputationBiasAtom>();        // Priority 45
        services.AddDetectorAtom<ClusterAtom>();               // Priority 850 (very late)

        return services;
    }

    /// <summary>
    ///     Adds a detector atom to the orchestrator. Also registers an
    ///     <see cref="INativeAtomNameMarker"/> that exposes the atom's Name
    ///     to <see cref="ContributingDetectorAdapterExtensions.AddContributingDetectorAdapters"/>
    ///     so the adapter path can skip contributors whose name a native atom
    ///     has already claimed. Name comes from the atom instance itself --
    ///     no hand-maintained lists.
    /// </summary>
    public static IServiceCollection AddDetectorAtom<TAtom>(
        this IServiceCollection services)
        where TAtom : class, IDetectorAtom
    {
        services.AddSingleton<TAtom>();
        services.AddSingleton<IDetectorAtom>(sp => sp.GetRequiredService<TAtom>());
        services.AddSingleton<INativeAtomNameMarker>(sp =>
            new NativeAtomNameMarker(sp.GetRequiredService<TAtom>().Name));
        return services;
    }

    /// <summary>
    ///     Adds a detector atom to the orchestrator with factory.
    /// </summary>
    public static IServiceCollection AddDetectorAtom<TAtom>(
        this IServiceCollection services,
        Func<IServiceProvider, TAtom> factory)
        where TAtom : class, IDetectorAtom
    {
        services.AddSingleton<TAtom>(factory);
        services.AddSingleton<IDetectorAtom>(sp => sp.GetRequiredService<TAtom>());
        services.AddSingleton<INativeAtomNameMarker>(sp =>
            new NativeAtomNameMarker(sp.GetRequiredService<TAtom>().Name));
        return services;
    }
}

/// <summary>
///     Marker registered by <see cref="BotDetectionOrchestratorExtensions.AddDetectorAtom{TAtom}(IServiceCollection)"/>
///     that carries the atom's <see cref="IDetectorAtom.Name"/>. The
///     migration adapter enumerates these markers (a distinct service type,
///     no recursion into <see cref="IDetectorAtom"/> resolution) to compute
///     the skip set for wrapped contributors.
/// </summary>
public interface INativeAtomNameMarker
{
    /// <summary>The <see cref="IDetectorAtom.Name"/> of the native atom this marker represents.</summary>
    string AtomName { get; }
}

internal sealed record NativeAtomNameMarker(string AtomName) : INativeAtomNameMarker;
