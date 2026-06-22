namespace Mostlylucid.BotDetection.StyloExtract.Options;

/// <summary>
/// Controls how the extract action policies interact with the response Cache-Control header.
/// </summary>
public enum CacheControlMode
{
    /// <summary>
    /// Do not touch Cache-Control. Whatever the downstream endpoint emitted stays.
    /// </summary>
    Respect = 0,

    /// <summary>
    /// Replace Cache-Control entirely with the directives in <see cref="CacheOverrideOptions"/>.
    /// </summary>
    Override,

    /// <summary>
    /// Add missing directives only; keep what the endpoint already set.
    /// </summary>
    Add,
}
