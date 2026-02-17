# Feature Specification: StyloSpam (lucidMAIL)

## Overview

`StyloSpam` (working codename: `lucidMAIL`) is a new detection area that applies StyloBot design patterns to email security and abuse intelligence.
It reuses the contributor + blackboard + policy architecture and extends it for:

- Email header and sender behavior analysis.
- Content analysis with small local LLMs.
- Cross-message patterning and campaign detection.
- Explainable, policy-driven actions (tag, warn, quarantine, block).

The design target is "zero external dependency by default", with optional integrations for enrichment.

---

## Goals

- Build a modular email detection pipeline using `IContributingDetector` style contributors.
- Keep all inference local-first (small models, Ollama/ONNX friendly).
- Detect both single-message risk and cross-message campaigns.
- Preserve explainability: every action ties to detector contributions and signals.
- Support incremental rollout: observe first, enforce later.

---

## Non-Goals (v1)

- Not a full mail server replacement.
- Not a full EDR/DLP suite.
- Not a single black-box LLM verdict system.
- Not dependent on cloud LLM APIs for core detection.

---

## StyloBot Integration Strategy

StyloSpam is designed as a sibling area that integrates tightly with StyloBot rather than replacing it.

Integration modes:

1. Shared-core mode
- StyloSpam reuses core orchestration abstractions from StyloBot:
  - `DetectionContribution` pattern,
  - blackboard signal model,
  - policy/action evaluation model,
  - confidence/probability split.

2. Sidecar mode
- StyloSpam runs as an independent service and emits normalized detection events to StyloBot dashboard and storage.
- Useful for staged rollout where mail and web traffic stay operationally separate.

3. Unified intelligence mode
- StyloSpam and StyloBot feed a shared campaign memory graph.
- Enables cross-channel correlation:
  - same infrastructure showing up in web and mail,
  - coordinated campaigns spanning inbox phishing and web probing.

Shared integration surfaces:

- Shared policy vocabulary: `Allow`, `Tag/Warn`, `Challenge`, `Quarantine`, `Block`.
- Shared reputation/campaign stores (where configured).
- Shared telemetry contracts and dashboard event schema.
- Shared local AI runtime strategy (small local models first).

---

## State-of-the-Art Spam Detection Baseline (Research)

StyloSpam should explicitly target a "current best-practice + modern ML" stack, not legacy spam filtering only.

## 1) Protocol and transport trust layer (mandatory)

Before ML scoring, enforce standards-based trust signals:

- SPF validation (`RFC 7208`)
- DKIM signature validation (`RFC 6376`)
- DMARC alignment/policy handling (`RFC 7489`)
- ARC chain-of-custody support for forwarded/list mail (`RFC 8617`)
- SMTP transport hardening:
  - MTA-STS (`RFC 8461`)
  - SMTP TLS Reporting (`RFC 8460`)
  - DANE for SMTP where available (`RFC 7672`)
- Authentication-Results normalization (`RFC 8601`) for downstream policy and explainability.

Interpretation:
- These are necessary but not sufficient; they reduce spoofing and transport abuse but do not prove message legitimacy.

## 2) Multi-signal semantic + structural modeling (modern baseline)

Recent results across 2024-2026 indicate stronger phishing/spam detection when combining:

- Transformer text understanding (BERT/DistilBERT class embeddings),
- with structural relationships (graph neural methods),
- and similarity retrieval or campaign memory.

Representative references:

- BERT + GraphSAGE hybrid spam detection (Journal of Big Data, 2025).
- DistilBERT + neural classifier framework (Discover Applied Sciences, published 2025, record 2026).
- Transformer embedding + vector similarity search for phishing email detection (Computers & Electrical Engineering, 2025).
- DistilBERT + Graph Attention for phishing emails (IEEE Access, 2025).
- Comparative deep learning phishing study (Sensors, 2024).

Architecture implication for StyloSpam:
- Keep deterministic checks in Wave 0.
- Use transformer-semantic contributor(s) in Wave 1/2.
- Add graph/similarity contributors for campaign-level evidence in Wave 2/3.

## 3) Campaign-first detection (not just per-message classification)

State-of-the-art deployments increasingly treat malicious email as campaign activity:

- near-duplicate templates,
- sender/infra reuse,
- URL and attachment family overlap,
- burst timing and recipient spread.

StyloSpam requirement:
- Maintain a campaign graph and similarity index as first-class services.
- Use campaign confidence to adjust per-message actions and tenant-level defenses.

## 4) Adaptive and resilient pipeline behavior

Modern detection systems need:

- drift-aware thresholds,
- continuous retraining or incremental model refresh,
- strict false-positive controls (especially for quarantine/block actions),
- graceful degradation when AI contributors time out.

StyloSpam requirement:
- Deterministic safety net always available.
- LLM contributors are additive, not single points of failure.
- Policy should support shadow mode, staged rollout, and per-tenant confidence gates.

## 5) Human-centered defense loop

Technical detection must integrate analyst/user feedback:

- report-as-phish and report-as-safe events,
- analyst triage labels feeding campaign memory,
- explainable reason payloads for each action.

StyloSpam requirement:
- All model and rule decisions must emit explainable contribution records.
- User-facing warning modes should be supported before hard enforcement.

## 6) Minimum SOTA bar for StyloSpam v1

v1 should include at least:

1. Standards authentication layer (SPF/DKIM/DMARC/ARC + Auth-Results parsing).
2. Transformer-based semantic classifier (small local model).
3. Similarity retrieval over message embeddings.
4. Campaign clustering (graph or equivalent relational model).
5. Action policy with confidence gates and quarantine controls.

---

## Proposed Architecture

## 0) Email Proxy Mode (YARP-style)

StyloSpam should support an "email proxy" deployment model, analogous to how StyloBot can run as a traffic gateway.

Primary proxy topologies:

1. Inbound SMTP proxy
- Sits in front of the final mail server.
- Accepts SMTP, inspects message envelope/body, then relays upstream.
- Can tag, quarantine, or reject before mailbox delivery.

2. Outbound SMTP relay proxy
- Sits between internal mail clients/services and external SMTP destinations.
- Detects compromised-account spam bursts and data exfiltration patterns.

3. API mailbox proxy/agent mode
- For Graph/Gmail API-based tenants where SMTP edge proxy is not feasible.
- Pull/ingest acts as a logical proxy with equivalent detection and policy.

Proxy decision points:

- Pre-accept (connection/session level): IP reputation, auth anomalies, protocol abuse.
- Pre-delivery (message level): headers/content/attachments/campaign signals.
- Post-delivery feedback: user report signals, bounce/reply behavior, campaign drift.

Proxy actions:

- `Allow` (pass through)
- `Tag` (prepend subject/header labels)
- `WarnBanner` (inject safe warning metadata/content)
- `Quarantine` (redirect to quarantine store/mailbox)
- `Reject` (SMTP 5xx at proxy edge)

This keeps the model consistent with StyloBot policy semantics while fitting email transport realities.

## 1) Ingestion Layer

Adapters normalize messages into a common envelope:

- SMTP receive hook.
- IMAP/Exchange journal pull.
- Microsoft Graph mailbox connector.
- Gmail API connector.
- File import (`.eml`, `.msg`) for offline testing/training.

Normalized object:

```csharp
public sealed record MailEnvelope(
    string MessageId,
    DateTimeOffset ReceivedAtUtc,
    string TenantId,
    string MailboxId,
    string From,
    IReadOnlyList<string> To,
    string? Subject,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<MailPart> Parts,
    IReadOnlyList<MailLink> Links,
    IReadOnlyList<MailAttachment> Attachments);
```

## 2) Blackboard Detection Pipeline

Contributors emit `DetectionContribution` and signals into a shared mail context:

- Wave 0: cheap deterministic checks.
- Wave 1: enriched behavioral and correlation checks.
- Wave 2: local LLM classification for uncertain or high-risk messages.
- Wave 3: campaign and cluster contributors (cross-message graph).

## 3) Decision and Action Policies

Policy engine maps risk and confidence to actions:

- `Allow`
- `Tag`
- `WarnBanner`
- `Quarantine`
- `Block`

All actions remain configurable per tenant and mailbox class.

---

## Detector Taxonomy

## A. Fast Deterministic Contributors (Wave 0)

1. `MailAuthContributor`
- SPF/DKIM/DMARC alignment and ARC chain quality.
- Signals: `mail.auth.spf`, `mail.auth.dkim`, `mail.auth.dmarc`, `mail.auth.arc_score`.

2. `HeaderAnomalyContributor`
- Message-ID anomalies, reply-chain inconsistencies, sender/display-name mismatch.
- Signals: `mail.header.anomaly_count`, `mail.header.reply_to_mismatch`.

3. `DomainReputationContributor`
- Domain age, TLD risk profile, sender-domain novelty per tenant.
- Signals: `mail.domain.age_days`, `mail.domain.novelty_score`.

4. `UrlStructureContributor`
- URL obfuscation patterns, punycode, redirect chains, tracking-token entropy.
- Signals: `mail.url.obfuscated`, `mail.url.redirect_depth`, `mail.url.lookalike_score`.

5. `AttachmentRiskContributor`
- Executable/script macro indicators, archive nesting, type mismatch.
- Signals: `mail.attachment.risk_score`, `mail.attachment.type_mismatch`.

## B. Behavioral and Correlation Contributors (Wave 1)

6. `SenderBehaviorContributor`
- Sender cadence drift, burst patterns, unusual mailbox targeting.
- Signals: `mail.sender.burst_score`, `mail.sender.target_spread`.

7. `RecipientPatternContributor`
- Lateral spread patterns across mailbox groups and departments.
- Signals: `mail.recipient.cluster_spread`, `mail.recipient.role_targeting`.

8. `ConversationContextContributor`
- Thread hijack indicators and context break from historical thread style.
- Signals: `mail.thread.context_shift`, `mail.thread.identity_mismatch`.

9. `ImpersonationContributor`
- Display-name impersonation and executive spoof style.
- Signals: `mail.impersonation.exec_like`, `mail.impersonation.name_similarity`.

## C. Local LLM Content Contributors (Wave 2)

10. `MailIntentLlmContributor`
- Small local model classifies intent:
  - benign/business,
  - marketing,
  - phishing/social engineering,
  - credential theft,
  - malware delivery.
- Emits structured JSON only, no free-form verdict string.
- Signals: `mail.llm.intent`, `mail.llm.risk`, `mail.llm.confidence`.

11. `PromptInjectionContributor` (for AI-targeted mail workflows)
- Detects instruction-injection patterns in email content meant for AI agents.
- Signals: `mail.ai.prompt_injection`, `mail.ai.tool_abuse_pattern`.

12. `ContentSafetyContributor`
- Optional local semantic checks for extortion, fraud language, urgency-pressure patterns.
- Signals: `mail.content.fraud_pattern`, `mail.content.urgency_score`.

## D. Cross-Message Campaign Contributors (Wave 3)

13. `MailSimilarityContributor`
- Embedding + fuzzy-hash similarity over body, URLs, and attachment fingerprints.
- Signals: `mail.similarity.cluster_id`, `mail.similarity.top_score`.

14. `CampaignGraphContributor`
- Graph-based campaign discovery:
  - sender infrastructure reuse,
  - URL domain overlap,
  - payload family overlap,
  - temporal burst coherence.
- Signals: `mail.campaign.id`, `mail.campaign.confidence`, `mail.campaign.size`.

15. `CampaignDriftContributor`
- Tracks evolution of a campaign over time and retunes policy response.
- Signals: `mail.campaign.drift_score`, `mail.campaign.phase`.

---

## Local LLM Strategy

Use local-first models with strict budgets:

- Primary: small instruct model (`1B` to `4B`) via Ollama.
- Optional ONNX classifier for ultra-fast triage on known categories.
- LLM only runs when:
  - deterministic risk is medium/high, or
  - policy explicitly requests semantic escalation.

Guardrails:

- JSON schema enforced output.
- Max token budget per message.
- Timeout with fail-open to deterministic stack.
- Deterministic temperature for classification.

Example output schema:

```json
{
  "intent": "credential_theft",
  "risk": 0.86,
  "confidence": 0.74,
  "reasons": ["fake login pretext", "urgency coercion", "spoofed trust anchor"]
}
```

---

## Campaign Detection Model

Campaign identity combines:

- Sender domain/IP/asn features.
- URL/domain features.
- Content and subject embeddings.
- Attachment hashes and extracted indicators.
- Temporal burst and target spread metrics.

Suggested campaign identity key:

`campaign_fingerprint = H(content_signature + url_signature + infra_signature + timing_signature)`

Storage:

- Short-term: in-memory/ephemeral for active detection.
- Long-term: SQLite/Postgres for historical trend and analyst workflows.

---

## Signal and Policy Model

Reuse existing confidence split:

- `bot_probability` equivalent for mail: `threat_probability`.
- Separate `confidence` from `probability`.

Example policy defaults:

- `Allow` if `threat_probability < 0.30` and confidence adequate.
- `Tag` if `0.30 <= threat_probability < 0.55`.
- `WarnBanner` if `0.55 <= threat_probability < 0.75`.
- `Quarantine` if `>= 0.75`.
- `Block` only for very high confidence and explicit tenant policy.

---

## Privacy and Data Handling

- Hash identities by default (mailbox, sender, recipient clusters).
- Avoid raw-body retention unless policy enables forensic mode.
- Store only extracted features and bounded snippets where possible.
- Keep all local LLM inference on-box by default.

---

## Proposed Project Layout

```text
Mostlylucid.StyloSpam/
  Gateway/
  Ingestion/
  Models/
  Orchestration/
  ContributingDetectors/
  Campaigns/
  Policies/
  Storage/
  Api/
  Dashboard/
```

Integration points:

- Dedicated folder: `Mostlylucid.StyloSpam/` for all email-domain implementation.
- `Gateway/` hosts the SMTP/API proxy runtime ("Email Proxy mode").
- Reuse `DetectionContribution` style models where practical.
- Reuse policy and action abstractions from `Mostlylucid.BotDetection`.
- Reuse signature/campaign storage patterns from existing cluster and reputation services.
- Add an adapter package (planned) for StyloBot dashboard/event compatibility.

---

## Delivery Plan

## Phase 0 - Foundation (1 to 2 weeks)

- Define `MailEnvelope`, parsing pipeline, and test corpus format.
- Add ingestion adapters for `.eml` and one live provider.
- Stand up baseline orchestration and policy skeleton.
- Define SMTP proxy interfaces and relay contracts in `Gateway/`.

## Phase 1 - Deterministic MVP (2 to 4 weeks)

- Implement contributors:
  - `MailAuthContributor`
  - `HeaderAnomalyContributor`
  - `UrlStructureContributor`
  - `AttachmentRiskContributor`
  - `SenderBehaviorContributor`
- Add risk scoring and `Allow/Tag/Quarantine`.
- Add metrics and explainability payloads.
- Deliver inbound SMTP proxy MVP (pass-through + tag + quarantine + reject).

## Phase 2 - Local LLM Semantics (2 to 3 weeks)

- Implement `MailIntentLlmContributor`.
- Add schema-validated structured outputs.
- Add bounded inference budgets and fallback behavior.

## Phase 3 - Campaign Intelligence (3 to 5 weeks)

- Implement similarity index and `CampaignGraphContributor`.
- Add campaign dashboard and timeline views.
- Add drift detection and campaign-level policy hooks.

## Phase 4 - Hardening and Enterprise Controls

- Multi-tenant policy packs.
- Analyst feedback loop and active learning.
- Optional external enrichment connectors.

---

## MVP Success Criteria

- Detector latency under target budget for Wave 0/1.
- Explainable verdict for every processed message.
- Local LLM contributes measurable precision gain on ambiguous mail.
- Campaign detector identifies recurring clusters with low false merge rate.

---

## Open Questions

1. Which connector is first: Microsoft Graph, Gmail, or SMTP ingest?
2. Should quarantine be mailbox-local or centralized at first release?
3. Do we allow raw body retention in v1 forensic mode, or feature-only storage?
4. What target throughput is required for first production pilot?

---

## References

Standards and technical baseline:

- RFC 7208 (SPF): https://www.rfc-editor.org/info/rfc7208
- RFC 6376 (DKIM): https://www.rfc-editor.org/info/rfc6376
- RFC 7489 (DMARC): https://www.rfc-editor.org/info/rfc7489
- RFC 8617 (ARC): https://www.rfc-editor.org/info/rfc8617
- RFC 8461 (MTA-STS): https://www.rfc-editor.org/info/rfc8461
- RFC 8460 (SMTP TLS Reporting): https://www.rfc-editor.org/info/rfc8460
- RFC 7672 (DANE for SMTP): https://www.rfc-editor.org/info/rfc7672
- RFC 8601 (Authentication-Results): https://www.rfc-editor.org/info/rfc8601
- M3AAWG Email Authentication summary: https://www.m3aawg.org/TechnologySummaries/EmailAuthentication

Email spam/phishing research references:

- BERT-GraphSAGE: hybrid approach to spam detection (Journal of Big Data, 2025): https://link.springer.com/article/10.1186/s40537-025-01176-9
- An LLM driven framework for email spam detection using DistilBERT embeddings and neural classifiers (Discover Applied Sciences, 2025/2026): https://link.springer.com/article/10.1007/s42452-025-08147-y
- Phishing email detection using vector similarity search leveraging transformer-based word embedding (Computers & Electrical Engineering, 2025): https://doi.org/10.1016/j.compeleceng.2025.110403
- PhishingGNN: Phishing Email Detection Using Graph Attention Networks and Transformer-Based Feature Extraction (IEEE Access, 2025): https://doi.org/10.1109/access.2025.3592135
- Advancing Phishing Email Detection: A Comparative Study of Deep Learning Models (Sensors, 2024): https://www.mdpi.com/1424-8220/24/7/2077
- Improving phishing email detection performance through deep learning with adaptive optimization (Scientific Reports, 2025): https://www.nature.com/articles/s41598-025-20668-5

Operational platform references:

- Microsoft Defender for Office 365 anti-phishing policy model: https://learn.microsoft.com/en-us/defender-office-365/anti-phishing-policies-about
