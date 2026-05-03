# Verified Bot Detection

Verifies bot identity claims using published IP ranges, Forward-Confirmed reverse DNS (FCrDNS), and honest bot detection. Identifies verified good bots (Googlebot, Bingbot), honest bots (self-identifying with matching rDNS), and spoofed bots (claiming a known identity but failing verification).

## How It Works

The detector runs in Wave 0 (priority 4) with no dependencies. It uses a `VerifiedBotRegistry` that maintains known bot definitions with their User-Agent patterns and verification methods. Detection proceeds through three tiers.

**Known bot verification**: When a User-Agent matches a known bot pattern (Googlebot, Bingbot, etc.), the detector verifies the claim via published CIDR ranges or FCrDNS lookup. If the client IP falls within published ranges or reverse DNS confirms the expected domain, the bot is marked as `VerifiedGoodBot` and triggers an early exit with high weight, allowing the request to bypass further analysis. If IP verification fails, the request is flagged as a **spoofed bot** with high confidence (default 0.85), indicating the UA claims a known identity but the IP does not match.

**Honest bot detection**: When a User-Agent is not a known bot but contains a URL (e.g., `MostlylucidBot/1.0 (+https://example.com/bot)`), the detector extracts the domain and performs reverse DNS on the client IP. If the rDNS hostname matches the UA-claimed domain, the bot is classified as an "honest bot" -- transparent about its identity but not from a known registry. If rDNS resolves to a different domain, a weak mismatch signal is emitted (low confidence, since CDNs and shared hosting can cause legitimate mismatches).

Reverse DNS results are cached for 30 minutes to avoid repeated lookups. The cache is capped at 50,000 entries.

## Signals Emitted

| Signal Key | Type | Description |
|---|---|---|
| `verifiedbot.checked` | boolean | Whether verification was attempted |
| `verifiedbot.confirmed` | boolean | Whether bot identity was confirmed |
| `verifiedbot.name` | string | Name of the verified bot |
| `verifiedbot.method` | string | Verification method: `ip_range`, `fcrdns`, `none` |
| `verifiedbot.spoofed` | boolean | UA claims known bot but IP fails verification |
| `verifiedbot.rdns_mismatch` | boolean | UA domain does not match rDNS hostname |

## Configuration

```json
{
  "BotDetection": {
    "Detectors": {
      "VerifiedBotContributor": {
        "Parameters": {
          "spoofed_ua_confidence": 0.85,
          "honest_bot_confidence": 0.3,
          "rdns_mismatch_confidence": 0.25,
          "dns_timeout_ms": 5000
        }
      }
    }
  }
}
```

## Parameters

| Parameter | Default | Description |
|---|---|---|
| `spoofed_ua_confidence` | 0.85 | Confidence when UA claims known bot but IP fails |
| `honest_bot_confidence` | 0.3 | Confidence for honest bot (UA domain matches rDNS) |
| `rdns_mismatch_confidence` | 0.25 | Confidence for rDNS domain mismatch |
| `dns_timeout_ms` | 5000 | DNS lookup timeout in milliseconds |
| `dns_verified_cache_ttl_hours` | 24 | Cache TTL for verified results |
| `dns_failed_cache_ttl_hours` | 1 | Cache TTL for failed DNS lookups |
| `ip_range_refresh_hours` | 24 | Interval for refreshing published IP ranges |
