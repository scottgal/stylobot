# Client-Side Detection in the CLI

## Scope

The CLI includes a browser test page for validating the client-side detector path, but it is now intentionally limited
to `demo` and `learning` mode.

It is not part of the production proxy surface.

## Current flow

1. Open `/test-client-side.html` in `demo` or `learning` mode.
2. The page calls `GET /api/bot-detection/test-status`.
3. The server returns:
   - the current server-side verdict for that request
   - the callback URL
   - a short-lived one-time callback token
4. The page runs browser capability checks locally.
5. The page posts the result to `POST /api/bot-detection/client-result` with `X-Stylobot-Test-Token`.
6. The server validates the token, checks the caller IP matches the issued token, computes the client-side score, and
   returns a demo-only comparison payload.

## Security model

The old implementation exposed production-facing problems:

- it leaked server verdicts in `X-Bot-*` response headers
- it accepted arbitrary client-echoed `serverDetection` data
- it published directly into the shared learning bus
- it accepted unbounded request bodies

The hardened implementation changes that:

- no client-visible `X-Bot-*` verdict headers are added to proxied responses
- upstream `Content-Security-Policy` and `X-Frame-Options` are preserved
- the test status endpoint is demo-only
- the callback endpoint is demo-only
- the callback requires a short-lived one-time token
- the callback token is bound to the originating client IP
- request bodies are capped at 16 KB
- the callback no longer publishes to the learning bus

## Endpoints

### `GET /test-client-side.html`

Available only in `demo` and `learning` mode.

Loads the embedded test page for exercising the browser checks and the demo callback flow.

### `GET /api/bot-detection/test-status`

Available only in `demo` and `learning` mode.

Returns JSON like:

```json
{
  "status": "ready",
  "serverDetection": {
    "isBot": false,
    "probability": 0.12,
    "botName": "ExampleBot"
  },
  "callbackUrl": "http://localhost:5080/api/bot-detection/client-result",
  "callbackToken": "short-lived-one-time-token"
}
```

### `POST /api/bot-detection/client-result`

Available only in `demo` and `learning` mode.

Requirements:

- `Content-Type: application/json`
- `X-Stylobot-Test-Token: <token>`
- body size <= 16 KB

Payload shape:

```json
{
  "timestamp": "2026-04-19T12:00:00Z",
  "clientChecks": {
    "hasCanvas": true,
    "hasWebGL": true,
    "hasAudioContext": true,
    "pluginCount": 3,
    "hardwareConcurrency": 8
  }
}
```

Response shape:

```json
{
  "status": "accepted",
  "message": "Demo client-side result processed",
  "serverDetection": {
    "isBot": false,
    "probability": 0.12
  },
  "clientEvaluation": {
    "botScore": 0.05,
    "mismatch": false,
    "hasCanvas": true,
    "hasWebGL": true,
    "hasAudioContext": true,
    "pluginCount": 3,
    "hardwareConcurrency": 8
  }
}
```

## Production behavior

In `production` mode:

- `/test-client-side.html` is not mapped
- `/api/bot-detection/test-status` is not mapped
- `/api/bot-detection/client-result` is not mapped
- Stylobot does not leak verdicts to the browser in response headers

If you want real client-side fingerprint collection in an application, use the library endpoints in
`Mostlylucid.BotDetection`, not the CLI demo page.
