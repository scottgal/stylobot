# OpenTelemetry Signal Adapter + Prometheus/Grafana Stack

## Goal

Automatically export StyloBot's 140+ detection signals as OpenTelemetry spans, metrics, and span events — without manually instrumenting individual detectors. Ship a Prometheus `/metrics` endpoint and a pre-built Grafana dashboard with world maps, score escalation visualization, and detector performance heatmaps.

## Principles

- **Zero per-detector changes**: The adapter reads from `AggregatedEvidence` after detection completes.
- **Signals drive everything**: The existing hierarchical signal keys (`geo.country_code`, `detection.useragent.confidence`, etc.) are the data source.
- **Opt-in**: `AddBotDetectionTelemetry()` enables OTel. Without it, no overhead.

## Architecture

```
HTTP Request
    |
BotDetectionMiddleware
    |-- Start Activity (span) via ActivitySource "Mostlylucid.BotDetection"
    |-- orchestrator.DetectWithPolicyAsync() -> AggregatedEvidence
    |-- BotDetectionInstrumentation.Record(activity, evidence, httpContext)
    |       |-- Span attributes: selected signals from allowlist (~30 keys)
    |       |-- Span events: per-contribution scoring journey
    |       |-- Metrics: counters, histograms via Meter
    |       |-- Geo metrics: country-tagged counters from geo.country_code
    |       |-- Escalation metrics: band transition tracking
    |-- Continue pipeline
```

## Components

### 1. `BotDetectionInstrumentation` (singleton)

Central recording class. Called once per request after detection. Reads `AggregatedEvidence` and emits:

- **Span attributes** from signal allowlist
- **Span events** per `DetectionContribution` (scoring journey)
- **Meter recordings** for counters and histograms

### 2. `BotDetectionMeter` (Meter: "Mostlylucid.BotDetection")

#### Counters

| Metric | Tags | Purpose |
|--------|------|---------|
| `stylobot_detections_total` | `risk_band`, `bot_type`, `action`, `country`, `is_bot` | Primary detection counter |
| `stylobot_detectors_runs_total` | `detector`, `outcome` | Detector execution tracking |
| `stylobot_attacks_total` | `category` | Attack type breakdown |
| `stylobot_verified_bots_total` | `bot_name`, `verified` | Verified vs spoofed bots |
| `stylobot_early_exits_total` | `verdict` | Fast-path effectiveness |
| `stylobot_score_escalation_total` | `from_band`, `to_band` | Band transition tracking |
| `stylobot_response_boost_total` | `from_band`, `to_band`, `boost_reason` | Fail2ban-style escalations |
| `stylobot_country_requests_total` | `country`, `is_bot` | Geo data for Grafana world map |

#### Histograms

| Metric | Tags | Purpose |
|--------|------|---------|
| `stylobot_detection_duration_seconds` | `policy`, `early_exit` | Detection latency distribution |
| `stylobot_detection_confidence` | `risk_band` | Confidence distribution |
| `stylobot_detection_bot_probability` | `bot_type` | Score distribution |
| `stylobot_detector_duration_seconds` | `detector`, `wave` | Per-detector latency |
| `stylobot_signals_per_request` | — | Signal density |
| `stylobot_detector_contribution` | `detector`, `direction` | Score delta per detector |
| `stylobot_detection_wave_score` | `wave` | Cumulative score per wave |

#### Gauges

| Metric | Tags | Purpose |
|--------|------|---------|
| `stylobot_active_visitors` | — | VisitorListCache count |
| `stylobot_cluster_count` | — | Active Leiden clusters |

### 3. Span Events (Scoring Journey)

Each `DetectionContribution` emits a span event:

```
Event name: "detector.contributed"
Attributes:
  detector = "UserAgentContributor"
  delta = 0.35
  weight = 2.0
  effective = 0.70
  cumulative_score = 0.70
  wave = 0
  reason = "Known scraping library: python-requests"
```

Viewable in Jaeger/Tempo as a timeline of how the bot probability evolved through the detection pipeline.

### 4. Signal Allowlist (Span Attributes)

Default ~30 high-value signals promoted to span attributes:

```
# Risk & Classification
risk.band, risk.score
ai.prediction, ai.confidence
heuristic.prediction, heuristic.confidence

# Identity
ua.bot_type, ua.bot_name, ua.family
verifiedbot.confirmed, verifiedbot.name, verifiedbot.spoofed

# Network
ip.is_datacenter, ip.provider, ip.asn_org
geo.country_code, geo.is_vpn, geo.is_tor, geo.is_hosting

# Behavioral
behavioral.rate_exceeded, behavioral.anomaly
cache.rapid_repeated, cache.behavior_anomaly

# Attack
attack.detected, attack.categories, attack.severity
ato.detected, ato.credential_stuffing

# Fingerprint
fingerprint.headless_score, fingerprint.integrity_score
tls.protocol, h2.client_type

# Cluster
cluster.id, cluster.member_count
```

Configurable via `BotDetectionTelemetryOptions.SignalAllowlist`.

## Registration

```csharp
// In Program.cs or Startup
builder.Services.AddBotDetection();
builder.Services.AddBotDetectionTelemetry(opts => {
    opts.EnableMetrics = true;        // Prometheus counters/histograms
    opts.EnableTracing = true;        // Spans + attributes
    opts.EnableScoreJourney = true;   // Per-contribution span events
});

// Wire up OTel SDK
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Mostlylucid.BotDetection"))
    .WithTracing(t => t.AddSource("Mostlylucid.BotDetection"));

// Expose /metrics for Prometheus scraping
app.MapPrometheusScrapingEndpoint();
```

## Docker Compose Stack

### New containers in `docker-compose.demo.yml`

```yaml
stylobot-prometheus:
  image: prom/prometheus:latest
  volumes:
    - ./demo/prometheus.yml:/etc/prometheus/prometheus.yml
  ports:
    - "9090:9090"

stylobot-grafana:
  image: grafana/grafana:latest
  environment:
    - GF_AUTH_ANONYMOUS_ENABLED=true
    - GF_AUTH_ANONYMOUS_ORG_ROLE=Viewer
    - GF_SECURITY_ADMIN_PASSWORD=stylobot
  volumes:
    - ./demo/grafana/datasources.yml:/etc/grafana/provisioning/datasources/datasources.yml
    - ./demo/grafana/dashboards/dashboard.yml:/etc/grafana/provisioning/dashboards/dashboard.yml
    - ./demo/grafana/dashboards/stylobot.json:/var/lib/grafana/dashboards/stylobot.json
  ports:
    - "3000:3000"
```

### Prometheus scrape config (`demo/prometheus.yml`)

```yaml
global:
  scrape_interval: 15s

scrape_configs:
  - job_name: 'stylobot-gateway'
    metrics_path: '/metrics'
    static_configs:
      - targets: ['gateway:8080']
  - job_name: 'stylobot-website'
    metrics_path: '/metrics'
    static_configs:
      - targets: ['website:8080']
```

### Caddy routing

```
localhost/grafana -> stylobot-grafana:3000
localhost/prometheus -> stylobot-prometheus:9090 (optional)
```

## Grafana Dashboard Layout (7 rows)

### Row 1 — Command Center (stat panels)
- Total Requests (sparkline)
- Bot Rate % (gauge, thresholds: green <30%, amber <70%, red >70%)
- Avg Detection Latency p50 (stat)
- Active Visitors (stat)
- Active Clusters (stat)
- Early Exit Rate % (stat)

### Row 2 — Threat Geography (full width)
- **Geomap panel**: `stylobot_country_requests_total{is_bot="true"}` with ISO alpha-2 `country` label
- Side bar gauge: Top 10 countries by bot volume

### Row 3 — Detection Timeline (time series)
- Stacked area: Detections by `risk_band` over time
- Overlaid lines: Detection latency p50/p95/p99

### Row 4 — Bot Taxonomy (pie + table + bars)
- Pie: Bot types (scraper, tool, verifiedBot, aiScraper, unknown)
- Table: Top signatures with hits, risk, action, country
- Bar: Actions taken (block, throttle, tarpit, challenge, logonly)

### Row 5 — Detector Performance (heatmap)
- Heatmap: Detector duration over time (rows = detectors, color = latency)
- Horizontal bars: Detector frequency
- Failure rate per detector

### Row 6 — Attack Intelligence
- Stacked bars: Attack categories over time
- Verified vs spoofed bots
- Early exit verdict breakdown

### Row 7 — Score Escalation Analysis
- Stacked bar: Per-detector weighted contribution (which detectors push scores most)
- Heatmap: Detector contribution magnitude over time
- Time series: Band escalation rate over time
- Bar gauge: "Tipping point" detectors (which detector most often pushes score over threshold)

## File Changes

| File | Change |
|------|--------|
| `Mostlylucid.BotDetection/Telemetry/BotDetectionInstrumentation.cs` | **New** — Central recording class |
| `Mostlylucid.BotDetection/Telemetry/BotDetectionMeter.cs` | **New** — Meter with all counters/histograms |
| `Mostlylucid.BotDetection/Telemetry/BotDetectionTelemetryOptions.cs` | **New** — Configuration model |
| `Mostlylucid.BotDetection/Telemetry/SignalAllowlist.cs` | **New** — Default signal-to-attribute mapping |
| `Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` | Modify — Add `AddBotDetectionTelemetry()` |
| `Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs` | Modify — Wire Activity + instrumentation recording |
| `Stylobot.Gateway/Program.cs` | Modify — Add OTel SDK + Prometheus endpoint |
| `mostlylucid.stylobot.website/src/Stylobot.Website/Program.cs` | Modify — Add OTel SDK + Prometheus endpoint |
| `docker-compose.demo.yml` | Modify — Add Prometheus + Grafana containers |
| `demo/prometheus.yml` | **New** — Scrape config |
| `demo/grafana/datasources.yml` | **New** — Prometheus datasource |
| `demo/grafana/dashboards/dashboard.yml` | **New** — Dashboard provisioner |
| `demo/grafana/dashboards/stylobot.json` | **New** — Full 7-row Grafana dashboard |
| `demo/Caddyfile` | Modify — Add /grafana and /prometheus routes |

## Verification

1. `dotnet build` — compiles clean
2. `docker compose -f docker-compose.demo.yml up -d` — all containers healthy
3. Generate traffic, then:
   - `curl localhost:8080/metrics` — Prometheus metrics visible
   - `localhost:3000` — Grafana dashboard with populated panels
   - World map shows country-level bot distribution
   - Detector heatmap shows per-detector latency
   - Score escalation panels show contribution flow
4. Optional: Add Jaeger/Tempo to see per-request scoring journey spans
