using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Markov;

/// <summary>
///     Pins <see cref="RequestMarkovClassifier"/>'s
///     <c>response.from_upstream</c> gate. When stylobot itself synthesised
///     the current status (load-shed 503, policy block 403, honeypot 404,
///     throttle 429), the Markov state MUST demote to PageView so the
///     session vector doesn't bake stylobot's own enforcement-shape codes
///     back into the visitor's behaviour vector (closed-loop feedback).
///     Per "ONLY upstream status codes should be factored in".
/// </summary>
public class RequestMarkovClassifierFromUpstreamTests
{
    private static BlackboardState BuildState(
        Action<DefaultHttpContext>? configureHttp = null,
        Dictionary<string, object>? signals = null)
    {
        var ctx = new DefaultHttpContext();
        configureHttp?.Invoke(ctx);
        var signalDict = new ConcurrentDictionary<string, object>(
            signals ?? new Dictionary<string, object>());
        return new BlackboardState
        {
            HttpContext = ctx,
            Signals = signalDict,
            SignalWriter = signalDict,
            CurrentRiskScore = 0,
            CompletedDetectors = ImmutableHashSet<string>.Empty,
            FailedDetectors = ImmutableHashSet<string>.Empty,
            Contributions = ImmutableList<DetectionContribution>.Empty,
            RequestId = Guid.NewGuid().ToString("N"),
            Elapsed = TimeSpan.Zero
        };
    }

    [Fact]
    public void NotFound_status_demotes_to_PageView_when_response_is_from_stylobot()
    {
        // Honeypot 404 set by stylobot itself: must not be treated as a
        // scanner-shape Markov transition (closed-loop feedback guard).
        var state = BuildState(
            ctx => ctx.Response.StatusCode = 404,
            signals: new() { [SignalKeys.ResponseFromUpstream] = (bool?)false });
        Assert.Equal(RequestState.PageView, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void Auth_status_demotes_to_PageView_when_response_is_from_stylobot()
    {
        // Policy block 403 / 401 from stylobot's own enforcement: must not
        // classify as AuthAttempt.
        var state = BuildState(
            ctx => ctx.Response.StatusCode = 403,
            signals: new() { [SignalKeys.ResponseFromUpstream] = (bool?)false });
        Assert.Equal(RequestState.PageView, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void NotFound_status_returns_NotFound_when_response_is_from_upstream()
    {
        // Regression guard: upstream 404 still classifies as NotFound.
        var state = BuildState(
            ctx => ctx.Response.StatusCode = 404,
            signals: new() { [SignalKeys.ResponseFromUpstream] = (bool?)true });
        Assert.Equal(RequestState.NotFound, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void NotFound_status_returns_NotFound_when_signal_absent_back_compat()
    {
        // Back-compat: absent signal means upstream (existing FOSS hosts).
        var state = BuildState(ctx => ctx.Response.StatusCode = 404);
        Assert.Equal(RequestState.NotFound, RequestMarkovClassifier.Classify(state));
    }
}