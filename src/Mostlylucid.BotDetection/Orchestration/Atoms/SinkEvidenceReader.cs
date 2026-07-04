using System.Globalization;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Reads prior detector contributions and the running risk score off the
///     shared <see cref="SignalSink"/> so late-stage / meta-layer atoms
///     (HeuristicLate / Similarity / Ai) can rebuild an
///     <see cref="AggregatedEvidence"/> view WITHOUT a separate ledger-access
///     contract.
/// </summary>
/// <remarks>
///     <para>
///         The <c>DetectorOrchestrator</c> in <c>Mostlylucid.Ephemeral</c>
///         raises <c>contribution.&lt;detector&gt;.&lt;index&gt;:&lt;confidence&gt;|&lt;weight&gt;|&lt;botType&gt;|&lt;botName&gt;</c>
///         signals for every DetectAsync return, and
///         <c>risk.current_score:&lt;value&gt;</c> / <c>risk.current_confidence:&lt;value&gt;</c>
///         after each wave. This helper is the single place that knows the
///         signal-encoded contribution shape.
///     </para>
///     <para>
///         Reason strings are intentionally NOT on the sink -- they may
///         contain operator-authored prose. Late atoms that need per-
///         contribution reasons should not exist; they fall into
///         <c>state.WriteSignal</c> anti-shape territory.
///     </para>
/// </remarks>
internal static class SinkEvidenceReader
{
    private const string ContributionPrefix = "contribution.";

    /// <summary>
    ///     Reads the current running bot-probability score off the sink.
    ///     Set by <c>DetectorOrchestrator</c> after each wave. Falls back to
    ///     0.5 when no upstream wave has completed (the atom is about to run
    ///     the very first check).
    /// </summary>
    public static double ReadCurrentRiskScore(SignalSink sink) =>
        sink.ReadDoubleHint("risk.current_score", 0.5);

    /// <summary>
    ///     Reads the current running detection confidence off the sink.
    /// </summary>
    public static double ReadCurrentConfidence(SignalSink sink) =>
        sink.ReadDoubleHint("risk.current_confidence", 0.0);

    /// <summary>
    ///     Parses every <c>contribution.*</c> signal in the current window
    ///     back into <see cref="DetectionContribution"/> records. Reason
    ///     strings are empty (not sink-carried) and <c>Signals</c> collections
    ///     are empty (rich state lives on atoms, not the sink).
    /// </summary>
    public static IReadOnlyList<DetectionContribution> ReadContributions(SignalSink sink)
    {
        var raw = sink.Sense(s => s.Signal.StartsWith(ContributionPrefix, StringComparison.Ordinal));
        if (raw.Count == 0) return Array.Empty<DetectionContribution>();

        var result = new List<DetectionContribution>(raw.Count);
        foreach (var signal in raw)
        {
            if (!TryParseContribution(signal.Signal, out var contribution)) continue;
            result.Add(contribution);
        }
        return result;
    }

    /// <summary>
    ///     Names of every detector that fired at least one contribution in
    ///     the current window. Cheaper than <see cref="ReadContributions"/>
    ///     when only the presence set is needed.
    /// </summary>
    public static IReadOnlySet<string> ReadContributingDetectorNames(SignalSink sink)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var raw = sink.Sense(s => s.Signal.StartsWith(ContributionPrefix, StringComparison.Ordinal));
        foreach (var signal in raw)
        {
            var body = signal.Signal.AsSpan(ContributionPrefix.Length);
            var dotIdx = body.IndexOf('.');
            if (dotIdx <= 0) continue;
            names.Add(body[..dotIdx].ToString());
        }
        return names;
    }

    /// <summary>
    ///     Builds a lightweight <see cref="AggregatedEvidence"/> from the
    ///     signals currently on the sink. Suitable for callers that require
    ///     a mid-flight evidence view -- e.g. <c>HeuristicDetector</c>'s
    ///     full-mode call. Reason strings on contributions are empty; only
    ///     scalar contribution shape is preserved.
    /// </summary>
    /// <param name="sink">Current per-request signal sink.</param>
    /// <param name="context">HttpContext for elapsed-time computation.</param>
    /// <param name="ledgerId">Identifier for the synthetic ledger.</param>
    public static AggregatedEvidence BuildEvidence(SignalSink sink, HttpContext? context, string ledgerId)
    {
        var contributions = ReadContributions(sink);
        var detectorNames = new HashSet<string>(contributions.Select(c => c.DetectorName), StringComparer.Ordinal);

        var aiDetectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Onnx", "Llm" };
        var aiRan = detectorNames.Any(d => aiDetectors.Contains(d));

        var ledger = new DetectionLedger(ledgerId);
        foreach (var contribution in contributions)
            ledger.AddContribution(contribution);

        // Signals snapshot: all sink hints reduced to a string dict. Rich
        // objects stay off the sink by design so the value type is always
        // string here.
        var signalsDict = SnapshotSinkSignals(sink);

        return new AggregatedEvidence
        {
            Ledger = ledger,
            BotProbability = ReadCurrentRiskScore(sink),
            Confidence = ReadCurrentConfidence(sink),
            RiskBand = RiskBand.Medium,
            PrimaryBotType = InferPrimaryBotType(contributions),
            PrimaryBotName = InferPrimaryBotName(contributions, signalsDict),
            Signals = signalsDict,
            TotalProcessingTimeMs = 0.0,
            CategoryBreakdown = ledger.CategoryBreakdown,
            ContributingDetectors = detectorNames,
            FailedDetectors = new HashSet<string>(StringComparer.Ordinal),
            AiRan = aiRan
        };
    }

    private static Dictionary<string, object> SnapshotSinkSignals(SignalSink sink)
    {
        var dict = new Dictionary<string, object>(StringComparer.Ordinal);
        var signals = sink.Sense(_ => true);
        foreach (var signal in signals)
        {
            var raw = signal.Signal;
            var colon = raw.IndexOf(':');
            if (colon <= 0)
            {
                dict.TryAdd(raw, true);
                continue;
            }
            dict.TryAdd(raw[..colon], raw[(colon + 1)..]);
        }
        return dict;
    }

    private static BotType? InferPrimaryBotType(IReadOnlyList<DetectionContribution> contributions)
    {
        var botTypeStr = contributions
            .Where(c => !string.IsNullOrEmpty(c.BotType))
            .OrderByDescending(c => c.Weight)
            .Select(c => c.BotType)
            .FirstOrDefault();

        return botTypeStr is not null && Enum.TryParse<BotType>(botTypeStr, true, out var bt)
            ? bt
            : null;
    }

    private static string? InferPrimaryBotName(
        IReadOnlyList<DetectionContribution> contributions,
        IReadOnlyDictionary<string, object> signals)
    {
        var fromContribution = contributions
            .Where(c => !string.IsNullOrEmpty(c.BotName))
            .OrderByDescending(c => c.Weight)
            .Select(c => c.BotName)
            .FirstOrDefault();
        if (!string.IsNullOrEmpty(fromContribution)) return fromContribution;

        if (signals.TryGetValue(SignalKeys.UserAgentBotName, out var ubn) && ubn is string ubnStr && !string.IsNullOrEmpty(ubnStr))
            return ubnStr;
        if (signals.TryGetValue(SignalKeys.IdentityDisplayName, out var idn) && idn is string idnStr && !string.IsNullOrEmpty(idnStr))
            return idnStr;
        return null;
    }

    /// <summary>
    ///     Parses a signal of the form
    ///     <c>contribution.&lt;detector&gt;.&lt;index&gt;:&lt;confidence&gt;|&lt;weight&gt;|&lt;botType&gt;|&lt;botName&gt;</c>
    ///     back into a <see cref="DetectionContribution"/>. Returns false on
    ///     malformed input.
    /// </summary>
    private static bool TryParseContribution(string signal, out DetectionContribution contribution)
    {
        contribution = default!;
        if (!signal.StartsWith(ContributionPrefix, StringComparison.Ordinal)) return false;

        var body = signal.AsSpan(ContributionPrefix.Length);
        var colon = body.IndexOf(':');
        if (colon <= 0) return false;

        var header = body[..colon];
        var payload = body[(colon + 1)..];

        var lastDot = header.LastIndexOf('.');
        if (lastDot <= 0) return false;
        var detectorName = header[..lastDot].ToString();

        var parts = payload.ToString().Split('|');
        if (parts.Length < 4) return false;

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence))
            return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var weight))
            return false;
        var botType = string.IsNullOrEmpty(parts[2]) ? null : parts[2];
        var botName = string.IsNullOrEmpty(parts[3]) ? null : parts[3];

        contribution = new DetectionContribution
        {
            DetectorName = detectorName,
            Category = detectorName,
            ConfidenceDelta = confidence,
            Weight = weight,
            Reason = string.Empty,
            BotType = botType,
            BotName = botName
        };
        return true;
    }
}
