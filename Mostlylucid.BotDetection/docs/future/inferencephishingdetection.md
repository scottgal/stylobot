# Feature Spec: Inference-Based Phishing Detection Using LLMs

## 1. Overview

This feature adds **inference-based phishing detection** to the Mostlylucid.YarpGateway stack by:

* Capturing signals from:

    * Referrer headers
    * Honeypot credential attempts
    * PII detector
    * HaveIBeenPwned (HIBP) context
    * Existing bot/genome signals
* Feeding them into a **fast LLM categorizer** that infers whether the traffic is:

    * Benign
    * Generic bot
    * Credential stuffing
    * Phishing-driven credential stuffing
    * Targeted/manual phishing
* Emitting a **structured “PhishingInference” signal** back into the genome and policy engine.
* Optionally storing enriched “phishing events” for **RAG-based long-term analysis** and campaign naming.

The system remains **purely defensive** and privacy-aware:

* No passwords stored in plaintext.
* Sensitive data hashed or redacted.
* HIBP used only in intended ways (breach/password checks).

---

## 2. Goals and Non-Goals

### 2.1 Goals

* Detect likely phishing-origin login attempts based on:

    * Referrer analysis
    * Credential stuffing patterns
    * PII presence and structure
    * Breach context (HIBP)
    * Existing risk/genome signals
* Provide a **single high-level “phishing inference” contribution** into the genome:

    * `phishing.isLikelyPhishing`
    * `phishing.strength`
    * `phishing.attackFamily` (optional, if recognizable pattern)
* Enable:

    * Stricter policies for suspected phishing campaigns
    * Better long-term reporting and clustering of phishing campaigns
* Use a fast LLM as a **categorizer**, not as an exploit generator.

### 2.2 Non-Goals

* Do not generate exploits, payloads, or POC code.
* Do not actively probe or attack external systems.
* Do not store or reuse raw credentials for offensive purposes.
* Do not depend on the LLM for hard blocking decisions (LLM is advisory; policies remain rule-based).

---

## 3. High-Level Architecture

```mermaid
flowchart LR
    Req[Incoming Request] --> Detect[Existing Detection Pipeline]
    Detect -->|Signals, Genome| Honeypot{Honeypot Route?}
    Honeypot -->|No| Policy[Normal Action Policies]
    Honeypot -->|Yes| Capture[Capture Attempted Credentials + PII]

    Capture --> Ref[Referrer Analyzer]
    Ref --> HIBP[HIBP Enrichment]
    HIBP --> Agg[Phishing Signal Aggregator]
    Detect --> Agg

    Agg --> LLMReq[Build LLM Categorizer Request]
    LLMReq --> LLM[Fast LLM Categorizer]
    LLM --> LLMResp[Phishing Inference Result]
    LLMResp --> Genome[Add PhishingInference Contribution]
    Genome --> Policy[Action Policies]

    Agg --> Store[Long-Term Phishing Event Store (optional)]
    Store --> RAG[RAG / Reporting / Campaign Naming]
```

---

## 4. Components

### 4.1 Referrer Analyzer

**Responsibility:** Extract and classify properties of the HTTP `Referer` header.

Inputs:

* Raw `Referer` header (if present).
* Gateway’s own primary domains (from config).

Outputs (signals):

```json
{
  "referrer.present": true,
  "referrer.raw": "https://mail.google.com/mail/u/0/#inbox/…",
  "referrer.domain": "mail.google.com",
  "referrer.registrableDomain": "google.com",
  "referrer.type": "webmail|shortener|direct|tracker|other",
  "referrer.isShortener": false,
  "referrer.isWebmail": true,
  "referrer.domainSimilarityToSite": 0.82,
  "referrer.suspectQueryParams": true,
  "referrer.hasCampaignParams": true
}
```

Heuristics:

* Detect **shorteners**: `bit.ly`, `t.co`, `tinyurl.com`, `lnkd.in`, etc. (configurable list).
* Detect **webmail**: `mail.google.com`, `outlook.live.com`, `mail.yahoo.com`, etc. (configurable list).
* Detect **tracking/redirectors**: `click.*`, `trk.*`, `link.*` patterns.
* Compute **domain similarity** to gateway’s canonical domains (Levenshtein/Jaro/substring-based).
* Flag suspicious query params:

    * `email=`, `user=`, `account=`, `login=`, base64-like blobs, etc.

Detector output in `contributions`:

```json
{
  "detector": "Referrer",
  "category": "Referrer",
  "impact": 0.0,
  "weight": 1.0,
  "reason": "Referrer analyzed; see referrer.* signals",
  "signals": { /* as above */ }
}
```

Impact can be zero here; inference happens in the LLM-based detector.

---

### 4.2 Honeypot Credential Capture

**Responsibility:** Safely capture and transform credentials submitted to **honeypot-auth endpoints**.

Triggered when:

* An ActionPolicy routes a request to an `auth-honeypot` cluster (e.g., for `/login`, `/signin`, `/api/auth/*`).

Captured fields (before transformation):

* Username / identifier fields (e.g., `username`, `email`, `login`).
* Password fields (e.g., `password`, `pass`, `pwd`).
* Entire request body (for PII scanning only; not all stored).

Transformations:

* Username:

    * Store either:

        * full email (for HIBP email checks), OR
        * partially redacted (e.g., local-part hashed, domain retained).
    * Configurable via `HoneypotCredentials.UsernameStorageMode`:

        * `FullEmail`
        * `RedactedLocalPart`
        * `HashedEmail`
* Password:

    * Never store plaintext.
    * Immediately compute:

        * Internal hash/HMAC: `HMAC_SHA256(password, internalKey)`
        * SHA-1 for HIBP Pwned Passwords k-Anonymity check.
    * Store only hashes and HIBP counts.

Example stored credential object (for detectors and event store):

```json
{
  "username": "user@example.com",
  "usernameRedacted": "****@example.com",
  "passwordHashInternal": "hmac256:...",
  "passwordHashSha1": "A94A8FE5CCB19BA61C4C0873D391E987982FBBD3",
  "source": "HoneypotLogin",
  "loginPath": "/login",
  "timestamp": "2025-12-04T16:05:23Z"
}
```

---

### 4.3 PII Detection Integration

**Responsibility:** Classify and scrub PII in honeypot contexts and relevant real endpoints.

Inputs:

* Raw request body / captured fields (username, password, possibly query/form values).

Outputs (signals):

```json
{
  "pii.hasEmail": true,
  "pii.hasPassword": true,
  "pii.hasOtherSensitive": false,
  "pii.classification": "LikelyCredentialAttempt",
  "pii.severity": "High"
}
```

Behavior:

* Run **after** credential capture, but **before** long-term storage.
* Ensure role:

    * Identify if the request contains credential-like PII.
    * Drive severity/risk in phishing inference.

---

### 4.4 HIBP Integration

**Responsibility:** Enrich honeypot credential attempts with breach context.

Subcomponents:

1. **Email Breach Check** (HIBP Breaches API):

    * Input: email (or hashed/obscured representation, depending on API constraints).
    * Output signals:

      ```json
      {
        "hibp.emailChecked": true,
        "hibp.emailBreaches": 3,
        "hibp.emailLastBreach": "2023-09-14"
      }
      ```

2. **Password Pwned Check** (HIBP Pwned Passwords via k-Anonymity):

    * Input: first 5 chars of SHA-1 hash.
    * Output signals:

      ```json
      {
        "hibp.passwordChecked": true,
        "hibp.passwordPwned": true,
        "hibp.passwordPwnedCount": 15729
      }
      ```

Behaviour:

* Only run in honeypot or configured high-risk contexts.
* Rate-limited; API key protected.
* Never store full HIBP payloads; only counts/booleans.

---

### 4.5 Credential Stuffing Detector (Existing / Extended)

**Responsibility:** Detect credential stuffing behaviour using captured credential hashes and honeypot events.

Inputs:

* Internal password hash.
* Username/email pattern.
* Historical honeypot attempts (in-memory or DB-backed).

Signals:

```json
{
  "honeypot.matchCountByPassword": 300,
  "honeypot.matchCountByTuple": 42,
  "honeypot.firstSeen": "2025-12-01T09:00:00Z",
  "honeypot.lastSeen": "2025-12-04T16:05:23Z"
}
```

Contribution:

```json
{
  "detector": "CredentialStuffing",
  "category": "Honeypot",
  "impact": 1.8,
  "weight": 3.0,
  "reason": "Same password hash used across 300 honeypot login attempts for different usernames",
  "signals": {
    "honeypot.matchCountByPassword": 300
  }
}
```

---

### 4.6 LLM-Based Phishing Inference Service

**Responsibility:** Take a compact feature bundle and infer phishing likelihood and type.

#### 4.6.1 Inputs (Feature Bundle)

A synthesized JSON feature object, *not* raw request:

```json
{
  "request": {
    "method": "POST",
    "path": "/login",
    "endpointType": "Auth",
    "timestamp": "2025-12-04T16:05:23Z"
  },
  "risk": {
    "overallRiskScore": 0.92,
    "riskBand": "High"
  },
  "referrer": {
    "present": true,
    "domain": "bit.ly",
    "registrableDomain": "bit.ly",
    "type": "shortener",
    "domainSimilarityToSite": 0.15,
    "isWebmail": false,
    "isShortener": true,
    "suspectQueryParams": true
  },
  "pii": {
    "hasEmail": true,
    "hasPassword": true,
    "classification": "LikelyCredentialAttempt",
    "severity": "High"
  },
  "hibp": {
    "emailBreaches": 3,
    "passwordPwned": true,
    "passwordPwnedCount": 15729
  },
  "stuffing": {
    "matchCountByPassword": 300,
    "matchCountByTuple": 42
  },
  "genomeSummary": {
    "detectedBotType": "Scraper",
    "actionPolicy": "honeypot-credentials",
    "signalsOfNote": [
      "High bot likelihood by heuristic",
      "LLM predicted bot",
      "Honeypot route triggered"
    ]
  }
}
```

#### 4.6.2 LLM Prompt (Conceptual)

System prompt (conceptual):

> You are a security analyst. You receive summarized features about a single login attempt and its context.
> Your job is to infer whether this attempt is likely:
>
> * benign
> * generic bot noise
> * credential stuffing (reuse of breached credentials)
> * phishing-driven credential entry (user followed a phishing link and entered credentials)
> * targeted/manual phishing.
    >   Only use the provided fields. Do not invent technical details. Return a strict JSON object.

User content: the feature bundle.

Expected response JSON:

```json
{
  "phishingLikely": true,
  "phishingStrength": "High",
  "phishingType": "PhishingCredentialStuffing",
  "shortLabel": "Phishing-based credential stuffing from shortener link",
  "explanation": "Login attempt to /login carried both email and password, came from a URL shortener, used a password seen in public breaches, and matches a pattern of 300 similar honeypot attempts. This suggests a credential stuffing campaign where users click phishing links.",
  "attackFamilyHint": "ShortenerPhishStuffing-2025-12",
  "confidence": 0.91
}
```

The feature bundle is deliberately compact and non-sensitive. No raw passwords, minimal PII, no referrer query body
needed in most cases.

#### 4.6.3 Implementation Details

* Use a **fast, small LLM** (e.g. `gemma3:4b` or similar) exposed via existing LLM infra.
* Strict JSON mode (with validation and fallback if parsing fails).
* Timeouts: configurable (e.g. 50–100ms budget per call; fallback to “no inference” on timeout).
* Only invoked when:

    * endpoint is auth-like OR
    * there are strong credential/PII signals OR
    * risk score already high.

---

### 4.7 Phishing Inference Detector

**Responsibility:** Wrap LLM result into a genome contribution and normalized signals.

Outputs:

Signals:

```json
{
  "phishing.inferenceRan": true,
  "phishing.likely": true,
  "phishing.strength": "High",
  "phishing.type": "PhishingCredentialStuffing",
  "phishing.attackFamilyHint": "ShortenerPhishStuffing-2025-12",
  "phishing.confidence": 0.91
}
```

Contribution:

```json
{
  "detector": "PhishingInference",
  "category": "Phishing",
  "impact": 1.5,
  "weight": 3.0,
  "reason": "LLM-based phishing inference: High likelihood of phishing-based credential stuffing",
  "signals": {
    "phishing.likely": true,
    "phishing.strength": "High",
    "phishing.type": "PhishingCredentialStuffing",
    "phishing.attackFamilyHint": "ShortenerPhishStuffing-2025-12",
    "phishing.confidence": 0.91
  }
}
```

Impact + weight tuned to ensure phishing inference significantly influences risk when confidence is high.

---

### 4.8 Policy Engine Integration

Extend `DetectionPolicies` and `ActionPolicies` to account for `phishing.*` signals.

Example:

```json
"DetectionPolicies": {
  "auth-endpoints": {
    "Description": "Authentication endpoints with phishing-aware routing",
    "UseFastPath": true,
    "ActionPolicyName": "allow",
    "Transitions": [
      { "WhenRiskExceeds": 0.8, "ActionPolicyName": "honeypot-credentials" },
      { "WhenSignal": "phishing.likely", "ActionPolicyName": "phishing-challenge-or-block" }
    ]
  }
},
"ActionPolicies": {
  "phishing-challenge-or-block": {
    "Type": "ChallengeOrBlock",
    "ChallengeType": "CaptchaOr2FA",
    "BlockThreshold": 0.9
  }
}
```

Policy examples:

* If `phishing.likely == true` and `phishing.confidence > 0.85` → force challenge or soft-block.
* If `phishing.type == "PhishingCredentialStuffing"` → always route to honeypot until thresholds reached, then block.
* If phishing inference is low confidence → use it as a weak signal only.

---

### 4.9 Long-Term Phishing Event Store (Optional)

**Responsibility:** Store enriched phishing-related events for RAG and reporting.

Schema (conceptual):

```json
{
  "id": "phish-evt-20251204-0001",
  "timestamp": "2025-12-04T16:05:23Z",
  "fingerprint": "fp_...",
  "ipHash": "ip_...",
  "route": "/login",
  "referrerDomain": "bit.ly",
  "referrerType": "shortener",
  "piiSummary": {
    "hasEmail": true,
    "hasPassword": true
  },
  "hibpSummary": {
    "emailBreaches": 3,
    "passwordPwned": true
  },
  "stuffingSummary": {
    "matchCountByPassword": 300
  },
  "phishingInference": {
    "likely": true,
    "strength": "High",
    "type": "PhishingCredentialStuffing",
    "attackFamilyHint": "ShortenerPhishStuffing-2025-12",
    "confidence": 0.91
  },
  "genomeSnapshot": {
    "riskScore": 0.92,
    "riskBand": "High",
    "botType": "Scraper",
    "actionPolicy": "honeypot-credentials"
  }
}
```

Stored in:

* Postgres + pgvector (for embeddings).
* Or dedicated vector DB.

Used for:

* RAG queries.
* Campaign clustering and naming.
* Dashboard metrics.

---

## 5. Configuration

Example config block:

```json
{
  "PhishingDetection": {
    "Enabled": true,
    "LLM": {
      "Provider": "Ollama",
      "Model": "gemma3:4b",
      "Endpoint": "http://ollama:11434",
      "TimeoutMs": 100,
      "MaxRequestsPerSecond": 50
    },
    "Referrer": {
      "EnableAnalysis": true,
      "ShortenerDomains": [ "bit.ly", "t.co", "tinyurl.com", "lnkd.in" ],
      "WebmailDomains": [ "mail.google.com", "outlook.live.com", "mail.yahoo.com" ],
      "TrackerPatterns": [ "click.", "trk.", "link." ],
      "DomainSimilarityThreshold": 0.75
    },
    "Honeypot": {
      "EnableCredentialCapture": true,
      "UsernameStorageMode": "FullEmail",
      "EnablePIIDetection": true,
      "EnableHIBPEmailCheck": true,
      "EnableHIBPPasswordCheck": true
    },
    "LLMTrigger": {
      "OnlyForAuthEndpoints": true,
      "MinRiskScoreForInference": 0.5,
      "RequirePIICredentials": true
    }
  }
}
```

---

## 6. Privacy and Safety

* No plaintext passwords stored or logged.
* Passwords hashed immediately (internal HMAC + SHA-1 for HIBP k-Anonymity).
* PII detection used to **minimize** what is persisted:

    * Redact where possible.
    * Configurable username handling.
* HIBP used:

    * For counts / yes-no, not as a dump.
    * With rate limiting and key management.
* LLM:

    * Receives only derived features, not raw secrets.
    * Used solely for **classification**, not exploit generation.
* Event stores:

    * Encrypted at rest (recommended).
    * Access-controlled.

---

## 7. Implementation Phases

### Phase 1: Foundations

* Implement `ReferrerDetector` and signals.
* Hook PII detector into honeypot auth endpoints.
* Implement safe credential capture & transformation.
* Add HIBP integration (email + password checks).
* Extend credential stuffing detector with new signals.

### Phase 2: LLM Categorizer

* Implement feature bundler for phishing inference.
* Implement LLM calling service with JSON validation.
* Implement `PhishingInferenceDetector` and genome integration.
* Add basic policy transitions using `phishing.likely`.

### Phase 3: Long-Term Memory and Reporting

* Implement phishing event store schema.
* Add optional RAG index.
* Expose API endpoints for:

    * listing phishing campaigns
    * querying events by time window / route / family
* (Future) Add UI dashboard for phishing campaigns and family naming.

---

## 8. Success Criteria

* Low overhead in normal traffic:

    * LLM phishing inference only invoked when configured conditions are met.
* High signal quality:

    * Campaigns with:

        * repeated honeypot hits,
        * PII credentials,
        * HIBP-confirmed breach usage,
        * suspicious referrers,
          are consistently marked as `phishing.likely = true`.
* Clear explainability:

    * Genome contains a single, high-level `PhishingInference` contribution with human-readable `reason`.
* Safety:

    * No plaintext credentials in logs or persistent storage.
    * LLM never sees raw passwords or full sensitive payloads.
