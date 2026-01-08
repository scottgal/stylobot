## Feature Spec: Sensitivity-Aware Dynamic Content Blocking

**Name:** `SensitivityAwareContentRouting`
**Role:** Orchestrator that decides *when* to run **blocking content detectors** based on **signature sensitivity** and
real-time request context.
**Status:** Draft – ready to implement.

---

## 1. Purpose

Some “signatures” (users / tenants / apps / tools) are **more likely** to send:

* PII
* regulated content
* highly sensitive text (legal, medical, finance, HR, etc.)

For these, you want:

> **Stronger, inline content checks** on *every* response, not just when generic risk is high.

At the same time you *don’t* want to:

* run blocking content checks on every request for everyone (too expensive, too slow)
* store any raw PII

So this feature:

* Maintains a **Sensitivity Profile per signature**
* Uses that profile **early in the request path**
* Emits a **signal** telling the **response sub-coordinator**:

  > “For *this* request / user: engage blocking content detectors.”

---

## 2. Core Concepts

### 2.1 Signature

You already have the notion of “signature” (user/app/tenant profile). We extend it with sensitivity metadata.

```csharp
public sealed record SignatureId(string Value); // e.g. tenant/user/tool id
```

### 2.2 Sensitivity Profile

Attached to a signature:

```csharp
public sealed record SensitivityProfile(
    SignatureId SignatureId,
    SensitivityLevel Level,
    bool LikelyPII,
    bool LikelyRegulated,
    bool LikelyUserGeneratedContent,
    DateTimeOffset UpdatedAt
);

public enum SensitivityLevel
{
    Low,
    Medium,
    High,
    Critical
}
```

**How it’s set:**

* Initially from static config (e.g. “support-portal” = High).
* Later refined by async content detectors:

    * If a signature often produces PII-like patterns → bump to `High`.
    * If it’s a machine/system account sending only telemetry → stay `Low`.

### 2.3 Request Sensitivity Decision

Per request, we derive:

```csharp
public sealed record RequestSensitivityDecision(
    string RequestId,
    SignatureId SignatureId,
    SensitivityLevel Level,
    bool RequireBlockingContentCheck
);
```

---

## 3. Pipeline Overview

High-level flow:

1. **Request enters** → Request Coordinator
2. Request Coordinator:

    * resolves `SignatureId` for this caller
    * fetches `SensitivityProfile`
    * decides `RequireBlockingContentCheck`
    * emits a **signal** for the response sub-coordinator
3. **Response sub-coordinator**:

    * listens for these signals, keyed by RequestId
    * when the backend response arrives, checks if this request was marked `RequireBlockingContentCheck`
    * if yes → runs **blocking content detectors** inline
    * if no → only runs async detectors

So exactly what you said:

> you send a signal to the response handling sub-coordinator to apply a blocking check for that user on demand.

---

## 4. Ephemeral Integration

### 4.1 Request side: Sensitivity decision atom

Inside your main request coordinator (or a dedicated atom):

```csharp
public sealed record SensitivitySignal(
    string RequestId,
    SignatureId SignatureId,
    SensitivityLevel Level,
    bool RequireBlockingContentCheck
);
```

The **Request atom**:

* Receives `DetectionContext` (IP, UA, headers, auth info, signature).
* Looks up `SensitivityProfile` from the signature store.
* Applies simple policy (see Section 5).
* Emits `SensitivitySignal` into the SignalSink.

### 4.2 Response side: Sub-coordinator

Your **Response Handling Sub-Coordinator** subscribes to:

```csharp
onSignal<SensitivitySignal>(signal =>
{
    // Typically store in a small per-request dictionary / cache keyed by RequestId
    _sensitivityCache[signal.RequestId] = signal;
});
```

When a response comes back and you’re about to run content detectors:

1. Lookup `RequestId` in `_sensitivityCache`.
2. If `RequireBlockingContentCheck` is `true`, switch to **blocking mode** for this response:

    * buffer up to `maxBufferBytes`
    * run `ContentContributor` in blocking mode
    * possibly scrub / block / challenge
3. Otherwise:

    * only run async content detection (non-blocking).

Because the whole thing is inside Ephemeral, you get:

* bounded windows of signals
* auto-cleanup when operations complete
* thread-safe access

---

## 5. Policy: When to Require Blocking Content Checks

### 5.1 Baseline rules (configurable)

Define a simple policy:

```csharp
public sealed record SensitivityRoutingPolicy(
    SensitivityLevel MinBlockingLevel,
    bool AlwaysBlockOnSensitiveEndpoints,
    IReadOnlyList<string> SensitivePaths
);
```

Defaults (example):

* `MinBlockingLevel = SensitivityLevel.High`
* `AlwaysBlockOnSensitiveEndpoints = true`
* `SensitivePaths = ["/login", "/profile", "/settings", "/api/chat", "/admin"]`

Decision logic per request:

```csharp
bool requireBlocking =
    profile.Level >= policy.MinBlockingLevel
    || (policy.AlwaysBlockOnSensitiveEndpoints && PathMatchesSensitiveEndpoint(request.Path));
```

Where `PathMatchesSensitiveEndpoint` is a simple prefix/pattern match.

### 5.2 Additional dynamic cues (optional)

You can optionally also require blocking if:

* This same signature has **recent PII-like patterns** detected async.
* There is an **ongoing incident flag** for the tenant.
* The request is to a **known PII endpoint** (e.g., `/api/upload-id`, `/api/payment`).

These extra rules are still deterministic — no magic.

---

## 6. Learning: How Sensitivity Profiles Are Updated

This is where your async content detectors come in:

1. Async `ContentContributor` runs on a sample of responses.
2. When it detects PII-like or regulated patterns (via your offline PII detectors), it raises:

```csharp
public sealed record SensitivityObservation(
    SignatureId SignatureId,
    bool ObservedPII,
    bool ObservedRegulated,
    DateTimeOffset Timestamp
);
```

3. A **SensitivityProfileUpdaterAtom** consumes these observations and maintains `SensitivityProfile` per signature:

* If many positives over time → elevate to `High` or `Critical`.
* If long period of no PII/regulated content → maybe decay downwards (configurable).

Importantly, you never store *what* the PII was, only:

* “This signature tends to produce PII content”
* Possibly some high-level class labels (“medical terms”, “financial card-like patterns”, etc.)

---

## 7. YARP Behaviour

### 7.1 Where TLS terminates

As we said earlier:

* TLS must terminate at YARP + Stylobot so you can inspect content.
* YARP → backend can be HTTP or HTTPS; doesn’t matter for detectors.

### 7.2 YARP integration hooks

In YARP, you:

* Attach a **request transform / delegating handler** that:

    * extracts signature info
    * calls `SensitivityAwareContentRouting` (or just emits `SensitivitySignal`).
* Attach a **response transform** that:

    * looks up the sensitivity decision for this `RequestId`
    * chooses blocking vs async content detection accordingly.

So the YARP response path has a simple switch:

```csharp
if (sensitivityDecision.RequireBlockingContentCheck)
{
    await RunBlockingContentDetectorsAsync(response, context);
}
else
{
    // Fire-and-forget: push to Ephemeral content detector atom
    _ = RunAsyncContentDetectors(response, context);
}
```

---

## 8. Config Example

`sensitivity_routing.yaml`:

```yaml
enabled: true

routingPolicy:
  minBlockingLevel: High
  alwaysBlockOnSensitiveEndpoints: true
  sensitivePaths:
    - "/login"
    - "/profile"
    - "/user/settings"
    - "/api/chat"
    - "/api/payments"
    - "/admin"

learning:
  piiObservationThreshold: 5        # number of observations before bumping level
  decayAfterDaysWithoutPii: 30      # days to consider lowering level
  maxLevel: Critical
```

`signature_profiles.yaml` (initial seeding):

```yaml
profiles:
  - id: "tenant:blog"
    level: Low
    likelyPII: false
    likelyRegulated: false

  - id: "tenant:hr-portal"
    level: High
    likelyPII: true
    likelyRegulated: true

  - id: "tenant:medical-app"
    level: Critical
    likelyPII: true
    likelyRegulated: true
```

Enterprise can override these per-tenant through its admin plane; free users edit them as files.

---

## 9. Safety & Guarantees

* Blocking content checks are applied **only** when:

    * a signature is known or inferred to be sensitive, *or*
    * the endpoint is flagged as sensitive.
* No raw PII is stored:

    * Only pattern labels + sensitivity flags are recorded.
* Blocking detectors are bounded:

    * max bytes to inspect
    * max time per inspection
* Existing behaviour (for non-sensitive signatures) stays:

    * low-latency,
    * async content inspection only.

---

## 10. Example Scenarios

### Scenario 1 — HR Portal (known PII-heavy)

* Signature “hr-portal” has `SensitivityLevel.High`.
* Any request from that signature automatically:

    * `RequireBlockingContentCheck = true`.
* Response sub-coordinator always runs blocking content detectors on its responses.

### Scenario 2 — Generic blog tenant

* Signature “blog” starts at `Low`.
* Async content detectors rarely see PII.
* `RequireBlockingContentCheck = false` except on globally sensitive endpoints (if configured).
* No extra latency for blog traffic.

### Scenario 3 — Chat-like endpoint starts leaking PII

* Signature “chat-tenant” initially `Medium`.
* Async detection sees recurring PII-like patterns.
* Sensitivity updater bumps to `High`.
* Future chat responses for that tenant:

    * trigger blocking checks
    * allow you to scrub / mask / enforce stricter policies.

---

So yes, your “weird pipeline” becomes:

* **Request path**: “What do we know about this signature? Should we arm the big guns for this response?”
* **Signal**: `SensitivitySignal` to the response sub-coordinator.
* **Response path**: “Did the request say this user is sensitive? If yes, block-inspect; if not, just sample async.”

If you like, I can next:

* sketch the actual C# interface for the `SensitivityProfileStore` and `SensitivityRoutingAtom`, or
* draw a little mermaid diagram of the whole request→signal→response flow.

Nice, yes, this slots really naturally into pure ASP.NET too — you just move the “response sub-coordinator” into
middleware.

Here’s an **add-on spec section** you can paste under the existing “SensitivityAwareContentRouting / ContentContributor”
spec:

---

## 11. ASP.NET Core Middleware Integration

**Goal:**
Allow all the sensitivity-aware, blocking/async content detection to run **inside a normal ASP.NET Core app**, *even
without YARP*, by plugging into the **response pipeline**.

The middleware:

* wraps the ASP.NET response,
* decides per-request whether to run **blocking content detection**,
* always has the option to emit an **async content signal** to Ephemeral for later scoring.

It works whether:

* Kestrel is directly exposed to the internet, or
* Kestrel sits behind Nginx, Caddy, Traefik, etc., as long as **TLS terminates at Kestrel or upstream** and the app sees
  plaintext HTTP.

---

### 11.1 Middleware Responsibilities

For each request, the middleware must:

1. Generate or retrieve a **RequestId** (and `SignatureId` if available from context).
2. Optionally resolve **SensitivityProfile** and emit a `SensitivitySignal` (if you want to keep the same “request
   decides for response” design).
3. Wrap the **response body stream** in a buffering wrapper:

    * capture up to `maxBufferBytes` for potential inspection,
    * pass-through everything to the client.
4. After the downstream pipeline completes and the response is ready:

    * Build a `ContentSignal` from:

        * status code,
        * path, method,
        * response length,
        * buffered body slice.
    * Decide whether:

        * to run **blocking content detectors** inline (for *this* request), and/or
        * to enqueue **async** content detection via Ephemeral.

The same **Content Detection Contributor** implementation is used; only the plumbing differs.

---

### 11.2 Registration & Ordering

Add the middleware late in the pipeline, after routing and endpoint execution, but **before** any final response filters
that might compress/encrypt body.

Typical ordering:

```csharp
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseRouting();

// auth, etc.
app.UseAuthentication();
app.UseAuthorization();

// Stylobot sensitivity + content middleware
app.UseMiddleware<SensitivityRoutingMiddleware>(); // optional, request-side
app.UseMiddleware<ContentInspectionMiddleware>();  // response-side

app.MapControllers();
app.Run();
```

You can combine sensitivity routing and content inspection into a single middleware if you like, but conceptually
they’re two pieces.

---

### 11.3 Request-Side: SensitivityRoutingMiddleware

Optional middleware that:

* Extracts `SignatureId` from:

    * user claims,
    * tenant headers,
    * or some app-specific mapping.
* Fetches `SensitivityProfile` from the signature store.
* Applies `SensitivityRoutingPolicy` to compute:

```csharp
RequestSensitivityDecision(
    RequestId,
    SignatureId,
    Level,
    RequireBlockingContentCheck
);
```

* Emits a `SensitivitySignal` into Ephemeral and/or stores the decision in `HttpContext.Items`:

```csharp
context.Items["Stylobot.RequestSensitivity"] = decision;
```

Pseudo-spec:

```csharp
public sealed class SensitivityRoutingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ISensitivityProfileStore _profiles;
    private readonly SensitivityRoutingPolicy _policy;
    private readonly ILogger<SensitivityRoutingMiddleware> _logger;

    public SensitivityRoutingMiddleware(
        RequestDelegate next,
        ISensitivityProfileStore profiles,
        SensitivityRoutingPolicy policy,
        ILogger<SensitivityRoutingMiddleware> logger)
    {
        _next = next;
        _profiles = profiles;
        _policy = policy;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = GetOrCreateRequestId(context);
        var signatureId = ResolveSignatureId(context); // tenant/user/app

        var profile = await _profiles.GetProfileAsync(signatureId, context.RequestAborted);
        var decision = SensitivityDecisionEngine.Decide(profile, _policy, context.Request.Path, requestId);

        context.Items["Stylobot.RequestSensitivity"] = decision;

        // Optional: also raise Ephemeral SensitivitySignal here

        await _next(context);
    }
}
```

This makes the later middleware’s life easier.

---

### 11.4 Response-Side: ContentInspectionMiddleware

This is the main integration point for the **Content Detection Contributor** in ASP.NET Core.

Responsibilities:

1. Wrap `HttpResponse.Body` in a buffering stream:

    * store up to `maxBufferBytes`.
2. Execute downstream middleware & endpoints.
3. After they return:

    * restore the original body stream,
    * build `ContentSignal` with:

        * RequestId,
        * SignatureId (if known),
        * status code,
        * path, method,
        * response size,
        * buffered body slice.
4. Look up `RequestSensitivityDecision` from `HttpContext.Items` (if present).
5. Based on decision & global config:

    * if `RequireBlockingContentCheck` → run `ContentContributor` in **blocking mode** on the captured body slice:

        * may scrub/mask or drop response before writing.
    * in any case → enqueue `ContentSignal` to Ephemeral for **async** analysis.
6. Write the (possibly modified) body out to the client.

Pseudo-spec:

```csharp
public sealed class ContentInspectionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IContentDetectionService _contentDetector;
    private readonly ContentContributorConfig _config;
    private readonly ILogger<ContentInspectionMiddleware> _logger;

    public ContentInspectionMiddleware(
        RequestDelegate next,
        IContentDetectionService contentDetector,
        ContentContributorConfig config,
        ILogger<ContentInspectionMiddleware> logger)
    {
        _next = next;
        _contentDetector = contentDetector;
        _config = config;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var originalBody = context.Response.Body;

        await using var buffer = new MemoryStream();
        context.Response.Body = new TeeStream(originalBody, buffer);

        try
        {
            await _next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Position = 0;
        var previewBytes = GetPreviewSlice(buffer, _config.MaxPreviewBytes);

        var requestId = GetOrCreateRequestId(context);
        var sensitivity = context.Items["Stylobot.RequestSensitivity"] as RequestSensitivityDecision;

        var contentSignal = new ContentSignal(
            RequestId: requestId,
            ClientId: ResolveClientId(context),
            Timestamp: DateTimeOffset.UtcNow,
            StatusCode: context.Response.StatusCode,
            Path: context.Request.Path,
            Method: context.Request.Method,
            ResponseBytes: buffer.Length,
            BodyPreview: previewBytes,
            MatchedPatterns: Array.Empty<string>(),
            TemplateHash: null
        );

        if (sensitivity?.RequireBlockingContentCheck == true &&
            _config.BlockingModeEnabled)
        {
            // Blocking mode: interpret and maybe modify the body before sending
            var decision = await _contentDetector.EvaluateBlockingAsync(contentSignal, context.RequestAborted);

            switch (decision.Action)
            {
                case ContentBlockAction.Allow:
                    // Write original buffer to client
                    buffer.Position = 0;
                    await buffer.CopyToAsync(originalBody);
                    break;

                case ContentBlockAction.Mask:
                    var maskedBody = decision.MaskedBody; // from detector
                    await originalBody.WriteAsync(maskedBody, 0, maskedBody.Length);
                    break;

                case ContentBlockAction.Block:
                    context.Response.StatusCode = decision.OverrideStatusCode ?? StatusCodes.Status403Forbidden;
                    await originalBody.WriteAsync(decision.BlockBodyBytes);
                    break;
            }
        }
        else
        {
            // Non-blocking: just pass through original response
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody);

            // And fire-and-forget async analysis
            _ = _contentDetector.EvaluateAsync(contentSignal, context.RequestAborted);
        }
    }
}
```

Notes:

* `TeeStream` is just a stream that writes to both the original response and a buffer.
* `MaxPreviewBytes` caps how much content you ever examine.
* `EvaluateBlockingAsync` must obey `maxBlockingDurationMs`.

---

### 11.5 Request / Client IDs

Use something like:

* `requestId`:

    * from `HttpContext.TraceIdentifier` or
    * from `X-Request-ID` header (if already present).
* `clientId`:

    * IP (`RemoteIpAddress`) + UA hash, or
    * your existing `FingerprintId` from BotDetection.

This keeps the **ContentSignal** consistent with your other detectors.

---

### 11.6 Behaviour With / Without YARP

* **Without YARP**:

    * TLS terminates at Kestrel.
    * ASP.NET middleware sees full plaintext HTTP.
    * Full content detection works on responses.

* **With YARP fronting ASP.NET**:

    * If TLS terminates at **YARP** and YARP → ASP.NET is HTTP:

        * Then *either*:

            * YARP hosts the content middleware itself (closer to the edge), or
            * you let ASP.NET do content detection behind YARP on the cleartext hop.
    * If you do TLS passthrough from YARP to ASP.NET:

        * content inspection **must** happen in the first TLS terminator (whichever that is).

The middleware spec itself doesn’t care which; it just assumes it’s running in a place where it can see plaintext HTTP
responses.

---

### 11.7 Configuration for ASP.NET version

Reuse `content_contributor.yaml` + `sensitivity_routing.yaml`:

* bound via `IOptions<ContentContributorConfig>` and `IOptions<SensitivityRoutingPolicy>` in ASP.NET DI.
* same pattern sets, same thresholds.
* the only ASP.NET-specific knobs are:

    * per-path sensitivity based on routing (e.g. `[SensitiveEndpoint]` attribute mapping),
    * whether to register middlewares globally or only for certain endpoints.

---

With this, you now have:

* **YARP integration path** *and*
* **pure ASP.NET middleware path**

both using the same underlying:

* `SensitivityProfile`
* `RequestSensitivityDecision`
* `ContentSignal`
* `Content Detection Contributor`

So you can deploy Stylobot in front of:

* classic ASP.NET sites,
* APIs & microservices,
* YARP reverse proxies,

without changing your detector logic — only where you plug in the middleware.
