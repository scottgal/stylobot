# Single-Writer Centroid Drain (Slim* SQLite writer-breach fix) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the fire-and-forget `Task.Run`-per-add SQLite writes in the three `Slim*` similarity searches (and `FingerprintAbsorptionService`) with ONE shared single-writer centroid drain, restoring the single-writer + LFU-sampled persistence invariant the atom refactor broke.

**Architecture:** A new `SqliteCentroidWriter : ICentroidWriter` singleton owns a bounded `Channel<CentroidWriteMessage>` (DropOldest) drained by one long-lived `Task.Run(DrainAsync)` loop holding a single reused `SqliteConnection` (reconnect-on-failure), switch-routing type-tagged messages (session/signature/intent) to the right centroid table. Callers `Enqueue` non-blocking; LFU/decision-necessity sampling is applied AT ENQUEUE (via `DecisionNecessity.Value`) so the channel only carries decision-worthy writes. Mirrors `SessionPersistenceAtom`'s drain exactly.

**Tech Stack:** C#/.NET 10, `System.Threading.Channels`, `Microsoft.Data.Sqlite`, existing `DecisionNecessity` (static sampler), xUnit + in-memory SQLite (`Data Source=...;Mode=Memory;Cache=Shared`).

## Global Constraints (overview-ratified, binding — from project_slim_search_writer_breach.md)
1. ONE shared drain, NOT per-store: SQLite's write lock is FILE-wide, so per-store drains still contend. Type-tagged messages switch-routed to the right table via ONE connection.
2. LFU/decision-necessity sampling at ENQUEUE time, not drain time (unsampled writes must not fill the channel and displace decision-worthy under DropOldest).
3. Bounded `Channel` with `FullMode = DropOldest`; count drops; log the drop count on a periodic cadence; NEVER block the detection path (`TryWrite` only).
4. Single long-lived `SqliteConnection` in the drain loop with reconnect-on-failure; NO per-message `new SqliteConnection(...)`.
5. NO `BackgroundService`: singleton whose ctor starts `_drainer = Task.Run(() => DrainAsync(ct))` with a `while`/`await foreach(ReadAllAsync)` loop. Precedent: `Orchestration/Sessions/SessionPersistenceAtom.cs:95-140`.
6. Fail-closed on drain exception: log + drop that message + keep draining; never bubble to the channel writer (the detection path).
7. All settings on an Options class (no magic numbers): channel capacity, drop-log cadence, reconnect backoff, sampling threshold + the `DecisionNecessity` threshold/half-life.
- No em dashes anywhere. Tests in `Mostlylucid.BotDetection.Test`. FOSS core (`Mostlylucid.BotDetection`).

## Confirmed seams (verbatim)
- `SessionPersistenceAtom` drain precedent: `_writeQueue = Channel.CreateBounded<T>(new BoundedChannelOptions(cap){ FullMode = DropOldest, SingleReader = true, SingleWriter = false }); _drainerCts = new(); _drainerTask = Task.Run(() => DrainAsync(_drainerCts.Token));` and `DrainAsync`: `await foreach (var x in _writeQueue.Reader.ReadAllAsync(ct)) { try { await WriteAsync(x, ct); } finally { Interlocked.Decrement(ref _pending); } }` + on-cancel synchronous `while (Reader.TryRead(out var x)) WriteAsync(...)` graceful drain.
- `DecisionNecessity.Value(double botProbability, double threat, double ageSeconds, double threshold, double halfLifeSeconds, double bandwidth = 0.15) -> double` (noisy-OR of uncertainty+threat, scaled by recency). Higher = more worth persisting. `Storage/DecisionNecessity.cs:58`.
- Centroid store Upsert signatures (each currently opens its OWN `new SqliteConnection(_connectionString)` per call; ctor `(string connectionString, ILogger<T>)`):
  - `SqliteSessionCentroidStore.UpsertSessionAsync(SessionCentroidRow row, CancellationToken ct = default)`
  - `SqliteSignatureCentroidStore.UpsertSignatureAsync(string signatureId, float[] vector, bool wasBot, double confidence, CancellationToken ct = default)`
  - `SqliteIntentCentroidStore.UpsertIntentAsync(string signatureId, float[] vector, double threatScore, string intentCategory, CancellationToken ct = default)`
- `SessionCentroidRow { string SignatureId; float[] Vector; float[]? VelocityVector; float[]? VarianceVector; float[]? FreqFingerprint; string? ClusterId; int CompressionLevel; bool IsBot; double BotProbability; double Priority; }`
- The breach call sites to rewire: `Similarity/SlimSessionVectorSearch.cs:114`, `Similarity/SlimSignatureSimilaritySearch.cs:92`, `Similarity/SlimIntentSearch.cs:92`, `Identity/FingerprintAbsorptionService.cs:163` (debounced — secondary).
- DI home: `Modules/BotDetectionModule.cs:301-302` (`TryAddSingleton<ISignatureSimilaritySearch, SlimSignatureSimilaritySearch>()` etc.). The 3 centroid stores are registered nearby with a connection string; all three centroid tables live in the SAME database file (confirm the shared connection string before Task 3).

---

### Task 1: `CentroidWriterOptions` + `CentroidWriteMessage` types + `ICentroidWriter`
**Files:** Create `src/Mostlylucid.BotDetection/Data/Centroids/CentroidWriterOptions.cs`, `.../CentroidWriteMessage.cs`, `.../ICentroidWriter.cs`; Test `src/Mostlylucid.BotDetection.Test/Data/CentroidWriterOptionsTests.cs`.
**Interfaces — Produces:**
- `sealed class CentroidWriterOptions { int ChannelCapacity = 2048; TimeSpan DropLogCadence = TimeSpan.FromMinutes(1); TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(2); double SamplingThreshold = 0.05; double DecisionThreshold = 0.70; double DecisionHalfLifeSeconds = 604800; /* 7d */ }` with `const string SectionName = "BotDetection:CentroidWriter"`.
- `abstract record CentroidWriteMessage;` with `sealed record SessionCentroidWrite(SessionCentroidRow Row) : CentroidWriteMessage;`, `sealed record SignatureCentroidWrite(string SignatureId, float[] Vector, bool WasBot, double Confidence) : CentroidWriteMessage;`, `sealed record IntentCentroidWrite(string SignatureId, float[] Vector, double ThreatScore, string IntentCategory) : CentroidWriteMessage;`.
- `interface ICentroidWriter { void Enqueue(CentroidWriteMessage message); int QueueDepth { get; } long DroppedCount { get; } }` (Enqueue is non-blocking `TryWrite`).
- [ ] Step 1: Failing test `CentroidWriterOptionsTests`: bind an in-memory config with `BotDetection:CentroidWriter:ChannelCapacity=64` + `SamplingThreshold=0.2`; assert `IOptions<CentroidWriterOptions>.Value` reflects both; assert defaults (`ChannelCapacity==2048`, `SamplingThreshold==0.05`). Also assert the three `CentroidWriteMessage` subrecords are constructible and pattern-match in a `switch` (a tiny switch expression returning the table name). Run -> FAIL (types missing).
- [ ] Step 2-4: Implement the options class, the message records, the interface -> Run -> PASS -> Commit.

### Task 2: connection-accepting Upsert overloads on the 3 centroid stores
**Files:** Modify `src/Mostlylucid.BotDetection/Data/SqliteSessionCentroidStore.cs`, `.../SqliteSignatureCentroidStore.cs`, `.../SqliteIntentCentroidStore.cs`; Test `src/Mostlylucid.BotDetection.Test/Data/CentroidStoreSharedConnectionTests.cs`.
**Interfaces — Produces (add to each store, alongside the existing public method):**
- `SqliteSessionCentroidStore.UpsertSessionAsync(SqliteConnection conn, SessionCentroidRow row, CancellationToken ct = default)`
- `SqliteSignatureCentroidStore.UpsertSignatureAsync(SqliteConnection conn, string signatureId, float[] vector, bool wasBot, double confidence, CancellationToken ct = default)`
- `SqliteIntentCentroidStore.UpsertIntentAsync(SqliteConnection conn, string signatureId, float[] vector, double threatScore, string intentCategory, CancellationToken ct = default)`
Refactor: extract the existing command-building body into the new `(SqliteConnection conn, ...)` overload (uses the passed OPEN connection, does NOT open/close). The existing public `(...)` overload becomes: `await using var conn = new SqliteConnection(_connectionString); await conn.OpenAsync(ct); await UpsertXAsync(conn, ...args, ct);` (back-compat for warmup/tests). Keep the existing try/catch-logs-warning on the public overload; the connection-accepting overload lets exceptions propagate (the drain owns fail-closed).
- [ ] Step 1: Failing test `CentroidStoreSharedConnectionTests`: open ONE `SqliteConnection` to a `Mode=Memory;Cache=Shared` DB, create the three centroid tables, construct the three stores, call each `Upsert*Async(conn, ...)` on the SAME connection, then read each table back and assert the row landed. Run -> FAIL (overload missing).
- [ ] Step 2-4: Implement the overloads by extracting the command body -> Run -> PASS (confirm the existing public-overload tests still pass) -> Commit.

### Task 3: `SqliteCentroidWriter` — the single-writer drain
**Files:** Create `src/Mostlylucid.BotDetection/Data/Centroids/SqliteCentroidWriter.cs`; Test `src/Mostlylucid.BotDetection.Test/Data/SqliteCentroidWriterTests.cs`.
**Interfaces — Consumes:** Task 1 (`ICentroidWriter`, `CentroidWriteMessage`, `CentroidWriterOptions`), Task 2 (the `(SqliteConnection conn, ...)` overloads), the 3 centroid stores. **Produces:** `sealed class SqliteCentroidWriter : ICentroidWriter, IDisposable`. Ctor `(string connectionString, SqliteSessionCentroidStore sessionStore, SqliteSignatureCentroidStore signatureStore, SqliteIntentCentroidStore intentStore, IOptions<CentroidWriterOptions> options, ILogger<SqliteCentroidWriter> logger)`. Mirror `SessionPersistenceAtom`: bounded channel (`options.ChannelCapacity`, `DropOldest`, `SingleReader=true`), `_drainerTask = Task.Run(() => DrainAsync(_cts.Token))` in ctor. `Enqueue`: `if (!_channel.Writer.TryWrite(message)) Interlocked.Increment(ref _dropped);` (with DropOldest, TryWrite returns true; the increment is belt-and-braces). `DrainAsync`: hold ONE `SqliteConnection` opened lazily; `await foreach (var msg in _channel.Reader.ReadAllAsync(ct))` -> `switch (msg) { SessionCentroidWrite s => await _sessionStore.UpsertSessionAsync(conn, s.Row, ct); SignatureCentroidWrite g => await _signatureStore.UpsertSignatureAsync(conn, g.SignatureId, g.Vector, g.WasBot, g.Confidence, ct); IntentCentroidWrite i => await _intentStore.UpsertIntentAsync(conn, i.SignatureId, i.Vector, i.ThreatScore, i.IntentCategory, ct); }` each wrapped in try/catch (log + continue = fail-closed); on a SqliteException that indicates a broken connection, dispose + null the connection + `await Task.Delay(options.ReconnectBackoff, ct)` so the next message reopens it. Periodically (every `options.DropLogCadence`, tracked by comparing a stored `DateTimeOffset` passed... use a simple counter: every N messages OR a `PeriodicTimer` in the drain) log `_dropped` if it grew. `Dispose`: cancel, drain remaining synchronously, dispose connection.
- [ ] Step 1: Failing tests `SqliteCentroidWriterTests` (real `Mode=Memory;Cache=Shared` DB + tables): (a) `Enqueue(new SignatureCentroidWrite(...))` then await a short poll -> the row lands in `signature_centroids` (routing + one-connection write); (b) enqueue one of each type -> all three tables get their row; (c) fail-closed: a message whose row violates a constraint (or a stubbed store that throws once) does NOT stop a following good message from persisting; (d) `DroppedCount` increments when the channel is flooded past capacity with a paused drain (or assert DropOldest semantics); (e) `Dispose` drains the last queued message. Run -> FAIL.
- [ ] Step 2-4: Implement the drain (mirror `SessionPersistenceAtom.cs:95-140`) -> Run -> PASS -> Commit.

### Task 4: enqueue-time sampling + rewire the 3 `Slim*` `AddAsync`
**Files:** Modify `src/Mostlylucid.BotDetection/Similarity/SlimSessionVectorSearch.cs`, `.../SlimSignatureSimilaritySearch.cs`, `.../SlimIntentSearch.cs` (add `ICentroidWriter` + `IOptions<CentroidWriterOptions>` ctor deps; replace the `Task.Run` persist block); Test `src/Mostlylucid.BotDetection.Test/Similarity/SlimSearchEnqueueTests.cs`.
**Interfaces — Consumes:** Task 1+3. In each `AddAsync`, AFTER `_cache.Set(...)`, replace the `_ = Task.Run(async () => { ... Upsert... })` with:
```
var necessity = DecisionNecessity.Value(
    botProbability: <isBot?botProbability:1-botProbability OR the raw prob per store>,
    threat: <threatScore for intent; botProbability for signature/session>,
    ageSeconds: 0,               // fresh write
    threshold: _opts.DecisionThreshold,
    halfLifeSeconds: _opts.DecisionHalfLifeSeconds);
if (necessity >= _opts.SamplingThreshold)
    _centroidWriter.Enqueue(new SignatureCentroidWrite(signatureId, vector, wasBot, confidence)); // or Session/Intent
```
No `Task.Run`, no `await` on a SQLite write, never blocks. `AddAsync` returns `Task.CompletedTask` after the (synchronous, non-blocking) `Enqueue`.
- [ ] Step 1: Failing tests `SlimSearchEnqueueTests` (inject a fake `ICentroidWriter` capturing enqueued messages): (a) a HIGH-necessity add (uncertain `botProbability ~0.5` / high threat) -> exactly one `Enqueue` with the right message subtype + payload; (b) a LOW-necessity add (confident `botProbability ~0.99`, threat 0, so necessity below `SamplingThreshold`) -> NO enqueue (sampled out); (c) `AddAsync` completes synchronously (returns a completed Task) and never calls `Task.Run` (assert via the fake writer being called inline on the calling thread). Run -> FAIL.
- [ ] Step 2-4: Add the ctor deps + replace the persist blocks in all three searches -> Run -> PASS (existing Slim* tests still green) -> Commit.

### Task 5: DI registration + rewire `FingerprintAbsorptionService` + integration
**Files:** Modify `src/Mostlylucid.BotDetection/Modules/BotDetectionModule.cs` (register `ICentroidWriter` + bind options + ensure eager resolution), `src/Mostlylucid.BotDetection/Identity/FingerprintAbsorptionService.cs` (route its absorption persist through `ICentroidWriter` if it writes a centroid; if its `Task.Run` is purely the debounce timer around a non-centroid absorption, leave the debounce but ensure any centroid write inside `RunAbsorptionForAsync` enqueues rather than opens its own connection); Test `src/Mostlylucid.BotDetection.Test/Data/CentroidWriterIntegrationTests.cs`.
- [ ] Step 1: Failing tests: (a) DI smoke — build the core service provider (or the minimal registration) with `AddBotDetection` + a real DB path, assert `ICentroidWriter` resolves as a singleton and the three `Slim*` searches receive it; (b) integration — resolve the real `SlimSignatureSimilaritySearch` + `SqliteCentroidWriter` over a temp DB, call `AddAsync` with a high-necessity entry, poll until the row appears in `signature_centroids`, assert the process used ONE writer (no per-add connection — assert by reading `SqliteCentroidWriter.QueueDepth`/drop metrics, or that only the writer wrote). Run -> FAIL.
- [ ] Step 2-5: Register `services.TryAddSingleton<ICentroidWriter, SqliteCentroidWriter>(sp => new SqliteCentroidWriter(<shared centroid connection string, same the 3 stores use>, ...))` near `BotDetectionModule.cs:301`; add `services.Configure<CentroidWriterOptions>(config.GetSection(CentroidWriterOptions.SectionName))`; ensure the writer is resolved at startup so its drain loop starts (add to the hosted-singletons bootstrap resolution list if singletons are lazily resolved). Rewire `FingerprintAbsorptionService` centroid write. -> Run -> PASS -> build `dotnet build src/Mostlylucid.BotDetection` -> run `Mostlylucid.BotDetection.Test` full -> Commit.
- [ ] Step 6: Report the full Task.Run sweep results to overview (thread `overview-slim-search-sqlite-writer-breach` follow-up) + ping when the PR/branch is up for their review. Validation: run `scripts/k6/k6-memory-cardinality.js` on a clean rig (SUT this Mac, load from Maxo/.15) and confirm RSS no longer balloons + throughput holds.

## Self-Review
- **Constraint coverage:** one-shared-drain -> T3 (switch-route, one connection); sample-at-enqueue -> T4 (`DecisionNecessity.Value` before `Enqueue`); DropOldest+drop-log -> T1 options + T3 counter; single-connection-reconnect -> T3; no-BackgroundService -> T3 (`Task.Run(DrainAsync)` in ctor, mirrors `SessionPersistenceAtom`); fail-closed -> T3 (per-message try/catch); configurable -> T1 `CentroidWriterOptions`.
- **Type consistency:** `ICentroidWriter.Enqueue(CentroidWriteMessage)` + the 3 subrecords (T1) consumed by T3 (drain switch) and T4 (Slim enqueue); the `(SqliteConnection conn, ...)` overloads (T2) consumed by T3's drain; `DecisionNecessity.Value(...)` (T4) uses the 6-arg signature verbatim.
- **Placeholders:** the "shared centroid connection string" in T5 is a locate-direction (confirm the 3 stores share one DB + reuse that connection string) resolved by reading their DI registration; the `botProbability`/`threat` argument mapping per store in T4 is spelled per-store in the code block.
- **Debounce caveat:** `FingerprintAbsorptionService` is debounced/deduped (secondary); T5 only reroutes its centroid write, it does not remove the debounce timer.
