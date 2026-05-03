namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Snapshot of a detection for background LLM classification.
///     Contains everything needed to classify without HttpContext.
/// </summary>
public sealed record LlmClassificationRequest
{
    public required string RequestId { get; init; }
    public required string PrimarySignature { get; init; }
    public required string UserAgent { get; init; }
    public required string PreBuiltRequestInfo { get; init; }
    public required double HeuristicProbability { get; init; }
    public required List<string> TopReasons { get; init; }
    public required IReadOnlyDictionary<string, object> Signals { get; init; }
    public string? BotType { get; init; }
    public string? BotName { get; init; }
    public string? Path { get; init; }
    public string? Method { get; init; }
    public double Confidence { get; init; }
    public string? RiskBand { get; init; }
    public string? Action { get; init; }
    public DateTimeOffset EnqueuedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    ///     Whether this is a new/unknown signature (prioritize in queue).
    /// </summary>
    public bool IsNewSignature { get; init; }

    /// <summary>
    ///     Multi-vector signature dictionary for churn-resistant identity correlation.
    ///     Keys: "primary" (IP+UA), "ip", "ua", "subnet" (IP/24).
    ///     LLM results update reputation for ALL vectors so that
    ///     IP changes (dynamic ISP) or UA changes (browser update) don't lose history.
    /// </summary>
    public IReadOnlyDictionary<string, string>? SignatureVectors { get; init; }

    /// <summary>Whether this is a drift-detection sample (low-risk sampled for verification)</summary>
    public bool IsDriftSample { get; init; }

    /// <summary>Whether this is a high-risk confirmation sample</summary>
    public bool IsConfirmationSample { get; init; }

    /// <summary>Source: "ambiguous", "new_signature", "aberrant", "drift_sample", "confirmation"</summary>
    public string? EnqueueReason { get; init; }
}
