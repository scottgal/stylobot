using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Adapts legacy IContributingDetector implementations to the IDetectorAtom interface.
///     This enables incremental migration: existing detectors continue working while
///     new detectors can be written as pure IDetectorAtom implementations.
/// </summary>
/// <remarks>
///     **Migration Path:**
///     1. Wrap existing IContributingDetector with this adapter
///     2. New detectors implement IDetectorAtom directly
///     3. Gradually migrate existing detectors to IDetectorAtom
///     4. Remove adapters when migration complete
///
///     **Signal Bridge:**
///     The adapter creates a BlackboardState from SignalSink signals,
///     runs the legacy detector, and returns the contributions.
///     PII is accessed via HttpContextAccessor (stored in sink metadata).
/// </remarks>
public sealed class ContributingDetectorAdapter : IDetectorAtom
{
    private readonly IContributingDetector _detector;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ContributingDetectorAdapter(
        IContributingDetector detector,
        IHttpContextAccessor httpContextAccessor)
    {
        _detector = detector;
        _httpContextAccessor = httpContextAccessor;
    }

    public string Name => _detector.Name;
    public string Category => "Legacy"; // Legacy detectors don't have category
    public int Priority => _detector.Priority;
    public bool IsEnabled => _detector.IsEnabled;
    public TimeSpan Timeout => _detector.ExecutionTimeout;
    public bool IsOptional => _detector.IsOptional;

    public IReadOnlyList<string> RequiredSignals =>
        ConvertTriggerConditions(_detector.TriggerConditions);

    public async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        // Get HttpContext from accessor (required for legacy detectors)
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            return Array.Empty<DetectionContribution>();
        }

        // Build BlackboardState from SignalSink signals
        var signals = BuildSignalsFromSink(sink, sessionId);
        var contributions = BuildContributionsFromSink(sink, sessionId);

        var blackboardState = new BlackboardState
        {
            HttpContext = httpContext,
            Signals = signals,
            CurrentRiskScore = CalculateCurrentRisk(contributions),
            CompletedDetectors = GetCompletedDetectors(sink, sessionId),
            FailedDetectors = GetFailedDetectors(sink, sessionId),
            Contributions = contributions,
            RequestId = sessionId,
            Elapsed = TimeSpan.Zero // Not tracked in adapter
        };

        // Run the legacy detector
        return await _detector.ContributeAsync(blackboardState, ct);
    }

    /// <summary>
    ///     Converts TriggerCondition to signal patterns for RequiredSignals.
    /// </summary>
    private static IReadOnlyList<string> ConvertTriggerConditions(
        IReadOnlyList<TriggerCondition> conditions)
    {
        var patterns = new List<string>();

        foreach (var condition in conditions)
        {
            switch (condition)
            {
                case SignalExistsTrigger signalExists:
                    patterns.Add(signalExists.SignalKey);
                    break;
                case SignalValueTrigger<bool> boolTrigger when boolTrigger.ExpectedValue:
                    patterns.Add(boolTrigger.SignalKey);
                    break;
                // Other trigger types map to signal patterns where possible
            }
        }

        return patterns;
    }

    /// <summary>
    ///     Builds signal dictionary from SignalSink events.
    /// </summary>
    private static IReadOnlyDictionary<string, object> BuildSignalsFromSink(
        SignalSink sink,
        string sessionId)
    {
        var signals = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        // Get all signals for this session
        var sessionSignals = sink.Sense(s => s.Key == sessionId);

        foreach (var signal in sessionSignals)
        {
            // Parse signal name to extract key-value pairs
            // Format: "key:value" or "key" (boolean true)
            var parts = signal.Signal.Split(':', 2);
            var key = parts[0];

            if (parts.Length == 2)
            {
                // Has value
                if (double.TryParse(parts[1], out var doubleVal))
                    signals[key] = doubleVal;
                else if (bool.TryParse(parts[1], out var boolVal))
                    signals[key] = boolVal;
                else if (int.TryParse(parts[1], out var intVal))
                    signals[key] = intVal;
                else
                    signals[key] = parts[1];
            }
            else
            {
                // Boolean indicator
                signals[key] = true;
            }
        }

        return signals;
    }

    /// <summary>
    ///     Builds contribution list from SignalSink contribution signals.
    /// </summary>
    private static IReadOnlyList<DetectionContribution> BuildContributionsFromSink(
        SignalSink sink,
        string sessionId)
    {
        // Contributions are tracked as signals with prefix "contribution."
        var contributionSignals = sink.Sense(s =>
            s.Key == sessionId &&
            s.Signal.StartsWith("contribution.", StringComparison.OrdinalIgnoreCase));

        // For now return empty - contributions are accumulated in the ledger
        return Array.Empty<DetectionContribution>();
    }

    /// <summary>
    ///     Gets set of completed detectors from sink signals.
    /// </summary>
    private static IReadOnlySet<string> GetCompletedDetectors(SignalSink sink, string sessionId)
    {
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var completedSignals = sink.Sense(s =>
            s.Key == sessionId &&
            s.Signal.StartsWith("detector.", StringComparison.OrdinalIgnoreCase) &&
            s.Signal.EndsWith(".completed", StringComparison.OrdinalIgnoreCase));

        foreach (var signal in completedSignals)
        {
            // Extract detector name from "detector.{name}.completed"
            var name = signal.Signal
                .Replace("detector.", "")
                .Replace(".completed", "");
            completed.Add(name);
        }

        return completed;
    }

    /// <summary>
    ///     Gets set of failed detectors from sink signals.
    /// </summary>
    private static IReadOnlySet<string> GetFailedDetectors(SignalSink sink, string sessionId)
    {
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var failedSignals = sink.Sense(s =>
            s.Key == sessionId &&
            (s.Signal.Contains(".timeout", StringComparison.OrdinalIgnoreCase) ||
             s.Signal.Contains(".error", StringComparison.OrdinalIgnoreCase)));

        foreach (var signal in failedSignals)
        {
            // Extract detector name from failure signal
            var parts = signal.Signal.Split('.');
            if (parts.Length >= 2)
            {
                failed.Add(parts[1]);
            }
        }

        return failed;
    }

    /// <summary>
    ///     Calculates current risk score from contributions.
    /// </summary>
    private static double CalculateCurrentRisk(IReadOnlyList<DetectionContribution> contributions)
    {
        if (contributions.Count == 0)
            return 0.5;

        var weighted = contributions.Where(c => c.Weight > 0).ToList();
        if (weighted.Count == 0)
            return 0.5;

        var weightedSum = weighted.Sum(c => c.ConfidenceDelta * c.Weight);
        return 1.0 / (1.0 + Math.Exp(-weightedSum)); // Sigmoid
    }
}

/// <summary>
///     Factory for creating ContributingDetectorAdapter instances.
/// </summary>
public sealed class ContributingDetectorAdapterFactory
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ContributingDetectorAdapterFactory(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    ///     Creates an IDetectorAtom adapter for a legacy IContributingDetector.
    /// </summary>
    public IDetectorAtom CreateAdapter(IContributingDetector detector)
    {
        return new ContributingDetectorAdapter(detector, _httpContextAccessor);
    }

    /// <summary>
    ///     Creates adapters for all registered IContributingDetector instances.
    /// </summary>
    public IEnumerable<IDetectorAtom> CreateAdapters(IEnumerable<IContributingDetector> detectors)
    {
        return detectors.Select(CreateAdapter);
    }
}
