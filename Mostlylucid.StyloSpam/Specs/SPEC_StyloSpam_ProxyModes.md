# SPEC: StyloSpam Email Proxy Modes (Future)

## Goal

StyloSpam will operate like an email proxy layer, similar in spirit to how YARP fronts HTTP, but for mail flows and policy decisions.

## Mode 1: Incoming

Incoming mode will protect destination mailboxes.

### Protocol Support

- SMTP proxy receive path (first-class).
- IMAP mailbox polling/sync.
- Gmail API ingestion.
- Outlook (Microsoft Graph) ingestion.

### Planned Processing Flow

1. StyloSpam will ingest a message from one of the incoming connectors.
2. It will normalize all inputs into a canonical envelope.
3. Contributors will score auth, links, payload, semantics, and campaign context.
4. Policy engine will output allow/tag/warn/quarantine/block.
5. Connector-specific action will execute:
   - SMTP: relay, hold, or reject.
   - IMAP/Gmail/Outlook: tag/move/quarantine with provider-specific mechanisms.

## Mode 2: Outgoing

Outgoing mode will protect platforms that send mail on behalf of users.

### Primary Use Case

Platforms that provide email sending as a feature will call StyloSpam before relay.
StyloSpam will guard against abusive users sending spam/phishing through the platform.

### Planned Processing Flow

1. Client platform will submit raw MIME or simplified message payload.
2. StyloSpam will score message semantics and abuse signals.
3. Per-user and per-tenant behavior tracking will be applied.
4. Abuse guard will enforce temporary blocking for repeat offenders.
5. If policy allows, StyloSpam will relay via configured SMTP upstream.

## API Contract Direction

- Filter-only endpoints will return score, verdict, reasons, and guard state.
- Filter-and-relay endpoints will return both policy decision and relay outcome.
- Envelope model compatibility will continue to support common MailKit/SMTP message formats.

## Security Constraints

- StyloSpam will prefer feature extraction over long-lived raw content retention.
- Tenant isolation will be enforced for all behavior and campaign features.
- Local model inference will be the default for semantic classification in sensitive environments.

