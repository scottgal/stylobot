# Verified Bot Detection

Verifies bot identity claims using published IP ranges, Forward-Confirmed reverse DNS (FCrDNS), and honest bot detection. Identifies verified good bots (Googlebot, Bingbot), honest bots (self-identifying with matching rDNS), and spoofed bots (claiming a known identity but failing verification).

## How It Works

The detector runs in Wave 0 (priority 4) with no dependencies. It uses a `VerifiedBotRegistry` that maintains known bot definitions with their User-Agent patterns and verification methods. Detection proceeds through three tiers.

**Known bot verification**: When a User-Agent matches a known bot pattern (Googlebot, Bingbot, etc.), the detector verifies the claim via published CIDR ranges or FCrDNS lookup. If the client IP falls within published ranges or reverse DNS confirms the expected domain, the bot is marked as `VerifiedGoodBot` and triggers an early exit with high weight, allowing the request to bypass further analysis. If IP verification fails, the request is flagged as a **spoofed bot** with high confidence (default 0.85), indicating the UA claims a known identity but the IP does not match.

**Honest bot detection**: When a User-Agent is not a known bot but contains a URL (e.g., `MostlylucidBot/1.0 (+https://example.com/bot)`), the detector extracts the domain and performs reverse DNS on the client IP. If the rDNS hostname matches the UA-claimed domain, the bot is classified as an "honest bot" -- transparent about its identity but not from a known registry. If rDNS resolves to a different domain, a weak mismatch signal is emitted (low confidence, since CDNs and shared hosting can cause legitimate mismatches).

Reverse DNS results are cached for 30 minutes to avoid repeated lookups. The cache is capped at 5,000 entries (LRU eviction).

## Fediverse Domain Verification (FediverseDomainAtom, 7.5+)

For fediverse User-Agents (Mastodon, Pleroma, Misskey, Akkoma, GoToSocial, etc., which carry a `+https://instance/` URL in the UA), a separate contributor (`FediverseDomainAtom`, Priority 5) runs NodeInfo verification followed by a forward-DNS confirmation step.

**Why forward-DNS is required:** NodeInfo alone is insufficient -- anyone can put `+https://mastodon.social/` in their UA from any IP and NodeInfo will confirm the instance exists, so `FriendlyDomainVerified=true` would fire for a spoofer. The forward-DNS step resolves the claimed instance domain's A/AAAA records and checks whether the client IP is in the result set. This binds the claim to the request.

- Forward-DNS results are cached per instance hostname with a 5-minute TTL (bounded at 5,000 entries).
- A positive match emits `verifiedbot.forward_dns_matched = true` and `verifiedbot.method = "forward_dns"`.
- A failed DNS lookup emits `verifiedbot.forward_dns_error` with the exception type; absence of `verifiedbot.forward_dns_matched` stays distinguishable from a failed lookup.
- The `FediverseDomainVerifier` maintains a 24h positive / 1h negative cache for NodeInfo results, so only first-encounter domains pay the outbound HTTPS cost.

## Persistent Trust State (7.5+)

Verification results are now persisted to the `fingerprints` table so they survive process restarts. Previously, trust was an in-memory one-way latch on `SignatureCoordinator` and was lost on restart. The columns added in 7.5 are `claim_status`, `verification_method`, `verified_at`, and `trust_observations`. The verifier contributors read these at request entry and emit `verifiedbot.cached` to skip re-verification when the result is still within `TrustOptions.TrustCacheTtl`. See [`identity-fingerprint-match.md`](identity-fingerprint-match.md) for the full schema.

## Signals Emitted

| Signal Key | Type | Description |
|---|---|---|
| `verifiedbot.checked` | boolean | Whether verification was attempted |
| `verifiedbot.confirmed` | boolean | Whether bot identity was confirmed |
| `verifiedbot.name` | string | Name of the verified bot |
| `verifiedbot.method` | string | Verification method: `ip_range`, `fcrdns`, `forward_dns`, `nodeinfo`, or `none` |
| `verifiedbot.spoofed` | boolean | UA claims known bot but IP fails verification |
| `verifiedbot.rdns_mismatch` | boolean | UA domain does not match rDNS hostname |
| `verifiedbot.forward_dns_matched` | boolean | Client IP was found in the claimed fediverse instance's resolved A/AAAA records (FediverseDomainAtom) |
| `verifiedbot.forward_dns_error` | string | Exception type when forward-DNS lookup failed (SocketException, OperationCanceledException) |
| `verifiedbot.cached` | boolean | Re-verification was skipped because a prior result is within TrustOptions.TrustCacheTtl |
| `fediverse.domain_verified` | boolean | NodeInfo confirmed the claimed fediverse instance hosts ActivityPub software |

## Configuration

```json
{
  "BotDetection": {
    "Detectors": {
      "VerifiedBotAtom": {
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
