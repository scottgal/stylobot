# Startup Setup Command Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make app startup non-blocking by fixing `DataHubGeoLocationService`, and add a `stylobot setup` console command that checks and downloads missing resources (bot lists, ONNX model, GeoIP CSV).

**Architecture:** `ISetupResource` interface in `Mostlylucid.BotDetection/Setup/` with three implementations (BotList, ONNX, GeoIP). `SetupService` collects all registered `ISetupResource` instances via DI and exposes `CheckAllAsync`/`DownloadMissingAsync`. The Console `setup` command builds a minimal service provider (no web host, no 49 detectors) and calls `SetupService`. The `DataHubGeoLocationService.StartAsync` fix is a two-line change making it fire-and-forget.

**Tech Stack:** .NET 10, xUnit, Moq, `IEnumerable<ISetupResource>` DI pattern, `IHttpClientFactory`, `IOptions<T>`

---

## File Map

**Create:**
- `src/Mostlylucid.BotDetection/Setup/ISetupResource.cs` -interface + `ResourceStatus` record + `ResourcePresence` enum
- `src/Mostlylucid.BotDetection/Setup/SetupService.cs` -collects ISetupResource, check all + download missing
- `src/Mostlylucid.BotDetection/Setup/BotListSetupResource.cs` -checks/downloads bot lists via IBotListDatabase
- `src/Mostlylucid.BotDetection/Setup/OnnxSetupResource.cs` -checks/downloads ONNX model + vocab files
- `src/Mostlylucid.GeoDetection.Contributor/Setup/GeoIpSetupResource.cs` -checks/downloads DataHub GeoIP CSV
- `src/Mostlylucid.BotDetection.Console/SetupCommand.cs` -Console `setup` command implementation
- `src/Mostlylucid.BotDetection.Test/Setup/SetupServiceTests.cs` -unit tests for SetupService
- `src/Mostlylucid.BotDetection.Test/Setup/BotListSetupResourceTests.cs`
- `src/Mostlylucid.BotDetection.Test/Setup/OnnxSetupResourceTests.cs`
- `src/Mostlylucid.GeoDetection.Test/Setup/GeoIpSetupResourceTests.cs`

**Modify:**
- `src/Mostlylucid.GeoDetection/Services/DataHubGeoLocationService.cs:103-107` -make StartAsync fire-and-forget
- `src/Mostlylucid.GeoDetection.Test/Services/DataHubGeoLocationServiceTests.cs` -add StartAsync test
- `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` -add `AddBotDetectionSetupServices()` and register setup resources in `AddBotDetection()`
- `src/Mostlylucid.GeoDetection.Contributor/Extensions/ServiceCollectionExtensions.cs` -register `GeoIpSetupResource` when provider is DataHubCsv
- `src/Mostlylucid.BotDetection.Console/Program.cs:38-60` -add `case "setup":` + update help text

---

## Task 1: Fix DataHubGeoLocationService Startup Blocking

**Files:**
- Modify: `src/Mostlylucid.GeoDetection/Services/DataHubGeoLocationService.cs:103-107`
- Modify: `src/Mostlylucid.GeoDetection.Test/Services/DataHubGeoLocationServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `DataHubGeoLocationServiceTests.cs` (requires building a service with a mock HTTP factory that simulates a slow download):

```csharp
[Fact]
public async Task StartAsync_ReturnsImmediately_WithoutWaitingForDownload()
{
    // Arrange: HTTP client that never completes (simulates slow network)
    var tcs = new TaskCompletionSource<HttpResponseMessage>();
    var handlerMock = new Mock<HttpMessageHandler>();
    handlerMock
        .Protected()
        .Setup<Task<HttpResponseMessage>>(
            "SendAsync",
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>())
        .Returns(tcs.Task); // never completes

    var client = new HttpClient(handlerMock.Object);
    _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
    
    // Use a fresh temp dir so it tries to download
    var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tempDir);
    var opts = Options.Create(new GeoLite2Options
    {
        Provider = GeoProvider.DataHubCsv,
        DatabasePath = Path.Combine(tempDir, "GeoLite2-City.csv"),
        CacheDuration = TimeSpan.FromMinutes(5)
    });
    var svc = new DataHubGeoLocationService(_loggerMock.Object, opts, _httpClientFactoryMock.Object, _memoryCache);

    // Act: measure how long StartAsync takes
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await svc.StartAsync(CancellationToken.None);
    sw.Stop();

    // Assert: returned in < 200ms even though download never completes
    Assert.True(sw.ElapsedMilliseconds < 200,
        $"StartAsync blocked for {sw.ElapsedMilliseconds}ms - should return immediately");
    
    // Cleanup
    Directory.Delete(tempDir, recursive: true);
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test src/Mostlylucid.GeoDetection.Test --filter "StartAsync_ReturnsImmediately" -v n
```

Expected: FAIL -test currently hangs (or times out) because `StartAsync` awaits `EnsureDatabaseLoadedAsync`.

- [ ] **Step 3: Fix DataHubGeoLocationService.StartAsync**

In `src/Mostlylucid.GeoDetection/Services/DataHubGeoLocationService.cs`, replace lines 103–107:

```csharp
public Task StartAsync(CancellationToken cancellationToken)
{
    // Fire-and-forget: database loads in the background.
    // GetLocationAsync calls EnsureDatabaseLoadedAsync before serving any request,
    // so all lookups block until the DB is ready -but Kestrel startup is not blocked.
    _ = EnsureDatabaseLoadedAsync(CancellationToken.None);
    return Task.CompletedTask;
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test src/Mostlylucid.GeoDetection.Test --filter "StartAsync_ReturnsImmediately" -v n
```

Expected: PASS in < 1s

- [ ] **Step 5: Build and run all GeoDetection tests**

```bash
dotnet test src/Mostlylucid.GeoDetection.Test -v n
```

Expected: All tests pass (green).

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.GeoDetection/Services/DataHubGeoLocationService.cs
git add src/Mostlylucid.GeoDetection.Test/Services/DataHubGeoLocationServiceTests.cs
git commit -m "fix(geo): make DataHubGeoLocationService.StartAsync non-blocking"
```

---

## Task 2: ISetupResource Interface + ResourceStatus Record

**Files:**
- Create: `src/Mostlylucid.BotDetection/Setup/ISetupResource.cs`
- Create: `src/Mostlylucid.BotDetection.Test/Setup/SetupResourceTests.cs` (basic type-level tests)

- [ ] **Step 1: Write the test**

Create `src/Mostlylucid.BotDetection.Test/Setup/SetupResourceTests.cs`:

```csharp
using Mostlylucid.BotDetection.Setup;

namespace Mostlylucid.BotDetection.Test.Setup;

public class SetupResourceTests
{
    [Fact]
    public void ResourceStatus_Fresh_HasCorrectPresence()
    {
        var status = new ResourceStatus("Bot Lists", "desc", ResourcePresence.Fresh, "/tmp/db", "ok");

        Assert.Equal(ResourcePresence.Fresh, status.Presence);
        Assert.Equal("Bot Lists", status.Name);
    }

    [Fact]
    public void ResourceStatus_Missing_HasCorrectPresence()
    {
        var status = new ResourceStatus("ONNX", "desc", ResourcePresence.Missing, "/tmp/models");

        Assert.Equal(ResourcePresence.Missing, status.Presence);
        Assert.Null(status.Detail);
    }

    [Fact]
    public void ResourceStatus_Stale_HasCorrectPresence()
    {
        var status = new ResourceStatus("GeoIP", "desc", ResourcePresence.Stale, "/tmp/geo", "10 days old");

        Assert.Equal(ResourcePresence.Stale, status.Presence);
        Assert.Equal("10 days old", status.Detail);
    }
}
```

- [ ] **Step 2: Run test to verify it fails (type not found)**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "SetupResourceTests" -v n
```

Expected: FAIL -compile error: `ResourceStatus`, `ResourcePresence` not found.

- [ ] **Step 3: Create the interface file**

Create `src/Mostlylucid.BotDetection/Setup/ISetupResource.cs`:

```csharp
namespace Mostlylucid.BotDetection.Setup;

public enum ResourcePresence { Fresh, Stale, Missing }

public record ResourceStatus(
    string Name,
    string Description,
    ResourcePresence Presence,
    string? Path,
    string? Detail = null);

public interface ISetupResource
{
    string Name { get; }
    string Description { get; }
    Task<ResourceStatus> CheckAsync(CancellationToken ct = default);
    Task DownloadAsync(IProgress<string>? progress, CancellationToken ct = default);
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "SetupResourceTests" -v n
```

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Setup/ISetupResource.cs
git add src/Mostlylucid.BotDetection.Test/Setup/SetupResourceTests.cs
git commit -m "feat(setup): add ISetupResource interface and ResourceStatus record"
```

---

## Task 3: SetupService

**Files:**
- Create: `src/Mostlylucid.BotDetection/Setup/SetupService.cs`
- Create: `src/Mostlylucid.BotDetection.Test/Setup/SetupServiceTests.cs`

- [ ] **Step 1: Write the tests**

Create `src/Mostlylucid.BotDetection.Test/Setup/SetupServiceTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Setup;

namespace Mostlylucid.BotDetection.Test.Setup;

public class SetupServiceTests
{
    private static Mock<ISetupResource> MakeResource(string name, ResourcePresence presence)
    {
        var mock = new Mock<ISetupResource>();
        mock.Setup(r => r.Name).Returns(name);
        mock.Setup(r => r.Description).Returns("desc");
        mock.Setup(r => r.CheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceStatus(name, "desc", presence, null));
        mock.Setup(r => r.DownloadAsync(It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    [Fact]
    public async Task CheckAllAsync_ReturnsStatusFromAllResources()
    {
        var r1 = MakeResource("BotLists", ResourcePresence.Fresh);
        var r2 = MakeResource("Onnx", ResourcePresence.Missing);
        var sut = new SetupService([r1.Object, r2.Object]);

        var statuses = await sut.CheckAllAsync();

        Assert.Equal(2, statuses.Count);
        Assert.Contains(statuses, s => s.Name == "BotLists" && s.Presence == ResourcePresence.Fresh);
        Assert.Contains(statuses, s => s.Name == "Onnx" && s.Presence == ResourcePresence.Missing);
    }

    [Fact]
    public async Task DownloadMissingAsync_SkipsFreshResources()
    {
        var fresh = MakeResource("BotLists", ResourcePresence.Fresh);
        var missing = MakeResource("Onnx", ResourcePresence.Missing);
        var sut = new SetupService([fresh.Object, missing.Object]);

        await sut.DownloadMissingAsync(progress: null, force: false);

        fresh.Verify(r => r.DownloadAsync(It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
        missing.Verify(r => r.DownloadAsync(It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DownloadMissingAsync_WithForce_DownloadsAll()
    {
        var fresh = MakeResource("BotLists", ResourcePresence.Fresh);
        var missing = MakeResource("Onnx", ResourcePresence.Missing);
        var sut = new SetupService([fresh.Object, missing.Object]);

        await sut.DownloadMissingAsync(progress: null, force: true);

        fresh.Verify(r => r.DownloadAsync(It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
        missing.Verify(r => r.DownloadAsync(It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DownloadMissingAsync_AlsoDownloadsStaleResources()
    {
        var stale = MakeResource("GeoIP", ResourcePresence.Stale);
        var sut = new SetupService([stale.Object]);

        await sut.DownloadMissingAsync(progress: null, force: false);

        stale.Verify(r => r.DownloadAsync(It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAllAsync_EmptyResources_ReturnsEmpty()
    {
        var sut = new SetupService([]);

        var statuses = await sut.CheckAllAsync();

        Assert.Empty(statuses);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "SetupServiceTests" -v n
```

Expected: FAIL -`SetupService` not found.

- [ ] **Step 3: Implement SetupService**

Create `src/Mostlylucid.BotDetection/Setup/SetupService.cs`:

```csharp
namespace Mostlylucid.BotDetection.Setup;

public class SetupService(IEnumerable<ISetupResource> resources)
{
    private readonly IReadOnlyList<ISetupResource> _resources = resources.ToList();

    public async Task<IReadOnlyList<ResourceStatus>> CheckAllAsync(CancellationToken ct = default)
    {
        var tasks = _resources.Select(r => r.CheckAsync(ct));
        return await Task.WhenAll(tasks);
    }

    public async Task DownloadMissingAsync(IProgress<string>? progress, bool force, CancellationToken ct = default)
    {
        foreach (var resource in _resources)
        {
            var status = await resource.CheckAsync(ct);
            if (status.Presence == ResourcePresence.Fresh && !force)
                continue;

            progress?.Report($"Downloading {resource.Name}...");
            await resource.DownloadAsync(progress, ct);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "SetupServiceTests" -v n
```

Expected: All 5 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Setup/SetupService.cs
git add src/Mostlylucid.BotDetection.Test/Setup/SetupServiceTests.cs
git commit -m "feat(setup): add SetupService to orchestrate resource check and download"
```

---

## Task 4: BotListSetupResource

**Files:**
- Create: `src/Mostlylucid.BotDetection/Setup/BotListSetupResource.cs`
- Create: `src/Mostlylucid.BotDetection.Test/Setup/BotListSetupResourceTests.cs`

- [ ] **Step 1: Write the tests**

Create `src/Mostlylucid.BotDetection.Test/Setup/BotListSetupResourceTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Setup;

namespace Mostlylucid.BotDetection.Test.Setup;

public class BotListSetupResourceTests
{
    private static IOptions<BotDetectionOptions> DefaultOptions() =>
        Options.Create(new BotDetectionOptions { DatabasePath = "/tmp/test-botdetection.db" });

    [Fact]
    public async Task CheckAsync_WhenNeverUpdated_ReturnsMissing()
    {
        var db = new Mock<IBotListDatabase>();
        db.Setup(d => d.GetLastUpdateTimeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync((DateTime?)null);

        var sut = new BotListSetupResource(db.Object, DefaultOptions());
        var status = await sut.CheckAsync();

        Assert.Equal(ResourcePresence.Missing, status.Presence);
    }

    [Fact]
    public async Task CheckAsync_WhenRecentlyUpdated_ReturnsFresh()
    {
        var db = new Mock<IBotListDatabase>();
        db.Setup(d => d.GetLastUpdateTimeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(DateTime.UtcNow.AddHours(-2));

        var sut = new BotListSetupResource(db.Object, DefaultOptions());
        var status = await sut.CheckAsync();

        Assert.Equal(ResourcePresence.Fresh, status.Presence);
    }

    [Fact]
    public async Task CheckAsync_WhenUpdatedMoreThanOneDayAgo_ReturnsStale()
    {
        var db = new Mock<IBotListDatabase>();
        db.Setup(d => d.GetLastUpdateTimeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(DateTime.UtcNow.AddDays(-2));

        var sut = new BotListSetupResource(db.Object, DefaultOptions());
        var status = await sut.CheckAsync();

        Assert.Equal(ResourcePresence.Stale, status.Presence);
    }

    [Fact]
    public async Task DownloadAsync_CallsInitializeAndUpdate()
    {
        var db = new Mock<IBotListDatabase>();
        db.Setup(d => d.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        db.Setup(d => d.UpdateListsAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var sut = new BotListSetupResource(db.Object, DefaultOptions());
        await sut.DownloadAsync(null);

        db.Verify(d => d.InitializeAsync(It.IsAny<CancellationToken>()), Times.Once);
        db.Verify(d => d.UpdateListsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "BotListSetupResourceTests" -v n
```

Expected: FAIL -`BotListSetupResource` not found.

- [ ] **Step 3: Implement BotListSetupResource**

Create `src/Mostlylucid.BotDetection/Setup/BotListSetupResource.cs`:

```csharp
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Setup;

public class BotListSetupResource : ISetupResource
{
    private readonly IBotListDatabase _database;
    private readonly string _dbPath;

    public BotListSetupResource(IBotListDatabase database, IOptions<BotDetectionOptions> options)
    {
        _database = database;
        _dbPath = options.Value.DatabasePath ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db");
    }

    public string Name => "Bot Lists";
    public string Description => "Bot pattern lists and datacenter IP ranges (SQLite)";

    public async Task<ResourceStatus> CheckAsync(CancellationToken ct = default)
    {
        var lastUpdate = await _database.GetLastUpdateTimeAsync("bot_patterns", ct);

        if (!lastUpdate.HasValue)
            return new ResourceStatus(Name, Description, ResourcePresence.Missing, _dbPath, "Never downloaded");

        var age = DateTime.UtcNow - lastUpdate.Value;
        if (age.TotalDays > 1)
            return new ResourceStatus(Name, Description, ResourcePresence.Stale, _dbPath,
                $"Updated {(int)age.TotalDays}d ago -daily update recommended");

        return new ResourceStatus(Name, Description, ResourcePresence.Fresh, _dbPath,
            $"Updated {lastUpdate.Value:yyyy-MM-dd HH:mm} UTC");
    }

    public async Task DownloadAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        await _database.InitializeAsync(ct);
        await _database.UpdateListsAsync(ct);
        progress?.Report("Bot lists updated.");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "BotListSetupResourceTests" -v n
```

Expected: All 4 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Setup/BotListSetupResource.cs
git add src/Mostlylucid.BotDetection.Test/Setup/BotListSetupResourceTests.cs
git commit -m "feat(setup): add BotListSetupResource"
```

---

## Task 5: OnnxSetupResource

**Files:**
- Create: `src/Mostlylucid.BotDetection/Setup/OnnxSetupResource.cs`
- Create: `src/Mostlylucid.BotDetection.Test/Setup/OnnxSetupResourceTests.cs`

- [ ] **Step 1: Write the tests**

Create `src/Mostlylucid.BotDetection.Test/Setup/OnnxSetupResourceTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Setup;

namespace Mostlylucid.BotDetection.Test.Setup;

public class OnnxSetupResourceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public OnnxSetupResourceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private IOptions<BotDetectionOptions> Opts(bool autoDownload = true) =>
        Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Qdrant = { AutoDownloadEmbeddingModel = autoDownload }
        });

    private string ModelsDir => Path.Combine(_tempDir, "models");
    private string ModelPath => Path.Combine(ModelsDir, "all-MiniLM-L6-v2.onnx");
    private string VocabPath => Path.Combine(ModelsDir, "vocab.txt");

    [Fact]
    public async Task CheckAsync_BothFilesMissing_ReturnsMissing()
    {
        var sut = new OnnxSetupResource(Opts());

        var status = await sut.CheckAsync();

        Assert.Equal(ResourcePresence.Missing, status.Presence);
    }

    [Fact]
    public async Task CheckAsync_BothFilesPresent_ReturnsFresh()
    {
        Directory.CreateDirectory(ModelsDir);
        await File.WriteAllTextAsync(ModelPath, "fake model");
        await File.WriteAllTextAsync(VocabPath, "fake vocab");

        var sut = new OnnxSetupResource(Opts());
        var status = await sut.CheckAsync();

        Assert.Equal(ResourcePresence.Fresh, status.Presence);
    }

    [Fact]
    public async Task CheckAsync_OnlyModelPresent_ReturnsMissing()
    {
        Directory.CreateDirectory(ModelsDir);
        await File.WriteAllTextAsync(ModelPath, "fake model");

        var sut = new OnnxSetupResource(Opts());
        var status = await sut.CheckAsync();

        Assert.Equal(ResourcePresence.Missing, status.Presence);
    }

    [Fact]
    public async Task CheckAsync_OnlyVocabPresent_ReturnsMissing()
    {
        Directory.CreateDirectory(ModelsDir);
        await File.WriteAllTextAsync(VocabPath, "fake vocab");

        var sut = new OnnxSetupResource(Opts());
        var status = await sut.CheckAsync();

        Assert.Equal(ResourcePresence.Missing, status.Presence);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "OnnxSetupResourceTests" -v n
```

Expected: FAIL -`OnnxSetupResource` not found.

- [ ] **Step 3: Implement OnnxSetupResource**

Create `src/Mostlylucid.BotDetection/Setup/OnnxSetupResource.cs`:

```csharp
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Setup;

public class OnnxSetupResource : ISetupResource
{
    private const string ModelUrl =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx";
    private const string VocabUrl =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt";

    private readonly string _modelPath;
    private readonly string _vocabPath;
    private readonly string _modelsDir;

    public OnnxSetupResource(IOptions<BotDetectionOptions> options)
    {
        var opts = options.Value;
        var baseDir = opts.DatabasePath != null
            ? Path.GetDirectoryName(opts.DatabasePath) ?? AppContext.BaseDirectory
            : AppContext.BaseDirectory;
        _modelsDir = Path.Combine(baseDir, "models");
        _modelPath = Path.Combine(_modelsDir, opts.Qdrant.EmbeddingModel);
        _vocabPath = Path.Combine(_modelsDir, "vocab.txt");
    }

    public string Name => "ONNX Embedding Model";
    public string Description => "all-MiniLM-L6-v2 semantic similarity model (~90MB)";

    public Task<ResourceStatus> CheckAsync(CancellationToken ct = default)
    {
        var modelExists = File.Exists(_modelPath);
        var vocabExists = File.Exists(_vocabPath);

        if (modelExists && vocabExists)
            return Task.FromResult(new ResourceStatus(Name, Description, ResourcePresence.Fresh, _modelsDir,
                "Model and vocab present"));

        var detail = $"{(modelExists ? "model" : "model missing")}, {(vocabExists ? "vocab" : "vocab missing")}";
        return Task.FromResult(new ResourceStatus(Name, Description, ResourcePresence.Missing, _modelsDir, detail));
    }

    public async Task DownloadAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_modelsDir);

        if (!File.Exists(_modelPath))
        {
            progress?.Report("Downloading ONNX model (~90MB) from HuggingFace...");
            await DownloadFileAsync(ModelUrl, _modelPath, ct);
            progress?.Report($"  Model saved to {_modelPath}");
        }

        if (!File.Exists(_vocabPath))
        {
            progress?.Report("Downloading vocab.txt...");
            await DownloadFileAsync(VocabUrl, _vocabPath, ct);
        }
    }

    private static async Task DownloadFileAsync(string url, string dest, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
        await stream.CopyToAsync(file, ct);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "OnnxSetupResourceTests" -v n
```

Expected: All 4 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Setup/OnnxSetupResource.cs
git add src/Mostlylucid.BotDetection.Test/Setup/OnnxSetupResourceTests.cs
git commit -m "feat(setup): add OnnxSetupResource"
```

---

## Task 6: GeoIpSetupResource

**Files:**
- Create: `src/Mostlylucid.GeoDetection.Contributor/Setup/GeoIpSetupResource.cs`
- Create: `src/Mostlylucid.GeoDetection.Test/Setup/GeoIpSetupResourceTests.cs`

`GeoIpSetupResource` lives in `Mostlylucid.GeoDetection.Contributor` because it needs both `GeoLite2Options` (from `Mostlylucid.GeoDetection`) and `ISetupResource` (from `Mostlylucid.BotDetection`), and that project already references both.

The test lives in `Mostlylucid.GeoDetection.Test`. That project references `Mostlylucid.GeoDetection` but NOT `Mostlylucid.GeoDetection.Contributor` or `Mostlylucid.BotDetection`. Add the references now:

- [ ] **Step 1: Add project references to GeoDetection.Test**

Edit `src/Mostlylucid.GeoDetection.Test/Mostlylucid.GeoDetection.Test.csproj` and add:

```xml
<ItemGroup>
  <ProjectReference Include="..\Mostlylucid.BotDetection\Mostlylucid.BotDetection.csproj" />
  <ProjectReference Include="..\Mostlylucid.GeoDetection.Contributor\Mostlylucid.GeoDetection.Contributor.csproj" />
</ItemGroup>
```

Also add Moq since it likely isn't in the GeoDetection test project:

```xml
<ItemGroup>
  <PackageReference Include="Moq" Version="4.20.72" />
</ItemGroup>
```

Verify the test project builds:

```bash
dotnet build src/Mostlylucid.GeoDetection.Test -v q
```

- [ ] **Step 2: Write the tests**

Create `src/Mostlylucid.GeoDetection.Test/Setup/GeoIpSetupResourceTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Setup;
using Mostlylucid.GeoDetection.Contributor.Setup;
using Mostlylucid.GeoDetection.Models;

namespace Mostlylucid.GeoDetection.Test.Setup;

public class GeoIpSetupResourceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock = new();

    public GeoIpSetupResourceTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string CsvPath => Path.Combine(_tempDir, "data", "geoip2-ipv4.csv");

    private IOptions<GeoLite2Options> Opts() => Options.Create(new GeoLite2Options
    {
        Provider = GeoProvider.DataHubCsv,
        DatabasePath = Path.Combine(_tempDir, "data", "GeoLite2-City.csv")
    });

    [Fact]
    public async Task CheckAsync_WhenCsvMissing_ReturnsMissing()
    {
        var sut = new GeoIpSetupResource(Opts(), _httpClientFactoryMock.Object);

        var status = await sut.CheckAsync();

        Assert.Equal(ResourcePresence.Missing, status.Presence);
    }

    [Fact]
    public async Task CheckAsync_WhenCsvFresh_ReturnsFresh()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CsvPath)!);
        await File.WriteAllTextAsync(CsvPath, "fake csv data");
        File.SetLastWriteTimeUtc(CsvPath, DateTime.UtcNow.AddHours(-1));

        var sut = new GeoIpSetupResource(Opts(), _httpClientFactoryMock.Object);
        var status = await sut.CheckAsync();

        Assert.Equal(ResourcePresence.Fresh, status.Presence);
    }

    [Fact]
    public async Task CheckAsync_WhenCsvOlderThan7Days_ReturnsStale()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CsvPath)!);
        await File.WriteAllTextAsync(CsvPath, "fake csv data");
        File.SetLastWriteTimeUtc(CsvPath, DateTime.UtcNow.AddDays(-10));

        var sut = new GeoIpSetupResource(Opts(), _httpClientFactoryMock.Object);
        var status = await sut.CheckAsync();

        Assert.Equal(ResourcePresence.Stale, status.Presence);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
dotnet test src/Mostlylucid.GeoDetection.Test --filter "GeoIpSetupResourceTests" -v n
```

Expected: FAIL -`GeoIpSetupResource` not found.

- [ ] **Step 4: Implement GeoIpSetupResource**

Create `src/Mostlylucid.GeoDetection.Contributor/Setup/GeoIpSetupResource.cs`:

```csharp
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Setup;
using Mostlylucid.GeoDetection.Models;

namespace Mostlylucid.GeoDetection.Contributor.Setup;

public class GeoIpSetupResource : ISetupResource
{
    private const string CsvUrl = "https://datahub.io/core/geoip2-ipv4/r/geoip2-ipv4.csv";
    private readonly string _csvPath;
    private readonly IHttpClientFactory _httpClientFactory;

    public GeoIpSetupResource(IOptions<GeoLite2Options> options, IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;

        var path = options.Value.DatabasePath;
        if (path.EndsWith(".mmdb", StringComparison.OrdinalIgnoreCase))
            path = Path.ChangeExtension(path, ".csv");
        if (!path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            path = Path.Combine("data", "geoip2-ipv4.csv");
        if (!Path.IsPathRooted(path))
            path = Path.Combine(AppContext.BaseDirectory, path);

        _csvPath = path;
    }

    public string Name => "GeoIP CSV Database";
    public string Description => "DataHub GeoIP2-IPv4 country database (~27MB), updated weekly";

    public Task<ResourceStatus> CheckAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_csvPath))
            return Task.FromResult(new ResourceStatus(Name, Description, ResourcePresence.Missing, _csvPath,
                "File not found"));

        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_csvPath);
        if (age.TotalDays > 7)
            return Task.FromResult(new ResourceStatus(Name, Description, ResourcePresence.Stale, _csvPath,
                $"Updated {(int)age.TotalDays} days ago -weekly update recommended"));

        return Task.FromResult(new ResourceStatus(Name, Description, ResourcePresence.Fresh, _csvPath,
            $"Updated {(int)age.TotalHours}h ago"));
    }

    public async Task DownloadAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        progress?.Report("Downloading GeoIP CSV database (~27MB)...");

        var dir = Path.GetDirectoryName(_csvPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var client = _httpClientFactory.CreateClient("DataHub");
        using var response = await client.GetAsync(CsvUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(_csvPath);
        await content.CopyToAsync(file, ct);

        progress?.Report($"GeoIP database saved to {_csvPath}");
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test src/Mostlylucid.GeoDetection.Test --filter "GeoIpSetupResourceTests" -v n
```

Expected: All 3 tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.GeoDetection.Contributor/Setup/GeoIpSetupResource.cs
git add src/Mostlylucid.GeoDetection.Test/Setup/GeoIpSetupResourceTests.cs
git add src/Mostlylucid.GeoDetection.Test/Mostlylucid.GeoDetection.Test.csproj
git commit -m "feat(setup): add GeoIpSetupResource in GeoDetection.Contributor"
```

---

## Task 7: DI Registration

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs`
- Modify: `src/Mostlylucid.GeoDetection.Contributor/Extensions/ServiceCollectionExtensions.cs`

This task wires the setup resources and `SetupService` into DI so callers can resolve them.

### 7a: Add `AddBotDetectionSetupServices()` and register in `AddBotDetection()`

`AddBotDetectionSetupServices()` is a minimal registration (no full detector stack) used by the Console `setup` command. `AddBotDetection()` also calls it so that apps with the full stack automatically have setup services available.

- [ ] **Step 1: Add setup registrations to `ServiceCollectionExtensions.cs`**

In `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs`:

1. Add `using Mostlylucid.BotDetection.Setup;` at the top (with other usings).

2. Add this new public extension method after `AddBotDetection()` (before the private helpers):

```csharp
/// <summary>
///     Registers the setup services needed for <c>stylobot setup</c>.
///     Called automatically by AddBotDetection(). Can also be called in isolation
///     by the Console setup command for a minimal host (no 49-detector stack).
/// </summary>
public static IServiceCollection AddBotDetectionSetupServices(this IServiceCollection services)
{
    services.TryAddSingleton<IBotListFetcher, BotListFetcher>();
    services.TryAddSingleton<IBotListDatabase>(sp =>
    {
        var options = sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
        var fetcher = sp.GetRequiredService<IBotListFetcher>();
        var logger = sp.GetRequiredService<ILogger<BotListDatabase>>();
        return new BotListDatabase(fetcher, logger, options.DatabasePath);
    });
    services.TryAddEnumerable(ServiceDescriptor.Singleton<ISetupResource, BotListSetupResource>());

    // OnnxSetupResource only useful when auto-download is enabled
    services.TryAddEnumerable(ServiceDescriptor.Singleton<ISetupResource, OnnxSetupResource>());

    services.TryAddSingleton<SetupService>();
    return services;
}
```

3. Call `AddBotDetectionSetupServices()` from inside `RegisterCoreServices()`. Find where `IBotListFetcher` and `IBotListDatabase` are already registered (around line 285) and replace those two `TryAddSingleton` calls with a single `AddBotDetectionSetupServices()` call:

Before (lines ~284-292):
```csharp
// Register bot list fetcher and database
services.TryAddSingleton<IBotListFetcher, BotListFetcher>();
services.TryAddSingleton<IBotListDatabase>(sp =>
{
    var options = sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
    var fetcher = sp.GetRequiredService<IBotListFetcher>();
    var logger = sp.GetRequiredService<ILogger<BotListDatabase>>();
    return new BotListDatabase(fetcher, logger, options.DatabasePath);
});
```

After:
```csharp
// Register bot list fetcher, database, and setup services
services.AddBotDetectionSetupServices();
```

- [ ] **Step 2: Build to verify no errors**

```bash
dotnet build src/Mostlylucid.BotDetection -v q
```

Expected: Succeeded, 0 errors.

### 7b: Register GeoIpSetupResource in GeoDetection.Contributor

- [ ] **Step 3: Update GeoDetection.Contributor ServiceCollectionExtensions**

In `src/Mostlylucid.GeoDetection.Contributor/Extensions/ServiceCollectionExtensions.cs`:

1. Add usings:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Setup;
using Mostlylucid.GeoDetection.Contributor.Setup;
using Mostlylucid.GeoDetection.Models;
```

2. At the end of `AddGeoDetectionContributor(Action<GeoContributorOptions>?)`, before `return services;`, add:

```csharp
// Register GeoIP setup resource so `stylobot setup` can check/download the CSV
services.TryAddEnumerable(ServiceDescriptor.Singleton<ISetupResource, GeoIpSetupResource>());
```

3. Do the same in the second overload of `AddGeoDetectionContributor(GeoContributorOptions)`.

The final `AddGeoDetectionContributor(Action<GeoContributorOptions>? configureOptions)` method looks like:

```csharp
public static IServiceCollection AddGeoDetectionContributor(
    this IServiceCollection services,
    Action<GeoContributorOptions>? configureOptions = null)
{
    if (configureOptions != null)
        services.Configure(configureOptions);
    else
        services.AddOptions<GeoContributorOptions>().BindConfiguration("BotDetection:Geo");

    services.TryAddEnumerable(ServiceDescriptor.Singleton<IContributingDetector, GeoContributor>());
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IContributingDetector, GeoClientContributor>());
    services.TryAddEnumerable(ServiceDescriptor.Singleton<ISetupResource, GeoIpSetupResource>());

    return services;
}
```

The second overload (`AddGeoDetectionContributor(GeoContributorOptions options)`) similarly adds the line before `return services;`:

```csharp
services.TryAddEnumerable(ServiceDescriptor.Singleton<ISetupResource, GeoIpSetupResource>());
```

- [ ] **Step 4: Build the entire solution to verify no errors**

```bash
dotnet build mostlylucid.stylobot.sln -v q
```

Expected: Succeeded, 0 errors, 0 warnings (setup-related).

- [ ] **Step 5: Run all tests to verify nothing regressed**

```bash
dotnet test mostlylucid.stylobot.sln -v n --no-build
```

Expected: All existing tests pass. New setup tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs
git add src/Mostlylucid.GeoDetection.Contributor/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat(setup): register setup services and resources in DI"
```

---

## Task 8: Console Setup Command

**Files:**
- Create: `src/Mostlylucid.BotDetection.Console/SetupCommand.cs`
- Modify: `src/Mostlylucid.BotDetection.Console/Program.cs`

The `stylobot setup` command builds a minimal `ServiceProvider` (no Kestrel, no YARP, no 49 detectors) using `AddBotDetectionSetupServices()`, then runs `SetupService`. Since the Console project does not reference `Mostlylucid.GeoDetection.Contributor`, the `GeoIpSetupResource` is not registered -correct, because the Console does not use geo detection.

- [ ] **Step 1: Create SetupCommand.cs**

Create `src/Mostlylucid.BotDetection.Console/SetupCommand.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Setup;
using SQLitePCL;

namespace Mostlylucid.BotDetection.Console;

public static class SetupCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var checkOnly = args.Contains("--check-only", StringComparer.OrdinalIgnoreCase);
        var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);

        Batteries.Init();

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .AddEnvironmentVariables("STYLOBOT_")
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddHttpClient();
        services.AddMemoryCache();
        services.Configure<BotDetectionOptions>(config.GetSection("BotDetection"));
        services.AddOptions<BotDetectionOptions>().BindConfiguration("BotDetection");
        services.PostConfigure<BotDetectionOptions>(opts =>
        {
            opts.DatabasePath ??= Path.Combine(BotDetectionOptions.ResolveDataDirectory(), "botdetection.db");
        });

        services.AddBotDetectionSetupServices();

        await using var sp = services.BuildServiceProvider();
        var setup = sp.GetRequiredService<SetupService>();

        System.Console.WriteLine();
        System.Console.WriteLine("  stylobot setup -checking resources");
        System.Console.WriteLine();

        var statuses = await setup.CheckAllAsync();

        foreach (var status in statuses)
        {
            var icon = status.Presence switch
            {
                ResourcePresence.Fresh   => "  [ok]   ",
                ResourcePresence.Stale   => "  [stale]",
                ResourcePresence.Missing => "  [miss] ",
                _                        => "         "
            };
            System.Console.WriteLine($"  {icon}  {status.Name}");
            if (status.Detail != null)
                System.Console.WriteLine($"           {status.Detail}");
        }

        System.Console.WriteLine();

        if (checkOnly)
            return 0;

        var needsDownload = statuses.Where(s => s.Presence != ResourcePresence.Fresh || force).ToList();
        if (needsDownload.Count == 0)
        {
            System.Console.WriteLine("  All resources are up to date. Nothing to download.");
            System.Console.WriteLine("  Run with --force to re-download anyway.");
            return 0;
        }

        System.Console.WriteLine($"  Downloading {needsDownload.Count} resource(s)...");
        System.Console.WriteLine();

        var progress = new Progress<string>(msg => System.Console.WriteLine($"  {msg}"));

        try
        {
            await setup.DownloadMissingAsync(progress, force);
            System.Console.WriteLine();
            System.Console.WriteLine("  Setup complete. Run 'stylobot' to start.");
            return 0;
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"  Setup failed: {ex.Message}");
            return 1;
        }
    }
}
```

- [ ] **Step 2: Add `setup` case to Program.cs switch**

In `src/Mostlylucid.BotDetection.Console/Program.cs`, in the `switch (firstArg)` block (lines 38-60), add before the closing brace:

```csharp
case "setup":
    return await Mostlylucid.BotDetection.Console.SetupCommand.RunAsync(cmdArgs);
```

Also add the `setup` command to the help text. Find the `Console.WriteLine("  Commands:")` block and add:

```csharp
Console.WriteLine("    stylobot setup [--check-only] [--force]       Check and download missing resources");
```

- [ ] **Step 3: Build the Console project**

```bash
dotnet build src/Mostlylucid.BotDetection.Console -v q
```

Expected: Succeeded, 0 errors.

- [ ] **Step 4: Smoke test -check-only mode**

```bash
dotnet run --project src/Mostlylucid.BotDetection.Console -- setup --check-only
```

Expected output (exact values vary):

```
  stylobot setup -checking resources

    [miss]   Bot Lists
             Never downloaded
    [miss]   ONNX Embedding Model
             model missing, vocab missing
```

The command must exit with code 0 and print a resource table. If it crashes, investigate and fix before proceeding.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection.Console/SetupCommand.cs
git add src/Mostlylucid.BotDetection.Console/Program.cs
git commit -m "feat(console): add 'stylobot setup' command"
```

---

## Task 9: Verify End-to-End

- [ ] **Step 1: Build the full solution**

```bash
dotnet build mostlylucid.stylobot.sln -c Release -v q
```

Expected: Succeeded, 0 errors.

- [ ] **Step 2: Run all tests**

```bash
dotnet test mostlylucid.stylobot.sln --no-build -c Release -v n
```

Expected: All tests pass (including all 16 new Setup tests and updated DataHubGeoLocationServiceTests).

- [ ] **Step 3: Verify demo app starts non-blocking**

```bash
dotnet run --project src/Mostlylucid.BotDetection.Demo -c Release &
BGPID=$!
sleep 8
curl -s -o /dev/null -w "%{http_code}" http://localhost:5080/health
kill $BGPID 2>/dev/null
```

Expected: `404` or `200` returned within 8 seconds. The app must reach "Now listening on http://localhost:5080" before the GeoIP CSV finishes downloading.

- [ ] **Step 4: Verify setup --check-only works from Console**

```bash
dotnet run --project src/Mostlylucid.BotDetection.Console -c Release -- setup --check-only
```

Expected: Prints resource table and exits 0.

- [ ] **Step 5: Final commit and tag**

```bash
git commit --allow-empty -m "chore: verify startup setup command end-to-end"
```