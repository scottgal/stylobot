namespace Mostlylucid.BotDetection.RateLimiting;

/// <summary>
///     A leaky-bucket response-bandwidth limiter. Wraps the upstream response
///     <see cref="Stream"/> so writes that would exceed
///     <paramref name="bytesPerSecond"/> block-await until the bucket refills.
///     The client sees their effective download bandwidth capped at the
///     configured rate.
///     <para>
///     Distinct from <see cref="IRateLimiter"/>: that caps request COUNT, this
///     caps response BYTE rate. A bot that passes the request gate can still
///     drain content slowly enough that scraping a large catalogue costs more
///     than it would otherwise.
///     </para>
/// </summary>
public interface IDataRateLimiter
{
    /// <summary>
    ///     Wrap <paramref name="inner"/> so subsequent writes are throttled to
    ///     <paramref name="bytesPerSecond"/>. The returned stream is owned by
    ///     the caller and disposed on response completion. Subjects key the
    ///     bucket so two distinct actors don't share bandwidth budget.
    /// </summary>
    Stream WrapResponseStream(
        IReadOnlyList<RateLimitSubject> subjects,
        Stream inner,
        long bytesPerSecond);
}
