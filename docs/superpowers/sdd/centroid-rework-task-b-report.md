# Centroid Rework Task B - Completion Report

Branch: `claude/atom-followups`
Commit: `48b31aad`

## What was done

### 1. Slim* AddAsync rewired to Record hot path

All three slim search classes now call the store's `RecordX` facade directly
from `AddAsync`. No sampling gate at the call site; the store's
`WriteBehindLfuStore.ColdnessScore` eviction is the prioritisation.

- `SlimSignatureSimilaritySearch.AddAsync` calls `_signatureCentroidStore.RecordSignature(...)`
- `SlimSessionVectorSearch.AddAsync` calls `_sessionCentroidStore.RecordSession(row)`
- `SlimIntentSearch.AddAsync` calls `_intentCentroidStore.RecordIntent(...)`

Ctors changed from `(IOptions<BotDetectionOptions>, ICentroidWriter, IOptions<CentroidWriterOptions>, ILogger)` to `(IOptions<BotDetectionOptions>, I*CentroidStore, ILogger)`.

All three `AddAsync` methods return `Task.CompletedTask` (no `Task.Run`, no blocking).

### 2. Channel-writer drift deleted

Five source files removed:
- `Data/Centroids/ICentroidWriter.cs`
- `Data/Centroids/SqliteCentroidWriter.cs`
- `Data/Centroids/CentroidWriteMessage.cs`
- `Data/Centroids/NullCentroidWriter.cs`
- `Data/Centroids/CentroidWriterOptions.cs`

DI registrations removed from `BotDetectionModule.cs`:
- `AddOptions<CentroidWriterOptions>().BindConfiguration(...)`
- `TryAddSingleton<ICentroidWriter, NullCentroidWriter>()`

### 3. I-1: access_count dropped

`access_count` was hot/cold-divergent (hot tier tracked it in `MergeIntoExisting`;
`PersistBatchAsync` never wrote it back to SQLite). It was not read by
`ColdnessScore`. Dropped from:
- `SignatureCentroidEntry` record (removed `int AccessCount` positional field)
- `SignatureCentroidRow` record (removed `int AccessCount = 0` default)
- DDL in `SqliteSignatureCentroidStore.InitializeOnceAsync`
- `GetRecentSignaturesAsync` SELECT and reader
- `LoadFromDurableTierAsync` SELECT (ordinal 4 was access_count, now ordinal 4 is updated_at)
- `PersistBatchAsync` INSERT column list
- `session_store.sql` CREATE TABLE
- `SqliteDetectionArchive.cs` migration line (line 79)
- `CreateInitial` and `MergeIntoExisting` in `SqliteSignatureCentroidStore`

### 4. M-1 race fixed

`UpsertSignatureAsync(string, ...)`, `UpsertSessionAsync(SessionCentroidRow, ...)`, and
`UpsertIntentAsync(string, ...)` now acquire `_writeLock` before opening a connection
and writing directly to SQLite. This prevents races with `PersistBatchAsync` which
also runs under `_writeLock`.

Shared-connection overloads `UpsertSignatureAsync(SqliteConnection, ...)`,
`UpsertSessionAsync(SqliteConnection, ...)`, and `UpsertIntentAsync(SqliteConnection, ...)`
were deleted along with the private `UpsertXxxDirectAsync` helpers (only callers were
the now-deleted `SqliteCentroidWriter`).

## Tests

New: `SlimSearchRecordTests.cs` (9 tests)
- Signature, session, intent each have 3 assertions:
  - `RecordX` called on the calling thread (no `Task.Run`)
  - Payload matches `AddAsync` arguments
  - `AddAsync` returns a completed Task

Deleted: `SqliteCentroidWriterTests.cs`, `CentroidWriterOptionsTests.cs`,
`SlimSearchEnqueueTests.cs`, `CentroidStoreSharedConnectionTests.cs`

Updated to use `NullXxxCentroidStore` (removed `NullCentroidWriter`/`CentroidWriterOptions`):
- `SlimSignatureSimilaritySearchTests.cs`
- `SlimSessionVectorSearchTests.cs`
- `SlimIntentSearchTests.cs`
- `SessionVectorAnalyticsTests.cs`
- `GhostCentroidMatchingTests.cs`

`SqliteVectorCentroidStoreTests.cs`: removed `UpsertSignature_IncrementsAccessCount`
test and `access_count` from inline DDL.

## Results

- `dotnet build src/Mostlylucid.BotDetection`: 0 errors, 35 pre-existing XML doc warnings
- `dotnet build src/Mostlylucid.BotDetection.Test`: 0 errors
- `dotnet test --filter "FullyQualifiedName~SlimSearchRecord"`: 9/9 passed
- `dotnet test --filter "Centroid|SlimSignature|SlimSession|SlimIntent|GhostCentroid|SessionVectorAnalytics"`: 104/104 passed
