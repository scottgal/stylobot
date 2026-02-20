# Inconsistency Detection

Detects mismatches between a request's claimed identity (User-Agent) and its actual behavior (headers, IP type, browser version). These cross-signal inconsistencies are strong indicators of bots that spoof browser User-Agents but fail to replicate the full set of headers a real browser sends.

## How It Works

The detector runs in Wave 1 (priority 50) after raw signal detectors have populated their signals. It requires the `detection.useragent` signal to be present before executing. The detector performs four independent checks against the request.

**Datacenter IP with browser UA**: Real browsers rarely originate from datacenter IP ranges (AWS, GCP, Azure, etc.). When a request claims to be Mozilla/Chrome/Firefox/Safari but comes from a datacenter IP (as identified by the IP detector), a high-confidence bot signal is emitted.

**Missing Accept-Language**: All real browsers send an `Accept-Language` header. Bots and scripts that spoof browser User-Agents frequently omit this header. Requests with a browser UA but no Accept-Language receive a moderate bot signal.

**Chrome without Client Hints**: Modern Chrome (v89+) sends `sec-ch-ua` Client Hints headers on every navigation request. Chrome UAs without these headers suggest a headless browser or spoofed UA string. Legitimate exceptions (service workers, fetch API) are excluded from this check.

**Outdated browser version**: Bots often use frozen or very old browser version strings. The detector checks the Chrome major version against a configurable minimum (default 90) and flags requests with outdated versions.

When no inconsistencies are found, the detector emits a human contribution (negative bot signal), providing a small confidence boost toward human classification.

## Signals Emitted

| Signal Key | Type | Description |
|---|---|---|
| `detection.inconsistency.confidence` | number | Bot confidence from inconsistency analysis |
| `detection.inconsistency.mismatch_count` | integer | Number of mismatches detected |
| `detection.inconsistency.mismatch_types` | array | Types of mismatches found |

## Configuration

```json
{
  "BotDetection": {
    "Detectors": {
      "InconsistencyContributor": {
        "Parameters": {
          "datacenter_browser_confidence": 0.7,
          "missing_language_confidence": 0.5,
          "missing_client_hints_confidence": 0.2,
          "outdated_browser_confidence": 0.3,
          "min_chrome_version": 90
        }
      }
    }
  }
}
```

## Parameters

| Parameter | Default | Description |
|---|---|---|
| `datacenter_browser_confidence` | 0.7 | Confidence for browser UA from datacenter IP |
| `missing_language_confidence` | 0.5 | Confidence for browser UA without Accept-Language |
| `missing_client_hints_confidence` | 0.2 | Confidence for Chrome UA without sec-ch-ua |
| `outdated_browser_confidence` | 0.3 | Confidence for outdated browser version |
| `min_chrome_version` | 90 | Minimum Chrome version considered current |
