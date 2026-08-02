using Mostlylucid.BotDetection.Identity;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Phase 1 of the 2026-08-02 fp-cache-current architecture mandate: while a fingerprint
///     is inside its drift-reopen window, verdict writes must use the wide reopen alpha
///     instead of the slow steady-state alpha, so a fingerprint whose behaviour just
///     changed converges within ~1-2 observations instead of dozens.
/// </summary>
public sealed class DriftReopenAbsorptionTests
{
    private const double SteadyStateAlpha = 0.2;
    private const double ReopenAlpha = 0.6;

    [Fact]
    public void ResolveAlpha_uses_steady_state_alpha_when_never_drifted()
    {
        var alpha = DriftReopenAbsorption.ResolveAlpha(
            driftReopenedUntilUtc: null,
            nowUtc: new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc),
            SteadyStateAlpha, ReopenAlpha);

        Assert.Equal(SteadyStateAlpha, alpha);
    }

    [Fact]
    public void ResolveAlpha_uses_wide_alpha_while_inside_the_reopen_window()
    {
        var now = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        var reopenedUntil = now.AddMinutes(3);

        var alpha = DriftReopenAbsorption.ResolveAlpha(reopenedUntil, now, SteadyStateAlpha, ReopenAlpha);

        Assert.Equal(ReopenAlpha, alpha);
    }

    [Fact]
    public void ResolveAlpha_falls_back_to_steady_state_once_the_window_has_closed()
    {
        var now = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        var reopenedUntil = now.AddSeconds(-1); // closed a moment ago

        var alpha = DriftReopenAbsorption.ResolveAlpha(reopenedUntil, now, SteadyStateAlpha, ReopenAlpha);

        Assert.Equal(SteadyStateAlpha, alpha);
    }

    [Fact]
    public void ResolveAlpha_at_the_exact_boundary_is_no_longer_reopened()
    {
        // now == reopenedUntil: the window has JUST closed (strict less-than semantics),
        // so this must read as steady-state, not reopened.
        var boundary = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

        var alpha = DriftReopenAbsorption.ResolveAlpha(boundary, boundary, SteadyStateAlpha, ReopenAlpha);

        Assert.Equal(SteadyStateAlpha, alpha);
    }

    [Fact]
    public void ResolveAlpha_converges_a_drifted_fingerprint_to_a_strong_observation_within_two_writes()
    {
        // The actual regression this exists to prevent: a mature fingerprint cached at a
        // LOW probability (clean history) that suddenly behaves like a 98%-confidence bot.
        // Under the OLD flat steady-state alpha (0.2) it would take many observations to
        // cross even 50%. Under the reopened alpha (0.6) it should cross 50% within 2 writes.
        var now = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        var reopenedUntil = now.AddMinutes(5);
        var cached = 0.05; // clean adblocker history
        const double freshObservation = 0.98; // the new, strongly-contradicting evidence

        var alpha1 = DriftReopenAbsorption.ResolveAlpha(reopenedUntil, now, SteadyStateAlpha, ReopenAlpha);
        cached = cached * (1.0 - alpha1) + freshObservation * alpha1;

        var alpha2 = DriftReopenAbsorption.ResolveAlpha(reopenedUntil, now.AddSeconds(1), SteadyStateAlpha, ReopenAlpha);
        cached = cached * (1.0 - alpha2) + freshObservation * alpha2;

        Assert.True(cached > 0.5, $"Expected the cached score to cross 50% bot within 2 reopened writes, got {cached}.");
    }
}
