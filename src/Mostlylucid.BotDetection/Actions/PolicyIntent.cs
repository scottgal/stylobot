namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Coarse-grained intent of an action policy. Sits one layer above
///     <see cref="ActionType"/>: it describes *what the operator is trying to
///     do* rather than *which class is wired up*. Dashboard chips, docs, and
///     the per-BotType mapping default-table all key off this.
/// </summary>
/// <remarks>
///     The mapping from <see cref="ActionType"/> to <see cref="PolicyIntent"/>
///     is one-way (a policy implementation declares its intent); the reverse
///     mapping is many-to-one because several action classes can share an
///     intent (e.g. <c>block</c>, <c>block-hard</c>, <c>block-soft</c> are
///     all <see cref="Block"/>).
/// </remarks>
public enum PolicyIntent
{
    /// <summary>
    ///     Pipeline runs and a verdict is recorded, but the request is not
    ///     interfered with -- this is the "log-only / shadow" lane plus the
    ///     "confirmed human" lane.
    /// </summary>
    Pass = 0,

    /// <summary>
    ///     Refuse the request. <see cref="BlockActionPolicy"/> and its
    ///     variants (block-hard / block-soft / block-debug) carry this intent.
    /// </summary>
    Block,

    /// <summary>
    ///     Allow the request but cap the total request rate. The token bucket
    ///     is keyed on a stable identity (signature for friendly bots, IP for
    ///     grey-area). Capacity overflow routes into another policy via
    ///     <c>OverLimitAction</c>.
    /// </summary>
    RateLimit,

    /// <summary>
    ///     Allow the request but slow it down with a per-request delay (with
    ///     optional jitter / exp-backoff). No capacity cap -- the bot is
    ///     paying time, not tokens. Used for tools and scrapers.
    /// </summary>
    Throttle,

    /// <summary>
    ///     Present a proof-of-work, CAPTCHA, or other interactive challenge
    ///     that must be solved before the request continues.
    /// </summary>
    Challenge,
}

/// <summary>
///     Conversion helpers between <see cref="ActionType"/> (which class is
///     wired up) and <see cref="PolicyIntent"/> (what the operator is trying
///     to do). New action classes should declare their own intent rather
///     than relying on the inferred mapping here.
/// </summary>
public static class PolicyIntentExtensions
{
    /// <summary>
    ///     Best-effort mapping from <see cref="ActionType"/> to
    ///     <see cref="PolicyIntent"/>. Used as the default for action classes
    ///     that don't declare their own intent explicitly.
    /// </summary>
    public static PolicyIntent ToIntent(this ActionType type) => type switch
    {
        ActionType.Block     => PolicyIntent.Block,
        ActionType.Throttle  => PolicyIntent.Throttle,
        ActionType.Challenge => PolicyIntent.Challenge,
        ActionType.LogOnly   => PolicyIntent.Pass,
        ActionType.Redirect  => PolicyIntent.Block,    // redirect-to-honeypot is "refuse + serve fake"
        _                    => PolicyIntent.Throttle, // Custom defaults to the least surprising option
    };
}
