using Mostlylucid.BotDetection.Analysis;

namespace Mostlylucid.BotDetection.Test.Analysis;

/// <summary>
///     Pins the closed-loop envelope fields on <see cref="SessionRequest"/>
///     (audit #8 + <c>project_centroid_learning_feedback_loop</c>). The fields
///     must round-trip on the readonly record struct so any session-store
///     persistence path (SQLite / Postgres / commercial) carries the
///     enforcement-shape envelope alongside the existing FromUpstream flag.
/// </summary>
public class SessionRequestEnvelopeTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Defaults_preserve_back_compat_envelope_shape()
    {
        // Old-style construction without envelope arguments must still produce
        // the natural-traffic shape: FromUpstream=true, Shed=false, modes/policy
        // null. Existing callers (commercial / extension) keep working.
        var req = new SessionRequest(
            RequestState.PageView,
            BaseTime,
            "/index",
            200);

        Assert.True(req.FromUpstream);
        Assert.False(req.Shed);
        Assert.Null(req.EnforcementMode);
        Assert.Null(req.PolicyRevision);
    }

    [Fact]
    public void Envelope_round_trips_through_record_struct()
    {
        var req = new SessionRequest(
            RequestState.PageView,
            BaseTime,
            "/api/data",
            429,
            FromUpstream: false,
            Shed: true,
            EnforcementMode: "shed",
            PolicyRevision: "rev-2026-06-30");

        Assert.False(req.FromUpstream);
        Assert.True(req.Shed);
        Assert.Equal("shed", req.EnforcementMode);
        Assert.Equal("rev-2026-06-30", req.PolicyRevision);
    }

    [Theory]
    [InlineData("natural")]
    [InlineData("shed")]
    [InlineData("throttle")]
    [InlineData("block")]
    [InlineData("challenge")]
    public void EnforcementMode_accepts_each_canonical_value(string mode)
    {
        // Pins the canonical enforcement-mode value set the orchestrator /
        // dispatcher stamp. If a future writer invents a new value, the
        // session store's downstream filter needs to know.
        var req = new SessionRequest(
            RequestState.PageView,
            BaseTime,
            "/index",
            200,
            EnforcementMode: mode);

        Assert.Equal(mode, req.EnforcementMode);
    }
}