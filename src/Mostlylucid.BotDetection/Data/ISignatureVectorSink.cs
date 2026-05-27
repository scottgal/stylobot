namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Write-side abstraction for the dashboard's per-signature cached session
///     vector. Detection-layer code (<c>SessionVectorContributor</c>,
///     <c>SessionAtomizerService</c>) calls <see cref="RecordLatestVector"/> when
///     it computes a fresh vector; the dashboard layer's
///     <c>SignatureAggregateCache</c> implements this and routes the value onto
///     <c>SignatureAggregate.LatestSessionVector</c>. Null in gateway-mode
///     deployments without the dashboard package.
/// </summary>
public interface ISignatureVectorSink
{
    /// <summary>
    ///     Push the freshly-computed session vector for
    ///     <paramref name="primarySignature"/> into the dashboard's aggregate
    ///     cache. Implementation expects the cache row to already exist (the
    ///     dashboard's broadcast middleware pre-seeds via
    ///     <c>EnsureRow</c> before the orchestrator runs); short or empty
    ///     vectors are ignored.
    /// </summary>
    void RecordLatestVector(string primarySignature, float[] vector);
}
