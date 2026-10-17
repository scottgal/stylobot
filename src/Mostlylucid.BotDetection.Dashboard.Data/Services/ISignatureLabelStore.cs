namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Operator-supplied ground-truth labels on signatures for detector weighting / ML work.
///     An operator clicks "bot / human / benign-bot / uncertain" on a top-signatures row in
///     the dashboard; we persist the label alongside the signature's most recent detection
///     state so offline weight-tuning can compute per-detector precision/recall against
///     real ground truth.
///
///     FOSS ships a SQLite implementation. Commercial wires PostgreSQL so labels
///     accumulate across deploys.
/// </summary>
public interface ISignatureLabelStore
{
    /// <summary>Upsert a label. Most recent wins per (signature × labeler).</summary>
    Task<SignatureLabel> UpsertAsync(SignatureLabel label, CancellationToken ct = default);

    /// <summary>Most-recent label for a signature, or null.</summary>
    Task<SignatureLabel?> GetLatestAsync(string signature, CancellationToken ct = default);

    /// <summary>Retrieve labels since a timestamp, for bulk export.</summary>
    Task<IReadOnlyList<SignatureLabel>> ListSinceAsync(DateTime? since, int limit, CancellationToken ct = default);

    /// <summary>Remove a label. Used when an operator corrects an earlier mistake.</summary>
    Task RemoveAsync(string signature, string labeledBy, CancellationToken ct = default);

    /// <summary>Aggregate count by label kind for dashboard badges.</summary>
    Task<IReadOnlyDictionary<SignatureLabelKind, int>> GetCountsAsync(CancellationToken ct = default);
}

/// <summary>
///     An operator-supplied judgement on a signature. Treated as ground truth - the
///     weighting pipeline will promote these into detector training / F1 evaluation.
/// </summary>
public sealed record SignatureLabel
{
    public required string Signature { get; init; }
    public required SignatureLabelKind Kind { get; init; }

    /// <summary>Operator's self-assessed confidence in their label (0.0 – 1.0).</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>Opaque identifier - e.g., the dashboard user's email or admin session.</summary>
    public required string LabeledBy { get; init; }

    public DateTime LabeledAt { get; init; } = DateTime.UtcNow;

    /// <summary>Short free-text justification -- e.g., "Classic cred-stuffing pattern on /login".</summary>
    public string? Note { get; init; }

    /// <summary>Human-readable display name -- e.g., "Partner Crawler - Acme Corp", "Internal Monitoring Bot".</summary>
    public string? DisplayName { get; init; }
}

/// <summary>Operator's judgement about a signature. Keep the set small and unambiguous.</summary>
public enum SignatureLabelKind
{
    /// <summary>Bad bot (scraper, scanner, credential stuffer, etc.).</summary>
    Bot = 0,
    /// <summary>Human user.</summary>
    Human = 1,
    /// <summary>Legitimate bot (Googlebot, Bingbot, status checker). Not malicious.</summary>
    BenignBot = 2,
    /// <summary>Operator looked, couldn't tell - logged for later revisit rather than polluting the corpus.</summary>
    Uncertain = 3
}