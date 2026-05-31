# Live demo sites — hosting plan

Each SDK has a runnable container demo. Hosted under its own
`x.stylo.bot` subdomain (set up out-of-band via Cloudflare tunnel), each
container listens on its own port locally and the outer proxy maps
`<subdomain>.stylo.bot → http://host:<port>`.

## Port assignments

| Subdomain (planned) | Container | Local port | Source |
|---|---|---|---|
| `express.stylo.bot` | `stylobot-sample-express` | **3001** | `sdk/node/samples/express-sample/` |
| `aspnet.stylo.bot` | `stylobot-demo` (existing) | **3002** | `src/Mostlylucid.BotDetection.Demo/Dockerfile` (currently exposes 8080 -- remap on the compose `ports:` line) |
| `caddy.stylo.bot` | `caddy` (integration test) | **3003** | `tests/integration/caddy-sidecar/docker-compose.yml` (currently 14080 -- remap on host-side) |

The container's internal port can stay at whatever the Dockerfile EXPOSEs;
only the host-side mapping needs to be the chosen subdomain port.

## What each demo proves

### `express.stylo.bot` -- the Node SDK

Express middleware (`@stylobot/node`) calling out to a stylobot gateway
via the public REST API (`POST /api/v1/detect`). The page renders
`<sb-summary>` + `<sb-topbots>` widgets server-side from the verdict, a
`window.__sb` global for client-side access, a `/protected` route that
gates browser vs python-requests, `/api/verdict` returning JSON, and
`/debug` returning the full detection result.

### `aspnet.stylo.bot` -- the ASP.NET UI tag helpers

The `Mostlylucid.BotDetection.Demo` project with `Views/Components/Gating.cshtml`
exercising every behaviour-aware tag helper from the
[Behaviour-Aware ASP.NET UI](https://mostlylucid.net/blog/behaviour-aware-ux)
post: `<sb-bot>`, `<sb-human>`, `<sb-gate>`, `<sb-risk>`, `<sb-honeypot>`,
`<sb-signal>`, `<sb-confidence>`, `<sb-badge>`, `<sb-summary>`. Renders
differently depending on the request's classification, which the visitor
can toggle by sending different UAs.

### `caddy.stylo.bot` -- the Caddy plugin

The `caddy-stylobot` Go plugin in front of a tiny upstream echo server.
Caddy queries the sidecar via gRPC and decorates the request with
`X-Bot-Detection-*` headers before forwarding upstream. The upstream
echoes its received headers so visitors can see the contract end-to-end.

## Run any one locally

### Express sample

```bash
docker build -f sdk/node/samples/express-sample/Dockerfile \
             -t stylobot-sample-express sdk/node
docker run -p 3001:3001 \
  -e STYLOBOT_URL=http://<gateway-host>:5080 \
  -e STYLOBOT_API_KEY=<your-key> \
  stylobot-sample-express
# Visit http://localhost:3001/
```

Or use the compose, which includes a pinned gateway:

```bash
docker compose -f sdk/node/samples/express-sample/docker-compose.yml up --build
# Visit http://localhost:3001/
```

### ASP.NET Demo

```bash
docker build -f src/Mostlylucid.BotDetection.Demo/Dockerfile -t stylobot-demo src
docker run -p 3002:8080 stylobot-demo
# Visit http://localhost:3002/SignatureDemo  (or the gating component)
```

### Caddy sample

```bash
cd tests/integration/caddy-sidecar
# Sidecar must be running first (see tests/integration/README.md)
docker compose up --build
# Caddy on http://localhost:14080 forwards to upstream + adds X-Bot-Detection-* headers
```

## Deployment shape under the website infra

For each subdomain:

1. **Container image** -- push to docker hub or build at deploy time on the
   host where the website lives.
2. **Compose entry** -- add the service alongside the website's stack
   (`mostlylucid.stylobot.website/docker-compose.local.yml`) with the
   port mapping from the table above and a shared `STYLOBOT_URL` env var
   pointing at the stylobot gateway service.
3. **Cloudflare tunnel routing** -- map `<sub>.stylo.bot` to the
   container's host-side port. Set up out-of-band per the project's
   "never touch Cloudflare anything" rule.
4. **API key** -- give each sample its own API key in the gateway's
   `BotDetection:ApiKeys` config so detection isn't throttled, and so
   per-sample telemetry can be tracked separately.

## Why one container per subdomain

When the multi-domain detection work lands (planned post-7.0), each
`x.stylo.bot` site will be classified independently by the gateway --
giving us a real-world multi-tenant test surface using our own demos as
the tenants. Each container running its own SDK + having its own
hostname is the right shape for that.
