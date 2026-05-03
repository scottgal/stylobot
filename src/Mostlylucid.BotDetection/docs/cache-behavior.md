# Cache Behavior Detection

Wave: 0 (Fast Path)
Priority: 15

## Purpose

Analyzes HTTP caching behavior to detect bots. Real browsers use cache validation headers (If-None-Match, If-Modified-Since) and respect cache-control directives. Bots typically ignore these, making rapid repeated requests for the same resources without caching.

## Signals Emitted

| Signal Key | Type | Description |
|------------|------|-------------|
| `cache.validation_missing` | bool | Missing ETag/Last-Modified validation headers |
| `cache.compression_supported` | bool | Supports gzip/br compression |
| `cache.rapid_repeated` | bool | Rapid re-requests without cache headers |
| `cache.behavior_anomaly` | bool | Overall cache pattern anomaly detected |
| `ResourceRequestCount` | int | Count of repeated resource requests |

## Detection Logic

1. **Stateful tracking**: Maintains per-client profiles of resource request patterns (1-hour sliding window)
2. **Validation header check**: Monitors for If-None-Match and If-Modified-Since headers on subsequent requests to static resources (.css, .js, .png, .jpg, etc.)
3. **Compression support**: Checks Accept-Encoding header for gzip/br support (real browsers always support compression)
4. **Rapid repeat detection**: Flags clients re-requesting the same resource within 5 seconds without cache headers
5. **Anomaly scoring**: Combines missing validation + no compression + rapid repeats into overall anomaly signal

## Performance

Typical execution: <1ms (in-memory cache lookup).
Cache resource entries expire after 10 minutes. Client profiles expire after 1 hour.
