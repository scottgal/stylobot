using System.Collections.Generic;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Projects the two verdict-derived policy facets -- <c>score.bot_probability</c>
///     and <c>is_human</c> -- into an evidence signal bag.
///
///     <para>
///         <b>Why this exists (8.8).</b> Policy predicates read a string-keyed signal
///         bag, but the bot verdict lives on <see cref="AggregatedEvidence"/> as typed
///         properties. Nothing bridged the two, so every seed rule referencing
///         <c>score.bot_probability</c> or <c>is_human</c> silently never matched --
///         <see cref="Policies.Predicate.PredicateEvaluator"/> returns <c>false</c> on an
///         unknown facet, making a dead facet indistinguishable from a false predicate.
///         Six of eight shipped seed rules were inert this way, including both defaults.
///     </para>
///
///     <para>
///         <b>Call this at EVERY site that builds an <see cref="AggregatedEvidence"/>
///         with a real verdict</b> -- main path, early-exit path, and the sink/fast path.
///         A facet that is live on one path and absent on another is a silent
///         inconsistency of exactly the kind this release exists to remove. The early-exit
///         path matters most: early exits ARE the confirmed-bot cases.
///     </para>
///
///     <para>
///         <b>Do NOT call it on the error-fallback evidence</b>
///         (<c>BotDetectionOrchestrator</c>'s catch block, probability 0.5 /
///         <see cref="RiskBand.Unknown"/>). That request produced no verdict; projecting
///         <c>is_human</c> there would mark every errored request human and turn an
///         exception into a silent allow.
///     </para>
/// </summary>
internal static class VerdictFacetProjection
{
    /// <summary>
    ///     Write the derived verdict facets into <paramref name="signals"/>.
    /// </summary>
    /// <param name="signals">The dict that becomes <c>evidence.Signals</c>.</param>
    /// <param name="botProbability">The request's aggregated bot probability.</param>
    /// <param name="primaryBotType">
    ///     The authoritative bot identity, or <c>null</c> when none was established.
    ///     Non-null means bot regardless of probability.
    /// </param>
    /// <param name="botFloor">
    ///     <c>Classification.BotFloor</c> -- the canonical binary "is a bot" cut.
    /// </param>
    public static void Project(
        IDictionary<string, object> signals,
        double botProbability,
        BotType? primaryBotType,
        double botFloor)
    {
        signals[SignalKeys.ScoreBotProbability] = botProbability;

        // "BOTS ARE NEVER HUMAN" (operator, 2026-06-20): a catalogue-identified bot is a
        // bot even when the probability sigmoid puts it below the floor. Human therefore
        // requires BOTH no authoritative identity AND a sub-floor score -- never a
        // probability-only cut, which is the Bytespider contradiction
        // CatalogIdentityIsBotTests forbids. Derived here, never stored: the v8 rule is
        // that every surface derives its verdict from bot_probability + identity rather
        // than reading a separately-persisted boolean that can drift.
        signals[SignalKeys.IsHuman] = primaryBotType is null && botProbability < botFloor;
    }
}
