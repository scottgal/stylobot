# StyloBot Gateway — E-commerce / Scraper Defense

Protects e-commerce sites (Magento, WooCommerce, Shopify headless) from:

- **Price scrapers** — bots that crawl `/catalog/**` and `/api/products/**` to harvest
  pricing data. This example routes them to a bot-sink cluster instead of your real
  backend, so scrapers receive a 429 instead of real prices.
- **Inventory bots** — high-frequency crawlers checking stock levels.
- **Checkout abuse** — credential stuffing and payment probing on `/checkout/**`.
- **Account takeover** — automated login attempts on `/customer/account/**`.

## How the Bot-Sink Routing Works

YARP has two routes. The first matches requests where `X-Bot-Detection-RiskBand: High`
(added by StyloBot after detection). Those go to `bot-sink`. Everything else goes to
your real backend. The two routes have explicit `Order` values (1 and 2) so the bot
route is checked first.

```
Incoming request
  → StyloBot detects: RiskBand = High
  → Gateway adds header: X-Bot-Detection-RiskBand: High
  → YARP route "bot-traffic" matches
  → Proxied to bot-sink (returns 429)

Incoming request
  → StyloBot detects: RiskBand = Low
  → YARP route "human-traffic" matches
  → Proxied to your real backend
```

## Quick Start

```bash
cp .env.example .env   # set ADMIN_SECRET
docker compose up -d
```

Replace the stub `app` service with your real e-commerce backend.

## Tuning

- Route `Medium` bots to sink too? Add `"Medium"` to the `Values` array in `yarp.json`.
- Feed bots fake catalog data instead of 429s? Replace `bot-sink` with a service
  that returns plausible-but-wrong prices. StyloBot's holodeck feature automates this.
- Magento 2 specific: add `/rest/V1/products/**` and `/rest/V1/inventory/**` to the
  `scraper-defense` PathPolicies.
- WooCommerce specific: add `/wp-json/wc/v3/products/**` to `scraper-defense`.
