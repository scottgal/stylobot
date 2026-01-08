Here we go — a **Content Inspector** spec that plugs cleanly into your existing semantic firewall / botdetection /
gateway ideas, with as many detectors & “contributors” as I can cram in without turning it into an RFC.

---

# Content Inspector – Feature Spec

## 1. Purpose

Introduce a **Content Inspector** component at the proxy/gateway layer that:

* Inspects **request and response bodies** (within size / type limits).
* Runs a set of **pluggable detectors** to extract semantic and structural signals.
* Emits **features** and **flags** that:

    * feed your heuristic / clustering,
    * drive routing (forensics, honeypot, throttle, cache policy),
    * assist compliance (PII, secrets, risky content),
    * support future RAG / analytics.

The inspector is purely **observational + feature-extracting** in this spec. Rewriting / blocking is done by separate
policy layers that consume these features.

---

## 2. Scope / Non-Goals

### In scope

* Request + response body inspection.
* Structural & semantic detectors for:

    * formats (JSON, HTML, GraphQL, SQL-like, XML, etc.)
    * PII & secrets
    * credentials / tokens
    * exploit / injection patterns
    * spam/scam/abuse-ish content
    * protocol-specific structures (GraphQL, REST JSON, form posts)
* Aggregation into `ContentSignals` that are attached to:

    * the existing `TrafficSegment`,
    * the `DetectionResult` / blackboard.

### Out of scope (this feature)

* Actually **blocking** based on content (done by semantic firewall rules).
* Deep ML pipelines (can be added as an async analyzer later).
* Full DLP product behaviour (we only flag risk; what to do with it is policy).

---

## 3. Architecture Overview

```mermaid
flowchart TB
    subgraph Gateway Pipeline
        A[Incoming Request] --> B[Request Metadata Detectors]
        B --> C[Content Tap (Request)]
        C --> D[Proxy to Upstream]
        D --> E[Content Tap (Response)]
        E --> F[Response Detectors + Heuristic]
    end

    subgraph Content Inspector
        C --> G[ContentInspector<br/>(Request)]
        E --> H[ContentInspector<br/>(Response)]
        G --> I[ContentSignals (Request)]
        H --> J[ContentSignals (Response)]
    end

    I --> F
    J --> F
```

### Components

* **Content Tap Middleware**

    * Buffers body (up to a limit), normalises content-type, hands bytes to inspector.

* **ContentInspector**

    * Orchestrator that runs multiple **detectors**.
    * Aggregates their results into a single `ContentSignals`.

* **Detectors / Contributors**

    * Small, focused analyzers (format, PII, injection, spam, etc).
    * Emit “signals” (features + flags).

* **Downstream Consumers**

    * Heuristic / cluster model.
    * Forensics / honeypot routing.
    * Auto-throttle / cache policies.
    * Analytics / dashboards.

---

## 4. Core Interfaces & Data Models

### 4.1 Direction & Envelope

```csharp
public enum ContentDirection
{
    Request,
    Response
}

public sealed record ContentEnvelope(
    ContentDirection Direction,
    TrafficSegment Segment,      // method + route + policy + etc
    string? ContentType,         // e.g. "application/json"
    string? Charset,             // e.g. "utf-8"
    long OriginalLengthBytes,
    ReadOnlyMemory<byte> Body,
    IReadOnlyDictionary<string, string> Headers);
```

### 4.2 Inspector Interface

```csharp
public interface IContentInspector
{
    Task<ContentSignals> InspectAsync(
        ContentEnvelope envelope,
        CancellationToken ct = default);
}
```

### 4.3 Detector / Contributor Interface

```csharp
public interface IContentDetector
{
    string Name { get; }

    Task<ContentDetectorResult> AnalyzeAsync(
        ContentEnvelope envelope,
        CancellationToken ct = default);
}

public sealed record ContentDetectorResult(
    string DetectorName,
    IReadOnlyDictionary<string, object?> Features,
    IReadOnlyList<string> Flags,        // e.g. ["sql_injection_pattern", "contains_card_number"]
    double SuspicionScore               // 0.0–1.0 (detector-local)
);
```

The `Features` are merged into a flat feature-map / blackboard; `Flags` are used for triggers & diagnostics.

### 4.4 Aggregated Signals

```csharp
public sealed record ContentSignals
{
    // Meta
    public bool Inspected { get; init; }
    public bool Truncated { get; init; }
    public string? NormalisedMimeType { get; init; }    // "json", "html", "text", "graphql", "sql", "xml", "binary"

    // Language & text
    public string? DetectedLanguage { get; init; }      // "en", "fr", etc
    public double? LanguageConfidence { get; init; }    // 0–1

    // Risk scores (aggregated)
    public double PiiScore { get; init; }               // 0–1
    public double SecretScore { get; init; }            // 0–1
    public double InjectionScore { get; init; }         // 0–1
    public double AbuseContentScore { get; init; }      // 0–1
    public double SpamScore { get; init; }              // 0–1

    // Booleans for quick checks
    public bool ContainsCredentials { get; init; }
    public bool ContainsAuthTokens { get; init; }
    public bool ContainsPaymentData { get; init; }
    public bool ContainsSqlLikePatterns { get; init; }
    public bool ContainsTemplateInjectionPatterns { get; init; }
    public bool ContainsXssLikePatterns { get; init; }
    public bool ContainsPathTraversalPatterns { get; init; }
    public bool ContainsSuspiciousUrls { get; init; }
    public bool AppearsEncryptedOrRandom { get; init; }

    // Structural
    public bool IsJson { get; init; }
    public bool IsHtml { get; init; }
    public bool IsXml { get; init; }
    public bool IsGraphql { get; init; }
    public bool IsSqlLike { get; init; }
    public bool IsFormUrlEncoded { get; init; }
    public bool IsMultipartFormData { get; init; }

    // Summaries / tags from detectors
    public IReadOnlyList<string> Tags { get; init; }          // ["login_payload", "graphql_query", "file_upload"]
    public IReadOnlyList<string> Flags { get; init; }         // aggregated detector flags

    // Optional summarised view for forensics / RAG
    public string? CompactSummary { get; init; }              // short ≤ 512 chars
    public IReadOnlyDictionary<string, object?> RawFeatures { get; init; } // merged feature map
}
```

---

## 5. Detector Categories & Contributors

Below: all the detector types that make sense here. Each is one implementation of `IContentDetector`.

### 5.1 Structural / Format Detector

**Name:** `FormatDetector`

**Goal:** Determine basic content shape.

**Responsibilities:**

* Inspect `Content-Type` and sniff first bytes.
* Set flags / features:

    * `content.is_json`, `content.is_html`, `content.is_xml`, `content.is_graphql`, `content.is_sql_like`,
      `content.is_text`, `content.is_binary`.
* Detect GraphQL:

    * `{ query: "...", variables: {...} }`
    * or raw `query { ... }` POST bodies.
* Detect SQL-like:

    * presence of `SELECT`, `INSERT`, `UNION`, etc. in certain patterns.
* Detect form posts:

    * `application/x-www-form-urlencoded`
    * `multipart/form-data` (file uploads).

**Features example:**

```json
{
  "content.format": "json",
  "content.is_graphql": false,
  "content.is_form_urlencoded": true
}
```

---

### 5.2 Language & Text Detector

**Name:** `LanguageDetector`

**Goal:** Identify natural language and its likelihood.

**Responsibilities:**

* Apply lightweight language detection on textual content.
* Fill:

    * `content.language` (ISO code),
    * `content.language_confidence`.

**Use:**

* Normalising messages for heuristics / RAG.
* Tailoring honeypot responses or TTS/IVR flows.

---

### 5.3 PII Detector

**Name:** `PiiDetector`

**Goal:** Roughly estimate presence of personally identifiable information.

**Responsibilities:**

* Regex + heuristic detection of:

    * emails, phone numbers, full names patterns,
    * addresses / postcodes,
    * national identifiers (configurable per region),
    * date-of-birth-like patterns (YYYY-MM-DD near “dob”, etc.).
* Aggregate into `PiiScore`:

    * simple scoring: 0–1 based on count & strength.

**Features:**

```json
{
  "pii.email_count": 3,
  "pii.phone_count": 1,
  "pii.score": 0.7
}
```

---

### 5.4 Secrets / Token Detector

**Name:** `SecretDetector`

**Goal:** Detect things that look like credentials / secrets.

**Responsibilities:**

* Identify:

    * API keys (prefix+length patterns),
    * JWTs (header.payload.signature),
    * OAuth tokens,
    * bearer tokens (`Authorization` headers in body),
    * high-entropy strings likely to be secrets.
* Use entropy-based heuristic:

    * `secret.high_entropy_strings` count,
    * simple classification: random-looking vs human text.

**Features:**

```json
{
  "secret.jwt_count": 2,
  "secret.high_entropy_count": 4,
  "secret.score": 0.8
}
```

---

### 5.5 Credential & Auth Payload Detector

**Name:** `CredentialPayloadDetector`

**Goal:** Identify login / registration / auth payloads.

**Responsibilities:**

* Look for fields:

    * `username`, `user`, `email`, `login`,
    * `password`, `pwd`, `pass`, `otp`, `code`.
* Tag:

    * `content.tags += "login_payload"`,
    * `content.contains_credentials = true`.

**Use:**

* Extra sensitive for forensics,
* Distinguishing credential stuffing from normal traffic.

---

### 5.6 Payment / Financial Detector

**Name:** `PaymentDetector`

**Goal:** Spot sensitive payment information.

**Responsibilities:**

* Detect:

    * card numbers (Luhn) + known BIN ranges,
    * CVV/CVC patterns,
    * expiry dates near card number,
    * IBAN / sort code / account number patterns.
* Score `ContainsPaymentData` and `PiiScore` bump.

**Features:**

```json
{
  "payment.card_count": 1,
  "payment.score": 0.9
}
```

---

### 5.7 Injection / Exploit Pattern Detector

**Name:** `InjectionDetector`

**Goal:** Catch common injection / exploit shapes.

**Responsibilities:**

* SQL Injection-ish patterns:

    * `' OR 1=1`, `UNION SELECT`, `--`, `/* */` misuse.
* Command injection:

    * `; rm -rf`, `&&`, `||`, shell meta on suspicious routes.
* Template injection:

    * `{{...}}`, `${...}`, `#{...}`, `@{...}`, on template-ish fields.
* Path traversal:

    * `../`, `..\`, `%2e%2e/` etc in file/path fields.
* XSS-ish:

    * `<script>`, `javascript:`, `onerror=`, `<img src=x onerror=...` etc.

**Features:**

```json
{
  "inj.sql_pattern_count": 2,
  "inj.template_pattern_count": 1,
  "inj.path_traversal": true,
  "inj.score": 0.85
}
```

---

### 5.8 URL / Link Detector

**Name:** `UrlDetector`

**Goal:** Extract and classify URLs.

**Responsibilities:**

* Extract URLs from text/HTML/JSON values.
* Classify:

    * internal vs external,
    * suspicious TLDs / punycode,
    * odd query params (e.g. track, ref, token).
* Flag:

    * `content.contains_suspicious_urls`.

---

### 5.9 HTML / DOM Pattern Detector

**Name:** `HtmlPatternDetector`

**Goal:** Understand basic HTML semantics.

**Responsibilities:**

* When `IsHtml`:

    * parse minimally,
    * detect:

        * forms that POST credentials,
        * inline scripts,
        * hidden fields carrying weird tokens,
        * iframes to external origins.

**Use:**

* Distinguish normal HTML response vs phishing-ish, injection surfaces, etc.

---

### 5.10 JSON Shape & Field Detector

**Name:** `JsonShapeDetector`

**Goal:** Extract field-level information from JSON payloads.

**Responsibilities:**

* Parse JSON into a shallow shape:

    * top-level keys,
    * nested keys (limited depth),
    * array lengths for key arrays.
* Tag typical domains:

    * `"email", "name", "roles", "permissions", "price", "sku", "token"`.
* Provide:

    * `json.field_count`,
    * `json.array_lengths`,
    * `json.contains_fields` list.

This is great for:
cluster features, RAG indexing, per-route behavioural baselines.

---

### 5.11 GraphQL Detector

**Name:** `GraphqlDetector`

**Goal:** Recognise GraphQL operations and their style.

**Responsibilities:**

* Detect:

    * `query`, `mutation`, `subscription`,
    * introspection queries (`__schema`, `__type`),
    * large deeply nested queries (possible abuse / scraping).
* Features:

    * `graphql.operation_type`,
    * `graphql.field_count`,
    * `graphql.introspection` bool.

2nd-order use: “Graphy” bots vs normal frontends.

---

### 5.12 SQL Payload Detector (Content)

**Name:** `SqlPayloadDetector`

**Goal:** For pure SQL-ish payloads.

**Responsibilities:**

* If body → **entirely SQL-like**:

    * treat as DB tool / API usage vs “weird”.
* Features:

    * `sql.statement_count`,
    * `sql.has_ddl`, `sql.has_dml`,
    * `sql.score`.

Could feed a “SQL gateway” mode / data exfil detection later.

---

### 5.13 File Upload & MIME Detector

**Name:** `FileUploadDetector`

**Goal:** Inspect multipart/form-data boundaries and file metadata.

**Responsibilities:**

* Detect file uploads:

    * filenames, content types, approximate sizes.
* Tag:

    * `content.tags += "file_upload"`,
    * `file.has_executable_mime`, `file.has_script_mime` etc.

---

### 5.14 Entropy / Compression / Encryption Detector

**Name:** `EntropyDetector`

**Goal:** Spot encrypted/packed/random blobs.

**Responsibilities:**

* Compute Shannon entropy on chunks.
* Flag:

    * `AppearsEncryptedOrRandom = true` on suspicious high entropy, long sequences.

Useful for:

* “Unexpected encrypted blobs” on routes that shouldn’t see them.

---

### 5.15 Abuse / Toxic Content Detector (Optional)

**Name:** `AbuseContentDetector`

**Goal:** Rough detection of abusive/hateful/offensive content (for messaging / comments endpoints).

**Responsibilities:**

* Rule-based / small-model heuristics (no need for full frontier LLM here by default).
* Compute `AbuseContentScore` 0–1.

Use:

* Additional features for fraud / scam classification.
* Enterprise compliance.

---

### 5.16 Spam / Scam Pattern Detector

**Name:** `SpamDetector`

**Goal:** Spot spammy / scammy text patterns.

**Responsibilities:**

* Look for:

    * classic spam phrases (“work from home”, “urgent action required”, etc.),
    * phishing templates (“your account has been locked”, etc.),
    * suspicious link density.
* Compute `SpamScore`.

Could later be augmented by a small model, but start heuristic.

---

### 5.17 “Content Role” Classifier (Very Simple)

**Name:** `ContentRoleDetector`

**Goal:** Classify rough “type” of payload:

* login_payload
* registration_payload
* order_payload
* search_query
* message / comment
* admin_action
* log-like

**Implementation:**

* Use segment + JSON shape + field names to heuristically assign tags.

---

### 5.18 Semantic Summariser (Optional, Async)

**Name:** `SemanticSummaryDetector`

**Goal:** Produce a short, privacy-safe summary for forensics & RAG.

**Responsibilities:**

* Only used on:

    * forensics-flagged events,
    * or opt-in routes / high-value segments.
* Use LLM / translator to produce:

    * short natural language summary,
    * possibly labelled with “kind of behaviour / action”.

Stored in `CompactSummary`.

---

## 6. Aggregation & Weighting

The `ContentInspector`:

1. Invokes detectors (probably in parallel with sensible timeouts).
2. Merges their `Features` into a **single feature map** (key-prefixed by detector).
3. Aggregates scores:

    * `PiiScore` = weighted max/mean of PII + Payment + Credential detectors.
    * `SecretScore` = from SecretDetector + EntropyDetector.
    * `InjectionScore` = from InjectionDetector + SqlPayloadDetector.
    * `AbuseContentScore` = from AbuseContentDetector.
    * `SpamScore` = from SpamDetector.
4. Unions all `Flags` and `Tags`.
5. Outputs a `ContentSignals` instance attached to the request/response context.

These features then:

* Become part of the heuristic’s feature vector.
* Are visible in debug JSON and dashboard.
* Feed forensics triggers & honeypot decisions.

---

## 7. Configuration

Top-level config idea:

```json
"ContentInspection": {
  "Enabled": true,
  "MaxBodyBytes": 65536,
  "InspectRequests": true,
  "InspectResponses": true,

  "EnabledDetectors": [
    "FormatDetector",
    "LanguageDetector",
    "PiiDetector",
    "SecretDetector",
    "CredentialPayloadDetector",
    "PaymentDetector",
    "InjectionDetector",
    "UrlDetector",
    "JsonShapeDetector",
    "GraphqlDetector",
    "FileUploadDetector",
    "EntropyDetector",
    "AbuseContentDetector",
    "SpamDetector",
    "ContentRoleDetector"
  ],

  "ExcludedContentTypes": [ "image/", "video/", "application/zip" ],
  "RedactFields": [ "password", "authorization", "cardNumber", "cvv", "ssn" ],
  "SemanticSummary": {
    "Enabled": false,
    "MaxChars": 512
  }
}
```

---

## 8. Privacy & Safety Guardrails

* **Size limits:** Only inspect up to `MaxBodyBytes`.
* **Type limits:** Skip obviously binary content by default.
* **Redaction:** Before anything touches logs / forensics:

    * redact configured field names,
    * optionally hash identifiers.
* **Forensics:** Store only summaries & structured signals where possible; raw content only in strict, short-retention
  stores.
* **Configurable:** Make it trivially easy to **turn off** heavy or sensitive detectors per route / segment.

---

If you want, next step I can:

* turn this into actual **C# skeleton code** (interfaces + a couple of sample detectors), or
* draft a **policy language** for “if content signals X + risk Y ⇒ route to Z / honeypot / forensics”.
