using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Escalation;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Bot detection as a configurable "pack" of detector atoms.
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
///     This pack-based approach means:
///     - Detectors are plug-and-play (register via DI)
///     - Configuration via YAML manifests
///     - Swappable storage (SQLite, Postgres, etc.)
///     - Real-time dashboard integration
/// </remarks>
public sealed class BotDetectionPack : IDisposable
{
    private readonly EscalatorConfig _escalatorConfig;
    private readonly ILogger<BotDetectionPack> _logger;
    private readonly ILogger<SignatureEscalatorAtom> _escalatorLogger;
    private readonly BotDetectionOptions _options;
    private readonly DetectorOrchestrator _orchestrator;
    private readonly IServiceProvider _serviceProvider;
    private readonly SignalSink _signalSink;
    private readonly SignatureResponseCoordinatorCache _signatureCoordinators;

    public BotDetectionPack(
        IServiceProvider serviceProvider,
        IOptions<BotDetectionOptions> options,
        SignatureResponseCoordinatorCache signatureCoordinators,
        IOptions<EscalatorConfig> escalatorConfig,
        ILogger<BotDetectionPack> logger,
        ILogger<SignatureEscalatorAtom> escalatorLogger)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _signatureCoordinators = signatureCoordinators;
        _escalatorConfig = escalatorConfig.Value;
        _logger = logger;
        _escalatorLogger = escalatorLogger;

        // Create shared signal sink for this pack
        _signalSink = new SignalSink(
            maxCapacity: _options.MaxSignalCapacity,
            maxAge: TimeSpan.FromMinutes(_options.SignalRetentionMinutes));

        // Create orchestrator with configured options
        _orchestrator = new DetectorOrchestrator(new DetectorOrchestratorOptions
        {
            ParallelWaveExecution = _options.ParallelDetection,
            EnableQuorumExit = _options.EnableQuorumExit,
            QuorumConfidenceThreshold = _options.QuorumConfidenceThreshold,
            Timeout = TimeSpan.FromMilliseconds(_options.TimeoutMs)
        });

        // Register all detector atoms from DI
        var detectorAtoms = serviceProvider.GetServices<IDetectorAtom>();
        foreach (var atom in detectorAtoms.Where(d => d.IsEnabled))
        {
            _orchestrator.Register(atom);
            _logger.LogDebug("Registered detector atom: {Name} (Priority: {Priority})",
                atom.Name, atom.Priority);
        }

        _logger.LogInformation(
            "BotDetectionPack initialized with {Count} detector atoms",
            detectorAtoms.Count(d => d.IsEnabled));
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

        // Build signature for cross-request coordination
        var signature = BuildSignature(context);

        // Create escalator for this detection (TIGHT with operation sink)
        await using var escalator = new SignatureEscalatorAtom(
            _signalSink,
            signature,
            sessionId,
            _signatureCoordinators,
            _escalatorLogger,
            _escalatorConfig);

        try
        {
            // Step 1: Hydrate signals from HttpContext
            RequestHydratorAtom.HydrateFromContext(_signalSink, context, sessionId);

            // Step 2: Run detection through orchestrator
            var ledger = await _orchestrator.DetectAsync(_signalSink, sessionId, ct);

            stopwatch.Stop();

            // Step 3: Convert ledger to AggregatedEvidence
            var evidence = ToAggregatedEvidence(ledger, stopwatch.Elapsed);

            // Step 4: Emit completion signal and hydrate risk signals for escalator
            _signalSink.Raise($"detection.completed:{evidence.BotProbability:F2}", sessionId);
            _signalSink.Raise("request.risk", evidence.BotProbability.ToString("F4"));
            _signalSink.Raise("request.honeypot", (evidence.CategoryBreakdown.ContainsKey("Honeypot")).ToString());

            // Step 5: Use escalator for salience-based escalation (replaces manual EmitEscalationSignals)
            // Escalator uses pattern matching and rules to decide what to escalate
            await escalator.OnRequestAnalysisCompleteAsync(ct);

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
    ///     Builds a multi-factor signature for cross-request coordination.
    /// </summary>
    private static string BuildSignature(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var ua = context.Request.Headers.UserAgent.ToString();
        var uaHash = ua.Length > 0 ? ua.GetHashCode().ToString("X8") : "empty";
        return $"{ip}:{uaHash}";
    }

    /// <summary>
    ///     Converts the detection ledger to domain-specific AggregatedEvidence.
    /// </summary>
    private AggregatedEvidence ToAggregatedEvidence(DetectionLedger ledger, TimeSpan elapsed)
    {
        // Determine risk band from probability
        var riskBand = ledger.BotProbability switch
        {
            >= 0.95 => RiskBand.VeryHigh,
            >= 0.80 => RiskBand.High,
            >= 0.60 => RiskBand.Medium,
            >= 0.40 => RiskBand.Elevated,
            >= 0.20 => RiskBand.Low,
            _ => RiskBand.VeryLow
        };

        // Check for early exit verdicts
        EarlyExitVerdict? earlyExitVerdict = null;
        if (ledger.EarlyExit && ledger.EarlyExitContribution != null)
        {
            earlyExitVerdict = ledger.EarlyExitContribution.EarlyExitVerdict switch
            {
                "VerifiedBadBot" => EarlyExitVerdict.VerifiedBadBot,
                "VerifiedGoodBot" => EarlyExitVerdict.VerifiedGoodBot,
                "Whitelisted" => EarlyExitVerdict.Whitelisted,
                "Blacklisted" => EarlyExitVerdict.Blacklisted,
                _ => null
            };
            riskBand = RiskBand.Verified;
        }

        // Parse bot type if present
        BotType? primaryBotType = null;
        if (!string.IsNullOrEmpty(ledger.BotType) &&
            Enum.TryParse<BotType>(ledger.BotType, true, out var parsed))
        {
            primaryBotType = parsed;
        }

        // Resolve PrimaryBotName via the same priority order the BlackboardOrchestrator
        // path uses (DetectionLedgerExtensions.ResolveDisplayName): matcher-set
        // IdentityDisplayName signal first, then ledger.BotName fallback, then
        // FingerprintNameComposer.Compose for human-shape UA-only requests.
        var primaryBotName = ResolveDisplayName(ledger.MergedSignals, ledger.BotName);

        // Verdict-honest override: when the verdict says bot but the resolved name
        // came from a human-browser archetype match (centroid coincidence on a Chrome
        // XHR / uBlock shape that happens to overlap chrome-desktop or Mastodon),
        // rewrite to "Unknown Bot {family}". The Ephemeral orchestrator's evidence
        // build path must agree with the BlackboardOrchestrator path so the BDF
        // missing-browser-headers case (prob=0.90 named "Chrome Desktop") and the
        // prod regression on signature fA_cI4MGbTxkwr9-4UsUEg both get verdict-honest
        // names regardless of which orchestrator is registered in DI.
        var isActuallyBot = ledger.BotProbability >= 0.5;
        if (isActuallyBot && IsHumanArchetypeWithoutCatalogClaim(ledger.MergedSignals, primaryBotName))
        {
            primaryBotName = Services.FingerprintNameComposer.ComposeUnknownBot(ledger.MergedSignals);
        }

        return new AggregatedEvidence
        {
            Ledger = ledger,
            BotProbability = ledger.BotProbability,
            Confidence = ledger.Confidence,
            RiskBand = riskBand,
            EarlyExit = ledger.EarlyExit,
            EarlyExitVerdict = earlyExitVerdict,
            PrimaryBotType = primaryBotType,
            PrimaryBotName = primaryBotName,
            Signals = ledger.MergedSignals,
            TotalProcessingTimeMs = elapsed.TotalMilliseconds,
            CategoryBreakdown = ledger.CategoryBreakdown,
            ContributingDetectors = ledger.ContributingDetectors,
            FailedDetectors = ledger.FailedDetectors
        };
    }

    private static string? ResolveDisplayName(
        IReadOnlyDictionary<string, object> signals, string? fallback)
    {
        var fromSignal = signals.TryGetValue(Models.SignalKeys.IdentityDisplayName, out var v)
            ? v as string : null;
        if (!string.IsNullOrEmpty(fromSignal)) return fromSignal;
        if (!string.IsNullOrEmpty(fallback)) return fallback;
        return Services.FingerprintNameComposer.Compose(signals);
    }

    private static bool IsHumanArchetypeWithoutCatalogClaim(
        IReadOnlyDictionary<string, object> signals, string? resolvedName)
    {
        if (string.IsNullOrEmpty(resolvedName)) return false;
        var hasCatalogClaim = signals.TryGetValue(Models.SignalKeys.UserAgentBotName, out var ubn)
                              && ubn is string s && !string.IsNullOrEmpty(s);
        if (hasCatalogClaim) return false;
        var kind = signals.TryGetValue(Models.SignalKeys.IdentityArchetypeKind, out var k) ? k as string : null;
        if (kind != "human-browser" && kind != "human-adblocker") return false;
        var archetypeName = signals.TryGetValue(Models.SignalKeys.IdentityArchetypeName, out var n) ? n as string : null;
        return !string.IsNullOrEmpty(archetypeName) && string.Equals(archetypeName, resolvedName, StringComparison.Ordinal);
    }


    public void Dispose()
    {
        _signalSink.ClearPattern("*");
        _logger.LogDebug("BotDetectionPack disposed");
    }
}

/// <summary>
///     Extension methods for registering BotDetectionPack in DI.
/// </summary>
public static class BotDetectionPackExtensions
{
    /// <summary>
    ///     Adds BotDetectionPack and related services for the "pack" architecture.
    /// </summary>
    public static IServiceCollection AddBotDetectionPack(
        this IServiceCollection services)
    {
        // Register the pack as scoped (one per request)
        services.AddScoped<BotDetectionPack>();

        // Register the hydrator atom
        services.AddSingleton<IDetectorAtom, RequestHydratorAtom>();

        // Register signature coordinator cache (singleton for cross-request coordination).
        // The factory pulls the upstream-health gate from DI (optional - hosts
        // without the rate-limit module get null and the ReputationLane treats
        // upstream as healthy, preserving today's behaviour).
        services.AddSingleton<SignatureResponseCoordinatorCache>(sp =>
            new SignatureResponseCoordinatorCache(
                sp.GetRequiredService<ILogger<SignatureResponseCoordinatorCache>>(),
                upstreamHealth: sp.GetService<Mostlylucid.BotDetection.RateLimit.UpstreamHealthGate>()));

        // Register escalator config (with defaults, can be overridden via configuration)
        services.AddOptions<EscalatorConfig>()
            .BindConfiguration("BotDetection:Escalation");

        // Native detector atoms -- the migration target for the legacy
        // IContributingDetector implementations. Each atom is a
        // per-taxonomy-role IDetectorAtom that reads/writes the shared
        // SignalSink directly. Ordered by Priority (Wave 0 -> Wave N).
        services.AddNativeDetectorAtoms();

        // Migration adapters: every un-migrated IContributingDetector is
        // wrapped as a ContributingDetectorAdapter so the pack path has full
        // detector coverage today. The skip set is derived at DI-build time
        // from INativeAtomNameMarker registrations authored inside
        // AddDetectorAtom<T>() -- the atom's own Name property is the sole
        // source of truth, no hand-maintained list. Deleted with the last
        // legacy contributor.
        services.AddContributingDetectorAdapters();

        return services;
    }

    /// <summary>
    ///     Registers all native <see cref="IDetectorAtom"/> implementations
    ///     that have been converted from the legacy
    ///     <c>IContributingDetector</c> contract.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only runs under the pack path (behind
    ///         <c>BotDetection:UsePackPath</c>). Legacy contributors continue
    ///         to run under the blackboard path. Atoms and contributors are
    ///         additive today -- the same detection role can be represented
    ///         in both. Once every legacy contributor has a native atom, the
    ///         blackboard path retires and only the pack path remains.
    ///     </para>
    ///     <para>
    ///         Add newly-converted atoms here (grouped by taxonomy role for
    ///         readability, but the pack orchestrator sorts by Priority at
    ///         runtime).
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddNativeDetectorAtoms(
        this IServiceCollection services)
    {
        // SensorAtoms -- boundary / signal extractors
        services.AddDetectorAtom<SignatureAtom>();             // Priority 1  (Wave 0)
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
        services.AddDetectorAtom<VerifiedBotInlineAtom>();     // Priority 4  (Wave 0)
        services.AddDetectorAtom<FingerprintPriorAtom>();      // Priority 4  (Wave 0)
        services.AddDetectorAtom<MultiLayerCorrelationAtom>(); // Priority 4  (Wave 0)
        services.AddDetectorAtom<VerifiedBotAtom>();           // Priority 4  (Wave 0)
        services.AddDetectorAtom<ThreatIntelAtom>();           // Priority 7  (Wave 0)
        services.AddDetectorAtom<HaxxorAtom>();                // Priority 7  (Wave 0)
        services.AddDetectorAtom<SecurityToolAtom>();          // Priority 8  (Wave 0)
        services.AddDetectorAtom<AiScraperAtom>();             // Priority 9  (Wave 0)

        // ConstrainerAtoms -- validate + constrain proposals
        services.AddDetectorAtom<HeaderAtom>();                // Priority 10
        services.AddDetectorAtom<UserAgentAtom>();             // Priority 10
        services.AddDetectorAtom<IpAtom>();                    // Priority 12
        services.AddDetectorAtom<ResponseBehaviorAtom>();      // Priority 12
        services.AddDetectorAtom<CacheBehaviorAtom>();         // Priority 15
        services.AddDetectorAtom<ProjectHoneypotAtom>();       // Priority 15
        services.AddDetectorAtom<BehavioralAtom>();            // Priority 20
        services.AddDetectorAtom<GeoChangeAtom>();             // Priority 16
        services.AddDetectorAtom<ClientSideAtom>();            // Priority 18
        services.AddDetectorAtom<CookieBehaviorAtom>();        // Priority 20
        services.AddDetectorAtom<HeaderCorrelationAtom>();     // Priority 21
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
    ///     Adds a detector atom to the pack. Also registers an
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
    ///     Adds a detector atom to the pack with factory.
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
///     Marker registered by <see cref="BotDetectionPackExtensions.AddDetectorAtom{TAtom}(IServiceCollection)"/>
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
