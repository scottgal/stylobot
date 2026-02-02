## Mostlylucid.ApiHolodeck (Future Feature)

**A behavioural-routing policy that uses `mostlylucid.mockllmapi` to generate honeypot responses.**

### 1. Concept

ApiHolodeck isn’t a new mock engine – it’s a **gateway-level policy** that:

* Lives inside **Stylobot.Gateway**
* Uses **Mostlylucid.BotDetection** to decide “who gets holodecked”
* Uses **mostlylucid.mockllmapi** as the **simulation backend**
* Keys its “fake universe” by **fingerprint / context**

Rough flow:

```text
Request
  → BotDetection genome
    → ActionPolicy: holodeck
      → ApiHolodeck
        → mostlylucid.mockllmapi (via /api/mock, /OpenApi, SignalR, etc.)
          → Synthetic response
```

So “turning on ApiHolodeck” is just “switch this ActionPolicy on”.

---

## 2. New Action Policy Type: `holodeck`

In your existing `BotDetection:ActionPolicies`:

```json
"ActionPolicies": {
  "allow": { "Type": "Allow" },
  "block": { "Type": "Block", "StatusCode": 403 },

  "holodeck": {
    "Type": "Holodeck",
    "Mode": "realistic-but-useless",
    "ContextSource": "Fingerprint",      // Fingerprint | Ip | Session
    "MockApiBasePath": "/api/mock",      // The mapped path for mostlylucid.mockllmapi
    "IncludeSchema": false,              // If you ever want X-Response-Schema
    "MaxStudyRequests": 50               // After N requests, stop studying & hard-block
  }
}
```

Then in a `DetectionPolicy`:

```json
"DetectionPolicies": {
  "default": {
    "ActionPolicyName": "allow",
    "Transitions": [
      { "WhenRiskExceeds": 0.5, "ActionPolicyName": "holodeck" },
      { "WhenRiskExceeds": 0.9, "ActionPolicyName": "block" }
    ]
  }
}
```

So as risk creeps up, you just **flip them into the holodeck** instead of the real backend.

---

## 3. How ApiHolodeck Calls `mostlylucid.mockllmapi`

You already have:

```csharp
builder.Services.AddLLMockApi(config);
app.MapLLMockApi("/api/mock");
```

ApiHolodeck just becomes a small gateway component that:

1. Takes the *original request* (method, path, query, body).

2. Derives a **context key** – e.g. from BotDetection fingerprint:

   ```csharp
   var contextKey = botResult.FingerprintId ?? botResult.IpAddress ?? "anon";
   ```

3. Rewrites the request internally to hit mockllmapi:

    * If you want **“shape-less” AI mocking**:
      forward as-is to `/api/mock/{original-path-and-query}`
    * If you want structured honeypots:
      set `X-Response-Shape` / `shape` / OpenAPI-based path depending on config.

Pseudocode:

```csharp
public class HolodeckActionExecutor : IBotActionExecutor
{
    private readonly HttpClient _mockApiClient;

    public HolodeckActionExecutor(IHttpClientFactory factory)
        => _mockApiClient = factory.CreateClient("HolodeckMockApi");

    public async Task ExecuteAsync(HttpContext context, BotDecision decision)
    {
        var fingerprint = decision.FingerprintId ?? "anon";

        // Build proxied URI to mostlylucid.mockllmapi
        var originalPath = context.Request.Path.ToString();
        var originalQuery = context.Request.QueryString.ToString();

        var uri = $"/api/mock{originalPath}?{originalQuery}&context={fingerprint}";

        using var proxyRequest = new HttpRequestMessage(
            new HttpMethod(context.Request.Method), uri);

        // Optionally forward body & headers
        // Optionally add X-Response-Shape, X-Error-Code, etc.

        var response = await _mockApiClient.SendAsync(proxyRequest,
            HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers)
            context.Response.Headers[header.Key] = header.Value.ToArray();

        await response.Content.CopyToAsync(context.Response.Body);
    }
}
```

The key bit: **we pass `context={fingerprint}`** to mockllmapi’s **API Contexts** so each fingerprint gets a consistent
synthetic world.

---

## 4. Holodeck Modes → MockLLMAPI Features

Map Holodeck modes onto what `mostlylucid.mockllmapi` already does:

* `realistic` → vanilla `/api/mock/**` with no shape: free-form, realistic data.
* `realistic-but-useless` → use a very generic shape (e.g. always “demo” fields), or an OpenAPI spec that doesn’t match
  your real one.
* `strict-schema` → use `X-Response-Shape` or OpenAPI mocks (`/petstore/...`) with `IncludeShapeInResponse` turned on.
* `chaos` → use **error simulation** and **token limits** to sporadically break things:

    * `?error=500&errorMessage=Internal%20Error`
    * random `$error` in shape
* `adversarial-for-bots` → shapes with `$cache` warmup + weirdness:

    * odd enums, inconsistent fields, non-monotonic IDs, etc.

Example Holodeck → mockllmapi mapping config (future):

```json
"HolodeckProfiles": {
  "realistic-but-useless": {
    "UseContext": true,
    "AddContextQuery": true,
    "MaxGraphDepth": 3
  },
  "chaos": {
    "UseErrorSimulation": true,
    "ErrorRate": 0.3,
    "MaxGraphDepth": 2
  }
}
```

Internally that just controls which query params/headers ApiHolodeck adds when it forwards to `/api/mock`.

---

## 5. YARP Integration: “Holodeck Cluster” (optional)

If you want to treat it as a **cluster** instead of an in-proc middleware call:

```json
"ReverseProxy": {
  "Clusters": {
    "holodeck-cluster": {
      "Destinations": {
        "mock": { "Address": "http://mockllmapi:5116" }
      }
    }
  }
}
```

Then your `ActionPolicy` can be:

```json
"ActionPolicies": {
  "holodeck": {
    "Type": "Redirect",
    "RedirectCluster": "holodeck-cluster",
    "PreserveOriginalPath": true,
    "AppendQuery": "context={fingerprint}"
  }
}
```

In that world, ApiHolodeck is mostly configuration + a tiny bit of smarts to expand `{fingerprint}` tokens.

---

## 6. Feedback Loop (Study → Cutoff)

Because `mostlylucid.mockllmapi` supports:

* contexts
* OpenAPI mocks
* SignalR streaming
* SSE streaming

You can:

1. Route suspicious fingerprints into specific **holodeck contexts**.
2. Log how they walk through that fake API.
3. Once `MaxStudyRequests` for a fingerprint or pattern is hit:

    * update a **fastpath rule** (e.g. “this header/UA/template is now hard-block”)
    * flip their ActionPolicy from `holodeck` → `block` or `throttle`.

From your side, that’s just:

* **no new code in mockllmapi**,
* only: “here’s the context key, here’s the mode, feed me JSON.”

---

## 7. TL;DR – What “switching the policy in” means

Practically, in vFuture:

* You already have `mostlylucid.mockllmapi` running (maybe as another container).
* In `BotDetection:ActionPolicies`, you define `"holodeck"` using type `Holodeck` or a `Redirect` to `holodeck-cluster`.
* The behavioural router, when risk crosses a threshold, chooses that action instead of `allow`.
* ApiHolodeck forwards the request to `/api/mock` (or `/OpenApi`, or SignalR) **with a `context` tied to the fingerprint
  **.
* The scraper/agent lives forever in a fake world until your cutoff rule has learned enough.

If you want, next step I can draft:

* A **small “Future Features” section** you can drop into the Stylobot Gateway README (“Holodeck Routing (planned)”).
* Or a **concrete example BotDetection config** snippet that assumes `mockllmapi` is running in the same compose.
