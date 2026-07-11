# Signal contracts and the foundation wave

This document codifies the rules that prevent the class of regression introduced
in commit `afc2f6c` (2026-05-09) and discovered on 2026-05-15. Read it before
touching anything that produces, consumes, or routes signals through the
detection orchestrator.

## The class of bug we are guarding against

`signature.primary` and similar identity facts are written by contributors and
read by ~20 downstream consumers (persistence, dashboard fingerprint table,
deterministic name synthesizer, prior-probability delta, friendly-bot
override). The bug shape is:

1. The fact has more than one writer (signal store, contribution payload,
   `HttpContext.Items`).
2. The orchestrator does not propagate every writer into the read surface
   (`AggregatedEvidence.Signals`).
3. Or the contributor that writes the fact is policy-gated and the production
   policy omits it, while dev tests run a "run-all" demo policy that masks the
   omission.
4. Bot probability still computes correctly because contributions carry their
   own probability deltas. The breakage is invisible in unit tests and only
   manifests in display, persistence, naming, and learning.

Every consumer silently degrades. Nothing throws. The only symptom is "the
dashboard looks wrong".

## Rule 1: foundation versus classifier (v8 atom model)

**[Updated 2026-07-11 for the v8 atom orchestrator; supersedes the
`IFoundationContributor` framing.]** Detection runs through the atom orchestrator:
every `IDetectorAtom` (`DetectorAtomBase`) registers via `AddNativeDetectorAtoms`,
and the orchestrator **sorts by `Priority` and runs them in that order — Wave 0 →
Wave N** (`Orchestration/Atoms/BotDetectionOrchestrator.cs`). "Waves" are a priority
band, **not** an enum and **not** four fixed phases — the taxonomy-role grouping in
the registration (Sensor / Extractor / Guard / Constrainer / Proposer / Ranker) is
for readability; only `Priority` drives run order. The legacy
`IFoundationContributor` / blackboard-contributor model is **retired** —
`IFoundationContributor` no longer exists in code. The foundation-vs-classifier
distinction survives, re-expressed on atoms:

**Foundation atoms (Wave 0)** establish identity context before any classifier
weighs in. They are the lowest-`Priority` atoms — the "Wave 0" band — that declare
**empty `RequiredSignals`**, so the orchestrator runs them **unconditionally, first,
regardless of policy**. Their output is consumed by later atoms and by
display/persistence as ground truth. ~27 atoms today; e.g. `SignatureAtom`,
`TransportProtocolAtom`, `IdentityVectorAtom`, `FingerprintPriorAtom`,
`FingerprintMatchAtom`, `FastPathReputationAtom`, `VerifiedBotAtom`,
`ContentSequenceAtom`.

**Classifier atoms** compute bot-probability deltas on what foundation
established. They run later by `Priority` and many are gated by `RequiredSignals`
(they run only when the signals they need are present) — a tight policy may skip
expensive classification on cheap traffic. E.g. `UserAgentAtom`, `HeaderAtom`,
`BehavioralAtom`, `HeuristicAtom`, `AiScraperAtom`, `LlmAtom`.

**Triggered classifiers** depend on a prior round-trip that may never come, so
they can never be a wave the pipeline waits on. They run when their precondition
holds: `FingerprintApprovalAtom` (out-of-band approval), `ChallengeVerificationAtom`
(a solved JS challenge), `ClientSideAtom` (in-page JS POSTing back — the
client-attested beacon, see Rule 5).

An atom is foundation iff: inputs derive from the current request alone or from
in-memory/SQLite caches keyed on the just-computed identity; **empty
`RequiredSignals`**; latency is microseconds; output is consumed as ground truth;
it does useful work on a cold/fresh request. Otherwise it is a classifier (or
triggered classifier).

**The foundation set is not asserted by a single test** (the old
`DetectorRegistrationCoverageTests` is gone). `AtomEmitContractTests` asserts the
full atom set registers (a count floor) and that no atom raises an undeclared
signal; `DefaultPolicyAndCoverageTests` asserts the coverage detectors are in
`DetectionPolicy.Default`. Adding a Wave-0 atom has no set-assertion to update —
but you still owe the BDF probe in Rule 4.

## Rule 2: one fact, one store

Each fact (`signature.primary`, `transport.protocol_class`, etc.) has one
canonical writer. Other components read from the resulting signal. Do not add
the same fact to a second store (`HttpContext.Items`, a private field on a
service, a sibling dict). If a legacy second store still exists, the migration
is to make it a read-only projection of the canonical store, then delete it.

The legacy `BotDetectionMiddleware` path that writes `signature.primary` to
`HttpContext.Items[PrimarySignatureKey]` is the residual second store. New
code must not depend on it. Existing reads should migrate to
`evidence.Signals`.

## Rule 3: signals merge into the read surface

`AggregatedEvidence.Signals` is the canonical read surface. It must contain
both:

- per-state signals from `BlackboardState.SignalWriter` (what
  `state.WriteSignal(...)` writes), and
- per-contribution signals from `DetectionLedger.MergedSignals` (what
  `contribution.Signals` carries).

`EphemeralDetectionOrchestrator.MergeSignalSources` is the only correct way
to build this dict. If you write a new orchestrator or a new code path that
constructs `AggregatedEvidence`, you must call the same merge or replicate
its semantics. The
`Mostlylucid.BotDetection.Orchestration.Tests.Integration.BdfReplayTests`
rig will catch you if you do not.

## Rule 4: tests assert on the read surface, under the production policy

Unit tests on bot probability are necessary but not sufficient. Every
production-relevant fact must have an integration assertion that the fact
appears in `evidence.Signals` after a real detection run, under
`DetectionPolicy.Default` (not Demo, not a hand-built policy). The BDF rig at
`src/Mostlylucid.BotDetection.Orchestration.Tests/Integration/BdfReplayTests.Integration.cs`
is where this lives. Add a probe to `BdfReplayActual.SignalProbes` and a new
assertion in `AssertSignalsFlowed` whenever you add a foundation signal.

## Rule 5: externally-sourced (client-attested) signals are a distinct low-trust tier

Signals derived from the current request, our caches, or our own detectors are
trusted: we produced them. **Client-attested signals** — values a browser POSTs
back about itself (in-page JS: version-gated feature presence, engine tells,
mode markers) — are different. The client controls the value, so they are
attacker-influenceable *even when signed*: signing proves channel
**provenance/integrity** (the key is on the client), never value
**truthfulness**. They ingress under these rules:

1. **One adaptor, one writer.** A single adaptor validates the beacon and admits
   client-attested signals. It is the sole canonical writer of the
   `clientattested.*` namespace (Rule 2). Nothing else writes those keys.
2. **The beacon payload must be bound to the token.** HMAC over `payload+token`,
   not the token alone — otherwise a valid-token holder (or an off-browser
   `curl` farm replaying a canned payload) can POST fabricated values. The
   adaptor rejects any beacon whose payload signature does not verify.
3. **The whitelist is the security boundary.** The adaptor admits only an
   explicit allow-list of `clientattested.*` keys. A client must **never** be
   able to write a high-trust fact (`reputation.*`, `verifiedbot.*`,
   `signature.*`, priors). Widening the whitelist is a security change, not a
   feature — review it as one.
4. **Merge asymmetrically.** Client-attested signals may only **raise**
   suspicion (via *inconsistency* — deviation of the observed values from the
   learned centroid for the claimed browser/version/mode). They may **never
   lower** bot-probability toward "human" on the strength of self-reported
   "good" values. The detection value is the *inconsistency*, never the raw
   attested value. The best outcome a spoofer can buy by POSTing perfect values
   is *consistency = neutral*; there is no human discount to game.
5. **Triggered classifier, never foundation.** Like `ClientSideAtom`, the adaptor
   + any consumer depend on a prior round-trip that may never come, so they run as
   triggered classifiers (later-wave, gated on the beacon), never Wave 0 the
   pipeline waits on (Rule 1).

The learned-centroid comparison that turns attested values into an inconsistency
verdict (`browser.characteristic_drift`) lives in the identity centroid tier and
is emitted by `InconsistencyAtom` (priority 50); see the browser-consistency design.

## The decision flowchart

Before merging a change to detection code:

```
Touching a detector atom?
  Does it produce a fact something downstream reads as truth,
  from the current request alone, with empty RequiredSignals?
    YES → foundation (Wave 0). Runs unconditionally, first, by Priority.
    NO  → classifier. Later wave; RequiredSignals-gated / policy filters apply.

Adding a new signal key any consumer reads?
  Add a SignalProbe to BdfReplayActual.
  Add an assertion in BdfReplayTests.AssertSignalsFlowed.
  Confirm the assertion runs under DetectionPolicy.Default, not Demo.

Adding a parallel store for a fact already in the signal dict?
  Stop. One fact, one store. Migrate or remove.

Changing how stores merge into AggregatedEvidence.Signals?
  Stop. Enumerate every consumer of evidence.Signals.
  Re-run the BDF rig. Add probes for any consumer not yet covered.
```

## Why the BDF rig is the load-bearing test

The premergedSignals regression survived for six days in production despite
1957 passing unit tests. The unit tests asserted on probability and contribution
counts. Probability still computed. The breakage was in display surfaces fed by
`evidence.Signals`. Only an integration test that fires real requests through
the active orchestrator and asserts on the read surface can catch this shape of
bug. Keep the BDF rig fast, keep it under `DetectionPolicy.Default`, and add a
probe for every signal a consumer reads.