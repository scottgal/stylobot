namespace Mostlylucid.BotDetection.ThreatIntel;

/// <summary>
///     Result of comparing a session's radar shape against the CVE fingerprint pool.
/// </summary>
public sealed record CveFingerprintMatch
{
    /// <summary>The CVE/GHSA identifier that matched.</summary>
    public required string AdvisoryId { get; init; }

    /// <summary>Cosine similarity score (0-1, higher = closer match).</summary>
    public required double Similarity { get; init; }

    /// <summary>Severity of the matched advisory (critical/high/medium/low).</summary>
    public required string Severity { get; init; }

    /// <summary>Short description of the advisory.</summary>
    public string? Title { get; init; }

    /// <summary>Leiden cluster label if the fingerprint belongs to an exploit family.</summary>
    public string? ClusterLabel { get; init; }
}

/// <summary>
///     Interface for matching session radar shapes against CVE-derived fingerprints.
///     Implemented by the commercial gateway plugin (backed by pgvector or SQLite vss).
///     The FOSS default is a no-op that returns no matches.
/// </summary>
public interface ICveFingerprintMatcher
{
    /// <summary>
    ///     Find CVE fingerprints similar to the given session radar dimensions.
    /// </summary>
    /// <param name="sessionDimensions">The 16-dimension radar shape of the current session.</param>
    /// <param name="topK">Maximum number of matches to return.</param>
    /// <param name="minSimilarity">Minimum cosine similarity threshold (0-1).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matches ordered by descending similarity, empty if no matches or no fingerprints loaded.</returns>
    Task<IReadOnlyList<CveFingerprintMatch>> FindMatchesAsync(
        IReadOnlyDictionary<string, double> sessionDimensions,
        int topK = 3,
        double minSimilarity = 0.75,
        CancellationToken ct = default);

    /// <summary>Number of active CVE fingerprints loaded.</summary>
    int FingerprintCount { get; }
}

/// <summary>
///     No-op implementation for FOSS installs without threat intel.
///     Returns no matches and zero fingerprints.
/// </summary>
public sealed class NullCveFingerprintMatcher : ICveFingerprintMatcher
{
    public Task<IReadOnlyList<CveFingerprintMatch>> FindMatchesAsync(
        IReadOnlyDictionary<string, double> sessionDimensions,
        int topK = 3, double minSimilarity = 0.75,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CveFingerprintMatch>>(Array.Empty<CveFingerprintMatch>());

    public int FingerprintCount => 0;
}