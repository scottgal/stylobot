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
                sessionId, evidence.BotProbability, evidence.Confidence, stopwatch.ElapsedMilliseconds);

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

        return new AggregatedEvidence
        {
            Ledger = ledger,
            BotProbability = ledger.BotProbability,
            Confidence = ledger.Confidence,
            RiskBand = riskBand,
            EarlyExit = ledger.EarlyExit,
            EarlyExitVerdict = earlyExitVerdict,
            PrimaryBotType = primaryBotType,
            PrimaryBotName = ledger.BotName,
            Signals = ledger.MergedSignals,
            TotalProcessingTimeMs = elapsed.TotalMilliseconds,
            CategoryBreakdown = ledger.CategoryBreakdown,
            ContributingDetectors = ledger.ContributingDetectors,
            FailedDetectors = ledger.FailedDetectors
        };
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

        // Register signature coordinator cache (singleton for cross-request coordination)
        services.AddSingleton<SignatureResponseCoordinatorCache>();

        // Register escalator config (with defaults, can be overridden via configuration)
        services.AddOptions<EscalatorConfig>()
            .BindConfiguration("BotDetection:Escalation");

        return services;
    }

    /// <summary>
    ///     Adds a detector atom to the pack.
    /// </summary>
    public static IServiceCollection AddDetectorAtom<TAtom>(
        this IServiceCollection services)
        where TAtom : class, IDetectorAtom
    {
        services.AddSingleton<IDetectorAtom, TAtom>();
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
        services.AddSingleton<IDetectorAtom>(factory);
        return services;
    }
}
