# Semantic firewall layer for protection in depth

## Overview

A semantics-aware governance layer that sits behind traditional network firewalls and mail gateways, evaluating the
meaning, intent, and risk of content flowing inbound and outbound. It orchestrates contributors (detectors, middleware)
via policies, role graphs, and performance profiles to deliver customizable, explainable protection in depth.

---

## Goals

- **Intent-aware defense:** Inspect semantics (tone, toxicity, PII risk, doxing) rather than only headers and IPs.
- **Customizable policy mesh:** Per-org, per-department, and per-role controls with granular actions.
- **Protection in depth:** Multi-lane pipeline from lightweight rules to deep semantic reasoning.
- **Explainability:** Standardized scorecards with signals, weights, and rationale for every decision.
- **Operational reliability:** Self-tuning via Prometheus metrics; deterministic behavior; auditable changes.

---

## Architecture

### Pipeline lanes get

- **Light lane:**
    - **Purpose:** Low-latency screening.
    - **Components:** Strict rules, header sanity, IP reputation cache, ASN/geo.
    - **Actions:** Log, soft challenge, pass-through with tags.

- **Context lane:**
    - **Purpose:** Moderate cost, richer context.
    - **Components:** Honeypot checks, device/UA drift analysis, accessibility audits, content patterning.
    - **Actions:** Quarantine, redact, route to moderation.

- **Deep lane:**
    - **Purpose:** High-fidelity semantic reasoning.
    - **Components:** Sentiment analysis, toxicity classifiers, doxing detection, PII redaction middleware, optional LLM
      reasoning.
    - **Actions:** Block, rewrite/redact inline, escalate to human review, generate incident.

### Orchestration components

- **PerformanceContributor:**
    - **Role:** Reads Prometheus; selects Lean/Balanced/Paranoid profiles; controls lane activation.
    - **Signals:** `profile:lean|balanced|paranoid`, contributor expense map, reasons.

- **Policy graph engine:**
    - **Role:** Applies intent/risk tags to actions; composes department/role overrides.
    - **Inputs:** Semantic tags, role graph attributes, profile state, trust levels.

- **Role graph integration:**
    - **Source:** HR/IdP (AD/Okta).
    - **Usage:** Department-specific policies (e.g., Legal “never log”), executive high-trust routing, Finance stricter
      outbound.

- **Marketplace and contributor registry:**
    - **Purpose:** Discover, validate, and load audited tools; pin versions; shadow-run before activation.
    - **Trust:** Dual-signed packages (author+platform), transparency log, fitness/expense metadata.

- **Telemetry and scorecards:**
    - **Purpose:** Unified, non-PII decision logging; operational metrics; compliance stamps.
    - **Storage:** Append-only audit channel; Prometheus exporters.

---

## Capabilities

### Inbound screening

- **Phishing/social engineering:**
    - **Signals:** Header anomalies, domain spoof patterns, sentiment manipulative cues.
    - **Actions:** Quarantine, enrich with indicators, alert SecOps.

- **Harassment/toxicity:**
    - **Signals:** Abusive language, threatening tone, sentiment drift spikes.
    - **Actions:** Block at edge, notify recipient safety policy, preserve evidence in secure audit.

- **Doxing attempts:**
    - **Signals:** Structured personal address/phone extraction patterns, collocation of identifiers.
    - **Actions:** Block or redact, create incident.

### Outbound screening

- **PII leakage prevention:**
    - **Signals:** Email, phone, ID numbers, customer record references.
    - **Actions:** Inline redaction, route to compliance review for high-risk payloads.

- **Confidential info governance:**
    - **Signals:** Project codenames, unreleased financials, legal case identifiers.
    - **Actions:** Block or rewrite; require approval chain via role graph.

- **Tone and reputation protection:**
    - **Signals:** Hostile tone, sarcasm-to-threat drift, inappropriate phrasing.
    - **Actions:** Soft deny with suggested rewrite; log policy rationale.

---

## Policies and configurability

### Profiles and thresholds

- **Lean:**
    - **Intent:** Latency-first environments.
    - **Active:** Light lane only; log and tag; medium+ trust contributors.
    - **Triggers:** High CPU/memory/latency.

- **Balanced:**
    - **Intent:** Standard operations.
    - **Active:** Light + context lanes; selective deep checks.
    - **Triggers:** Normal load; moderate risk signals.

- **Paranoid:**
    - **Intent:** Elevated threat or high-stakes departments.
    - **Active:** Full deep lane; strict blocks; medium/high trust tools only.
    - **Triggers:** Attack surge, strong toxicity/doxing/PII risk.

### Role-aware controls

- **Legal:**
    - **Policy:** Never log raw content; only non-PII signatures; redaction mandatory.
    - **Actions:** Block on high-risk; require counsel approvals.

- **Finance:**
    - **Policy:** Aggressive outbound PII screening; strict compliance stamps.
    - **Actions:** Quarantine and approval for flagged items.

- **Corporate comms:**
    - **Policy:** Light touch; tone guardrails; transparent logging.
    - **Actions:** Suggest rewrites; pass with audit.

### Marketplace governance

- **Install rules:**
    - **Policy:** Shadow-run contributors to collect fitness; promote on pass.
    - **Safety:** Kill switch via platform signature revocation; rollback pinned versions.
    - **Economics:** Data-for-tools barter using verified non-PII traffic shapes.

---

## Auditing, safety, and measurement

- **Scorecards:**
    - **Fields:** Signals, weights, decision, rationale, profile, role policy applied, trust level.
    - **Safety:** “Verified non-PII” stamp; redaction counts; contributor provenance IDs.

- **Operational telemetry:**
    - **Metrics:** Runtime, memory, cache hit/miss, external calls, timeouts, error codes.
    - **Drift:** Sentiment trendlines, toxicity spikes, PII detection rates, doxing attempts.

- **Fitness and effectiveness:**
    - **Measures:** Precision/recall vs. labeled signatures, lift vs. baseline packs, per-department success rates.
    - **Adaptation:** Automatic reweighting; profile tuning; contributor promotion/demotion.

---

## C# interfaces and example contracts

### Semantic firewall engine

```csharp
public interface ISemanticFirewall
{
    Task<FirewallDecision> EvaluateAsync(FirewallContext context, CancellationToken ct);
}

public sealed class FirewallContext
{
    public string FlowId { get; init; } = default!;
    public Direction Direction { get; init; } // Inbound/Outbound
    public string Department { get; init; } = "Corporate";
    public string Role { get; init; } = "Employee";
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public ReadOnlyMemory<byte> Payload { get; init; } // Raw content or normalized text
    public IReadOnlyCollection<Signal> PreSignals { get; init; } = Array.Empty<Signal>(); // From upstream detectors
    public Profile Profile { get; init; } = Profile.Balanced;
}

public enum Direction { Inbound, Outbound }
public enum Profile { Lean, Balanced, Paranoid }
```

### Decision and scorecard

```csharp
public sealed class FirewallDecision
{
    public DecisionAction Action { get; init; } // Allow, Block, Quarantine, Rewrite
    public ReadOnlyMemory<byte>? RewrittenPayload { get; init; }
    public Scorecard Scorecard { get; init; } = new();
}

public enum DecisionAction { Allow, Block, Quarantine, Rewrite }

public sealed class Scorecard
{
    public string FlowId { get; init; } = default!;
    public string Profile { get; init; } = "Balanced";
    public string Department { get; init; } = "Corporate";
    public string Role { get; init; } = "Employee";
    public IReadOnlyCollection<Signal> Signals { get; init; } = Array.Empty<Signal>();
    public IReadOnlyCollection<ContributorVerdict> ContributorVerdicts { get; init; } = Array.Empty<ContributorVerdict>();
    public string Rationale { get; init; } = string.Empty;
    public IReadOnlyCollection<string> SafetyStamps { get; init; } = Array.Empty<string>(); // e.g., Verified non-PII
}
```

### Contributor contract (detector/middleware)

```csharp
public interface ISemanticContributor
{
    string Name { get; }
    int Priority { get; }
    ExpenseProfile Expense { get; }
    TrustLevel Trust { get; }

    Task<ContributorVerdict> EvaluateAsync(FirewallContext context, CancellationToken ct);
}

public enum ExpenseProfile { Light, Medium, Heavy }
public enum TrustLevel { Low, Medium, High }

public sealed class ContributorVerdict
{
    public bool Succeeded { get; init; }
    public IReadOnlyCollection<Signal> Signals { get; init; } = Array.Empty<Signal>();
    public string Rationale { get; init; } = string.Empty;
    public ContributorDiagnostics Diagnostics { get; init; } = new();
}
```

### Policy graph engine

```csharp
public interface IPolicyGraph
{
    PolicyOutcome Apply(
        IReadOnlyCollection<Signal> signals,
        Profile profile,
        DepartmentRole deptRole);

    // Returns final action and required middleware contributors
    // based on semantic tags, role overrides, and profile
}

public sealed class PolicyOutcome
{
    public DecisionAction Action { get; init; }
    public IReadOnlyCollection<string> RequiredContributors { get; init; } = Array.Empty<string>();
    public string Rationale { get; init; } = string.Empty;
}

public sealed class DepartmentRole
{
    public string Department { get; init; } = "Corporate";
    public string Role { get; init; } = "Employee";
    public RolePolicyOverrides Overrides { get; init; } = new();
}

public sealed class RolePolicyOverrides
{
    public bool NeverLog { get; init; }
    public bool RequireRedaction { get; init; }
    public TrustLevel MinimumTrust { get; init; } = TrustLevel.Low;
}
```

---

## Deployment and operations

- **Placement:**
    - **Behind static firewalls:** Mail gateways, reverse proxies, API gateways.
    - **Edge router integration:** WASM filters for Envoy/NGINX; sidecar service for L7 content inspection.

- **Scaling:**
    - **Horizontal:** Stateless workers; shared cache for IP/ASN/geo; message bus for quarantine flows.
    - **Profiles:** PerformanceContributor steers activation; Prometheus metrics drive thresholds.

- **Safety modes:**
    - **Shadow:** Run contributors without enforcement to collect fitness.
    - **Audit-only:** Log decisions, no blocks; ideal for Legal or initial rollout.
    - **Strict:** Enforce blocks/rewrites with approval workflows.

- **Governance:**
    - **Signing:** Dual-sign packages; transparency log; version pinning.
    - **Kill switch:** Revoke platform signature to disable compromised tools globally.
    - **Barter economy:** Data-for-tools via verified non-PII traffic shapes; marketplace credits.

---

If you want, I can generate a minimal repo scaffold with the interfaces above, a manifest schema, scorecard format, and
CI pipeline that enforces signing, tests, and performance benchmarks—so you can operationalize this semantic firewall
layer quickly.
