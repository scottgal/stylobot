Here’s a feature spec you can pretty much drop into your repo as
`features/ResponseDetectionContributor.md` (or similar).

---

# Feature Spec: Response Detection Contributor

**Name:** `ResponseDetectionContributor`
**Type:** Detector / score contributor
**Scope:** HTTP(S) and API traffic (optionally SSH banners later)
**Editions:**

* **Free:** status-code–only + simple body heuristics (via config)
* **Enterprise:** status codes + richer body pattern sets + per-tenant overrides

---

## 1. Purpose & Rationale

Most existing detectors in Stylobot/BotDetection focus on **request-side** features:

* IP, UA, headers, path, method
* Behaviour (timings, click patterns, navigation graph)
* Reputation, version age, etc.

The **Response Detection Contributor** adds a complementary view:

> “Given how this client’s *requests* behave, what do the *responses* we send back look like, over time?”

We use **status codes** and **response body cues** as low-cost signals to:

* detect **404/4xx scanning** (but as a small, contextual heuristic, *not* a big hammer like Fail2ban)
* pick up **5xx cascades** from suspicious actors (probing for errors / stack traces)
* recognise **honeypot hits / trap endpoints**
* see **authentication flows** (repeated 401/403 patterns)
* detect bot-like interactions with **error/validation pages**

This detector **never stores full response bodies**—it only uses:

* codes,
* small, configured pattern matches, and
* optional hashes/labels.

---

## 2. Goals

1. **Contribute a bounded heuristic score** to overall bot/human classification, based on server responses associated
   with a client.
2. **Detect response-side patterns** indicative of automation or abuse:

    * High 4xx rate, 404 scan profiles.
    * 5xx bursts from the same actor.
    * Repeated auth failures (401/403) with suspicious cadence.
    * Honeypot endpoints being “found” and explored.
3. **Remain cheap and edge-friendly**:

    * No full-text indexing.
    * No heavy LLM/AI calls.
    * Config-driven patterns for bodies.
4. **Work for both Free & Enterprise**:

    * Same core logic.
    * Enterprise can customise weights and pattern sets; Free uses config files only.

---

## 3. Non-Goals

* Not a replacement for full WAF / RASP.
* Not a content-classifier (no parsing of user data / PII).
* Not a logging system for full response bodies.
* Not an LLM-based explainer (it just feeds into `Heuristic`/`Behavioral` and its own score).

---

## 4. Inputs & Data Model

### 4.1 Per-request input

For each completed request, the detector sees:

```csharp
public sealed record ResponseSignal(
    string RequestId,
    string ClientId,          // IP+UA or fingerprint key
    DateTimeOffset Timestamp,
    int StatusCode,           // e.g. 200, 404, 500
    long ResponseBytes,
    string? Path,
    string? Method,
    IReadOnlyDictionary<string, string> ResponseHeaders,
    ResponseBodySummary BodySummary
);
```

Where `BodySummary` is **PII-safe**:

```csharp
public sealed record ResponseBodySummary(
    bool IsPresent,
    int  Length,
    IReadOnlyList<string> MatchedPatterns,   // symbolic names, not snippets
    string? TemplateId                        // optional pre-labelled template (“login-failed-page”)
);
```

* **MatchedPatterns** come from config (see §6).
* **TemplateId** is optional; can be produced by upstream templating code if you already know which page it is.

### 4.2 Aggregation window

The contributor operates over an **Ephemeral window** keyed by `ClientId`:

For each client it keeps rolling stats over a small time horizon (e.g. last 60–300 seconds, or last N responses):

* `total_responses`
* `count_2xx, count_3xx, count_4xx, count_5xx`
* `count_404`
* `unique_404_paths`
* `auth_failures` (401/403 on auth-marked paths)
* `honeypot_hits`
* `error_page_patterns` (e.g. stack-trace-like, generic error templates)
* `validation_error_patterns` (e.g. “invalid password”, “too many attempts”)
* `mean_response_size`, `max_response_size` (optionally)
* `response_timing_correlation` (if you also know processing time)

---

## 5. Feature Extraction & Heuristics

### 5.1 Core features

Per client (within current Ephemeral window):

1. **4xx ratio**
   `f_4xx_ratio = count_4xx / max(1, total_responses)`

2. **404 density**
   `f_404_ratio = count_404 / max(1, total_responses)`
   `f_404_unique_paths = unique_404_paths`

3. **404 scan pattern score**

    * High `f_404_ratio`
    * High `f_404_unique_paths`
    * Paths match typical discovery patterns (`/admin`, `/wp-*`, `/.git`, etc.).

4. **5xx anomaly score**

    * `f_5xx_ratio = count_5xx / total_responses`
    * Are 5xx concentrated on specific endpoints?
    * Is this IP triggering 5xx where others are not (if you have global context)?

5. **Auth struggle score**

    * Number of 401/403 responses on login/auth endpoints.
    * Combined with behavioural timing, may suggest brute force.

6. **Honeypot interaction score**

    * Count of responses from honeypot endpoints (configured or flagged).
    * Weight high: human users almost never see these.

7. **Error template hits**

    * Count of body patterns like:

        * “Stack trace” markers
        * “An unexpected error occurred”
        * “Debug mode”
    * Suggests probing for weaknesses.

8. **Validation / abuse feedback loops**

    * Patterns associated with:

        * “Too many attempts”
        * “Rate limit exceeded”
        * “Your IP has been blocked”
    * Suggests someone is repeatedly hammering until they trip existing defences.

### 5.2 Output score

The detector outputs a **single `DetectorScore`**, e.g.:

```csharp
new DetectorScore(
    Name: "ResponseContributor",
    Score: rawScore,     // -1.0 (very human) .. +1.0 (very bot-like)
    Weight: policyWeight,
    Notes: "High 404 density on exploratory paths" // optional
);
```

`rawScore` is formed by combining feature contributions:

* `404 scan pattern` → small positive weight (bot-ish)
* `balanced 2xx/4xx/3xx with occasional 404` → neutral to slightly human
* `high 5xx exclusively from this client` → positive weight
* `honeypot hits` → strong positive weight
* `auth struggle with weird cadence` → positive weight (but co-scored with `Behavioral`).

The overall mapping is configurable (see §6); defaults are conservative.

---

## 6. Configuration

### 6.1 Base config (file-based, Free & Enterprise)

`response_contributor.yaml`:

```yaml
enabled: true

window:
  duration: 120s     # per-client aggregation window
  maxEvents: 200

weights:
  base: 0.7          # default Weight in DetectorScore
  features:
    four_xx_ratio: 0.2
    four_oh_four_scan: 0.35
    five_xx_anomaly: 0.3
    auth_struggle: 0.2
    honeypot_hit: 0.8
    error_template: 0.25
    abuse_feedback: 0.3

thresholds:
  four_xx_ratio_high: 0.7
  four_oh_four_ratio_scan: 0.5
  four_oh_four_unique_paths_scan: 15
  five_xx_ratio_high: 0.4
  auth_failures_high: 10
  honeypot_hit_min: 1

bodyPatterns:
  # These are names -> regex or simple contains, but we only store the *name*.
  - name: stack_trace_marker
    pattern: "Exception in thread|Stack trace|Traceback (most recent call last)"
  - name: generic_error_message
    pattern: "An unexpected error occurred"
  - name: login_failed_message
    pattern: "Invalid username or password"
  - name: rate_limited_message
    pattern: "Too many requests|Rate limit exceeded"
  - name: ip_blocked_message
    pattern: "Your IP has been blocked|Access denied for this IP"

honeypots:
  # endpoints that should almost never be accessed by humans
  - "/__stylobot-hp"
  - "/.git/"
  - "/wp-admin/install.php"
```

**Free:** this file is edited manually.
**Enterprise:** same structure, but can be modified via Policy/Override APIs per tenant.

### 6.2 Enterprise extensions

Enterprise can:

* Override `weights.features` per tenant.
* Add/disable `bodyPatterns` per tenant.
* Configure `honeypots` per tenant/app.

All changes remain **bounded**: no weight can exceed a safe multiplier (e.g. 0.5x–2x of default).

---

## 7. How the Score Feeds Into Overall Detection

The `ResponseDetectionContributor` plugs into the existing detector framework:

* It produces a `DetectorScore("ResponseContributor", ...)`.
* This is combined with other detectors (Behavioral, Heuristic, VersionAge, etc.) in the policy’s scoring model.
* Example influence:

    * `High 404 scan + honeypot hits` → bump risk band by up to +1.
    * `Normal 2xx/3xx distribution` → slight human bias, especially if other detectors are neutral.
    * `High 5xx anomaly` → small increase in risk, but *also* flags possible app error.

Important: **404s alone are never decisive**; they only nudge the score unless corroborated by paths & other detectors.

---

## 8. Privacy & Safety

* No full response bodies stored.
* `BodySummary.MatchedPatterns` only stores **symbolic pattern names**.
* Optional hashing of canonicalised body to detect “same error page” without storing content.
* Config patterns must be designed to avoid pulling in PII (no emails, names, free text, etc.).
* Logging for this detector should optionally redact paths that may contain IDs or tokens.

---

## 9. Telemetry & Debuggability

For debugging / dashboard use:

* Expose a **breakdown** for each detection (in verbose/debug mode):

```jsonc
"responseContributor": {
  "four_xx_ratio": 0.75,
  "four_oh_four_ratio": 0.60,
  "four_oh_four_unique_paths": 23,
  "five_xx_ratio": 0.00,
  "auth_failures": 0,
  "honeypot_hits": 1,
  "matched_body_patterns": ["stack_trace_marker"],
  "rawScore": 0.62
}
```

* This is surfaced in:

    * debug JSON view,
    * enterprise dashboards,
    * logs (when enabled).

Free users see the **score** and possibly a compact breakdown; enterprise gets richer drill-down.

---

## 10. Test Cases / Scenarios

### 10.1 Normal human browsing

* Mixed 200/302 with occasional 404 (typoed URL, old link).
* 4xx ratio < 0.2, 404 uniques small, no honeypot.
* **Expected:** `rawScore` near 0 or slightly negative (human-ish).

### 10.2 WordPress scanner

* 100+ different 404s in short window: `/wp-*.php`, `/.git/`, `/phpmyadmin`, `/config.php`, etc.
* 4xx ~ 1.0, 404 unique paths high, paths match known patterns.
* **Expected:** positive `rawScore`, enough to push risk to High combined with other detectors.

### 10.3 Auth brute-force

* Many 401s/403s on `/login` from same IP.
* Timing regular, other behavioural detectors also suspicious.
* **Expected:** moderate positive `rawScore` reinforcing `Behavioral`/`Heuristic` decisions.

### 10.4 Buggy client hitting 500s

* Lots of 5xx from one integration client, but same endpoint, same UA, known AS.
* May need exception config or lower weight.
* **Expected:** mild risk bump; not enough alone to mark as bot unless combined with other weirdness.

### 10.5 Honeypot hits

* Client discovers `/__stylobot-hp` and pokes around.
* **Expected:** strong positive `rawScore`, near-max, flagged as bot/attacker.

---

That’s the “Response Detection Contributor” in a nutshell:

* cheap,
* heuristic,
* fits your existing detector model,
* treats 404s and 5xxs as *small but meaningful signals*,
* and plays nicely with your edge-first, config-heavy philosophy.
