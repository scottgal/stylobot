# Feature Specification: Performance Contributor with Policy Profiles & Prometheus Integration

## Overview

Introduce a **PerformanceContributor** that benchmarks all contributing detectors, characterizes their expense, and
orchestrates detection profiles dynamically. Profiles (Lean, Balanced, Paranoid) are selected based on **real‑time
Prometheus metrics** (latency, CPU, memory, cache hit/miss, error rates) and policy thresholds. This enables the system
to adaptively dial detection depth up or down depending on current performance and risk.

---

## Goals

- **Self‑tuning pipeline**: Automatically adjust contributors based on system load and traffic risk.
- **Policy‑driven orchestration**: Allow administrators to define thresholds for profile switching.
- **Unified observability**: Reuse Prometheus metrics for contributor benchmarking.
- **Explainability**: Log contributor expense profiles and profile decisions in JSON scorecards.
- **Resilience**: Ensure graceful fallback to balanced profile if metrics unavailable.

---

## Architecture

### 1. Contributor Benchmarking

- **Metrics collected (via Prometheus)**:
    - Runtime (ms per execution)
    - Memory usage (MB)
    - Cache hit/miss ratio
    - Timeout/failure rate
- **Frequency**: Continuous logging with rolling averages (e.g., 5‑minute windows).
- **Output**: `ContributorExpenseProfile` (light/medium/heavy).

### 2. PerformanceContributor

- **Priority**: 5 (early wave, before heuristics).
- **Action**:
    - Queries Prometheus for system metrics.
    - Aggregates contributor expense profiles.
    - Selects detection profile (Lean, Balanced, Paranoid).
    - Signals contributors to enable/disable based on profile.

### 3. Policy Profiles

| Profile      | Active Contributors                        | Trigger Condition                    | Latency Target |
|--------------|--------------------------------------------|--------------------------------------|----------------|
| **Lean**     | Strict rules + lightweight heuristics      | High latency / resource pressure     | < 50ms         |
| **Balanced** | Adds IP reputation, ASN/Geo, header sanity | Normal load                          | < 150ms        |
| **Paranoid** | Full stack incl. external services + LLM   | High‑risk traffic / suspicious surge | < 500ms        |

- **Policy thresholds**:
    - Latency > 200ms → switch to Lean.
    - CPU > 80% or memory > 75% → switch to Lean.
    - Suspicious traffic surge (e.g., > 20% flagged IPs) → switch to Paranoid.

### 4. Outputs

- **Signals**:
    - `PerformanceProfile` (lean/balanced/paranoid).
    - `ContributorExpenseProfiles` (map of contributor → cost).
- **Reasons**:
    - “Switched to lean profile due to high latency.”
    - “Enabled paranoid profile for suspicious traffic surge.”

---

## Security & Trust

- **Fail‑safe**: If Prometheus metrics unavailable, default to Balanced profile.
- **Explainability**: Scorecards log profile decisions and contributor costs.
- **Policy control**: Admins define thresholds via config or PromQL queries.

---

## Performance Targets

- **Overhead**: < 20ms per evaluation.
- **Profile switching latency**: < 100ms.
- **Contributor benchmarking accuracy**: ±5% runtime measurement.

---

## Future Extensions

- **Fitness integration**: Combine expense with accuracy to select optimal contributors.
- **Adaptive evolution**: Profiles evolve based on traffic patterns and contributor fitness.
- **Cross‑instance sharing**: Performance metrics propagated across servers for global optimization.
- **Dashboard integration**: Visualize contributor expense and profile switching in Prometheus/Grafana.

---

### 🚀 Summary

This spec makes your detection pipeline **self‑aware and self‑tuning**. Contributors are modular, expense is
characterized via Prometheus, and policy profiles let you dial detection depth up or down automatically. It’s the bridge
from a static ruleset to a **living, evolutionary detection ecosystem**.

---

Would you like me to also sketch a **visual flow diagram** (Lean → Balanced → Paranoid switching paths) so you’ve got a
clear picture of how the PerformanceContributor orchestrates profiles in real time?
