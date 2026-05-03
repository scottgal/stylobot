# Mostlylucid.StyloSpam

StyloSpam is the email-security sibling to StyloBot, focused on spam/phishing scoring, semantic filtering, and campaign-aware controls.
It is the implementation track for the broader `lucidMAIL` roadmap area.

## Projects

- `Mostlylucid.StyloSpam.Core`
  - Shared scoring engine, envelope models, parser helpers, and contributors.
- `Mostlylucid.StyloSpam.Incoming`
  - Incoming mode service for SMTP proxy + IMAP/Gmail/Outlook ingestion.
- `Mostlylucid.StyloSpam.Outgoing`
  - Outgoing pass-through semantic filter to stop users abusing platform send pipelines.
- `Mostlylucid.StyloSpam.Tool`
  - CLI `.NET tool` (`stylospam-score`) for offline MIME scoring.

## Incoming Mode

Runtime components:

- SMTP proxy hosted service (receive -> score -> policy -> optional upstream relay).
- IMAP polling service (unread pull + scoring).
- Gmail API polling service (token-based message pull + scoring).
- Outlook Graph polling service (token-based message pull + scoring).
- Optional small local LLM semantic contributor (Ollama-compatible endpoint).

API endpoints:

- `GET /capabilities`
- `POST /incoming/score/raw`
- `POST /incoming/score/simple`

## Outgoing Mode

Runtime components:

- Scoring + user send-velocity tracking.
- Repeated-offender abuse guard (temporary block window after strike threshold).
- Filter-only endpoints for pre-send gating.
- Filter-and-relay endpoints with policy-aware SMTP relay behavior.
- Optional small local LLM semantic contributor (Ollama-compatible endpoint).

API endpoints:

- `POST /outgoing/filter/raw`
- `POST /outgoing/filter/simple`
- `POST /outgoing/filter-and-relay/raw`
- `POST /outgoing/filter-and-relay/simple`
- `GET /outgoing/users/{tenantId}/{userId}/stats`

## CLI Tool

Examples:

```bash
dotnet run --project Mostlylucid.StyloSpam/Mostlylucid.StyloSpam.Tool/Mostlylucid.StyloSpam.Tool.csproj -- --help

dotnet run --project Mostlylucid.StyloSpam/Mostlylucid.StyloSpam.Tool/Mostlylucid.StyloSpam.Tool.csproj -- email.eml --mode incoming --json

cat email.eml | dotnet run --project Mostlylucid.StyloSpam/Mostlylucid.StyloSpam.Tool/Mostlylucid.StyloSpam.Tool.csproj -- --stdin --mode outgoing
```

Supported file formats:

- `.eml`
- `.mime`
- `.mbox` (first message)

## Specs

- `Specs/README.md`
- `Specs/SPEC_StyloSpam_Vision.md`
- `Specs/SPEC_StyloSpam_ProxyModes.md`
- `Specs/SPEC_StyloSpam_Detectors.md`
- `ContributingDetectors/README.md`
