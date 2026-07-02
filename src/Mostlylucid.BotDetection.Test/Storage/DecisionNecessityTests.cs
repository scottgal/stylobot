using FluentAssertions;
using Mostlylucid.BotDetection.Storage;

namespace Mostlylucid.BotDetection.Test.Storage;

/// <summary>
///     Unit tests for <see cref="DecisionNecessity"/> — the value-of-information
///     retention scorer. The properties under test ARE the design intent:
///     borderline and high-threat survive; resolved-and-harmless (certain human,
///     or certain benign bot) is shed first; recency breaks ties.
/// </summary>
public sealed class DecisionNecessityTests
{
    private const double Threshold = 0.70;
    private const double HalfLife = 3600;   // 1h

    private static double V(double p, double threat, double age = 0)
        => DecisionNecessity.Value(p, threat, age, Threshold, HalfLife);

    [Fact]
    public void Uncertainty_peaks_at_the_threshold()
    {
        DecisionNecessity.Uncertainty(0.70, Threshold).Should().BeApproximately(1.0, 1e-9);
        DecisionNecessity.Uncertainty(0.98, Threshold).Should().BeLessThan(0.10); // confident bot
        DecisionNecessity.Uncertainty(0.02, Threshold).Should().BeLessThan(0.01); // confident human
    }

    [Fact]
    public void Borderline_scores_higher_than_confident_either_way()
    {
        var borderline = V(0.70, threat: 0.0);
        V(0.98, threat: 0.0).Should().BeLessThan(borderline); // certain bot, benign
        V(0.05, threat: 0.0).Should().BeLessThan(borderline); // certain human
    }

    [Fact]
    public void Confident_benign_is_shed_but_confident_dangerous_is_kept()
    {
        // The subtle case: same certainty (p=0.98), threat flips the outcome.
        var benign = V(0.98, threat: 0.02);
        var dangerous = V(0.98, threat: 0.95);
        benign.Should().BeLessThan(0.15);          // resolved + harmless → shed
        dangerous.Should().BeGreaterThan(0.90);    // consequential → kept
        dangerous.Should().BeGreaterThan(benign);
    }

    [Fact]
    public void Resolved_and_harmless_is_the_global_minimum()
    {
        // A certain, low-threat human is the first thing a store under pressure sheds.
        var harmless = V(0.03, threat: 0.0);
        harmless.Should().BeLessThan(V(0.70, threat: 0.0));  // < borderline
        harmless.Should().BeLessThan(V(0.03, threat: 0.9));  // < high-threat
        harmless.Should().BeLessThan(0.05);
    }

    [Fact]
    public void Recency_breaks_ties_for_equal_classification()
    {
        var fresh = V(0.70, threat: 0.5, age: 0);
        var stale = V(0.70, threat: 0.5, age: 2 * HalfLife); // two half-lives old
        stale.Should().BeLessThan(fresh);
        stale.Should().BeApproximately(fresh * 0.25, 1e-6);  // 2 half-lives → x0.25
    }

    [Fact]
    public void ColdnessScore_orders_evict_first_ascending()
    {
        // Lower ColdnessScore = colder = evicted first.
        long Cold(double p, double t, double age) =>
            DecisionNecessity.ColdnessScore(p, t, age, Threshold, HalfLife);

        var harmless   = Cold(0.03, 0.0, 0);
        var borderline = Cold(0.70, 0.0, 0);
        var dangerous  = Cold(0.98, 0.95, 0);
        harmless.Should().BeLessThan(borderline);
        harmless.Should().BeLessThan(dangerous);
    }
}
