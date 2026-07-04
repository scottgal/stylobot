# Structural Architecture Tests for LLM-Authored Code

**Date:** 2026-07-04
**Status:** Spec for handoff to a fresh agent
**Priority:** Blocks nothing; runs parallel to the active atom-conversion migration on the `realign-pack-signalsink-blackboard` branch of the FOSS repo (`/Users/scottgalloway/RiderProjects/stylobot`)

---

## 1. Motivation

The bot-detection pack migration is being driven by an LLM. Behavioural tests (287+ passing) verify the code works. They do **not** verify it obeys the architectural invariants the operator has committed to over months.

The insight this spec ships: **behavioural tests are the wrong gate for LLM-authored code**. Humans self-enforce invariants in the edit loop and via PR review. LLMs do not; the same model that introduces a shape violation will confidently defend it. Deterministic structural gates are the compliance layer for probabilistic authoring.

The concept is not new. Ford & Parsons, *Building Evolutionary Architectures* (2017), call these **architecture fitness functions**. Java has ArchUnit; .NET has ArchUnitNET and NetArchTest.Rules. The gap: none of the mainstream write-ups frame architecture tests as **the compliance layer for LLM code**. The threat model is different (drift within a commit, not across months), which changes both WHERE the tests live (build-time compiler errors, not CI-time xUnit failures) and HOW strict they are (blocking, not advisory).

## 2. Reference incidents this must catch

From the atom-migration arc, structural violations that would slip a behavioural gate:

1. Hardcoded curated string catalogs inside atoms (`DatacenterPrefixes` in `IpAtom`, `KnownFingerprints` in `Http2FingerprintAtom`, `WindowSizePatterns` in `TcpIpFingerprintAtom`, `KnownBotFingerprints` in `TlsFingerprintAtom`). All were carried over from contributors with `TODO: migrate to YAML per feedback_no_word_lists` -- LLM never self-corrected.
2. PII leaking to sink via `sink.Raise($"user.email:{email}")` or the raw `SignalKeys.UserAgent` write.
3. Rich object stringified into signal payload -- `sink.Raise($"payload:{obj}")` where the ToString is structural.
4. Missing `<name>.ran` ledger entry (breaks ran-vs-value pattern).
5. `state.WriteSignal` or `BlackboardState` reads inside `Orchestration/Atoms/` (legacy contract escape hatch).
6. `AddSingleton<IDetectorAtom, T>` bypassing `AddDetectorAtom<T>()` -- skips the `INativeAtomNameMarker`, causes the migration adapter to double-fire.
7. `RequiredSignals` encoding an OR-arm (contract is intersection-only; OR must live inline in `DetectAsync`).
8. New `ContributingDetectorAdapter`-shaped bridge classes reappearing after that adapter's scope-to-migration mandate.
9. Static mutable state (`private static ConcurrentDictionary<..>`) inside atom classes. Atoms are singletons; use instance state so they're testable in isolation.
10. Multiple atoms writing the same signal key with different semantics (schema collision).

Load-bearing memory files that codify the underlying rules:

- `~/.claude/projects/-Users-scottgalloway-RiderProjects-stylobot-commercial/memory/reference_stylobot_intended_architecture.md`
- `~/.claude/projects/.../memory/feedback_signals_atoms_pattern.md`
- `~/.claude/projects/.../memory/feedback_no_word_lists.md`
- `~/.claude/projects/.../memory/feedback_structural_tests_for_llm_code.md` (contains the four-tier plan)

## 3. Goals

Deliver a research report AND a working prototype:

1. **Report** in `docs/architecture/structural-tests.md` covering:
   - Recommended tool for each of the four tiers below.
   - Adopt-vs-build decision per tier with rationale.
   - Concrete migration path from behavioural-only test suite to layered structural + behavioural.
   - LLM-specific angle (build-time vs test-time enforcement; blocking vs advisory) explicitly addressed.

2. **Prototype** landing in `src/Mostlylucid.BotDetection.ArchTests/` (new project) covering **Tier 1 completely** and **one concrete Tier 2 rule** end-to-end:
   - Every atom in `Mostlylucid.BotDetection/Orchestration/Atoms/` extends `DetectorAtomBase`.
   - Every atom has a matching `INativeAtomNameMarker` registered.
   - No atom class contains a `HashSet<string>` / `Dictionary<string, ..>` initializer literal larger than 5 entries (catches the hardcoded catalogs from incident #1).
   - Each rule fails as expected against a demonstrably-violating fixture (write a fixture that fails, then a real atom that passes).

3. **Wire-up** to CI: prototype passes as part of the standard `dotnet test` invocation for the FOSS repo. Failing structural tests block CI the same way failing behavioural tests do.

## 4. Non-goals

- **Do not modify existing atoms.** The migration is active. Refactoring atoms to fit a new test surface will merge-conflict with the ongoing conversion work. Only add tests; leave source alone. If a real atom fails a structural rule (e.g. the hardcoded-catalog rule), report it -- do not fix.
- **Do not implement Tier 3 (snapshot contract tests) or Tier 4 (meta-property tests).** Those are follow-on work. Prototype Tier 1 + one Tier 2 rule; report on Tier 3/4 approach without landing them.
- **Do not add build-time analyzer packages yet.** The prototype uses xUnit-time enforcement. The report should recommend build-time analyzers for follow-on but the initial prototype is xUnit-only to keep the scope tight.
- **Do not touch the `realign-pack-signalsink-blackboard` branch.** Cut a fresh branch from `main` in the FOSS repo, name it `arch-tests-prototype`. The migration branch will merge into main first; this branch rebases on top.

## 5. Four-tier taxonomy (repeat, for the fresh agent's context)

**Tier 1 -- Reflection convention tests.** xUnit + reflection. Cheapest. Assert type/naming/dependency invariants:

- Every `IDetectorAtom` in the atoms namespace extends `DetectorAtomBase`.
- Every atom has a matching `INativeAtomNameMarker` registered in DI.
- `Name` matches the type name minus the `Atom` suffix.
- No static mutable fields on atom types.
- No two enabled atoms claim the same `Name`.

**Tier 2 -- Roslyn / source-syntax convention tests.** Walk the syntax tree, ban patterns reflection cannot see:

- No `state.WriteSignal` call inside files under `Orchestration/Atoms/`.
- No `BlackboardState.` type reference inside atom source (the adapter is exempt).
- Every `DetectAsync` method emits a `<name>.ran` signal via `sink.Raise("<name>.ran", sessionId)` early in its body.
- No `sink.Raise($"...{obj}")` where the interpolation target is a non-primitive type.
- No hardcoded curated string list (heuristic: HashSet<string> / Dictionary<string, ..> collection initializer with more than a configurable threshold of literal entries).

**Tier 3 -- Runtime contract snapshot tests.** Verify.NET or Snapshooter. Boot the pack with a synthetic HttpContext, run each atom, snapshot the emitted signal SCHEMA (names only, no values). Golden fixture per atom; drift forces a deliberate `--update-snapshot`.

**Tier 4 -- Meta-property tests.** Pack-wide invariants:

- No two atoms produce the same signal key (or, if they do, both names appear in a documented allow-list file).
- Priority values partition into expected wave ranges without gaps that would stall a wave.
- The adapter's skip set (`INativeAtomNameMarker` markers) matches exactly the set of atoms that override a corresponding legacy contributor.

## 6. Concrete tooling shortlist to evaluate

Evaluate in the report, adopt for Tiers 1-2 in the prototype:

- **NetArchTest.Rules** ([BenMorris/NetArchTest](https://github.com/BenMorris/NetArchTest)) -- .NET-native reflection, fluent DSL, no Cecil dep. Likely primary choice for Tier 1.
- **ArchUnitNET** ([TNG/ArchUnitNET](https://github.com/TNG/ArchUnitNET)) -- .NET port of Java ArchUnit. Cecil-backed. Compare against NetArchTest for Tier 1.
- **Roslyn `CSharpSyntaxWalker`** in an xUnit test -- for Tier 2 source-pattern rules that reflection cannot see.
- **Verify.NET** ([VerifyTests/Verify](https://github.com/VerifyTests/Verify)) -- for Tier 3 snapshots, deferred.
- **Semgrep** -- pattern-based static analysis, cross-language, evaluate as a supplement not a primary tool.

## 7. Prior-art reading list (embed short summaries in the report)

- Ford & Parsons, *Building Evolutionary Architectures* (2017; 2nd ed. 2023). The canonical text on fitness functions. Skim; the taxonomy is what matters (atomic/holistic, triggered/continual, static/dynamic).
- ThoughtWorks Technology Radar entries on architecture fitness functions and ArchUnit (multiple editions since 2018).
- Michael Feathers' writing on "characterisation tests" -- adjacent, not identical.
- Neal Ford's "Architecture as Code" talks (short-form).

Also worth searching: any 2025-2026 blog posts explicitly framing architecture tests as an LLM-code-review compliance layer. Best guess: sparse, which is what makes the operator's framing worth documenting.

## 8. Deliverable manifest

Working tree at end of the fresh agent's run:

```
docs/architecture/structural-tests.md                        # research report (NEW)
src/Mostlylucid.BotDetection.ArchTests/                      # new test project
    Mostlylucid.BotDetection.ArchTests.csproj
    Tier1_ReflectionConventionTests.cs
    Tier2_HardcodedCatalogSourceTest.cs                      # one Tier 2 rule
    Fixtures/
        BadAtom_WithLargeHardcodedList.cs                    # deliberately violating
        BadAtom_WithoutMarker.cs                             # deliberately violating
Stylobot.slnx                                                # updated to include new project
.github/workflows/ci.yml                                     # updated so structural tests run alongside behavioural (if CI config exists)
```

Do NOT commit uncommitted changes on the migration branch's working tree. Verify branch state before starting.

## 9. Success criteria

- Report is a single markdown file, under 1500 words, with a Recommendation section at the top and Rationale below.
- Prototype test project builds clean, adds zero warnings, and runs green.
- Prototype's Tier 1 tests catch the deliberate `BadAtom_WithoutMarker.cs` fixture (fixture is expected to fail structural checks; a separate `IgnoreFact` skips it in the CI run but documents the expected failure).
- Prototype's Tier 2 hardcoded-catalog test either passes (if the atoms have been cleaned up by then) OR fails cleanly and reports the specific atom + line number for each violation (which is the desired behaviour today given TODO comments left in the atom code).

## 10. Handoff to the fresh agent

Prompt shape:

> You are picking up the spec at `docs/superpowers/specs/2026-07-04-structural-architecture-tests-spec.md` in `/Users/scottgalloway/RiderProjects/stylobot-commercial`. Read it end to end. The FOSS repo is at `/Users/scottgalloway/RiderProjects/stylobot`; that is where the new test project lands. Cut a fresh branch `arch-tests-prototype` from `main` in the FOSS repo. Do NOT touch the `realign-pack-signalsink-blackboard` branch. Do NOT modify existing atoms. Land the deliverables in Section 8; verify against Section 9. Ask before writing to disk if any Section 3 goal is unclear.

Constraints for the fresh agent:

- Read the memory files listed in Section 2 before writing anything.
- Do NOT skip the report; the tooling recommendation is as important as the code.
- Do NOT scope-creep into Tier 3 or Tier 4.
- Report back with the git branch + PR-ready branch, not merged.

## 11. Related open work

- Task #27 (from the active session's task list): "Contract lock-in tests for converted atoms." That is the Tier 3 work referenced in this spec. Do not close #27; this spec's prototype is upstream of it.
- The atom migration on `realign-pack-signalsink-blackboard` continues in parallel. When it merges, the atoms this spec's tests target will change. The Tier 1 tests should survive that merge unchanged; the Tier 2 hardcoded-catalog test will likely need to accommodate whatever cleanup the migration does or does not perform.

---

**Author's note (to the fresh agent):** the operator's framing that structural tests are the compliance layer for LLM-authored code is worth writing up as a first-class point in the report, not a footnote. Prior art on architecture tests assumes human authorship; the LLM angle inverts the threat model and changes where tests should live. Say so in the report.
