# Transport Protocol Detection

Detects WebSocket, gRPC, GraphQL, and Server-Sent Events (SSE) from HTTP request headers and validates protocol-specific RFC compliance. Bots frequently get protocol headers wrong because they copy minimal examples or skip required fields.

## How It Works

The detector runs in Wave 0 (priority 5) with no dependencies. It inspects HTTP headers to identify the transport protocol before any upgrade occurs. All these protocols start as regular HTTP requests that flow through middleware before protocol upgrade, making header-level validation possible.

For **WebSocket**, it validates RFC 6455 compliance: Sec-WebSocket-Key must be present (16-byte base64), Sec-WebSocket-Version must be 13, Origin must be present (browsers always send it), and Origin/Host must match (Cross-Site WebSocket Hijacking detection). For **gRPC**, it checks for the required `te: trailers` header, flags browser User-Agents making raw gRPC calls (browsers use grpc-web instead), and detects gRPC reflection endpoint probing. For **GraphQL**, it detects introspection queries (`__schema`/`__type`) in query strings, batch query indicators, and GET requests with mutation/subscription operations (a spec violation). For **SSE**, it checks for the WHATWG-required `Cache-Control: no-cache` header and detects history replay attempts via `Last-Event-ID: 0` or `-1`.

The detector emits the identified protocol as a signal (`transport.protocol`) for use by downstream detectors. It does not read request bodies or inspect post-upgrade frames.

## Two-Level Transport Classification

Beyond protocol identification, TransportProtocolContributor emits a two-level classification that downstream detectors use to suppress false positives on streaming traffic:

- **Transport class** (`transport.transport_class`): Physical transport — `http`, `websocket`, or `sse`
- **Protocol class** (`transport.protocol_class`): Application protocol — `signalr`, `grpc`, `api`, or `unknown`
- **Streaming flag** (`transport.is_streaming`): Composite signal — `true` for WebSocket, SSE, or any SignalR transport (including long-polling)

### SignalR Detection

SignalR connections are detected generically from protocol-spec patterns (no site-specific paths):

- **Negotiate**: POST + path ends `/negotiate` + `negotiateVersion` query parameter
- **Connect**: Any transport with `id=` query parameter (connection token from negotiate)

The SignalR transport type is classified as `negotiate`, `websocket`, `sse`, or `longpolling`.

### SSE Reconnect Detection

When `Last-Event-ID` is present, the detector emits `transport.sse_reconnect = true` and `transport.sse_last_event_id` with the value. This allows StreamAbuseContributor to track reconnect frequency.

For the full stream-aware detection architecture, including how downstream detectors consume these signals and the StreamAbuseContributor, see [Stream Transport Detection](stream-transport-detection.md).

## Signals Emitted

| Signal Key | Type | Description |
|---|---|---|
| `transport.protocol` | string | Detected protocol: `http`, `websocket`, `grpc`, `grpc-web`, `graphql`, `sse` |
| `transport.is_upgrade` | boolean | Whether request is a WebSocket upgrade |
| `transport.websocket_version` | string | Sec-WebSocket-Version value |
| `transport.websocket_origin` | boolean | Whether Origin header is present on upgrade |
| `transport.grpc_content_type` | string | gRPC content-type value |
| `transport.graphql_introspection` | boolean | GraphQL introspection query detected |
| `transport.graphql_batch` | boolean | GraphQL batch query indicator found |
| `transport.sse` | boolean | SSE request detected |
| `transport.transport_class` | string | Transport class: `http`, `websocket`, `sse` |
| `transport.protocol_class` | string | Protocol class: `signalr`, `grpc`, `api`, `unknown` |
| `transport.is_signalr` | boolean | Whether request is part of a SignalR connection |
| `transport.signalr_type` | string | SignalR transport: `negotiate`, `websocket`, `sse`, `longpolling` |
| `transport.sse_reconnect` | boolean | SSE reconnect detected (Last-Event-ID present) |
| `transport.sse_last_event_id` | string | Last-Event-ID value from SSE reconnect |
| `transport.is_streaming` | boolean | Any streaming transport (WebSocket, SSE, or SignalR) |

## Configuration

```json
{
  "BotDetection": {
    "Detectors": {
      "TransportProtocolContributor": {
        "Parameters": {
          "missing_ws_headers_confidence": 0.6,
          "grpc_browser_ua_confidence": 0.5,
          "graphql_introspection_confidence": 0.3,
          "sse_missing_cache_control_confidence": 0.15
        }
      }
    }
  }
}
```

## Parameters

| Parameter | Default | Description |
|---|---|---|
| `missing_ws_headers_confidence` | 0.6 | Confidence for missing WebSocket required headers |
| `invalid_ws_version_confidence` | 0.5 | Confidence for wrong Sec-WebSocket-Version |
| `missing_ws_origin_confidence` | 0.3 | Confidence for missing Origin on upgrade |
| `ws_origin_mismatch_confidence` | 0.5 | Confidence for Origin/Host mismatch (CSWSH) |
| `grpc_browser_ua_confidence` | 0.5 | Confidence for raw gRPC from browser UA |
| `grpc_missing_te_confidence` | 0.4 | Confidence for missing `te: trailers` |
| `grpc_reflection_confidence` | 0.4 | Confidence for reflection endpoint probing |
| `graphql_introspection_confidence` | 0.3 | Confidence for introspection query |
| `graphql_batch_confidence` | 0.15 | Confidence for batch query indicator |
| `graphql_get_mutation_confidence` | 0.5 | Confidence for mutation via GET (spec violation) |
| `sse_missing_cache_control_confidence` | 0.15 | Confidence for missing Cache-Control on SSE |
| `sse_history_replay_confidence` | 0.3 | Confidence for Last-Event-ID history replay |
