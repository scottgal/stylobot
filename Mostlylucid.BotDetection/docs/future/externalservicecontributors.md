# Feature Specification: External Service Contributors

## Overview

Enable the bot detection pipeline to incorporate **external intelligence services** (e.g., UA‑free check‑in APIs, IP
reputation feeds, header validators, bot‑intel APIs) as **first‑class contributing detectors**. These services act as
federated members of the blackboard/quorum, providing additional signals and confidence scores to strengthen verdicts.

---

## Goals

- **Federated intelligence**: Augment internal detectors with external sources of truth.
- **Explainability**: Log external verdicts as transparent contributions in JSON scorecards.
- **Trust weighting**: Apply configurable weights to external services based on accuracy and reliability.
- **Resilience**: Ensure the system continues functioning even if external services fail or degrade.
- **Evolutionary potential**: External contributors participate in the fitness landscape, allowing adaptive weighting
  over time.

---

## Architecture

1. **Trigger Conditions**
    - Fires after basic detectors (UA, headers, IP) have produced signals.
    - Configurable to run in Wave 1+ depending on latency tolerance.

2. **Pipeline Flow**
    - **Fast Path**: Internal deterministic detectors.
    - **Heuristic Contributor**: Lightweight adaptive rules.
    - **External Service Contributors**:
        - Call external APIs (UA‑free check‑in, IP reputation, etc.).
        - Parse responses into `DetectionContribution` objects.
        - Add signals such as `ExternalVerdict`, `ExternalConfidence`, `ExternalReasons`.
    - **LLM Contributors**: Deep reasoning for ambiguous cases.
    - **Quorum**: Weighted contributions (internal + external) produce final verdict.

3. **Outputs**
    - Signals:
        - `ExternalVerdict` (bot/human/suspicious)
        - `ExternalConfidence` (0–1)
        - `ExternalSource` (service name)
    - Reasons:
        - “External UA‑free API flagged datacenter IP.”
        - “IP reputation feed marked address as high‑risk.”
    - Weight: Configurable (default 1.5–2.0, adjustable based on service trust).

---

## Security & Trust

- **Reputational weighting**: Services gain or lose influence based on historical accuracy.
- **Cryptographic signing**: External contributions verified to prevent tampering.
- **Timeout handling**: Failures logged as neutral contributions (“External service unavailable”).
- **Privacy compliance**: External calls restricted to non‑PII signals (UA, IP, headers).

---

## Performance Targets

- **API call latency**: < 200ms per external contributor.
- **Contribution runtime**: < 250ms end‑to‑end.
- **Failure resilience**: System continues with quorum even if external contributors fail.

---

## Future Extensions

- **Federated registries**: Multiple external services combined with trust weighting.
- **Adaptive weighting**: Fitness evaluators adjust external service influence dynamically.
- **Cross‑instance sharing**: External verdicts cached in registry for reuse.
- **Dashboard integration**: Visualize external contributions alongside internal detectors.

---

This spec positions external services as **federated immune cells** in your architecture — augmenting your innate and
adaptive detectors with global intelligence, while still preserving explainability and quorum balance.
