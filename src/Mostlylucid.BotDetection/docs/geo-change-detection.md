# Geographic Change Detection

Detects geographic drift for visitor signatures and maintains per-country bot reputation scores. Bots rotating proxies exhibit unnaturally rapid country changes that real users rarely produce.

## How It Works

The detector runs in Wave 1 (priority 16) after GeoContributor has emitted `geo.country_code`. It performs two functions: country reputation tracking and per-signature geographic drift detection.

**Country reputation** is maintained via a `CountryReputationTracker` that records every detection result (bot probability and classification) per country. Countries with bot rates above the `high_bot_rate_threshold` (default 0.6) receive a moderate bot confidence contribution; countries above `very_high_bot_rate_threshold` (default 0.85) receive a stronger signal. Reputation scores decay over time so transient bot campaigns do not permanently taint a country.

**Geographic drift** tracks per-signature country history using in-memory state. When a signature's country changes, the detector records the change timestamp and increments the distinct country count. If country changes exceed the `rapid_drift_threshold` (default 3) within the `rapid_drift_window_minutes` (default 60 minutes), this is flagged as rapid country switching -- a strong indicator of proxy rotation. Regular country changes (2+ distinct countries) receive a moderate drift signal. The detector also emits the `geo.change.drift_detected` signal consumed by AccountTakeoverContributor for composite drift scoring.

## Signals Emitted

| Signal Key | Type | Description |
|---|---|---|
| `geo.change.checked` | boolean | Whether geo change analysis ran |
| `geo.change.distinct_countries` | integer | Number of distinct countries seen for this signature |
| `geo.change.total_changes` | integer | Total country changes for this signature |
| `geo.change.drift_detected` | boolean | Whether any country change occurred |
| `geo.change.previous_country` | string | Previous country code before change |
| `geo.change.rapid_drift` | boolean | Whether rapid country switching was detected |
| `geo.change.country_bot_rate` | number | Bot rate for the current country (0.0-1.0) |
| `geo.change.reputation_level` | string | `high` or `very_high` if elevated bot rate |

## Configuration

```json
{
  "BotDetection": {
    "Detectors": {
      "GeoChange": {
        "Parameters": {
          "drift_confidence": 0.6,
          "rapid_drift_confidence": 0.8,
          "rapid_drift_threshold": 3,
          "rapid_drift_window_minutes": 60,
          "high_bot_rate_threshold": 0.6,
          "very_high_bot_rate_threshold": 0.85
        }
      }
    }
  }
}
```

## Parameters

| Parameter | Default | Description |
|---|---|---|
| `drift_confidence` | 0.6 | Confidence for regular country change |
| `drift_weight` | 1.5 | Weight multiplier for regular drift |
| `rapid_drift_confidence` | 0.8 | Confidence for rapid country switching |
| `rapid_drift_weight` | 1.8 | Weight multiplier for rapid drift |
| `rapid_drift_threshold` | 3 | Country changes needed for rapid drift |
| `rapid_drift_window_minutes` | 60 | Time window for rapid drift detection |
| `country_reputation_confidence` | 0.3 | Confidence for elevated country bot rate |
| `country_reputation_weight` | 1.3 | Weight multiplier for country reputation |
| `high_bot_rate_threshold` | 0.6 | Country bot rate threshold for "high" |
| `very_high_bot_rate_threshold` | 0.85 | Country bot rate threshold for "very high" |
| `max_history_entries` | 10000 | Maximum tracked signature histories |
