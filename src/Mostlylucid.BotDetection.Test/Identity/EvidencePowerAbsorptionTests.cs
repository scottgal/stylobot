using Mostlylucid.BotDetection.Identity;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     2026-08-02 fp-cache-current architecture (final, operator-approved model): the
///     fingerprint's cached score updates in REAL TIME per observation, weighted by that
///     observation's EVIDENCE POWER -- not a flat alpha, not a scheduled/session-boundary
///     fold. Definitive evidence (honeypot hit, verified-bad-bot, security-tool detection,
///     high threat/attack severity -- <see cref="Mostlylucid.BotDetection.Orchestration.DetectionLedgerExtensions.HasHostileSignals"/>,
///     the SAME classifier the Tool-family demotion arm already uses) sets the fingerprint
///     to reflect it INSTANTLY, one hit. Weak/ambiguous evidence nudges it a little; it
///     must accumulate. "Requests lack power" = WEAK evidence lacks power, not "requests
///     can't move the fingerprint at all."
/// </summary>
public sealed class EvidencePowerAbsorptionTests
{
    [Fact]
    public void IsDefinitive_true_for_hostile_signals()
    {
        Assert.True(EvidencePowerAbsorption.IsDefinitive(hasHostileSignals: true, earlyExitVerifiedBadBot: false));
    }

    [Fact]
    public void IsDefinitive_true_for_verified_bad_bot_early_exit()
    {
        Assert.True(EvidencePowerAbsorption.IsDefinitive(hasHostileSignals: false, earlyExitVerifiedBadBot: true));
    }

    [Fact]
    public void IsDefinitive_false_for_an_ordinary_high_confidence_verdict()
    {
        // High probability + confidence alone (no honeypot/security-tool/attack signal) is
        // NOT categorically definitive -- it goes through the graduated tier instead.
        Assert.False(EvidencePowerAbsorption.IsDefinitive(hasHostileSignals: false, earlyExitVerifiedBadBot: false));
    }

    [Theory]
    [InlineData(0.5, 1.0, 0.0)]  // maximally ambiguous probability -> zero certainty regardless of confidence
    [InlineData(1.0, 1.0, 1.0)]  // maximally extreme + maximally confident -> full certainty
    [InlineData(0.0, 1.0, 1.0)]  // extreme the OTHER direction (confident human) -> still full certainty
    [InlineData(1.0, 0.0, 0.0)]  // extreme probability but zero backing confidence -> zero certainty
    [InlineData(0.75, 0.5, 0.25)] // partial extremity * partial confidence
    public void ComputeCertainty_derives_from_probability_extremity_times_confidence(
        double botProbability, double confidence, double expected)
    {
        var certainty = EvidencePowerAbsorption.ComputeCertainty(botProbability, confidence);
        Assert.Equal(expected, certainty, precision: 6);
    }

    [Fact]
    public void ResolveGraduatedAlpha_at_zero_certainty_is_the_steady_state_floor()
    {
        var alpha = EvidencePowerAbsorption.ResolveGraduatedAlpha(certainty: 0.0, steadyStateAlpha: 0.2, ceilingAlpha: 0.9);
        Assert.Equal(0.2, alpha);
    }

    [Fact]
    public void ResolveGraduatedAlpha_at_full_certainty_is_the_ceiling_not_full_overwrite()
    {
        // Ceiling stays below 1.0 deliberately -- literal instant-overwrite is reserved for
        // the definitive tier only, so a very-confident-but-not-categorically-definitive
        // observation still moves the fingerprint hard without fully discarding history.
        var alpha = EvidencePowerAbsorption.ResolveGraduatedAlpha(certainty: 1.0, steadyStateAlpha: 0.2, ceilingAlpha: 0.9);
        Assert.Equal(0.9, alpha, precision: 6);
    }

    [Fact]
    public void ResolveGraduatedAlpha_interpolates_linearly_between_floor_and_ceiling()
    {
        var alpha = EvidencePowerAbsorption.ResolveGraduatedAlpha(certainty: 0.5, steadyStateAlpha: 0.2, ceilingAlpha: 0.9);
        Assert.Equal(0.55, alpha, precision: 6);
    }

    [Fact]
    public void WeakAmbiguousObservation_barely_moves_the_cached_score()
    {
        // The exact "must accumulate" case: a single weak/ambiguous request (moderate
        // confidence, near-uncertain probability) should nudge a clean-history score only a
        // little.
        var cached = 0.05;
        const double freshObservation = 0.6; // weakly bot-leaning
        const double confidence = 0.3;       // low backing evidence

        var certainty = EvidencePowerAbsorption.ComputeCertainty(freshObservation, confidence);
        var alpha = EvidencePowerAbsorption.ResolveGraduatedAlpha(certainty, steadyStateAlpha: 0.2, ceilingAlpha: 0.9);
        var blended = cached * (1.0 - alpha) + freshObservation * alpha;

        Assert.True(blended < 0.35, $"Expected a weak observation to barely move the score, got {blended}.");
    }

    [Fact]
    public void DefinitiveObservation_sets_the_score_instantly_not_a_blend()
    {
        // The exact "honeypot first-hit trips instantly" case: a definitive observation
        // must produce the observation's own value directly, not an EWMA blend toward it.
        var cached = 0.05; // clean history
        const double freshObservation = 0.98;

        var isDefinitive = EvidencePowerAbsorption.IsDefinitive(hasHostileSignals: true, earlyExitVerifiedBadBot: false);
        var blended = isDefinitive ? freshObservation : cached; // caller's actual branch shape

        Assert.True(isDefinitive);
        Assert.Equal(0.98, blended);
    }
}
