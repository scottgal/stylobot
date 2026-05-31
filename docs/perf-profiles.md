# Perf profiles

The stylobot Console picks Kestrel limits, ThreadPool min-thread counts,
and HTTP/2 window sizes from a profile selected at startup. Profiles
trade memory against latency and concurrency headroom; defaults are
safe but conservative. Pick the profile that matches your traffic
shape.

## How to choose

| If your traffic is… | Pick |
|---|---|
| Mostly JSON API calls, short responses, no WebSockets | `api` |
| A public website with browser visitors + SignalR/WebSocket | `site` |
| Currently under active attack (DDoS, scraper flood) | `highrisk` |
| Unknown / mixed / just starting out | `balanced` (default) |

## How to set

Three ways, in precedence order:

```bash
# Env var (preferred for systemd / docker)
STYLOBOT_PROFILE=api stylobot 5080 http://localhost:8080

# CLI flag
stylobot 5080 http://localhost:8080 --profile site

# (Default if neither set: balanced)
```

The gateway logs the chosen profile at startup:

```
[Info] Kestrel profile 'api': threads=100/100 maxConn=20000 h2Streams=400
```

## Profile parameters

|  | balanced | api | site | highrisk |
|---|---|---|---|---|
| ThreadPool min worker / IOCP | 50 / 50 | 100 / 100 | 200 / 200 | 50 / 50 |
| Kestrel `MaxConcurrentConnections` | 10 000 | 20 000 | 10 000 | **2 000** |
| Kestrel `MaxConcurrentUpgradedConnections` (WebSocket / SignalR) | 1 000 | 100 | **10 000** | 100 |
| `MaxRequestBodySize` | 256 KB | 64 KB | 1 MB | 32 KB |
| `KeepAliveTimeout` | 30 s | 15 s | 120 s | **5 s** |
| `RequestHeadersTimeout` | 10 s | 5 s | 15 s | **3 s** |
| `MinRequestBody/ResponseDataRate` | 100 B/s | 200 B/s | 100 B/s | **1000 B/s** |
| `Http2.MaxStreamsPerConnection` | 200 | 400 | 400 | **100** |
| `Http2.InitialConnectionWindowSize` | 1 MB | 1 MB | 2 MB | 64 KB |
| Startup RSS (Win x64, estimate) | ~300 MB | ~400 MB | ~600 MB | ~250 MB |

Bold cells are the *defining* knobs for that profile.

> **RSS values are estimates** based on `min_threads × ~2 MB stack` on
> Windows. Only `balanced` and `site` have been measured directly
> (≈300 MB and ≈600 MB respectively). Memory reclaims when traffic
> drops; the gateway is happy at these baselines.

## What each profile is optimising for

### `api`

JSON-over-HTTPS, short request bodies, short responses, no long-lived
streams. We want to absorb large bursts of independent short-lived
TCP connections.

- `MaxConcurrentConnections` doubled (20 k) — APIs see more distinct
  callers, fewer kept-open connections per caller.
- `Http2.MaxStreamsPerConnection` doubled (400) — multiplex many
  parallel API calls per connection.
- `KeepAliveTimeout` halved (15 s) — recycle idle connections fast.
- `MaxRequestBodySize` 64 KB — APIs don't accept big uploads.
- `MaxConcurrentUpgradedConnections` only 100 — no WebSocket traffic.
- ThreadPool 100/100 — handlers are short, more worker threads keep
  median latency tight under burst.

### `site`

Mixed browser traffic: page loads + static assets + SignalR/WebSocket
push channels. Connections live a long time; user-perceived latency
matters more than raw throughput.

- `MaxConcurrentUpgradedConnections` raised to 10 k — every connected
  browser holds an open WebSocket.
- `KeepAliveTimeout` raised to 120 s — browsers reuse the same TCP
  connection across page navigations.
- `MaxRequestBodySize` 1 MB — accommodate form uploads, image
  attachments etc.
- `Http2.InitialConnectionWindowSize` 2 MB — fewer WINDOW_UPDATE
  round-trips when serving large pages.
- ThreadPool 200/200 — handlers may block on per-request DB or LLM
  work; pre-warmed thread pool absorbs spikes.

### `highrisk`

Under active attack. The optimisation flips: we want to *reject* fast
rather than serve fast. Every limit gets tighter.

- `MaxConcurrentConnections` **dropped to 2 000** — anything over this
  gets a TCP RST. Limits the attacker's queue depth.
- `KeepAliveTimeout` **5 s** — attackers love idle connections;
  recycle aggressively.
- `RequestHeadersTimeout` **3 s** — Slowloris dies fast.
- `MinRequestBodyDataRate` raised **10x to 1000 B/s** — slow-trickle
  uploads get killed immediately.
- `Http2.MaxStreamsPerConnection` **capped at 100** — denies HTTP/2
  stream-flooding amplification.
- `Http2.InitialConnectionWindowSize` 64 KB — back to spec default;
  small window forces attacker to keep sending WINDOW_UPDATE.
- ThreadPool unchanged (50/50) — under attack we don't want more
  threads to feed the attack; the fast-refuse path is non-allocating.
- `MaxRequestBodySize` 32 KB — POST floods get truncated.

### `balanced`

The safe default if you're not sure. Same numbers the 2026-05-31 soak
validated against the actual win-x64 AOT gateway: 150 K requests at
50 RPS over an hour, p50 11 ms, memory plateau 150-220 MB.

## Pairings with action policies

Profiles are **transport-layer** tuning. They don't change what
detection does. Pair with detection profiles via `DefaultPolicyName`:

| Kestrel profile | Recommended detection policy |
|---|---|
| `api` | `production` (DefaultActionPolicyName=block at risk≥0.7) + `RequireApiKey` on protected routes |
| `site` | `production` with `DefaultActionPolicyName=throttle-stealth` for borderline; serves a fake delay to bots while real users pass |
| `highrisk` | `production` with `DefaultActionPolicyName=block-hard` (or `redirect-honeypot`) |
| `balanced` | whatever your `appsettings.json` already has |

## Verifying

The startup log line confirms which profile loaded:

```
[Info] Kestrel profile 'api': threads=100/100 maxConn=20000 h2Streams=400
```

For end-to-end verification, set the env var, restart the gateway, and
run a load test against it. Compare numbers to
`docs/perf-pass-2026-05-31.md` (which used the balanced profile).

## What profiles do NOT fix

- The detection pipeline itself. p50 of ~11 ms is dominated by
  foundation contributors + proxy hop, not Kestrel. The per-profile
  knobs are mostly about HOW MUCH LOAD the gateway can absorb without
  falling over — not how fast each request completes.
- Upstream slowness. If your backend is slow, no Kestrel knob saves
  you; the request still waits on the proxy step.
- Detection action-policy delays. `throttle-stealth` deliberately
  imposes `Task.Delay(...)` per request. This is intentional. If you
  see high p99 latency for bot UAs, that's the throttle policy
  working as designed.
