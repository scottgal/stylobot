# Admin endpoints

Operator endpoints for setup/observability without redeploying. Both are
**off by default** and only respond when an admin token is configured.

| Endpoint | Effect | Returns |
|---|---|---|
| `POST /stylobot/admin/restart` | Calls `IHostApplicationLifetime.StopApplication()` after flushing the response. The supervisor (Docker, systemd, launchctl) brings a fresh process up. | `202 {"status":"restarting"}` |
| `GET\|POST /stylobot/admin/learning/health` | Returns the identity calibration service's last decision + drift metrics. | `200` JSON |

FOSS has no runtime options-reload (`POST /admin/reload` was removed) -- config
changes need `restart` above, or a full redeploy. Hot-reload / live-apply is a
commercial-only capability (via `IConfigurationOverrideSource`, independent of
this endpoint).

## Enabling the endpoints

Add the `StyloBot:Dashboard:Admin` block to your operator-side `appsettings.json`
(or override via environment variables):

```json
{
  "StyloBot": {
    "Dashboard": {
      "Admin": {
        "Enabled": true,
        "Token": "REPLACE-WITH-LONG-RANDOM-STRING",
        "BasePath": "/stylobot/admin"
      }
    }
  }
}
```

Or via env:

```bash
STYLOBOT_ADMIN_ENABLED=true
STYLOBOT_ADMIN_TOKEN=<long-random-secret>
```

Pick a token that is at least 32 random bytes (e.g. `openssl rand -hex 32`). Rotate
it on any incident or operator handover.

## Auth

Every request must carry a bearer header:

```http
POST /stylobot/admin/restart HTTP/1.1
Authorization: Bearer <token>
```

The middleware compares the supplied token to the configured one in constant time.
Failure modes:

- **No `Enabled=true`** -> route falls through to the rest of the pipeline; the
  admin surface is not exposed at all.
- **`Enabled=true` but `Token` empty** -> `401` with body
  `{"status":"admin_disabled","message":"Set StyloBot:Dashboard:Admin:Token..."}`
  so the operator sees the exact config key to fix. No anonymous path.
- **Wrong or missing bearer** -> `401` with `WWW-Authenticate: Bearer
  realm="stylobot-admin"`. The attempt is logged at Warning level with the source
  IP and path.
- **Wrong HTTP method** -> `405` with `Allow: POST`.

## Reverse-proxy notes

`BasePath` defaults to `/stylobot/admin`, which sits under the dashboard base path
so whatever reverse-proxy rules already protect the dashboard cover the admin
endpoints as well. If the dashboard is behind a VPN, an IP allowlist, or mTLS
client certs, that same posture covers admin -- no extra rules needed.

For deployments that expose the dashboard publicly, keep admin pinned to a
narrow source range upstream of the gateway (Cloudflare Access, Tailscale ACL,
nginx `allow`/`deny`). The bearer token is your second factor, not your only one.

## What's coming

A follow-up will tighten the alerting story for admin attempts (currently just
Warning-level structured logs). Track it in the issue queue under `admin-audit`.
