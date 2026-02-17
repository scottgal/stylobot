# Mostlylucid.StyloSpam

StyloSpam is the email-security sibling to StyloBot, focused on spam/phishing scoring and campaign-aware filtering.

## Projects

- `Mostlylucid.StyloSpam.Core`
  - Shared scoring engine, envelope models, and detector contributors.
- `Mostlylucid.StyloSpam.Incoming`
  - Incoming mode service for SMTP/IMAP/Gmail/Outlook ingestion paths.
- `Mostlylucid.StyloSpam.Outgoing`
  - Outgoing pass-through semantic filter to prevent platform users from sending spam.
- `Mostlylucid.StyloSpam.Tool`
  - CLI `.NET tool` (`stylospam-score`) for offline scoring of MIME emails.

## Incoming Mode

Key endpoints:

- `GET /capabilities`
- `POST /incoming/score/raw`
- `POST /incoming/score/simple`

Connector scaffolding includes:

- SMTP proxy connector
- IMAP connector
- Gmail API connector
- Outlook Graph connector

## Outgoing Mode

Key endpoints:

- `POST /outgoing/filter/raw`
- `POST /outgoing/filter/simple`
- `GET /outgoing/users/{tenantId}/{userId}/stats`

Outgoing mode includes per-user send-velocity tracking to guard against abusive platform senders.

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
