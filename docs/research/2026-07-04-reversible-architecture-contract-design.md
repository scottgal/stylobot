# Reversible Architecture Contract

**Date:** 2026-07-04
**Status:** Design spec (research complete). Supersedes the *authoring model* of `2026-07-04-structural-architecture-tests-spec.md`; reuses its enforcement tech.
**Repos in play:** FOSS `stylobot` (atoms under test), `lucidview/Naiad` (Mermaid parser, reused), `stylobot-commercial` (this spec).

---

## 1. Motivation

The prior spec (`2026-07-04-structural-architecture-tests-spec.md`) proposed hand-coded structural tests as the compliance layer for LLM-authored code. Research on that spec surfaced a sharper insight: today the *soft* guidance an LLM reads (CLAUDE.md, cursor-rules, memory files) and the *hard* deterministic gate (ArchUnit/Roslyn tests) are **two separate artifacts that drift apart**. The agent is told the boundaries in one file and checked against them in another, and nothing keeps the two in sync.

This design collapses them into **one reversible markdown document** that is simultaneously:

1. The coding agent's **operating envelope** — the thing it searches locally to know "you are safe to operate within these boundaries."
2. The **source of truth** the deterministic tests are interpreted from.

Same document guides and enforces. When the boundaries change, they change in one place, and both the agent's context and the gate move together. Drift between guidance and enforcement becomes structurally impossible, not merely discouraged.

The research verdict on novelty: both halves are individually mature (ArchUnit-style gates; architecture-as-context for agents), but **no shipped tool unifies them into a single machine-readable contract that is both agent context and deterministic gate**. The closest named work is ThoughtWorks Radar Vol. 34's *"Architecture drift reduction with LLMs"* (Assess, Apr 2026), which is reactive/post-hoc remediation, not a single generate-time-plus-gate artifact. This design occupies that gap.

## 2. Core concept

A **contract document** is a single markdown file containing three co-located layers:

- **Prose** — the human- and agent-readable narrative of the boundaries.
- **Layer A — a Mermaid diagram** (`C4Container`/`C4Component` or a flowchart) expressing components, boundaries/zones, and the *allowed dependency edges* between them.
- **Layer B — a fenced machine-readable rule block** (YAML) expressing the fine-grained *code-shape statements* that a dependency graph physically cannot represent.

A generic enforcement harness parses Layers A and B and asserts the compiled code conforms. The same file is referenced from CLAUDE.md / is locally grep-able, so the agent reads the identical boundaries it will be judged against.

```
┌─ contract.md ───────────────────────────────────────────┐
│  # Bot-detection atom boundaries   (prose: agent reads)  │
│                                                          │
│  ```mermaid  ← Layer A: allowed dependency graph         │
│  C4Component ... Rel(atoms, signalsink, "raises") ...    │
│  ```                                                     │
│                                                          │
│  ```arch-rules  ← Layer B: code-shape statements (YAML)  │
│  - id: no-hardcoded-catalog ...                          │
│  ```                                                     │
└──────────────────────────────────────────────────────────┘
        │ parse                          │ read as context
        ▼                                ▼
   Enforcement harness (xUnit)      Coding LLM (CLAUDE.md ref)
   → deterministic gate             → operating envelope
```

## 3. Why this is buildable now (prior art + local assets)

- **The Mermaid parse half is already solved locally.** `lucidview/Naiad` (namespace `MermaidSharp`) ships `C4Parser : IDiagramParser<C4Model>` and `FlowchartParser : IDiagramParser<FlowchartModel>`. `C4Model` decomposes a Mermaid C4 diagram into `Elements` (`C4Element{ Id, Label, Description, Technology, Type, IsExternal, BoundaryId, Link }`), `Relationships` (`C4Relationship{ From, To, Label, Technology }`), and `Boundaries` (`C4Boundary{ Id, Label, Type, ElementIds }`). That is exactly the graph the contract needs: elements = components, relationships = the allowlist of permitted dependency edges, boundaries = zones/layers. **No PlantUML, no Structurizr, no hand-rolled parser** — we self-consume naiad's model. (No-duplication rule: reuse naiad.)
- **The diagram-as-gate pattern is proven.** ArchUnit/ArchUnitNET's `Types().Should().AdhereToPlantUmlDiagram(diagram, consideringAllDependencies())` already derives dependency rules from a component diagram in strict mode (any undrawn dependency is a violation). We replicate that behaviour against naiad's `C4Model` instead of PlantUML.
- **The rules-as-data pattern is proven.** dependency-cruiser (JS) and Spectral (Stoplight, generic JSON/YAML ruleset engine, named by ThoughtWorks as a deterministic architecture guardian) both keep allowed/forbidden rules in a data file that a generic engine interprets. Layer B is the same idea, scoped to .NET code-shape predicates.
- **The enforcement tech is settled by the prior research.** Tier-1 type-shape and dependency rules: ArchUnitNET (`TngTech.ArchUnitNET` 0.13.3, actively maintained; NetArchTest.Rules is abandoned). Tier-2 source-shape rules: Roslyn `Microsoft.CodeAnalysis.CSharp` (glob + `CSharpSyntaxTree.ParseText` for syntactic rules; Buildalyzer semantic model only where a rule needs type info). DI-registration rules: plain reflection over the built `IServiceCollection` (Cecil-based DSLs can't see it). Snapshot rules (deferred): Verify.

## 4. Contract format

### Layer A — the dependency/boundary graph (Mermaid, parsed by naiad)

A Mermaid `C4Component` (or flowchart) block. Each `C4Element` maps to a code region; each `C4Relationship` is a permitted dependency edge; each `C4Boundary` is a zone/layer.

**The load-bearing convention: element → code mapping.** Two authoring options, harness supports both:
- **In-diagram (default, keeps the mapping visible on the element):** the `C4Element.Technology` field carries the namespace/assembly glob, e.g. `Container(atoms, "Detector Atoms", "Mostlylucid.BotDetection.Orchestration.Atoms.*")`. Pragmatic but slightly overloads C4's "technology" slot.
- **Layer-B override table (cleaner separation, keeps Layer A a pure renderable diagram):** an `element-map:` block in the YAML maps element `Id` → namespace glob. Preferred when the diagram is also used for human/customer-facing rendering where a namespace in the tech slot would look wrong.

`Link` is available as a secondary pointer (e.g. to source). Elements with no mapping are documentation-only and not enforced (logged, never silently ignored). A meta-rule fails the build if a mapped element resolves to zero types.

**Semantics (strict by default):** the drawn relationships are the *complete allowlist* of cross-element dependencies. A code dependency that crosses two mapped elements and is **not** a drawn `C4Relationship` is a violation. This is the `consideringAllDependencies()` behaviour; the permissive `consideringOnlyDependenciesInDiagram()` mode is available per-contract but off by default, because a silent free pass on undrawn edges defeats the guardrail.

### Layer B — code-shape statements (fenced YAML, `arch-rules`)

For the invariants a graph cannot express. Each rule is data; a generic xUnit theory interprets it. Rule kinds (each maps to an enforcement backend):

| Rule kind | Backend | Example (from the atom incidents) |
|---|---|---|
| `must-extend` | ArchUnitNET | every `IDetectorAtom` extends `DetectorAtomBase` |
| `name-matches` | ArchUnitNET | `Name` == type name minus `Atom` suffix |
| `no-static-mutable-field` | ArchUnitNET | atoms hold instance state, not `static` mutable fields |
| `must-be-registered` | reflection over `IServiceCollection` | every `INativeAtomNameMarker` has a matching atom; no duplicate `Name` |
| `banned-call-in-path` | Roslyn syntactic | no `state.WriteSignal(...)` under `Orchestration/Atoms/` |
| `no-collection-literal-over` | Roslyn syntactic | no collection initializer > N entries (the hardcoded-catalog rule) |
| `must-emit-signal` | Roslyn syntactic | every `DetectAsync` raises `<name>.ran` early |
| `no-nonprimitive-interpolation` | Roslyn semantic (Buildalyzer) | no `sink.Raise($"...{obj}")` where `{obj}` is non-primitive (PII/state leak) |

Sketch:

```yaml
# ```arch-rules
- id: no-hardcoded-catalog
  kind: no-collection-literal-over
  scope: Mostlylucid.BotDetection.Orchestration.Atoms
  threshold: 5
  message: "Curated catalog belongs in a YAML manifest (feedback_no_word_lists)."
- id: atoms-extend-base
  kind: must-extend
  selector: "*Atom in Orchestration.Atoms implementing IDetectorAtom"
  base: DetectorAtomBase
```

## 5. Enforcement engine (the reversible gate)

A single generic xUnit project (one, not per-rule):

1. **Load** every `*.contract.md` under a configured root.
2. **Layer A:** extract the ```mermaid block, parse via naiad `C4Parser` → `C4Model`. Build the element→namespace map from `Technology`. Load the target assembly's dependency graph (ArchUnitNET `Architecture`, Cecil-backed). Assert every cross-element edge in the code is present as a `C4Relationship`; fail (strict) otherwise, naming the offending type + the undrawn edge.
3. **Layer B:** parse the ```arch-rules block; dispatch each rule to its backend (ArchUnitNET predicate / reflection / Roslyn walker). Each failure reports rule `id`, offending type, and file:line.
4. **Reverse direction (the "reversible" property):** the harness can emit the *observed* element graph (what the code actually depends on) as a Mermaid block, so a drift is diffable both ways — the doc claims edges the code lacks, or the code has edges the doc lacks. The doc stays the source of truth; the emitter is a diagnostic, never an auto-writer (no silent doc mutation).

"Reversible" therefore means three things at once: (a) the same artifact reads forwards as agent guidance and executes as a gate; (b) a violation is detectable from either side (doc→code or code→doc) by the same test; (c) the observed graph can be projected back out of the code for comparison.

## 6. The LLM-context half

The contract file is referenced from the repo's CLAUDE.md and lives at a stable, grep-able path. Because Layer A is Mermaid (renders on GitHub, and LLMs read Mermaid fluently) and Layer B is terse YAML, the agent ingests the exact boundaries it will be judged against. There is no second "guidance" document to drift.

**Build-time vs test-time (the LLM threat-model point, carried from the prior research):** the prototype enforces at xUnit/CI time. The distinctive follow-on is a **build-time Roslyn analyzer** driven by the same Layer B block, surfacing violations as compiler errors inside the agent's edit loop, where it self-corrects immediately (cf. AngularArchitects' "Stop hook" deterministic-feedback pattern; Codesai's "a rule violation physically prevents the build from passing"). Human authorship tolerates CI-time gates because drift crosses commits; LLM authorship drifts *within a single edit*, so the highest-value enforcement is as early as the compiler. The contract format is enforcement-backend-agnostic precisely so the same rules can later drive an analyzer without re-authoring.

## 7. What the graph cannot express (why Layer B is mandatory)

Confirmed by research. A C4/dependency graph can deterministically enforce: allowed component relationships, dependency direction / forbidden reverse edges, layer/zone boundaries, no-skip-layer, and (strict mode) "no dependency that isn't drawn." It **cannot** express node-local predicates: "no hardcoded catalog," "emit a `.ran` signal," "no PII in string interpolation," "no static mutable field," "sealed implementations," "register both the options and `IOptions<T>`." Those are code-shape rules, not graph edges, so they live in Layer B. The diagram is necessary but not sufficient; the two layers are complementary, not redundant.

## 8. Relationship to the prior structural-tests spec

- The prior spec's **four tiers become rule *kinds*** the contract expresses (Tier 1 → `must-extend`/`name-matches`/`must-be-registered`; Tier 2 → the Roslyn `banned-call`/`collection-literal`/`must-emit`; Tier 3 snapshot → a future `snapshot` kind via Verify; Tier 4 meta → `must-be-registered` pack-wide + graph strict mode).
- The prior spec's enforcement tooling recommendations **carry over unchanged**; this design only changes *where the rules are authored* (a data contract vs hardcoded test methods) and *who reads them* (agent + gate vs gate only).
- **The existing `DetectorAtomWireupTests.Integration.cs` already implements several rules by hand** (no-duplicate-`Name`, wave partitioning, marker↔atom, skip-set match). Those become the first `must-be-registered`/meta entries in Layer B; the harness generalizes that file's pattern rather than replacing it.
- The reference incident catalogs are real and present today (`IpAtom.DatacenterPrefixes`, `Http2FingerprintAtom.KnownFingerprints`, `TcpIpFingerprintAtom.WindowSizePatterns`, `TlsFingerprintAtom.KnownBotFingerprints`, all `static readonly` with live `TODO: migrate to YAML per feedback_no_word_lists`). Note: because they are `static readonly`, the `no-static-mutable-field` rule does **not** catch them; `no-collection-literal-over` is the rule that does. Keep the two distinct.

## 9. Design decisions (made, with rationale)

1. **Mermaid over PlantUML/Structurizr.** naiad already parses Mermaid C4 + flowchart into a typed model locally; Mermaid renders on GitHub and is LLM-native. This overrides the research's default PlantUML lean, which existed only because ArchUnitNET ships a PlantUML bridge — moot when we consume naiad's `C4Model` directly.
2. **One document, two machine-readable layers.** A single source that is both agent context and gate is the entire point; splitting graph and code-shape rules across files would reintroduce the drift this design exists to kill.
3. **Strict allowlist by default** (`consideringAllDependencies`). A guardrail that silently passes undrawn edges is not a guardrail.
4. **Enforcement-backend-agnostic rule kinds.** Layer B rules name a *kind*, not an implementation, so the same contract can drive xUnit today and a build-time analyzer later.
5. **Emitter is read-only.** The reverse projection is a diagnostic; the doc is never auto-mutated (no silent contract rewriting, consistent with "never let the tool quietly weaken the contract").

## 10. Risks and open questions

- **Element→namespace mapping is load-bearing.** If `Technology` globs are wrong or missing, enforcement is hollow. Mitigation: a meta-rule that fails if a mapped element resolves to zero types, and a report of unmapped namespaces.
- **Mermaid C4 is officially "experimental" upstream** — but we own naiad and pin its parser, so the grammar is stable *for us*. Flowchart is a stable fallback dialect if C4 proves awkward.
- **Semantic-model rules (non-primitive interpolation) need a design-time build** (Buildalyzer + matching SDK on the runner), slower and more fragile than syntactic rules. Keep them in a separate opt-in test class off the hot path.
- **Contract granularity.** One contract per pack, or one per bounded context? Start with one contract for the bot-detection atom pack; generalize only if a second consumer appears (YAGNI).
- **Cross-repo authorship.** The atoms are in FOSS `stylobot`; naiad is in `lucidview`; this spec is in `stylobot-commercial`. The enforcement project references the FOSS assembly + the naiad parser. Packaging/reference topology is an implementation-plan concern.

## 11. Scope and non-goals

- **This is a spec. No implementation** (per operator directive). No test project, no branch, no edits to atoms or naiad.
- **Do not modify existing atoms** (the migration is active on `realign-pack-signalsink-blackboard`).
- The prototype scope (which rules land first, which repo/branch, CI wiring) is deferred to a later implementation plan, not decided here.
- Tier 3 (snapshot) and the build-time analyzer are named as follow-ons, not in the first cut.

## 12. Prior-art citations

- Ford, Parsons, Kua, *Building Evolutionary Architectures* (2nd ed. 2023) — fitness-function taxonomy (atomic/holistic, triggered/continual/temporal, static/dynamic, automated/manual, intentional/emergent).
- ArchUnit / ArchUnitNET `AdhereToPlantUmlDiagram` — the diagram-as-gate precedent. <https://archunitnet.readthedocs.io/en/latest/guide/>
- dependency-cruiser rules-as-config — <https://github.com/sverweij/dependency-cruiser/blob/main/doc/rules-reference.md>
- Spectral (Stoplight) generic ruleset engine — <https://github.com/stoplightio/spectral>
- ThoughtWorks Radar Vol. 34, *Architecture drift reduction with LLMs* (Assess, Apr 2026) — <https://www.thoughtworks.com/radar/techniques/architecture-drift-reduction-with-llms>
- Codesai, *Our Architectural Guardrails for AI-Generated Code* (Apr 2026) — deterministic gates give "100% adherence."
- naiad Mermaid parser — `lucidview/Naiad/src/Naiad/Diagrams/C4/C4Model.cs` (`MermaidSharp.Diagrams.C4`).

## 13. Next step

Per "write specs, don't implement," this stops at the spec. The natural follow-on is an implementation plan (via the writing-plans skill) covering: contract file location + first ruleset, the generic harness project, the naiad reference, and the first prototype rules (dependency strict-mode + `no-collection-literal-over`, which fires against the real catalogs today). Gated on operator approval of this spec.