# Liquid Data Contexts

This document lists the Liquid variables available in each widget template when using `SbSsrCoordinator.renderWidgets()` or `POST /_stylobot/partials/render`.

Each widget's context is built server-side from live data (event store, aggregate cache, visitor cache). Values reflect the current state of the StyloBot dashboard database.

---

## summary

Traffic overview statistics for the current database window.

| Variable | Type | Description |
|----------|------|-------------|
| `bot_requests` | integer | Total requests classified as bot |
| `human_requests` | integer | Total requests classified as human |
| `total_requests` | integer | All requests (bot + human + uncertain) |
| `uncertain_requests` | integer | Requests below the classification threshold |
| `bot_rate` | float (0-1) | Fraction of total requests that are bots |
| `bot_percentage` | float (0-100) | `bot_rate` expressed as a percentage |
| `unique_signatures` | integer | Number of distinct fingerprint signatures seen |
| `avg_processing_ms` | float | Average detection pipeline time in milliseconds |

### Example

```liquid
<div data-sb-widget="summary">
  <p>{{ bot_requests }} bots / {{ human_requests }} humans</p>
  <p>Bot rate: {{ bot_rate | times: 100 | round: 1 }}%</p>
  {% if bot_rate > 0.5 %}
    <p class="alert">High bot traffic</p>
  {% endif %}
  <p>{{ unique_signatures }} unique fingerprints</p>
  <p>Avg processing: {{ avg_processing_ms | round: 2 }}ms</p>
</div>
```

---

## topbots

The top 50 signatures ordered by hit count, filtered to bots.

The context exposes a single `bots` array.

### bots array item

| Variable | Type | Description |
|----------|------|-------------|
| `signature_id` | string | HMAC-SHA256 primary signature hash |
| `bot_name` | string or null | Deterministic bot name (e.g. `GPTBot/1.0`) |
| `bot_type` | string or null | Taxonomy type (e.g. `AiBot`, `Scraper`, `ExploitScanner`) |
| `risk_band` | string or null | Risk classification (`Low`, `Medium`, `High`, etc.) |
| `hit_count` | integer | Number of requests from this signature |
| `bot_probability` | float (0-1) | Most recent detection probability |
| `action` | string or null | Action taken (`Allow`, `Throttle`, `Challenge`, `Block`) |
| `country_code` | string or null | ISO 3166-1 alpha-2 country code |
| `last_seen` | string (ISO 8601) | Timestamp of last request from this signature |

### Example

```liquid
<div data-sb-widget="topbots">
  <h2>Top Bots</h2>
  <ul>
    {% for bot in bots %}
      <li>
        <strong>{{ bot.bot_name | default: "Unknown" }}</strong>
        ({{ bot.bot_type | default: "?" }})
        - {{ bot.hit_count }} hits
        - {{ bot.risk_band }}
        {% if bot.country_code %}from {{ bot.country_code }}{% endif %}
      </li>
    {% endfor %}
  </ul>
</div>
```

---

## visitors

All visitors from the in-memory visitor cache (up to 50), sorted by last seen.

The context exposes a `visitors` array and a `total_count` integer.

### visitors array item

| Variable | Type | Description |
|----------|------|-------------|
| `signature_id` | string | HMAC-SHA256 primary signature hash |
| `is_bot` | boolean | True if classified as bot |
| `risk_band` | string | Risk classification |
| `bot_name` | string or null | Deterministic bot name |
| `bot_type` | string or null | Taxonomy type |
| `hits` | integer | Number of requests from this visitor |
| `country_code` | string or null | ISO 3166-1 alpha-2 country code |
| `last_seen` | string (ISO 8601) | Timestamp of last request |

### Additional context variables

| Variable | Type | Description |
|----------|------|-------------|
| `total_count` | integer | Total number of visitors in cache |

### Example

```liquid
<div data-sb-widget="visitors">
  <p>{{ total_count }} visitors</p>
  <ul>
    {% for v in visitors %}
      <li>
        {{ v.signature_id | truncate: 12 }}
        - {{ v.is_bot | replace: "true", "Bot" | replace: "false", "Human" }}
        - {{ v.risk_band }}
        - {{ v.hits }} hits
      </li>
    {% endfor %}
  </ul>
</div>
```

---

## countries

Country-level aggregations, up to 50 entries ordered by total request count.

The context exposes a `countries` array.

### countries array item

| Variable | Type | Description |
|----------|------|-------------|
| `country_code` | string | ISO 3166-1 alpha-2 code (e.g. `US`, `CN`, `DE`) |
| `country_name` | string or null | Full country name when available |
| `total_count` | integer | Total requests from this country |
| `bot_count` | integer | Bot requests from this country |
| `human_count` | integer | Human requests (`total_count - bot_count`) |
| `bot_rate` | float (0-1) | Fraction of requests that are bots |

### Example

```liquid
<div data-sb-widget="countries">
  <table>
    <thead><tr><th>Country</th><th>Total</th><th>Bots</th><th>Bot Rate</th></tr></thead>
    <tbody>
      {% for c in countries %}
        <tr>
          <td>{{ c.country_name | default: c.country_code }}</td>
          <td>{{ c.total_count }}</td>
          <td>{{ c.bot_count }}</td>
          <td>{{ c.bot_rate | times: 100 | round: 1 }}%</td>
        </tr>
      {% endfor %}
    </tbody>
  </table>
</div>
```

---

## endpoints

Endpoint-level aggregations (method + path pairs), up to 50 entries.

The context exposes an `endpoints` array.

### endpoints array item

| Variable | Type | Description |
|----------|------|-------------|
| `method` | string | HTTP method (`GET`, `POST`, etc.) |
| `path` | string | URL path |
| `total_count` | integer | Total requests to this endpoint |
| `bot_count` | integer | Bot requests |
| `human_count` | integer | Human requests |
| `bot_rate` | float (0-1) | Fraction that are bots |
| `unique_signatures` | integer | Distinct fingerprints seen |
| `avg_processing_ms` | float | Average detection time in ms |
| `avg_threat_score` | float (0-1) | Average threat probe score |
| `last_seen` | string (ISO 8601) | Timestamp of last request |

### Example

```liquid
<div data-sb-widget="endpoints">
  {% for e in endpoints %}
    <div>
      <code>{{ e.method }} {{ e.path }}</code>
      - {{ e.total_count }} requests
      ({{ e.bot_rate | times: 100 | round: 0 }}% bots)
      {% if e.avg_threat_score > 0.5 %}
        <span class="threat">High threat</span>
      {% endif %}
    </div>
  {% endfor %}
</div>
```

---

## useragents

User-agent family aggregations from the aggregate cache, up to 25 entries.

The context exposes a `useragents` array.

### useragents array item

| Variable | Type | Description |
|----------|------|-------------|
| `family` | string | User-agent family name (e.g. `Chrome`, `Googlebot`, `GPTBot`) |
| `category` | string | Category (`Browser`, `Bot`, `AI`, `Tool`, `Scraper`, `MonitoringBot`) |
| `total_count` | integer | Total requests from this UA family |
| `bot_count` | integer | Bot requests |
| `human_count` | integer | Human requests |
| `bot_rate` | float (0-1) | Fraction that are bots |
| `avg_confidence` | float (0-1) | Average detector confidence |
| `last_seen` | string (ISO 8601) | Timestamp of last request |

### Example

```liquid
<div data-sb-widget="useragents">
  <h2>User Agents</h2>
  {% for ua in useragents %}
    <div class="{{ ua.category | downcase }}">
      <strong>{{ ua.family }}</strong> ({{ ua.category }})
      - {{ ua.total_count }} requests
      - {{ ua.bot_rate | times: 100 | round: 0 }}% bot
      - conf: {{ ua.avg_confidence | round: 2 }}
    </div>
  {% endfor %}
</div>
```

---

## threats

Recent threat entries (CVE probes, honeypot engagements, high threat-score detections), up to 50 entries.

The context exposes a `threats` array.

### threats array item

| Variable | Type | Description |
|----------|------|-------------|
| `signature` | string | HMAC-SHA256 primary signature hash of the attacker |
| `path` | string | URL path that was probed |
| `cve_id` | string or null | CVE identifier if a known vulnerability was targeted |
| `cve_severity` | string or null | CVE severity (`Low`, `Medium`, `High`, `Critical`) |
| `threat_score` | float (0-1) | Threat severity score |
| `threat_band` | string or null | `None`, `Low`, `Elevated`, `High`, or `Critical` |
| `bot_name` | string or null | Deterministic bot name |
| `bot_type` | string or null | Taxonomy type |
| `bot_probability` | float (0-1) | Detection probability |
| `country_code` | string or null | ISO 3166-1 alpha-2 country code |
| `in_honeypot` | boolean | True if an active honeypot engagement |
| `timestamp` | string (ISO 8601) | When the probe was detected |

### Example

```liquid
<div data-sb-widget="threats">
  <h2>Recent Threats</h2>
  {% if threats.size == 0 %}
    <p>No threats detected.</p>
  {% else %}
    <ul>
      {% for t in threats %}
        <li>
          <code>{{ t.path }}</code>
          {% if t.cve_id %}({{ t.cve_id }}){% endif %}
          - score: {{ t.threat_score | round: 2 }}
          {% if t.in_honeypot %}<em>honeypot</em>{% endif %}
          - {{ t.timestamp }}
        </li>
      {% endfor %}
    </ul>
  {% endif %}
</div>
```

---

## Notes

- All `timestamp` and `last_seen` values are ISO 8601 UTC strings (e.g. `2025-05-02T14:32:00.000Z`). Use the `| date` Liquid filter to reformat.
- `null` values in arrays become empty strings in Liquid. Use `| default: "fallback"` where you want a fallback.
- The `bot_rate` values are 0-1 floats. Multiply by 100 with `| times: 100` before displaying as percentages.
- All signature IDs are HMAC-SHA256 hashes. No raw IP addresses or user-agent strings are included in contexts (zero-PII design).
- Contexts are built fresh per request from live data. No stale caching from the Liquid layer; the .NET server-side caches are already 2-second TTL.
