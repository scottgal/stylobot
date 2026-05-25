# Fingerprint match

## Concept

Fast-match a metastable fingerprint. Each visitor is a **shape** in vector space, not a single point. The shape is:

- a centroid (the maturity-weighted long-term baseline of every observation ever absorbed)
- a small cloud of recent unabsorbed observation vectors (the current forms, before they roll into the centroid)
- a per-fingerprint weight vector (which dimensions identify *this* visitor - not all dims matter equally for everyone)
- statistics that describe how the shape evolves (maturity, quality, drift, member count)

A request's per-request vector V matches a shape when V is close to *any part* of it - the centroid (long-term identity) or any of its recent forms (current behaviour) - measured by the shape's own weight vector. Same visitor through IP rotation, UA updates, browser version bumps, mobile-cell-tower changes resolves to the same shape because rotation moves only a few dims and the shape's weighting holds the identity together.

Everything lives in one SQLite database. No external index files. No separate vector store.

## Why

Today `PrimarySignature = HMAC(IP, UA)` is the load-bearing identity key everywhere: persistence, reputation, dashboard, learning, EntityResolution. Both inputs are the most-rotated parts of any client. Every IP change and every browser auto-update produces a brand-new identity, severing reputation, learning history, and visitor continuity.

A metastable fingerprint shape survives those rotations because it composes many factors, retains the cloud of recent forms, weighs dimensions per-identity, and matches by similarity *to the shape*, not by equality of one hash.

## Two-pass match

Core principle: humans must not pay Pass 2 cost. Pass 2 runs only when Pass 1 cannot confidently resolve identity. Bots earn Pass 2 by missing L1 or by L1-confirming poorly. We can afford to be slower for bot decisions, never for humans.

```
Request → composes feature vector V

Pass 1 (point lookup, O(1))
  primary_signature = HMAC(IP, UA)
  fingerprint_keys[primary_signature] → candidate fingerprint_id (or null)

If L1 hit:
  Load candidate row (centroid, weights, inferred_client_type) - single SELECT.
  Compute effective_weight = global_weight ⊙ candidate.weights.
  Quick confirm - weighted_cosine(V, candidate.centroid, effective_weight).
                   No vec0 search; one dot product.
  if score >= MERGE_THRESHOLD:
    confirmed; record observation; emit identity.* signals; DONE.
    → human path: one row read, one cosine compare. Microseconds.
  else:
    L1 was a stale or collided cache entry. Escalate to Pass 2.

If L1 miss OR L1 confirm-failed:
  Pass 2 (vector match, sync, sqlite-vec O(log n))
    Top-K from fingerprints_vec        (centroid index, long-term identity)
    Top-K from observations_vec        (recent unabsorbed observations, current form)
    Union, dedupe by fingerprint_id
    For each candidate fingerprint:
      effective_weight = global_weight ⊙ candidate.weights
      score = max(
        weighted_cosine(V, candidate.centroid, effective_weight),
        weighted_cosine(V, candidate.best_obs, effective_weight)
      )
    Best score is the match.

  Outcome by score:
    score >= MERGE_THRESHOLD               → match; this is the fingerprint.
    LOOSE_THRESHOLD <= score < MERGE       → rotation candidate (see below).
    score < LOOSE_THRESHOLD                → no match; allocate new fingerprint
                                             seeded from nearest archetype.

  If Pass 1 had a candidate and Pass 2 picked a different fingerprint:
    correction:
      - record per-dim differentiator on fingerprint_corrections
      - update Pass 2 fingerprint's weights toward discriminating dims
      - upsert fingerprint_keys[primary_signature] to Pass 2's winner
        (INSERT if L1 missed; UPDATE if L1 had a stale mapping;
         next request with this IP+UA goes straight to the right fingerprint)

Rotation-candidate semantics (LOOSE <= score < MERGE):
  Treat as match for this request - assign the candidate fingerprint_id.
  Record observation on the candidate so its centroid drifts toward the new form
  (this is the only path by which significant rotation is absorbed without
  fragmenting identity).
  Emit identity.rotation_candidate signal so EntityResolution can review the
  band and either confirm-merge or split if the centroid keeps spreading.
  Do NOT allocate a new fingerprint at this band; that's reserved for scores
  below LOOSE_THRESHOLD where there is no plausible existing identity.
```

Detectors run regardless of either pass; fingerprint match is orthogonal to bot-vs-human verdict.

**L1-confirmed is "trust the identity, fast-respond, still observe."** It is never "trust and do nothing." Two consequences:

1. **Cached per-fingerprint verdict served immediately.** Each fingerprint row carries `cached_bot_probability` (EWMA over recent observations' classifier verdicts) and `cached_risk_band`. The L1 confirm path reads these and emits them as `identity.cached_bot_probability` / `identity.cached_risk_band` signals so action policies and the deterministic bot/human verdict use the fingerprint's *learned* prior, not just this request's in-line score. Humans benefit (their cached score is low, response is fast). Bots pretending to be a known-good fingerprint don't (their cached score climbs as drift accumulates).

2. **Background L2 re-verification samples L1-confirmed observations.** A separate component, `FingerprintDriftService`, periodically pulls sampled observations from the L1-confirm queue and runs a full Pass 2 against them. When delayed L2 disagrees with the L1-cached `fingerprint_id`, it records a *delayed correction* (same row shape as a real-time correction) and updates `fingerprint_keys` so the next L1 hit goes to the right fingerprint. The cached_bot_probability EWMA is also updated by this service (and by the absorption service, on the same in-memory recent-classifier-verdict cache).

Together these guard against the pass-as-human-then-drift-to-bot failure mode: a fingerprint that L1 confirms today on the Chrome-desktop centroid can have its centroid drift toward headless-chrome over subsequent observations, surface as `identity.client_type_drift`, AND have its `cached_bot_probability` climb so the *next* L1-confirmed request to the same fingerprint already serves a higher risk band. No request ever pays Pass 2 cost on the hot path; drift is caught and propagated entirely off-band.

### Feedback latency tiers

Feedback must propagate fast or it isn't really feedback. Each loop runs at the latency it can afford and that the data warrants:

| Loop                            | Cadence                | Notes |
|---------------------------------|------------------------|-------|
| `cached_bot_probability` EWMA   | every request, in-line | Each post-detection verdict immediately EWMA-updates the matched fingerprint's row (single in-memory dictionary, write-through to SQLite on a short batch). Next request to the same fingerprint reads the updated score in L1 confirm. Zero-latency feedback for the served verdict. |
| Drift verification (delayed L2) | every few seconds      | `FingerprintDriftService` ticks at `DriftCheckIntervalSeconds` (default 5). Each tick pulls up to `DriftBatchSize` (default 50) sampled observations, runs Pass 2 against each, records delayed corrections, updates `fingerprint_keys`. Drift surfaces within seconds, not hours. |
| Per-fingerprint absorption      | per fingerprint, on maturity threshold | Hot fingerprints fire absorption every few seconds (their threshold of 5 requests fills fast); cold fingerprints absorb when maturity or age threshold trips. Each absorption recomputes inferred_client_type and emits drift signal if it changed. |
| Global weights calibration      | every 30 min (default) | Fisher ratios over the dataset move slowly; running this less frequently is fine. Operator-tunable. |
| Archetype refinement            | same cycle as calibration | Bundled with the calibration tick. |

The hot path serves the cached score from the previous tick of the loop. The previous tick had at most a few seconds of staleness for drift, near-zero for the cached bot probability, and 30 min for global calibration weights - and the in-line classifier pipeline still runs every request, so gross misclassification always gets corrected within the same response, not only on the next one.

Sampling rate for the drift-verification queue is configurable (`DriftSamplingRate`, default 0.05 - 5% of L1-confirmed requests get re-verified by L2 in the background); the slow-path classifier detectors always run regardless of sampling.

Cost profile by traffic shape:
- Stable human (L1 hit, confirm passes): one point lookup, one cosine. ≪ 1 ms.
- Returning visitor with mild drift (L1 hit, confirm passes at lower margin): same as above.
- Rotating bot (L1 miss): vec0 search + re-rank. Bounded by TopK; ms range.
- IP+UA collision against a different fingerprint (L1 hit, confirm fails): full Pass 2 escalation. Caught and corrected; next request from the same IP+UA goes straight through.

## Storage

One SQLite database. Seven tables - `fingerprints`, `fingerprint_keys`, `fingerprint_observations`, `fingerprint_corrections`, `identity_dimension_weights`, `identity_archetypes`, `identity_vector_layout` - plus two vec0 virtual indexes in vec0 mode.

A fingerprint can be the canonical identity for many `primary_signature` values over its lifetime (every IP+UA rotation under the same identity adds another row to `fingerprint_keys` pointing at the same `fingerprint_id`). `member_count` on the fingerprint row equals the number of `fingerprint_keys` rows it owns. `fingerprint_keys` is the L1 cache; `fingerprints` is the canonical identity store.

```sql
CREATE TABLE fingerprints (
    fingerprint_id      TEXT PRIMARY KEY,        -- UUID
    centroid            BLOB NOT NULL,           -- float[D], maturity-weighted mean of all absorbed observations
    centroid_maturity   INTEGER NOT NULL,        -- count of observations folded into centroid
    weights             BLOB NOT NULL,           -- float[D], per-fingerprint weighted-cosine weights
    member_count        INTEGER NOT NULL,        -- distinct primary_signatures that map here
    observation_count   INTEGER NOT NULL,        -- lifetime total observations
    correction_count    INTEGER NOT NULL,        -- times Pass 2 corrected Pass 1 to this fingerprint
    first_seen          TEXT NOT NULL,
    last_seen           TEXT NOT NULL,
    quality             REAL NOT NULL            -- average dimension-presence ratio
);

CREATE TABLE fingerprint_keys (
    primary_signature   TEXT PRIMARY KEY,
    fingerprint_id      TEXT NOT NULL REFERENCES fingerprints,
    first_seen          TEXT NOT NULL,
    last_seen           TEXT NOT NULL,
    hit_count           INTEGER NOT NULL    -- L1 hits on this primary_signature
);
CREATE INDEX ix_fpk_fp ON fingerprint_keys(fingerprint_id);

CREATE TABLE fingerprint_observations (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    fingerprint_id      TEXT NOT NULL REFERENCES fingerprints,
    vector              BLOB NOT NULL,           -- float[D], full detail
    observed_at         TEXT NOT NULL,
    absorbed_at         TEXT                     -- null while detailed; set when folded into centroid
);
CREATE INDEX ix_fpo_active ON fingerprint_observations(fingerprint_id) WHERE absorbed_at IS NULL;

-- Invariant: observations_vec mirrors only the rows where absorbed_at IS NULL.
-- Absorption deletes from observations_vec. Pass 2's "current form" search therefore
-- never returns absorbed observations; older forms live only in the centroid.

CREATE TABLE fingerprint_corrections (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    request_id          TEXT NOT NULL,
    primary_signature   TEXT NOT NULL,
    pass1_fingerprint   TEXT,                    -- null when L1 had no match
    pass2_fingerprint   TEXT NOT NULL REFERENCES fingerprints,
    differentiator      BLOB NOT NULL,           -- float[D] of (V - L1.centroid)² - (V - L2.centroid)² per dim
    observed_at         TEXT NOT NULL
);

-- vec0 indexes (sqlite-vec extension). The index lives in the DB; no external file.
CREATE VIRTUAL TABLE fingerprints_vec USING vec0(centroid float[D]);
CREATE VIRTUAL TABLE observations_vec USING vec0(vector float[D]);
```

### Engine abstraction

The schema above shows the sqlite-vec layout. When the extension can't be loaded (constrained environments, AOT publishes that don't ship the native binary), the schema differs: the two `CREATE VIRTUAL TABLE ... USING vec0(...)` statements are skipped, and the matcher uses a brute-force cosine UDF over `fingerprints.centroid` and `fingerprint_observations.vector` directly.

Both modes implement the same C# interface (`IIdentityAnchorIndex`) with identical query semantics. The C# layer chooses an implementation at startup based on whether vec0 loaded successfully. SQL shape, index objects, and migration scripts diverge between modes; query semantics and result shapes do not. Brute-force is O(n) per query and acceptable up to a few thousand active fingerprints; vec0 is O(log n) effective and required at scale.

### Vector dimensionality and layout versioning

`D` (vector dimension count) is fixed at deployment startup and baked into the binary blob layout. A new `identity_vector_layout` table records the layout version in use:

```sql
CREATE TABLE identity_vector_layout (
    id            INTEGER PRIMARY KEY CHECK (id = 1),
    version       INTEGER NOT NULL,           -- layout schema version
    dimension     INTEGER NOT NULL,           -- D
    layout_json   TEXT NOT NULL,              -- ordered dim names + slot ranges + encoding rules
    installed_at  TEXT NOT NULL
);
```

Every blob (centroid, vector, weights, dimension_mask, differentiator) implicitly belongs to the version recorded in this row. Changing D or the slot layout is a layout-version bump that requires re-encoding all stored blobs (one-shot migration, off the request path). Pass 1 / Pass 2 / matcher do not see version drift; the loader refuses to start if any blob length mismatches the active layout's D.

The `vector version` slot in the Quality bucket of every composed vector echoes the layout version so a misconfigured upstream vector cannot silently match against a different layout's blobs.

## Vector composition

Composed by `IdentityVectorContributor` (foundation Match step) from signals already on the blackboard plus raw headers it pulls directly. Dimension count is whatever the encoding produces (~110-180 dims expected). No artificial cap.

```
Network              ASN, IP-subnet, country, region, city, is_datacenter, is_vpn, is_tor
Locale               accept-language stack ordered, timezone hint, save-data
Header bag           accept exact, accept-encoding ordered, sec-ch-ua brands ordered,
                     sec-ch-ua mobile/platform/arch/bitness/model/full-version-list,
                     sec-fetch dest/mode/site/user pattern, header order, header case pattern,
                     upgrade-insecure-requests, dnt, sec-gpc, priority,
                     cache-control / pragma, te, connection
HTTP-library tells   x-requested-with, custom header signature
Transport            tls_ja4, h2 settings hash, alpn, tcp p0f                (zero on plaintext)
Session              cookie count log-norm, has-returning-cookie, entry page family,
                     referer host family, request rate, session age, method pattern, path entropy
Quality              dimension-presence ratio, transport-quality, cleartext flag, vector version
```

Encoding rules:
- High-cardinality strings (ASN, header order hash) use locality-sensitive hashing into multiple slots: small string changes move only one slot, not all.
- Sets (sec-fetch pattern) encode as scaled bitmask.
- Counts log-normalised: `tanh(log(1 + count) / k)`.
- Booleans encode as ±1.
- Missing data encodes as 0; the quality dimension records absence.
- Full vector L2-normalised so plain cosine and weighted cosine are well-behaved.

## Metastability: maturity-weighted absorption

Each observation is initially stored as a detailed vector in `fingerprint_observations` (`absorbed_at IS NULL`). It stays detailed until an absorption threshold fires:

- Maturity threshold: the fingerprint has seen N additional requests since this observation.
- Age threshold: this observation is older than the configured retention window AND the fingerprint is currently *active*. Active means the fingerprint has received at least one observation within `ActiveWindowDays` (default 90). Inactive fingerprints' detailed observations are kept indefinitely so a returning visitor after a long absence still has detailed forms to match against.

Both are operator-tunable. Neither is a data cap. If a fingerprint sees one observation per year, that observation stays detailed forever until something arrives near it.

Absorption is a single transaction:

```
new_centroid = (centroid * maturity + obs.vector) / (maturity + 1)
maturity++
mark obs.absorbed_at = now
DELETE row from observations_vec for this obs
UPDATE fingerprints + fingerprints_vec for the new centroid
```

Centroid carries every absorbed observation's contribution forever. Detailed observations preserve recent forms. Together they cover both the year-ago returning visitor (centroid match) and the just-released-Chrome-version match (recent observation match).

## Lookup mechanics

The Pass 2 query in detail:

```
1. SELECT top-K (default 10) candidates from fingerprints_vec by plain cosine to V
2. SELECT top-K candidates from observations_vec by plain cosine to V
   → group by fingerprint_id, keep best obs per fingerprint
3. Union both candidate sets, dedupe by fingerprint_id (keep BOTH the
   centroid-distance and best-observation-distance for each candidate)
4. For each candidate fingerprint:
     load (centroid, weights) from fingerprints row
     effective_weight = global_weight ⊙ candidate.weights
     score = max(
       weighted_cosine(V, candidate.centroid, effective_weight),
       weighted_cosine(V, candidate.best_obs, effective_weight)   -- if present
     )
5. Best score → handled per the outcome rules in the two-pass section.
   On the new-fingerprint branch only: scan archetypes by plain cosine to V
   (small set, brute-force, ≪ 1 ms), pick nearest archetype, seed the new
   fingerprint per the templates rule in the archetypes section.
```

Both vec0 queries are O(log n) effective. The re-rank is O(K). Total per-request L2 cost is independent of total fingerprint count and bounded by K.

## Cluster-derived global dimension weights

Some dimensions discriminate identity better than others as a property of the dataset, independent of any one fingerprint. TLS JA4 stays nearly constant within a visitor and varies sharply between visitors - high identity weight. Request rate varies request-to-request even for one visitor - low identity weight. ASN is in between.

`BotClusterService` already groups related fingerprints (Leiden clustering over the existing signature graph). Those cluster labels are the supervision signal: dimensions that vary little within a cluster but a lot between clusters are the strong identity discriminators.

```
For each dim i:
  within_var[i]   = mean over clusters of variance(centroid[i] | members of cluster)
  between_var[i]  = variance over clusters of mean(centroid[i] | members of cluster)
  fisher_ratio[i] = between_var[i] / (within_var[i] + epsilon)
  global_weight[i] = normalise(fisher_ratio) so the vector has mean 1.0

Stored in identity_dimension_weights (one row, refreshed periodically by
IdentityWeightCalibrationService).

Effective weight at match time:
  effective_weight = global_weight ⊙ per_fingerprint_weight       (element-wise product)
  weighted_cosine(V, candidate, effective_weight)
```

```sql
CREATE TABLE identity_dimension_weights (
    id                INTEGER PRIMARY KEY CHECK (id = 1),    -- single row
    weights           BLOB NOT NULL,                          -- float[D]
    samples_used      INTEGER NOT NULL,
    clusters_used     INTEGER NOT NULL,
    archetypes_used   INTEGER NOT NULL,
    last_computed_at  TEXT NOT NULL
);
```

### Archetypes: the seed population

The system does not start empty. Pre-loaded **archetypes** populate the fingerprint space from request 1, representing canonical traffic shapes:

- Human browsers: Chrome desktop (Win / Mac / Linux), Firefox desktop, Safari desktop, Mobile Chrome, Mobile Safari
- Verified bots: Googlebot, Bingbot, DuckDuckBot, Applebot
- Social fetchers: Mastodon family, Slack-LinkedIn-Twitter unfurlers, Discord
- Tools: curl, wget, python-requests, Go-http-client, Java HttpClient, Postman
- Headless / automation: HeadlessChrome (Puppeteer/Playwright signature)

Each archetype is a labelled centroid plus a dimension mask saying which dims it confidently asserts.

```sql
CREATE TABLE identity_archetypes (
    archetype_id      TEXT PRIMARY KEY,
    name              TEXT NOT NULL,
    description       TEXT,
    centroid          BLOB NOT NULL,           -- float[D]; 0 in unmasked dims
    dimension_mask    BLOB NOT NULL,           -- float[D] in [0,1]; confidence per dim
    archetype_kind    TEXT NOT NULL,           -- "human-browser" | "verified-bot" | "tool" | "headless"
    descendant_count  INTEGER NOT NULL,        -- fingerprints currently mapped to this archetype
    last_refined_at   TEXT NOT NULL
);
```

Loaded at startup from `Definitions/IdentityArchetypes/*.yaml` (alongside the existing `BotPatterns/`). Stored in their own table - they are not rows in `fingerprints` and have no entry in `fingerprint_keys`. The L1 cache never points at them. Pass 2 queries them as a separate fallback step (small set, brute-force cosine is fine; vec0 unnecessary).

### Three roles for archetypes

1. **Templates for new-fingerprint allocation.** When Pass 2 finds no real fingerprint above LOOSE_THRESHOLD, the matcher looks up the nearest archetype by plain cosine. The new fingerprint is allocated with:
   - centroid = archetype centroid blended with the observation (light blend, mostly the observation)
   - weights = archetype dimension_mask
   - archetype_origin = archetype_id (recorded on the fingerprint row for lineage)
   - inferred_client_type = archetype_id (seed and inferred type start aligned; drift can move it later)
   - inferred_type_confidence = nearest-archetype cosine score
   The fingerprint inherits the archetype's prior on which dims matter, then drifts from it.

2. **Calibration label source.** `IdentityWeightCalibrationService` treats each archetype as one cluster centroid in the Fisher-ratio computation, weighted by `descendant_count + 1` (so populated archetypes count more, empty ones still contribute their prior). Real fingerprints contribute their own centroids as cluster centroids too. The result is a global weight vector that's sensible from cold start (archetypes carry it) and gets sharper as real traffic accumulates.

3. **Self-refinement.** Archetypes are not frozen. The same calibration service refreshes each archetype's centroid as a regularised mean of its descendants:
   ```
   archetype.centroid = (1 - α) * original_seed + α * mean(descendant centroids)
   ```
   `α` ramps from 0 (no descendants) toward 0.7 as `descendant_count` grows. The original YAML seed is regulariser; with millions of real Chrome desktop fingerprints the archetype matches what Chrome actually looks like in this deployment, not what the spec writer guessed.

The matcher's effective weights are therefore: *seeded* (from the nearest archetype on allocation), *personalised* (per-fingerprint learning from corrections and stability - see next section), and *contextualised* (global from Fisher ratios over fingerprints + archetypes). All three durable, all in the same DB.

### Global weight loading on the matcher

`global_weight` is loaded from `identity_dimension_weights` at matcher startup and held in memory. The matcher checks `last_computed_at` once per refresh interval (default 60 s) and reloads the blob if it has advanced. Pass 1 (quick confirm) and Pass 2 (re-rank) both compose `effective_weight = global_weight ⊙ candidate.weights` from this in-memory copy. The hot path never reads from `identity_dimension_weights`.

### Inferred client type

The system's job is not just to identify a fingerprint as itself; it is to *infer what kind of client it is* from observed behaviour. The nearest-archetype computation gives that classification for free.

Per fingerprint, the matcher tracks two archetype links:

- **`archetype_origin`** - the archetype the fingerprint was seeded from when first allocated. Immutable. Lineage.
- **`inferred_client_type`** - the archetype this fingerprint currently most resembles by weighted cosine over its centroid. Mutable. Recomputed *whenever the centroid updates* (during absorption transactions); the result is stored on the row. Per-request emission of `identity.client_type` reads the cached value off the row - never a per-request scan over archetypes.

These can diverge. A fingerprint allocated as a Chrome desktop visitor whose subsequent observations look more like HeadlessChrome ends up with `archetype_origin = chrome-desktop` and `inferred_client_type = headless-chrome`. That divergence is a strong signal in itself.

```sql
-- Added to the fingerprints table:
--   archetype_origin           TEXT,        -- seed; null only for archetypes themselves
--   inferred_client_type       TEXT NOT NULL,
--   inferred_type_confidence   REAL NOT NULL,
--   inferred_type_changed_at   TEXT NOT NULL
```

Per-request signals carry the inference into the rest of the pipeline:

```
identity.client_type            inferred_client_type of the matched fingerprint
identity.client_type_confidence weighted cosine score to that archetype
identity.client_type_origin     archetype_origin (lineage)
identity.client_type_drift      bool - set when this request's update flipped
                                inferred_client_type to a different archetype
```

The verdict logic and action policies treat `identity.client_type` as a behavioural classification, not just a label: a fingerprint inferred as `headless-chrome` warrants different policy than one inferred as `chrome-desktop`, even if both currently look human-shaped on a single request. The dashboard groups and labels by `inferred_client_type`. The deterministic name synthesizer prefers the archetype name when present.

Drift is the loop close: behaviour observed → centroid update → nearest-archetype recomputed → if it changed, emit `identity.client_type_drift` with the prior and new types. EntityResolution and the dashboard surface drift events for review. A fingerprint repeatedly drifting between archetypes is itself diagnostic (could be a multiplexed identity, could be a tool changing its disguise).

### Archetype YAML

Mirrors the existing `BotPatterns/*.yaml` pattern.

```yaml
# Definitions/IdentityArchetypes/chrome-desktop.yaml
archetype_id: chrome-desktop
name: Chrome Desktop
description: Chromium-family browser on a desktop OS
archetype_kind: human-browser
dimensions:
  ua_family:
    value: Chrome
    confidence: 0.9
  accept_encoding_ordered:
    value: "gzip, deflate, br, zstd"
    confidence: 0.85
  sec_ch_ua_mobile:
    value: false
    confidence: 0.95
  sec_fetch_pattern:
    value: "dest+mode+site+user"
    confidence: 0.9
  has_upgrade_insecure_requests:
    value: true
    confidence: 0.9
  # dims not listed are unmasked (mask = 0); the archetype makes no claim about them
```

Loader compiles each YAML into the binary `centroid` + `dimension_mask` blobs at startup using the same encoding rules as the live IdentityVectorContributor. Archetypes and live vectors share dimension layout and encoding, so cosine between them is meaningful.

## Per-fingerprint weight learning

Per-fingerprint weights have two learning signals. Both nudge the same vector; both run in the request hot path; both are durable on the fingerprint row.

### Signal 1: corrections (what discriminated when L1 was wrong)

When Pass 2 corrects Pass 1, the differentiator vector is computed and stored on the correction row. The Pass 2 fingerprint's weights are updated toward the dimensions that distinguished it from L1's wrong guess:

```
For each dim i:
  delta_i = (V[i] - L1.centroid[i])² - (V[i] - L2.centroid[i])²
  if delta_i > 0:
    L2.weights[i] += correction_learning_rate * delta_i
```

### Signal 2: stability (what stays the same on this fingerprint's traffic)

When a confirmed match folds an observation into the centroid (the absorption transaction), per-dim deviation is also computable: dims where the observation matched the centroid closely are dims that have been *stable* for this fingerprint. Boost the fingerprint's weight there. Conversely, dims where the observation deviated wildly are noisy for this fingerprint - slightly reduce their weight.

```
For each dim i:
  deviation_i = |obs[i] - centroid[i]|
  stability_i = 1 / (1 + deviation_i)               # in (0, 1], 1 = perfectly stable

  fingerprint.weights[i] += stability_learning_rate * (stability_i - 0.5)
                                                     # positive nudge when stable, negative when noisy
```

A fingerprint whose Accept-Encoding has been the exact same string for 1000 absorptions ends up with a high weight on that dim - for *that fingerprint*, Accept-Encoding is highly identifying. A fingerprint whose request-rate dim swings widely ends up with a low weight on that dim - request-rate doesn't reliably identify *that fingerprint*.

The two signals compose. Stability provides the everyday gradient (every absorption is a learning event, no rare correction needed). Corrections provide the sharp edits (when the matcher was actually wrong, the discriminating dims jump in importance).

### Update cadence and bounds

After either signal updates the weight vector:

```
fingerprint.weights = fingerprint.weights * D / sum(fingerprint.weights)
                                                  # renormalise to mean 1.0
fingerprint.weights[i] = clamp(fingerprint.weights[i], min_weight, max_weight)
                                                  # numeric stability, not a data cap
```

Weights initialise from the seeding archetype's `dimension_mask` (which is in the same shape as a weight vector). The two learning signals subsequently personalise from that prior. `correction_learning_rate`, `stability_learning_rate`, `min_weight`, and `max_weight` are all configuration knobs.

## Signals emitted

```
identity.fingerprint_id        the matched (or newly allocated) fingerprint UUID
identity.fingerprint_l1        Pass 1's candidate (may differ from final)
identity.vector                the composed feature vector (for debugging / replay)
identity.vector_quality        scalar [0, 1] from the quality dimension
identity.match_score           weighted cosine of the winning match
identity.is_new_fingerprint    bool - set on the allocate-new branch
identity.is_correction         bool - Pass 1 and Pass 2 disagreed (NOT the same as
                                rotation candidate; rotation lands on the same
                                fingerprint, correction picks a different one)
identity.rotation_candidate    bool - score landed in [LOOSE, MERGE) band; the
                                candidate was assigned and observation recorded
                                on it; EntityResolution will review
identity.rotation_dimensions   list of {dim_name, observed, expected} triples
                                computed when score lands in the rotation band:
                                top-K dims by |V[i] - candidate.centroid[i]| weighted
                                by effective_weight[i]; named via the layout-version
                                map in identity_vector_layout.layout_json
config.warning.cleartext_http  bool - set when transport dims are zero on what should be TLS
```

`identity.fingerprint_id` is the canonical identity for downstream consumers: persistence (`requests` and `signatures` get an `identity_fingerprint_id` column), reputation, dashboard fingerprint table, learning, EntityResolution.

## Detection pipeline integration

```
Foundation Compute:
  Signature, TransportProtocol, PiiQueryString
  → write request signals (PrimarySignature, transport.*, pii.*)

Foundation Match:
  FastPathReputation, FingerprintPrior, ContentSequence,
  IdentityVectorContributor (composes V, writes identity.vector + identity.vector_quality),
  FingerprintMatchContributor (runs Pass 1 + Pass 2, writes identity.* signals)

Classifier wave:
  Existing classifiers, unchanged. Identity match outcome does not gate them.

Aggregator → AggregatedEvidence (with identity.* in Signals)

Persistence:
  RequestPersistenceService writes by identity.fingerprint_id, not primary_signature
  (primary_signature still recorded as a per-request column for fingerprint_keys updates)
```

## Learning feedback system

Every component named so far is a node in one closed loop. Each cycle improves the next match's accuracy with no separate "training step" - learning is online, durable, and runs in the same DB the matcher reads.

```
                         ┌──────────────────────────────┐
                         │  Request arrives             │
                         │  IdentityVectorContributor   │
                         │  composes vector V           │
                         └──────────────┬───────────────┘
                                        │
                                        ▼
                         ┌──────────────────────────────┐
                         │  FingerprintMatchContributor │
                         │  Pass 1 quick confirm        │
                         │  Pass 2 vec0 + re-rank       │
                         └──────────────┬───────────────┘
                                        │
              ┌─────────────────────────┼──────────────────────────┐
              │                         │                          │
              ▼                         ▼                          ▼
    ┌───────────────────┐   ┌────────────────────┐   ┌────────────────────────┐
    │ confirmed match   │   │ rotation candidate │   │ correction (L1 ≠ L2)   │
    │                   │   │                    │   │                        │
    │ observation row   │   │ observation row    │   │ corrections row        │
    │ on this fp        │   │ on this fp         │   │ + per-fp weight update │
    │                   │   │ + signal           │   │ + fingerprint_keys     │
    │                   │   │                    │   │   upsert               │
    └─────────┬─────────┘   └─────────┬──────────┘   └────────────┬───────────┘
              │                       │                           │
              │                       ▼                           │
              │             ┌─────────────────────┐               │
              │             │ EntityResolution    │               │
              │             │ reviews rotation    │               │
              │             │ → merge or split    │               │
              │             └─────────────────────┘               │
              │                                                   │
              └────────────────┬──────────────────────────────────┘
                               │
                               ▼
              ┌─────────────────────────────────────┐
              │ Background absorption (per fp,      │
              │ fires per maturity threshold)       │
              │  • fold mature/aged observations    │
              │    into centroid (maturity-mean)    │
              │  • signal 2: per-fp stability       │
              │    weight update                    │
              │  • recompute inferred_client_type   │
              │    → emit drift if changed          │
              └────────────────┬────────────────────┘
                               │
                               ├──────────────────────────────────────┐
                               │                                      ▼
                               │            ┌──────────────────────────────────────┐
                               │            │ FingerprintDriftService              │
                               │            │ (every ~5 s)                         │
                               │            │  • pull sampled L1-confirmed obs     │
                               │            │  • run delayed Pass 2 against each   │
                               │            │  • record delayed corrections        │
                               │            │  • update fingerprint_keys & cached  │
                               │            │    bot probability EWMA              │
                               │            └─────────────────┬────────────────────┘
                               │                              │
                               ▼                              ▼
              ┌─────────────────────────────────────┐
              │ BotClusterService (Leiden)          │
              │  → cluster labels per fingerprint   │
              └────────────────┬────────────────────┘
                               │
                               ▼
              ┌─────────────────────────────────────┐
              │ IdentityWeightCalibrationService    │
              │  • Fisher ratios over (real         │
              │    clusters + archetypes)           │
              │  → global_weight                    │
              │  • re-blend archetype centroids     │
              │    toward descendants' mean         │
              └────────────────┬────────────────────┘
                               │
                               ▼
              ┌─────────────────────────────────────┐
              │ Next match's effective_weight =     │
              │   global_weight ⊙ per_fp_weight     │
              │ Sharper than the last match.        │
              └─────────────────────────────────────┘
```

Eight feedback paths, each a learning event:

1. **Observation → centroid** via maturity-weighted absorption. The shape's long-term baseline absorbs every observation forever.
2. **Observation → recent-form cloud** until absorbed. Gives Pass 2 a current-form match path.
3. **Absorption → per-fingerprint stability weight nudge**. Dims that have been stable for *this* fingerprint matter more for matching it.
4. **Correction → per-fingerprint discriminator weight update**. Dims that distinguished L2 from L1's wrong guess get sharp weight bumps.
5. **Correction → fingerprint_keys upsert**. The L1 cache learns the right answer for next time, so the same IP+UA never costs Pass 2 again.
6. **Rotation candidate → EntityResolution review**. Borderline matches that aren't auto-confirmed get cluster-level analysis; merges and splits feed back into the fingerprint shape population.
7. **Cluster labels → global Fisher ratios → global_weight**. Dimensions that discriminate between actor groups get system-wide weight; dimensions that vary noisily within groups get downweighted.
8. **Descendants → archetype centroid refinement**. The archetype population drifts toward the deployment's actual traffic distribution, which sharpens both new-fingerprint seeding and the calibration labels for the next cycle.

There are no terminal nodes. Every path eventually produces a row that the next request reads. Cold-start population is archetypes; steady-state population is the union of refined archetypes plus accumulated real fingerprints; the matcher sees no distinction between "warmed up" and "cold start" because every state is a query against the same tables.

The C# wiring:

```
IdentityVectorContributor          (foundation Match step, hot path)
FingerprintMatchContributor        (foundation Match step, hot path,
                                    runs Pass 1 + Pass 2)
IFingerprintObservationWriter      (writes obs row + queues for absorption)
IFingerprintCorrectionRecorder     (writes correction row + applies weight delta)
FingerprintAbsorptionService       (background, per-fingerprint absorption +
                                    stability learning + inferred-type recompute)
IdentityWeightCalibrationService   (background every 30m, global weights +
                                    archetype refinement)
IIdentityAnchorIndex               (vec0 or brute-force; queried by Pass 2)
IdentityArchetypeRegistry          (loaded from YAML at startup, refreshed
                                    by calibration service)
```

Each component owns one node in the loop. None reach across; data passes through DB rows. Restarting any one of them resumes the loop from where its last persisted output left off.

## Slow-path coordinator

The matcher's Pass 2 + correction-write + observation-record + EWMA-update path is the actual cost of identity work. Pass 1 (cache hits, L1 confirm wins) is sub-ms; Pass 2 needs the vector search and the store writes. Under burst - legitimate flash crowd, or an adversary deliberately tripping Pass 2 on every request - running Pass 2 in parallel for a single fingerprint produces N×CPU and N×SQLite-write contention for one verdict that should serve all of them. Worse, an adversary who understands the gate semantics can engineer requests to live in the ambiguity band, keeping the slow path saturated while the fast path emits low-confidence verdicts on the current request.

`IdentityProcessingCoordinator` is the bounded queue that gates the slow path. Pass 1 (cache hits, L1 confirm wins) does NOT touch this coordinator; verdicts continue to serve in microseconds. The coordinator only gates Pass 2 (when an L1 candidate exists to fall back to) and on-demand drift verification.

Four layered defences:

**Layer 1 - Keyed serialisation per fingerprint id.** At most one slow-path operation in flight per fp. Subsequent requests for the same fp arriving within `CoalesceWindowMs` of an in-flight call are *coalesced* - they receive a "shed" outcome and the matcher falls back to the L1 candidate's identity verdict. Older in-flight calls (>CoalesceWindowMs) cause new arrivals to skip the queue entirely and use the fast-path default. This eliminates duplicate Pass 2 invocations and serialises correction writes / observation inserts per fp so SQLite never sees concurrent updates to the same row.

**Layer 2 - Priority scheduling.** Single global priority queue rather than per-fp queues, so risky work jumps the line. Item priority is the fingerprint's risk score (cached_bot_probability for known fps) plus an aging boost so low-priority work can't be starved indefinitely. Operator-triggered work (`OperatorReverify`, `OperatorAiOpinion` from the Identities dashboard) gets a +100 priority bias and bypasses the breaker - a "Re-verify" click always runs, even under sustained pressure.

**Layer 3 - Admission control.** Per-fp cap on queued items (`MaxQueuedPerFingerprint`, default 4) plus a global queue depth cap (`MaxQueueDepth`, default 10000) with drop-oldest backpressure. Under sustained burst from one fp the freshest few requests set the verdict for all; older queued items resolve as `SheddedQueueFull` and their callers fall back to the fast-path default. The drop-oldest semantics match the verdict cache's "one verdict serves many" intent.

**Layer 4 - Circuit breaker.** When global queue depth stays above `BreakerTripThreshold` (default 80%) for `BreakerTripHoldSeconds` (default 5s), the breaker opens. New non-operator slow-path work returns `SheddedBreakerOpen` immediately; the matcher falls back to L1 verdicts. Auto-resets when depth drops below `BreakerResetThreshold` (default 30%) for `BreakerResetHoldSeconds` (default 10s). Both transitions require sustained conditions to avoid flapping.

The breaker matters because the slow path is a finite resource. Under adversarial burst, the right behaviour is to **fail open to the fast path**, not block requests. The fast path keeps serving cached or default verdicts at sub-ms; the dashboard surfaces shed events via `identity.slow_path_shed` so operators see the degradation rather than silent failure.

Worker pool: `WorkerCount` (default 4) parallel workers pull from the priority queue. Per-fp ordering is enforced by an in-flight tracker - a worker dequeueing an item whose fp is already in flight requeues it with a small priority penalty and continues. WorkerCount=1 makes the dispatch strictly serial (deterministic, useful for tests); higher counts give parallelism across fingerprints (essential for the manual AI opinion path which can take seconds via the LLM call).

Caller contract: the matcher only routes Pass 2 through the coordinator when an L1 candidate exists (so the shed fallback path has a verdict to emit). For first-time identities (no L1 binding), Pass 2 runs INLINE - concurrent allocations may produce duplicate fps (loser becomes an orphan, `fingerprint_keys` upsert resolves to one of them), but every request still emits a fingerprint id, which is the load-bearing invariant.

## Ambiguity-persistence meta-signal

The boundary-probing defence. An adversary who understands the two-pass match can engineer requests to live in the ambiguity band - just novel enough to trip Pass 2 on every request, knowing the slow path is always one request behind the fast path's emitted verdict. The cluster-inheritance / entity-family fallback closes most of that gap, but a probe-the-boundary attacker is specifically engineered to NOT cluster cleanly, so cluster fallback doesn't catch them.

The fix: aggregate the ambiguity-band events into a per-fingerprint signal that flags persistent boundary-probing as bot behaviour in its own right. Repeated slow-path triggering is itself a behavioural shape, and a rare one for legitimate traffic.

Implementation:
- `fingerprints.ambiguity_persistence` (REAL, 0..1) - EWMA-smoothed fraction of recent matches for this fp that landed in the ambiguity band.
- Each match outcome bumps the EWMA: ambiguity events (Pass 2 correction, rotation candidate, L1 confirm fail, allocation) push toward 1; clean L1 confirm successes push toward 0.
- The bump uses `UPDATE … RETURNING` for an atomic single-roundtrip read of the post-EWMA value. Concurrent writers serialise at the SQLite layer (no lost updates).
- When the post-bump value crosses `AmbiguityProbingThreshold` (default 0.4), the matcher emits `identity.ambiguity_probing = true` as a positive bot signal. Downstream classifiers can apply a flat probability bias on top.
- Always emits `identity.ambiguity_persistence` (the raw value) so the dashboard and the verdict cache can see the EWMA without thresholding.

The signal composes with the slow-path coordinator: even when the breaker is tripped under adversarial burst, the EWMA bump still happens on every request (the bump is a single UPDATE - fast). So the matcher's fast path keeps recording the boundary-probing pattern even when slow-path enrichment is shed. The adversary loses the "always one request behind" advantage they get when the slow path is the only thing watching for the pattern.

The Identities dashboard surfaces the value as a colour-banded "Ambig" column (red ≥40%, amber ≥20%, muted otherwise) so operators triaging a fingerprint can spot the boundary-probing pattern at a glance - a fingerprint with high ambiguity_persistence + low correction_count is the classic engineered-to-stay-ambiguous signal.

## What this replaces

- The HMAC(IP+UA) PrimarySignature stops being the identity key. It remains as the per-request fingerprint and as the Pass 1 lookup key.
- `identity_fingerprint_id` becomes the persistence key for `signatures`, `requests`, `sessions`, reputation tables.
- The dashboard "Top Fingerprints" panel groups by `identity.fingerprint_id`. The hash IDs the user saw earlier are replaced by either the synthesised deterministic name or a stable fingerprint UUID.
- EntityResolution learns over `identity.fingerprint_id` clusters. Rotation candidates from L2 feed merge/split decisions.

## What stays the same

- Detector pipeline.
- AggregatedEvidence shape and merge semantics.
- Foundation wave architecture.
- Signal store, BlackboardState, MergeSignalSources contract.
- The two parallel signal stores collapsing rule from `signal-contracts.md`.

## What this does NOT include in its initial form

- Cross-fingerprint clustering beyond what EntityResolution already does.
- Time-series of weight evolution per fingerprint (correction_count is a cheap proxy).
- Multi-tenant isolation of fingerprint stores.

These are extensions, not preconditions.

## Configuration knobs

```
Identity:
  Vector:
    AbsorptionMaturityThreshold = 5         # absorb obs after fingerprint sees N more requests
    AbsorptionAgeDays           = 30        # absorb obs older than this on active fingerprints
    ActiveWindowDays            = 90        # fingerprint counts as active if observed within
    ObservationSamplingRate     = 1.0       # fraction of L1-confirmed requests to record obs for
                                            # (1.0 = every request; 0.1 = 10% sample on very hot fps)
  Match:
    MergeThreshold              = 0.92      # weighted-cosine score for confident match
    LooseThreshold              = 0.75      # below this, allocate new fingerprint
    TopK                        = 10        # candidates per vec0 query
    RotationDimensionsTopK      = 5         # dims listed in identity.rotation_dimensions
  Weights:
    CorrectionLearningRate      = 0.05      # per-fingerprint signal 1
    StabilityLearningRate       = 0.01      # per-fingerprint signal 2 (gentler, every absorption)
    MinWeight                   = 0.1
    MaxWeight                   = 10.0
    GlobalRefreshSeconds        = 60        # how often the matcher rechecks
                                            # identity_dimension_weights.last_computed_at
  Calibration:
    CalibrationIntervalMinutes  = 30        # IdentityWeightCalibrationService run cadence
    ArchetypeRefinementCap      = 0.7       # max α in archetype self-refinement
  Engine:
    PreferSqliteVec             = true      # else brute-force UDF
```

All defaults. None are caps on data; they are tuning parameters for the matcher and its background services.

---

## Self-review checklist

Each item should be checkable against the body above.

1. **Single concept named.** "Metastable fingerprint, fast match, two passes, one DB." Body opens with this and never deviates.
2. **One database, no external files.** All tables in the SQLite DB. Vec0 virtual tables live in the same DB. Fallback UDF uses the same tables. No mention of file-backed indexes anywhere.
3. **Two-pass match defined exactly once.** Pass 1 is point lookup, Pass 2 is vector cosine, sync, comparison rules listed. Not respecified elsewhere.
4. **Compression / metastability defined exactly once.** Maturity-weighted absorption, no caps, age and maturity thresholds named. Not respecified.
5. **Per-fingerprint weights stored on the fingerprint row.** Not a separate table. Initialised from the seeding archetype's mask, updated by both learning signals (see item 20). Defined once.
6. **Vector composition listed in one place.** Encoding rules grouped at the end of that section.
7. **No artificial caps on data.** Caps mentioned (TopK = 10, MinWeight = 0.1, MaxWeight = 10) are tuning constants for matcher cost or numeric stability. None bound how many fingerprints, observations, or corrections can exist.
8. **Detector pipeline orthogonal.** Stated in the integration section; reaffirmed in "What stays the same".
9. **Signal contract.** All identity.* signals defined together, none invented later. PrimarySignature retained as per-request fingerprint and Pass 1 key, role explicit.
10. **Migration scope.** "What this replaces" and "What stays the same" name the boundaries. No phased version commitments.
11. **No version commitments.** Spec describes what the system does; release sequencing is operator decision.
12. **Failure modes covered.** Sqlite-vec missing → brute-force fallback. Plaintext HTTP → quality dim records it; config warning signal emitted.
13. **L1 wrong is recoverable.** Correction rewrites `fingerprint_keys[primary_signature]` to point at the Pass 2 winner; future requests with the same IP+UA go straight to the right fingerprint. Stated in the Pass 2 comparison block.
14. **Per-anchor weight clamps named as numeric stability, not data cap.** Stated explicitly in the weights section.
15. **Human cost bound.** Stable humans hit L1, confirm passes, no Pass 2. One point lookup + one cosine compare. Stated in the two-pass section opening principle and the cost-profile bullet list.
16. **Cold start handled by archetypes, not by uniform-weight fallback.** System is non-empty from request 1: archetypes are real rows, real cosine candidates, real calibration labels. Stated in the archetypes section.
17. **Archetypes are templates, not consumed.** Real visitors spawn new fingerprints seeded from the nearest archetype's centroid + mask, with `archetype_origin` recorded for lineage. Archetypes themselves accept no members. Stated in role 1 of the archetypes section.
18. **Archetypes self-refine.** The same calibration service that computes global weights also re-blends each archetype's centroid toward its descendants' mean, regularised by the original YAML seed. Stated in role 3 of the archetypes section.
19. **Behavioural inference is a first-class output.** Each fingerprint's nearest current archetype is its `inferred_client_type`, recomputed on every centroid update, surfaced as `identity.client_type` per request, with a `identity.client_type_drift` event when it flips. Stated in the inferred-client-type section.
20. **Per-fingerprint weights have two learning signals.** Corrections (sharp edits when L1 was wrong) and stability (everyday gradient from per-dim deviation on absorptions). Both compose; both stated in the per-fingerprint weight learning section.
21. **All learning state is durable in the same DB.** Archetypes, dimension weights, per-fingerprint weights, observations, corrections - all SQLite tables. No in-process caches that can't be reconstructed from the DB.
22. **Effective weight is the only weight at match time.** Pass 1 quick confirm and Pass 2 re-rank both use `effective_weight = global_weight ⊙ candidate.weights`. Per-fingerprint weights alone never decide a match. Stated in the two-pass section, the lookup mechanics, and the global-weight-loading section.
23. **Rotation candidate is not a correction.** Distinct semantics: rotation lands on the same fingerprint and absorbs the new form into its centroid; correction picks a different fingerprint. Two distinct signals. Stated in the rotation-candidate semantics block and the signals section.
24. **Engine choice is a C# abstraction, schema differs.** The vec0 layout and brute-force layout share the C# `IIdentityAnchorIndex` interface and identical query semantics, not identical SQL. Stated in the engine abstraction subsection.
25. **Vector layout is versioned.** `D` is fixed at deployment; `identity_vector_layout` records the active version. Layout changes are one-shot off-path migrations. The vector's `vector version` quality slot guards against cross-layout matches. Stated in the layout-versioning subsection.
26. **Allocation initialises both inferred fields.** New fingerprints set `inferred_client_type = archetype_origin` and `inferred_type_confidence` from the seeding match score so the row is non-NULL from creation. Stated in role 1 of the archetypes section.
27. **Archetype scan happens only on the new-fingerprint branch.** Lookup mechanics step 5 references it; the matcher never scans archetypes for confirmed matches or rotation candidates.
28. **Closed-loop learning system.** Eight named feedback paths, each writing a DB row that the next request reads. No terminal nodes, no separate training step. Stated in the learning feedback section.
29. **One component per loop node.** Each named C# class owns exactly one node in the feedback diagram. Components communicate through DB rows, never directly. Restarting any one resumes the loop from its last persisted output.
30. **L1-confirmed still observes.** Pass 1 confirm is "trust the identity, fast-respond, still observe" - never "trust and skip". Every confirmed match writes an observation row (subject to ObservationSamplingRate) and the full classifier pipeline still runs. Background absorption then drives drift detection. Stated in the two-pass section after the cost profile.
31. **Cached fingerprint-level verdict served in L1.** Each fingerprint row carries `cached_bot_probability` and `cached_risk_band`. L1 confirm reads them and emits as signals so action policies use the fingerprint's *learned* prior, not just this request's in-line score. Stated in the L1-still-observes consequences.
32. **FingerprintDriftService runs delayed L2 verification.** Distinct from the absorption service. Pulls sampled observations from the L1-confirm queue every few seconds, runs Pass 2, records delayed corrections, updates fingerprint_keys. Stated in the L1-still-observes consequences and in the loop diagram (extra node).
33. **Latency tiers for feedback are explicit.** `cached_bot_probability` updates per request in-line; drift verification runs every few seconds; absorption fires per-fp on maturity threshold; global calibration runs every 30 min. The hot path serves the previous tick of every loop. Stated in the feedback latency tiers table.

If any item above doesn't check out against the body, the body is wrong.