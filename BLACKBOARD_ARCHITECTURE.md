# Blackboard Architecture Change

## What Changed?

The bot detection system transitioned from a **lane-based** architecture to a **blackboard pattern** architecture for analyzing request and response signals.

## Old Architecture: Lane-Based

### How It Worked

In the old system, we had separate "lanes" that ran in parallel:

```
Request → ┌─────────────────┐
          │ BehavioralLane  │ → Score: 0.65
          ├─────────────────┤
          │ SpectralLane    │ → Score: 0.72
          ├─────────────────┤
          │ ReputationLane  │ → Score: 0.58
          └─────────────────┘
                 ↓
          Combine Scores → Final: 0.68
```

**Key Components:**
- `BehavioralLane` - Analyzed behavior patterns (request rate, path sequences, etc.)
- `SpectralLane` - Analyzed frequency patterns and timing
- `ReputationLane` - Looked up IP/signature reputation
- `OperationCompleteSignal` - Signal sent when request/response complete

**Problems:**
- Rigid structure - hard to add new analysis types
- Lane coupling - lanes couldn't easily share insights
- Signal duplication - same data sent to multiple lanes
- Complex coordination - managing lane lifecycle and results

## New Architecture: Blackboard Pattern

### How It Works

The blackboard pattern is inspired by how experts collaborate on a problem by writing observations on a shared blackboard that everyone can read and contribute to.

```
                  ┌─────────────────────────┐
                  │      Blackboard         │
                  │   (SignalSink)          │
                  ├─────────────────────────┤
                  │ request.ip.datacenter   │
                  │ request.path.honeypot   │
                  │ request.detector.risk   │
                  │ response.status.code    │
                  │ response.detector.score │
                  │ ... any signal ...      │
                  └─────────────────────────┘
                        ↑         ↓
            ┌───────────┴─────────┴────────────┐
            │                                   │
    ┌───────▼───────┐                  ┌───────▼───────┐
    │   Contributor  │                  │   Contributor  │
    │   (Detector)   │                  │  (Escalator)   │
    │                │                  │                │
    │ - Reads signals│                  │ - Reads signals│
    │ - Writes new   │                  │ - Makes decision│
    │   observations │                  │                │
    └────────────────┘                  └────────────────┘
```

**Key Components:**

1. **Blackboard (SignalSink)**: Central shared memory where all signals are written
   - Any component can read any signal
   - Signals are timestamped and typed
   - Pattern matching for finding related signals

2. **Contributors**: Independent components that read from and write to the blackboard
   - **Detectors**: Write risk signals (e.g., `request.detector.risk = 0.85`)
   - **Escalators**: Read signals and make decisions (store, alert, block)
   - **Response Analyzers**: Write response analysis signals

3. **Signal Keys**: Hierarchical naming like file paths
   ```
   request.ip.datacenter = "AWS"
   request.path.honeypot = true
   request.detector.risk = 0.85
   response.status.code = 404
   response.detector.score = 0.72
   ```

4. **Pattern Matching**: Find signals using wildcards
   ```csharp
   // Find any risk signal from any detector
   var risks = sink.FindMatching("request.*.risk");

   // Find any response status
   var status = sink.FindMatching("response.status*");
   ```

### Example Flow

1. **Request arrives**:
   ```
   Blackboard: []
   ```

2. **IP Detector runs**:
   ```csharp
   // IMPORTANT: Raw IPs are NEVER stored in the blackboard!
   // Only privacy-safe indicators are written:
   sink.Raise(new SignalKey("request.ip.datacenter"), "AWS");
   sink.Raise(new SignalKey("request.ip.is_datacenter"), "true");
   ```
   ```
   Blackboard (in-memory only, ephemeral): [
     request.ip.datacenter = "AWS"
     request.ip.is_datacenter = "true"
   ]

   // Raw IP "52.1.2.3" exists ONLY in state.ClientIp property
   // - Accessed directly by detectors that need it
   // - Never written to blackboard
   // - Cleared from memory when request completes
   ```

3. **Path Detector runs**:
   ```csharp
   // Detector reads path from state (not from blackboard):
   var path = state.Path;  // e.g., "/wp-admin"

   // Check if honeypot and write indicator only:
   sink.Raise(new SignalKey("request.path.honeypot"), "true");
   sink.Raise(new SignalKey("request.path.type"), "admin");

   // Path value itself is NOT stored on blackboard!
   ```
   ```
   Blackboard: [
     request.ip.datacenter = "AWS"
     request.ip.is_datacenter = "true"
     request.path.honeypot = "true"  ← NEW! (boolean flag, not path)
     request.path.type = "admin"     ← NEW! (category, not path)
   ]

   // NOTE: No IP address, no path value!
   // Only privacy-safe metadata and boolean indicators.
   ```

4. **Escalator reads all signals and generates hashed signature**:
   ```csharp
   var honeypot = sink.GetLatest("request.path.honeypot"); // "true"
   var datacenter = sink.GetLatest("request.ip.datacenter"); // "AWS"

   // Decision: Honeypot + Datacenter = High risk!
   if (honeypot == "true" && datacenter != null) {
       // If storage enabled, create privacy-safe signature:
       var hasher = new PiiHasher(secretKey);
       var signature = hasher.ComputeSignature(
           state.ClientIp,    // Raw IP from state (not blackboard!)
           state.UserAgent    // Raw UA from state (not blackboard!)
       );
       // signature = "XpK3nR8vMq-_Wd7" (HMAC hash)

       // Store ONLY the hash + non-PII metadata:
       await StoreSignature(new {
           Signature = signature,     // "XpK3nR8vMq-_Wd7" (hashed)
           Datacenter = datacenter,   // "AWS" (not PII)
           Honeypot = true,           // boolean (not PII)
           Risk = 0.95                // score (not PII)
       });
       // Raw IP/UA never touches disk!
   }
   ```

## Why Blackboard Is Better

### 1. **Flexibility**
Old:
```csharp
// Adding a new analysis type requires:
// - New lane class
// - Wire into coordinator
// - Manage lifecycle
public class MyNewLane : ILane { ... }
```

New:
```csharp
// Just write signals to the blackboard
sink.Raise(new SignalKey("request.custom.analysis"), value);
// Any component can read it immediately
```

### 2. **Dynamic Signal Extraction**
Old:
```csharp
// Hard-coded properties
var risk = operationSignal.RequestRisk;
var honeypot = operationSignal.Honeypot;
```

New:
```csharp
// Pattern-based extraction
var config = new {
    RequestPatterns = {
        ["risk"] = "request.*.risk",
        ["honeypot"] = "request.*.honeypot"
    }
};
var matcher = new SignalPatternMatcher(config.RequestPatterns);
var signals = matcher.ExtractFrom(sink);
// Automatically finds any matching signals
```

### 3. **No Signal Duplication**
Old:
```csharp
// Send same data to multiple lanes
await behavioralLane.Analyze(operation);
await spectralLane.Analyze(operation);
await reputationLane.Analyze(operation);
```

New:
```csharp
// Write once, read many times
sink.Raise(new SignalKey("request.risk"), 0.85);
// Any contributor can read it when needed
```

### 4. **Easy Testing**
Old:
```csharp
// Must mock entire lane infrastructure
var lane = new BehavioralLane(sink);
var operation = new OperationCompleteSignal { ... };
await lane.Analyze(operation);
```

New:
```csharp
// Just populate blackboard with signals
var sink = new SignalSink();
sink.Raise(new SignalKey("request.risk"), 0.85);
sink.Raise(new SignalKey("request.honeypot"), true);
// Test escalator logic directly
```

## Code Migration Guide

### If You Were Using Lanes

**Old Code:**
```csharp
var behavioralLane = new BehavioralLane(sink);
var spectralLane = new SpectralLane(sink);
var window = new List<OperationCompleteSignal> { ... };

await behavioralLane.AnalyzeAsync(window);
await spectralLane.AnalyzeAsync(window);
```

**New Code:**
```csharp
// Contributors automatically write to blackboard
// No manual lane management needed

// Read signals using pattern matching
var matcher = new SignalPatternMatcher(new Dictionary<string, string> {
    ["risk"] = "request.*.risk",
    ["score"] = "response.*.score"
});

var signals = matcher.ExtractFrom(sink);
var risk = signals.TryGetValue("risk", out var r) ? (double)r : 0.0;
```

### If You Were Using OperationCompleteSignal

**Old Code:**
```csharp
var signal = new OperationCompleteSignal {
    Signature = "test-sig",
    RequestRisk = 0.85,
    ResponseScore = 0.72,
    Honeypot = true,
    Datacenter = "AWS"
};
await coordinator.ReceiveOperationAsync(signal);
```

**New Code:**
```csharp
// Write individual signals to blackboard
sink.Raise(new SignalKey("request.detector.risk"), 0.85);
sink.Raise(new SignalKey("request.path.honeypot"), true);
sink.Raise(new SignalKey("request.ip.datacenter"), "AWS");
sink.Raise(new SignalKey("response.detector.score"), 0.72);

// Escalator reads signals via pattern matching
var escalator = new SignatureEscalatorAtom(
    sink,
    signature,
    requestId,
    coordinatorCache,
    logger);

await escalator.OnOperationCompleteAsync();
```

## Real-World Analogy

### Lane-Based (Old)
Like an assembly line where each station does one specific job in sequence:

```
Car → [Paint Station] → [Wheel Station] → [Seat Station] → Done
```
- Workers can't see what others are doing
- Can't skip stations even if not needed
- Hard to add new stations without stopping the line

### Blackboard (New)
Like a team of experts solving a mystery by sharing clues on a whiteboard:

```
Whiteboard:
- "Fingerprints found at scene" (Detective A)
- "Suspect owns a blue car" (Detective B)
- "Blue car seen near scene" (Detective C)
```
- Everyone sees all the clues
- Anyone can add new clues
- Experts collaborate naturally
- Easy to bring in specialists

## CRITICAL: Zero-PII Architecture

### PII is NEVER Stored on the Blackboard

The blackboard (SignalSink) is **ephemeral in-memory only** and contains **zero PII by design**:

```csharp
// ❌ WRONG - Never do this:
sink.Raise(new SignalKey("request.ip"), "52.1.2.3");  // RAW IP = PII!

// ✅ CORRECT - Privacy-safe indicators only:
sink.Raise(new SignalKey("request.ip.is_datacenter"), "true");
sink.Raise(new SignalKey("request.ip.datacenter"), "AWS");
```

**Where Raw PII Lives:**
- **In Memory Only**: `state.ClientIp`, `state.UserAgent` properties
- **Accessed Directly**: Detectors read from state, not signals
- **Never Persisted to Disk**: Cleared when request completes
- **CRITICAL**: Raw IP/UA are ONLY used to compute the HMAC signature hash, then discarded

### How Signatures ARE Stored (Core Feature)

Signature storage is **ALWAYS ON** for high-confidence detections. Signatures use **HMAC-SHA256 with secret keys**:

```csharp
// In PiiHasher.cs:
var hasher = new PiiHasher(secretKey);  // 256-bit random key from Key Vault

// Raw IP comes from state.ClientIp (in-memory, never on blackboard):
string signature = hasher.HashIp(state.ClientIp);
// Input: [raw IP in memory]
// Output: "XpK3nR8vMq-_Wd7" (truncated base64url HMAC, non-reversible)

// Stored in DB: ONLY the hash + non-PII signals
{
  "signatureId": "XpK3nR8vMq-_Wd7",  // HMAC hash (keyed with secret) - THE KEY
  "signals": {
    "is_datacenter": true,          // Non-PII indicator
    "datacenter_name": "AWS",       // Non-PII metadata
    "honeypot_hit": true,           // Behavioral flag
    "bot_probability": 0.85,        // Score
    "risk_band": "High"             // Classification
  },
  "timestamp": "2025-12-11T12:34:56Z",
  "path": "/api/users"               // Can be stored (not PII)
}

// ✅ Zero PII stored: NO raw IP, NO raw UA
// ✅ SignatureId = HMAC(IP + UA) - consistent but non-reversible
// ✅ All other fields are privacy-safe indicators and behavioral signals
// ✅ Hash is consistent: Same IP → same hash (for pattern detection)
// ✅ Hash is private: Without secret key, can't reverse or brute-force
```

**Security Properties:**
- **Non-Reversible**: Uses HMAC (keyed hash), not encryption
- **Secret Key**: 256-bit key stored securely (Key Vault, env var)
- **Never Together**: Secret key NEVER stored with signatures
- **Consistent**: Same IP → same hash (for pattern detection)
- **Rotation**: Keys can rotate daily/weekly via HKDF derivation

**You CAN Verify Signatures (But Never Share the Key):**
```csharp
// Developer tool for debugging - verify a detection:
var hasher = new PiiHasher(secretKey);
var suspectedIp = "[IP from logs]";  // Only for local debugging
var testHash = hasher.HashIp(suspectedIp);
// Check if testHash matches stored signature

// ⚠️ CRITICAL SECURITY WARNINGS:
// 1. NEVER commit the secret key to version control
// 2. NEVER share signatures alongside the secret key
// 3. NEVER log raw IPs in production
// 4. Key must be stored in Key Vault / secure env var
//
// Without the key: signatures are privacy-safe, can't be reversed
// With the key: could theoretically brute-force common IPs (but slow)
```

**Signature Storage:** Always enabled for high-confidence bot/human detections - this is how the system learns and tracks patterns over time!

**CRITICAL ARCHITECTURE NOTE:**
- **SignatureId** = HMAC-SHA256 hash of (Raw IP + Raw UA) - stored in DB ✅
- **Raw IP** = NEVER stored in DB - only in-memory for hash computation ❌
- **Raw UA** = NEVER stored in DB - only in-memory for hash computation ❌
- **Signals** = Privacy-safe indicators (datacenter flags, bot scores, etc.) - stored in DB ✅
- **Path, Method, Timestamp** = Non-PII metadata - stored in DB ✅

The SignatureId is the **key** that allows pattern detection (same IP+UA → same hash) while maintaining **zero PII** (hash is non-reversible without secret key).

## Benefits Summary

| Aspect | Lane-Based | Blackboard |
|--------|-----------|-----------|
| **Flexibility** | Rigid lanes | Dynamic signals |
| **Extensibility** | Add new lane | Just write signals |
| **Collaboration** | Isolated | Shared knowledge |
| **Testing** | Complex mocking | Simple signal population |
| **Performance** | Fixed overhead | On-demand reading |
| **Debugging** | Lane lifecycle complex | Signal history visible |
| **Privacy** | Same | ZERO PII on blackboard |

## Key Classes

- **`SignalSink`**: The blackboard (from `Mostlylucid.Ephemeral`)
- **`SignalKey`**: Hierarchical signal names
- **`SignalPatternMatcher`**: Find signals by pattern
- **`SignatureEscalatorAtom`**: Reads blackboard and makes decisions
- **`EscalatorConfig`**: Pattern definitions for signal extraction
- **`EscalationRule`**: Condition-based decision rules

## Further Reading

- Blackboard pattern: https://en.wikipedia.org/wiki/Blackboard_(design_pattern)
- See `Mostlylucid.BotDetection\Orchestration\SignatureEscalatorAtom.cs` for implementation
- See `Mostlylucid.BotDetection\Orchestration\Escalation\EscalatorConfig.cs` for pattern examples
