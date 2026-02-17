# SPEC: StyloSpam Vision (Future)

## Purpose

StyloSpam will become the email-security domain inside StyloBot, under the working area name `lucidMAIL`.
It will apply bot-detection principles to email abuse: sender authenticity, semantic intent, campaign behavior, and cross-tenant abuse patterns.

## Positioning

- StyloBot will remain the platform identity.
- StyloSpam will be the email module name used across APIs, services, and docs.
- `lucidMAIL` will be the roadmap/program name for the broader email product area.

## Future Architecture

StyloSpam will evolve into these bounded areas:

- `Gateway`
  - Will host inbound SMTP proxy and outbound SMTP relay/filter paths.
- `Ingestion`
  - Will ingest IMAP, Gmail API, and Microsoft Graph mailbox streams.
- `Models`
  - Will normalize MIME/provider payloads to one canonical envelope.
- `ContributingDetectors`
  - Will host pluggable scoring contributors (auth, content, links, campaigns, behavior).
- `Campaigns`
  - Will cluster related messages and emit campaign-level risk signals.
- `Policies`
  - Will map scores to allow/tag/warn/quarantine/block and tenant-specific actions.
- `Storage`
  - Will persist aggregates, fingerprints, and feedback features (not raw PII where avoidable).
- `Api`
  - Will expose score/filter/relay/status/policy endpoints for platform integration.
- `Dashboard`
  - Will provide operations and abuse analyst visibility.
- `Orchestration`
  - Will coordinate detector execution, retries, and background workflows.

## Delivery Phases

1. Foundation
   - Core scoring engine and local scoring tool will be productionized.
   - Incoming and outgoing mode services will run as independent deployables.
2. Proxy Hardening
   - SMTP proxy/relay will support policy-based flow control and transport hardening.
   - Gmail/Outlook/IMAP ingestion will move from polling-first to resilient sync patterns.
3. Campaign Intelligence
   - Near-duplicate and graph-based campaign detection will drive group-level blocking.
4. LLM Augmentation
   - Small local LLM models will add semantic phishing/spam reasoning with strict guardrails.
5. Cross-Product Integration
   - StyloSpam telemetry will feed StyloBot-wide abuse posture and shared policy controls.

