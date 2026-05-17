using System.Text.Json;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Api.Models;

public sealed partial record DetectResponse
{
    public required VerdictDto Verdict { get; init; }
    public required IReadOnlyList<ReasonDto> Reasons { get; init; }
    /// <summary>
    ///     Per-request signal bag. Heterogeneous values are projected as
    ///     <see cref="JsonElement"/> so the response is statically serializable (AOT-friendly):
    ///     System.Text.Json never has to polymorphically resolve <c>object</c> at write time.
    ///     Clients read concrete values via <c>JsonElement.GetString()</c> / <c>GetDouble()</c>
    ///     / <c>GetBoolean()</c>.
    /// </summary>
    public required IReadOnlyDictionary<string, JsonElement> Signals { get; init; }
    public required MetaDto Meta { get; init; }

    public static DetectResponse FromEvidence(AggregatedEvidence evidence)
    {
        var recommendedAction = evidence.RiskBand switch
        {
            RiskBand.High or RiskBand.VeryHigh => RecommendedAction.Block,
            RiskBand.Medium => RecommendedAction.Challenge,
            RiskBand.Elevated => RecommendedAction.Throttle,
            _ => RecommendedAction.Allow
        };

        return new DetectResponse
        {
            Verdict = new VerdictDto
            {
                IsBot = evidence.BotProbability >= 0.7,
                BotProbability = Math.Round(evidence.BotProbability, 4),
                Confidence = Math.Round(evidence.Confidence, 4),
                BotType = evidence.PrimaryBotType?.ToString(),
                BotName = evidence.PrimaryBotName,
                RiskBand = evidence.RiskBand.ToString(),
                RecommendedAction = recommendedAction.ToString(),
                ThreatScore = Math.Round(evidence.ThreatScore, 4),
                ThreatBand = evidence.ThreatBand.ToString()
            },
            Reasons = evidence.Contributions
                .Where(c => Math.Abs(c.ConfidenceDelta) > 0.01)
                .Select(c => new ReasonDto
                {
                    Detector = c.DetectorName,
                    Detail = c.Reason ?? c.DetectorName,
                    Impact = Math.Round(c.ConfidenceDelta, 4)
                })
                .ToList(),
            Signals = ProjectSignals(evidence.Signals),
            Meta = new MetaDto
            {
                ProcessingTimeMs = Math.Round(evidence.TotalProcessingTimeMs, 2),
                DetectorsRun = evidence.ContributingDetectors.Count,
                PolicyName = evidence.PolicyName,
                AiRan = evidence.AiRan
            }
        };
    }
}

public sealed record VerdictDto
{
    public required bool IsBot { get; init; }
    public required double BotProbability { get; init; }
    public required double Confidence { get; init; }
    public string? BotType { get; init; }
    public string? BotName { get; init; }
    public required string RiskBand { get; init; }
    public required string RecommendedAction { get; init; }
    public required double ThreatScore { get; init; }
    public required string ThreatBand { get; init; }
}

public sealed record ReasonDto
{
    public required string Detector { get; init; }
    public required string Detail { get; init; }
    public required double Impact { get; init; }
}

public sealed record MetaDto
{
    public required double ProcessingTimeMs { get; init; }
    public required int DetectorsRun { get; init; }
    public string? PolicyName { get; init; }
    public required bool AiRan { get; init; }
    public string? RequestId { get; init; }
}

internal static class SignalProjection
{
    /// <summary>
    ///     Convert the orchestrator's <c>IReadOnlyDictionary&lt;string, object&gt;</c> signal
    ///     bag into a dictionary of <see cref="JsonElement"/> values. Each value is round-
    ///     tripped through <c>JsonSerializer.SerializeToElement</c> using a non-source-gen
    ///     options instance that handles the heterogeneous runtime types (bool, string,
    ///     double, int, array, nested record). This single non-AOT path is the deliberate
    ///     boundary between the orchestrator's <c>object</c>-typed blackboard and the API's
    ///     statically typed response surface — every downstream serializer (the one the
    ///     framework wires up for the endpoint response) sees only <c>JsonElement</c>.
    /// </summary>
    private static readonly JsonSerializerOptions ProjectionOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(
        "Heterogeneous object dictionary serialised via SerializeToElement; this is the " +
        "deliberate boundary between the blackboard and the API response. Bypasses source " +
        "generation by design because signal values are runtime-typed. AOT publishes that " +
        "exercise the detect endpoint must accept that some custom signal types may fail " +
        "to serialise (caught and dropped per-key in the catch below).")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(
        "JsonSerializer.SerializeToElement(object) requires runtime type inspection.")]
    public static IReadOnlyDictionary<string, JsonElement> Project(IReadOnlyDictionary<string, object> signals)
    {
        if (signals is null || signals.Count == 0)
            return new Dictionary<string, JsonElement>(0);
        var projected = new Dictionary<string, JsonElement>(signals.Count, StringComparer.Ordinal);
        foreach (var (k, v) in signals)
        {
            try
            {
                projected[k] = JsonSerializer.SerializeToElement(v, ProjectionOptions);
            }
            catch
            {
                // Defensive: a single un-serializable signal must not break the whole
                // response. Drop it from the projection.
            }
        }
        return projected;
    }
}

public partial record DetectResponse
{
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "SignalProjection.Project is intentionally reflection-based; the runtime catches per-signal serialisation failures.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Heterogeneous object dictionary requires runtime type inspection by design.")]
    private static IReadOnlyDictionary<string, JsonElement> ProjectSignals(IReadOnlyDictionary<string, object> signals)
        => SignalProjection.Project(signals);
}
