# UI Code Review Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix all issues identified in the `Mostlylucid.BotDetection.UI` code review -5 critical, 9 important, 6 minor.

**Architecture:** All fixes are surgical -no interface changes, no new abstractions. Each fix targets a specific file and a specific defect identified in the review. Tasks are ordered so earlier tasks don't conflict with later ones.

**Tech Stack:** C# 13 / .NET 10, SQLite (Microsoft.Data.Sqlite), MailKit (new dep), Fluid.Core, SignalR

---

## Files Modified

| File | Changes |
|------|---------|
| `Services/SqliteSignatureLabelStore.cs` | Add `_initLock` SemaphoreSlim, double-check init, dispose it |
| `Services/SqliteDashboardEventStore.cs` | Fix DisposeAsync, N+1 timeseries, country/endpoint detail queries, GetSignaturesAsync param, GetDetectionsAsync cmd disposal, top_reasons_json DDL migration, add periodic prune method |
| `Services/LiquidWidgetRenderer.cs` | LRU eviction instead of full cache clear |
| `Services/VisitorListCache.cs` | Batch EvictOldest, static compiled regexes in InferBotIdentity |
| `Services/DashboardSummaryBroadcaster.cs` | Call periodic prune on each broadcast tick |
| `Services/Auth/StyloBotSmtpEmailSender.cs` | Replace SmtpClient with MailKit |
| `ViewComponents/Dashboard/SbSummaryStatsViewComponent.cs` | Replace int.MaxValue with named constant |
| `ViewComponents/Dashboard/SbSessionsListViewComponent.cs` | Bound fetch to needed count |
| `Models/DashboardModels.cs` | Document IpAddress field as intentionally always null |
| `Mostlylucid.BotDetection.UI.csproj` | Add MailKit package reference |

---

## Task 1: Fix `SqliteSignatureLabelStore` -add init lock and dispose it

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Services/SqliteSignatureLabelStore.cs`

- [ ] **Step 1: Add `_initLock` field and wrap `EnsureInitializedAsync` with double-check**

Replace the field declarations and `EnsureInitializedAsync` method:

```csharp
// Replace:
private readonly SemaphoreSlim _writeLock = new(1, 1);
private bool _initialized;

// With:
private readonly SemaphoreSlim _initLock = new(1, 1);
private readonly SemaphoreSlim _writeLock = new(1, 1);
private bool _initialized;
```

Replace `EnsureInitializedAsync`:

```csharp
private async Task EnsureInitializedAsync(CancellationToken ct = default)
{
    if (_initialized) return;
    await _initLock.WaitAsync(ct);
    try
    {
        if (_initialized) return;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;

            CREATE TABLE IF NOT EXISTS labels (
                signature TEXT NOT NULL,
                kind INTEGER NOT NULL,
                confidence REAL NOT NULL DEFAULT 1.0,
                labeled_by TEXT NOT NULL,
                labeled_at TEXT NOT NULL,
                note TEXT,
                PRIMARY KEY (signature, labeled_by)
            );

            CREATE INDEX IF NOT EXISTS idx_labels_signature ON labels(signature);
            CREATE INDEX IF NOT EXISTS idx_labels_at ON labels(labeled_at DESC);
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        _initialized = true;
        _logger.LogInformation("SQLite label store initialized");
    }
    finally
    {
        _initLock.Release();
    }
}
```

- [ ] **Step 2: Dispose `_initLock` in `DisposeAsync`**

```csharp
// Replace:
public ValueTask DisposeAsync()
{
    _writeLock.Dispose();
    return ValueTask.CompletedTask;
}

// With:
public ValueTask DisposeAsync()
{
    _initLock.Dispose();
    _writeLock.Dispose();
    return ValueTask.CompletedTask;
}
```

- [ ] **Step 3: Build to verify**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Services/SqliteSignatureLabelStore.cs
git commit -m "fix(ui): add _initLock to SqliteSignatureLabelStore to prevent init race"
```

---

## Task 2: Fix `SqliteDashboardEventStore` -dispose `_initLock`

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Services/SqliteDashboardEventStore.cs` (line ~1081)

- [ ] **Step 1: Fix DisposeAsync**

```csharp
// Replace:
public ValueTask DisposeAsync()
{
    _writeLock.Dispose();
    return ValueTask.CompletedTask;
}

// With:
public ValueTask DisposeAsync()
{
    _initLock.Dispose();
    _writeLock.Dispose();
    return ValueTask.CompletedTask;
}
```

- [ ] **Step 2: Build to verify**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

- [ ] **Step 3: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Services/SqliteDashboardEventStore.cs
git commit -m "fix(ui): dispose _initLock in SqliteDashboardEventStore.DisposeAsync"
```

---

## Task 3: Fix N+1 in `GetTimeSeriesAsync`

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Services/SqliteDashboardEventStore.cs` (line ~420)

The current implementation issues one query per time bucket (typically 24 queries for hourly data). Replace the entire method with a single GROUP BY query that lets SQLite do the bucketing.

- [ ] **Step 1: Replace `GetTimeSeriesAsync` with single-query implementation**

```csharp
public async Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize)
{
    await EnsureInitializedAsync();

    // Compute SQLite strftime format from bucket size
    // Supported: 1h → '%Y-%m-%dT%H:00:00', 1d → '%Y-%m-%dT00:00:00', smaller → minute
    string bucketFormat;
    int bucketSeconds;
    if (bucketSize >= TimeSpan.FromDays(1))
    {
        bucketFormat = "%Y-%m-%dT00:00:00";
        bucketSeconds = (int)TimeSpan.FromDays(1).TotalSeconds;
    }
    else if (bucketSize >= TimeSpan.FromHours(1))
    {
        bucketFormat = "%Y-%m-%dT%H:00:00";
        bucketSeconds = (int)TimeSpan.FromHours(1).TotalSeconds;
    }
    else
    {
        bucketFormat = "%Y-%m-%dT%H:%M:00";
        bucketSeconds = (int)bucketSize.TotalSeconds;
    }

    await using var conn = new SqliteConnection(_connectionString);
    await conn.OpenAsync();

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
        SELECT
            strftime('{bucketFormat}', timestamp) AS bucket,
            SUM(CASE WHEN is_bot = 1 THEN 1 ELSE 0 END) AS bots,
            SUM(CASE WHEN is_bot = 0 THEN 1 ELSE 0 END) AS humans,
            COUNT(*) AS total
        FROM detections
        WHERE timestamp >= @start AND timestamp < @end
        GROUP BY bucket
        ORDER BY bucket
        """;
    cmd.Parameters.AddWithValue("@start", startTime.ToString("O"));
    cmd.Parameters.AddWithValue("@end", endTime.ToString("O"));

    var dbPoints = new Dictionary<string, DashboardTimeSeriesPoint>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var bucket = reader.GetString(0);
        if (DateTime.TryParse(bucket, out var ts))
            dbPoints[bucket] = new DashboardTimeSeriesPoint
            {
                Timestamp = ts,
                BotCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                HumanCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                TotalCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3)
            };
    }

    // Fill gaps with zero-count buckets to keep the chart series continuous
    var points = new List<DashboardTimeSeriesPoint>();
    var current = startTime;
    while (current < endTime)
    {
        var key = current.ToString(bucketSeconds >= 86400 ? "yyyy-MM-ddT00:00:00" :
                                  bucketSeconds >= 3600   ? "yyyy-MM-ddTHH:00:00" :
                                                            "yyyy-MM-ddTHH:mm:00");
        points.Add(dbPoints.TryGetValue(key, out var p) ? p : new DashboardTimeSeriesPoint
        {
            Timestamp = current,
            BotCount = 0,
            HumanCount = 0,
            TotalCount = 0
        });
        current = current.Add(bucketSize);
    }
    return points;
}
```

- [ ] **Step 2: Build to verify**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

- [ ] **Step 3: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Services/SqliteDashboardEventStore.cs
git commit -m "fix(ui): replace N+1 timeseries queries with single strftime GROUP BY"
```

---

## Task 4: Fix `GetCountryDetailAsync` and `GetEndpointDetailAsync` -direct queries

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Services/SqliteDashboardEventStore.cs` (lines ~551-635)

Both methods currently call the full-list method and filter in memory. Replace with direct WHERE-clause queries.

- [ ] **Step 1: Replace `GetCountryDetailAsync`**

```csharp
public async Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null)
{
    await EnsureInitializedAsync();
    await using var conn = new SqliteConnection(_connectionString);
    await conn.OpenAsync();

    var timeFilter = "";
    if (startTime.HasValue) timeFilter += " AND timestamp >= @start";
    if (endTime.HasValue)   timeFilter += " AND timestamp <= @end";

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
        SELECT
            COUNT(*) AS total,
            SUM(CASE WHEN is_bot = 1 THEN 1 ELSE 0 END) AS bots
        FROM detections
        WHERE country_code = @cc{timeFilter}
        """;
    cmd.Parameters.AddWithValue("@cc", countryCode);
    if (startTime.HasValue) cmd.Parameters.AddWithValue("@start", startTime.Value.ToString("O"));
    if (endTime.HasValue)   cmd.Parameters.AddWithValue("@end", endTime.Value.ToString("O"));

    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync() || reader.IsDBNull(0)) return null;

    var total = reader.GetInt32(0);
    if (total == 0) return null;
    var bots = reader.GetInt32(1);

    return new DashboardCountryDetail
    {
        CountryCode = countryCode,
        TotalCount = total,
        BotCount = bots,
        BotRate = total > 0 ? (double)bots / total : 0,
        TopBotTypes = new Dictionary<string, int>(),
        TopBots = new List<DashboardTopBotEntry>()
    };
}
```

- [ ] **Step 2: Replace `GetEndpointDetailAsync`**

```csharp
public async Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null)
{
    await EnsureInitializedAsync();
    await using var conn = new SqliteConnection(_connectionString);
    await conn.OpenAsync();

    var timeFilter = "";
    if (startTime.HasValue) timeFilter += " AND timestamp >= @start";
    if (endTime.HasValue)   timeFilter += " AND timestamp <= @end";

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
        SELECT
            COUNT(*) AS total,
            SUM(CASE WHEN is_bot = 1 THEN 1 ELSE 0 END) AS bots,
            COUNT(DISTINCT signature) AS sigs,
            AVG(processing_time_ms) AS avg_ms,
            AVG(threat_score) AS avg_threat
        FROM detections
        WHERE method = @method AND path = @path{timeFilter}
        """;
    cmd.Parameters.AddWithValue("@method", method);
    cmd.Parameters.AddWithValue("@path", path);
    if (startTime.HasValue) cmd.Parameters.AddWithValue("@start", startTime.Value.ToString("O"));
    if (endTime.HasValue)   cmd.Parameters.AddWithValue("@end", endTime.Value.ToString("O"));

    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync() || reader.IsDBNull(0)) return null;

    var total = reader.GetInt32(0);
    if (total == 0) return null;
    var bots = reader.GetInt32(1);

    return new DashboardEndpointDetail
    {
        Method = method,
        Path = path,
        TotalCount = total,
        BotCount = bots,
        BotRate = total > 0 ? (double)bots / total : 0,
        UniqueSignatures = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
        AvgProcessingTimeMs = reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
        AvgThreatScore = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
        TopActions = new Dictionary<string, int>(),
        TopCountries = new Dictionary<string, int>(),
        RiskBands = new Dictionary<string, int>(),
        TopBots = new List<DashboardTopBotEntry>(),
        RecentDetections = new List<SignatureDetectionRow>()
    };
}
```

- [ ] **Step 3: Build to verify**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

- [ ] **Step 4: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Services/SqliteDashboardEventStore.cs
git commit -m "fix(ui): replace GetCountryDetailAsync and GetEndpointDetailAsync full-scan with direct WHERE queries"
```

---

## Task 5: Fix `GetSignaturesAsync` -parameterize is_bot; fix `GetDetectionsAsync` cmd disposal; add `top_reasons_json` migration

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Services/SqliteDashboardEventStore.cs`

- [ ] **Step 1: Fix `GetSignaturesAsync` -use parameter for `is_bot`**

```csharp
// Replace:
var sql = "SELECT * FROM signatures";
if (isBot.HasValue) sql += " WHERE is_bot = " + (isBot.Value ? "1" : "0");
sql += " ORDER BY last_seen DESC LIMIT @limit OFFSET @offset";

await using var cmd = conn.CreateCommand();
cmd.CommandText = sql;
cmd.Parameters.AddWithValue("@limit", limit);
cmd.Parameters.AddWithValue("@offset", offset);

// With:
var sql = "SELECT * FROM signatures";
if (isBot.HasValue) sql += " WHERE is_bot = @isBot";
sql += " ORDER BY last_seen DESC LIMIT @limit OFFSET @offset";

await using var cmd = conn.CreateCommand();
cmd.CommandText = sql;
if (isBot.HasValue) cmd.Parameters.AddWithValue("@isBot", isBot.Value ? 1 : 0);
cmd.Parameters.AddWithValue("@limit", limit);
cmd.Parameters.AddWithValue("@offset", offset);
```

- [ ] **Step 2: Fix `GetDetectionsAsync` -use `await using` on cmd**

The current code uses `var cmd = conn.CreateCommand()` (not disposed on exception). Change to:

```csharp
// Replace:
var sql = "SELECT * FROM detections";
var conditions = new List<string>();
var cmd = conn.CreateCommand();

// With:
var sql = "SELECT * FROM detections";
var conditions = new List<string>();
await using var cmd = conn.CreateCommand();
```

(The `cmd.CommandText = sql;` assignment on line ~319 is already correct since it happens before execution.)

- [ ] **Step 3: Add `top_reasons_json` column migration in `EnsureInitializedAsync`**

In the migration section of `EnsureInitializedAsync` (where `user_agent_raw` and `risk_justification` are migrated), add `top_reasons_json`:

```csharp
// Replace the migration array:
foreach (var (table, column, colDef) in new (string, string, string)[]
{
    ("detections", "user_agent_raw", "TEXT"),
    ("detections", "risk_justification", "TEXT"),
    ("signatures", "risk_justification", "TEXT")
})

// With:
foreach (var (table, column, colDef) in new (string, string, string)[]
{
    ("detections", "user_agent_raw", "TEXT"),
    ("detections", "risk_justification", "TEXT"),
    ("signatures", "risk_justification", "TEXT"),
    ("signatures", "top_reasons_json", "TEXT")
})
```

Also add a comment above `GetTopBotsAsync` to document the cross-store dependency:

```csharp
// NOTE: This query joins the 'sessions' table created by SqliteSessionStore (core package).
// Both stores use the same database file. The join is intentional; on a fresh DB with no
// sessions yet, the subquery returns NULL for last_path, which is handled by reader.IsDBNull(12).
public async Task<List<DashboardTopBotEntry>> GetTopBotsAsync(...)
```

- [ ] **Step 4: Build to verify**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Services/SqliteDashboardEventStore.cs
git commit -m "fix(ui): parameterize is_bot in GetSignaturesAsync; fix cmd disposal; add top_reasons_json migration"
```

---

## Task 6: Add periodic prune to `DashboardSummaryBroadcaster`

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Services/DashboardSummaryBroadcaster.cs`
- Modify: `src/Mostlylucid.BotDetection.UI/Services/IDashboardEventStore.cs` (add `PruneOldDetectionsAsync`)
- Modify: `src/Mostlylucid.BotDetection.UI/Services/SqliteDashboardEventStore.cs` (implement it)

- [ ] **Step 1: Add `PruneOldDetectionsAsync` to `IDashboardEventStore`**

Open `Services/IDashboardEventStore.cs` and add:

```csharp
/// <summary>Deletes detection records older than the specified cutoff. Returns count pruned.</summary>
Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default);
```

- [ ] **Step 2: Implement `PruneOldDetectionsAsync` in `SqliteDashboardEventStore`**

Add this method to `SqliteDashboardEventStore.cs`:

```csharp
public async Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default)
{
    await EnsureInitializedAsync(ct);
    await _writeLock.WaitAsync(ct);
    try
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM detections WHERE timestamp < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct);
    }
    finally
    {
        _writeLock.Release();
    }
}
```

- [ ] **Step 3: Call prune on every broadcaster tick**

In `DashboardSummaryBroadcaster.ExecuteAsync`, after the `await Task.WhenAll(...)` line, add:

```csharp
// Prune detections older than 7 days - runs every broadcast tick (not just startup)
try
{
    var cutoff = DateTime.UtcNow.AddDays(-7);
    var pruned = await _eventStore.PruneOldDetectionsAsync(cutoff, stoppingToken);
    if (pruned > 0)
        _logger.LogDebug("Pruned {Count} old dashboard detections", pruned);
}
catch (Exception ex)
{
    _logger.LogWarning(ex, "Failed to prune old detections");
}
```

- [ ] **Step 4: Build to verify**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Services/IDashboardEventStore.cs \
        src/Mostlylucid.BotDetection.UI/Services/SqliteDashboardEventStore.cs \
        src/Mostlylucid.BotDetection.UI/Services/DashboardSummaryBroadcaster.cs
git commit -m "fix(ui): add periodic prune via DashboardSummaryBroadcaster instead of startup-only"
```

---

## Task 7: Fix `LiquidWidgetRenderer` -LRU eviction

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Services/LiquidWidgetRenderer.cs`

Currently when the cache hits 500 entries, the entire cache is cleared (thundering herd). Replace with evicting the oldest single entry.

- [ ] **Step 1: Replace cache-full branch**

```csharp
// Replace:
if (_cache.Count >= MaxCacheSize)
    _cache.Clear();

// With:
if (_cache.Count >= MaxCacheSize)
{
    // Evict one arbitrary entry to avoid thundering-herd full-clear
    var keyToEvict = _cache.Keys.FirstOrDefault();
    if (keyToEvict != null) _cache.TryRemove(keyToEvict, out _);
}
```

- [ ] **Step 2: Build to verify**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

- [ ] **Step 3: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Services/LiquidWidgetRenderer.cs
git commit -m "fix(ui): evict single LRU entry in LiquidWidgetRenderer instead of full cache clear"
```

---

## Task 8: Fix `VisitorListCache` -batch eviction and compiled regexes

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Services/VisitorListCache.cs`

Two fixes: (1) `EvictOldest` calls on every request at O(n log n) -change to only evict when 10% over capacity; (2) `InferBotIdentity` uses non-compiled `Regex.IsMatch` in a hot path -replace with static compiled fields.

- [ ] **Step 1: Fix `EvictOldest` -batch eviction**

```csharp
// Replace:
private void EvictOldest()
{
    if (_visitors.Count <= _maxVisitors) return;

    var toRemove = _visitors
        .OrderBy(kv => kv.Value.LastSeen)
        .Take(_visitors.Count - _maxVisitors)
        .Select(kv => kv.Key)
        .ToList();

    foreach (var key in toRemove)
        _visitors.TryRemove(key, out _);
}

// With:
private void EvictOldest()
{
    // Only evict when 10% over capacity to amortize the O(n log n) sort cost.
    var overage = _visitors.Count - _maxVisitors;
    if (overage <= _maxVisitors / 10) return;

    var toRemove = _visitors
        .OrderBy(kv => kv.Value.LastSeen)
        .Take(overage)
        .Select(kv => kv.Key)
        .ToList();

    foreach (var key in toRemove)
        _visitors.TryRemove(key, out _);
}
```

- [ ] **Step 2: Add static compiled regexes for `InferBotIdentity`**

The `InferBotIdentity` method uses `Regex.IsMatch(ua, @"...", RegexOptions.IgnoreCase)` for ~15 patterns. Add static compiled fields alongside the existing `AiNameRegex`, `SearchNameRegex`, `ToolNameRegex` fields:

```csharp
// Add these static compiled regex fields near the top of the class
// (alongside the existing AiNameRegex / SearchNameRegex / ToolNameRegex fields):

private static readonly Regex AiBotUaRegex = new(
    @"GPTBot|ChatGPT|CCBot|anthropic-ai|ClaudeBot|Google-Extended|PerplexityBot|Bytespider|Applebot-Extended|cohere-ai|FacebookBot|Meta-ExternalAgent",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

private static readonly Regex SearchBotUaRegex = new(
    @"Googlebot|bingbot|YandexBot|Baiduspider|DuckDuckBot|Slurp|Sogou|Applebot(?!-Extended)",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

private static readonly Regex SeoBotUaRegex = new(
    @"SemrushBot|AhrefsBot|MJ12bot|DotBot|PetalBot|MegaIndex|SerpstatBot|Sistrix|Screaming",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

private static readonly Regex MonitorBotUaRegex = new(
    @"UptimeRobot|Pingdom|Site24x7|StatusCake|Datadog|NewRelic|GTmetrix|PageSpeed|Lighthouse",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

private static readonly Regex PythonBotUaRegex = new(
    @"python-requests|python-urllib|python-httpx|aiohttp",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

private static readonly Regex CurlUaRegex = new(
    @"^curl/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

private static readonly Regex WgetUaRegex = new(
    @"^wget/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

private static readonly Regex GoBotUaRegex = new(
    @"Go-http-client|golang", RegexOptions.IgnoreCase | RegexOptions.Compiled);

private static readonly Regex JavaBotUaRegex = new(
    @"Java/|Apache-HttpClient|okhttp", RegexOptions.IgnoreCase | RegexOptions.Compiled);

private static readonly Regex NodeBotUaRegex = new(
    @"node-fetch|axios|undici", RegexOptions.IgnoreCase | RegexOptions.Compiled);

private static readonly Regex RubyBotUaRegex = new(
    @"Ruby|Faraday|Typhoeus", RegexOptions.IgnoreCase | RegexOptions.Compiled);

private static readonly Regex PhpBotUaRegex = new(
    @"PHP/|Guzzle|php-curl", RegexOptions.IgnoreCase | RegexOptions.Compiled);

private static readonly Regex PerlBotUaRegex = new(
    @"libwww-perl|LWP|Mechanize", RegexOptions.IgnoreCase | RegexOptions.Compiled);

private static readonly Regex CrawlerUaRegex = new(
    @"Scrapy|Nutch|Heritrix", RegexOptions.IgnoreCase | RegexOptions.Compiled);

private static readonly Regex HeadlessUaRegex = new(
    @"HeadlessChrome|Headless", RegexOptions.IgnoreCase | RegexOptions.Compiled);

private static readonly Regex PhantomUaRegex = new(
    @"PhantomJS", RegexOptions.IgnoreCase | RegexOptions.Compiled);

private static readonly Regex SeleniumUaRegex = new(
    @"Selenium|WebDriver", RegexOptions.IgnoreCase | RegexOptions.Compiled);

private static readonly Regex PlaywrightUaRegex = new(
    @"Playwright", RegexOptions.IgnoreCase | RegexOptions.Compiled);

private static readonly Regex PuppeteerUaRegex = new(
    @"Puppeteer", RegexOptions.IgnoreCase | RegexOptions.Compiled);
```

- [ ] **Step 3: Update `InferBotIdentity` to use the compiled fields**

In the UA-based section of `InferBotIdentity`, replace all `Regex.IsMatch(ua, @"...", RegexOptions.IgnoreCase)` calls with the compiled field matches:

```csharp
// 2. UA-based inference
if (!string.IsNullOrEmpty(userAgent))
{
    var ua = userAgent;
    if (AiBotUaRegex.IsMatch(ua))
        return (ExtractUaBotName(ua) ?? "AI Crawler", "AiBot");
    if (SearchBotUaRegex.IsMatch(ua))
        return (ExtractUaBotName(ua) ?? "Search Bot", "SearchEngine");
    if (SeoBotUaRegex.IsMatch(ua))
        return (ExtractUaBotName(ua) ?? "SEO Crawler", "Scraper");
    if (MonitorBotUaRegex.IsMatch(ua))
        return (ExtractUaBotName(ua) ?? "Monitor", "MonitoringBot");
    if (PythonBotUaRegex.IsMatch(ua))
        return ("Python Bot", "Scraper");
    if (CurlUaRegex.IsMatch(ua))
        return ("curl", "Scraper");
    if (WgetUaRegex.IsMatch(ua))
        return ("wget", "Scraper");
    if (GoBotUaRegex.IsMatch(ua))
        return ("Go Bot", "Scraper");
    if (JavaBotUaRegex.IsMatch(ua))
        return ("Java Bot", "Scraper");
    if (NodeBotUaRegex.IsMatch(ua))
        return ("Node.js Bot", "Scraper");
    if (RubyBotUaRegex.IsMatch(ua))
        return ("Ruby Bot", "Scraper");
    if (PhpBotUaRegex.IsMatch(ua))
        return ("PHP Bot", "Scraper");
    if (PerlBotUaRegex.IsMatch(ua))
        return ("Perl Bot", "Scraper");
    if (CrawlerUaRegex.IsMatch(ua))
        return ("Web Crawler", "Scraper");
    if (HeadlessUaRegex.IsMatch(ua))
        return ("Headless Chrome", "Scraper");
    if (PhantomUaRegex.IsMatch(ua))
        return ("PhantomJS", "Scraper");
    if (SeleniumUaRegex.IsMatch(ua))
        return ("Selenium Bot", "Scraper");
    if (PlaywrightUaRegex.IsMatch(ua))
        return ("Playwright Bot", "Scraper");
    if (PuppeteerUaRegex.IsMatch(ua))
        return ("Puppeteer Bot", "Scraper");
}
```

- [ ] **Step 4: Remove the `using System.Text.RegularExpressions` import alias if it's no longer needed for `Regex.IsMatch`** (the static class calls are now gone; but the type is still used for the field declarations, so keep the using).

- [ ] **Step 5: Build to verify**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Services/VisitorListCache.cs
git commit -m "fix(ui): batch EvictOldest in VisitorListCache; replace hot-path Regex.IsMatch with compiled fields"
```

---

## Task 9: Replace `SmtpClient` with MailKit

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj`
- Modify: `src/Mostlylucid.BotDetection.UI/Services/Auth/StyloBotSmtpEmailSender.cs`

`System.Net.Mail.SmtpClient` is deprecated in .NET 6+ and has broken async semantics. Replace with `MailKit.Net.Smtp`.

- [ ] **Step 1: Add MailKit package reference**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.UI
dotnet add package MailKit
```

- [ ] **Step 2: Rewrite `StyloBotSmtpEmailSender.cs`**

```csharp
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Mostlylucid.BotDetection.UI.Models.Auth;

namespace Mostlylucid.BotDetection.UI.Services.Auth;

public sealed class StyloBotSmtpEmailSender : IEmailSender<StyloBotUser>
{
    private readonly StyloBotSmtpOptions _smtp;
    private readonly ILogger<StyloBotSmtpEmailSender> _logger;

    public StyloBotSmtpEmailSender(
        IOptions<StyloBotSmtpOptions> options,
        ILogger<StyloBotSmtpEmailSender> logger)
    {
        _smtp = options.Value;
        _logger = logger;
    }

    public Task SendConfirmationLinkAsync(StyloBotUser user, string email, string confirmationLink) =>
        SendAsync(email, "Confirm your StyloBot Dashboard account",
            $"<p>Please confirm your account by <a href='{HtmlEncode(confirmationLink)}'>clicking here</a>.</p>" +
            $"<p>Or copy this link: {HtmlEncode(confirmationLink)}</p>");

    public Task SendPasswordResetLinkAsync(StyloBotUser user, string email, string resetLink) =>
        SendAsync(email, "Reset your StyloBot Dashboard password",
            $"<p>Reset your password by <a href='{HtmlEncode(resetLink)}'>clicking here</a>.</p>" +
            $"<p>This link expires in 24 hours.</p>");

    public Task SendPasswordResetCodeAsync(StyloBotUser user, string email, string resetCode) =>
        SendAsync(email, "Your StyloBot Dashboard verification code",
            $"<p>Your verification code is: <strong style='font-size:1.4em;letter-spacing:2px'>{HtmlEncode(resetCode)}</strong></p>" +
            $"<p>This code expires in 15 minutes.</p>");

    private async Task SendAsync(string to, string subject, string htmlBody)
    {
        if (string.IsNullOrEmpty(_smtp.Host))
        {
            _logger.LogWarning(
                "SMTP not configured - email to {To} dropped. Set {Section}:Host in appsettings.json.",
                to, StyloBotSmtpOptions.Section);
            return;
        }

        var fromAddress = _smtp.FromAddress ?? $"noreply@{_smtp.Host}";

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_smtp.FromName, fromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = htmlBody };

        using var client = new SmtpClient();
        await client.ConnectAsync(
            _smtp.Host,
            _smtp.Port,
            _smtp.EnableSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None);

        if (_smtp.Username != null)
            await client.AuthenticateAsync(_smtp.Username, _smtp.Password);

        await client.SendAsync(message);
        await client.DisconnectAsync(true);

        _logger.LogInformation("Email sent to {To}: {Subject}", to, subject);
    }

    private static string HtmlEncode(string value) =>
        System.Net.WebUtility.HtmlEncode(value);
}
```

- [ ] **Step 3: Build to verify**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

- [ ] **Step 4: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj \
        src/Mostlylucid.BotDetection.UI/Services/Auth/StyloBotSmtpEmailSender.cs
git commit -m "fix(ui): replace deprecated SmtpClient with MailKit for async-correct email sending"
```

---

## Task 10: Minor fixes -`SbSummaryStatsViewComponent`, `SbSessionsListViewComponent`, `DashboardModels.cs`

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/ViewComponents/Dashboard/SbSummaryStatsViewComponent.cs`
- Modify: `src/Mostlylucid.BotDetection.UI/ViewComponents/Dashboard/SbSessionsListViewComponent.cs`
- Modify: `src/Mostlylucid.BotDetection.UI/Models/DashboardModels.cs`

- [ ] **Step 1: Fix `SbSummaryStatsViewComponent` -replace `int.MaxValue` with explicit intent**

The summary stats needs all visitors to compute totals. Replace `int.MaxValue` with a named approach that makes the coupling to the cache bound explicit:

```csharp
// Replace:
var (allVisitors, totalCount, _, _) = visitorCache.GetFiltered("all", "lastSeen", "desc", 1, int.MaxValue);

// With:
// Fetch all cached visitors (cache is bounded to VisitorListCache._maxVisitors, default 100).
// Using GetFiltered with a very large pageSize returns the full cache content in one call.
const int maxCachedVisitors = 1_000; // must be >= VisitorListCache._maxVisitors
var (allVisitors, totalCount, _, _) = visitorCache.GetFiltered("all", "lastSeen", "desc", 1, maxCachedVisitors);
```

- [ ] **Step 2: Fix `SbSessionsListViewComponent` -bound fetch to needed count**

```csharp
// Replace:
var allSessions = await sessionStore.GetRecentSessionsAsync(200, isBot);

// With:
// Fetch only as many sessions as needed for the current page plus a small buffer.
// The store has no server-side pagination so we over-fetch conservatively.
var fetchCount = Math.Min((page * pageSize) + pageSize, 200);
var allSessions = await sessionStore.GetRecentSessionsAsync(fetchCount, isBot);
```

- [ ] **Step 3: Document `IpAddress` field in `DashboardModels.cs`**

```csharp
// Replace:
public string? IpAddress { get; init; }

// With:
/// <summary>Always null. Raw IPs are never persisted (zero-PII design). Present for interface compatibility only.</summary>
public string? IpAddress { get; init; }
```

- [ ] **Step 4: Build to verify**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/ViewComponents/Dashboard/SbSummaryStatsViewComponent.cs \
        src/Mostlylucid.BotDetection.UI/ViewComponents/Dashboard/SbSessionsListViewComponent.cs \
        src/Mostlylucid.BotDetection.UI/Models/DashboardModels.cs
git commit -m "fix(ui): minor fixes: int.MaxValue constant, session fetch bound, IpAddress doc"
```

---

## Task 11: Final build and verification

- [ ] **Step 1: Full solution build**

```bash
dotnet build /Users/scottgalloway/RiderProjects/stylobot/mostlylucid.stylobot.sln
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Run tests**

```bash
dotnet test /Users/scottgalloway/RiderProjects/stylobot/mostlylucid.stylobot.sln
```
Expected: All tests pass.

- [ ] **Step 3: Final commit if anything was missed**

Review `git status` -if any modified files remain uncommitted, commit them:

```bash
git status
# If clean: done.
```