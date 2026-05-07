# StyloBot Gateway — Shadow Mode

Use this when you want to understand your traffic before committing to blocking.
All requests pass through. Bots are detected and logged, but never blocked or throttled.

## Quick Start

```bash
cp .env.example .env   # set APP_UPSTREAM=http://yourapp:3000
docker compose up -d
```

Check what's being detected (admin API is open without a secret in this example):

```bash
curl http://localhost:8080/admin/summary
curl http://localhost:8080/admin/topbots
```

## Enabling Blocking After Your Review

After reviewing 7 days of data, flip the switch:

```bash
echo "BLOCK_BOTS=true" >> .env
docker compose up -d --force-recreate gateway
```

## What to Look For

- **High `X-Bot-Detection-RiskBand: High` count on `/wp-login.php`?** Brute force in progress.
- **Lots of `X-Bot-Type: Scraper` on your product catalog?** Price scraping in progress.
- **`X-Is-Search-Engine: true` traffic?** That is Googlebot — do NOT block it.

Use the per-endpoint data to tune PathPolicies before enabling blocking:

```bash
curl http://localhost:8080/admin/endpoints   # which paths bots target most
curl http://localhost:8080/admin/countries   # traffic by country
```

## Security Note

`ADMIN_ALLOW_INSECURE=true` is set in this example for convenience. Before moving to
production, remove it and set `ADMIN_SECRET` to a secure random value.
