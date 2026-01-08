# **Content Detection Contributor**

### *(Sits alongside Request, Response, Behavioral, and Heuristic detectors)*

Version: **v1.0**
Status: **Draft – Ready for implementation**

---

# 1. **Purpose**

The **Content Detection Contributor** evaluates the *response body content itself* to produce additional
behavioural/semantic signals for Stylobot’s bot/human classifier.

It adds the ability to:

* Detect content-based anomalies (e.g., error pages, debug leakage)
* Identify response patterns correlated with bots or malicious activity
* Perform *blocking* inline content inspection for high-risk scenarios
* Perform *async* content inspection for low-risk scenarios to avoid latency

This module complements the **Response Detection Contributor** by looking **inside** the response, rather than only at
status codes, headers, or patterns.

---

# 2. **Design Principles**

### ✔ Edge-first

All analysis is local; sensitive bodies are never stored or transmitted externally.

### ✔ Two operational modes

1. **Async Content Detectors**

    * Run off the hot path
    * Safe for low-latency workloads
    * Suitable for analytics, learning, enrichment

2. **Blocking Content Detectors**

    * Only enabled for:

        * high-risk detections
        * sensitive endpoints
        * known attack surfaces
    * Inspect content inline *before* it reaches the client
    * May drop, scrub, mask, or rewrite content

### ✔ Privacy by design

* Never persist raw bodies
* Only extract symbolic “pattern hits”
* Optionally hash body templates

### ✔ Configurable and deterministic

* Patterns via YAML or per-tenant policy
* Bounded scoring
* Zero introspection of user PII

---

# 3. **Scope**

### Included

* Short-body pattern detection
* Error-page recognition
* Honeypot content detection
* Debug/stack-trace detection
* Abuse feedback loop detection
* Inline scrubbing/masking for blocking mode
* Integration with Ephemeral and Response detection

### Excluded

* Full-text indexing
* Full LLM inference on bodies (that’s a separate enterprise addon)
* DPI-level TLS interception
* Non-HTTP content (initially)

---

# 4. **Pipeline Position**

```
Client → YARP + Stylobot → Backend → Response → Content Detection Contributor → Client
                       ↘ async detectors (Ephemeral)
```

### Blocking detectors:

* Inserted in the YARP response pipeline
* Operate *after* backend response but *before* sending to the client
* Allowed to inspect up to `maxBufferBytes`

### Async detectors:

* Run as Ephemeral tasks
* Operate only on metadata + a limited body slice
* Contribute to scoring but do not affect the response

---

# 5. **Inputs**

The contributor receives a **ContentSignal**, produced by YARP response transforms:

```csharp
public sealed record ContentSignal(
    string RequestId,
    string ClientId,          // IP + fingerprint/UA
    DateTimeOffset Timestamp,
    int StatusCode,
    string Path,
    string Method,
    long ResponseBytes,
    byte[] BodyPreview,       // <= maxPreviewBytes (safe slice)
    IReadOnlyList<string> MatchedPatterns, // names only
    string? TemplateHash      // optional SHA-256 of canonicalised body
);
```

* `BodyPreview` is capped (e.g., 8 KB or less).
* `MatchedPatterns` come from pattern sets (see config).
* `TemplateHash` is optional and is used for “same error page repeatedly” detection without storing content.

---

# 6. **Feature Extraction**

The Content Contributor produces **features** combined into a raw score.

### 6.1 Error Template Recognition

Patterns such as:

* “An unexpected error occurred”
* “Stack trace”, “Traceback”
* “NullReferenceException”
* SQL error messages
* ASP.NET / PHP / Node error templates
* Known CMS error templates

Feature: `error_template_score`

### 6.2 Debug Leakage Detection

Indicators:

* stack traces
* variable dump markers
* development banners (“Debug True”, “Development Mode”)

Feature: `debug_leakage_score`

### 6.3 Abuse Feedback Loop Patterns

Repeated body content indicating abuse:

* “Too many requests”
* “You have been rate limited”
* “Your IP has been blocked”
* “Invalid password”
* “Try again in X seconds”

Feature: `abuse_feedback_score`

### 6.4 Honeypot Content Detection

If body matches honey-content patterns:

* decoy admin pages
* fake login pages
* synthetic error templates

Feature: `honeypot_content_score` (strong positive)

### 6.5 Content-Type and Size Anomalies

* Unexpected MIME type transitions
* Repeated tiny responses for odd paths
* Suddenly massive responses in probing patterns
* JSON/XML structures in unexpected endpoints

Feature: `content_shape_score`

### 6.6 Template Hash Frequency

Repeated identical error page to same actor or group.

Feature: `template_repeat_score`

---

# 7. **Scoring Model**

The detector outputs:

```csharp
new DetectorScore(
    Name: "ContentContributor",
    Score: rawScore,   // -1..+1
    Weight: policyWeight,
    Notes: "Matched debug template | repeated 404 error-body"
);
```

### Raw score construction (simplified):

```
rawScore =
    w1 * error_template_score +
    w2 * debug_leakage_score +
    w3 * abuse_feedback_score +
    w4 * honeypot_content_score +
    w5 * content_shape_score +
    w6 * template_repeat_score
```

All weights configurable.

---

# 8. **Blocking Detector Mode**

### When enabled?

Blocking triggers if:

* `riskBand >= High` (from *request + response detectors*)
* OR content-path is marked *sensitive*
* OR signature-specific blocking rule fires
* OR downstream sanitiser requires inspection

### Blocking constraints

* Max `maxBlockingDurationMs` → cancel if exceeded
* Max body buffer `maxBufferBytes`
* Only pattern matches + hashing allowed
* No storage of raw body beyond request lifetime

### Actions allowed in blocking mode:

* **Allow** (clean)
* **Mask** (strip error info / debug details)
* **Block** response
* **Redirect to challenge**
* **Return honeypot content** (if configured)

---

# 9. **Configuration Model**

`content_contributor.yaml` (shared by Free + Enterprise with override layers):

```yaml
enabled: true

operational:
  maxPreviewBytes: 8192
  maxBufferBytes: 65536
  maxBlockingDurationMs: 20
  asyncMode: true
  blockingMode:
    enabled: true
    sensitivePaths:
      - "/login"
      - "/admin"
      - "/register"
      - "/api/payments"

weights:
  base: 0.7
  features:
    error_template: 0.35
    debug_leakage: 0.4
    abuse_feedback: 0.25
    honeypot_content: 0.8
    content_shape: 0.2
    template_repeat: 0.15

patterns:
  error_templates:
    - name: "generic_error"
      pattern: "An unexpected error occurred"
    - name: "stack_trace"
      pattern: "Exception|Traceback"
    - name: "sql_error"
      pattern: "SQLSTATE|syntax error near"

  abuse_feedback:
    - name: "rate_limited"
      pattern: "Too many requests|Rate limit exceeded"
    - name: "invalid_login"
      pattern: "Invalid username or password"

  honeypot_content:
    - name: "fake_admin"
      pattern: "<title>Admin Panel</title>"

  debug_markers:
    - name: "debug_banner"
      pattern: "Debug mode|Development environment"
```

### Enterprise overrides:

* Per-tenant patterns
* Per-tenant sensitive paths
* Per-tenant feature weights
* Per-tenant template hashing enable/disable

---

# 10. **Safety & Privacy**

* Raw bodies are never logged or stored.
* Only predefined pattern names are retained.
* Optional hashing is SHA-256 of canonicalised body (removes PII).
* Blocking mode enforces strict timeouts so detector cannot stall traffic.
* Response sizes capped before analysis.
* No LLM inference unless explicitly configured (enterprise only).

---

# 11. **Integration with Stylobot Pipeline**

### In Detect → Score:

* Response Detection Contributor (codes/headers)
* **Content Detection Contributor (content)**
* Behavioral (timing)
* Heuristic
* Reputation
* Client fingerprint
* Etc.

All detectors feed into the **risk model** and help classify:

* isBot
* humanProbability / botProbability
* recommendedAction
* confidence

### In Dashboard:

Enterprise will see:

```json
"contentContributor": {
  "patternHits": ["stack_trace", "rate_limited"],
  "templateRepeat": 3,
  "rawScore": 0.62
}
```

Free gets only the final detector score and a generic explanation.

---

# 12. **Test Cases**

### Case A — Debug-leak from backend

Bodies containing stack-trace text.

Expected:

* `debug_leakage_score` high
* riskBand bumps at least +1
* blocking mode masks body if configured

### Case B — 404 exploration bot

Bodies match common CMS error pages repeatedly.

Expected:

* Low-weight positive score (ties into ResponseContributor)

### Case C — Honeypot content hit

Bot lands on fake admin.

Expected:

* Score strongly positive
* recommendedAction = Block

### Case D — Normal user

Nearly all content clean patterns.

Expected:

* Score ~0 or slightly human

---

# **Summary**

The **Content Detection Contributor** adds deep semantic inspection to your pipeline:

* lightweight
* safe
* privacy-focused
* asynchronous by default
* optionally blocking in high-risk paths
* fully configurable
* fits perfectly with your Ephemeral + signature-based detection model

It completes the “response side” of detection and enables advanced behavioural and semantic analysis without breaking
TLS or adding high latency.

---

If you want, I can now write:

* The **YARP integration spec** (response transform pipeline with async + blocking paths)
* The **Ephemeral signal spec** for content detection
* The **signature integration spec** (how detectors contribute weights per signature)

Just tell me which part you want next.
