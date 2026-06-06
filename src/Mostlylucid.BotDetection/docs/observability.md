# Observability

StyloBot ships structured logs, metrics, and OpenTelemetry export through the
`Mostlylucid.BotDetection.Observability` package. The detection pipeline is the
data source; your existing observability stack is the destination.

## What you get

| Surface | What it carries | How to consume |
|---|---|---|
| **DetectionEvent log line** | One structured `ILogger` entry per completed detection with `StyloBot_*` properties | Any backend Serilog or `Microsoft.Extensions.Logging` writes to (Datadog, Seq, Splunk, Loki, CloudWatch) |
| **Blackboard signal log stream** | Every global signal raised by detectors as `ILogger<StyloBotSignalCategory>` calls | Same as above, filterable by category |
| **Enricher** | `StyloBot_*` properties on every host log line emitted during a request | Serilog `Enrich.WithStyloBot(services)` |
| **Metrics** | `Mostlylucid.BotDetection` and `Mostlylucid.BotDetection.Signals` meters | OTLP exporter or `/metrics` Prometheus endpoint on the Gateway |
| **Traces** | `Mostlylucid.BotDetection` ActivitySource, `BotDetection.Detect` activity per request | OTLP exporter |

## Quick start

```csharp
builder.Services.AddBotDetection();
builder.Services.AddStyloBotObservability(builder.Configuration);

builder.Host.UseSerilog((ctx, services, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.WithStyloBot(services));
```

The Serilog wiring is only needed when you want host-emitted log lines (your
own controllers, middleware, etc.) tagged with the current request's verdict.
`AddStyloBotObservability` alone is enough to emit the DetectionEvent line and
the signal stream through the host's `ILogger` pipeline.

## Configuration

```json
{
  "BotDetection": {
    "Observability": {
      "PublishDetectionEventsToSerilog": true,
      "SignalLog": {
        "Enabled": true,
        "IncludePrefixes": [],
        "ExcludePrefixes": [ "trace.", "debug." ]
      },
      "OpenTelemetry": {
        "EnableTracing": true,
        "EnableMetrics": true,
        "EnableLogs": true,
        "OtlpEndpoint": "http://localhost:4317",
        "ServiceName": "stylobot"
      }
    }
  }
}
```

| Key | Default | Notes |
|---|---|---|
| `PublishDetectionEventsToSerilog` | `true` | Replaces the no-op `IDetectionEventPublisher` with the Serilog-pipeline publisher. Set false to keep your own publisher (e.g. commercial Redis bridge). |
| `SignalLog.Enabled` | `true` | Hosts the `BlackboardSignalLogBridge` that subscribes to every blackboard signal and forwards as `ILogger<StyloBotSignalCategory>`. |
| `SignalLog.IncludePrefixes` | `[]` | Only signals whose key starts with one of these prefixes are emitted. Empty means "all signals (subject to ExcludePrefixes)". |
| `SignalLog.ExcludePrefixes` | `[]` | Signals matching any prefix are dropped. Use to silence high-cardinality noise (`trace.`, `debug.`). |
| `OpenTelemetry.Enable*` | `true` | Toggles each OTel signal independently. |
| `OpenTelemetry.OtlpEndpoint` | `null` | Null defers to the OTel SDK default (`http://localhost:4317`). |
| `OpenTelemetry.ServiceName` | `"stylobot"` | Resource attribute on every emitted log/metric/trace. |

## Backends

| Backend | Wiring |
|---|---|
| Seq / Datadog / Splunk / Loki | Add the appropriate Serilog sink to your host. StyloBot's events flow through it automatically. |
| Prometheus | Already mapped at `/metrics` on the Gateway. No change. |
| OTLP (Collector / Tempo / Mimir / Grafana Agent) | Set `OtlpEndpoint`. Logs, metrics, and traces all export. |

## Properties cheatsheet

The DetectionEvent log line carries these structured properties:

`StyloBot_Signature`, `StyloBot_IsBot`, `StyloBot_Probability`, `StyloBot_Confidence`,
`StyloBot_RiskBand`, `StyloBot_ThreatBand`, `StyloBot_Action`, `StyloBot_BotName`,
`StyloBot_BotType`, `StyloBot_CountryCode`, `StyloBot_Path`, `StyloBot_Method`,
`StyloBot_StatusCode`, `StyloBot_ProcessingTimeMs`, `StyloBot_RequestId`,
`StyloBot_GatewayId`.

The host-side Serilog enricher tags every host log line emitted during a
request with: `StyloBot_Signature`, `StyloBot_IsBot`, `StyloBot_BotProbability`,
`StyloBot_BotType`, `StyloBot_BotName`, `StyloBot_PolicyName`, `StyloBot_Action`.

`StyloBot_RiskBand` and `StyloBot_ThreatBand` are emitted by the DetectionEvent
log line but not yet by the host-side enricher (the verdict band lives on the
blackboard rather than on `HttpContext.Items`). Future enhancement.

## Level mapping

The DetectionEvent publisher derives log level from the chosen action policy:

| `Action` | Log level |
|---|---|
| `block` | Warning |
| `challenge`, `throttle-tools`, `throttle-stealth`, `throttle-status`, `redirect-honeypot` | Information |
| anything else, bot verdict | Information |
| anything else, human verdict | Debug |

The blackboard signal stream emits at Information by default. Filter via
`Logging:LogLevel:Mostlylucid.BotDetection.Observability.Signals.StyloBotSignalCategory`.

## Example log line

```
StyloBot detection: signature=tBfv2ecoUE0v1HPpUgmCpw isBot=True prob=1 conf=1
  risk=VeryHigh threat=Critical action=Allow botName=curl botType=Tool
  country= path=/api method=GET status=200 elapsedMs=0
  requestId=0HNM3N86FM21S:00000001 gateway=stylobot-edge-1
```

In a Serilog backend, query against `StyloBot_IsBot:true and StyloBot_RiskBand:VeryHigh`
or build a dashboard panel on `count by StyloBot_Action`.
