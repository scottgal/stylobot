using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Unit tests for TransportProtocolContributor.
///     Tests WebSocket, gRPC, GraphQL, SSE detection and protocol-specific validation.
/// </summary>
public class TransportProtocolContributorTests
{
    private readonly Mock<ILogger<TransportProtocolContributor>> _loggerMock;
    private readonly Mock<IDetectorConfigProvider> _configProviderMock;

    public TransportProtocolContributorTests()
    {
        _loggerMock = new Mock<ILogger<TransportProtocolContributor>>();
        _configProviderMock = new Mock<IDetectorConfigProvider>();

        _configProviderMock.Setup(c => c.GetDefaults(It.IsAny<string>()))
            .Returns(new DetectorDefaults());
        _configProviderMock.Setup(c => c.GetManifest(It.IsAny<string>()))
            .Returns((DetectorManifest?)null);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string _, string _, int def) => def);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()))
            .Returns((string _, string _, double def) => def);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string _, string _, bool def) => def);
    }

    private TransportProtocolContributor CreateContributor()
    {
        return new TransportProtocolContributor(_loggerMock.Object, _configProviderMock.Object);
    }

    private BlackboardState CreateState(Dictionary<string, string>? headers = null, string? path = null,
        string? contentType = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Protocol = "HTTP/2";

        if (path != null)
            httpContext.Request.Path = path;

        if (contentType != null)
            httpContext.Request.ContentType = contentType;

        if (headers != null)
            foreach (var (key, value) in headers)
                httpContext.Request.Headers[key] = value;

        var signalDict = new ConcurrentDictionary<string, object>();
        return new BlackboardState
        {
            HttpContext = httpContext,
            Signals = signalDict,
            SignalWriter = signalDict,
            CurrentRiskScore = 0,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = Array.Empty<DetectionContribution>(),
            RequestId = Guid.NewGuid().ToString()
        };
    }

    // ==========================================
    // Properties Tests
    // ==========================================

    [Fact]
    public void Name_ReturnsTransportProtocol()
    {
        var contributor = CreateContributor();
        Assert.Equal("TransportProtocol", contributor.Name);
    }

    [Fact]
    public void TriggerConditions_IsEmpty_RunsInFirstWave()
    {
        var contributor = CreateContributor();
        Assert.Empty(contributor.TriggerConditions);
    }

    // ==========================================
    // Regular HTTP Request Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_RegularHttp_EmitsHttpProtocol_NoPenalties()
    {
        var contributor = CreateContributor();
        var state = CreateState();

        var contributions = await contributor.ContributeAsync(state);

        Assert.Equal("http", state.Signals[SignalKeys.TransportProtocol]);
        // Should only have the neutral "analysis complete" contribution
        Assert.Single(contributions);
        Assert.Equal(0, contributions[0].ConfidenceDelta);
    }

    // ==========================================
    // WebSocket Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_ValidWebSocketUpgrade_NoPenalties()
    {
        var contributor = CreateContributor();
        // Valid WebSocket upgrade: all required headers present
        var key = Convert.ToBase64String(new byte[16]); // valid 16-byte base64
        var state = CreateState(new Dictionary<string, string>
        {
            ["Connection"] = "Upgrade",
            ["Upgrade"] = "websocket",
            ["Sec-WebSocket-Key"] = key,
            ["Sec-WebSocket-Version"] = "13",
            ["Origin"] = "https://example.com"
        });

        var contributions = await contributor.ContributeAsync(state);

        Assert.Equal("websocket", state.Signals[SignalKeys.TransportProtocol]);
        Assert.True((bool)state.Signals[SignalKeys.TransportIsUpgrade]);
        Assert.True((bool)state.Signals[SignalKeys.TransportWebSocketOrigin]);
        // No bot penalties for valid upgrade
        Assert.DoesNotContain(contributions, c => c.ConfidenceDelta > 0);
    }

    [Fact]
    public async Task ContributeAsync_WebSocketMissingKey_BotSignal()
    {
        var contributor = CreateContributor();
        var state = CreateState(new Dictionary<string, string>
        {
            ["Connection"] = "Upgrade",
            ["Upgrade"] = "websocket",
            ["Sec-WebSocket-Version"] = "13",
            ["Origin"] = "https://example.com"
        });

        var contributions = await contributor.ContributeAsync(state);

        Assert.Equal("websocket", state.Signals[SignalKeys.TransportProtocol]);
        var botContrib = contributions.FirstOrDefault(c =>
            c.Reason != null && c.Reason.Contains("Sec-WebSocket-Key"));
        Assert.NotNull(botContrib);
        Assert.True(botContrib!.ConfidenceDelta > 0);
    }

    [Fact]
    public async Task ContributeAsync_WebSocketWrongVersion_BotSignal()
    {
        var contributor = CreateContributor();
        var key = Convert.ToBase64String(new byte[16]);
        var state = CreateState(new Dictionary<string, string>
        {
            ["Connection"] = "Upgrade",
            ["Upgrade"] = "websocket",
            ["Sec-WebSocket-Key"] = key,
            ["Sec-WebSocket-Version"] = "8",
            ["Origin"] = "https://example.com"
        });

        var contributions = await contributor.ContributeAsync(state);

        Assert.Equal("8", state.Signals[SignalKeys.TransportWebSocketVersion]);
        var botContrib = contributions.FirstOrDefault(c =>
            c.Reason != null && c.Reason.Contains("Sec-WebSocket-Version"));
        Assert.NotNull(botContrib);
        Assert.True(botContrib!.ConfidenceDelta > 0);
    }

    [Fact]
    public async Task ContributeAsync_WebSocketMissingOrigin_BotSignal()
    {
        var contributor = CreateContributor();
        var key = Convert.ToBase64String(new byte[16]);
        var state = CreateState(new Dictionary<string, string>
        {
            ["Connection"] = "Upgrade",
            ["Upgrade"] = "websocket",
            ["Sec-WebSocket-Key"] = key,
            ["Sec-WebSocket-Version"] = "13"
            // No Origin header
        });

        var contributions = await contributor.ContributeAsync(state);

        Assert.False((bool)state.Signals[SignalKeys.TransportWebSocketOrigin]);
        var botContrib = contributions.FirstOrDefault(c =>
            c.Reason != null && c.Reason.Contains("Origin"));
        Assert.NotNull(botContrib);
        Assert.True(botContrib!.ConfidenceDelta > 0);
    }

    [Fact]
    public async Task ContributeAsync_WebSocketInvalidKey_BotSignal()
    {
        var contributor = CreateContributor();
        var state = CreateState(new Dictionary<string, string>
        {
            ["Connection"] = "Upgrade",
            ["Upgrade"] = "websocket",
            ["Sec-WebSocket-Key"] = "not-valid-base64!!",
            ["Sec-WebSocket-Version"] = "13",
            ["Origin"] = "https://example.com"
        });

        var contributions = await contributor.ContributeAsync(state);

        var botContrib = contributions.FirstOrDefault(c =>
            c.Reason != null && c.Reason.Contains("Invalid Sec-WebSocket-Key"));
        Assert.NotNull(botContrib);
        Assert.True(botContrib!.ConfidenceDelta > 0);
    }

    [Fact]
    public async Task ContributeAsync_WebSocketOriginMismatch_CSWSH_BotSignal()
    {
        var contributor = CreateContributor();
        var key = Convert.ToBase64String(new byte[16]);
        var state = CreateState(new Dictionary<string, string>
        {
            ["Connection"] = "Upgrade",
            ["Upgrade"] = "websocket",
            ["Sec-WebSocket-Key"] = key,
            ["Sec-WebSocket-Version"] = "13",
            ["Origin"] = "https://evil.com",
            ["Host"] = "example.com"
        });

        var contributions = await contributor.ContributeAsync(state);

        var botContrib = contributions.FirstOrDefault(c =>
            c.Reason != null && c.Reason.Contains("CSWSH"));
        Assert.NotNull(botContrib);
        Assert.True(botContrib!.ConfidenceDelta > 0);
    }

    [Fact]
    public async Task ContributeAsync_WebSocketOriginMatchesHost_NoCswsh()
    {
        var contributor = CreateContributor();
        var key = Convert.ToBase64String(new byte[16]);
        var state = CreateState(new Dictionary<string, string>
        {
            ["Connection"] = "Upgrade",
            ["Upgrade"] = "websocket",
            ["Sec-WebSocket-Key"] = key,
            ["Sec-WebSocket-Version"] = "13",
            ["Origin"] = "https://example.com",
            ["Host"] = "example.com"
        });

        var contributions = await contributor.ContributeAsync(state);

        Assert.DoesNotContain(contributions, c =>
            c.Reason != null && c.Reason.Contains("CSWSH"));
    }

    // ==========================================
    // gRPC Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_GrpcWithProperHeaders_Neutral()
    {
        var contributor = CreateContributor();
        var state = CreateState(
            new Dictionary<string, string>
            {
                ["te"] = "trailers"
            },
            contentType: "application/grpc");

        var contributions = await contributor.ContributeAsync(state);

        Assert.Equal("grpc", state.Signals[SignalKeys.TransportProtocol]);
        Assert.Equal("application/grpc", state.Signals[SignalKeys.TransportGrpcContentType]);
        // With te: trailers and no browser UA, should have no bot penalties
        Assert.DoesNotContain(contributions, c => c.ConfidenceDelta > 0);
    }

    [Fact]
    public async Task ContributeAsync_GrpcMissingTeTrailers_BotSignal()
    {
        var contributor = CreateContributor();
        var state = CreateState(contentType: "application/grpc");

        var contributions = await contributor.ContributeAsync(state);

        Assert.Equal("grpc", state.Signals[SignalKeys.TransportProtocol]);
        var botContrib = contributions.FirstOrDefault(c =>
            c.Reason != null && c.Reason.Contains("te: trailers"));
        Assert.NotNull(botContrib);
        Assert.True(botContrib!.ConfidenceDelta > 0);
    }

    [Fact]
    public async Task ContributeAsync_GrpcWithBrowserUA_BotSignal()
    {
        var contributor = CreateContributor();
        var state = CreateState(
            new Dictionary<string, string>
            {
                ["te"] = "trailers",
                ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0"
            },
            contentType: "application/grpc");

        var contributions = await contributor.ContributeAsync(state);

        var botContrib = contributions.FirstOrDefault(c =>
            c.Reason != null && c.Reason.Contains("browser User-Agent"));
        Assert.NotNull(botContrib);
        Assert.True(botContrib!.ConfidenceDelta > 0);
    }

    [Fact]
    public async Task ContributeAsync_GrpcReflectionProbe_BotSignal()
    {
        var contributor = CreateContributor();
        var state = CreateState(
            new Dictionary<string, string>
            {
                ["te"] = "trailers"
            },
            path: "/grpc.reflection.v1alpha.ServerReflection/ServerReflectionInfo",
            contentType: "application/grpc");

        var contributions = await contributor.ContributeAsync(state);

        var botContrib = contributions.FirstOrDefault(c =>
            c.Reason != null && c.Reason.Contains("reflection"));
        Assert.NotNull(botContrib);
        Assert.True(botContrib!.ConfidenceDelta > 0);
    }

    [Fact]
    public async Task ContributeAsync_GrpcWeb_NoTeTrailersPenalty()
    {
        var contributor = CreateContributor();
        // grpc-web is legitimate from browsers and doesn't require te: trailers
        var state = CreateState(
            new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0 Chrome/120.0"
            },
            contentType: "application/grpc-web+proto");

        var contributions = await contributor.ContributeAsync(state);

        Assert.Equal("grpc-web", state.Signals[SignalKeys.TransportProtocol]);
        // grpc-web should NOT get the te: trailers penalty or browser UA penalty
        Assert.DoesNotContain(contributions, c => c.ConfidenceDelta > 0);
    }

    [Fact]
    public async Task ContributeAsync_GrpcPlusProto_DetectsAsGrpc()
    {
        var contributor = CreateContributor();
        var state = CreateState(
            new Dictionary<string, string>
            {
                ["te"] = "trailers"
            },
            contentType: "application/grpc+proto");

        var contributions = await contributor.ContributeAsync(state);

        Assert.Equal("grpc", state.Signals[SignalKeys.TransportProtocol]);
    }

    // ==========================================
    // GraphQL Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_GraphqlPath_DetectsAsGraphql()
    {
        var contributor = CreateContributor();
        var state = CreateState(path: "/graphql", contentType: "application/json");

        var contributions = await contributor.ContributeAsync(state);

        Assert.Equal("graphql", state.Signals[SignalKeys.TransportProtocol]);
    }

    [Fact]
    public async Task ContributeAsync_GraphqlGqlPath_DetectsAsGraphql()
    {
        var contributor = CreateContributor();
        var state = CreateState(path: "/api/gql", contentType: "application/json");

        var contributions = await contributor.ContributeAsync(state);

        Assert.Equal("graphql", state.Signals[SignalKeys.TransportProtocol]);
    }

    [Fact]
    public async Task ContributeAsync_GraphqlIntrospection_BotSignal()
    {
        var contributor = CreateContributor();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Protocol = "HTTP/2";
        httpContext.Request.Path = "/graphql";
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.QueryString = new QueryString("?query={__schema{types{name}}}");

        var signalDict = new ConcurrentDictionary<string, object>();
        var state = new BlackboardState
        {
            HttpContext = httpContext,
            Signals = signalDict,
            SignalWriter = signalDict,
            CurrentRiskScore = 0,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = Array.Empty<DetectionContribution>(),
            RequestId = Guid.NewGuid().ToString()
        };

        var contributions = await contributor.ContributeAsync(state);

        Assert.True((bool)state.Signals[SignalKeys.TransportGraphqlIntrospection]);
        var botContrib = contributions.FirstOrDefault(c =>
            c.Reason != null && c.Reason.Contains("introspection"));
        Assert.NotNull(botContrib);
        Assert.True(botContrib!.ConfidenceDelta > 0);
    }

    [Fact]
    public async Task ContributeAsync_GraphqlBatch_MildBotSignal()
    {
        var contributor = CreateContributor();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Protocol = "HTTP/2";
        httpContext.Request.Path = "/graphql";
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Headers["X-GraphQL-Batch"] = "true";

        var signalDict = new ConcurrentDictionary<string, object>();
        var state = new BlackboardState
        {
            HttpContext = httpContext,
            Signals = signalDict,
            SignalWriter = signalDict,
            CurrentRiskScore = 0,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = Array.Empty<DetectionContribution>(),
            RequestId = Guid.NewGuid().ToString()
        };

        var contributions = await contributor.ContributeAsync(state);

        Assert.True((bool)state.Signals[SignalKeys.TransportGraphqlBatch]);
        var botContrib = contributions.FirstOrDefault(c =>
            c.Reason != null && c.Reason.Contains("batch"));
        Assert.NotNull(botContrib);
        // Batch is mild — legitimate but high-abuse
        Assert.True(botContrib!.ConfidenceDelta > 0);
        Assert.True(botContrib.ConfidenceDelta < 0.3, "Batch signal should be mild");
    }

    [Fact]
    public async Task ContributeAsync_GraphqlGetMutation_SpecViolation_BotSignal()
    {
        var contributor = CreateContributor();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Protocol = "HTTP/2";
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/graphql";
        httpContext.Request.QueryString = new QueryString("?query=mutation{deleteUser(id:1){ok}}");

        var signalDict = new ConcurrentDictionary<string, object>();
        var state = new BlackboardState
        {
            HttpContext = httpContext,
            Signals = signalDict,
            SignalWriter = signalDict,
            CurrentRiskScore = 0,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = Array.Empty<DetectionContribution>(),
            RequestId = Guid.NewGuid().ToString()
        };

        var contributions = await contributor.ContributeAsync(state);

        var botContrib = contributions.FirstOrDefault(c =>
            c.Reason != null && c.Reason.Contains("spec violation"));
        Assert.NotNull(botContrib);
        Assert.True(botContrib!.ConfidenceDelta > 0);
    }

    [Fact]
    public async Task ContributeAsync_GraphqlNormal_NoPenalties()
    {
        var contributor = CreateContributor();
        var state = CreateState(path: "/graphql", contentType: "application/json");

        var contributions = await contributor.ContributeAsync(state);

        Assert.Equal("graphql", state.Signals[SignalKeys.TransportProtocol]);
        Assert.False((bool)state.Signals[SignalKeys.TransportGraphqlIntrospection]);
        Assert.False((bool)state.Signals[SignalKeys.TransportGraphqlBatch]);
        Assert.DoesNotContain(contributions, c => c.ConfidenceDelta > 0);
    }

    // ==========================================
    // SSE Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_SseWithCacheControl_NoPenalty()
    {
        var contributor = CreateContributor();
        var state = CreateState(new Dictionary<string, string>
        {
            ["Accept"] = "text/event-stream",
            ["Cache-Control"] = "no-cache"
        });

        var contributions = await contributor.ContributeAsync(state);

        Assert.Equal("sse", state.Signals[SignalKeys.TransportProtocol]);
        Assert.True((bool)state.Signals[SignalKeys.TransportSse]);
        // With Cache-Control: no-cache, should have no penalties
        Assert.DoesNotContain(contributions, c => c.ConfidenceDelta > 0);
    }

    [Fact]
    public async Task ContributeAsync_SseMissingCacheControl_BotSignal()
    {
        var contributor = CreateContributor();
        var state = CreateState(new Dictionary<string, string>
        {
            ["Accept"] = "text/event-stream"
            // No Cache-Control header
        });

        var contributions = await contributor.ContributeAsync(state);

        Assert.Equal("sse", state.Signals[SignalKeys.TransportProtocol]);
        var botContrib = contributions.FirstOrDefault(c =>
            c.Reason != null && c.Reason.Contains("Cache-Control"));
        Assert.NotNull(botContrib);
        Assert.True(botContrib!.ConfidenceDelta > 0);
    }

    [Fact]
    public async Task ContributeAsync_SseHistoryReplay_BotSignal()
    {
        var contributor = CreateContributor();
        var state = CreateState(new Dictionary<string, string>
        {
            ["Accept"] = "text/event-stream",
            ["Cache-Control"] = "no-cache",
            ["Last-Event-ID"] = "0"
        });

        var contributions = await contributor.ContributeAsync(state);

        var botContrib = contributions.FirstOrDefault(c =>
            c.Reason != null && c.Reason.Contains("history replay"));
        Assert.NotNull(botContrib);
        Assert.True(botContrib!.ConfidenceDelta > 0);
    }

    [Fact]
    public async Task ContributeAsync_SseNormalLastEventId_NoReplayPenalty()
    {
        var contributor = CreateContributor();
        var state = CreateState(new Dictionary<string, string>
        {
            ["Accept"] = "text/event-stream",
            ["Cache-Control"] = "no-cache",
            ["Last-Event-ID"] = "42"
        });

        var contributions = await contributor.ContributeAsync(state);

        // Normal Last-Event-ID should not trigger replay warning
        Assert.DoesNotContain(contributions, c =>
            c.Reason != null && c.Reason.Contains("history replay"));
    }

    // ==========================================
    // Protocol Priority Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_WebSocketTakesPriorityOverSse()
    {
        var contributor = CreateContributor();
        var key = Convert.ToBase64String(new byte[16]);
        var state = CreateState(new Dictionary<string, string>
        {
            ["Connection"] = "Upgrade",
            ["Upgrade"] = "websocket",
            ["Sec-WebSocket-Key"] = key,
            ["Sec-WebSocket-Version"] = "13",
            ["Origin"] = "https://example.com",
            ["Accept"] = "text/event-stream" // Also has SSE accept
        });

        var contributions = await contributor.ContributeAsync(state);

        // WebSocket should win because it's checked first
        Assert.Equal("websocket", state.Signals[SignalKeys.TransportProtocol]);
    }

    [Fact]
    public async Task ContributeAsync_ContentTypeGraphql_DetectsAsGraphql()
    {
        var contributor = CreateContributor();
        // Detect via content-type instead of path
        var state = CreateState(
            path: "/api/v1/data",
            contentType: "application/graphql");

        var contributions = await contributor.ContributeAsync(state);

        Assert.Equal("graphql", state.Signals[SignalKeys.TransportProtocol]);
    }

    // ==========================================
    // Behind Proxy Tests
    // ==========================================

    [Fact]
    public async Task ContributeAsync_WebSocketBehindProxy_StillDetects()
    {
        var contributor = CreateContributor();
        var key = Convert.ToBase64String(new byte[16]);
        var state = CreateState(new Dictionary<string, string>
        {
            ["Connection"] = "Upgrade",
            ["Upgrade"] = "websocket",
            ["Sec-WebSocket-Key"] = key,
            ["Sec-WebSocket-Version"] = "13",
            ["Origin"] = "https://example.com",
            ["X-HTTP-Protocol"] = "HTTP/1.1" // Behind proxy
        });

        var contributions = await contributor.ContributeAsync(state);

        Assert.Equal("websocket", state.Signals[SignalKeys.TransportProtocol]);
    }
}
