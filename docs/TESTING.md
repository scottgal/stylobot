# Testing

## Local validation

- Demo app endpoints
- Gateway admin endpoints
- Known bot/human User-Agent requests

## Suggested test matrix

- Human browser UA
- Scraper UA
- Security tool UA
- Search engine bot UA
- High-rate repeated requests

## Commands

```bash
dotnet test mostlylucid.stylobot.sln
```

Use integration tests in:

- `Mostlylucid.BotDetection.Test`
- `Mostlylucid.BotDetection.Orchestration.Tests`
- `Stylobot.Gateway.Tests`
