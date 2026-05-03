# Multi-Factor Signatures - Plain English Explanation

## What Are Signatures?

When you visit a website using this bot detection system, it creates **multiple privacy-safe signatures** to identify
your browser and network. Think of these like fingerprints - but they're cryptographically hashed so your actual
information (IP address, browser details) is never stored.

## Why Multiple Signatures?

Using multiple signature factors solves real-world problems:

### Problem 1: Your IP Address Changes

**Scenario**: You're on mobile and switch from WiFi to cellular, or your ISP rotates your IP address.

**Without multi-factor**: The system would think you're a completely different person.

**With multi-factor**: Your User-Agent and Browser Fingerprint signatures still match, so the system recognizes you're
the same user who just changed networks.

### Problem 2: Your Browser Updates

**Scenario**: Chrome auto-updates from version 119 to 120, changing your User-Agent string.

**Without multi-factor**: The system would think you're a new user.

**With multi-factor**: Your IP and Browser Fingerprint signatures still match, so the system knows it's just a browser
update.

### Problem 3: False Positives

**Scenario**: You and a coworker share the same office network (same IP) and both use the latest Chrome (same
User-Agent).

**Without multi-factor**: The system might think you're the same person.

**With multi-factor**: Your Browser Fingerprint signatures are different (different hardware), so the system correctly
identifies you as different users.

## The Six Signature Factors

### 1. Primary Signature (IP + User-Agent)

- **What it is**: HMAC hash of your IP address combined with your browser's User-Agent string
- **When it matches**: Exact same client, same network, same browser
- **Stability**: Low - changes if either IP or browser updates
- **Example**: `XpK3nR8vMq-_...` (truncated hash)

**Plain English**: "This is your main ID card - it only matches if everything is exactly the same."

---

### 2. IP Signature

- **What it is**: HMAC hash of just your IP address
- **When it matches**: Same network location, even if browser changes
- **Stability**: Medium - stable within ISP session (days to weeks for static IPs)
- **Example**: `7Qm9Kp2LnWx...` (truncated hash)

**Plain English**: "This tracks your network location. If you update your browser but stay on the same WiFi, this still
matches."

---

### 3. User-Agent Signature

- **What it is**: HMAC hash of just your browser's User-Agent string
- **When it matches**: Same browser/device, even if network changes
- **Stability**: Medium-High - stable until browser updates
- **Example**: `9ZxV3RtYpLm...` (truncated hash)

**Plain English**: "This tracks your browser. If you move from WiFi to cellular (IP changes) but keep using the same
phone, this still matches."

---

### 4. Browser Fingerprint Signature

- **What it is**: HMAC hash of hardware-based browser characteristics (Canvas rendering, WebGL capabilities,
  AudioContext processing, screen resolution, timezone, fonts)
- **When it matches**: Same physical device, even if network AND browser change
- **Stability**: High - survives IP and browser updates
- **Example**: `4Hn7Wp9QmKx...` (truncated hash)

**Plain English**: "This is your device's unique 'hardware fingerprint'. Even if you travel abroad (new IP) and update
your browser (new User-Agent), your GPU still renders graphics the same way."

---

### 5. Browser Configuration Signature

- **What it is**: HMAC hash of installed plugins, extensions, fonts, Accept-Language, Accept-Encoding, DNT preference
- **When it matches**: Same browser installation with same extensions
- **Stability**: High - rarely changes
- **Example**: `2Lt6Mn8KpVw...` (truncated hash)

**Plain English**: "This tracks your browser's unique setup - like having uBlock Origin + Privacy Badger + specific
language settings. This rarely changes unless you reinstall your browser."

---

### 6. Network Subnet Signature

- **What it is**: HMAC hash of your IP's /24 subnet (e.g., 203.0.113.0/24 groups all IPs from 203.0.113.0 to
  203.0.113.255)
- **When it matches**: Same datacenter or corporate network
- **Stability**: Medium - stable within ISP allocation
- **Example**: `8Xt4Bp7NmLq...` (truncated hash)

**Plain English**: "This groups traffic from the same building or datacenter. Useful for detecting coordinated attacks
from the same location."

---

## How Matching Works

### Minimum 2 Factors Required

To avoid false positives, the system requires **at least 2 signature factors** to match before identifying you as a
returning user.

### Confidence Levels

**100% Confidence - Exact Match**

- Primary signature matches (IP + UA both identical)
- **Scenario**: You're using the same browser on the same network as before

**90% Confidence - Dynamic IP**

- User-Agent + Browser Fingerprint + Browser Config match
- IP changed
- **Scenario**: Mobile user switching between WiFi and cellular

**85% Confidence - Browser Update**

- IP + Browser Fingerprint + Browser Config match
- User-Agent changed
- **Scenario**: Your browser auto-updated overnight

**70% Confidence - Partial Match**

- Any 2+ factors match
- **Scenario**: Some changes, but still likely the same user

**0% Confidence - No Match**

- Only 1 or 0 factors match
- **Scenario**: Different user, or you changed too many things at once

## Privacy Protection

### What Gets Stored

✅ **ONLY cryptographic hashes** (HMAC-SHA256 with secret key)

- Example: `XpK3nR8vMq-_Wd7jLmN8pQrS9tUvW2xY4z`

✅ **Privacy-safe indicators** (non-PII)

- Example: `is_datacenter=true`, `datacenter_name="AWS"`, `bot_probability=0.95`

### What NEVER Gets Stored

❌ **Raw IP addresses** (e.g., `203.0.113.42`)
❌ **Raw User-Agent strings** (e.g., `Mozilla/5.0 Chrome/120...`)
❌ **Any personally identifying information**

### Can You Reverse the Hashes?

**No.** HMAC-SHA256 with a secret key is cryptographically non-reversible:

- You can't get the original IP from the hash without the secret key
- Brute-forcing requires knowing the secret key (which only the server has)
- Each deployment uses a different secret key
- Key rotation prevents long-term correlation

### Daily Key Rotation (Optional)

Deployments can enable daily key derivation using HKDF (Key Derivation Function):

- Master key + current date → unique daily key
- Same user today: signature `XpK3nR...`
- Same user tomorrow: signature `9ZxV3R...` (different!)
- Prevents tracking users across multiple days

## Real-World Examples

### Example 1: Mobile User on Vacation

```
Initial request (at home on WiFi):
  IP Signature: Abc123...
  UA Signature: Def456...
  Browser Fingerprint: Ghi789...
  → Match: Exact (100%)

Traveling (mobile network):
  IP Signature: Xyz999...  ← CHANGED (new network)
  UA Signature: Def456...  ← SAME
  Browser Fingerprint: Ghi789...  ← SAME
  → Match: Dynamic IP (90%)
  → Identified as same user, different network ✓
```

### Example 2: Browser Auto-Update

```
Before update:
  IP Signature: Abc123...
  UA Signature: Chrome119...
  Browser Fingerprint: Ghi789...
  → Match: Exact (100%)

After Chrome updates to 120:
  IP Signature: Abc123...  ← SAME
  UA Signature: Chrome120...  ← CHANGED (browser updated)
  Browser Fingerprint: Ghi789...  ← SAME (hardware unchanged)
  → Match: Browser Update (85%)
  → Identified as same user, updated browser ✓
```

### Example 3: Corporate Network (No False Positive)

```
Employee A:
  IP Signature: Corp123...  ← Same office
  UA Signature: Chrome120...  ← Same browser version
  Browser Fingerprint: GPU_Intel...  ← Intel GPU

Employee B (same office):
  IP Signature: Corp123...  ← Same office
  UA Signature: Chrome120...  ← Same browser version
  Browser Fingerprint: GPU_Nvidia...  ← Nvidia GPU (DIFFERENT)
  → Match: Weak (0%)
  → Correctly identified as different users ✓
```

### Example 4: Bot with Rotating Proxies

```
Bot Request 1:
  IP Signature: Datacenter1...
  UA Signature: HeadlessChrome...
  Browser Fingerprint: None  ← Headless = no canvas
  → First request

Bot Request 2 (10 seconds later, rotated proxy):
  IP Signature: Datacenter2...  ← CHANGED (proxy rotation)
  UA Signature: HeadlessChrome...  ← SAME
  Browser Fingerprint: None  ← Still no canvas
  → Match: Weak (0%) - only 1 factor (UA)
  → Pattern emerges: same UA, multiple datacenters, no fingerprint
  → Detected as bot ✓
```

## Summary

**Multi-factor signatures solve real problems:**

- ✅ Handle dynamic IPs (mobile users, VPNs)
- ✅ Handle browser updates (auto-updates)
- ✅ Avoid false positives (shared corporate networks)
- ✅ Detect bots (proxy rotation, headless browsers)
- ✅ Maintain privacy (no raw PII stored, only hashes)

**Key Benefits:**

- **Robust tracking** - Works even when network or browser changes
- **Privacy-safe** - HMAC hashes are non-reversible
- **Configurable** - Tune thresholds for your use case
- **Pattern detection** - Correlate requests across signature factors
- **Zero-PII** - Compliant with GDPR, CCPA, etc.
