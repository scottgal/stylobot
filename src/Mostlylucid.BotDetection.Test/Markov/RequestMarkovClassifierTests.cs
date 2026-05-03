using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Markov;

public class RequestMarkovClassifierTests
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
    public void SignalR_transport_signal_returns_SignalR()
    {
        var state = BuildState(signals: new() { [SignalKeys.TransportIsSignalR] = (bool?)true });
        Assert.Equal(RequestState.SignalR, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void WebSocket_upgrade_returns_WebSocket()
    {
        var state = BuildState(signals: new() { [SignalKeys.TransportIsUpgrade] = (bool?)true });
        Assert.Equal(RequestState.WebSocket, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void Static_protocol_class_returns_StaticAsset()
    {
        var state = BuildState(signals: new() { [SignalKeys.TransportProtocolClass] = "static" });
        Assert.Equal(RequestState.StaticAsset, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void Api_protocol_class_returns_ApiCall()
    {
        var state = BuildState(signals: new() { [SignalKeys.TransportProtocolClass] = "api" });
        Assert.Equal(RequestState.ApiCall, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void Default_GET_returns_PageView()
    {
        var state = BuildState(ctx => ctx.Request.Method = "GET");
        Assert.Equal(RequestState.PageView, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void SecFetchDest_script_returns_StaticAsset()
    {
        var state = BuildState(ctx =>
        {
            ctx.Request.Method = "GET";
            ctx.Request.Headers["Sec-Fetch-Dest"] = "script";
        });
        Assert.Equal(RequestState.StaticAsset, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void NotFound_status_returns_NotFound()
    {
        var state = BuildState(ctx => ctx.Response.StatusCode = 404);
        Assert.Equal(RequestState.NotFound, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void Auth_status_401_returns_AuthAttempt()
    {
        var state = BuildState(ctx => ctx.Response.StatusCode = 401);
        Assert.Equal(RequestState.AuthAttempt, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void Auth_status_403_returns_AuthAttempt()
    {
        var state = BuildState(ctx => ctx.Response.StatusCode = 403);
        Assert.Equal(RequestState.AuthAttempt, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void Post_with_form_contenttype_returns_FormSubmit()
    {
        var state = BuildState(ctx =>
        {
            ctx.Request.Method = "POST";
            ctx.Request.ContentType = "application/x-www-form-urlencoded";
        });
        Assert.Equal(RequestState.FormSubmit, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void Post_with_json_contenttype_returns_ApiCall()
    {
        var state = BuildState(ctx =>
        {
            ctx.Request.Method = "POST";
            ctx.Request.ContentType = "application/json";
        });
        Assert.Equal(RequestState.ApiCall, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void Path_with_search_returns_Search()
    {
        var state = BuildState(ctx =>
        {
            ctx.Request.Method = "GET";
            ctx.Request.Path = "/search";
        });
        Assert.Equal(RequestState.Search, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void Query_string_q_param_returns_Search()
    {
        var state = BuildState(ctx =>
        {
            ctx.Request.Method = "GET";
            ctx.Request.QueryString = new QueryString("?q=hello");
        });
        Assert.Equal(RequestState.Search, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void Accept_text_event_stream_returns_ServerSentEvent()
    {
        var state = BuildState(ctx =>
        {
            ctx.Request.Method = "GET";
            ctx.Request.Headers["Accept"] = "text/event-stream";
        });
        Assert.Equal(RequestState.ServerSentEvent, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void IsPrefetchRequest_purpose_header_returns_true()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Purpose"] = "prefetch";
        Assert.True(RequestMarkovClassifier.IsPrefetchRequest(ctx.Request));
    }

    [Fact]
    public void IsPrefetchRequest_sec_purpose_header_returns_true()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Sec-Purpose"] = "prefetch";
        Assert.True(RequestMarkovClassifier.IsPrefetchRequest(ctx.Request));
    }

    [Fact]
    public void IsPrefetchRequest_no_cors_document_returns_true()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Sec-Fetch-Mode"] = "no-cors";
        ctx.Request.Headers["Sec-Fetch-Dest"] = "document";
        Assert.True(RequestMarkovClassifier.IsPrefetchRequest(ctx.Request));
    }

    [Fact]
    public void IsPrefetchRequest_no_headers_returns_false()
    {
        var ctx = new DefaultHttpContext();
        Assert.False(RequestMarkovClassifier.IsPrefetchRequest(ctx.Request));
    }

    [Fact]
    public void IsPrefetchRequest_navigate_mode_returns_false()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Sec-Fetch-Mode"] = "navigate";
        ctx.Request.Headers["Sec-Fetch-Dest"] = "document";
        Assert.False(RequestMarkovClassifier.IsPrefetchRequest(ctx.Request));
    }
}
