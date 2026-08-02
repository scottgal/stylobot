# Licensing

## FOSS

No license required. Detection, learning, blocking, and all features work indefinitely.
Configure with `AddBotDetection()` and never set `BotDetection:Licensing:Token`.

## Commercial

A signed license JWT is required. Set it in configuration:

```json
{
  "BotDetection": {
    "Licensing": {
      "Token": "<your-license-jwt>",
      "Domains": ["yourdomain.com"]
    }
  }
}
```

Start a free 30-day trial (one per organization) at https://stylo.bot.

## What happens when a license expires

FOSS above never expires and never needs a license, full stop. Commercial trial/tier expiry
mechanics (what happens, grace windows, renewal) are documented at
[stylo.bot](https://stylo.bot), not here -- that detail belongs with the commercial product,
not the public engine.
