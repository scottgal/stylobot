# Account Takeover Detection

Detects credential stuffing, brute force attacks, direct POST login bypasses, geographic velocity anomalies, and post-login behavioral drift. Uses decay-aware baselines to avoid false positives on returning users after long absences.

## How It Works

The detector runs in Wave 1 (priority 25) after UserAgent, IP, Geo, and Behavioral detectors have emitted their signals. It tracks per-signature login activity using an in-memory sliding window with configurable retention. Login and sensitive paths are defined in the YAML manifest and matched using span-based path comparison.

For login endpoints, the detector tracks POST requests to count login attempts and failed logins (from ResponseBehavior signals). It detects credential stuffing when failed logins exceed a threshold within a time window, brute force when total attempts exceed a higher threshold, and direct POST attacks when login POSTs arrive without a prior GET to the login page. For sensitive paths (password change, email change, 2FA settings), it flags rapid access after login as potential account takeover.

Behavioral drift is computed as a weighted composite of five dimensions: geographic drift (country change), fingerprint drift (correlation anomalies), timing drift (regularity score), path drift (diversity change from baseline), and velocity drift (burst detection). The baseline trust decays exponentially using a configurable half-life (default 14 days), so users returning after long absences receive reduced drift penalties. Zero-PII design means no credential content is ever inspected.

## Signals Emitted

| Signal Key | Type | Description |
|---|---|---|
| `ato.detected` | boolean | Whether any ATO pattern was detected |
| `ato.credential_stuffing` | boolean | Credential stuffing pattern found |
| `ato.brute_force` | boolean | Brute force pattern found |
| `ato.direct_post` | boolean | Direct POST without form load |
| `ato.rapid_credential_change` | boolean | Rapid sensitive action after login |
| `ato.geo_velocity` | boolean | Geographic velocity anomaly |
| `ato.drift_score` | number | Composite drift score (0.0-1.0) |
| `ato.drift.geo` | boolean | Geographic drift component active |
| `ato.drift.fingerprint` | boolean | Fingerprint drift component active |
| `ato.drift.timing` | number | Timing drift component value |
| `ato.drift.path` | number | Path drift component value |
| `ato.drift.velocity` | number | Velocity drift component value |
| `ato.login_failed_count` | integer | Number of failed logins in window |

## Configuration

```json
{
  "BotDetection": {
    "Detectors": {
      "AccountTakeoverContributor": {
        "Parameters": {
          "failed_login_threshold": 5,
          "brute_force_threshold": 10,
          "window_size_minutes": 30,
          "baseline_half_life_days": 14.0
        }
      }
    }
  }
}
```

## Parameters

| Parameter | Default | Description |
|---|---|---|
| `failed_login_threshold` | 5 | Failed logins to trigger credential stuffing |
| `failed_login_window_minutes` | 5 | Window for failed login counting |
| `brute_force_threshold` | 10 | Total attempts to trigger brute force |
| `brute_force_window_minutes` | 5 | Window for brute force counting |
| `rapid_change_threshold_seconds` | 60 | Max seconds between login and sensitive action |
| `window_size_minutes` | 30 | Sliding window for tracker retention |
| `max_tracked_signatures` | 10000 | Maximum tracked signatures in memory |
| `stuffing_confidence` | 0.90 | Confidence for credential stuffing |
| `brute_force_confidence` | 0.90 | Confidence for brute force |
| `direct_post_confidence` | 0.60 | Confidence for direct POST bypass |
| `rapid_change_confidence` | 0.85 | Confidence for rapid sensitive action |
| `geo_velocity_confidence` | 0.88 | Confidence for geographic velocity anomaly |
| `drift_weight_geo` | 0.30 | Weight for geographic drift dimension |
| `drift_weight_fingerprint` | 0.25 | Weight for fingerprint drift dimension |
| `drift_weight_timing` | 0.15 | Weight for timing drift dimension |
| `drift_weight_path` | 0.20 | Weight for path drift dimension |
| `drift_weight_velocity` | 0.10 | Weight for velocity drift dimension |
| `baseline_half_life_days` | 14.0 | Days for baseline trust to halve |
