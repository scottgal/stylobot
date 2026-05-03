# StyloBot CLI Performance And Reliability Review

Date: 2026-04-18

Scope: `Mostlylucid.BotDetection.Console`

## Summary

The CLI client is an ASP.NET Core/YARP gateway packaged as a single-file Native AOT executable. The main issues found were not raw throughput bottlenecks, but operational reliability and configuration drift:

- broken named CLI flags despite being documented
- incomplete telemetry wiring compared with `Stylobot.Gateway`
- demo and production policies not matching their documented detector behavior
- forwarded-header trust configured too loosely
- inconsistent configuration precedence between code, JSON, and environment variables

These have now been corrected.

Note: the current detector set is 49 detectors total, including the `Threat Intelligence` detector.

## Changes Made

### 1. CLI argument handling

`Program.cs` now supports both of these invocation styles correctly:

```bash
stylobot 5080 http://localhost:8080
stylobot --port 5080 --upstream http://localhost:8080
```

It also now validates:

- `--port` must be `1-65535`
- `--upstream` must be a valid absolute `http://` or `https://` URL

Precedence is now:

1. CLI named flags
2. positional args
3. environment variables
4. built-in defaults

Environment variables:

- `PORT`
- `UPSTREAM`
- `DEFAULT_UPSTREAM` as fallback compatibility
- `MODE`
- `STYLOBOT_POLICY`

### 2. Configuration loading

The CLI now loads configuration consistently in both logging bootstrap and runtime host setup:

1. `appsettings.json` if present
2. `appsettings.{mode}.json` if present
3. optional `--config <path>`
4. environment variables
5. `STYLOBOT_`-prefixed environment variables

This removes the earlier split-brain behavior where logging and the application host did not read config the same way.

### 3. Telemetry and observability

The CLI now uses the same standard bot-detection telemetry path as the gateway:

- `AddBotDetectionTelemetry()`
- OpenTelemetry metrics
- OpenTelemetry tracing
- Prometheus scraping endpoint at `/metrics` with loopback-only exposure by default

Meters/sources wired:

- `Mostlylucid.BotDetection`
- `Mostlylucid.BotDetection.Signals`
- `Mostlylucid.BotDetection` activity source

Telemetry defaults are now explicitly enabled in shipped config:

```json
"BotDetection": {
  "Telemetry": {
    "EnableMetrics": true,
    "EnableTracing": true,
    "EnableScoreJourney": true
  }
}
```

### 4. Detector policy corrections

#### Demo mode

The shipped demo config previously claimed to run all detectors but only listed a subset. It now behaves like a real full-pipeline demo:

- `FastPath: []`
- `UseFastPath: false`
- `ForceSlowPath: true`
- `EscalateToAi: true`
- `BypassTriggerConditions: true`

That means demo mode now runs the full registered detector set instead of a partial fast-path sample.

#### Production mode

The shipped production config previously set AI escalation but did not include `Llm` in `AiPath`, so `--llm` could not fully activate the intended path. It now includes:

```json
"AiPath": ["Llm", "HeuristicLate"]
```

This makes `--llm <url>` operationally meaningful.

### 5. Forwarded-header trust model

The earlier CLI behavior trusted forwarded headers from any sender. That is unsafe on an internet-facing port because a client can spoof `X-Forwarded-For`.

The CLI now follows an explicit trust model:

- by default, trust only configured proxies/networks
- if `--tunnel` is enabled, trust loopback proxies (`127.0.0.1`, `::1`) for local `cloudflared`
- `TrustAllForwardedProxies` remains available, but only as an explicit unsafe override

Configuration:

```json
"Network": {
  "TrustAllForwardedProxies": false,
  "KnownNetworks": "10.0.0.0/8,192.168.0.0/16",
  "KnownProxies": "127.0.0.1,::1"
}
```

Environment variables:

- `TRUST_ALL_FORWARDED_PROXIES`
- `KNOWN_NETWORKS`
- `KNOWN_PROXIES`

## Performance Notes

### Good

- single binary deployment
- Native AOT enabled
- trimming enabled
- single-file publish enabled
- minimal console output in non-verbose mode
- live table avoids high-volume stdout logging
- reverse proxy config is in-memory and simple
- health endpoint is lightweight
- response learning endpoint is mapped before YARP

### Acceptable but worth watching

- Prometheus exporter adds useful visibility with modest overhead; this is a good tradeoff
- file log flush interval is set to one second, which is reasonable for durability but not free
- background tunnel stderr parsing runs continuously when `--tunnel` is enabled
- JSON callback parsing uses `JsonDocument`; acceptable for low-volume callback traffic

### Not changed in this pass

- forwarded-header trust still depends on deployment correctness; misconfigured proxy lists will break source IP extraction
- `ForwardLimit = 1` is conservative and correct for the common case, but multi-hop proxy chains need explicit design
- `LiveDetectionTableService` still has an unused field warning
- the broader solution still has unrelated warnings outside the CLI path

## Operational Guidance

### Recommended local/dev run

```bash
stylobot --port 5080 --upstream http://localhost:3000
```

### Recommended production behind a local reverse proxy

```bash
export KNOWN_PROXIES="127.0.0.1,::1"
stylobot --mode production --port 8080 --upstream http://backend:8080
```

### Recommended production behind a private proxy subnet

```bash
export KNOWN_NETWORKS="10.0.0.0/8,192.168.0.0/16"
stylobot --mode production --port 8080 --upstream http://backend:8080
```

### Metrics

Prometheus scrape endpoint:

```text
GET /metrics
```

Default exposure: loopback-only unless `Telemetry:AllowPublicMetricsEndpoint` or
`STYLOBOT_ALLOW_PUBLIC_METRICS=true` is set deliberately.

Health endpoint:

```text
GET /health
```

Response body is now minimal:

```json
{"status":"healthy"}
```

## Validation Performed

- `dotnet build Mostlylucid.BotDetection.Console/Mostlylucid.BotDetection.Console.csproj`
- CLI parser smoke check with invalid named port argument

## Remaining Risks

1. If operators enable `TrustAllForwardedProxies`, IP spoofing risk returns immediately.
2. If deployments use multiple proxy hops, `ForwardLimit = 1` may be too strict.
3. The console README still contains some older narrative around detector composition and should be kept in sync with shipped config over time.
