namespace Mostlylucid.BotDetection.Lifecycle;

/// <summary>
///     What we know about a path's response history. Populated by
///     <see cref="IPathLifecycleStore"/>; consumed by the honeypot threat
///     scorer to lift the threat score when a 4xx hits a path that used to
///     serve real content.
/// </summary>
/// <remarks>
///     <para>
///         A scanner hitting a path that <em>used to be real</em> is
///         qualitatively more dangerous than a scanner hitting a path that
///         never existed: they have institutional memory of your site
///         (cached crawl, old documentation link, leaked credential dump
///         tied to a former endpoint). The fact that the path now 4xx's
///         means we mitigated the breach, not that the intent was benign.
///     </para>
/// </remarks>
public sealed record PathLifecycle
{
    /// <summary>The eTLD+1 (registrable) domain that owns this path. "unknown"
    ///     when the observation had no <c>HttpContext</c> (background jobs, tests).</summary>
    public string Domain { get; init; } = "unknown";

    /// <summary>The full lowercased port-stripped host that served this path.
    ///     Used with <see cref="Path"/> as the aggregation key so the same path
    ///     on different hosts is tracked independently.</summary>
    public string Host { get; init; } = "unknown";

    /// <summary>The exact request path (normalised lower-case, no query).</summary>
    public required string Path { get; init; }

    /// <summary>First time we saw any request hit this path.</summary>
    public DateTime FirstSeenUtc { get; init; }

    /// <summary>Total successful (2xx) responses ever recorded.</summary>
    public int Total2xx { get; init; }

    /// <summary>Total 4xx responses recorded (404s + 403s + etc).</summary>
    public int Total4xx { get; init; }

    /// <summary>Total 3xx/5xx responses (catch-all).</summary>
    public int TotalOther { get; init; }

    /// <summary>Most recent 2xx response timestamp, or null if never.</summary>
    public DateTime? Last2xxUtc { get; init; }

    /// <summary>First time we observed a 4xx after this path had a 2xx history. Null if not yet flipped.</summary>
    public DateTime? First4xxAfter2xxUtc { get; init; }

    /// <summary>Most recent response of ANY status.</summary>
    public DateTime LastSeenUtc { get; init; }

    /// <summary>True when this path has served at least one 2xx in its history.</summary>
    public bool HasEverServed2xx => Total2xx > 0;

    /// <summary>
    ///     True when the path used to serve 2xx but its last status flip was
    ///     to a 4xx. The threat-score-boost signal.
    /// </summary>
    public bool IsFormerlyReal => HasEverServed2xx && First4xxAfter2xxUtc.HasValue;
}
