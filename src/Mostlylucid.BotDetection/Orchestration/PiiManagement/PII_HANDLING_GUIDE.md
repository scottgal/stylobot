# PII Handling Guide for Stylobot Detectors

## The Core Principle

**PII (Personally Identifiable Information) must NEVER be placed in signal payloads.**

This is a **critical architectural requirement**, not a nice-to-have. Violating this principle would:

- Break the zero-PII architecture
- Create GDPR/CCPA compliance issues
- Undermine the core value proposition of Stylobot

## What is PII?

For Stylobot's purposes, PII includes:

- IP addresses (full or partial)
- User agent strings
- Geographic coordinates (city, region, lat/lon)
- Session IDs
- Referer URLs
- Accept-Language headers (full strings)
- Any other data that could identify or track individuals

## What is NOT PII?

The following are safe for signals:

- Country codes (e.g., "US", "GB") - GDPR Article 4 excludes these
- Timezone names (e.g., "America/New_York") - behavioral data
- Primary language codes (e.g., "en", "fr") - non-identifying
- Boolean indicators (e.g., `ip.available`, `ua.available`)
- Hashed values using PiiHasher (e.g., `ipdetected:a3k9f2Jx-kL3s`)

## The Two-Channel Architecture

### Channel 1: State Properties (For Raw PII)

Detectors needing raw PII access it **directly from `BlackboardState` properties**:

```csharp
public async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
    BlackboardState state,
    CancellationToken cancellationToken)
{
    // ✅ CORRECT - Access raw PII from state properties
    var ip = state.ClientIp;
    var ua = state.UserAgent;
    var referer = state.Referer;
    var locale = state.AcceptLanguage;

    // Use raw PII for detection logic
    var isDatacenter = await _datacenterChecker.IsDatacenterIp(ip);

    // ...
}
```

**Available State Properties:**

- `state.ClientIp` - Raw IP address
- `state.UserAgent` - Raw user agent string
- `state.Referer` - Raw referer header
- `state.AcceptLanguage` - Raw Accept-Language header
- `state.SessionId` - Session/trace identifier
- `state.Path` - Request path
- `state.HttpContext` - Full HTTP context (use sparingly)

### Channel 2: Signals (For Indicators and Hashes)

When emitting signals, use `PiiSignalHelper` to ensure privacy-safe payloads:

```csharp
public async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
    BlackboardState state,
    CancellationToken cancellationToken)
{
    var ip = state.ClientIp;
    var ua = state.UserAgent;

    // ✅ CORRECT - Emit privacy-safe signals
    var signals = new Dictionary<string, object>();

    // Merge in IP signals (boolean + hash)
    foreach (var kvp in PiiSignalHelper.EmitIpSignals(ip, _hasher))
    {
        signals[kvp.Key] = kvp.Value;
    }

    // Merge in UA signals
    foreach (var kvp in PiiSignalHelper.EmitUserAgentSignals(ua, _hasher))
    {
        signals[kvp.Key] = kvp.Value;
    }

    // Result: signals = {
    //   "ip.available": true,
    //   "ip.detected": "a3k9f2Jx-kL3s",  // HMAC hash, not raw IP
    //   "ip.subnet": "xP2l9sK-fH8q",     // HMAC hash of /24 subnet
    //   "ua.available": true,
    //   "ua.detected": "mN7h4tR-9Kj2"    // HMAC hash, not raw UA
    // }

    return Single(new DetectionContribution
    {
        DetectorName = Name,
        Category = "Network",
        Confidence = 0.8,
        Weight = 1.0,
        Reason = "Datacenter IP detected",
        Signals = signals.ToImmutableDictionary()
    });
}
```

## Common Patterns

### Pattern 1: Simple IP Detection

```csharp
// Access raw IP from state
var ip = state.ClientIp;

// Use raw IP for detection logic
var isVpn = await _vpnChecker.IsVpnIp(ip);

// Emit privacy-safe signals
var signals = PiiSignalHelper.EmitIpSignals(ip, _hasher);
signals["ip.is_vpn"] = isVpn;  // Boolean is safe
```

### Pattern 2: Geographic Detection

```csharp
// Access raw IP from state
var ip = state.ClientIp;

// Look up geo data
var geoData = await _geoProvider.LookupAsync(ip);

// Emit privacy-safe geo signals
var signals = PiiSignalHelper.EmitGeoSignals(geoData, _hasher);

// Result: {
//   "geo.available": true,
//   "geo.country_code": "US",           // Country is NOT PII
//   "geo.timezone": "America/New_York", // Timezone is NOT PII
//   "geo.location_hash": "...",         // City/region are hashed
//   "geo.coordinates_hash": "..."       // GPS coords are hashed
// }
```

### Pattern 3: Composite Request Signature

```csharp
// Access raw PII from state
var ip = state.ClientIp;
var ua = state.UserAgent;
var path = state.Path;

// Create composite signature
var signals = PiiSignalHelper.EmitRequestSignature(ip, ua, path, _hasher);

// Result: {
//   "request.signature": "qW5e8uI-2Lp0"  // HMAC(IP|UA|Path)
// }

// This signature is deterministic (same IP+UA+Path = same hash)
// but non-reversible (cannot recover IP/UA from hash)
```

### Pattern 4: Triggering on Hashed Signals

```csharp
public override IReadOnlyList<TriggerCondition> TriggerConditions =>
[
    // ✅ CORRECT - Trigger on boolean indicator
    new SignalExistsTrigger(PiiSignalKeys.IpAvailable),

    // ✅ CORRECT - Trigger on hashed value
    new SignalExistsTrigger(PiiSignalKeys.IpDetected),

    // ❌ WRONG - Don't expect raw IP in signals
    // new SignalExistsTrigger("raw_ip")
];

public async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
    BlackboardState state,
    CancellationToken cancellationToken)
{
    // Even though we triggered on ip.available, we still access raw IP from state
    var ip = state.ClientIp;

    // NOT from signals:
    // var ip = state.GetSignal<string>("raw_ip"); // ❌ WRONG
}
```

## Anti-Patterns (DO NOT DO THIS)

### ❌ Anti-Pattern 1: Raw PII in Signals

```csharp
// ❌ WRONG - Raw IP in signal payload
var signals = new Dictionary<string, object>
{
    ["client_ip"] = "192.168.1.100",  // VIOLATION
    ["user_agent"] = "Mozilla/5.0...", // VIOLATION
    ["location"] = "San Francisco, CA" // VIOLATION
};
```

**Why wrong?** Signals are stored in the blackboard and may be logged, persisted, or transmitted. Raw PII in signals
breaks zero-PII architecture.

**Fix:**

```csharp
// ✅ CORRECT - Boolean indicators and hashes
var signals = PiiSignalHelper.EmitIpSignals("192.168.1.100", _hasher);
// Result: { "ip.available": true, "ip.detected": "a3k9f2Jx-kL3s" }
```

### ❌ Anti-Pattern 2: Partial IP Addresses

```csharp
// ❌ WRONG - Even partial IPs are PII
var signals = new Dictionary<string, object>
{
    ["ip_prefix"] = "192.168.1"  // Still identifying
};
```

**Why wrong?** IP prefixes can still identify subnets and networks.

**Fix:**

```csharp
// ✅ CORRECT - Hash the subnet
var signals = new Dictionary<string, object>
{
    [PiiSignalKeys.IpSubnet] = _hasher.HashIpSubnet("192.168.1.100")
};
```

### ❌ Anti-Pattern 3: Reading PII from Signals

```csharp
// ❌ WRONG - Expecting raw PII from signals
var ip = state.GetSignal<string>("client_ip");
```

**Why wrong?** Signals should never contain raw PII.

**Fix:**

```csharp
// ✅ CORRECT - Read raw PII from state properties
var ip = state.ClientIp;
```

### ❌ Anti-Pattern 4: Logging Raw PII

```csharp
// ❌ WRONG - Logging raw IP
_logger.LogInformation("Request from IP {Ip}", state.ClientIp);
```

**Why wrong?** Logs persist indefinitely and may be shipped to external systems.

**Fix:**

```csharp
// ✅ CORRECT - Log hashed values
_logger.LogInformation(
    "Request from IP hash {IpHash}",
    _hasher.HashIp(state.ClientIp));
```

## Signal Key Constants

Use `PiiSignalKeys` for consistent signal naming:

```csharp
// IP signals
PiiSignalKeys.IpAvailable        // "ip.available" (bool)
PiiSignalKeys.IpDetected         // "ip.detected" (hash)
PiiSignalKeys.IpSubnet           // "ip.subnet" (hash)

// UA signals
PiiSignalKeys.UaAvailable        // "ua.available" (bool)
PiiSignalKeys.UaDetected         // "ua.detected" (hash)

// Geo signals
PiiSignalKeys.GeoAvailable       // "geo.available" (bool)
PiiSignalKeys.GeoCountryCode     // "geo.country_code" (string, NOT PII)
PiiSignalKeys.GeoLocationHash    // "geo.location_hash" (hash)
PiiSignalKeys.GeoCoordinatesHash // "geo.coordinates_hash" (hash)
PiiSignalKeys.GeoTimezone        // "geo.timezone" (string, NOT PII)

// Session signals
PiiSignalKeys.SessionAvailable   // "session.available" (bool)
PiiSignalKeys.SessionDetected    // "session.detected" (hash)

// Locale signals
PiiSignalKeys.LocaleAvailable    // "locale.available" (bool)
PiiSignalKeys.LocaleLanguage     // "locale.language" (string, NOT PII)
PiiSignalKeys.LocaleDetected     // "locale.detected" (hash)

// Referer signals
PiiSignalKeys.RefererAvailable   // "referer.available" (bool)
PiiSignalKeys.RefererDetected    // "referer.detected" (hash)

// Request signature
PiiSignalKeys.RequestSignature   // "request.signature" (hash)
```

## PiiHasher Usage

### Injecting PiiHasher

```csharp
public class MyDetector : ContributingDetectorBase
{
    private readonly PiiHasher _hasher;

    public MyDetector(PiiHasher hasher)
    {
        _hasher = hasher;
    }
}
```

### Hashing Operations

```csharp
// Hash IP address
var ipHash = _hasher.HashIp("192.168.1.100");

// Hash IP subnet (/24, /16, /8)
var subnetHash = _hasher.HashIpSubnet("192.168.1.100", prefixLength: 24);

// Hash user agent
var uaHash = _hasher.HashUserAgent("Mozilla/5.0...");

// Hash location
var locationHash = _hasher.HashLocation("San Francisco, CA");

// Composite signature
var signature = _hasher.ComputeSignature("192.168.1.100", "Mozilla/5.0...", "/login");

// Behavior signature (zero-PII by construction)
var behaviorHash = _hasher.HashBehavior(
    "timing: 234ms, 189ms, 267ms",
    "headers: accept+encoding-lang",
    "path_shape: /blog/*/comments"
);
```

## Detector Checklist

Before shipping a detector, verify:

- [ ] ✅ Raw PII is accessed ONLY from `state` properties, never from signals
- [ ] ✅ Emitted signals use `PiiSignalHelper` or manual hashing via `PiiHasher`
- [ ] ✅ No raw IP addresses in signal payloads
- [ ] ✅ No raw user agent strings in signal payloads
- [ ] ✅ No geographic coordinates (city, lat/lon) in signal payloads
- [ ] ✅ No session IDs in signal payloads
- [ ] ✅ No referer URLs in signal payloads
- [ ] ✅ Logs use hashed values, not raw PII
- [ ] ✅ Contribution reasons don't leak PII (e.g., "Datacenter IP" not "IP 192.168.1.100 is datacenter")

## Summary

**The Golden Rules:**

1. **Access raw PII from `state` properties** - `state.ClientIp`, `state.UserAgent`, etc.
2. **Emit signals using `PiiSignalHelper`** - ensures boolean indicators + hashed values
3. **Never put raw PII in signal payloads** - this breaks zero-PII architecture
4. **Use `PiiHasher` for any manual hashing** - HMAC-SHA256 with secret keys
5. **Country codes and timezones are safe** - they're not PII per GDPR
6. **Log hashed values, not raw PII** - logs persist indefinitely

Following these rules ensures Stylobot can legitimately claim:

> **"We store literally zero PII. Raw IP/UA/location never touches disk - only non-reversible, keyed signatures are
persisted."**
