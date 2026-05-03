# Response Behavior Detection

Wave: 0 (Fast Path)
Priority: 12

## Purpose

Feeds response-side bot behavior back into request detection by analyzing historical patterns from the ResponseCoordinator. Detects honeypot hits, 404 scanning patterns, authentication brute-forcing, error template harvesting, and rate limit violations.

## Signals Emitted

| Signal Key | Type | Description |
|------------|------|-------------|
| `response.coordinator_available` | bool | ResponseCoordinator service available |
| `response.client_signature` | string | IP:UA hash for lookup |
| `response.has_history` | bool | Client has prior response history |
| `response.total_responses` | int | Total response count for client |
| `response.historical_score` | double | Aggregated behavior score (0.0-1.0) |
| `response.honeypot_hits` | int | Count of honeypot path accesses |
| `response.count_404` | int | Count of 404 responses |
| `response.unique_404_paths` | int | Unique probed paths returning 404 |
| `response.scan_pattern_detected` | bool | Systematic scanning detected |
| `response.auth_failures` | int | Failed authentication attempts |
| `response.auth_struggle` | string | Severity: "mild", "moderate", "severe" |
| `response.error_pattern_count` | int | Count of error patterns triggered |
| `response.error_harvesting` | bool | Error template harvesting detected |
| `response.rate_limit_violations` | int | Rate limit violation count |

## Detection Logic

| Pattern | Threshold | Confidence | Description |
|---------|-----------|------------|-------------|
| Honeypot hits | > 0 | 0.9 | Any honeypot path access = strong bot signal |
| 404 scanning | > 15 total, > 10 unique | 0.5-0.9 | Systematic path probing |
| Auth brute force | > 20 failures | 0.85 | Severe credential stuffing |
| Error harvesting | > 10 patterns | 0.7 | Probing error responses for information |
| Rate limiting | > 5 violations | 0.75 | Repeated rate limit triggers |

## Performance

Typical execution: <1ms (in-memory lookup from ResponseCoordinator).
Requires ResponseCoordinator middleware to be active for collecting response data.
