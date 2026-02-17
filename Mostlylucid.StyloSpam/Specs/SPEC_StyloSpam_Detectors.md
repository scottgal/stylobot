# SPEC: StyloSpam State-of-the-Art Detector Roadmap (Future)

## Objective

StyloSpam will implement a layered detector stack so no single evasion class can bypass the system.

## Detector Layers

1. Authentication and identity trust
   - SPF, DKIM, DMARC alignment and failure patterns will be scored.
   - ARC chain integrity and forwarding anomalies will be scored.
   - Transport/security policy signals (MTA-STS, TLS-RPT) will be scored.

2. URL and sender infrastructure analysis
   - Domain age, lexical brand mimicry, punycode/homograph risk, and redirect depth will be scored.
   - Link-to-sender and reply-to mismatches will be scored.

3. Content and stylometric analysis
   - Spam/phish phrase families, urgency patterns, and social engineering structures will be scored.
   - Header/body incoherence and template-level anomalies will be scored.

4. Attachment and payload analysis
   - Attachment type risk, macro/compressed nesting risk, and filename deception patterns will be scored.
   - MIME structure anomalies and parser edge-case patterns will be scored.

5. Campaign and graph detection
   - Near-duplicate clustering (subject/body/link shingles) will identify campaigns.
   - Temporal burst and sender-recipient graph anomalies will trigger campaign-level controls.

6. Behavioral abuse controls (outgoing)
   - Per-user velocity, recipient spread, and repeated high-risk verdict streaks will drive throttles/blocks.
   - Cross-tenant reputation aggregates will inform adaptive policy thresholds.

7. LLM semantic layer (small, local)
   - Small local models will provide semantic intent classification and rationale snippets.
   - LLM output will be one contributor, never a single-point decision source.

## Decision Strategy

- A weighted ensemble will combine layer outputs into calibrated score + confidence.
- Policies will map to allow/tag/warn/quarantine/block by tenant profile.
- Campaign verdict escalation will override low-risk single-message scores when systemic abuse is evident.

## Research Anchors

- SPF: RFC 7208
  - https://datatracker.ietf.org/doc/html/rfc7208
- DKIM: RFC 6376
  - https://datatracker.ietf.org/doc/html/rfc6376
- DMARC: RFC 7489
  - https://datatracker.ietf.org/doc/html/rfc7489
- ARC: RFC 8617
  - https://datatracker.ietf.org/doc/html/rfc8617
- MTA-STS: RFC 8461
  - https://datatracker.ietf.org/doc/html/rfc8461
- SMTP TLS Reporting (TLS-RPT): RFC 8460
  - https://datatracker.ietf.org/doc/html/rfc8460
- Gmail sender requirements update (bulk sender enforcement details, 2025 update)
  - https://support.google.com/a/answer/14229414
- Yahoo sender requirements
  - https://senders.yahooinc.com/smtp-error-codes/
- Microsoft Defender for Office 365 anti-phishing guidance
  - https://learn.microsoft.com/en-us/defender-office-365/anti-phishing-policies-about
- "SUNRISE TO SUNSET" (campaign and abuse operations research)
  - https://www.usenix.org/conference/usenixsecurity23/presentation/ho
- RETVec (resilient text vectorizer for noisy/obfuscated text)
  - https://research.google/blog/re-tvec-resilient-and-efficient-text-vectorizer/
- Magika (AI-driven file type identification for attachment pipelines)
  - https://opensource.googleblog.com/2024/02/magika-ai-powered-fast-and-efficient-file-type-identification.html
- Small local LLM email classification evidence (2025)
  - https://arxiv.org/abs/2505.00034

