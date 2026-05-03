# Proof-of-Work Challenge System

StyloBot's proof-of-work challenge is a **response action**, not a detection mechanism. Detection is the product - the challenge is one of many response options, and it's the smart one because difficulty is evidence-driven.

## How It Works

1. Detection pipeline runs (49-detector pipeline, wave-gated) and scores the visitor
2. If the score triggers a `challenge-pow` action policy, the visitor gets a PoW challenge
3. The client's browser solves SHA-256 micro-puzzles using Web Workers in parallel
4. Solutions are POSTed to `/bot-detection/challenge/verify`
5. On verification, a signed HMAC token cookie is issued (valid 30 minutes)
6. On the **next request**, the `ChallengeVerificationContributor` reads the solve metadata and emits human/bot signals based on solve characteristics

## Architecture

### Micro-Puzzle Model (not Argon2)

SHA-256 micro-puzzles (like Friendly Captcha) instead of Argon2 WASM because:
- `crypto.subtle.digest('SHA-256')` is in every browser, hardware-accelerated, zero dependencies
- Argon2 WASM is ~200KB, flaky on mobile Safari, hard to debug
- Economic cost scales via puzzle count (4-32), not hash hardness

Each puzzle: find a nonce such that `SHA256(seed + nonce)` has N leading zero hex characters.

### Blackboard-Driven Difficulty

Difficulty scales based on the full blackboard signal set, not just bot probability:

| Signal | Effect |
|--------|--------|
| `evidence.BotProbability` | Base: 4 puzzles at 0.5, up to 32 at 1.0 |
| `session.velocity_magnitude > 0.5` | +4 puzzles (behavioral shift) |
| `cluster.type` present | +8 puzzles (in a bot cluster) |
| `reputation.bias_applied` | +4 puzzles (known bad) |
| `intent.threat_score > 0.5` | +1 zero per puzzle (harder hash) |

### Transport-Aware

- **Browser clients**: HTML page with Web Worker pool, progress bar, auto-submit
- **API/SignalR/gRPC clients**: 429 + `Retry-After` header + JSON challenge details (challenge ID, puzzle seeds, verify URL)

### Challenge-as-Signal (Confidence Booster)

The verification result feeds BACK as a detection signal on subsequent requests:

| Solve Characteristic | Signal |
|---------------------|--------|
| Realistic timing (200-5000ms/puzzle) | Human signal (-0.35 delta) |
| Too fast (<50ms/puzzle) | Bot signal (+0.15 delta) |
| Low timing jitter (<0.05 CV) with 4+ puzzles | Bot signal (+0.10 delta) |
| High timing jitter (>0.15 CV) | Human signal (-0.05 delta) |
| 0-1 Web Workers | Bot signal (+0.08 delta) |

A visitor scoring 0.6 (edge case) who solves a PoW with realistic browser timing gets pushed below threshold, reducing false positives.

### Server-Side Store

Challenges are persisted to **SQLite** (FOSS) or **PostgreSQL** (commercial). Single-use validation via atomic `UPDATE ... RETURNING` ensures a challenge can't be replayed.

## Configuration

```json
{
  "BotDetection": {
    "ActionPolicies": {
      "my-pow": {
        "Type": "Challenge",
        "ChallengeType": "ProofOfWork",
        "BasePuzzleCount": 4,
        "MaxPuzzleCount": 32,
        "BaseDifficultyZeros": 3,
        "MaxDifficultyZeros": 5,
        "ChallengeExpirySeconds": 120,
        "VerifyEndpoint": "/bot-detection/challenge/verify"
      }
    }
  }
}
```

## Endpoint Security

The verify endpoint runs **full detection** with `BotPolicyAttribute(BlockThreshold = 0.95)`. Detection still runs, signals still collected, reputation still built - but only confirmed bots (0.95+) are blocked. The edge case visitor who triggered the challenge passes through.

No skip paths. No bypasses. Detection always runs.
