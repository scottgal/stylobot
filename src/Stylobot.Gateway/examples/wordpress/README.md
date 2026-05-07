# StyloBot Gateway — WordPress Protection

Protects a WordPress site from the most common automated attacks:

- **xmlrpc.php abuse** — used for DDoS amplification and mass brute-force. Nearly all
  legitimate traffic stopped using REST API years ago. This example blocks at 30% bot
  confidence — almost everything hitting xmlrpc.php is a bot.
- **wp-login.php brute force** — credential stuffing at scale. Blocked at 40% confidence.
- **wp-admin scanner probes** — tools like WPScan, Nuclei, and generic vuln scanners.
  Blocked at 40% confidence.
- **General bot traffic** — scrapers, AI crawlers, headless browsers. Throttled by default
  on the main site (won't break legitimate Googlebot traffic).

## Quick Start

1. Copy `.env.example` to `.env` and set the required secrets.
2. Update `wordpress` destination in `config/yarp.json` if your container name differs.
3. Start:

```bash
docker compose up -d
```

## What Your WordPress Backend Receives

Every request to WordPress arrives with these headers from the gateway:

```
X-Bot-Detected: true
X-Bot-Confidence: 0.94
X-Bot-Detection-RiskBand: High
X-Bot-Type: SecurityScanner
X-Is-Malicious-Bot: true
```

WordPress plugins (Wordfence, Jetpack) can read these. To log them in `functions.php`:

```php
add_action('init', function() {
    if ($_SERVER['HTTP_X_BOT_DETECTED'] === 'true') {
        error_log('Bot blocked by gateway: ' . $_SERVER['HTTP_X_BOT_DETECTION_RISKBAND']);
    }
});
```

## Tuning

- Too aggressive on wp-login.php? Raise `ImmediateBlockThreshold` in the `strict` policy to `0.6`.
- Want to monitor before blocking? Change `DefaultActionPolicyName` to `logonly` and check logs
  for one week before enabling blocking.
- Running WooCommerce? See `../ecommerce/` for checkout-specific protection.
