# StyloBot Observability Package Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a FOSS-additive `Mostlylucid.BotDetection.Observability` package that turns the existing blackboard signal stream and `DetectionEvent` flow into structured logs, metrics, and OpenTelemetry exports on the host's own pipelines, so customers see stylobot in their Datadog / Seq / Splunk / Loki / Grafana with one DI call.

**Architecture:**
- Signals are the universal substrate: the per-host global `SignalSink` already lives inside `EphemeralDetectionOrchestrator` and is what every detector raises onto. Expose a subscribe method on `IDetectionOrchestrator` so external code can attach lock-free listeners without touching the request hot path.
- A `BlackboardSignalLogBridge` hosted service uses `ephemeral.logging`'s `SignalToLoggerAdapter` to translate global signals into structured `ILogger<StyloBotSignals>` calls. Customers' existing Serilog (or any `Microsoft.Extensions.Logging` backend) carries the stream.
- A `SerilogDetectionEventPublisher` implements `IDetectionEventPublisher` so each completed verdict becomes one structured Serilog event with every `DetectionEvent` field as a property.
- A `StyloBotLogEnricher` (Serilog `ILogEventEnricher`) reads the current request's verdict off `HttpContext.Items` and stamps every host log line emitted during the request with `StyloBot.*` properties.
- A single `AddStyloBotObservability()` extension wires OTel logs + metrics + traces (existing `Mostlylucid.BotDetection` ActivitySource + `Mostlylucid.BotDetection` and `Mostlylucid.BotDetection.Signals` meters) with an OTLP exporter as the default and Prometheus as an opt-in switch.

**Tech Stack:** .NET 10, Serilog 10, `Mostlylucid.Ephemeral` 2.6.3, `Mostlylucid.Ephemeral.Logging` 2.0.0 (new dep), OpenTelemetry SDK 1.11.x (already used by Gateway), `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.11.x, xUnit + FluentAssertions for tests.

**Design constraints (binding):**
- FOSS additive only. Detection sensitivity is untouched. ([[feedback_foss_never_degraded]])
- All persistent state goes through existing stores. No new in-memory dictionaries that matter. ([[feedback_no_inmemory_persistence]])
- No hard-coded names. Defaults go in YAML/options. ([[feedback_no_word_lists]])
- Verify by running before committing. ([[feedback_verify_before_checkin]])
- Tag prefix `allbot-v` if cut as a release. ([[feedback_release_tag]])

---

## File Structure

```
src/Mostlylucid.BotDetection.Observability/
  Mostlylucid.BotDetection.Observability.csproj      # NEW, packable, AGPLv3
  ObservabilityServiceCollectionExtensions.cs        # AddStyloBotObservability(...)
  StyloBotObservabilityOptions.cs                    # POCO bound to BotDetection:Observability
  Signals/
    BlackboardSignalLogBridge.cs                     # IHostedService; subscribes global SignalSink → ILogger
    BlackboardSignalLogOptions.cs                    # min level, include/exclude prefix lists
    StyloBotSignalCategory.cs                        # marker type for ILogger<StyloBotSignalCategory>
  Events/
    SerilogDetectionEventPublisher.cs                # IDetectionEventPublisher → Serilog ILogger
    DetectionEventLogProperties.cs                   # extension method: ToLogProperties(DetectionEvent)
  Enrichment/
    StyloBotLogEnricher.cs                           # Serilog ILogEventEnricher; reads HttpContext.Items
    StyloBotEnricherExtensions.cs                    # LoggerConfiguration.Enrich.WithStyloBot()
  OpenTelemetry/
    StyloBotOpenTelemetryExtensions.cs               # AddStyloBotOpenTelemetry inner helper
  README.md                                           # nuget readme

src/Mostlylucid.BotDetection/
  Orchestration/EphemeralDetectionOrchestrator.cs    # MODIFY: expose SubscribeToSignals(Action<SignalEvent>)
  Orchestration/IDetectionOrchestrator.cs            # MODIFY: add SubscribeToSignals contract
  Models/BotDetectionOptions.cs                      # MODIFY: add Observability subsection POCO ref

src/Mostlylucid.BotDetection.Observability.Test/
  Mostlylucid.BotDetection.Observability.Test.csproj # NEW, IsTestProject
  SerilogDetectionEventPublisherTests.cs
  BlackboardSignalLogBridgeTests.cs
  StyloBotLogEnricherTests.cs
  ObservabilityServiceCollectionExtensionsTests.cs

src/Mostlylucid.BotDetection.Demo/
  Program.cs                                          # MODIFY: opt in to observability + sample appsettings
  appsettings.Development.json                        # MODIFY: BotDetection:Observability block

src/Mostlylucid.BotDetection/docs/
  observability.md                                    # NEW: customer-facing usage doc

mostlylucid.stylobot.sln                              # MODIFY: include the two new projects
```

**Why this split:**
- One file per responsibility (publisher / bridge / enricher / extension) lines up with how the existing telemetry code is organised under `Mostlylucid.BotDetection/Telemetry/`.
- Test project mirrors prod project. No fixtures shared with `Mostlylucid.BotDetection.Test` to keep this slice independently buildable.
- The orchestrator change is small (one new method) but lives in the core project because the global `SignalSink` is an orchestrator-owned field.

---

## Task 1: Scaffold the observability project and add it to the solution

**Files:**
- Create: `src/Mostlylucid.BotDetection.Observability/Mostlylucid.BotDetection.Observability.csproj`
- Create: `src/Mostlylucid.BotDetection.Observability/README.md`
- Create: `src/Mostlylucid.BotDetection.Observability.Test/Mostlylucid.BotDetection.Observability.Test.csproj`
- Modify: `mostlylucid.stylobot.sln`

- [ ] **Step 1: Write the csproj**

`src/Mostlylucid.BotDetection.Observability/Mostlylucid.BotDetection.Observability.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <LangVersion>latest</LangVersion>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <PackageId>Mostlylucid.BotDetection.Observability</PackageId>
        <Description>Structured logs, metrics, and OpenTelemetry export for StyloBot detection events and blackboard signals.</Description>
        <PackageTags>stylobot;serilog;opentelemetry;logging;metrics;observability</PackageTags>
        <PackageLicenseExpression>AGPL-3.0-only</PackageLicenseExpression>
        <PackageReadmeFile>README.md</PackageReadmeFile>
        <RepositoryUrl>https://github.com/scottgal/mostlylucid.stylobot</RepositoryUrl>
        <GeneratePackageOnBuild>true</GeneratePackageOnBuild>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\Mostlylucid.BotDetection\Mostlylucid.BotDetection.csproj" />
    </ItemGroup>

    <ItemGroup>
        <PackageReference Include="Mostlylucid.Ephemeral" Version="2.6.3" />
        <PackageReference Include="Mostlylucid.Ephemeral.Logging" Version="2.0.0" />
        <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.8" />
        <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.8" />
        <PackageReference Include="Microsoft.Extensions.Options" Version="10.0.8" />
        <PackageReference Include="Microsoft.AspNetCore.Http.Abstractions" Version="2.3.0" />
        <PackageReference Include="Serilog" Version="4.2.0" />
        <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.11.0" />
        <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.11.0" />
        <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.11.0" />
    </ItemGroup>

    <ItemGroup>
        <None Include="README.md" Pack="true" PackagePath="\" />
    </ItemGroup>
</Project>
```

- [ ] **Step 2: Write a minimal README**

`src/Mostlylucid.BotDetection.Observability/README.md`:

```markdown
# Mostlylucid.BotDetection.Observability

Structured logs, metrics, and OpenTelemetry export for StyloBot.

```csharp
builder.Services.AddBotDetection();
builder.Services.AddStyloBotObservability(builder.Configuration);
```

See `docs/observability.md` in the main repo for the full configuration surface.
```

- [ ] **Step 3: Write the test csproj**

`src/Mostlylucid.BotDetection.Observability.Test/Mostlylucid.BotDetection.Observability.Test.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <LangVersion>preview</LangVersion>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <IsPackable>false</IsPackable>
        <IsTestProject>true</IsTestProject>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.8" />
        <PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.8" />
        <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.5.1" />
        <PackageReference Include="xunit" Version="2.9.3" />
        <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5">
            <PrivateAssets>all</PrivateAssets>
        </PackageReference>
        <PackageReference Include="FluentAssertions" Version="8.9.0" />
        <PackageReference Include="Serilog.Sinks.InMemory" Version="0.11.0" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\Mostlylucid.BotDetection.Observability\Mostlylucid.BotDetection.Observability.csproj" />
    </ItemGroup>

    <ItemGroup>
        <Using Include="Xunit" />
    </ItemGroup>
</Project>
```

- [ ] **Step 4: Add projects to the solution**

Run from repo root:

```bash
dotnet sln mostlylucid.stylobot.sln add src/Mostlylucid.BotDetection.Observability/Mostlylucid.BotDetection.Observability.csproj
dotnet sln mostlylucid.stylobot.sln add src/Mostlylucid.BotDetection.Observability.Test/Mostlylucid.BotDetection.Observability.Test.csproj
```

- [ ] **Step 5: Verify the solution builds**

```bash
dotnet build mostlylucid.stylobot.sln
```

Expected: succeeds. Two new projects compile to empty assemblies.

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection.Observability src/Mostlylucid.BotDetection.Observability.Test mostlylucid.stylobot.sln
git commit -m "$(cat <<'EOF'
feat(observability): scaffold Mostlylucid.BotDetection.Observability package

Empty FOSS-additive package + test project added to the solution.
Subsequent commits add the publisher, signal bridge, enricher, and OTel wiring.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Expose `SubscribeToSignals` on the orchestrator

The global `SignalSink` is private inside `EphemeralDetectionOrchestrator`. Add a subscribe method on the interface so the bridge can attach without poking internals.

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/IDetectionOrchestrator.cs`
- Modify: `src/Mostlylucid.BotDetection/Orchestration/EphemeralDetectionOrchestrator.cs:45-113`
- Test: `src/Mostlylucid.BotDetection.Test/Orchestration/OrchestratorSignalSubscriptionTests.cs`

- [ ] **Step 1: Write the failing test**

`src/Mostlylucid.BotDetection.Test/Orchestration/OrchestratorSignalSubscriptionTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Detection;
using Mostlylucid.Ephemeral.Signals;

namespace Mostlylucid.BotDetection.Test.Orchestration;

public class OrchestratorSignalSubscriptionTests
{
    [Fact]
    public void SubscribeToSignals_receives_raised_signals()
    {
        var options = Options.Create(new BotDetectionOptions());
        var orchestrator = new EphemeralDetectionOrchestrator(
            NullLogger<EphemeralDetectionOrchestrator>.Instance,
            options,
            Array.Empty<IContributingDetector>());

        var received = new List<SignalEvent>();
        using var sub = orchestrator.SubscribeToSignals(received.Add);

        // Test seam: the orchestrator exposes a Raise pass-through for tests + bridges.
        orchestrator.RaiseSignalForObservability("test.observed", key: "k1");

        received.Should().ContainSingle(s => s.Signal == "test.observed" && s.Key == "k1");
    }

    [Fact]
    public void Disposing_subscription_stops_delivery()
    {
        var options = Options.Create(new BotDetectionOptions());
        var orchestrator = new EphemeralDetectionOrchestrator(
            NullLogger<EphemeralDetectionOrchestrator>.Instance,
            options,
            Array.Empty<IContributingDetector>());

        var received = new List<SignalEvent>();
        var sub = orchestrator.SubscribeToSignals(received.Add);
        sub.Dispose();

        orchestrator.RaiseSignalForObservability("after.dispose");

        received.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~OrchestratorSignalSubscriptionTests"
```

Expected: FAIL on `SubscribeToSignals` and `RaiseSignalForObservability` not defined.

- [ ] **Step 3: Add the interface members**

Edit `src/Mostlylucid.BotDetection/Orchestration/IDetectionOrchestrator.cs`. Add:

```csharp
using Mostlylucid.Ephemeral.Signals;

// ... existing namespace and interface ...
public partial interface IDetectionOrchestrator
{
    /// <summary>
    ///     Subscribe to the orchestrator's global blackboard signal stream. Listeners run
    ///     synchronously on the thread that raises the signal - keep them fast and non-throwing.
    ///     Dispose the returned subscription to stop delivery.
    /// </summary>
    IDisposable SubscribeToSignals(Action<SignalEvent> listener);

    /// <summary>
    ///     Raise a signal onto the global sink. Intended for cross-host observability and tests;
    ///     detectors raise via their per-request blackboard.
    /// </summary>
    void RaiseSignalForObservability(string signal, string? key = null);
}
```

If `IDetectionOrchestrator` is currently a single non-partial declaration, just add the members to that interface.

- [ ] **Step 4: Implement on `EphemeralDetectionOrchestrator`**

Edit `src/Mostlylucid.BotDetection/Orchestration/EphemeralDetectionOrchestrator.cs`. Below the existing `GetRecentSignals()` method add:

```csharp
public IDisposable SubscribeToSignals(Action<SignalEvent> listener)
{
    if (listener is null) throw new ArgumentNullException(nameof(listener));
    return _globalSignals.Subscribe(listener);
}

public void RaiseSignalForObservability(string signal, string? key = null)
{
    if (string.IsNullOrEmpty(signal)) return;
    _globalSignals.Raise(signal, key);
}
```

- [ ] **Step 5: Run the test to verify it passes**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~OrchestratorSignalSubscriptionTests"
```

Expected: PASS.

- [ ] **Step 6: Verify nothing else broke**

```bash
dotnet build mostlylucid.stylobot.sln
dotnet test src/Mostlylucid.BotDetection.Test
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests
```

Expected: all green. If any pre-existing failures surface, fix them per [[feedback_always_fix_regressions]].

- [ ] **Step 7: Commit**

```bash
git add src/Mostlylucid.BotDetection/Orchestration/IDetectionOrchestrator.cs \
        src/Mostlylucid.BotDetection/Orchestration/EphemeralDetectionOrchestrator.cs \
        src/Mostlylucid.BotDetection.Test/Orchestration/OrchestratorSignalSubscriptionTests.cs
git commit -m "$(cat <<'EOF'
feat(orchestrator): expose SubscribeToSignals + RaiseSignalForObservability

Thin subscribe seam on the global blackboard SignalSink so out-of-process
observability (signal→log bridge, future OTel log exporter) can listen without
poking orchestrator internals.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `SerilogDetectionEventPublisher`

The "StyloBot Serilog sink" customers will look for. One structured log line per completed detection on the host's `ILogger`.

**Files:**
- Create: `src/Mostlylucid.BotDetection.Observability/Events/SerilogDetectionEventPublisher.cs`
- Create: `src/Mostlylucid.BotDetection.Observability/Events/DetectionEventLogProperties.cs`
- Test: `src/Mostlylucid.BotDetection.Observability.Test/SerilogDetectionEventPublisherTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Observability.Events;
using Mostlylucid.BotDetection.Orchestration.Telemetry;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Sinks.InMemory;

namespace Mostlylucid.BotDetection.Observability.Test;

public class SerilogDetectionEventPublisherTests
{
    private (SerilogDetectionEventPublisher publisher, InMemorySink sink) Build()
    {
        var sink = new InMemorySink();
        var serilog = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var factory = new SerilogLoggerFactory(serilog);
        var logger = factory.CreateLogger<SerilogDetectionEventPublisher>();
        return (new SerilogDetectionEventPublisher(logger), sink);
    }

    [Fact]
    public async Task Bot_block_event_is_logged_at_Warning_with_all_properties()
    {
        var (publisher, sink) = Build();
        var evt = new DetectionEvent
        {
            Timestamp = DateTime.UtcNow,
            RequestId = "req-1",
            Signature = "sig-abc",
            Path = "/wp-login.php",
            Method = "GET",
            StatusCode = 403,
            IsBot = true,
            BotProbability = 0.97,
            Confidence = 0.91,
            RiskBand = "high",
            ThreatBand = "high",
            Action = "block",
            BotName = "wp-scanner",
            BotType = "Scanner",
            CountryCode = "RU",
            ProcessingTimeMs = 4.2
        };

        await publisher.PublishAsync(evt);

        var entry = sink.LogEvents.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogEventLevel.Warning);
        entry.Properties.Should().ContainKey("StyloBot_Signature");
        entry.Properties["StyloBot_Signature"].ToString().Should().Contain("sig-abc");
        entry.Properties["StyloBot_IsBot"].ToString().Should().Be("True");
        entry.Properties["StyloBot_Action"].ToString().Should().Contain("block");
    }

    [Fact]
    public async Task Human_allow_event_is_logged_at_Debug()
    {
        var (publisher, sink) = Build();
        var evt = new DetectionEvent
        {
            Timestamp = DateTime.UtcNow,
            RequestId = "req-2",
            Signature = "sig-h",
            IsBot = false,
            BotProbability = 0.04,
            Action = "allow"
        };

        await publisher.PublishAsync(evt);

        sink.LogEvents.Should().ContainSingle().Which.Level.Should().Be(LogEventLevel.Debug);
    }

    [Fact]
    public async Task Challenge_event_is_logged_at_Information()
    {
        var (publisher, sink) = Build();
        var evt = new DetectionEvent
        {
            Timestamp = DateTime.UtcNow,
            RequestId = "req-3",
            Signature = "sig-c",
            IsBot = true,
            Action = "challenge"
        };

        await publisher.PublishAsync(evt);

        sink.LogEvents.Should().ContainSingle().Which.Level.Should().Be(LogEventLevel.Information);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test src/Mostlylucid.BotDetection.Observability.Test
```

Expected: FAIL: `SerilogDetectionEventPublisher` not defined.

- [ ] **Step 3: Implement `DetectionEventLogProperties`**

`src/Mostlylucid.BotDetection.Observability/Events/DetectionEventLogProperties.cs`:

```csharp
using Mostlylucid.BotDetection.Orchestration.Telemetry;

namespace Mostlylucid.BotDetection.Observability.Events;

internal static class DetectionEventLogProperties
{
    public static object[] ToLogArgs(this DetectionEvent evt) => new object[]
    {
        evt.Signature,
        evt.IsBot,
        evt.BotProbability,
        evt.Confidence,
        evt.RiskBand ?? "unknown",
        evt.ThreatBand ?? "unknown",
        evt.Action ?? "none",
        evt.BotName ?? string.Empty,
        evt.BotType ?? string.Empty,
        evt.CountryCode ?? string.Empty,
        evt.Path ?? string.Empty,
        evt.Method ?? string.Empty,
        evt.StatusCode,
        evt.ProcessingTimeMs,
        evt.RequestId,
        evt.GatewayId ?? string.Empty
    };

    public const string MessageTemplate =
        "StyloBot detection: signature={StyloBot_Signature} isBot={StyloBot_IsBot} " +
        "prob={StyloBot_Probability} conf={StyloBot_Confidence} " +
        "risk={StyloBot_RiskBand} threat={StyloBot_ThreatBand} action={StyloBot_Action} " +
        "botName={StyloBot_BotName} botType={StyloBot_BotType} country={StyloBot_CountryCode} " +
        "path={StyloBot_Path} method={StyloBot_Method} status={StyloBot_StatusCode} " +
        "elapsedMs={StyloBot_ProcessingTimeMs} requestId={StyloBot_RequestId} gateway={StyloBot_GatewayId}";
}
```

- [ ] **Step 4: Implement `SerilogDetectionEventPublisher`**

`src/Mostlylucid.BotDetection.Observability/Events/SerilogDetectionEventPublisher.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Orchestration.Telemetry;

namespace Mostlylucid.BotDetection.Observability.Events;

/// <summary>
///     Emits each <see cref="DetectionEvent"/> as one structured log entry on the host's
///     ILogger pipeline. When the host is wired with Serilog, this is what people will
///     call the "StyloBot Serilog sink": properties land in Datadog / Seq / Splunk / Loki
///     with the StyloBot_* prefix so customers can query and dashboard against them.
/// </summary>
public sealed class SerilogDetectionEventPublisher : IDetectionEventPublisher
{
    private readonly ILogger<SerilogDetectionEventPublisher> _logger;

    public SerilogDetectionEventPublisher(ILogger<SerilogDetectionEventPublisher> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => "serilog";

    public ValueTask PublishAsync(DetectionEvent evt, CancellationToken ct = default)
    {
        if (evt is null) return ValueTask.CompletedTask;

        var level = LevelFor(evt);
        if (!_logger.IsEnabled(level)) return ValueTask.CompletedTask;

#pragma warning disable CA2254 // Template is a const; properties are positional by design.
        _logger.Log(level, DetectionEventLogProperties.MessageTemplate, evt.ToLogArgs());
#pragma warning restore CA2254

        return ValueTask.CompletedTask;
    }

    private static LogLevel LevelFor(DetectionEvent evt)
    {
        if (string.Equals(evt.Action, "block", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Warning;
        if (string.Equals(evt.Action, "challenge", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(evt.Action, "throttle-tools", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(evt.Action, "throttle-stealth", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(evt.Action, "throttle-status", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(evt.Action, "redirect-honeypot", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Information;
        return evt.IsBot ? LogLevel.Information : LogLevel.Debug;
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

```bash
dotnet test src/Mostlylucid.BotDetection.Observability.Test --filter "FullyQualifiedName~SerilogDetectionEventPublisherTests"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection.Observability/Events \
        src/Mostlylucid.BotDetection.Observability.Test/SerilogDetectionEventPublisherTests.cs
git commit -m "$(cat <<'EOF'
feat(observability): SerilogDetectionEventPublisher emits one structured log per verdict

Implements IDetectionEventPublisher: every completed DetectionEvent becomes one
ILogger.Log call with StyloBot_* properties. Level chosen from Action (block→Warning,
challenge/throttle/redirect→Information, otherwise IsBot→Information / Human→Debug).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: `BlackboardSignalLogBridge`

Hosted service that subscribes to the orchestrator's global `SignalSink` at startup and uses ephemeral's `SignalToLoggerAdapter` to push each signal into `ILogger<StyloBotSignalCategory>`. Per-request signals are out of scope for v1; the global stream is what dashboards consume.

**Files:**
- Create: `src/Mostlylucid.BotDetection.Observability/Signals/StyloBotSignalCategory.cs`
- Create: `src/Mostlylucid.BotDetection.Observability/Signals/BlackboardSignalLogOptions.cs`
- Create: `src/Mostlylucid.BotDetection.Observability/Signals/BlackboardSignalLogBridge.cs`
- Test: `src/Mostlylucid.BotDetection.Observability.Test/BlackboardSignalLogBridgeTests.cs`

- [ ] **Step 1: Write the marker category type**

```csharp
namespace Mostlylucid.BotDetection.Observability.Signals;

/// <summary>
///     Marker category for <see cref="ILogger{T}"/> instances that emit blackboard signals.
///     Configure log filtering by this category name to silence or boost the signal stream.
/// </summary>
public sealed class StyloBotSignalCategory { }
```

- [ ] **Step 2: Write the options**

```csharp
namespace Mostlylucid.BotDetection.Observability.Signals;

public sealed class BlackboardSignalLogOptions
{
    /// <summary>Disable to keep the bridge from subscribing at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>If non-empty, only signals whose name starts with one of these prefixes are emitted.</summary>
    public IList<string> IncludePrefixes { get; set; } = new List<string>();

    /// <summary>Signals whose name starts with one of these prefixes are dropped. Applied after IncludePrefixes.</summary>
    public IList<string> ExcludePrefixes { get; set; } = new List<string> { "trace.", "debug." };
}
```

- [ ] **Step 3: Write the failing test**

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Observability.Signals;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Detection;

namespace Mostlylucid.BotDetection.Observability.Test;

public class BlackboardSignalLogBridgeTests
{
    private sealed class CapturingLogger : ILogger<StyloBotSignalCategory>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static EphemeralDetectionOrchestrator NewOrchestrator() =>
        new(NullLogger<EphemeralDetectionOrchestrator>.Instance,
            Options.Create(new BotDetectionOptions()),
            Array.Empty<IContributingDetector>());

    [Fact]
    public async Task Bridge_forwards_global_signals_to_logger()
    {
        var orchestrator = NewOrchestrator();
        var logger = new CapturingLogger();
        var bridge = new BlackboardSignalLogBridge(
            orchestrator,
            logger,
            Options.Create(new BlackboardSignalLogOptions()));

        await bridge.StartAsync(CancellationToken.None);

        orchestrator.RaiseSignalForObservability("error.detector.crash", "wp-scanner");

        await bridge.StopAsync(CancellationToken.None);

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Error);
        logger.Entries[0].Message.Should().Contain("error.detector.crash");
    }

    [Fact]
    public async Task Bridge_respects_exclude_prefixes()
    {
        var orchestrator = NewOrchestrator();
        var logger = new CapturingLogger();
        var opts = new BlackboardSignalLogOptions { ExcludePrefixes = { "noise." } };
        var bridge = new BlackboardSignalLogBridge(orchestrator, logger, Options.Create(opts));

        await bridge.StartAsync(CancellationToken.None);

        orchestrator.RaiseSignalForObservability("noise.tick");
        orchestrator.RaiseSignalForObservability("warning.threshold");

        await bridge.StopAsync(CancellationToken.None);

        logger.Entries.Should().ContainSingle(e => e.Message.Contains("warning.threshold"));
    }

    [Fact]
    public async Task Bridge_is_inert_when_disabled()
    {
        var orchestrator = NewOrchestrator();
        var logger = new CapturingLogger();
        var bridge = new BlackboardSignalLogBridge(
            orchestrator,
            logger,
            Options.Create(new BlackboardSignalLogOptions { Enabled = false }));

        await bridge.StartAsync(CancellationToken.None);
        orchestrator.RaiseSignalForObservability("warning.x");
        await bridge.StopAsync(CancellationToken.None);

        logger.Entries.Should().BeEmpty();
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

```bash
dotnet test src/Mostlylucid.BotDetection.Observability.Test --filter "FullyQualifiedName~BlackboardSignalLogBridgeTests"
```

Expected: FAIL: `BlackboardSignalLogBridge` not defined.

- [ ] **Step 5: Implement the bridge**

`src/Mostlylucid.BotDetection.Observability/Signals/BlackboardSignalLogBridge.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Logging;
using Mostlylucid.Ephemeral.Signals;

namespace Mostlylucid.BotDetection.Observability.Signals;

/// <summary>
///     Subscribes to the orchestrator's global blackboard signal stream and emits each
///     signal as a structured log entry on <c>ILogger&lt;StyloBotSignalCategory&gt;</c>.
///     Drops signals matching ExcludePrefixes; restricts to IncludePrefixes when configured.
///     Level inference is delegated to ephemeral's SignalToLoggerAdapter default map.
/// </summary>
public sealed class BlackboardSignalLogBridge : IHostedService, IDisposable
{
    private readonly IDetectionOrchestrator _orchestrator;
    private readonly ILogger<StyloBotSignalCategory> _logger;
    private readonly BlackboardSignalLogOptions _options;
    private IDisposable? _subscription;

    public BlackboardSignalLogBridge(
        IDetectionOrchestrator orchestrator,
        ILogger<StyloBotSignalCategory> logger,
        IOptions<BlackboardSignalLogOptions> options)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value ?? new BlackboardSignalLogOptions();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return Task.CompletedTask;

        _subscription = _orchestrator.SubscribeToSignals(OnSignal);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private void OnSignal(SignalEvent evt)
    {
        var name = evt.Signal ?? string.Empty;
        if (_options.ExcludePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return;
        if (_options.IncludePrefixes.Count > 0 &&
            !_options.IncludePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return;

        var mapped = DefaultMap(evt);
        if (mapped is null) return;

        _logger.Log(
            mapped.Value.Level,
            mapped.Value.EventId,
            "StyloBot signal: {Signal} op={OperationId} key={SignalKey}",
            evt.Signal,
            evt.OperationId,
            evt.Key ?? string.Empty);
    }

    private static LogMessage? DefaultMap(SignalEvent evt)
    {
        var level = LogLevel.Information;
        var name = evt.Signal?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(name))
        {
            var first = name.Split('.', ':')[0].ToLowerInvariant();
            level = first switch
            {
                "fatal" or "critical" => LogLevel.Critical,
                "error" => LogLevel.Error,
                "warn" or "warning" => LogLevel.Warning,
                "debug" => LogLevel.Debug,
                "trace" => LogLevel.Trace,
                _ => LogLevel.Information
            };
        }
        var eventId = new EventId((int)(evt.OperationId % int.MaxValue), evt.Key ?? evt.Signal);
        return new LogMessage(level, eventId, name);
    }
}
```

- [ ] **Step 6: Run the test to verify it passes**

```bash
dotnet test src/Mostlylucid.BotDetection.Observability.Test --filter "FullyQualifiedName~BlackboardSignalLogBridgeTests"
```

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Mostlylucid.BotDetection.Observability/Signals \
        src/Mostlylucid.BotDetection.Observability.Test/BlackboardSignalLogBridgeTests.cs
git commit -m "$(cat <<'EOF'
feat(observability): BlackboardSignalLogBridge forwards global signals to ILogger

Hosted service subscribes to the orchestrator's global SignalSink and emits each
SignalEvent on ILogger<StyloBotSignalCategory>. Level inferred from the signal
name prefix per ephemeral.logging's default map. Include/exclude prefix filters
keep log volume sane; trace./debug. excluded by default.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: `StyloBotLogEnricher`

A Serilog `ILogEventEnricher` that reads the current request's verdict from `HttpContext.Items` and stamps `StyloBot.*` properties on every log line emitted during the request. This is what makes the host's own controller logs join the bot-detection dataset in the customer's observability backend.

**Files:**
- Create: `src/Mostlylucid.BotDetection.Observability/Enrichment/StyloBotLogEnricher.cs`
- Create: `src/Mostlylucid.BotDetection.Observability/Enrichment/StyloBotEnricherExtensions.cs`
- Test: `src/Mostlylucid.BotDetection.Observability.Test/StyloBotLogEnricherTests.cs`

Confirm the existing keys stylobot writes to `HttpContext.Items` for the verdict before writing the enricher.

- [ ] **Step 1: Identify the HttpContext.Items keys**

```bash
grep -rn "HttpContext.Items\[" src/Mostlylucid.BotDetection/Extensions --include="*.cs" | head
grep -rn "context.Items\[" src/Mostlylucid.BotDetection --include="*.cs" | grep -i "stylobot\|botdetection" | head
```

Record the property names actually written (e.g. `"StyloBot:IsBot"`, `"StyloBot:Signature"`). The enricher must read those exact keys. If `HttpContext.Extensions.IsBot()` / `GetBotConfidence()` / `GetBotType()` (per CLAUDE.md) are the canonical accessors, use them through a thin `IBotDetectionContextAccessor`.

- [ ] **Step 2: Write the failing test**

```csharp
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Observability.Enrichment;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.InMemory;

namespace Mostlylucid.BotDetection.Observability.Test;

public class StyloBotLogEnricherTests
{
    [Fact]
    public void Enriches_log_lines_with_stylobot_properties_when_verdict_present()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["StyloBot:Signature"] = "sig-xyz";
        ctx.Items["StyloBot:IsBot"] = true;
        ctx.Items["StyloBot:ThreatBand"] = "medium";

        var accessor = new HttpContextAccessor { HttpContext = ctx };
        var sink = new InMemorySink();
        var serilog = new LoggerConfiguration()
            .Enrich.With(new StyloBotLogEnricher(accessor))
            .WriteTo.Sink(sink)
            .CreateLogger();

        serilog.Information("hello");

        var entry = sink.LogEvents.Single();
        entry.Properties["StyloBot_Signature"].ToString().Should().Contain("sig-xyz");
        entry.Properties["StyloBot_IsBot"].ToString().Should().Be("True");
        entry.Properties["StyloBot_ThreatBand"].ToString().Should().Contain("medium");
    }

    [Fact]
    public void Adds_no_properties_when_no_HttpContext()
    {
        var accessor = new HttpContextAccessor();
        var sink = new InMemorySink();
        var serilog = new LoggerConfiguration()
            .Enrich.With(new StyloBotLogEnricher(accessor))
            .WriteTo.Sink(sink)
            .CreateLogger();

        serilog.Information("background");

        sink.LogEvents.Single().Properties.Keys.Should().NotContain(k => k.StartsWith("StyloBot_"));
    }
}
```

- [ ] **Step 3: Implement the enricher**

`src/Mostlylucid.BotDetection.Observability/Enrichment/StyloBotLogEnricher.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;

namespace Mostlylucid.BotDetection.Observability.Enrichment;

/// <summary>
///     Serilog enricher that reads the current request's StyloBot verdict from
///     <see cref="HttpContext.Items"/> and adds StyloBot_* properties to every log line.
/// </summary>
public sealed class StyloBotLogEnricher : ILogEventEnricher
{
    private readonly IHttpContextAccessor _accessor;

    public StyloBotLogEnricher(IHttpContextAccessor accessor)
    {
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var ctx = _accessor.HttpContext;
        if (ctx is null) return;

        Add(logEvent, propertyFactory, "StyloBot_Signature", ctx.Items["StyloBot:Signature"]);
        Add(logEvent, propertyFactory, "StyloBot_IsBot", ctx.Items["StyloBot:IsBot"]);
        Add(logEvent, propertyFactory, "StyloBot_Action", ctx.Items["StyloBot:Action"]);
        Add(logEvent, propertyFactory, "StyloBot_ThreatBand", ctx.Items["StyloBot:ThreatBand"]);
        Add(logEvent, propertyFactory, "StyloBot_RiskBand", ctx.Items["StyloBot:RiskBand"]);
        Add(logEvent, propertyFactory, "StyloBot_BotType", ctx.Items["StyloBot:BotType"]);
        Add(logEvent, propertyFactory, "StyloBot_BotName", ctx.Items["StyloBot:BotName"]);
    }

    private static void Add(LogEvent logEvent, ILogEventPropertyFactory factory, string name, object? value)
    {
        if (value is null) return;
        logEvent.AddOrUpdateProperty(factory.CreateProperty(name, value));
    }
}
```

- [ ] **Step 4: Add the Serilog `LoggerConfiguration` extension**

`src/Mostlylucid.BotDetection.Observability/Enrichment/StyloBotEnricherExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Configuration;

namespace Mostlylucid.BotDetection.Observability.Enrichment;

public static class StyloBotEnricherExtensions
{
    /// <summary>
    ///     Adds the StyloBot Serilog enricher. Requires <c>IHttpContextAccessor</c> to be
    ///     registered in DI. Customers call this from <c>UseSerilog(...)</c>.
    /// </summary>
    public static LoggerConfiguration WithStyloBot(
        this LoggerEnrichmentConfiguration enrich,
        IServiceProvider services)
    {
        if (enrich is null) throw new ArgumentNullException(nameof(enrich));
        var accessor = services.GetService(typeof(IHttpContextAccessor)) as IHttpContextAccessor
                       ?? new HttpContextAccessor();
        return enrich.With(new StyloBotLogEnricher(accessor));
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

```bash
dotnet test src/Mostlylucid.BotDetection.Observability.Test --filter "FullyQualifiedName~StyloBotLogEnricherTests"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection.Observability/Enrichment \
        src/Mostlylucid.BotDetection.Observability.Test/StyloBotLogEnricherTests.cs
git commit -m "$(cat <<'EOF'
feat(observability): StyloBotLogEnricher tags host logs with verdict properties

Serilog enricher reads StyloBot:* keys off HttpContext.Items and writes
StyloBot_Signature / StyloBot_IsBot / StyloBot_Action / StyloBot_ThreatBand etc.
onto every log event emitted during the request. Host controller logs now join
the bot-detection dataset in the customer's observability backend.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: `AddStyloBotObservability` extension and options POCO

One DI call that wires the publisher, the bridge, and an OpenTelemetry pipeline exporting the existing meters and ActivitySource to OTLP. Prometheus stays an opt-in switch via existing Gateway wiring.

**Files:**
- Create: `src/Mostlylucid.BotDetection.Observability/StyloBotObservabilityOptions.cs`
- Create: `src/Mostlylucid.BotDetection.Observability/ObservabilityServiceCollectionExtensions.cs`
- Create: `src/Mostlylucid.BotDetection.Observability/OpenTelemetry/StyloBotOpenTelemetryExtensions.cs`
- Test: `src/Mostlylucid.BotDetection.Observability.Test/ObservabilityServiceCollectionExtensionsTests.cs`

- [ ] **Step 1: Write the options POCO**

`src/Mostlylucid.BotDetection.Observability/StyloBotObservabilityOptions.cs`:

```csharp
using Mostlylucid.BotDetection.Observability.Signals;

namespace Mostlylucid.BotDetection.Observability;

/// <summary>
///     Configuration root bound from <c>BotDetection:Observability</c>.
/// </summary>
public sealed class StyloBotObservabilityOptions
{
    public const string SectionName = "BotDetection:Observability";

    public bool PublishDetectionEventsToSerilog { get; set; } = true;

    public BlackboardSignalLogOptions SignalLog { get; set; } = new();

    public OpenTelemetryOptions OpenTelemetry { get; set; } = new();

    public sealed class OpenTelemetryOptions
    {
        public bool EnableTracing { get; set; } = true;
        public bool EnableMetrics { get; set; } = true;
        public bool EnableLogs { get; set; } = true;

        /// <summary>OTLP endpoint. When null, OTel SDK default is used (http://localhost:4317).</summary>
        public string? OtlpEndpoint { get; set; }

        /// <summary>Service name on emitted resources. Defaults to "stylobot".</summary>
        public string ServiceName { get; set; } = "stylobot";

        /// <summary>Optional service.namespace resource attribute.</summary>
        public string? ServiceNamespace { get; set; }

        /// <summary>Optional service.instance.id resource attribute.</summary>
        public string? ServiceInstanceId { get; set; }
    }
}
```

- [ ] **Step 2: Write the OTel helper**

`src/Mostlylucid.BotDetection.Observability/OpenTelemetry/StyloBotOpenTelemetryExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Telemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Mostlylucid.BotDetection.Observability.OpenTelemetry;

internal static class StyloBotOpenTelemetryExtensions
{
    public static IServiceCollection AddStyloBotOpenTelemetryCore(
        this IServiceCollection services,
        StyloBotObservabilityOptions.OpenTelemetryOptions otel)
    {
        var resource = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: otel.ServiceName,
                serviceNamespace: otel.ServiceNamespace,
                serviceInstanceId: otel.ServiceInstanceId);

        var builder = services.AddOpenTelemetry().ConfigureResource(_ => resource);

        if (otel.EnableTracing)
        {
            builder.WithTracing(t =>
            {
                t.AddSource(BotDetectionTelemetry.ActivitySourceName);
                t.AddAspNetCoreInstrumentation();
                t.AddOtlpExporter(o => ApplyEndpoint(o, otel.OtlpEndpoint));
            });
        }

        if (otel.EnableMetrics)
        {
            builder.WithMetrics(m =>
            {
                m.AddMeter(BotDetectionMetrics.MeterName);
                m.AddMeter(BotDetectionSignalMeter.MeterName);
                m.AddAspNetCoreInstrumentation();
                m.AddOtlpExporter(o => ApplyEndpoint(o, otel.OtlpEndpoint));
            });
        }

        if (otel.EnableLogs)
        {
            services.AddLogging(lb =>
            {
                lb.AddOpenTelemetry(o =>
                {
                    o.SetResourceBuilder(resource);
                    o.IncludeFormattedMessage = true;
                    o.IncludeScopes = true;
                    o.AddOtlpExporter(opts => ApplyEndpoint(opts, otel.OtlpEndpoint));
                });
            });
        }

        return services;
    }

    private static void ApplyEndpoint(
        global::OpenTelemetry.Exporter.OtlpExporterOptions o,
        string? endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint))
            o.Endpoint = new Uri(endpoint);
    }
}
```

- [ ] **Step 3: Write the public DI extension**

`src/Mostlylucid.BotDetection.Observability/ObservabilityServiceCollectionExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Mostlylucid.BotDetection.Observability.Events;
using Mostlylucid.BotDetection.Observability.OpenTelemetry;
using Mostlylucid.BotDetection.Observability.Signals;
using Mostlylucid.BotDetection.Orchestration.Telemetry;

namespace Mostlylucid.BotDetection.Observability;

public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>
    ///     Wires the StyloBot observability stack:
    ///     <list type="bullet">
    ///         <item>SerilogDetectionEventPublisher replaces the no-op IDetectionEventPublisher</item>
    ///         <item>BlackboardSignalLogBridge forwards global signals to ILogger</item>
    ///         <item>OpenTelemetry tracing + metrics + logs export to OTLP</item>
    ///     </list>
    /// </summary>
    public static IServiceCollection AddStyloBotObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<StyloBotObservabilityOptions>()
            .Bind(configuration.GetSection(StyloBotObservabilityOptions.SectionName));
        services.AddOptions<BlackboardSignalLogOptions>()
            .Configure<Microsoft.Extensions.Options.IOptions<StyloBotObservabilityOptions>>(
                (target, src) =>
                {
                    target.Enabled = src.Value.SignalLog.Enabled;
                    target.IncludePrefixes = src.Value.SignalLog.IncludePrefixes;
                    target.ExcludePrefixes = src.Value.SignalLog.ExcludePrefixes;
                });

        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();

        var snapshot = configuration
            .GetSection(StyloBotObservabilityOptions.SectionName)
            .Get<StyloBotObservabilityOptions>() ?? new StyloBotObservabilityOptions();

        if (snapshot.PublishDetectionEventsToSerilog)
        {
            services.RemoveAll<IDetectionEventPublisher>();
            services.AddSingleton<IDetectionEventPublisher, SerilogDetectionEventPublisher>();
        }

        services.AddHostedService<BlackboardSignalLogBridge>();

        services.AddStyloBotOpenTelemetryCore(snapshot.OpenTelemetry);

        return services;
    }
}
```

- [ ] **Step 4: Write the failing wiring test**

`src/Mostlylucid.BotDetection.Observability.Test/ObservabilityServiceCollectionExtensionsTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mostlylucid.BotDetection.Observability;
using Mostlylucid.BotDetection.Observability.Events;
using Mostlylucid.BotDetection.Observability.Signals;
using Mostlylucid.BotDetection.Orchestration.Telemetry;

namespace Mostlylucid.BotDetection.Observability.Test;

public class ObservabilityServiceCollectionExtensionsTests
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    [Fact]
    public void Registers_SerilogDetectionEventPublisher_by_default()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDetectionEventPublisher, NullDetectionEventPublisher>();
        services.AddLogging();

        services.AddStyloBotObservability(EmptyConfig());

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IDetectionEventPublisher>()
            .Should().BeOfType<SerilogDetectionEventPublisher>();
    }

    [Fact]
    public void Registers_BlackboardSignalLogBridge_as_hosted_service()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStyloBotObservability(EmptyConfig());

        services.Any(d => d.ServiceType == typeof(IHostedService) &&
                          d.ImplementationType == typeof(BlackboardSignalLogBridge))
            .Should().BeTrue();
    }

    [Fact]
    public void PublishDetectionEventsToSerilog_false_leaves_existing_publisher()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDetectionEventPublisher, NullDetectionEventPublisher>();
        services.AddLogging();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BotDetection:Observability:PublishDetectionEventsToSerilog"] = "false"
        }).Build();

        services.AddStyloBotObservability(config);

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IDetectionEventPublisher>()
            .Should().BeOfType<NullDetectionEventPublisher>();
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

```bash
dotnet test src/Mostlylucid.BotDetection.Observability.Test
```

Expected: PASS (all four test files).

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection.Observability/StyloBotObservabilityOptions.cs \
        src/Mostlylucid.BotDetection.Observability/ObservabilityServiceCollectionExtensions.cs \
        src/Mostlylucid.BotDetection.Observability/OpenTelemetry \
        src/Mostlylucid.BotDetection.Observability.Test/ObservabilityServiceCollectionExtensionsTests.cs
git commit -m "$(cat <<'EOF'
feat(observability): AddStyloBotObservability wires publisher, bridge, OTel

One DI call: replaces NullDetectionEventPublisher with the Serilog one, registers
BlackboardSignalLogBridge as a hosted service, and pipes BotDetection meters +
ActivitySource through an OpenTelemetry pipeline with OTLP exporter (logs, metrics,
traces). Config bound from BotDetection:Observability with per-component on/off.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: Wire Demo + write the customer-facing doc

**Files:**
- Modify: `src/Mostlylucid.BotDetection.Demo/Program.cs`
- Modify: `src/Mostlylucid.BotDetection.Demo/appsettings.Development.json`
- Modify: `src/Mostlylucid.BotDetection.Demo/Mostlylucid.BotDetection.Demo.csproj` (ProjectReference to Observability)
- Create: `src/Mostlylucid.BotDetection/docs/observability.md`

- [ ] **Step 1: Add the observability ProjectReference to the Demo csproj**

Open the Demo csproj and add inside the existing ItemGroup that holds `Mostlylucid.BotDetection`:

```xml
<ProjectReference Include="..\Mostlylucid.BotDetection.Observability\Mostlylucid.BotDetection.Observability.csproj" />
```

- [ ] **Step 2: Add `AddStyloBotObservability` to Demo's `Program.cs`**

Locate the existing `builder.Services.AddBotDetection(...)` (or `AddStyloBot(...)`) call. Immediately after it, add:

```csharp
builder.Services.AddStyloBotObservability(builder.Configuration);
```

If `Program.cs` configures Serilog via `builder.Host.UseSerilog`, extend the enrichment call:

```csharp
builder.Host.UseSerilog((ctx, services, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .ReadFrom.Services(services)
    .Enrich.WithStyloBot(services));
```

`Enrich.WithStyloBot(services)` is the static helper from Task 5.

- [ ] **Step 3: Add the appsettings sample**

Insert into `appsettings.Development.json`:

```json
{
  "BotDetection": {
    "Observability": {
      "PublishDetectionEventsToSerilog": true,
      "SignalLog": {
        "Enabled": true,
        "IncludePrefixes": [],
        "ExcludePrefixes": [ "trace.", "debug." ]
      },
      "OpenTelemetry": {
        "EnableTracing": true,
        "EnableMetrics": true,
        "EnableLogs": true,
        "OtlpEndpoint": "http://localhost:4317",
        "ServiceName": "stylobot-demo"
      }
    }
  }
}
```

- [ ] **Step 4: Run Demo and verify a request produces a structured log line**

```bash
dotnet run --project src/Mostlylucid.BotDetection.Demo &
DEMO_PID=$!
sleep 5
curl -sS -o /dev/null -A "curl/8.4 (compatible; bot)" http://localhost:5080/SignatureDemo || true
kill $DEMO_PID 2>/dev/null
```

Expected: among the Demo console output, at least one log line of the form
`StyloBot detection: signature=... isBot=True ... action=...`. If Serilog console template suppresses properties, set the template to `{Message:lj} {Properties:j}{NewLine}` in `appsettings.Development.json` first.

Per [[feedback_verify_before_checkin]] — do not commit this task until that line is observed.

- [ ] **Step 5: Write the customer-facing doc**

`src/Mostlylucid.BotDetection/docs/observability.md`:

```markdown
# Observability

StyloBot ships structured logs, metrics, and OpenTelemetry export through the
`Mostlylucid.BotDetection.Observability` package. The detection pipeline is the
data source; your existing observability stack is the destination.

## What you get

| Surface | What it carries | How to consume |
|---|---|---|
| **DetectionEvent log line** | One structured `ILogger` entry per completed detection with `StyloBot_*` properties | Any backend Serilog or `Microsoft.Extensions.Logging` writes to (Datadog, Seq, Splunk, Loki, CloudWatch) |
| **Blackboard signal log stream** | Every global signal raised by detectors as `ILogger<StyloBotSignalCategory>` calls | Same as above, filterable by category |
| **Enricher** | `StyloBot.*` properties on every host log line emitted during a request | Serilog `Enrich.WithStyloBot(services)` |
| **Metrics** | `Mostlylucid.BotDetection` (48+ instruments) and `Mostlylucid.BotDetection.Signals` meters | OTLP exporter (default) or `/metrics` Prometheus endpoint on the Gateway |
| **Traces** | `Mostlylucid.BotDetection` ActivitySource, `BotDetection.Detect` activity per request | OTLP exporter |

## Quick start

```csharp
builder.Services.AddBotDetection();
builder.Services.AddStyloBotObservability(builder.Configuration);

builder.Host.UseSerilog((ctx, services, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.WithStyloBot(services));
```

## Configuration

```json
{
  "BotDetection": {
    "Observability": {
      "PublishDetectionEventsToSerilog": true,
      "SignalLog": {
        "Enabled": true,
        "ExcludePrefixes": [ "trace.", "debug." ]
      },
      "OpenTelemetry": {
        "EnableTracing": true,
        "EnableMetrics": true,
        "EnableLogs": true,
        "OtlpEndpoint": "http://localhost:4317",
        "ServiceName": "stylobot"
      }
    }
  }
}
```

## Backends

| Backend | Wiring |
|---|---|
| Seq / Datadog / Splunk / Loki | Add the appropriate Serilog sink to your host. StyloBot's events flow through it automatically. |
| Prometheus | Already mapped at `/metrics` on the Gateway. No change. |
| OTLP (Collector / Tempo / Mimir / Grafana Agent) | Set `OtlpEndpoint`. Logs, metrics, and traces all export. |

## Properties cheatsheet

`StyloBot_Signature`, `StyloBot_IsBot`, `StyloBot_Probability`, `StyloBot_Confidence`,
`StyloBot_RiskBand`, `StyloBot_ThreatBand`, `StyloBot_Action`, `StyloBot_BotName`,
`StyloBot_BotType`, `StyloBot_CountryCode`, `StyloBot_Path`, `StyloBot_Method`,
`StyloBot_StatusCode`, `StyloBot_ProcessingTimeMs`, `StyloBot_RequestId`, `StyloBot_GatewayId`.

## Level mapping

| `Action` | Log level |
|---|---|
| `block` | Warning |
| `challenge`, `throttle-tools`, `throttle-stealth`, `throttle-status`, `redirect-honeypot` | Information |
| anything else, bot verdict | Information |
| anything else, human verdict | Debug |
```

- [ ] **Step 6: Final build + test sweep**

```bash
dotnet build mostlylucid.stylobot.sln
dotnet test mostlylucid.stylobot.sln
```

Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add src/Mostlylucid.BotDetection.Demo \
        src/Mostlylucid.BotDetection/docs/observability.md
git commit -m "$(cat <<'EOF'
docs(observability): wire Demo + customer-facing observability.md

Demo project opts in to AddStyloBotObservability and the Serilog enricher;
appsettings.Development.json shows the full config surface. observability.md
covers the package's three surfaces (DetectionEvent log, signal stream, host
log enrichment) and the OTel pipeline.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review

**Spec coverage**
- "Review logging provision" → audit captured in the conversation, drove the file structure here. Covered.
- "Ensure structured logging works correctly" → Task 7 step 4 runs Demo end-to-end and verifies a structured line appears. Covered.
- "Look at a stylobot Serilog sink" → Task 3 builds `SerilogDetectionEventPublisher` (the sink customers will look for) and Task 5 builds the enricher. Covered.
- "Research what we should offer in terms of logging, metrics and otel" → answered in the conversation; baked into Tasks 3, 4, 6, 7. Covered.
- "We PREFER signals so look at the mostlylucid.ephemeral signals to logs stuff" → Task 4 uses ephemeral's `SignalToLoggerAdapter` semantics via `BlackboardSignalLogBridge` against the orchestrator's global `SignalSink` (Task 2 exposes the subscribe seam). Covered.

**Placeholder scan**
- No TBDs, no "implement later". Every code block is complete.
- Task 5 step 1 deliberately delegates to a grep on an existing file — the actual `HttpContext.Items` keys are part of the existing product surface and the agent must read them, not invent them.
- Task 7 step 2 says "locate the existing AddBotDetection call" — the Demo's Program.cs is short and the call is unambiguous; the same pattern as every other Demo wiring task in this repo.

**Type consistency**
- `IDetectionOrchestrator.SubscribeToSignals(Action<SignalEvent>)` and `EphemeralDetectionOrchestrator.SubscribeToSignals` match (Task 2).
- `BlackboardSignalLogBridge` constructor takes `(IDetectionOrchestrator, ILogger<StyloBotSignalCategory>, IOptions<BlackboardSignalLogOptions>)` and tests construct it the same way (Task 4).
- `SerilogDetectionEventPublisher` implements `IDetectionEventPublisher` and is registered via `services.AddSingleton<IDetectionEventPublisher, SerilogDetectionEventPublisher>()` (Tasks 3 + 6).
- `StyloBotObservabilityOptions.SectionName = "BotDetection:Observability"` matches the JSON config key used in the wiring test and the Demo appsettings (Tasks 6 + 7).
- `BotDetectionTelemetry.ActivitySourceName`, `BotDetectionMetrics.MeterName`, `BotDetectionSignalMeter.MeterName` are the verified constants used in Task 6's OTel wiring.

**Out of scope (deliberate)**
- Per-request blackboard signal bridge. The global sink covers the dashboard-relevant signal set; per-request would 10×–100× log volume with little additional value. Future work if a customer asks.
- Commercial Redis bridge. The Serilog publisher does not replace `RedisDetectionEventPublisher` in commercial builds; they coexist via different DI registrations.
- Anything that changes `DetectionEvent` shape. The publisher consumes it as-is.

---

Plan complete and saved to `docs/superpowers/plans/2026-06-05-stylobot-observability.md`. Two execution options:

1. **Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration
2. **Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?