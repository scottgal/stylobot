using System.Collections.Immutable;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Represents a signature emitted by a contributor during detection.
///     Signatures are privacy-preserving hashed identities with associated signals.
/// </summary>
public readonly record struct ContributorSignature
{
    /// <summary>
    ///     The hashed signature (e.g., IP hash, fingerprint hash).
    ///     This is the KEY used for cross-request tracking.
    /// </summary>
    public required string SignatureHash { get; init; }

    /// <summary>
    ///     Type of signature (IP, Fingerprint, UserAgent, etc.)
    ///     Allows different signature types to be tracked separately.
    /// </summary>
    public required SignatureType Type { get; init; }

    /// <summary>
    ///     Confidence that this signature represents bot-like behavior (0.0-1.0).
    ///     This is the single-request assessment.
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>
    ///     Signals collected by this contributor for this signature.
    ///     These are recorded in the SignatureCoordinator for cross-request analysis.
    /// </summary>
    public required IReadOnlyDictionary<string, object> Signals { get; init; }

    /// <summary>
    ///     Reason for this signature assessment.
    ///     Human-readable explanation of why this confidence was assigned.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    ///     Name of the contributor that emitted this signature.
    /// </summary>
    public required string DetectorName { get; init; }
}

/// <summary>
///     Types of signatures that can be tracked.
/// </summary>
public enum SignatureType
{
    /// <summary>IP-based signature (most common)</summary>
    IpAddress,

    /// <summary>Browser fingerprint signature</summary>
    Fingerprint,

    /// <summary>User-Agent signature</summary>
    UserAgent,

    /// <summary>Session-based signature</summary>
    Session,

    /// <summary>Composite signature (multiple factors)</summary>
    Composite,

    /// <summary>Custom signature type</summary>
    Custom
}

/// <summary>
///     Extended detection result that includes both contribution and signature emissions.
///     Contributors now emit BOTH traditional contributions AND signatures.
/// </summary>
public record DetectionResult
{
    /// <summary>
    ///     The traditional detection contribution (evidence, confidence delta, etc.)
    /// </summary>
    public required DetectionContribution Contribution { get; init; }

    /// <summary>
    ///     Signatures emitted by this contributor.
    ///     These are sent to the SignatureCoordinator for cross-request tracking.
    /// </summary>
    public IReadOnlyList<ContributorSignature> Signatures { get; init; }
        = Array.Empty<ContributorSignature>();
}

/// <summary>
///     Builder for creating contributor signatures with fluent API.
/// </summary>
public class SignatureBuilder
{
    private readonly Dictionary<string, object> _signals = new();
    private double _confidence;
    private string? _detectorName;
    private string? _reason;
    private string? _signatureHash;
    private SignatureType _type = SignatureType.IpAddress;

    /// <summary>
    ///     Set the signature hash (required).
    /// </summary>
    public SignatureBuilder WithHash(string signatureHash)
    {
        _signatureHash = signatureHash;
        return this;
    }

    /// <summary>
    ///     Set the signature type.
    /// </summary>
    public SignatureBuilder WithType(SignatureType type)
    {
        _type = type;
        return this;
    }

    /// <summary>
    ///     Set the confidence (0.0-1.0).
    /// </summary>
    public SignatureBuilder WithConfidence(double confidence)
    {
        _confidence = Math.Clamp(confidence, 0.0, 1.0);
        return this;
    }

    /// <summary>
    ///     Add a signal (key-value pair).
    /// </summary>
    public SignatureBuilder AddSignal(string key, object value)
    {
        _signals[key] = value;
        return this;
    }

    /// <summary>
    ///     Add multiple signals.
    /// </summary>
    public SignatureBuilder AddSignals(IReadOnlyDictionary<string, object> signals)
    {
        foreach (var (key, value) in signals) _signals[key] = value;
        return this;
    }

    /// <summary>
    ///     Set the reason/explanation.
    /// </summary>
    public SignatureBuilder WithReason(string reason)
    {
        _reason = reason;
        return this;
    }

    /// <summary>
    ///     Set the detector name.
    /// </summary>
    public SignatureBuilder WithDetector(string detectorName)
    {
        _detectorName = detectorName;
        return this;
    }

    /// <summary>
    ///     Build the signature.
    /// </summary>
    public ContributorSignature Build()
    {
        if (string.IsNullOrEmpty(_signatureHash))
            throw new InvalidOperationException("Signature hash is required");
        if (string.IsNullOrEmpty(_reason))
            throw new InvalidOperationException("Reason is required");
        if (string.IsNullOrEmpty(_detectorName))
            throw new InvalidOperationException("Detector name is required");

        return new ContributorSignature
        {
            SignatureHash = _signatureHash,
            Type = _type,
            Confidence = _confidence,
            Signals = _signals.ToImmutableDictionary(),
            Reason = _reason,
            DetectorName = _detectorName
        };
    }
}

/// <summary>
///     Extension methods for creating signatures from contributors.
/// </summary>
public static class SignatureExtensions
{
    /// <summary>
    ///     Create a signature builder for a contributor.
    /// </summary>
    public static SignatureBuilder CreateSignature(this IContributingDetector detector, string signatureHash)
    {
        return new SignatureBuilder()
            .WithHash(signatureHash)
            .WithDetector(detector.Name);
    }

    /// <summary>
    ///     Create a detection result with signatures.
    /// </summary>
    public static DetectionResult WithSignatures(
        this DetectionContribution contribution,
        params ContributorSignature[] signatures)
    {
        return new DetectionResult
        {
            Contribution = contribution,
            Signatures = signatures
        };
    }

    /// <summary>
    ///     Create a detection result with a single signature.
    /// </summary>
    public static DetectionResult WithSignature(
        this DetectionContribution contribution,
        ContributorSignature signature)
    {
        return new DetectionResult
        {
            Contribution = contribution,
            Signatures = new[] { signature }
        };
    }
}