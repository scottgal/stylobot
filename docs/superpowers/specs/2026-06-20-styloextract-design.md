# StyloExtract — design spec

**Date:** 2026-06-20
**Status:** Design approved; ready for implementation plan.
**Author:** Scott + Claude (brainstorming session)
**Scope:** v1 of a standalone .NET 10 NuGet library. Eventual integration into StyloBot is out of scope for this spec but the hook is sketched.

---

## 1. The wedge

StyloExtract is not "HTML to Markdown". That part is downstream and deterministic. The wedge is:

> **Recognise that two pages share the same layout template, so an extractor learned from one page can be reused on the next without re-classifying every block.**

Markdown, JSON, and any other output format are byproducts of a successful template match. The library's core is a fast layout fingerprint, a per-host template index, and a centroid-style learned extractor that drifts and refits as observations accrue.

Why this is open: deep-research (2026-06-20) confirmed production extractors (Trafilatura, Readability, DOM Distiller, Mercury/Postlight Parser) do not exploit same-template clustering. Trafilatura beats neural models on SIGIR 2023 with rule-based heuristics. Mercury achieves per-domain extraction through hand-coded CSS selectors registered manually. None of them learn extractors per template by clustering. This is genuine open product space.

Why this is feasible: MinHash/LSH over DOM structure is a published, production-validated primitive (Ben-Bassat & Rokah ICISSP 2019, shipped in IBM AppScan; Buttler et al. CIKM 2013; Yerlikaya & Bakal IJSI 2022). pq-grams (Augsten et al. ACM TODS 2010) give an O(n log n) tree-edit-distance approximation that is both set-friendly (MinHash input) and vector-friendly (cosine match against centroids). All primitives are straightforward in .NET.

---

## 2. Architecture overview

```
HTML (raw, no browser in v1)
  → AngleSharp DOM parse + clean
  → Structural fingerprint:
       - MinHash signature (128 × 32-bit) over normalised tag-path shingles
       - LSH bands (16 × 8) for indexed lookup
       - Anchor-path MinHash signature (separate, for template-discriminative power)
  → FAST PATH:  band_hash → candidate template_ids → Jaccard estimate
       ├─ HIT (Jaccard ≥ τ_fast):    apply cached LearnedExtractor → render
       └─ MISS: continue to slow path
  → SLOW PATH:
       - pq-gram count vector (p=2, q=3)
       - cosine match against candidate template centroids for this host
       ├─ MATCH (cosine ≥ τ_slow):  merge observation into centroid, update extractor, render
       └─ NOVEL: heuristic block classifier produces block map →
                 induce LearnedExtractor (CSS-selector rules) → register new template → render
```

Per-host scope: every match is gated by an HMAC of the effective site (registrable-domain or eTLD+1). No cross-host bleed. Mirrors StyloBot's `fingerprints.db` per-fingerprint-id pattern.

---

## 3. Pipeline detail

### 3.1 DOM parse + clean

AngleSharp. Strip `<script>`, `<style>`, `<template>`, `<noscript>`, `<svg>` for fingerprint purposes (kept for content extraction). Normalise whitespace, preserve semantic tags and ARIA roles.

### 3.2 Shingle generation

Depth-first walk of cleaned DOM. Each node contributes a shingle of:

```
(tagName, nthOfTypeBucketed, classTokenSetHash, ancestorTagPathHash)
```

- `nthOfTypeBucketed`: 1, 2, 3, "many" — collapses long lists.
- `classTokenSetHash`: xxHash3 of sorted class tokens **after** the noise filter (drop `dark-mode`, `is-*`, `js-*`, hashed BEM suffixes, theme variants, etc.). Noise filter rules live in a YAML resource (`Definitions/ClassNoise/class-noise-tokens.yaml`) — per CLAUDE.md "no word lists in C#" rule.
- `ancestorTagPathHash`: rolling hash of ancestor tag names up to depth 4.

Shingle width is 3 (default) — i.e. tag-path n-grams over the walk. Configurable.

### 3.3 MinHash signature

128 hash functions (xxHash3 with 128 seed offsets). For each shingle, hash with each function; keep min. Result: 128 × 32-bit signature, 512 bytes.

Estimated Jaccard between two pages = fraction of matching MinHash slots.

### 3.4 LSH bands

Split the 128-slot signature into 16 bands of 8 slots each. Hash each band. Each band hash is an indexed lookup key in `template_lsh_band_index`. Bands that collide are *candidate template matches* — Jaccard estimate confirms.

Sub-ms lookup against an indexed SQLite table: 16 prepared-statement lookups. Production stylobot precedent.

### 3.5 Anchor-path signature

Separate MinHash signature (128 × 32-bit) computed over the multiset of:

```
(tagPath, hrefRegistrableDomain, hrefHasHash, classTokenSetHash) per <a>
```

Why separate: the literature consistently identifies the DOM-path-of-URLs as the workhorse template signal (Bagban & Kulkarni IJSI 2022 + corroboration in RoadRunner / ExAlg / FiVaTech / MDR). Keeping it separate lets us weight nav/footer/menu structure independently from body structure.

Match scoring: combined similarity = `w_struct * jaccard_struct + w_anchor * jaccard_anchor`, defaults `w_struct=0.6, w_anchor=0.4`. Both exposed in config.

### 3.6 pq-gram count vector (slow path)

p=2 ancestors, q=3 siblings (Augsten defaults). Each pq-gram is a label tuple over the DOM tree; the count vector is the multiset count over all pq-grams.

Sparse representation: top-K dimensions kept (default K=256). Centroid is the mean count vector across the template's observation cloud. Match = cosine similarity.

**Important: do NOT use pq-gram distance as a metric.** Research refuted the normalised-pq-gram triangle-inequality claim (1-2 adversarial vote). Use the count vector for cosine similarity or as an input to MinHash; don't build metric-tree indices on it.

### 3.7 Heuristic block classifier (slow path only, novel layouts)

Runs only when slow path also misses. Per-block rules:

- `tag in {main, article}` + high text density → `MainContent`
- `tag == nav` OR `role == "navigation"` + link density > 0.7 → `PrimaryNavigation` / `SecondaryNavigation` (by depth)
- `tag == footer` OR class contains "footer" + copyright-pattern text → `Footer`
- `tag == aside` OR class contains "sidebar" → `Sidebar` / `RelatedLinks`
- `tag == form` OR > 2 input descendants → `Form`
- `tag == table` → `Table`
- `tag == pre` + code-pattern text → `CodeBlock`
- Class/id contains "ad", "advertisement", "promo", AND link density > 0.5 → `Advertisement`
- Class/id contains "cookie" + button descendant + viewport-fixed positioning hints → `CookieBanner`
- Default → `Boilerplate` (if not main-text-density) or `Unknown`

Heuristic-first is justified by SIGIR 2023 evidence (Bevendorff et al.): rule-based extractors beat large neural models on the boilerplate-removal benchmark. ONNX is deferred to v2 as an optional refinement.

### 3.8 Extractor induction

For each block the heuristic classifies, produce:

```
BlockRule
  Role: BlockRole
  CssSelectors: IReadOnlyList<string>  // generalised from XPath
  Confidence: double                   // initial from heuristic
  ObservationCount: int = 1
  DriftScore: double = 0
```

CSS selector generalisation: from the node's full XPath, derive a stable CSS selector by dropping `nth-of-type` indices when class tokens are stable, keeping them when class tokens are absent. e.g. `/html/body/div[2]/main/article` becomes `main > article` if `main` is unique.

The full set of `BlockRule`s for a page becomes its initial `LearnedExtractor`.

---

## 4. Public API surface

```csharp
namespace StyloExtract.Abstractions;

public interface ILayoutExtractor
{
    Task<ExtractionResult> ExtractAsync(
        string html,
        Uri? sourceUri = null,
        ExtractionOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed record ExtractionResult
{
    public required Uri? SourceUri { get; init; }
    public required string? Title { get; init; }
    public required LayoutMatch Match { get; init; }
    public required string Markdown { get; init; }
    public required IReadOnlyList<ExtractedBlock> Blocks { get; init; }
    public required ExtractionStats Stats { get; init; }
}

public sealed record LayoutMatch
{
    public required Guid? TemplateId { get; init; }       // null = ephemeral, not registered
    public required int TemplateVersion { get; init; }    // monotonic; bumps on refit
    public required string FingerprintHex { get; init; }  // debug-visible MinHash digest
    public required MatchStatus Status { get; init; }
    public required double Similarity { get; init; }
    public required int ObservationCount { get; init; }   // template's lifetime
    public required TimeSpan LatencyMatch { get; init; }
    public required TimeSpan LatencyTotal { get; init; }
}

public enum MatchStatus
{
    FastPathHit,       // LSH band hit, Jaccard above threshold
    SlowPathMatch,     // pq-gram cosine above threshold
    Novel,             // no match, registered as new template
    NovelEphemeral,    // no match, not registered (per options)
    Refit              // matched but drift forced version bump
}

public sealed record ExtractedBlock
{
    public required string Id { get; init; }
    public required BlockRole Role { get; init; }
    public required double Confidence { get; init; }
    public required string Text { get; init; }
    public required string Markdown { get; init; }
    public required string XPath { get; init; }
    public string? CssSelector { get; init; }
    public required int TextLength { get; init; }
    public required double LinkDensity { get; init; }
    public required IReadOnlyList<ExtractedLink> Links { get; init; }
}

public sealed record ExtractionStats
{
    public required int BlockCount { get; init; }
    public required int FingerprintShingleCount { get; init; }
    public required TimeSpan ParseTime { get; init; }
    public required TimeSpan FingerprintTime { get; init; }
    public required TimeSpan MatchTime { get; init; }
    public required TimeSpan RenderTime { get; init; }
}

public enum BlockRole
{
    Unknown = 0,
    MainContent, Article, Heading, Summary,
    PrimaryNavigation, SecondaryNavigation, Breadcrumb,
    Sidebar, RelatedLinks, Footer, Header,
    Advertisement, CookieBanner,
    Form, Table, CodeBlock,
    Boilerplate
    // Additional roles (Comments, ProductCard, etc.) defined but reserved for v2.
}

public enum ExtractionProfile
{
    MainContentOnly,
    RagFull,
    AgentNavigation,
    DebugFull
}
```

### Internal seams

```csharp
public interface IHtmlDomParser
public interface IDomCleaner
public interface IStructuralFingerprinter   // produces signature + bands + anchor sig
public interface ITemplateIndex             // band lookup, Jaccard scoring, centroid match
public interface IBlockSegmenter
public interface IBlockClassifier           // heuristic in v1
public interface IExtractorInducer          // produces LearnedExtractor from a classified page
public interface IExtractorApplicator       // applies a LearnedExtractor to a parsed DOM
public interface IMarkdownRenderer
public interface ITemplateVersionEventSink  // OnNewTemplate, OnRefit, OnVersionChange
```

All public records use `init`-only setters, required members, value equality.

---

## 5. Persistence — SQLite schema

Per CLAUDE.md "no in-memory persistence" rule. Mirrors StyloBot's `fingerprints.db` pattern. File path configurable; default `${BaseDir}/styloextract-templates.db`.

```sql
CREATE TABLE templates (
  template_id            BLOB PRIMARY KEY,
  host_hash              BLOB NOT NULL,
  version_number         INTEGER NOT NULL DEFAULT 1,
  signature_minhash      BLOB NOT NULL,    -- 512 bytes (128 × 4)
  anchor_signature       BLOB NOT NULL,    -- 512 bytes
  pq_gram_vector         BLOB NOT NULL,    -- sparse top-K, msgpack
  pq_gram_norm           REAL NOT NULL,
  extractor_blob         BLOB NOT NULL,    -- JSON LearnedExtractor (see §7)
  observation_count      INTEGER NOT NULL DEFAULT 1,
  created_at             INTEGER NOT NULL,
  last_seen              INTEGER NOT NULL,
  last_refit_at          INTEGER
);
CREATE INDEX ix_templates_host ON templates(host_hash, last_seen);

CREATE TABLE template_lsh_band_index (
  band_hash   BLOB NOT NULL,
  band_index  INTEGER NOT NULL,
  template_id BLOB NOT NULL,
  PRIMARY KEY (band_hash, band_index, template_id)
);

CREATE TABLE template_version_history (
  template_id          BLOB NOT NULL,
  version_number       INTEGER NOT NULL,
  signature_minhash    BLOB NOT NULL,
  pq_gram_vector       BLOB NOT NULL,
  extractor_blob       BLOB NOT NULL,
  retired_at           INTEGER NOT NULL,
  retirement_reason    TEXT,   -- "drift", "manual", "import"
  PRIMARY KEY (template_id, version_number)
);

CREATE TABLE template_observations (
  template_id          BLOB NOT NULL,
  observed_at          INTEGER NOT NULL,
  signature_minhash    BLOB NOT NULL,
  similarity_at_match  REAL NOT NULL
);
CREATE INDEX ix_obs_template ON template_observations(template_id, observed_at);
```

Observation table is bounded (LRU keep last N=100 per template, configurable) — used for centroid drift detection, not as a long-term log.

Version-history depth configurable (default 3) so a refit can be diffed against its predecessors without unbounded growth.

---

## 6. Aging and match prioritisation

**No TTL on templates.** Old-but-stable templates remain in the index indefinitely.

When multiple candidate templates pass the match threshold (Jaccard or cosine), ties break on a score that combines a permanent observation-durability bonus with a recency-of-activity bonus:

```
priority = similarity
         + λ_obs    * log(1 + total_obs_count)
         + λ_recent * exp(-age_days_since_last_seen / τ)
```

Defaults: `λ_obs = 0.02`, `λ_recent = 0.05`, `τ = 30` days. All configurable. Pure similarity break still dominates the high-similarity regime; the bonuses only matter when candidates are close.

Worked examples (similarity ties):
- Brand-new template, 2 observations, last seen today: bonus ≈ 0.02·ln 3 + 0.05·1 ≈ 0.07
- Freshly active, 50 obs, age 7 days: bonus ≈ 0.02·ln 51 + 0.05·exp(-7/30) ≈ 0.12
- Old-but-heavy, 10 000 obs, age 180 days: bonus ≈ 0.02·ln 10001 + 0.05·exp(-6) ≈ 0.19
- Old-and-light, 3 obs, age 180 days: bonus ≈ 0.02·ln 4 + 0.05·exp(-6) ≈ 0.03

So a 6-month-dormant template with heavy lifetime observation wins, a freshly active template out-ranks one with comparable similarity that has barely been seen, and an old-and-light template ranks last — matching the intent that templates never expire but observation depth still matters.

---

## 7. Learned extractors are centroids, not static rules

```csharp
public sealed record LearnedExtractor
{
    public required Guid TemplateId { get; init; }
    public required int Version { get; init; }
    public required IReadOnlyList<BlockRule> Rules { get; init; }
    public required ExtractorCentroidState Centroid { get; init; }
}

public sealed record BlockRule
{
    public required string RuleId { get; init; }
    public required BlockRole Role { get; init; }
    public required IReadOnlyList<string> CssSelectors { get; init; }
    public required double MeanConfidence { get; init; }
    public required int ObservationCount { get; init; }
    public required double DriftScore { get; init; }      // EWMA over per-obs deltas
}

public sealed record ExtractorCentroidState
{
    public required int TotalObservations { get; init; }
    public required IReadOnlyDictionary<BlockRole, RoleCentroid> ByRole { get; init; }
    public required double OverallDriftScore { get; init; }
    public required DateTimeOffset LastObservation { get; init; }
}

public sealed record RoleCentroid
{
    public required int ObservationCount { get; init; }
    public required double MeanLinkDensity { get; init; }
    public required double MeanTextLength { get; init; }
    public required double MeanDepth { get; init; }
}
```

Each application of an extractor to a page is also an *observation* of the extractor: the page either confirms the rules (the selectors all hit; the resulting role centroids match) or it diverges. Divergence updates `DriftScore`. When `OverallDriftScore` exceeds threshold (default 0.35), the next slow-path pass triggers a **refit**.

A refit:
1. Re-runs heuristic classification on the current page.
2. Combines with the recent observation cloud (last 30 observations).
3. Produces new `BlockRule`s.
4. Bumps `TemplateVersion`, retiring the previous version into `template_version_history`.
5. Emits `ITemplateVersionEventSink.OnVersionChange(templateId, oldVersion, newVersion, diff)`.

This means the same template centroid can later drive different output shapes for different consumers (RAG vs agent navigation vs index) by treating the extractor as a learning centroid rather than a frozen ruleset. The export format (§9) preserves this state.

---

## 8. Version detection as a first-class output

The refit machinery exists for correctness — but it doubles as a free side-effect feature:

> **Running StyloExtract locally over a site's pages over time is a site-template-version monitor.**

Detects:
- CMS updates (theme bumps, plugin changes that move structural anchors)
- Theme rollouts and redesigns
- A/B test promotions (consistent shift in one branch)
- Anti-scraper template churn (deliberate restructuring to break selectors)
- Component library upgrades (Tailwind class regimens, etc.)

API:

```csharp
public interface ITemplateVersionEventSink
{
    ValueTask OnNewTemplateAsync(NewTemplateEvent evt, CancellationToken ct);
    ValueTask OnVersionChangeAsync(VersionChangeEvent evt, CancellationToken ct);
}

public sealed record VersionChangeEvent
{
    public required Guid TemplateId { get; init; }
    public required string HostDisplayName { get; init; }   // not the hash — for human consumers
    public required int OldVersion { get; init; }
    public required int NewVersion { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
    public required TemplateVersionDiff Diff { get; init; }
}

public sealed record TemplateVersionDiff
{
    public required IReadOnlyList<PqGramDimensionChange> TopChangedDimensions { get; init; }
    public required IReadOnlyList<BlockRule> AddedRules { get; init; }
    public required IReadOnlyList<BlockRule> RemovedRules { get; init; }
    public required IReadOnlyList<RuleSelectorChange> ChangedSelectors { get; init; }
    public required double SignatureJaccardDelta { get; init; }
}

public sealed record PqGramDimensionChange
{
    public required string PqGramKey { get; init; }     // human-readable label tuple, e.g. "(div,article,h1,p,p)"
    public required double OldCount { get; init; }
    public required double NewCount { get; init; }
}

public sealed record RuleSelectorChange
{
    public required string RuleId { get; init; }
    public required BlockRole Role { get; init; }
    public required IReadOnlyList<string> OldSelectors { get; init; }
    public required IReadOnlyList<string> NewSelectors { get; init; }
}

public sealed record NewTemplateEvent
{
    public required Guid TemplateId { get; init; }
    public required string HostDisplayName { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
    public required string FingerprintHex { get; init; }
    public required int InitialBlockCount { get; init; }
}
```

Default sink does nothing. Consumers (StyloBot, or a standalone monitoring CLI) register their own.

---

## 9. Export format

JSON. Schema-versioned. Roundtrips cleanly:

```json
{
  "schemaVersion": 1,
  "exportedAt": "2026-06-20T00:00:00Z",
  "host": {
    "displayName": "example.com",
    "hashAlgorithm": "hmac-sha256",
    "hashKey": null
  },
  "templates": [
    {
      "templateId": "...",
      "version": 3,
      "fingerprints": {
        "signatureMinhash": "base64...",
        "anchorSignature": "base64...",
        "pqGramVector": { "p": 2, "q": 3, "topK": 256, "values": [...] }
      },
      "extractor": {
        "rules": [
          {
            "ruleId": "...",
            "role": "MainContent",
            "cssSelectors": ["main > article"],
            "meanConfidence": 0.94,
            "observationCount": 142,
            "driftScore": 0.08
          }
        ],
        "centroid": {
          "totalObservations": 142,
          "byRole": { "MainContent": { ... } },
          "overallDriftScore": 0.11,
          "lastObservation": "2026-06-19T..."
        }
      },
      "observations": { "count": 142, "firstSeen": "...", "lastSeen": "..." },
      "versionHistory": [
        { "version": 1, "retiredAt": "...", "reason": "drift" },
        { "version": 2, "retiredAt": "...", "reason": "drift" }
      ]
    }
  ]
}
```

Import resumes drift cleanly: incoming `LearnedExtractor` carries the centroid state, so subsequent observations continue from the same drift trajectory rather than starting fresh.

This is what unlocks a future centralised "extractor cache" (community catalogue of known-good templates for popular sites) without ever requiring it in v1.

---

## 10. Package topology

```
StyloExtract.Abstractions   interfaces + records, zero runtime deps
StyloExtract.Html           AngleSharp parser + DOM cleaner + tag-path walker
StyloExtract.Fingerprint    MinHash, pq-grams, LSH banding, shingle generation, xxHash3
StyloExtract.Templates      SQLite store + centroid + LearnedExtractor + export/import + version events
StyloExtract.Heuristics     block classifier + extractor inducer + applicator
StyloExtract.Core           orchestration: ILayoutExtractor.ExtractAsync (composes all of the above)
StyloExtract.Markdown       deterministic, profile-aware renderer (downstream of block map)
StyloExtract.AspNetCore     AddStyloExtract() + middleware sugar
StyloExtract.Cli            stylo-extract one-shot CLI (single page, no recursive crawl in v1)
```

Test projects:

```
StyloExtract.Core.Tests
StyloExtract.Fingerprint.Tests
StyloExtract.Heuristics.Tests
StyloExtract.Templates.Tests
StyloExtract.IntegrationTests   golden Markdown / golden block map over a fixture corpus
StyloExtract.Benchmarks         BenchmarkDotNet, mirrors stylobot's pattern
```

Deferred (separate packages, separate specs):

- `StyloExtract.Onnx` — slow-path block-classifier upgrade only; not in v1
- `StyloExtract.Playwright` — rendered-DOM mode
- `StyloExtract.Embeddings` — optional MarkupLM-class slow-path embedding cache
- `StyloExtract.Rag` — RAG chunk helpers

Target framework: `net10.0`. Standalone repo, sibling to `stylobot`. License: TBD by user, default match the stylobot main repo's posture (Unlicense until v7, then AGPLv3-only — per [[project_license_v7]]).

---

## 11. v1 scope — IN / OUT

### In

- Raw HTML to `ExtractionResult` with full `LayoutMatch`
- Fast path: MinHash structural signature + anchor-path signature + LSH bands
- Slow path: pq-gram count vector + cosine match
- Heuristic block classifier (no ONNX)
- Extractor induction + application + drift tracking
- Per-host SQLite template store (LSH band index, observation cloud, version history)
- Centroid-style learned extractors with persistent state
- Aging-based match prioritisation (no TTL)
- Refit on drift threshold breach → version bump → event emission
- Export / import in JSON v1 schema
- 17 `BlockRole` values; 4 profiles (`MainContentOnly`, `RagFull`, `AgentNavigation`, `DebugFull`)
- Deterministic Markdown renderer; JSON block-map output
- ASP.NET Core registration + DI extensions
- CLI: single-page extract; `--export`, `--import`, `--monitor` (watches a list of URLs for version changes — local fetch via `HttpClient` only)
- Benchmarks project

### Out (justified)

- **ONNX block classifier** — SIGIR 2023 evidence: heuristic beats neural for boilerplate work. Defer to v2 as optional slow-path refinement only.
- **Transformer page embeddings** (MarkupLM / WebFormer / DOM-LM) — no published latency, target token-level QA/IE, wrong for hot path. Slow-path enrichment only, not v1.
- **Browser rendering** (Playwright) — separate package, v2.
- **LLM scoring / reranking** — separate package.
- **RAG chunk helpers** — separate package.
- **Multi-page crawl in CLI** — single page only in v1.
- **Cross-host template clustering** — per-host only in v1. Cross-host pattern detection (e.g. "find WordPress instances") is a separate problem at a separate cost.
- **Hierarchical LSH** — research flagged it as design pattern, not closed-form theorem. Not relied on.
- **pq-gram as a metric for tree indexing** — research refuted triangle-inequality property. Used as count vector only.
- **Tree-edit-distance as ground truth** — pq-gram approximation only.
- **Centralised public extractor catalogue / community-shared templates** — export format supports it; no distribution channel in v1.

---

## 12. Configuration & DI

```csharp
builder.Services.AddStyloExtract(options =>
{
    options.DefaultProfile = ExtractionProfile.RagFull;

    options.StorePath = "${BaseDir}/styloextract-templates.db";
    options.HostHashKey = configuration["StyloExtract:HostHashKey"];   // null => process-random

    options.Fingerprint.MinHashSize = 128;
    options.Fingerprint.LshBands = 16;
    options.Fingerprint.LshRowsPerBand = 8;
    options.Fingerprint.ShingleWidth = 3;
    options.Fingerprint.AnchorWeight = 0.4;

    options.Match.FastPathJaccardThreshold = 0.85;
    options.Match.SlowPathCosineThreshold = 0.75;
    options.Match.AgingLambda = 0.05;
    options.Match.AgingTauDays = 30;

    options.Centroid.DriftRefitThreshold = 0.35;
    options.Centroid.ObservationsBeforeStable = 5;
    options.Centroid.ObservationCloudSize = 100;
    options.Centroid.VersionHistoryDepth = 3;
});
```

`AddStyloExtract` registers `HeuristicBlockClassifier` as `IBlockClassifier`. v2's `AddStyloExtractOnnx(modelPath)` wraps it.

`ITemplateVersionEventSink` is registered as no-op by default. Consumers register their own (e.g. a SignalR broadcaster, a Slack alerter, or a stylobot blackboard publisher).

---

## 13. Performance targets

| Stage | Target | Notes |
|---|---|---|
| **Fast-path match step alone** | <1ms p99 | LSH band lookup + Jaccard estimate; sub-ms target consistent with stylobot's L1 fingerprint match pattern |
| **Full `ExtractAsync` on fast-path HIT** | <15ms p99 | dominated by AngleSharp parse on a 200KB page |
| **Full `ExtractAsync` on slow-path MATCH** | <30ms p99 | adds pq-gram compute + cosine + persistence |
| **Full `ExtractAsync` on NOVEL** | <50ms p99 | adds heuristic classification + extractor induction + DB insert |
| **Memory per template** (signatures + centroid + extractor + 100 obs) | <12KB |  |
| **Allocation per `ExtractAsync` call** | bounded; no LOH per call | matches stylobot's hot-path discipline |

Caveat: <1ms for the match step alone assumes the MinHash signature is already computed (e.g. by an upstream contributor in stylobot's pipeline). From raw HTML, AngleSharp parse dominates and the full call is ~10-15ms.

---

## 14. Testing strategy

- **Unit tests** per package: fingerprint primitives are pure-function and table-testable; renderer is goldenable.
- **Heuristic golden tests**: per-role golden HTML fixtures → expected `BlockRole`. F1-per-role is the canary against heuristic drift.
- **Integration tests**: ~30 real-world pages spanning news, docs, ecommerce, marketing, SPA shells, WordPress, forum, support. Two pages per template (same site, different content) verify same-template match; cross-site pairs verify discriminative power.
- **Drift simulation**: synthetic template variants (class swap, child-order swap, A/B flag) tested against a baseline template for false-positive refit rate.
- **Version-detection regression**: a fixture corpus of "before / after" template-change pairs (manually labelled) verifies that refit fires when it should and stays silent when it shouldn't.
- **Export/import roundtrip**: serialise, deserialise, verify identical match behaviour.

Performance regression gating via the Benchmarks project (BenchmarkDotNet, mirrors stylobot's pattern), CI-mode for PRs.

---

## 15. Stylobot integration (future scope — sketched, not designed)

The intended hook:

A `ScraperViewContributor` lives in stylobot, takes a dependency on `StyloExtract.Abstractions`, runs only post-response when `bot.type == AiScraper` is confirmed. It calls `ILayoutExtractor.ExtractAsync` on the response body and emits:

- `scraper.layout.template_id` — group AI-scraper hits by template
- `scraper.layout.template_version` — same template, different version = stale-knowledge scraper signal
- `scraper.layout.status` — `Novel` on first sight is itself a behavioural anomaly
- `scraper.layout.similarity` — drift indicator
- `scraper.layout.useful_block_count` — what the scraper actually got

This gives the centroid/identity layer ([[project-session-vectors]], CLAUDE.md identity-fingerprint-match) a structural answer to "what did the scraper take away," complementing behavioural fingerprints. It also wires the version-change event sink to the stylobot dashboard, surfacing site-template changes alongside scraper behaviour.

Out of scope for the v1 StyloExtract spec. Captured here for design coherence.

---

## 16. Research caveats inherited

From deep-research 2026-06-20:

1. **pq-gram triangle inequality** was refuted (1-2 adversarial vote). Use as count vector for cosine or as MinHash input; do **not** build metric-tree indices on it.
2. **Hierarchical LSH guarantees** for tree-structured data were refuted (0-3). Treat hierarchical LSH as a design idea; do not rely on closed-form theoretical guarantees.
3. **Commercial extractor internals** (Diffbot, ScrapingBee, Bright Data, AWS Bedrock Data Automation, Cloudflare AI Gateway, Browserless) are opaque from public sources. Our claim of "open product space" is supported by the open-source production tools and the academic literature; we cannot claim it against commercial systems whose internals are unknown.
4. **2024-2026 contrastive-learning layout embedding work** is thin in the verified sources. Genuine evidence gap, not just unfound. Do not assume there is a state-of-the-art neural baseline waiting to be benchmarked against.
5. **MinHash band/shingle hyperparameters** are an empirical question. Defaults from literature (16×8 bands, shingle width 3, MinHash size 128) are reasonable starting points; tuning against an internal labelled corpus is post-v1 work.

---

## 17. Open questions for implementation

- How to surface CLI `--monitor` output: text only, JSON log, webhook? Decide during impl plan.
- Should `HostHashKey` rotation be supported (re-key all existing templates) or is the per-process random default acceptable for v1? Defer to impl plan.
- Should heuristic block-classifier rules live in C# or in YAML resources? CLAUDE.md "no word lists in C#" rule suggests YAML; the boundary between "feature extraction code" and "configuration" needs explicit clarification in the impl plan.
- AngleSharp version pinning — match a recent stable release; confirm AOT-friendliness if we ever want StyloExtract usable from a stylobot sidecar binary.

---

## 18. Out of scope, explicit (so future readers know these were considered and deferred)

- ONNX block classifier (v2, optional)
- Transformer embeddings (v3, optional, slow-path-only)
- Playwright / rendered DOM (v2)
- LLM enrichment / reranking (v2)
- RAG chunk helpers (v2)
- Multi-page crawl (v2 CLI)
- Cross-host template clustering (v3+, different problem)
- Centralised extractor catalogue / community distribution (v3+, infrastructure question)
- Adaptive output via centroid (v3, requires consumer-side feedback loop)
- StyloBot integration — separate spec on the stylobot side
- StyloWall — separate concept, separate spec (see [[project-content-change-detection]])