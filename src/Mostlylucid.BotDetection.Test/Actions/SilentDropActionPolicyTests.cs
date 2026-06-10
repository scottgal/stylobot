using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Test.Actions;

/// <summary>
///     Pins the silent-drop contract: calls <see cref="HttpContext.Abort"/>
///     on the request, writes no response body or headers, and returns an
///     <see cref="ActionResult"/> that short-circuits the pipeline. The
///     primitive HAProxy's <c>silent-drop</c> action exposes: Kestrel
///     resets the socket, no resources are held, the bot waits forever.
///
///     Why this matters: at any meaningful RPS the
///     <see cref="ThrottleActionPolicy"/> stealth path holds a Kestrel
///     concurrent-connection slot for the full delay. A bot operator who
///     spots stealth can pile up connections to exhaust
///     <c>MaxConcurrentConnections</c>. Silent-drop frees the slot
///     immediately and is the right answer for high-confidence bot
///     verdicts where there's no policy value in even returning a 429.
/// </summary>
public class SilentDropActionPolicyTests
{
    [Fact]
    public async Task Execute_AbortsConnection_AndReturnsBlockedResult()
    {
        var policy = new SilentDropActionPolicy("silent-drop");
        var ctx = NewContext(out var lifetime);

        var result = await policy.ExecuteAsync(ctx, Evidence());

        Assert.False(result.Continue, "silent-drop short-circuits the pipeline");
        Assert.True(lifetime.AbortCalled,
            "silent-drop must call IHttpRequestLifetimeFeature.Abort so Kestrel resets the connection");
    }

    [Fact]
    public async Task Execute_WritesNoResponseHeaders()
    {
        var policy = new SilentDropActionPolicy("silent-drop");
        var ctx = NewContext();

        await policy.ExecuteAsync(ctx, Evidence());

        // The default StatusCode for a fresh DefaultHttpContext is 200 and
        // headers should remain untouched. Silent-drop never writes a
        // response, so there should be nothing to inspect downstream.
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Empty(ctx.Response.Headers);
        Assert.Equal(0, ctx.Response.Body.Length);
    }

    [Fact]
    public async Task Execute_ReturnsZeroStatusCode_DescribingSilentDrop()
    {
        // ActionResult.StatusCode = 0 documents "no HTTP response was
        // emitted" so downstream middleware (logging, metrics) can
        // distinguish a silent-drop from a 200-with-no-body.
        var policy = new SilentDropActionPolicy("silent-drop");
        var ctx = NewContext();

        var result = await policy.ExecuteAsync(ctx, Evidence());

        Assert.Equal(0, result.StatusCode);
        Assert.NotNull(result.Description);
        Assert.Contains("silent-drop", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Type_AndIntent_AreBlockClass()
    {
        // Silent-drop is a denial policy. Surfaces as ActionType.Block /
        // PolicyIntent.Block so dashboards group it alongside the other
        // hard-deny outcomes.
        var policy = new SilentDropActionPolicy("silent-drop");

        Assert.Equal(ActionType.Block, policy.ActionType);
        Assert.Equal(PolicyIntent.Block, policy.Intent);
    }

    private static HttpContext NewContext()
        => NewContext(out _);

    private static HttpContext NewContext(out RecordingLifetime lifetime)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/";
        ctx.Response.Body = new MemoryStream();
        // DefaultHttpContext provides a stub IHttpRequestLifetimeFeature
        // whose Abort() is a no-op (real wire-up lives in Kestrel). Swap in
        // a recording fake so the test can observe HttpContext.Abort()
        // calling through.
        lifetime = new RecordingLifetime();
        ctx.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        return ctx;
    }

    private sealed class RecordingLifetime : IHttpRequestLifetimeFeature
    {
        private readonly CancellationTokenSource _cts = new();
        public bool AbortCalled { get; private set; }

        public CancellationToken RequestAborted
        {
            get => _cts.Token;
            set => throw new NotSupportedException();
        }

        public void Abort()
        {
            AbortCalled = true;
            _cts.Cancel();
        }
    }

    private static AggregatedEvidence Evidence() => new()
    {
        BotProbability = 0.98,
        Confidence = 1.0,
        RiskBand = RiskBand.VeryHigh,
        Signals = new Dictionary<string, object>(),
    };
}
