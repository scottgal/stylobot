# Webhook Recognition + Policy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make StyloBot recognize inbound webhook traffic as a legitimate machine class and score it low-threat (no path allowlist), then never slow recognized traffic while a high site-wide safety ceiling still sheds absolute floods.

**Architecture:** A `WebhookSensor` detector atom (Wave 0) mirrors `RegistryClientSensor`: it recognizes the behavioral webhook shape (POST + JSON + a webhook signature/event header) corroborated by a named-provider catalog, a learned dominant/"commonest" source IP, and a receiver-attested verified (2xx) track record. Corroboration comes from a SQLite `WebhookEndpointReputation` store fed by a post-`_next` middleware recorder that observes the upstream response status. A `webhook-recognized` benign arm in `PostDetectionActionGate` suppresses shaping for recognized requests; a shared site-wide `SafetyCeilingRpm` token-bucket cap still applies everywhere.

**Tech Stack:** .NET 10, `DetectorAtomBase` (mostlylucid.ephemeral.atoms.taxonomy), YamlDotNet, SQLite (Microsoft.Data.Sqlite), `ITokenBucketStore`, xUnit + FluentAssertions + Moq.

## Global Constraints

- **No bypass.** Detection always runs; recognition changes SCORE/ACTION only; recognition is per-request; NO `/webhooks/* → allow` config, no skip-path. The load-bearing spoof/negative test is mandatory.
- **No in-memory persistence.** Webhook reputation persists to SQLite. `ConcurrentDictionary` only for per-request transient/perf caches.
- **No magic numbers.** Every threshold/weight/delta comes from `webhook.archetype.yaml` via `_configProvider.GetParameter(Name, ...)` / `GetDefaults(Name)`.
- **No hard-coded lists in C#.** Signature-header names + named providers live in `webhook.archetype.yaml` (embedded resource).
- **5-file atom checklist** (per CLAUDE.md "Adding a New Detector"): atom class, YAML manifest, `SignalKeys`, DI registration, narrative builder.
- **Reference implementations to mirror** (read these first): `src/Mostlylucid.BotDetection/Orchestration/Atoms/RegistryClientSensor.cs`, `.../Definitions/RegistryClients/{RegistryClientCatalog.cs,RegistryClientArchetype.cs,registry-client.archetype.yaml}`, `src/Mostlylucid.BotDetection.Test/Orchestration/Atoms/RegistryClientSensorTests.cs`, and `src/Mostlylucid.BotDetection/Enforcement/PostDetectionActionGate.cs` (the `IsVerifiedCrawlerMarketingFetch` / registry-client benign arms).

---

### Task 1: Webhook archetype seed + catalog loader

**Files:**
- Create: `src/Mostlylucid.BotDetection/Definitions/Webhooks/webhook.archetype.yaml`
- Create: `src/Mostlylucid.BotDetection/Definitions/Webhooks/WebhookCatalog.cs`
- Create: `src/Mostlylucid.BotDetection/Definitions/Webhooks/WebhookArchetype.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Definitions/Webhooks/WebhookCatalogTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `WebhookCatalog` with `static WebhookCatalog Default`, `IReadOnlyList<string> SignatureHeaders`, `IReadOnlyList<WebhookProvider> Providers` (record `WebhookProvider(string Name, string SignatureHeader, string[] IpRanges)`), and scoring knobs `double CorroboratedConfidenceDelta` (default -0.8), `double CorroboratedWeight` (default 2.5), `int DominanceMinCount` (default 20), `double DominanceMinShare` (default 0.6), `int VerifiedMin2xx` (default 10). Mirror `RegistryClientCatalog`/`RegistryClientArchetype` exactly (same YAML-load-from-embedded-resource mechanism, same `.csproj` `*.yaml` glob).

- [ ] **Step 1: Write the failing test**

```csharp
public sealed class WebhookCatalogTests
{
    [Fact]
    public void Default_loads_signature_headers_and_providers_from_yaml()
    {
        var c = WebhookCatalog.Default;
        c.SignatureHeaders.Should().Contain(h => h.Equals("Stripe-Signature", StringComparison.OrdinalIgnoreCase));
        c.SignatureHeaders.Should().Contain(h => h.Equals("X-Hub-Signature-256", StringComparison.OrdinalIgnoreCase));
        c.Providers.Should().Contain(p => p.Name == "Stripe" && p.SignatureHeader == "Stripe-Signature");
        c.CorroboratedConfidenceDelta.Should().BeLessThan(0);
        c.CorroboratedWeight.Should().BeGreaterThan(0);
        c.DominanceMinCount.Should().BeGreaterThan(0);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~WebhookCatalog"`
Expected: FAIL — `WebhookCatalog` does not exist.

- [ ] **Step 3: Write minimal implementation**

Copy `registry-client.archetype.yaml` structure. Create `webhook.archetype.yaml`:

```yaml
archetype_id: webhook
name: Webhook Receiver
description: >
  Inbound webhook deliveries (Stripe, GitHub, Shopify, Slack, ...). Recognized by the
  behavioral shape (POST + JSON + a webhook signature/event header) corroborated by a
  named provider, the learned dominant source IP, or a verified (2xx) track record.
  Detection always runs; a POST without corroboration is scored normally (spoof guard).
signature_headers:
  - Stripe-Signature
  - X-Hub-Signature-256
  - X-Hub-Signature
  - X-GitHub-Event
  - X-GitHub-Delivery
  - X-Shopify-Hmac-Sha256
  - X-Slack-Signature
  - X-Webhook-Signature
  - X-Event-Key
providers:
  - name: Stripe
    signature_header: Stripe-Signature
    ip_ranges: []
  - name: GitHub
    signature_header: X-Hub-Signature-256
    ip_ranges: []
  - name: Shopify
    signature_header: X-Shopify-Hmac-Sha256
    ip_ranges: []
  - name: Slack
    signature_header: X-Slack-Signature
    ip_ranges: []
scoring:
  corroborated_confidence_delta: -0.8
  corroborated_weight: 2.5
  dominance_min_count: 20
  dominance_min_share: 0.6
  verified_min_2xx: 10
```

Create `WebhookArchetype.cs` (the DTO the YAML deserializes into) and `WebhookCatalog.cs` (loads the embedded YAML, exposes `Default` + the typed accessors) — mirror `RegistryClientArchetype.cs` + `RegistryClientCatalog.cs` line-for-line, substituting the webhook fields. Add the yaml to the `.csproj` embedded-resource glob if not already covered by `**/*.yaml`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~WebhookCatalog"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Definitions/Webhooks/ src/Mostlylucid.BotDetection.Test/Definitions/Webhooks/
git commit -m "feat(webhook): seed archetype yaml + catalog loader"
```

---

### Task 2: `SignalKeys.Webhook*` + `WebhookSensor` behavioral recognition (shape + named provider)

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Models/DetectionContext.cs` (SignalKeys block)
- Create: `src/Mostlylucid.BotDetection/Orchestration/Atoms/WebhookSensor.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Orchestration/Atoms/WebhookSensorTests.cs`

**Interfaces:**
- Consumes: `WebhookCatalog` (Task 1); `IWebhookEndpointReputation` (Task 3) — inject as nullable, default null; when null, only shape + named-provider corroboration are available.
- Produces: `WebhookSensor : DetectorAtomBase` (`base(name: "Webhook", category: "Webhook")`, `Priority => 7`, `RequiredSignals => Array.Empty<string>()`); constructor `(ILogger<WebhookSensor>, IDetectorConfigProvider, IHttpContextAccessor, WebhookCatalog? catalog = null, IWebhookEndpointReputation? reputation = null)`; `DetectAsync` returns the negative-delta `DetectionContribution` (BotType.GoodBot, no early-exit) when recognized, else `None()`. SignalKeys: `Webhook.Detected = "webhook.detected"`, `WebhookShape = "webhook.shape"`, `WebhookProvider = "webhook.provider"`, `WebhookIpDominant = "webhook.ip_dominant"`, `WebhookVerifiedRecord = "webhook.verified_record"`, `WebhookEndpoint = "webhook.endpoint"`.

- [ ] **Step 1: Write the failing tests** (mirror `RegistryClientSensorTests`; `Ctx(method, path, headers)` helper + `StubDetectorConfigProvider` + `StaticHttpContextAccessor` from the AtomContract test helpers)

```csharp
public sealed class WebhookSensorTests
{
    private const string Session = "s1";
    private static WebhookSensor New(HttpContext ctx, IWebhookEndpointReputation? rep = null)
        => new(NullLogger<WebhookSensor>.Instance, new StubDetectorConfigProvider(),
               new StaticHttpContextAccessor(ctx), WebhookCatalog.Default, rep);

    private static DefaultHttpContext Post(string path, (string,string)[] headers, string ct = "application/json")
    {
        var c = new DefaultHttpContext();
        c.Request.Method = "POST"; c.Request.Path = path; c.Request.ContentType = ct;
        foreach (var (k,v) in headers) c.Request.Headers[k] = v;
        return c;
    }

    [Fact]
    public async Task Post_with_named_provider_signature_header_is_recognized_lowthreat()
    {
        var ctx = Post("/hooks/stripe", new[]{("Stripe-Signature","t=1,v1=abc")});
        var r = await New(ctx).DetectAsync(new SignalSink(1000, TimeSpan.FromMinutes(1)), Session);
        r.Should().ContainSingle();
        r[0].ConfidenceDelta.Should().BeLessThan(0);
        r[0].BotType.Should().Be(BotType.GoodBot.ToString());
    }

    [Fact]  // LOAD-BEARING spoof guard — proof it is not a bypass
    public async Task Post_shape_but_no_provider_no_corroboration_is_not_recognized()
    {
        // signature-shaped header name that is NOT a known provider header + no reputation
        var ctx = Post("/hooks/stripe", new[]{("X-Random","x")});
        var sink = new SignalSink(1000, TimeSpan.FromMinutes(1));
        var r = await New(ctx).DetectAsync(sink, Session);
        r.Should().BeEmpty();
        sink.Detect(SignalKeys.WebhookShape).Should().BeFalse();
    }

    [Fact]
    public async Task Get_request_is_ignored()
    {
        var ctx = new DefaultHttpContext(); ctx.Request.Method = "GET"; ctx.Request.Path = "/hooks/stripe";
        (await New(ctx).DetectAsync(new SignalSink(1000, TimeSpan.FromMinutes(1)), Session)).Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~WebhookSensor"`
Expected: FAIL — `WebhookSensor` / SignalKeys missing.

- [ ] **Step 3: Write minimal implementation**

Add the `SignalKeys.Webhook*` constants (section-commented) to `DetectionContext.cs`. Create `WebhookSensor.cs` mirroring `RegistryClientSensor.cs`:

```csharp
public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(SignalSink sink, string sessionId, CancellationToken ct = default)
{
    var http = _httpContextAccessor.HttpContext;
    if (http is null) return Task.FromResult(None());
    var req = http.Request;
    if (!HttpMethods.IsPost(req.Method)) return Task.FromResult(None());

    var isJson = (req.ContentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase)
              || (req.ContentType ?? "").Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    // shape = POST + json + a KNOWN webhook signature header present
    var provider = _catalog.Providers.FirstOrDefault(p => req.Headers.ContainsKey(p.SignatureHeader));
    var hasSignatureHeader = provider is not null
        || _catalog.SignatureHeaders.Any(h => req.Headers.ContainsKey(h));
    var isShape = isJson && hasSignatureHeader;
    if (!isShape) return Task.FromResult(None());
    sink.Raise(SignalKeys.WebhookShape, sessionId);

    var endpoint = req.Path.Value ?? "/";
    var ip = http.Connection?.RemoteIpAddress?.ToString() ?? "";
    // corroborators (Task 4 fills dominant/verified from the store; null store => false)
    var dominant = _reputation?.IsDominantIp(endpoint, ip) ?? false;
    var verified = _reputation?.HasVerifiedRecord(endpoint, ip) ?? false;
    var namedProvider = provider is not null;

    var corroborated = namedProvider || dominant || verified;   // shape already required
    if (!corroborated)
    {
        // shape-only: observed (learning), but NOT recognized — scored normally (spoof guard)
        return Task.FromResult(None());
    }

    sink.Raise($"{SignalKeys.WebhookDetected}:true", sessionId);
    if (namedProvider) sink.Raise($"{SignalKeys.WebhookProvider}:{provider!.Name}", sessionId);
    if (dominant) sink.Raise(SignalKeys.WebhookIpDominant, sessionId);
    if (verified) sink.Raise(SignalKeys.WebhookVerifiedRecord, sessionId);
    sink.Raise($"{SignalKeys.WebhookEndpoint}:{endpoint}", sessionId);

    return Task.FromResult(Single(new DetectionContribution
    {
        DetectorName = Name, Category = Category,
        ConfidenceDelta = _configProvider.GetParameter(Name, "corroborated_confidence_delta", _catalog.CorroboratedConfidenceDelta),
        Weight = _configProvider.GetParameter(Name, "corroborated_weight", _catalog.CorroboratedWeight),
        Reason = $"Webhook {(provider?.Name ?? "receiver")}: shape corroborated ({(namedProvider?"provider":dominant?"dominant-ip":"verified-record")})",
        BotType = BotType.GoodBot.ToString(),
        BotName = provider?.Name ?? "Webhook"
    }));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~WebhookSensor"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Models/DetectionContext.cs src/Mostlylucid.BotDetection/Orchestration/Atoms/WebhookSensor.cs src/Mostlylucid.BotDetection.Test/Orchestration/Atoms/WebhookSensorTests.cs
git commit -m "feat(webhook): WebhookSensor behavioral recognition + spoof guard"
```

---

### Task 3: `IWebhookEndpointReputation` + `SqliteWebhookReputationStore`

**Files:**
- Create: `src/Mostlylucid.BotDetection/Reputation/IWebhookEndpointReputation.cs`
- Create: `src/Mostlylucid.BotDetection/Data/SqliteWebhookReputationStore.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Data/SqliteWebhookReputationStoreTests.cs`

**Interfaces:**
- Consumes: `WebhookCatalog` thresholds (DominanceMinCount, DominanceMinShare, VerifiedMin2xx).
- Produces: `interface IWebhookEndpointReputation { void RecordRequest(string endpoint, string ip); void RecordOutcome(string endpoint, string ip, int statusCode); bool IsDominantIp(string endpoint, string ip); bool HasVerifiedRecord(string endpoint, string ip); }`. `SqliteWebhookReputationStore : IWebhookEndpointReputation`, ctor `(string dbPath, WebhookCatalog catalog)`, table `webhook_endpoint_ip(endpoint TEXT, ip TEXT, req_count INT, status_2xx INT, status_4xx INT, first_seen, last_seen, PRIMARY KEY(endpoint,ip))`. `IsDominantIp` = this ip's req_count >= DominanceMinCount AND its share of the endpoint's total >= DominanceMinShare. `HasVerifiedRecord` = status_2xx >= VerifiedMin2xx AND status_2xx > status_4xx.

- [ ] **Step 1: Write the failing test**

```csharp
public sealed class SqliteWebhookReputationStoreTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"wh_{Guid.NewGuid():N}.db");
    private SqliteWebhookReputationStore New() => new(_db, WebhookCatalog.Default);
    public void Dispose() { if (File.Exists(_db)) File.Delete(_db); }

    [Fact]
    public void Dominant_ip_requires_min_count_and_share()
    {
        var s = New();
        for (var i = 0; i < 25; i++) s.RecordRequest("/h", "1.1.1.1");   // dominant
        s.RecordRequest("/h", "9.9.9.9");                                 // rare
        s.IsDominantIp("/h", "1.1.1.1").Should().BeTrue();
        s.IsDominantIp("/h", "9.9.9.9").Should().BeFalse();
    }

    [Fact]
    public void Verified_record_requires_consistent_2xx_over_4xx()
    {
        var s = New();
        for (var i = 0; i < 12; i++) s.RecordOutcome("/h", "1.1.1.1", 200);
        s.RecordOutcome("/h", "1.1.1.1", 400);
        s.HasVerifiedRecord("/h", "1.1.1.1").Should().BeTrue();
        for (var i = 0; i < 12; i++) s.RecordOutcome("/h", "2.2.2.2", 400); // spoofer: all 4xx
        s.HasVerifiedRecord("/h", "2.2.2.2").Should().BeFalse();
    }

    [Fact]
    public void Persists_across_reopen()
    {
        { var s = New(); for (var i=0;i<25;i++) s.RecordRequest("/h","1.1.1.1"); }
        New().IsDominantIp("/h", "1.1.1.1").Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~SqliteWebhookReputation"`
Expected: FAIL — types missing.

- [ ] **Step 3: Write minimal implementation**

Implement the interface + SQLite store. Follow the connection/DDL pattern of an existing SQLite store (e.g. `Data/SqliteFingerprintStore.cs` or `Identity/SqliteFingerprintStore.cs`): `CREATE TABLE IF NOT EXISTS webhook_endpoint_ip (...)`, upsert on `RecordRequest`/`RecordOutcome` (`INSERT ... ON CONFLICT(endpoint,ip) DO UPDATE SET req_count = req_count + 1, ...`), and the two boolean queries with the catalog thresholds. Single shared connection or open-per-op consistent with the sibling stores; no in-memory dictionary as the source of truth.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~SqliteWebhookReputation"`
Expected: PASS (all 3).

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Reputation/IWebhookEndpointReputation.cs src/Mostlylucid.BotDetection/Data/SqliteWebhookReputationStore.cs src/Mostlylucid.BotDetection.Test/Data/SqliteWebhookReputationStoreTests.cs
git commit -m "feat(webhook): SQLite endpoint/IP reputation (dominance + verified 2xx)"
```

---

### Task 4: Corroborate `WebhookSensor` via the reputation store

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/Atoms/WebhookSensor.cs` (already reads `_reputation`; add the observe-write)
- Test: `src/Mostlylucid.BotDetection.Test/Orchestration/Atoms/WebhookSensorTests.cs` (add cases)

**Interfaces:**
- Consumes: `IWebhookEndpointReputation` (Task 3).
- Produces: no new type. The sensor now (a) calls `_reputation?.RecordRequest(endpoint, ip)` for every webhook-shaped request (learning, even when not yet recognized), and (b) recognizes a shape-only request when `IsDominantIp` OR `HasVerifiedRecord` is true.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Shape_only_from_dominant_ip_is_recognized()
{
    var rep = new Mock<IWebhookEndpointReputation>();
    rep.Setup(r => r.IsDominantIp(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
    var ctx = Post("/hooks/x", new[]{("X-Webhook-Signature","z")}); // generic sig header, NOT a named provider
    var r = await New(ctx, rep.Object).DetectAsync(new SignalSink(1000, TimeSpan.FromMinutes(1)), Session);
    r.Should().ContainSingle(); r[0].ConfidenceDelta.Should().BeLessThan(0);
}

[Fact]
public async Task Every_webhook_shaped_request_records_a_request_for_learning()
{
    var rep = new Mock<IWebhookEndpointReputation>();
    var ctx = Post("/hooks/x", new[]{("X-Webhook-Signature","z")});
    await New(ctx, rep.Object).DetectAsync(new SignalSink(1000, TimeSpan.FromMinutes(1)), Session);
    rep.Verify(r => r.RecordRequest("/hooks/x", It.IsAny<string>()), Times.Once);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~WebhookSensor"`
Expected: FAIL — RecordRequest not called / dominant path not wired.

- [ ] **Step 3: Write minimal implementation**

In `WebhookSensor.DetectAsync`, right after `sink.Raise(SignalKeys.WebhookShape, ...)` and computing `endpoint`/`ip`, add `_reputation?.RecordRequest(endpoint, ip);`. The `dominant`/`verified` reads are already in place from Task 2; confirm the `corroborated` branch uses them.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~WebhookSensor"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Orchestration/Atoms/WebhookSensor.cs src/Mostlylucid.BotDetection.Test/Orchestration/Atoms/WebhookSensorTests.cs
git commit -m "feat(webhook): corroborate via dominant-ip/verified-record + learn every shaped request"
```

---

### Task 5: Post-`_next` status recorder middleware

**Files:**
- Create: `src/Mostlylucid.BotDetection/Middleware/WebhookOutcomeRecorderMiddleware.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Middleware/WebhookOutcomeRecorderMiddlewareTests.cs`

**Interfaces:**
- Consumes: `IWebhookEndpointReputation`; the `SignalKeys.WebhookShape` marker on `HttpContext.Items` (the sensor stashes `context.Items["sb.webhook.endpoint"] = endpoint` when it raises shape).
- Produces: `WebhookOutcomeRecorderMiddleware(RequestDelegate next, IWebhookEndpointReputation rep)` with `InvokeAsync` that calls `await _next(context)` FIRST, then — only if the request was webhook-shaped — reads `context.Response.StatusCode` and calls `rep.RecordOutcome(endpoint, ip, statusCode)`. Recording MUST be after `_next` (status is unknown before).

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Records_upstream_status_after_next_not_before()
{
    var rep = new Mock<IWebhookEndpointReputation>();
    int? seenAtRecord = null;
    rep.Setup(r => r.RecordOutcome(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
       .Callback<string,string,int>((_,_,s) => seenAtRecord = s);
    var ctx = new DefaultHttpContext(); ctx.Request.Method="POST"; ctx.Request.Path="/hooks/x";
    ctx.Items["sb.webhook.endpoint"] = "/hooks/x";
    ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("1.1.1.1");
    RequestDelegate next = c => { c.Response.StatusCode = 200; return Task.CompletedTask; }; // upstream sets 200
    await new WebhookOutcomeRecorderMiddleware(next, rep.Object).InvokeAsync(ctx);
    seenAtRecord.Should().Be(200);   // proves it read the status AFTER _next set it
    rep.Verify(r => r.RecordOutcome("/hooks/x", "1.1.1.1", 200), Times.Once);
}

[Fact]
public async Task Non_webhook_request_is_not_recorded()
{
    var rep = new Mock<IWebhookEndpointReputation>();
    var ctx = new DefaultHttpContext(); ctx.Request.Method="GET"; ctx.Request.Path="/";
    await new WebhookOutcomeRecorderMiddleware(_ => Task.CompletedTask, rep.Object).InvokeAsync(ctx);
    rep.Verify(r => r.RecordOutcome(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
}
```

Also add, in Task 2's sensor, the `context.Items["sb.webhook.endpoint"] = endpoint;` write where shape is raised (so the recorder knows which requests to record). Add that one line + a test assertion here if not already covered.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~WebhookOutcomeRecorder"`
Expected: FAIL — middleware missing.

- [ ] **Step 3: Write minimal implementation**

```csharp
public sealed class WebhookOutcomeRecorderMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IWebhookEndpointReputation _rep;
    public WebhookOutcomeRecorderMiddleware(RequestDelegate next, IWebhookEndpointReputation rep) { _next = next; _rep = rep; }
    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context); // status only known AFTER upstream responds
        if (context.Items.TryGetValue("sb.webhook.endpoint", out var ep) && ep is string endpoint)
        {
            var ip = context.Connection?.RemoteIpAddress?.ToString() ?? "";
            _rep.RecordOutcome(endpoint, ip, context.Response.StatusCode);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~WebhookOutcomeRecorder"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Middleware/WebhookOutcomeRecorderMiddleware.cs src/Mostlylucid.BotDetection.Test/Middleware/WebhookOutcomeRecorderMiddlewareTests.cs src/Mostlylucid.BotDetection/Orchestration/Atoms/WebhookSensor.cs
git commit -m "feat(webhook): post-_next upstream-status recorder feeds verified track record"
```

---

### Task 6: DI registration + narrative + orchestrator wiring

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/Atoms/BotDetectionOrchestrator.cs` (register `IDetectorAtom, WebhookSensor`)
- Modify: `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` (register `IWebhookEndpointReputation` → `SqliteWebhookReputationStore` via `TryAddSingleton`, db path from options; and `app.UseMiddleware<WebhookOutcomeRecorderMiddleware>()` in the `UseBotDetection`/`UseStyloBot` pipeline after detection, before/around `_next`)
- Modify: `src/Mostlylucid.BotDetection.UI/Services/DetectionNarrativeBuilder.cs` (`DetectorFriendlyNames["Webhook"]="Webhook receiver"`, `DetectorCategories["Webhook"]="Machine client"`)
- Test: extend `src/Mostlylucid.BotDetection.Test/` DI/registration coverage (mirror how `RegistryClient` is asserted, e.g. `AtomEmitContractTests`)

**Interfaces:** Consumes all prior tasks. Produces the wired pipeline.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void WebhookSensor_is_registered_as_detector_atom()
{
    var services = new ServiceCollection();
    services.AddBotDetection();               // the FOSS entrypoint
    var provider = services.BuildServiceProvider();
    provider.GetServices<IDetectorAtom>().Should().Contain(a => a.GetType() == typeof(WebhookSensor));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~WebhookSensor_is_registered"`
Expected: FAIL — not registered.

- [ ] **Step 3: Write minimal implementation**

Add `services.AddSingleton<IDetectorAtom, WebhookSensor>();` in the Wave-0 section of `BotDetectionOrchestrator.cs`; `services.TryAddSingleton<IWebhookEndpointReputation>(sp => new SqliteWebhookReputationStore(<webhooks-db-path from BotDetectionOptions>, WebhookCatalog.Default));` in `ServiceCollectionExtensions`; register the middleware in the `UseBotDetection`/`UseStyloBot` builder; add the narrative entries. For `AddBotDetectionInMemory`, register a no-op `NullWebhookEndpointReputation` (all reads false, writes no-op) mirroring the ephemeral-mode pattern.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~WebhookSensor_is_registered"`
Expected: PASS. Also run the full suite: `dotnet test src/Mostlylucid.BotDetection.Test/`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(webhook): register sensor + reputation store + outcome middleware + narrative"
```

---

### Task 7: `webhook-recognized` benign arm in `PostDetectionActionGate`

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/DetectionLedgerExtensions.cs` (surface `AggregatedEvidence.WebhookRecognized` via the sink-first `ReadBool(SignalKeys.WebhookDetected)` — mirror the `RegistryClientCorroborated` wiring)
- Modify: `src/Mostlylucid.BotDetection/Orchestration/DetectionContribution.cs` (`bool WebhookRecognized { get; init; }`)
- Modify: `src/Mostlylucid.BotDetection/Enforcement/PostDetectionActionGate.cs` (benign arm)
- Test: `src/Mostlylucid.BotDetection.Test/Enforcement/PostDetectionActionGateWebhookTests.cs`

**Interfaces:**
- Consumes: `SignalKeys.WebhookDetected`.
- Produces: `AggregatedEvidence.WebhookRecognized` (set via `ReadBool(SignalKeys.WebhookDetected)` in both evidence builders, exactly like `RegistryClientCorroborated`); a benign-route arm in `EvaluateAsync` — before the BotType fallback, if `evidence.WebhookRecognized` and no upstream override, set `TriggeredActionPolicyName="webhook-recognized"` and return `PolicyContinued` (no throttle/challenge). Mirror the registry-client arm verbatim.

- [ ] **Step 1: Write the failing test** (mirror `PostDetectionActionGateRegistryTests`)

```csharp
[Fact]
public async Task Recognized_webhook_is_not_throttled()
{
    // evidence with WebhookRecognized=true, BotProbability>=threshold, BotTypeActionPolicies["GoodBot"]="throttle-status"
    // -> outcome PolicyContinued, no throttle policy executed
}

[Fact]
public async Task Unrecognized_traffic_to_same_endpoint_still_gets_normal_action()
{
    // evidence WebhookRecognized=false, high prob -> resolves the normal BotType action (proof no path bypass)
}
```

(Fill the bodies by copying `PostDetectionActionGateRegistryTests` and swapping `RegistryClientCorroborated`→`WebhookRecognized`, `registry-client-recognized`→`webhook-recognized`.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~PostDetectionActionGateWebhook"`
Expected: FAIL.

- [ ] **Step 3: Write minimal implementation**

Add the `WebhookRecognized` field; set it via `ReadBool(SignalKeys.WebhookDetected)` in both `AggregatedEvidence` builders in `DetectionLedgerExtensions.cs`; add the benign arm in `PostDetectionActionGate.EvaluateAsync` (copy the registry-client arm, rename).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~PostDetectionActionGateWebhook"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(webhook): PostDetectionActionGate webhook-recognized benign arm (shape only unrecognized)"
```

---

### Task 8: Site-wide safety ceiling

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Models/BotDetectionOptions.cs` (`int SafetyCeilingRpm { get; set; } = 100_000;` — a very high default; XML-doc: absolute-flood ceiling applied to every endpoint incl. trusted/recognized)
- Modify: `src/Mostlylucid.BotDetection/Enforcement/PostDetectionActionGate.cs` (apply the ceiling via `ITokenBucketStore` even on the `webhook-recognized` / benign / no-action paths — key per visitor+endpoint; on exhaustion return 429; else continue)
- Test: `src/Mostlylucid.BotDetection.Test/Enforcement/SafetyCeilingTests.cs`

**Interfaces:**
- Consumes: `ITokenBucketStore`, `BotDetectionOptions.SafetyCeilingRpm`.
- Produces: ceiling enforcement helper `bool WithinSafetyCeiling(HttpContext, ITokenBucketStore, int rpm)` applied on every outcome path (including recognized webhook + no-override), before returning continue.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Recognized_traffic_below_ceiling_is_never_shaped_but_flood_is_shed()
{
    // SafetyCeilingRpm=5 (test value). First 5 recognized-webhook requests -> PolicyContinued (200).
    // The 6th in the same minute -> 429 (ceiling shed), proving legit volume passes but a flood is capped.
}

[Fact]
public async Task Ceiling_applies_site_wide_to_non_webhook_endpoint_too()
{
    // a plain no-action request over the ceiling -> 429
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~SafetyCeiling"`
Expected: FAIL.

- [ ] **Step 3: Write minimal implementation**

Add `SafetyCeilingRpm` to options. In `PostDetectionActionGate` (and/or the block-response gate boundary), before any `PolicyContinued`/`NoOverride` return, call the token bucket keyed on `(visitorKey + ":" + path)` with capacity+refill = `SafetyCeilingRpm`; if not admitted, write 429 + `Retry-After` and return `PolicyHandledResponse`. Reuse the `ThrottleActionHandler`/`RateLimitActionHandler` 429-shaping helper. Guard: only enforce when `ITokenBucketStore` is registered and `SafetyCeilingRpm > 0`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~SafetyCeiling"`
Expected: PASS. Then full suite: `dotnet test src/Mostlylucid.BotDetection.Test/ src/Mostlylucid.BotDetection.Orchestration.Tests/`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(webhook): site-wide safety ceiling (SafetyCeilingRpm) sheds absolute floods incl recognized traffic"
```

---

## Self-Review

- **Spec coverage:** recognition-layered (T1,T2,T4) ✓; verified-2xx track record post-`_next` (T3,T5) ✓; dominant/commonest IP (T3,T4) ✓; named-provider catalog (T1,T2) ✓; no-bypass negative test (T2 load-bearing + T7 unrecognized-still-shaped) ✓; SQLite persistence (T3) ✓; policy shape-only-unrecognized + benign arm (T7) ✓; site-wide high safety ceiling (T8) ✓; no-magic-numbers via yaml (T1,T2) ✓; 5-file atom pattern (T1,T2,T6) ✓; future permitted-IP/strict-mode seams = design-only, no task (correct — out of this cut).
- **Placeholder scan:** T7 test bodies say "copy `PostDetectionActionGateRegistryTests` and swap names" — that references a concrete existing file with an exact rename, not a vague placeholder; acceptable. All other code steps are concrete.
- **Type consistency:** `IWebhookEndpointReputation` methods (`RecordRequest`/`RecordOutcome`/`IsDominantIp`/`HasVerifiedRecord`) consistent across T3/T4/T5; `SignalKeys.Webhook*` names consistent T2→T7; `WebhookRecognized` field consistent T7; `SafetyCeilingRpm` consistent T8.
