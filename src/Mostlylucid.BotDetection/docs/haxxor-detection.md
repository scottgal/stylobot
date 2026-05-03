# Haxxor (Attack Payload) Detection

Detects SQL injection, XSS, path traversal, command injection, SSRF, template injection, webshell probes, config exposure scans, admin panel scanning, debug endpoint probing, backup/dump scanning, and encoding evasion attempts. Runs in Wave 0 (priority 7) on every request with zero dependencies.

## How It Works

The detector operates in two phases for maximum performance. Phase 1 performs zero-allocation, span-based path matching against pre-built pattern lists (path probes, webshell, backup, admin, debug, config exposure). Phase 2 only runs if SIMD-accelerated scanning detects suspicious characters (`'"<>;|` etc.) in the URL, at which point it applies compiled regex patterns for injection categories (SQLi, XSS, traversal, command injection, SSRF, SSTI) and checks for encoding evasion markers (double-encoding, null bytes, overlong UTF-8).

Attack categories are tracked using bit flags to avoid allocations. When multiple categories match, a compound confidence bonus is applied (configurable per extra category). The detector classifies injection attacks (SQLi, XSS, traversal, CMDi, SSRF, SSTI) as `MaliciousBot` with `StrongBotContribution`, while scanning patterns (path probes, config exposure, admin scan) are classified as `Scraper` with regular `BotContribution`.

Severity is computed from the combination of category count and whether injection attacks are present: critical (3+ categories with injection), high (any injection), medium (3+ scanning categories), or low (single scanning category).

## Signals Emitted

| Signal Key | Type | Description |
|---|---|---|
| `attack.detected` | boolean | Whether any attack pattern was found |
| `attack.categories` | string | Comma-separated matched categories |
| `attack.severity` | string | `low`, `medium`, `high`, or `critical` |
| `attack.sqli` | boolean | SQL injection pattern matched |
| `attack.xss` | boolean | XSS pattern matched |
| `attack.traversal` | boolean | Path traversal pattern matched |
| `attack.cmdi` | boolean | Command injection pattern matched |
| `attack.ssrf` | boolean | SSRF pattern matched |
| `attack.ssti` | boolean | Template injection pattern matched |
| `attack.path_probe` | boolean | Path probing detected (wp-admin, .env, .git) |
| `attack.config_exposure` | boolean | Config file scan detected |
| `attack.webshell_probe` | boolean | Webshell probe detected |
| `attack.backup_scan` | boolean | Backup/dump file scan detected |
| `attack.admin_scan` | boolean | Admin panel scan detected |
| `attack.debug_exposure` | boolean | Debug endpoint probe detected |
| `attack.encoding_evasion` | boolean | Encoding evasion attempt detected |

## Configuration

```json
{
  "BotDetection": {
    "Detectors": {
      "HaxxorContributor": {
        "Parameters": {
          "sqli_confidence": 0.95,
          "xss_confidence": 0.90,
          "traversal_confidence": 0.90,
          "compound_bonus_per_category": 0.05,
          "max_compound_confidence": 0.99,
          "regex_timeout_ms": 100,
          "max_input_length": 8192
        }
      }
    }
  }
}
```

## Parameters

| Parameter | Default | Description |
|---|---|---|
| `sqli_confidence` | 0.95 | Confidence for SQL injection detection |
| `xss_confidence` | 0.90 | Confidence for XSS detection |
| `traversal_confidence` | 0.90 | Confidence for path traversal detection |
| `cmdi_confidence` | 0.95 | Confidence for command injection detection |
| `ssrf_confidence` | 0.90 | Confidence for SSRF detection |
| `ssti_confidence` | 0.90 | Confidence for template injection detection |
| `path_probe_confidence` | 0.75 | Confidence for path probing |
| `config_exposure_confidence` | 0.80 | Confidence for config file scanning |
| `webshell_probe_confidence` | 0.85 | Confidence for webshell probes |
| `backup_scan_confidence` | 0.75 | Confidence for backup file scanning |
| `admin_scan_confidence` | 0.70 | Confidence for admin panel scanning |
| `debug_exposure_confidence` | 0.80 | Confidence for debug endpoint probing |
| `encoding_evasion_confidence` | 0.85 | Confidence for encoding evasion |
| `compound_bonus_per_category` | 0.05 | Additional confidence per extra category |
| `max_compound_confidence` | 0.99 | Maximum compound confidence cap |
| `regex_timeout_ms` | 100 | Per-regex timeout in milliseconds |
| `max_input_length` | 8192 | Maximum input length for regex scanning |
