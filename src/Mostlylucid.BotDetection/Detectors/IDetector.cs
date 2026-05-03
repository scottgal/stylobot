using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Detectors;

/// <summary>
///     Execution stage for detectors. Detectors in the same stage run in parallel.
///     Higher stages wait for lower stages to complete.
/// </summary>
public enum DetectorStage
{
    /// <summary>
    ///     Raw signal extraction (UA, headers, IP, client-side).
    ///     No dependencies on other detectors.
    /// </summary>
    RawSignals = 0,

    /// <summary>
    ///     Behavioral analysis that may depend on raw signals.
    ///     Runs after Stage 0 completes.
    /// </summary>
    Behavioral = 1,

    /// <summary>
    ///     Meta-analysis layers (inconsistency detection, risk assessment).
    ///     Reads signals from stages 0 and 1.
    /// </summary>
    MetaAnalysis = 2,

    /// <summary>
    ///     AI/ML-based detection that can use all prior signals.
    ///     Runs last, can learn from all other signals.
    /// </summary>
    Intelligence = 3
}

/// <summary>
///     Interface for bot detection strategies
/// </summary>
public interface IDetector
{
    /// <summary>
    ///     Name of the detector
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Execution stage for this detector.
    ///     Detectors in the same stage run in parallel.
    ///     Higher stages wait for lower stages to complete.
    /// </summary>
    DetectorStage Stage => DetectorStage.RawSignals;

    /// <summary>
    ///     Analyze an HTTP request for bot characteristics.
    ///     Legacy method - prefer DetectAsync with DetectionContext.
    /// </summary>
    /// <param name="context">HTTP context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detection result with confidence score and reasons</returns>
    Task<DetectorResult> DetectAsync(HttpContext context, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Analyze an HTTP request for bot characteristics using shared context.
    ///     Detectors should read signals from prior stages and write their own signals.
    /// </summary>
    /// <param name="detectionContext">Shared detection context with signal bus</param>
    /// <returns>Detection result with confidence score and reasons</returns>
    Task<DetectorResult> DetectAsync(DetectionContext detectionContext)
    {
        // Default implementation for backward compatibility
        return DetectAsync(detectionContext.HttpContext, detectionContext.CancellationToken);
    }
}

/// <summary>
///     Result from an individual detector
/// </summary>
public class DetectorResult
{
    /// <summary>
    ///     Confidence score from this detector (0.0 to 1.0)
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    ///     Reasons found by this detector
    /// </summary>
    public List<DetectionReason> Reasons { get; set; } = new();

    /// <summary>
    ///     Bot type if identified
    /// </summary>
    public BotType? BotType { get; set; }

    /// <summary>
    ///     Bot name if known
    /// </summary>
    public string? BotName { get; set; }
}