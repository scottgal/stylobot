using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     SensorAtom (per Taxonomy.md) that classifies the request's transport
///     protocol (WebSocket / gRPC / GraphQL / SSE / SignalR / plain HTTP)
///     and validates protocol-specific compliance at the HTTP layer -- before
///     any protocol upgrade. Priority 5, Wave 0.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>TransportProtocolContributor</c>. Preserves the WebSocket /
///         gRPC / GraphQL / SSE / SignalR analysis paths verbatim -- pure
///         HTTP-layer inspection, no body reads.
///     </para>
///     <para>
///         Emits the canonical transport signals (TransportProtocol /
///         TransportClass / TransportProtocolClass / TransportIsSignalR /
///         TransportIsStreaming / TransportIsUpgrade) that every deferred
///         detector keys off.
///     </para>
/// </remarks>
public sealed partial class TransportProtocolAtom : DetectorAtomBase
{
    // TODO: migrate to YAML per feedback_no_word_lists (shared UA-family list).
    private static readonly string[] BrowserUaFamilies =
    [
        "Mozilla/", "Chrome/", "Safari/", "Firefox/", "Edge/", "Opera/"
    ];

    private readonly ILogger<TransportProtocolAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public TransportProtocolAtom(
        ILogger<TransportProtocolAtom> logger,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "TransportProtocol", category: "Protocol")
    {
        _logger = logger;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 5;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    private double MissingWsHeadersConfidence => _configProvider.GetParameter(Name, "missing_ws_headers_confidence", 0.6);
    private double InvalidWsVersionConfidence => _configProvider.GetParameter(Name, "invalid_ws_version_confidence", 0.5);
    private double MissingWsOriginConfidence => _configProvider.GetParameter(Name, "missing_ws_origin_confidence", 0.3);
    private double WsOriginMismatchConfidence => _configProvider.GetParameter(Name, "ws_origin_mismatch_confidence", 0.5);
    private double GrpcBrowserUaConfidence => _configProvider.GetParameter(Name, "grpc_browser_ua_confidence", 0.5);
    private double GrpcMissingTeConfidence => _configProvider.GetParameter(Name, "grpc_missing_te_confidence", 0.4);
    private double GrpcReflectionConfidence => _configProvider.GetParameter(Name, "grpc_reflection_confidence", 0.4);
    private double GraphqlIntrospectionConfidence => _configProvider.GetParameter(Name, "graphql_introspection_confidence", 0.3);
    private double GraphqlBatchConfidence => _configProvider.GetParameter(Name, "graphql_batch_confidence", 0.15);
    private double GraphqlGetMutationConfidence => _configProvider.GetParameter(Name, "graphql_get_mutation_confidence", 0.5);
    private double SseMissingCacheControlConfidence => _configProvider.GetParameter(Name, "sse_missing_cache_control_confidence", 0.15);
    private double SseHistoryReplayConfidence => _configProvider.GetParameter(Name, "sse_history_replay_confidence", 0.3);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        var contributions = new List<DetectionContribution>();

        try
        {
            var request = context.Request;
            var protocol = "http";

            if (IsWebSocketUpgrade(request))
            {
                protocol = "websocket";
                sink.Raise($"{SignalKeys.TransportIsUpgrade}:true", sessionId);
                AnalyzeWebSocket(sink, sessionId, request, contributions);
            }
            else if (IsGrpc(request, out var grpcContentType))
            {
                protocol = grpcContentType.Contains("grpc-web", StringComparison.OrdinalIgnoreCase)
                    ? "grpc-web"
                    : "grpc";
                sink.Raise($"{SignalKeys.TransportGrpcContentType}:{grpcContentType}", sessionId);
                AnalyzeGrpc(sink, sessionId, request, protocol, contributions);
            }
            else if (IsGraphql(request))
            {
                protocol = "graphql";
                AnalyzeGraphql(sink, sessionId, request, contributions);
            }
            else if (IsSse(request))
            {
                protocol = "sse";
                sink.Raise($"{SignalKeys.TransportSse}:true", sessionId);
                AnalyzeSse(sink, sessionId, request, contributions);
            }

            sink.Raise($"{SignalKeys.TransportProtocol}:{protocol}", sessionId);

            var transportClass = protocol switch
            {
                "websocket" => "websocket",
                "sse" => "sse",
                _ => "http"
            };

            var isSignalR = IsSignalRNegotiate(request) || IsSignalRConnect(request, protocol);
            var signalRType = isSignalR ? ClassifySignalRType(request, protocol) : null;

            var protocolClass = isSignalR
                ? "signalr"
                : protocol switch
                {
                    "grpc" or "grpc-web" => "grpc",
                    "graphql" => "api",
                    _ => "unknown"
                };

            if (protocolClass == "unknown" && !isSignalR)
            {
                var requestPath = request.Path.Value ?? string.Empty;
                if (requestPath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                {
                    protocolClass = "api";
                }
                else
                {
                    var secFetchDest = request.Headers["Sec-Fetch-Dest"].FirstOrDefault() ?? string.Empty;
                    var secFetchMode = request.Headers["Sec-Fetch-Mode"].FirstOrDefault() ?? string.Empty;
                    if (secFetchDest.Equals("empty", StringComparison.OrdinalIgnoreCase)
                        && secFetchMode.Equals("cors", StringComparison.OrdinalIgnoreCase))
                        protocolClass = "api";
                }
            }

            var isStreaming = transportClass != "http" || isSignalR;

            sink.Raise($"{SignalKeys.TransportClass}:{transportClass}", sessionId);
            sink.Raise($"{SignalKeys.TransportProtocolClass}:{protocolClass}", sessionId);
            sink.Raise($"{SignalKeys.TransportIsSignalR}:{(isSignalR ? "true" : "false")}", sessionId);
            sink.Raise($"{SignalKeys.TransportIsStreaming}:{(isStreaming ? "true" : "false")}", sessionId);

            if (signalRType is not null)
                sink.Raise($"{SignalKeys.TransportSignalRType}:{signalRType}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error analyzing transport protocol");
            sink.Raise($"{SignalKeys.TransportProtocol}:http", sessionId);
            sink.Raise($"{SignalKeys.TransportIsStreaming}:false", sessionId);
        }

        if (contributions.Count == 0)
            contributions.Add(DetectionContribution.Info(Name, Category, "Transport protocol analysis complete"));

        return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
    }

    private void AnalyzeWebSocket(SignalSink sink, string sessionId, HttpRequest request, List<DetectionContribution> contributions)
    {
        var hasKey = request.Headers.ContainsKey("Sec-WebSocket-Key");
        var hasVersion = request.Headers.TryGetValue("Sec-WebSocket-Version", out var version);
        var hasOrigin = request.Headers.ContainsKey("Origin");

        if (!hasKey)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = MissingWsHeadersConfidence,
                Weight = 1.4,
                Reason = "WebSocket upgrade missing Sec-WebSocket-Key (required by RFC 6455)",
                BotType = BotType.Unknown.ToString()
            });
        }

        if (hasVersion)
        {
            var versionStr = version.ToString().Trim();
            sink.Raise($"{SignalKeys.TransportWebSocketVersion}:{versionStr}", sessionId);

            if (versionStr != "13")
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = InvalidWsVersionConfidence,
                    Weight = 1.2,
                    Reason = $"Invalid Sec-WebSocket-Version: {versionStr} (RFC 6455 requires 13)",
                    BotType = BotType.Unknown.ToString()
                });
            }
        }
        else if (hasKey)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = MissingWsHeadersConfidence,
                Weight = 1.2,
                Reason = "WebSocket upgrade missing Sec-WebSocket-Version",
                BotType = BotType.Unknown.ToString()
            });
        }

        sink.Raise($"{SignalKeys.TransportWebSocketOrigin}:{(hasOrigin ? "true" : "false")}", sessionId);
        if (!hasOrigin)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = MissingWsOriginConfidence,
                Weight = 1.0,
                Reason = "WebSocket upgrade missing Origin header (browsers always include it)",
                BotType = BotType.Unknown.ToString()
            });
        }

        if (hasKey)
        {
            var keyValue = request.Headers["Sec-WebSocket-Key"].ToString().Trim();
            if (!IsValidWebSocketKey(keyValue))
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = InvalidWsVersionConfidence,
                    Weight = 1.3,
                    Reason = "Invalid Sec-WebSocket-Key format (must be 16-byte base64)",
                    BotType = BotType.Unknown.ToString()
                });
            }
        }

        var originMatches = false;
        if (hasOrigin && request.Headers.TryGetValue("Host", out var host))
        {
            var originValue = request.Headers["Origin"].ToString().Trim();
            var hostValue = host.ToString().Trim();
            if (!string.IsNullOrEmpty(originValue) && !string.IsNullOrEmpty(hostValue))
            {
                if (Uri.TryCreate(originValue, UriKind.Absolute, out var originUri))
                {
                    var originHost = originUri.Host;
                    var hostOnly = hostValue.Contains(':') ? hostValue.Split(':')[0] : hostValue;
                    if (!string.Equals(originHost, hostOnly, StringComparison.OrdinalIgnoreCase))
                    {
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = Category,
                            ConfidenceDelta = WsOriginMismatchConfidence,
                            Weight = 1.4,
                            Reason = $"WebSocket Origin/Host mismatch: Origin={originHost}, Host={hostOnly} (potential CSWSH)",
                            BotType = BotType.Unknown.ToString()
                        });
                    }
                    else
                    {
                        originMatches = true;
                    }
                }
            }
        }

        if (contributions.Count == 0 && hasKey && hasVersion && version.ToString().Trim() == "13"
            && hasOrigin && originMatches)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = -0.2,
                Weight = 1.3,
                Reason = "RFC 6455 compliant WebSocket upgrade (valid key, version 13, matching origin)"
            });
        }
    }

    private void AnalyzeGrpc(SignalSink sink, string sessionId, HttpRequest request, string protocol, List<DetectionContribution> contributions)
    {
        if (protocol == "grpc")
        {
            var hasTe = request.Headers.TryGetValue("te", out var teValue)
                        && teValue.ToString().Contains("trailers", StringComparison.OrdinalIgnoreCase);

            if (!hasTe)
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = GrpcMissingTeConfidence,
                    Weight = 1.2,
                    Reason = "gRPC request missing te: trailers header (required by gRPC spec)",
                    BotType = BotType.Unknown.ToString()
                });
            }

            var ua = request.Headers.UserAgent.ToString();
            if (!string.IsNullOrEmpty(ua) && BrowserUaFamilies.Any(f =>
                    ua.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = GrpcBrowserUaConfidence,
                    Weight = 1.3,
                    Reason = "Raw gRPC request from browser User-Agent (browsers use grpc-web, not raw gRPC)",
                    BotType = BotType.Scraper.ToString()
                });
            }
        }

        var path = request.Path.Value ?? string.Empty;
        if (path.Contains("grpc.reflection", StringComparison.OrdinalIgnoreCase))
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = GrpcReflectionConfidence,
                Weight = 1.2,
                Reason = "gRPC reflection endpoint probed - service discovery reconnaissance",
                BotType = BotType.Scraper.ToString()
            });
        }
    }

    private void AnalyzeGraphql(SignalSink sink, string sessionId, HttpRequest request, List<DetectionContribution> contributions)
    {
        var queryString = request.QueryString.Value ?? string.Empty;
        var hasIntrospection = IntrospectionPattern().IsMatch(queryString);

        sink.Raise($"{SignalKeys.TransportGraphqlIntrospection}:{(hasIntrospection ? "true" : "false")}", sessionId);

        if (hasIntrospection)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = GraphqlIntrospectionConfidence,
                Weight = 1.1,
                Reason = "GraphQL introspection query detected (__schema/__type) - common bot reconnaissance",
                BotType = BotType.Unknown.ToString()
            });
        }

        var hasBatchHint = request.Headers.TryGetValue("X-GraphQL-Batch", out _)
                           || queryString.Contains("batch", StringComparison.OrdinalIgnoreCase);

        sink.Raise($"{SignalKeys.TransportGraphqlBatch}:{(hasBatchHint ? "true" : "false")}", sessionId);

        if (hasBatchHint)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = GraphqlBatchConfidence,
                Weight = 0.8,
                Reason = "GraphQL batch query indicator detected - legitimate but high-abuse pattern",
                BotType = BotType.Unknown.ToString()
            });
        }

        if (HttpMethods.IsGet(request.Method) && MutationSubscriptionPattern().IsMatch(queryString))
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = GraphqlGetMutationConfidence,
                Weight = 1.2,
                Reason = "GraphQL mutation/subscription via GET request (spec violation - GET is read-only)",
                BotType = BotType.Unknown.ToString()
            });
        }
    }

    private void AnalyzeSse(SignalSink sink, string sessionId, HttpRequest request, List<DetectionContribution> contributions)
    {
        var hasCacheControl = request.Headers.TryGetValue("Cache-Control", out var cacheControl)
                              && cacheControl.ToString().Contains("no-cache", StringComparison.OrdinalIgnoreCase);

        if (!hasCacheControl)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = SseMissingCacheControlConfidence,
                Weight = 0.8,
                Reason = "SSE request missing Cache-Control: no-cache (browsers always include this per WHATWG spec)",
                BotType = BotType.Unknown.ToString()
            });
        }

        if (request.Headers.TryGetValue("Last-Event-ID", out var lastEventId))
        {
            var eventIdValue = lastEventId.ToString().Trim();
            sink.Raise($"{SignalKeys.TransportSseReconnect}:true", sessionId);
            sink.Raise($"{SignalKeys.TransportSseLastEventId}:{eventIdValue}", sessionId);

            if (eventIdValue is "0" or "-1")
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = SseHistoryReplayConfidence,
                    Weight = 1.1,
                    Reason = $"SSE Last-Event-ID: {eventIdValue} - potential history replay attempt",
                    BotType = BotType.Unknown.ToString()
                });
            }
        }
    }

    private static bool IsWebSocketUpgrade(HttpRequest request)
    {
        return request.Headers.TryGetValue("Upgrade", out var upgrade)
               && upgrade.ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase)
               && request.Headers.TryGetValue("Connection", out var connection)
               && connection.ToString().Contains("Upgrade", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGrpc(HttpRequest request, out string contentType)
    {
        contentType = request.ContentType ?? string.Empty;
        return contentType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGraphql(HttpRequest request)
    {
        var path = request.Path.Value ?? string.Empty;
        if (path.EndsWith("/graphql", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/gql", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/graphql/", StringComparison.OrdinalIgnoreCase))
            return true;

        var contentType = request.ContentType ?? string.Empty;
        return contentType.StartsWith("application/graphql", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSse(HttpRequest request)
    {
        return request.Headers.TryGetValue("Accept", out var accept)
               && accept.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidWebSocketKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length != 24) return false;
        try
        {
            var bytes = Convert.FromBase64String(key);
            return bytes.Length == 16;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSignalRNegotiate(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method)) return false;
        var path = request.Path.Value ?? string.Empty;
        if (!path.EndsWith("/negotiate", StringComparison.OrdinalIgnoreCase)) return false;
        var query = request.QueryString.Value ?? string.Empty;
        return query.Contains("negotiateVersion", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSignalRConnect(HttpRequest request, string protocol)
    {
        var query = request.QueryString.Value ?? string.Empty;
        if (!query.Contains("id=", StringComparison.OrdinalIgnoreCase)) return false;
        return protocol is "websocket" or "sse" or "http";
    }

    private static string ClassifySignalRType(HttpRequest request, string protocol)
    {
        var path = request.Path.Value ?? string.Empty;
        if (path.EndsWith("/negotiate", StringComparison.OrdinalIgnoreCase)) return "negotiate";
        return protocol switch
        {
            "websocket" => "websocket",
            "sse" => "sse",
            _ => "longpolling"
        };
    }

    [GeneratedRegex(@"__schema|__type", RegexOptions.IgnoreCase)]
    private static partial Regex IntrospectionPattern();

    [GeneratedRegex(@"\bmutation\b|\bsubscription\b", RegexOptions.IgnoreCase)]
    private static partial Regex MutationSubscriptionPattern();
}
