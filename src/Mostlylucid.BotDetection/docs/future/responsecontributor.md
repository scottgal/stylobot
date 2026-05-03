Here's a focused spec just for **Response-Aware Detection** in your bot detector / gateway.

---

# Response-Aware Detection & Feedback

*Feature Spec for mostlylucid.botdetection / YARP Gateway*

## 1. Purpose & Goals

Add a **response-side detection layer** that:

1. Observes backend responses (status codes, latency, size, app-level error headers).
2. Turns them into **behavioural signals** (per-request + aggregated per-identity).
3. Feeds these signals into:

    * The **heuristic detector** (as new features).
    * The **learning system** (as “weak labels” / evidence for future tuning).
4. Does this **without** adding noticeable latency or tight coupling to any specific backend.

The core idea:

> “What the backend does (401, 403, 404, 429, 5xx, etc.) is evidence about the caller’s behaviour. Capture it and close
> the loop.”

---

## 2. Scope / Non-Goals

**In scope**

* Observing and recording:

    * HTTP status codes (full and family).
    * Latency buckets.
    * Response size buckets.
    * Optional app-level error codes via headers.
* Emitting **response signals** into the existing blackboard model.
* Maintaining short-term **per-identity aggregates** (IP / fingerprint / API key).
* Using those aggregates as **input features** for the heuristic detector.
* Surfacing response-based features in the JSON debug output & dashboard.

**Out of scope (for this feature)**

* Real-time re-scoring of an *already completed* request.
* Changing backend behaviour (no retries, no auto-fallback here).
* Deep parsing of JSON bodies for semantics (that’s a separate, future “semantic outcome detector”).

---

## 3. Architecture Overview

### 3.1 High-level Flow

```mermaid
flowchart LR
    A[Incoming Request] --> B[Request Detectors<br/>(UA, Header, Rate, etc.)]
    B --> C[Heuristic (Provisional Score)]
    C --> D[Forward to Backend]
    D --> E[Backend Response]
    E --> F[Response Observer Middleware]
    F --> G[ResponseDetector<br/>(Signals + Aggregates)]
    G --> H[Learning Store<br/>(Per-identity history)]
    H --> I[Next Request Priors<br/>(New Features)]
```

* Current request: request detectors → heuristic → allow/throttle/block.
* Response observer:

    * emits signals for logging & analytics,
    * updates per-identity state used as priors/features **for the next requests**.

---

## 4. Data Model

### 4.1 ResponseContext

New lightweight context passed into the detection layer:

```csharp
public sealed record ResponseContext(
    int StatusCode,
    long ElapsedMs,
    long? ResponseBytes,
    IReadOnlyDictionary<string, string> Headers);
```

Captured in the YARP gateway or ASP.NET middleware after the backend response is available.

### 4.2 Response Signals (per request)

Normalized signals added to the blackboard / detection result:

```csharp
public sealed record ResponseSignals
{
    public int StatusCode { get; init; }
    public int StatusFamily { get; init; }          // 2, 3, 4, 5
    public bool IsError { get; init; }              // >=400
    public bool IsAuthFailure { get; init; }        // 401 (optionally 403 on login)
    public bool IsAccessDenied { get; init; }       // 403
    public bool IsNotFound { get; init; }           // 404
    public bool IsRateLimited { get; init; }        // 429
    public bool IsServerError { get; init; }        // 5xx
    public int LatencyBucketMs { get; init; }       // 0–50, 51–200, 201–1000, etc.
    public int SizeBucketBytes { get; init; }       // 0–1K, 1–10K, 10–100K, etc.
    public string? AppErrorCode { get; init; }      // from X-App-Error or similar
}
```

### 4.3 Per-Identity Response History

Keyed by **identity** (to re-use your existing reputation key type):

```csharp
public sealed record ResponseHistorySnapshot(
    int TotalRequests,
    int TotalErrors,
    int AuthFailures,
    int AccessDenied,
    int NotFoundCount,
    int RateLimitedCount,
    int ServerErrorCount,
    int DistinctPaths404,
    int DistinctUserIdsFailed,    // optional (if available)
    double ErrorRatio,            // TotalErrors / TotalRequests
    double AuthFailRatio,         // AuthFailures / (AuthAttempts)
    DateTimeOffset LastUpdated);
```

Maintained with a sliding window (e.g. 5–15 min) using EMA or time-decayed counters.

---

## 5. Integration Points

### 5.1 Response Observer Middleware

**Location**

* Gateway: in the YARP pipeline, **after** the reverse proxy call returns.
* App: in ASP.NET Core middleware, **after** `next(context)`.

**Responsibility**

* Time the backend call.
* Capture status code, response size, and headers.
* Construct `ResponseContext`.
* Invoke the `IResponseSignalSink`.

Example:

```csharp
public sealed class ResponseObservationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IResponseSignalSink _sink;
    private readonly ILogger<ResponseObservationMiddleware> _logger;

    public ResponseObservationMiddleware(
        RequestDelegate next,
        IResponseSignalSink sink,
        ILogger<ResponseObservationMiddleware> logger)
    {
        _next = next;
        _sink = sink;
        _logger = logger;
    }

    public async Task Invoke(HttpContext context)
    {
        var sw = ValueStopwatch.StartNew();
        await _next(context);
        var elapsedMs = sw.GetElapsedTime().TotalMilliseconds;

        var responseContext = new ResponseContext(
            StatusCode: context.Response.StatusCode,
            ElapsedMs: (long)elapsedMs,
            ResponseBytes: context.Response.ContentLength,
            Headers: context.Response.Headers
                .ToDictionary(h => h.Key, h => h.Value.ToString()));

        await _sink.RecordAsync(context, responseContext);
    }
}
```

### 5.2 Response Signal Sink & Detector

```csharp
public interface IResponseSignalSink
{
    Task RecordAsync(HttpContext httpContext, ResponseContext response);
}
```

Default implementation:

* Derives identity (IP, fingerprint, API key, etc.) using existing `IdentityResolver`.
* Builds `ResponseSignals`.
* Updates per-identity `ResponseHistory`.
* Optionally pushes an **async event** into the learning pipeline.

Internally it calls a new detector: `ResponseDetector`.

---

## 6. ResponseDetector Behaviour

### 6.1 Feature Extraction Rules

Per-request signals:

* `resp.status_family`
* `resp.status_code`
* `resp.is_auth_failure`
* `resp.is_access_denied`
* `resp.is_not_found`
* `resp.is_rate_limited`
* `resp.latency_bucket`
* `resp.size_bucket`
* `resp.app_error_code` (categorical/hash)

Per-identity aggregates (sliding window):

* `resp.auth_fail_count_5m`
* `resp.auth_fail_ratio_5m`
* `resp.distinct_usernames_failed_5m`
* `resp.not_found_count_5m`
* `resp.distinct_paths_404_5m`
* `resp.rate_limited_count_5m`
* `resp.server_error_count_5m`

These become features like:

```json
{
  "response.status_code": 401,
  "response.is_auth_failure": true,
  "response.auth_fail_ratio_5m": 0.80,
  "response.distinct_usernames_failed_5m": 10,
  "response.not_found_count_5m": 0,
  "response.rate_limited_count_5m": 3
}
```

### 6.2 Contribution to Heuristic

The heuristic model gets new features (you already have infra for dynamic feature sets).
Initial guideline weighting (per segment):

* **Login / auth routes**:

    * High positive weight for:

        * high `auth_fail_ratio`
        * many distinct usernames (credential stuffing)
    * Slight negative weight for 1–2 failures followed by success (normal human).

* **Admin / privileged routes**:

    * Access denied (`403`) + “rare route” + unusual identity → strong bot/probe signal.

* **Random 404 patterns**:

    * Many distinct 404 paths from same identity → crawler / scanner.

* **429 hits**:

    * Repeated 429s → pushing limits deliberately.

Weights will be tuned over time by existing learning, but seed them with reasonable base values.

---

## 7. Learning & Feedback Loop

### 7.1 Response as Weak Label

While response codes are not full ground truth, they are **good hints**:

* Many `401/403` without a subsequent success → likely malicious or scripted.
* Many `404` on weird paths → likely scanner/scraper.
* Bursts of `429` → aggressive automation.

Use this as additional evidence in the learning system:

* When an identity/cluster is *later* confirmed as bot:

    * Upweight features like high `auth_fail_ratio`, 404 burst, repeated 429, etc.
* When confirmed as legitimate (false positive):

    * Downweight response features observed in that context.

### 7.2 Segment-aware

Response-based weights should be **segment-aware** (per route/policy), not global.
E.g.:

* 404s are normal on some internal APIs (probing), but not on `/api/login`.
* 401s are common for SSO flows, but suspicious elsewhere.

Tie this into your existing `TrafficSegment` model.

---

## 8. Configuration

New config section:

```json
"BotDetection": {
  "ResponseDetection": {
    "Enabled": true,
    "TrackLatency": true,
    "TrackSize": true,
    "StatusFamilies": [4, 5],            // which families to record
    "TrackStatusCodes": [401, 403, 404, 429],
    "SlidingWindowSeconds": 600,
    "Max404PathsPerIdentity": 100,
    "MaxUsernamesPerIdentity": 100,
    "AppErrorHeaderNames": [ "X-App-Error", "X-Error-Code" ]
  }
}
```

* `Enabled` – master switch.
* `SlidingWindowSeconds` – window for aggregates.
* `TrackStatusCodes` – allow tuning to environment.
* `AppErrorHeaderNames` – integration point with backend-specific error semantics.

---

## 9. Telemetry & Dashboard Impact

### 9.1 New fields in detection JSON

Add a `response` block:

```json
"response": {
  "statusCode": 401,
  "statusFamily": 4,
  "isAuthFailure": true,
  "authFailCount5m": 12,
  "authFailRatio5m": 0.92,
  "notFoundCount5m": 0,
  "rateLimitedCount5m": 0
}
```

### 9.2 UI

* **Per-identity view**:

    * Charts: auth failures, 404s, 429s over time.
* **Per-route view**:

    * Distribution of response codes by segment.
    * “Top routes producing 4xx / 5xx for suspected bots.”
* **Incident detail view**:

    * “Backend outcome” panel summarising:

        * status code,
        * whether this is part of a repeated pattern,
        * how much it contributed to the bot score.

---

## 10. Edge Cases & Safety

* If backend is flaky (lots of 5xx for everyone), ensure:

    * response signals do not *alone* push identities into “bot”.
    * 5xx is treated carefully (low initial weight) and learned over time.
* Ensure middleware is resilient:

    * If `IResponseSignalSink` fails, **do not** fail the request.
    * Log and continue.
* Avoid unbounded memory:

    * cap distinct 404 paths / usernames per identity.
    * periodically GC stale identities beyond the sliding window.

---

If you want, next step I can do a **concrete C# sketch** of:

* `ResponseSignalSink`
* `ResponseDetector`
* how to augment your existing `DetectionResult` with the `response` block.

If 429 with rate limiting headers ensure we can route to throttling until it recovers etc.