using Mostlylucid.BotDetection.Policies.Triggers;

namespace Mostlylucid.BotDetection.Test.Policies.Triggers;

/// <summary>
///     Coverage for <see cref="ArmedRuleAtom"/> -- the G5 sustain/recover
///     state machine. All time is driven explicitly by the test via the
///     <c>now</c> argument; no wall-clock dependence.
/// </summary>
public sealed class ArmedRuleAtomTests
{
    private static readonly TimeSpan Sustain = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Recover = TimeSpan.FromSeconds(60);

    [Fact]
    public void New_atom_is_unarmed_with_no_transitions()
    {
        var atom = new ArmedRuleAtom();
        Assert.False(atom.IsArmed);
        Assert.Null(atom.ArmedAt);
        Assert.Null(atom.RecoveredAt);
        Assert.Equal(0, atom.ArmCount);
        Assert.Equal(0, atom.RecoverCount);
    }

    [Fact]
    public void Sustained_true_arms_after_sustain_window()
    {
        var atom = new ArmedRuleAtom();
        var t0 = DateTimeOffset.UnixEpoch;

        // first true observation: starts the streak, no transition.
        Assert.False(atom.Observe(true, t0, Sustain, Recover));
        Assert.False(atom.IsArmed);

        // halfway through the sustain window: still no transition.
        Assert.False(atom.Observe(true, t0 + TimeSpan.FromSeconds(15), Sustain, Recover));
        Assert.False(atom.IsArmed);

        // at the exact sustain boundary: transitions to ARMED.
        Assert.True(atom.Observe(true, t0 + Sustain, Sustain, Recover));
        Assert.True(atom.IsArmed);
        Assert.Equal(t0 + Sustain, atom.ArmedAt);
        Assert.Equal(1, atom.ArmCount);

        // subsequent true observations are NOT transitions.
        Assert.False(atom.Observe(true, t0 + Sustain + TimeSpan.FromSeconds(5), Sustain, Recover));
        Assert.True(atom.IsArmed);
        Assert.Equal(1, atom.ArmCount);
    }

    [Fact]
    public void Sustained_false_recovers_after_recover_window()
    {
        var atom = new ArmedRuleAtom();
        var t0 = DateTimeOffset.UnixEpoch;

        // Get armed.
        atom.Observe(true, t0, Sustain, Recover);
        atom.Observe(true, t0 + Sustain, Sustain, Recover);
        Assert.True(atom.IsArmed);

        var armedAt = t0 + Sustain;

        // First false observation post-arm: starts the recover streak.
        Assert.False(atom.Observe(false, armedAt + TimeSpan.FromSeconds(1), Sustain, Recover));
        Assert.True(atom.IsArmed);

        // Halfway through recover: still armed.
        Assert.False(atom.Observe(false, armedAt + TimeSpan.FromSeconds(31), Sustain, Recover));
        Assert.True(atom.IsArmed);

        // At the recover boundary: transitions to UNARMED.
        var recoverAt = armedAt + TimeSpan.FromSeconds(1) + Recover;
        Assert.True(atom.Observe(false, recoverAt, Sustain, Recover));
        Assert.False(atom.IsArmed);
        Assert.Equal(recoverAt, atom.RecoveredAt);
        Assert.Equal(1, atom.RecoverCount);
    }

    [Fact]
    public void Brief_false_flip_inside_sustain_window_resets_arm_streak()
    {
        var atom = new ArmedRuleAtom();
        var t0 = DateTimeOffset.UnixEpoch;

        atom.Observe(true, t0, Sustain, Recover);
        atom.Observe(true, t0 + TimeSpan.FromSeconds(15), Sustain, Recover);

        // A false observation inside the sustain window resets the streak.
        Assert.False(atom.Observe(false, t0 + TimeSpan.FromSeconds(20), Sustain, Recover));
        Assert.False(atom.IsArmed);

        // Now the sustain clock restarts: true at t=21s, t=51s arms.
        Assert.False(atom.Observe(true, t0 + TimeSpan.FromSeconds(21), Sustain, Recover));
        Assert.True(atom.Observe(true, t0 + TimeSpan.FromSeconds(51), Sustain, Recover));
        Assert.True(atom.IsArmed);
    }

    [Fact]
    public void Brief_true_flip_inside_recover_window_resets_recover_streak()
    {
        var atom = new ArmedRuleAtom();
        var t0 = DateTimeOffset.UnixEpoch;

        // Get armed.
        atom.Observe(true, t0, Sustain, Recover);
        atom.Observe(true, t0 + Sustain, Sustain, Recover);
        var armedAt = t0 + Sustain;

        // Start recover streak.
        atom.Observe(false, armedAt + TimeSpan.FromSeconds(1), Sustain, Recover);

        // Single true blip mid-recover: streak resets, atom stays armed.
        Assert.False(atom.Observe(true, armedAt + TimeSpan.FromSeconds(30), Sustain, Recover));
        Assert.True(atom.IsArmed);

        // Recovery now requires another full Recover window of falses.
        Assert.False(atom.Observe(false, armedAt + TimeSpan.FromSeconds(31), Sustain, Recover));
        Assert.False(atom.Observe(false, armedAt + TimeSpan.FromSeconds(60), Sustain, Recover));
        // 60s after the new false streak starts (t=armedAt+31s) -> t=armedAt+91s
        Assert.True(atom.Observe(false, armedAt + TimeSpan.FromSeconds(91), Sustain, Recover));
        Assert.False(atom.IsArmed);
    }

    [Fact]
    public void Multiple_arm_cycles_increment_counters()
    {
        var atom = new ArmedRuleAtom();
        var t = DateTimeOffset.UnixEpoch;

        // Cycle 1.
        atom.Observe(true, t, Sustain, Recover);
        atom.Observe(true, t + Sustain, Sustain, Recover);
        atom.Observe(false, t + Sustain + TimeSpan.FromSeconds(1), Sustain, Recover);
        atom.Observe(false, t + Sustain + TimeSpan.FromSeconds(1) + Recover, Sustain, Recover);

        // Cycle 2.
        var t2 = t + Sustain + TimeSpan.FromSeconds(1) + Recover + TimeSpan.FromSeconds(1);
        atom.Observe(true, t2, Sustain, Recover);
        atom.Observe(true, t2 + Sustain, Sustain, Recover);

        Assert.Equal(2, atom.ArmCount);
        Assert.Equal(1, atom.RecoverCount);
        Assert.True(atom.IsArmed);
    }
}
