# StyloBot Public API & Node SDK Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a canonical REST API for StyloBot detection, a proxy header injection middleware, and a Node.js SDK as the first client.

**Architecture:** New .NET project `Mostlylucid.BotDetection.Api` exposes versioned endpoints at `/api/v1/*`. Detection requests are bridged to the existing orchestrator via synthetic `HttpContext`. Two npm packages (`@stylobot/core` for types+client, `@stylobot/node` for Express/Fastify middleware) consume the API. Gateway injects `X-StyloBot-*` headers for zero-latency proxy mode.

**Tech Stack:** .NET 10 minimal APIs, ASP.NET Core OpenAPI, xUnit, TypeScript, npm workspaces, Express, Fastify

**Spec:** `docs/superpowers/specs/2026-04-21-public-api-node-sdk-design.md`

---

## File Map

### .NET - `Mostlylucid.BotDetection.Api/`

| File | Responsibility |
|------|---------------|
| `Mostlylucid.BotDetection.Api.csproj` | Project file, references `Mostlylucid.BotDetection` and `Mostlylucid.BotDetection.UI` |
| `Models/DetectRequest.cs` | Canonical detection request DTO |
| `Models/DetectResponse.cs` | Canonical detection response DTO (Verdict, Reasons, Signals, Meta) |
| `Models/PaginatedResponse.cs` | Generic paginated envelope |
| `Models/ApiResponses.cs` | DTOs for read endpoints (summary, timeseries, etc.) |
| `Bridge/SyntheticHttpContext.cs` | Builds `DefaultHttpContext` from `DetectRequest` |
| `Endpoints/DetectEndpoints.cs` | `POST /api/v1/detect` and `/detect/batch` |
| `Endpoints/ReadEndpoints.cs` | All `GET /api/v1/*` read endpoints |
| `Endpoints/ManagementEndpoints.cs` | Tier 3 key/policy management |
| `Endpoints/MeEndpoints.cs` | `GET /api/v1/me` |
| `Auth/ApiKeyAuthenticationHandler.cs` | ASP.NET Core auth handler wrapping existing `IApiKeyStore` |
| `Middleware/ResponseHeaderInjectionMiddleware.cs` | Injects `X-StyloBot-*` headers after detection |
| `StyloBotApiExtensions.cs` | `AddStyloBotApi()` / `MapStyloBotApi()` entry points |

### .NET - Tests

| File | Responsibility |
|------|---------------|
| `Mostlylucid.BotDetection.Api.Tests/Mostlylucid.BotDetection.Api.Tests.csproj` | Test project |
| `Mostlylucid.BotDetection.Api.Tests/Models/DetectRequestTests.cs` | Request validation |
| `Mostlylucid.BotDetection.Api.Tests/Bridge/SyntheticHttpContextTests.cs` | Bridge correctness |
| `Mostlylucid.BotDetection.Api.Tests/Endpoints/DetectEndpointsTests.cs` | Detection endpoint integration tests |
| `Mostlylucid.BotDetection.Api.Tests/Endpoints/ReadEndpointsTests.cs` | Read endpoint tests |
| `Mostlylucid.BotDetection.Api.Tests/Auth/ApiKeyAuthTests.cs` | Auth handler tests |
| `Mostlylucid.BotDetection.Api.Tests/Middleware/ResponseHeaderTests.cs` | Header injection tests |

### Node - `sdk/node/`

| File | Responsibility |
|------|---------------|
| `sdk/node/package.json` | Workspace root |
| `sdk/node/packages/core/package.json` | `@stylobot/core` package |
| `sdk/node/packages/core/src/index.ts` | Main export |
| `sdk/node/packages/core/src/types.ts` | All TypeScript types matching API contract |
| `sdk/node/packages/core/src/client.ts` | `StyloBotClient` class |
| `sdk/node/packages/core/src/headers.ts` | Header constants and parser |
| `sdk/node/packages/core/src/__tests__/client.test.ts` | Client tests |
| `sdk/node/packages/core/src/__tests__/headers.test.ts` | Header parser tests |
| `sdk/node/packages/node/package.json` | `@stylobot/node` package |
| `sdk/node/packages/node/src/index.ts` | Main export |
| `sdk/node/packages/node/src/middleware.ts` | Express middleware |
| `sdk/node/packages/node/src/fastify.ts` | Fastify plugin |
| `sdk/node/packages/node/src/extract.ts` | Request extraction from Node `IncomingMessage` |
| `sdk/node/packages/node/src/__tests__/middleware.test.ts` | Express middleware tests |
| `sdk/node/packages/node/src/__tests__/fastify.test.ts` | Fastify plugin tests |
| `sdk/node/packages/node/src/__tests__/extract.test.ts` | Extractor tests |

---

## Task 1: Create the .NET Api project and solution wiring

**Files:**
- Create: `Mostlylucid.BotDetection.Api/Mostlylucid.BotDetection.Api.csproj`
- Modify: `mostlylucid.stylobot.sln`

- [ ] **Step 1: Create the project directory**

```bash
mkdir -p Mostlylucid.BotDetection.Api
```

- [ ] **Step 2: Create the .csproj file**

Create `Mostlylucid.BotDetection.Api/Mostlylucid.BotDetection.Api.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFrameworks>net10.0</TargetFrameworks>
        <LangVersion>preview</LangVersion>
        <Nullable>enable</Nullable>
        <ImplicitUsings>enable</ImplicitUsings>
        <IsPackable>true</IsPackable>
        <PackageId>Mostlylucid.BotDetection.Api</PackageId>
        <MinVerTagPrefix>botdetection-v</MinVerTagPrefix>
        <Authors>Mostlylucid</Authors>
        <Description>StyloBot Public REST API for bot detection and dashboard data</Description>
    </PropertyGroup>

    <ItemGroup>
        <FrameworkReference Include="Microsoft.AspNetCore.App"/>
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\Mostlylucid.BotDetection\Mostlylucid.BotDetection.csproj"/>
        <ProjectReference Include="..\Mostlylucid.BotDetection.UI\Mostlylucid.BotDetection.UI.csproj"/>
    </ItemGroup>

</Project>
```

- [ ] **Step 3: Add project to solution**

```bash
dotnet sln mostlylucid.stylobot.sln add Mostlylucid.BotDetection.Api/Mostlylucid.BotDetection.Api.csproj
```

- [ ] **Step 4: Verify it builds**

```bash
dotnet build Mostlylucid.BotDetection.Api/Mostlylucid.BotDetection.Api.csproj
```

Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection.Api/ mostlylucid.stylobot.sln
git commit -m "Add Mostlylucid.BotDetection.Api project skeleton"
```

---

## Task 2: API request and response models

**Files:**
- Create: `Mostlylucid.BotDetection.Api/Models/DetectRequest.cs`
- Create: `Mostlylucid.BotDetection.Api/Models/DetectResponse.cs`
- Create: `Mostlylucid.BotDetection.Api/Models/PaginatedResponse.cs`

- [ ] **Step 1: Create DetectRequest model**

Create `Mostlylucid.BotDetection.Api/Models/DetectRequest.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace Mostlylucid.BotDetection.Api.Models;

/// <summary>
///     Canonical detection request. SDKs extract request metadata from their
///     framework and POST this shape to /api/v1/detect.
/// </summary>
public sealed record DetectRequest
{
    /// <summary>HTTP method (GET, POST, etc.)</summary>
    [Required]
    public required string Method { get; init; }

    /// <summary>Request path (e.g., /products/123)</summary>
    [Required]
    public required string Path { get; init; }

    /// <summary>HTTP headers as flat key-value pairs.</summary>
    [Required]
    public required Dictionary<string, string> Headers { get; init; }

    /// <summary>Client IP address.</summary>
    [Required]
    public required string RemoteIp { get; init; }

    /// <summary>Request protocol (http or https).</summary>
    public string Protocol { get; init; } = "https";

    /// <summary>Optional TLS metadata (only available when caller terminates TLS).</summary>
    public TlsInfo? Tls { get; init; }
}

/// <summary>
///     TLS connection metadata for fingerprint detectors.
/// </summary>
public sealed record TlsInfo
{
    /// <summary>TLS protocol version (e.g., TLSv1.3).</summary>
    public string? Version { get; init; }

    /// <summary>Cipher suite name.</summary>
    public string? Cipher { get; init; }

    /// <summary>JA3 fingerprint hash.</summary>
    public string? Ja3 { get; init; }

    /// <summary>JA4 fingerprint hash.</summary>
    public string? Ja4 { get; init; }
}
```

- [ ] **Step 2: Create DetectResponse model**

Create `Mostlylucid.BotDetection.Api/Models/DetectResponse.cs`:

```csharp
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Api.Models;

/// <summary>
///     Canonical detection response returned by POST /api/v1/detect.
/// </summary>
public sealed record DetectResponse
{
    public required VerdictDto Verdict { get; init; }
    public required IReadOnlyList<ReasonDto> Reasons { get; init; }
    public required IReadOnlyDictionary<string, object> Signals { get; init; }
    public required MetaDto Meta { get; init; }

    /// <summary>
    ///     Map AggregatedEvidence from the orchestrator to the public API response.
    /// </summary>
    public static DetectResponse FromEvidence(AggregatedEvidence evidence)
    {
        var recommendedAction = evidence.RiskBand switch
        {
            RiskBand.High or RiskBand.VeryHigh => RecommendedAction.Block,
            RiskBand.Medium => RecommendedAction.Challenge,
            RiskBand.Elevated => RecommendedAction.Throttle,
            _ => RecommendedAction.Allow
        };

        return new DetectResponse
        {
            Verdict = new VerdictDto
            {
                IsBot = evidence.BotProbability >= 0.7,
                BotProbability = Math.Round(evidence.BotProbability, 4),
                Confidence = Math.Round(evidence.Confidence, 4),
                BotType = evidence.PrimaryBotType?.ToString(),
                BotName = evidence.PrimaryBotName,
                RiskBand = evidence.RiskBand.ToString(),
                RecommendedAction = recommendedAction.ToString(),
                ThreatScore = Math.Round(evidence.ThreatScore, 4),
                ThreatBand = evidence.ThreatBand.ToString()
            },
            Reasons = evidence.Contributions
                .Where(c => Math.Abs(c.ConfidenceDelta) > 0.01)
                .Select(c => new ReasonDto
                {
                    Detector = c.DetectorName,
                    Detail = c.Reason ?? c.DetectorName,
                    Impact = Math.Round(c.ConfidenceDelta, 4)
                })
                .ToList(),
            Signals = evidence.Signals,
            Meta = new MetaDto
            {
                ProcessingTimeMs = Math.Round(evidence.TotalProcessingTimeMs, 2),
                DetectorsRun = evidence.ContributingDetectors.Count,
                PolicyName = evidence.PolicyName,
                AiRan = evidence.AiRan
            }
        };
    }
}

public sealed record VerdictDto
{
    public required bool IsBot { get; init; }
    public required double BotProbability { get; init; }
    public required double Confidence { get; init; }
    public string? BotType { get; init; }
    public string? BotName { get; init; }
    public required string RiskBand { get; init; }
    public required string RecommendedAction { get; init; }
    public required double ThreatScore { get; init; }
    public required string ThreatBand { get; init; }
}

public sealed record ReasonDto
{
    public required string Detector { get; init; }
    public required string Detail { get; init; }
    public required double Impact { get; init; }
}

public sealed record MetaDto
{
    public required double ProcessingTimeMs { get; init; }
    public required int DetectorsRun { get; init; }
    public string? PolicyName { get; init; }
    public required bool AiRan { get; init; }
    public string? RequestId { get; init; }
}
```

- [ ] **Step 3: Create PaginatedResponse model**

Create `Mostlylucid.BotDetection.Api/Models/PaginatedResponse.cs`:

```csharp
namespace Mostlylucid.BotDetection.Api.Models;

/// <summary>
///     Standard paginated envelope for all list endpoints.
/// </summary>
public sealed record PaginatedResponse<T>
{
    public required IReadOnlyList<T> Data { get; init; }
    public required PaginationInfo Pagination { get; init; }
    public required ResponseMeta Meta { get; init; }
}

/// <summary>
///     Single-resource envelope for detail endpoints.
/// </summary>
public sealed record SingleResponse<T>
{
    public required T Data { get; init; }
    public required ResponseMeta Meta { get; init; }
}

public sealed record PaginationInfo
{
    public required int Offset { get; init; }
    public required int Limit { get; init; }
    public required int Total { get; init; }
}

public sealed record ResponseMeta
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}
```

- [ ] **Step 4: Verify it builds**

```bash
dotnet build Mostlylucid.BotDetection.Api/Mostlylucid.BotDetection.Api.csproj
```

Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection.Api/Models/
git commit -m "Add public API request/response models"
```

---

## Task 3: Synthetic HttpContext bridge

**Files:**
- Create: `Mostlylucid.BotDetection.Api/Bridge/SyntheticHttpContext.cs`
- Create: `Mostlylucid.BotDetection.Api.Tests/Mostlylucid.BotDetection.Api.Tests.csproj`
- Create: `Mostlylucid.BotDetection.Api.Tests/Bridge/SyntheticHttpContextTests.cs`

- [ ] **Step 1: Create the test project**

Create `Mostlylucid.BotDetection.Api.Tests/Mostlylucid.BotDetection.Api.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <LangVersion>preview</LangVersion>
        <Nullable>enable</Nullable>
        <ImplicitUsings>enable</ImplicitUsings>
        <IsPackable>false</IsPackable>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*"/>
        <PackageReference Include="xunit" Version="2.*"/>
        <PackageReference Include="xunit.runner.visualstudio" Version="2.*"/>
        <PackageReference Include="Moq" Version="4.*"/>
        <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.*-*"/>
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\Mostlylucid.BotDetection.Api\Mostlylucid.BotDetection.Api.csproj"/>
    </ItemGroup>

</Project>
```

Add to solution:

```bash
dotnet sln mostlylucid.stylobot.sln add Mostlylucid.BotDetection.Api.Tests/Mostlylucid.BotDetection.Api.Tests.csproj
```

- [ ] **Step 2: Write failing tests for SyntheticHttpContext**

Create `Mostlylucid.BotDetection.Api.Tests/Bridge/SyntheticHttpContextTests.cs`:

```csharp
using System.Net;
using Mostlylucid.BotDetection.Api.Bridge;
using Mostlylucid.BotDetection.Api.Models;

namespace Mostlylucid.BotDetection.Api.Tests.Bridge;

public class SyntheticHttpContextTests
{
    [Fact]
    public void FromDetectRequest_SetsRemoteIpAddress()
    {
        var request = CreateRequest(remoteIp: "203.0.113.42");

        var context = SyntheticHttpContext.FromDetectRequest(request);

        Assert.Equal(IPAddress.Parse("203.0.113.42"), context.Connection.RemoteIpAddress);
    }

    [Fact]
    public void FromDetectRequest_SetsMethod()
    {
        var request = CreateRequest(method: "POST");

        var context = SyntheticHttpContext.FromDetectRequest(request);

        Assert.Equal("POST", context.Request.Method);
    }

    [Fact]
    public void FromDetectRequest_SetsPath()
    {
        var request = CreateRequest(path: "/products/123");

        var context = SyntheticHttpContext.FromDetectRequest(request);

        Assert.Equal("/products/123", context.Request.Path.Value);
    }

    [Fact]
    public void FromDetectRequest_SetsScheme()
    {
        var request = CreateRequest(protocol: "http");

        var context = SyntheticHttpContext.FromDetectRequest(request);

        Assert.Equal("http", context.Request.Scheme);
    }

    [Fact]
    public void FromDetectRequest_CopiesHeaders()
    {
        var request = CreateRequest(headers: new Dictionary<string, string>
        {
            ["user-agent"] = "Mozilla/5.0 Test",
            ["accept-language"] = "en-US"
        });

        var context = SyntheticHttpContext.FromDetectRequest(request);

        Assert.Equal("Mozilla/5.0 Test", context.Request.Headers.UserAgent.ToString());
        Assert.Equal("en-US", context.Request.Headers.AcceptLanguage.ToString());
    }

    [Fact]
    public void FromDetectRequest_StoresTlsInfoInItems()
    {
        var request = CreateRequest(tls: new TlsInfo
        {
            Version = "TLSv1.3",
            Cipher = "TLS_AES_256_GCM_SHA384",
            Ja3 = "abc123"
        });

        var context = SyntheticHttpContext.FromDetectRequest(request);

        var tlsInfo = context.Items["BotDetection.TlsInfo"] as TlsInfo;
        Assert.NotNull(tlsInfo);
        Assert.Equal("TLSv1.3", tlsInfo.Version);
        Assert.Equal("abc123", tlsInfo.Ja3);
    }

    [Fact]
    public void FromDetectRequest_WithNullTls_DoesNotStoreTlsInfo()
    {
        var request = CreateRequest(tls: null);

        var context = SyntheticHttpContext.FromDetectRequest(request);

        Assert.False(context.Items.ContainsKey("BotDetection.TlsInfo"));
    }

    [Fact]
    public void FromDetectRequest_SetsTraceIdentifier()
    {
        var request = CreateRequest();

        var context = SyntheticHttpContext.FromDetectRequest(request);

        Assert.False(string.IsNullOrEmpty(context.TraceIdentifier));
    }

    [Fact]
    public void FromDetectRequest_WithQueryString_SetsPathAndQuery()
    {
        var request = CreateRequest(path: "/search?q=test&page=2");

        var context = SyntheticHttpContext.FromDetectRequest(request);

        Assert.Equal("/search", context.Request.Path.Value);
        Assert.Equal("?q=test&page=2", context.Request.QueryString.Value);
    }

    private static DetectRequest CreateRequest(
        string method = "GET",
        string path = "/",
        Dictionary<string, string>? headers = null,
        string remoteIp = "127.0.0.1",
        string protocol = "https",
        TlsInfo? tls = null) =>
        new()
        {
            Method = method,
            Path = path,
            Headers = headers ?? new Dictionary<string, string>
            {
                ["user-agent"] = "Mozilla/5.0 (Test)"
            },
            RemoteIp = remoteIp,
            Protocol = protocol,
            Tls = tls
        };
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
dotnet test Mostlylucid.BotDetection.Api.Tests/ --filter "SyntheticHttpContextTests" -v m
```

Expected: FAIL - `SyntheticHttpContext` class doesn't exist.

- [ ] **Step 4: Implement SyntheticHttpContext**

Create `Mostlylucid.BotDetection.Api/Bridge/SyntheticHttpContext.cs`:

```csharp
using System.Net;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Api.Models;

namespace Mostlylucid.BotDetection.Api.Bridge;

/// <summary>
///     Builds a synthetic HttpContext from a DetectRequest so the existing
///     BlackboardOrchestrator can run detection without a real HTTP pipeline.
/// </summary>
public static class SyntheticHttpContext
{
    public static HttpContext FromDetectRequest(DetectRequest request)
    {
        var context = new DefaultHttpContext();

        // Parse path and query string
        var pathAndQuery = request.Path;
        var queryIndex = pathAndQuery.IndexOf('?');
        if (queryIndex >= 0)
        {
            context.Request.Path = pathAndQuery[..queryIndex];
            context.Request.QueryString = new QueryString(pathAndQuery[queryIndex..]);
        }
        else
        {
            context.Request.Path = pathAndQuery;
        }

        context.Request.Method = request.Method;
        context.Request.Scheme = request.Protocol;

        // Copy headers
        foreach (var (key, value) in request.Headers)
        {
            context.Request.Headers[key] = value;
        }

        // Set remote IP
        if (IPAddress.TryParse(request.RemoteIp, out var ip))
        {
            context.Connection.RemoteIpAddress = ip;
        }

        // Store TLS info for fingerprint detectors
        if (request.Tls is not null)
        {
            context.Items["BotDetection.TlsInfo"] = request.Tls;
        }

        // Generate a trace identifier for correlation
        context.TraceIdentifier = Guid.NewGuid().ToString("N")[..12];

        return context;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test Mostlylucid.BotDetection.Api.Tests/ --filter "SyntheticHttpContextTests" -v m
```

Expected: All 8 tests PASS.

- [ ] **Step 6: Commit**

```bash
git add Mostlylucid.BotDetection.Api/Bridge/ Mostlylucid.BotDetection.Api.Tests/ mostlylucid.stylobot.sln
git commit -m "Add SyntheticHttpContext bridge with tests"
```

---

## Task 4: API Key authentication handler

**Files:**
- Create: `Mostlylucid.BotDetection.Api/Auth/ApiKeyAuthenticationHandler.cs`
- Create: `Mostlylucid.BotDetection.Api.Tests/Auth/ApiKeyAuthTests.cs`

- [ ] **Step 1: Write failing tests**

Create `Mostlylucid.BotDetection.Api.Tests/Auth/ApiKeyAuthTests.cs`:

```csharp
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Api.Tests.Auth;

public class ApiKeyAuthTests
{
    private const string ValidKey = "SB-TEST-KEY";
    private const string Scheme = ApiKeyAuthenticationHandler.SchemeName;

    [Fact]
    public async Task ValidKey_ReturnsSuccess()
    {
        var handler = CreateHandler(validKey: ValidKey);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-SB-Api-Key"] = ValidKey;

        await handler.InitializeAsync(
            new AuthenticationScheme(Scheme, null, typeof(ApiKeyAuthenticationHandler)),
            context);
        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("SB-TEST-KEY", result.Principal!.FindFirst("api_key_name")!.Value);
    }

    [Fact]
    public async Task MissingHeader_ReturnsNoResult()
    {
        var handler = CreateHandler(validKey: ValidKey);
        var context = new DefaultHttpContext();

        await handler.InitializeAsync(
            new AuthenticationScheme(Scheme, null, typeof(ApiKeyAuthenticationHandler)),
            context);
        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    [Fact]
    public async Task InvalidKey_ReturnsFail()
    {
        var handler = CreateHandler(validKey: ValidKey);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-SB-Api-Key"] = "SB-WRONG-KEY";

        await handler.InitializeAsync(
            new AuthenticationScheme(Scheme, null, typeof(ApiKeyAuthenticationHandler)),
            context);
        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.False(result.None);
    }

    private static ApiKeyAuthenticationHandler CreateHandler(string validKey)
    {
        var store = new Mock<IApiKeyStore>();
        store.Setup(s => s.ValidateKeyWithReason(validKey, It.IsAny<string>()))
            .Returns((
                new ApiKeyValidationResult
                {
                    KeyId = validKey,
                    Context = new Mostlylucid.BotDetection.Models.ApiKeyContext
                    {
                        KeyName = validKey,
                        DisabledDetectors = [],
                        WeightOverrides = new Dictionary<string, double>()
                    }
                },
                (ApiKeyRejection?)null));
        store.Setup(s => s.ValidateKeyWithReason(It.Is<string>(k => k != validKey), It.IsAny<string>()))
            .Returns((
                (ApiKeyValidationResult?)null,
                new ApiKeyRejection(ApiKeyRejectionReason.NotFound)));

        var options = new Mock<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Setup(o => o.Get(It.IsAny<string>())).Returns(new AuthenticationSchemeOptions());

        var loggerFactory = NullLoggerFactory.Instance;

        return new ApiKeyAuthenticationHandler(
            store.Object,
            options.Object,
            loggerFactory,
            UrlEncoder.Default);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test Mostlylucid.BotDetection.Api.Tests/ --filter "ApiKeyAuthTests" -v m
```

Expected: FAIL - `ApiKeyAuthenticationHandler` doesn't exist.

- [ ] **Step 3: Implement the auth handler**

Create `Mostlylucid.BotDetection.Api/Auth/ApiKeyAuthenticationHandler.cs`:

```csharp
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Api.Auth;

/// <summary>
///     ASP.NET Core authentication handler that validates X-SB-Api-Key headers
///     using the existing IApiKeyStore infrastructure.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "StyloBotApiKey";
    private const string HeaderName = "X-SB-Api-Key";

    private readonly IApiKeyStore _apiKeyStore;

    public ApiKeyAuthenticationHandler(
        IApiKeyStore apiKeyStore,
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
        _apiKeyStore = apiKeyStore;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var apiKeyHeader))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var apiKey = apiKeyHeader.ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var (result, rejection) = _apiKeyStore.ValidateKeyWithReason(apiKey, Request.Path.Value ?? "/");

        if (result is null)
        {
            var reason = rejection?.Reason.ToString() ?? "Unknown";
            return Task.FromResult(AuthenticateResult.Fail($"API key rejected: {reason}"));
        }

        var claims = new[]
        {
            new Claim("api_key_name", result.Context.KeyName),
            new Claim("api_key_id", result.KeyId),
            new Claim(ClaimTypes.AuthenticationMethod, SchemeName)
        };

        foreach (var tag in result.Context.Tags)
        {
            claims = [..claims, new Claim("api_key_tag", tag)];
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        // Store the API key context so the detection pipeline can read it
        Context.Items["BotDetection.ApiKeyContext"] = result.Context;

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test Mostlylucid.BotDetection.Api.Tests/ --filter "ApiKeyAuthTests" -v m
```

Expected: All 3 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection.Api/Auth/ Mostlylucid.BotDetection.Api.Tests/Auth/
git commit -m "Add API key authentication handler with tests"
```

---

## Task 5: Detection endpoint

**Files:**
- Create: `Mostlylucid.BotDetection.Api/Endpoints/DetectEndpoints.cs`
- Create: `Mostlylucid.BotDetection.Api.Tests/Endpoints/DetectEndpointsTests.cs`

- [ ] **Step 1: Write failing tests**

Create `Mostlylucid.BotDetection.Api.Tests/Endpoints/DetectEndpointsTests.cs`:

```csharp
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Api.Tests.Endpoints;

public class DetectEndpointsTests
{
    [Fact]
    public void DetectResponse_FromEvidence_MapsVerdictCorrectly()
    {
        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.92,
            Confidence = 0.87,
            RiskBand = RiskBand.High,
            PrimaryBotType = Mostlylucid.BotDetection.Models.BotType.Scraper,
            PrimaryBotName = "GPTBot",
            ThreatScore = 0.15,
            ThreatBand = ThreatBand.Low,
            TotalProcessingTimeMs = 4.2,
            ContributingDetectors = new HashSet<string> { "UserAgent", "Header", "Ip" },
            PolicyName = "default",
            AiRan = false,
            Signals = new Dictionary<string, object>
            {
                ["ua.isBot"] = true,
                ["ip.isDatacenter"] = true
            }
        };

        var response = DetectResponse.FromEvidence(evidence);

        Assert.True(response.Verdict.IsBot);
        Assert.Equal(0.92, response.Verdict.BotProbability);
        Assert.Equal(0.87, response.Verdict.Confidence);
        Assert.Equal("Scraper", response.Verdict.BotType);
        Assert.Equal("GPTBot", response.Verdict.BotName);
        Assert.Equal("High", response.Verdict.RiskBand);
        Assert.Equal("Block", response.Verdict.RecommendedAction);
        Assert.Equal(0.15, response.Verdict.ThreatScore);
        Assert.Equal("Low", response.Verdict.ThreatBand);
        Assert.Equal(3, response.Meta.DetectorsRun);
        Assert.Equal("default", response.Meta.PolicyName);
        Assert.False(response.Meta.AiRan);
        Assert.True((bool)response.Signals["ua.isBot"]);
    }

    [Fact]
    public void DetectResponse_FromEvidence_HumanVerdict()
    {
        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.12,
            Confidence = 0.95,
            RiskBand = RiskBand.VeryLow,
            ThreatScore = 0.0,
            ThreatBand = ThreatBand.None,
            TotalProcessingTimeMs = 2.1,
            ContributingDetectors = new HashSet<string> { "UserAgent" },
            Signals = new Dictionary<string, object>()
        };

        var response = DetectResponse.FromEvidence(evidence);

        Assert.False(response.Verdict.IsBot);
        Assert.Equal("Allow", response.Verdict.RecommendedAction);
        Assert.Null(response.Verdict.BotType);
    }

    [Theory]
    [InlineData(RiskBand.High, "Block")]
    [InlineData(RiskBand.VeryHigh, "Block")]
    [InlineData(RiskBand.Medium, "Challenge")]
    [InlineData(RiskBand.Elevated, "Throttle")]
    [InlineData(RiskBand.Low, "Allow")]
    [InlineData(RiskBand.VeryLow, "Allow")]
    [InlineData(RiskBand.Unknown, "Allow")]
    public void DetectResponse_FromEvidence_MapsRiskBandToAction(RiskBand riskBand, string expectedAction)
    {
        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.5,
            Confidence = 0.5,
            RiskBand = riskBand,
            ThreatScore = 0,
            ThreatBand = ThreatBand.None,
            TotalProcessingTimeMs = 1,
            ContributingDetectors = new HashSet<string>(),
            Signals = new Dictionary<string, object>()
        };

        var response = DetectResponse.FromEvidence(evidence);

        Assert.Equal(expectedAction, response.Verdict.RecommendedAction);
    }
}
```

- [ ] **Step 2: Run tests to verify they pass (models already exist)**

```bash
dotnet test Mostlylucid.BotDetection.Api.Tests/ --filter "DetectEndpointsTests" -v m
```

Expected: All tests PASS (these test the model mapping, not the HTTP layer).

- [ ] **Step 3: Implement DetectEndpoints**

Create `Mostlylucid.BotDetection.Api/Endpoints/DetectEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Bridge;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class DetectEndpoints
{
    public static IEndpointRouteBuilder MapDetectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Detection");

        group.MapPost("/detect", HandleDetect)
            .WithName("Detect")
            .WithOpenApi();

        group.MapPost("/detect/batch", HandleDetectBatch)
            .WithName("DetectBatch")
            .WithOpenApi();

        return endpoints;
    }

    private static async Task<IResult> HandleDetect(
        DetectRequest request,
        IBlackboardOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var httpContext = SyntheticHttpContext.FromDetectRequest(request);
        var evidence = await orchestrator.DetectAsync(httpContext, cancellationToken);
        var response = DetectResponse.FromEvidence(evidence);
        return Results.Ok(response);
    }

    private static async Task<IResult> HandleDetectBatch(
        DetectRequest[] requests,
        IBlackboardOrchestrator orchestrator,
        StyloBotApiOptions apiOptions,
        CancellationToken cancellationToken)
    {
        if (requests.Length > apiOptions.MaxBatchSize)
        {
            return Results.Problem(
                title: "Batch too large",
                detail: $"Maximum batch size is {apiOptions.MaxBatchSize}, got {requests.Length}",
                statusCode: 400,
                type: "https://stylo.bot/errors/batch-too-large");
        }

        var responses = new DetectResponse[requests.Length];
        for (var i = 0; i < requests.Length; i++)
        {
            var httpContext = SyntheticHttpContext.FromDetectRequest(requests[i]);
            var evidence = await orchestrator.DetectAsync(httpContext, cancellationToken);
            responses[i] = DetectResponse.FromEvidence(evidence);
        }

        return Results.Ok(responses);
    }
}
```

- [ ] **Step 4: Commit**

```bash
git add Mostlylucid.BotDetection.Api/Endpoints/DetectEndpoints.cs Mostlylucid.BotDetection.Api.Tests/Endpoints/
git commit -m "Add detect and detect/batch endpoints with mapping tests"
```

---

## Task 6: Read endpoints

**Files:**
- Create: `Mostlylucid.BotDetection.Api/Endpoints/ReadEndpoints.cs`
- Create: `Mostlylucid.BotDetection.Api/Endpoints/MeEndpoints.cs`

- [ ] **Step 1: Implement ReadEndpoints**

Create `Mostlylucid.BotDetection.Api/Endpoints/ReadEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class ReadEndpoints
{
    public static IEndpointRouteBuilder MapReadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Dashboard Data");

        group.MapGet("/detections", HandleDetections).WithName("GetDetections").WithOpenApi();
        group.MapGet("/signatures", HandleSignatures).WithName("GetSignatures").WithOpenApi();
        group.MapGet("/summary", HandleSummary).WithName("GetSummary").WithOpenApi();
        group.MapGet("/timeseries", HandleTimeseries).WithName("GetTimeseries").WithOpenApi();
        group.MapGet("/countries", HandleCountries).WithName("GetCountries").WithOpenApi();
        group.MapGet("/countries/{code}", HandleCountryDetail).WithName("GetCountryDetail").WithOpenApi();
        group.MapGet("/endpoints", HandleEndpoints).WithName("GetEndpoints").WithOpenApi();
        group.MapGet("/endpoints/{method}/{**path}", HandleEndpointDetail).WithName("GetEndpointDetail").WithOpenApi();
        group.MapGet("/topbots", HandleTopBots).WithName("GetTopBots").WithOpenApi();
        group.MapGet("/threats", HandleThreats).WithName("GetThreats").WithOpenApi();

        return endpoints;
    }

    private static async Task<IResult> HandleDetections(
        IDashboardEventStore store,
        int limit = 50,
        int offset = 0,
        bool? isBot = null,
        DateTime? since = null)
    {
        var filter = new DashboardFilter
        {
            Limit = Math.Min(limit, 200),
            Offset = offset,
            IsBot = isBot,
            StartTime = since
        };
        var detections = await store.GetDetectionsAsync(filter);
        return Results.Ok(new PaginatedResponse<object>
        {
            Data = detections.Cast<object>().ToList(),
            Pagination = new PaginationInfo { Offset = offset, Limit = limit, Total = detections.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<IResult> HandleSignatures(
        IDashboardEventStore store,
        int limit = 100,
        int offset = 0,
        bool? isBot = null)
    {
        var signatures = await store.GetSignaturesAsync(limit, offset, isBot);
        return Results.Ok(new PaginatedResponse<object>
        {
            Data = signatures.Cast<object>().ToList(),
            Pagination = new PaginationInfo { Offset = offset, Limit = limit, Total = signatures.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<IResult> HandleSummary(IDashboardEventStore store)
    {
        var summary = await store.GetSummaryAsync();
        return Results.Ok(new SingleResponse<object>
        {
            Data = summary,
            Meta = new ResponseMeta()
        });
    }

    private static async Task<IResult> HandleTimeseries(
        IDashboardEventStore store,
        string interval = "5m",
        DateTime? since = null,
        DateTime? until = null)
    {
        var bucketSize = interval switch
        {
            "1m" => TimeSpan.FromMinutes(1),
            "5m" => TimeSpan.FromMinutes(5),
            "15m" => TimeSpan.FromMinutes(15),
            "1h" => TimeSpan.FromHours(1),
            _ => TimeSpan.FromMinutes(5)
        };
        var start = since ?? DateTime.UtcNow.AddHours(-24);
        var end = until ?? DateTime.UtcNow;
        var timeseries = await store.GetTimeSeriesAsync(start, end, bucketSize);
        return Results.Ok(new PaginatedResponse<object>
        {
            Data = timeseries.Cast<object>().ToList(),
            Pagination = new PaginationInfo { Offset = 0, Limit = timeseries.Count, Total = timeseries.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<IResult> HandleCountries(
        IDashboardEventStore store,
        int limit = 20,
        DateTime? since = null,
        DateTime? until = null)
    {
        var countries = await store.GetCountryStatsAsync(limit, since, until);
        return Results.Ok(new PaginatedResponse<object>
        {
            Data = countries.Cast<object>().ToList(),
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = countries.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<IResult> HandleCountryDetail(
        string code,
        IDashboardEventStore store,
        DateTime? since = null,
        DateTime? until = null)
    {
        var detail = await store.GetCountryDetailAsync(code, since, until);
        if (detail is null) return Results.NotFound();
        return Results.Ok(new SingleResponse<object>
        {
            Data = detail,
            Meta = new ResponseMeta()
        });
    }

    private static async Task<IResult> HandleEndpoints(
        IDashboardEventStore store,
        int limit = 50,
        DateTime? since = null,
        DateTime? until = null)
    {
        var eps = await store.GetEndpointStatsAsync(limit, since, until);
        return Results.Ok(new PaginatedResponse<object>
        {
            Data = eps.Cast<object>().ToList(),
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = eps.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<IResult> HandleEndpointDetail(
        string method,
        string path,
        IDashboardEventStore store,
        DateTime? since = null,
        DateTime? until = null)
    {
        var detail = await store.GetEndpointDetailAsync(method, "/" + path, since, until);
        if (detail is null) return Results.NotFound();
        return Results.Ok(new SingleResponse<object>
        {
            Data = detail,
            Meta = new ResponseMeta()
        });
    }

    private static async Task<IResult> HandleTopBots(
        IDashboardEventStore store,
        int limit = 10,
        DateTime? since = null,
        DateTime? until = null)
    {
        var bots = await store.GetTopBotsAsync(limit, since, until);
        return Results.Ok(new PaginatedResponse<object>
        {
            Data = bots.Cast<object>().ToList(),
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = bots.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<IResult> HandleThreats(
        IDashboardEventStore store,
        int limit = 20,
        DateTime? since = null,
        DateTime? until = null)
    {
        var threats = await store.GetThreatsAsync(limit, since, until);
        return Results.Ok(new PaginatedResponse<object>
        {
            Data = threats.Cast<object>().ToList(),
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = threats.Count },
            Meta = new ResponseMeta()
        });
    }
}
```

- [ ] **Step 2: Implement MeEndpoints**

Create `Mostlylucid.BotDetection.Api/Endpoints/MeEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/me", HandleMe)
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithName("GetMe")
            .WithTags("Account")
            .WithOpenApi();

        return endpoints;
    }

    private static IResult HandleMe(HttpContext httpContext)
    {
        var keyContext = httpContext.Items["BotDetection.ApiKeyContext"] as ApiKeyContext;
        if (keyContext is null)
        {
            return Results.Problem(
                title: "No API key context",
                statusCode: 401,
                type: "https://stylo.bot/errors/no-api-key");
        }

        return Results.Ok(new SingleResponse<object>
        {
            Data = new
            {
                keyContext.KeyName,
                keyContext.DisabledDetectors,
                keyContext.WeightOverrides,
                keyContext.DetectionPolicyName,
                keyContext.ActionPolicyName,
                keyContext.Tags,
                keyContext.DisablesAllDetectors
            },
            Meta = new ResponseMeta()
        });
    }
}
```

- [ ] **Step 3: Verify it builds**

```bash
dotnet build Mostlylucid.BotDetection.Api/Mostlylucid.BotDetection.Api.csproj
```

Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add Mostlylucid.BotDetection.Api/Endpoints/
git commit -m "Add read and me endpoints for dashboard data"
```

---

## Task 7: Response header injection middleware

**Files:**
- Create: `Mostlylucid.BotDetection.Api/Middleware/ResponseHeaderInjectionMiddleware.cs`
- Create: `Mostlylucid.BotDetection.Api.Tests/Middleware/ResponseHeaderTests.cs`

- [ ] **Step 1: Write failing tests**

Create `Mostlylucid.BotDetection.Api.Tests/Middleware/ResponseHeaderTests.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Api.Middleware;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Api.Tests.Middleware;

public class ResponseHeaderTests
{
    [Fact]
    public void InjectHeaders_WritesAllExpectedHeaders()
    {
        var context = new DefaultHttpContext();
        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.92,
            Confidence = 0.87,
            RiskBand = RiskBand.High,
            PrimaryBotType = Mostlylucid.BotDetection.Models.BotType.Scraper,
            PrimaryBotName = "GPTBot",
            ThreatScore = 0.15,
            ThreatBand = ThreatBand.Low,
            PolicyName = "default",
            TotalProcessingTimeMs = 4,
            ContributingDetectors = new HashSet<string>(),
            Signals = new Dictionary<string, object>()
        };
        context.Items["BotDetection.AggregatedEvidence"] = evidence;

        ResponseHeaderInjection.InjectHeaders(context);

        Assert.Equal("true", context.Response.Headers["X-StyloBot-IsBot"].ToString());
        Assert.Equal("0.92", context.Response.Headers["X-StyloBot-Probability"].ToString());
        Assert.Equal("0.87", context.Response.Headers["X-StyloBot-Confidence"].ToString());
        Assert.Equal("Scraper", context.Response.Headers["X-StyloBot-BotType"].ToString());
        Assert.Equal("GPTBot", context.Response.Headers["X-StyloBot-BotName"].ToString());
        Assert.Equal("High", context.Response.Headers["X-StyloBot-RiskBand"].ToString());
        Assert.Equal("Block", context.Response.Headers["X-StyloBot-Action"].ToString());
        Assert.Equal("0.15", context.Response.Headers["X-StyloBot-ThreatScore"].ToString());
        Assert.Equal("Low", context.Response.Headers["X-StyloBot-ThreatBand"].ToString());
        Assert.Equal("default", context.Response.Headers["X-StyloBot-Policy"].ToString());
    }

    [Fact]
    public void InjectHeaders_NoEvidence_NoHeaders()
    {
        var context = new DefaultHttpContext();

        ResponseHeaderInjection.InjectHeaders(context);

        Assert.False(context.Response.Headers.ContainsKey("X-StyloBot-IsBot"));
    }

    [Fact]
    public void InjectHeaders_HumanVerdict_IsBotFalse()
    {
        var context = new DefaultHttpContext();
        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.12,
            Confidence = 0.95,
            RiskBand = RiskBand.VeryLow,
            ThreatScore = 0,
            ThreatBand = ThreatBand.None,
            TotalProcessingTimeMs = 2,
            ContributingDetectors = new HashSet<string>(),
            Signals = new Dictionary<string, object>()
        };
        context.Items["BotDetection.AggregatedEvidence"] = evidence;

        ResponseHeaderInjection.InjectHeaders(context);

        Assert.Equal("false", context.Response.Headers["X-StyloBot-IsBot"].ToString());
        Assert.Equal("Allow", context.Response.Headers["X-StyloBot-Action"].ToString());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test Mostlylucid.BotDetection.Api.Tests/ --filter "ResponseHeaderTests" -v m
```

Expected: FAIL - `ResponseHeaderInjection` doesn't exist.

- [ ] **Step 3: Implement the middleware**

Create `Mostlylucid.BotDetection.Api/Middleware/ResponseHeaderInjectionMiddleware.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Api.Middleware;

/// <summary>
///     Injects X-StyloBot-* headers into responses after bot detection runs.
///     Used by Gateway to pass detection results to upstream Node/Python apps.
/// </summary>
public class ResponseHeaderInjectionMiddleware
{
    private readonly RequestDelegate _next;

    public ResponseHeaderInjectionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Register a callback that fires just before response headers are sent
        context.Response.OnStarting(() =>
        {
            ResponseHeaderInjection.InjectHeaders(context);
            return Task.CompletedTask;
        });

        await _next(context);
    }
}

/// <summary>
///     Static helper for header injection - testable without middleware pipeline.
/// </summary>
public static class ResponseHeaderInjection
{
    private const double BotThreshold = 0.7;

    public static void InjectHeaders(HttpContext context)
    {
        if (!context.Items.TryGetValue("BotDetection.AggregatedEvidence", out var evidenceObj)
            || evidenceObj is not AggregatedEvidence evidence)
        {
            return;
        }

        var isBot = evidence.BotProbability >= BotThreshold;
        var action = evidence.RiskBand switch
        {
            RiskBand.High or RiskBand.VeryHigh => "Block",
            RiskBand.Medium => "Challenge",
            RiskBand.Elevated => "Throttle",
            _ => "Allow"
        };

        var headers = context.Response.Headers;
        headers["X-StyloBot-IsBot"] = isBot.ToString().ToLowerInvariant();
        headers["X-StyloBot-Probability"] = evidence.BotProbability.ToString("F2");
        headers["X-StyloBot-Confidence"] = evidence.Confidence.ToString("F2");
        headers["X-StyloBot-BotType"] = evidence.PrimaryBotType?.ToString() ?? "";
        headers["X-StyloBot-BotName"] = evidence.PrimaryBotName ?? "";
        headers["X-StyloBot-RiskBand"] = evidence.RiskBand.ToString();
        headers["X-StyloBot-Action"] = action;
        headers["X-StyloBot-ThreatScore"] = evidence.ThreatScore.ToString("F2");
        headers["X-StyloBot-ThreatBand"] = evidence.ThreatBand.ToString();
        headers["X-StyloBot-Policy"] = evidence.PolicyName ?? "";
        headers["X-StyloBot-RequestId"] = context.TraceIdentifier;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test Mostlylucid.BotDetection.Api.Tests/ --filter "ResponseHeaderTests" -v m
```

Expected: All 3 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection.Api/Middleware/ Mostlylucid.BotDetection.Api.Tests/Middleware/
git commit -m "Add X-StyloBot-* response header injection middleware with tests"
```

---

## Task 8: Extension methods and options (entry points)

**Files:**
- Create: `Mostlylucid.BotDetection.Api/StyloBotApiExtensions.cs`
- Create: `Mostlylucid.BotDetection.Api/StyloBotApiOptions.cs`

- [ ] **Step 1: Create the options class**

Create `Mostlylucid.BotDetection.Api/StyloBotApiOptions.cs`:

```csharp
namespace Mostlylucid.BotDetection.Api;

/// <summary>
///     Configuration for the StyloBot Public API.
/// </summary>
public class StyloBotApiOptions
{
    /// <summary>Enable Tier 3 management endpoints (requires OIDC). Default false.</summary>
    public bool EnableManagementEndpoints { get; set; }

    /// <summary>Enable OpenAPI spec at /api/v1/openapi.json. Default true.</summary>
    public bool EnableOpenApi { get; set; } = true;

    /// <summary>Maximum batch size for POST /api/v1/detect/batch. Default 100.</summary>
    public int MaxBatchSize { get; set; } = 100;

    /// <summary>Inject X-StyloBot-* headers into responses. Default false.</summary>
    public bool InjectResponseHeaders { get; set; }

    /// <summary>Inject X-StyloBot-* headers into forwarded requests (YARP). Default false.</summary>
    public bool ForwardRequestHeaders { get; set; }
}
```

- [ ] **Step 2: Create the extension methods**

Create `Mostlylucid.BotDetection.Api/StyloBotApiExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Endpoints;
using Mostlylucid.BotDetection.Api.Middleware;

namespace Mostlylucid.BotDetection.Api;

public static class StyloBotApiExtensions
{
    /// <summary>
    ///     Register StyloBot Public API services.
    ///     Call this after AddBotDetection() / AddStyloBot().
    /// </summary>
    public static IServiceCollection AddStyloBotApi(
        this IServiceCollection services,
        Action<StyloBotApiOptions>? configure = null)
    {
        var options = new StyloBotApiOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Register the API key auth scheme
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, null);

        if (options.EnableOpenApi)
        {
            services.AddOpenApi();
        }

        return services;
    }

    /// <summary>
    ///     Map StyloBot Public API endpoints.
    ///     Call this after UseRouting() and UseAuthentication().
    /// </summary>
    public static IEndpointRouteBuilder MapStyloBotApi(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<StyloBotApiOptions>();

        endpoints.MapDetectEndpoints();
        endpoints.MapReadEndpoints();
        endpoints.MapMeEndpoints();

        if (options.EnableOpenApi)
        {
            endpoints.MapOpenApi("/api/v1/openapi.json");
        }

        return endpoints;
    }

    /// <summary>
    ///     Add the X-StyloBot-* response header injection middleware.
    ///     Call this after UseBotDetection().
    /// </summary>
    public static IApplicationBuilder UseStyloBotResponseHeaders(this IApplicationBuilder app)
    {
        return app.UseMiddleware<ResponseHeaderInjectionMiddleware>();
    }
}
```

- [ ] **Step 3: Verify the full project builds**

```bash
dotnet build Mostlylucid.BotDetection.Api/Mostlylucid.BotDetection.Api.csproj
```

Expected: Build succeeded.

- [ ] **Step 4: Run all tests**

```bash
dotnet test Mostlylucid.BotDetection.Api.Tests/ -v m
```

Expected: All tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection.Api/StyloBotApiExtensions.cs Mostlylucid.BotDetection.Api/StyloBotApiOptions.cs
git commit -m "Add AddStyloBotApi/MapStyloBotApi entry points and options"
```

---

## Task 9: Wire API into Demo project for testing

**Files:**
- Modify: `Mostlylucid.BotDetection.Demo/Mostlylucid.BotDetection.Demo.csproj`
- Modify: `Mostlylucid.BotDetection.Demo/Program.cs`

- [ ] **Step 1: Add project reference to Demo**

Add to `Mostlylucid.BotDetection.Demo/Mostlylucid.BotDetection.Demo.csproj`:

```xml
<ProjectReference Include="..\Mostlylucid.BotDetection.Api\Mostlylucid.BotDetection.Api.csproj"/>
```

- [ ] **Step 2: Wire up the API in Demo's Program.cs**

Add after existing `AddStyloBot` registration:

```csharp
builder.Services.AddStyloBotApi(options =>
{
    options.InjectResponseHeaders = true;
    options.EnableOpenApi = true;
});
```

Add after existing `UseStyloBot()`:

```csharp
app.UseStyloBotResponseHeaders();
```

Add after existing `MapControllers()` or route registration:

```csharp
app.MapStyloBotApi();
```

- [ ] **Step 3: Build and verify**

```bash
dotnet build Mostlylucid.BotDetection.Demo/Mostlylucid.BotDetection.Demo.csproj
```

Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add Mostlylucid.BotDetection.Demo/
git commit -m "Wire StyloBot Public API into Demo project"
```

---

## Task 10: Node SDK workspace and @stylobot/core types

**Files:**
- Create: `sdk/node/package.json`
- Create: `sdk/node/tsconfig.json`
- Create: `sdk/node/packages/core/package.json`
- Create: `sdk/node/packages/core/tsconfig.json`
- Create: `sdk/node/packages/core/src/types.ts`
- Create: `sdk/node/packages/core/src/headers.ts`
- Create: `sdk/node/packages/core/src/index.ts`

- [ ] **Step 1: Create workspace root**

Create `sdk/node/package.json`:

```json
{
  "private": true,
  "workspaces": [
    "packages/core",
    "packages/node"
  ],
  "scripts": {
    "build": "npm run build --workspaces",
    "test": "npm test --workspaces",
    "clean": "rm -rf packages/*/dist"
  }
}
```

Create `sdk/node/tsconfig.json`:

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "Node16",
    "moduleResolution": "Node16",
    "strict": true,
    "esModuleInterop": true,
    "declaration": true,
    "declarationMap": true,
    "sourceMap": true,
    "outDir": "dist",
    "rootDir": "src",
    "skipLibCheck": true
  }
}
```

- [ ] **Step 2: Create @stylobot/core package**

Create `sdk/node/packages/core/package.json`:

```json
{
  "name": "@stylobot/core",
  "version": "0.1.0",
  "description": "StyloBot SDK core - types, client, and header parser",
  "type": "module",
  "main": "dist/index.js",
  "types": "dist/index.d.ts",
  "files": ["dist"],
  "scripts": {
    "build": "tsc",
    "test": "node --test src/__tests__/*.test.ts",
    "clean": "rm -rf dist"
  },
  "devDependencies": {
    "typescript": "^5.8.0",
    "@types/node": "^22.0.0"
  },
  "license": "MIT",
  "repository": {
    "type": "git",
    "url": "https://github.com/stylobot/stylobot",
    "directory": "sdk/node/packages/core"
  }
}
```

Create `sdk/node/packages/core/tsconfig.json`:

```json
{
  "extends": "../../tsconfig.json",
  "compilerOptions": {
    "outDir": "dist",
    "rootDir": "src"
  },
  "include": ["src/**/*.ts"],
  "exclude": ["src/__tests__"]
}
```

- [ ] **Step 3: Create types.ts**

Create `sdk/node/packages/core/src/types.ts`:

```typescript
// ============================================================
// Enums (string unions matching .NET enum names)
// ============================================================

export type BotType =
  | 'Unknown'
  | 'SearchEngine'
  | 'SocialMediaBot'
  | 'MonitoringBot'
  | 'Scraper'
  | 'MaliciousBot'
  | 'GoodBot'
  | 'VerifiedBot'
  | 'AiBot'
  | 'Tool'
  | 'ExploitScanner';

export type RiskBand =
  | 'Unknown'
  | 'VeryLow'
  | 'Low'
  | 'Elevated'
  | 'Medium'
  | 'High'
  | 'VeryHigh'
  | 'Verified';

export type RecommendedAction = 'Allow' | 'Throttle' | 'Challenge' | 'Block';

export type ThreatBand = 'None' | 'Low' | 'Elevated' | 'High' | 'Critical';

// ============================================================
// Detection request/response (matches .NET DetectRequest/DetectResponse)
// ============================================================

export interface TlsInfo {
  version?: string;
  cipher?: string;
  ja3?: string;
  ja4?: string;
}

export interface DetectRequest {
  method: string;
  path: string;
  headers: Record<string, string>;
  remoteIp: string;
  protocol?: string;
  tls?: TlsInfo;
}

export interface Verdict {
  isBot: boolean;
  botProbability: number;
  confidence: number;
  botType: BotType | null;
  botName: string | null;
  riskBand: RiskBand;
  recommendedAction: RecommendedAction;
  threatScore: number;
  threatBand: ThreatBand;
}

export interface DetectionReason {
  detector: string;
  detail: string;
  impact: number;
}

export interface DetectionMeta {
  processingTimeMs: number;
  detectorsRun: number;
  policyName: string | null;
  aiRan: boolean;
  requestId?: string;
}

export interface DetectResponse {
  verdict: Verdict;
  reasons: DetectionReason[];
  signals: Record<string, unknown>;
  meta: DetectionMeta;
}

// ============================================================
// Paginated envelope
// ============================================================

export interface PaginationInfo {
  offset: number;
  limit: number;
  total: number;
}

export interface ResponseMeta {
  generatedAt: string;
}

export interface PaginatedResponse<T> {
  data: T[];
  pagination: PaginationInfo;
  meta: ResponseMeta;
}

export interface SingleResponse<T> {
  data: T;
  meta: ResponseMeta;
}

// ============================================================
// Dashboard read types
// ============================================================

export interface Summary {
  totalRequests: number;
  botRequests: number;
  humanRequests: number;
  uncertainRequests: number;
  riskBandCounts: Record<string, number>;
  topBotTypes: Record<string, number>;
  uniqueSignatures: number;
}

export interface TimeseriesPoint {
  timestamp: string;
  total: number;
  bots: number;
  humans: number;
}

export interface CountryStat {
  countryCode: string;
  total: number;
  botCount: number;
  botRate: number;
}

export interface EndpointStat {
  method: string;
  path: string;
  total: number;
  botCount: number;
  botRate: number;
}

export interface BotStat {
  signatureId: string;
  botName: string | null;
  botType: string | null;
  hitCount: number;
  lastSeen: string;
}

export interface Threat {
  timestamp: string;
  path: string;
  threatType: string;
  threatScore: number;
  signatureId: string;
}

export interface ApiKeyInfo {
  keyName: string;
  disabledDetectors: string[];
  weightOverrides: Record<string, number>;
  detectionPolicyName: string | null;
  actionPolicyName: string | null;
  tags: string[];
  disablesAllDetectors: boolean;
}

// ============================================================
// Client options
// ============================================================

export interface StyloBotClientOptions {
  endpoint: string;
  apiKey?: string;
  bearerToken?: string;
  timeout?: number;
  retries?: number;
}

// ============================================================
// Query parameter types
// ============================================================

export interface PaginationQuery {
  limit?: number;
  offset?: number;
}

export interface DetectionsQuery extends PaginationQuery {
  isBot?: boolean;
  since?: string;
}

export interface SignaturesQuery extends PaginationQuery {
  isBot?: boolean;
}

export interface SummaryQuery {
  period?: '1h' | '24h' | '7d';
}

export interface TimeseriesQuery {
  interval?: '1m' | '5m' | '15m' | '1h';
  since?: string;
  until?: string;
}

export interface ThreatsQuery extends PaginationQuery {
  severity?: string;
  since?: string;
  until?: string;
}
```

- [ ] **Step 4: Create headers.ts**

Create `sdk/node/packages/core/src/headers.ts`:

```typescript
import type { Verdict, RiskBand, RecommendedAction, ThreatBand, BotType } from './types.js';

/** Header names injected by StyloBot Gateway. */
export const STYLOBOT_HEADERS = {
  IS_BOT: 'x-stylobot-isbot',
  PROBABILITY: 'x-stylobot-probability',
  CONFIDENCE: 'x-stylobot-confidence',
  BOT_TYPE: 'x-stylobot-bottype',
  BOT_NAME: 'x-stylobot-botname',
  RISK_BAND: 'x-stylobot-riskband',
  ACTION: 'x-stylobot-action',
  THREAT_SCORE: 'x-stylobot-threatscore',
  THREAT_BAND: 'x-stylobot-threatband',
  POLICY: 'x-stylobot-policy',
  REQUEST_ID: 'x-stylobot-requestid',
} as const;

/**
 * Parse X-StyloBot-* headers into a typed Verdict.
 * Returns null if the required X-StyloBot-IsBot header is missing.
 */
export function parseStyloBotHeaders(
  headers: Record<string, string | string[] | undefined>
): Verdict | null {
  const get = (key: string): string | undefined => {
    const val = headers[key];
    if (Array.isArray(val)) return val[0];
    return val ?? undefined;
  };

  const isBotRaw = get(STYLOBOT_HEADERS.IS_BOT);
  if (isBotRaw === undefined) return null;

  return {
    isBot: isBotRaw === 'true',
    botProbability: parseFloat(get(STYLOBOT_HEADERS.PROBABILITY) ?? '0'),
    confidence: parseFloat(get(STYLOBOT_HEADERS.CONFIDENCE) ?? '0'),
    botType: (get(STYLOBOT_HEADERS.BOT_TYPE) as BotType) || null,
    botName: get(STYLOBOT_HEADERS.BOT_NAME) || null,
    riskBand: (get(STYLOBOT_HEADERS.RISK_BAND) as RiskBand) ?? 'Unknown',
    recommendedAction: (get(STYLOBOT_HEADERS.ACTION) as RecommendedAction) ?? 'Allow',
    threatScore: parseFloat(get(STYLOBOT_HEADERS.THREAT_SCORE) ?? '0'),
    threatBand: (get(STYLOBOT_HEADERS.THREAT_BAND) as ThreatBand) ?? 'None',
  };
}
```

- [ ] **Step 5: Create index.ts**

Create `sdk/node/packages/core/src/index.ts`:

```typescript
export * from './types.js';
export * from './headers.js';
export { StyloBotClient } from './client.js';
```

- [ ] **Step 6: Commit**

```bash
git add sdk/
git commit -m "Add @stylobot/core package with types and header parser"
```

---

## Task 11: @stylobot/core client and tests

**Files:**
- Create: `sdk/node/packages/core/src/client.ts`
- Create: `sdk/node/packages/core/src/__tests__/headers.test.ts`
- Create: `sdk/node/packages/core/src/__tests__/client.test.ts`

- [ ] **Step 1: Create StyloBotClient**

Create `sdk/node/packages/core/src/client.ts`:

```typescript
import type {
  StyloBotClientOptions,
  DetectRequest,
  DetectResponse,
  PaginatedResponse,
  SingleResponse,
  Summary,
  TimeseriesPoint,
  CountryStat,
  EndpointStat,
  BotStat,
  Threat,
  ApiKeyInfo,
  DetectionsQuery,
  SignaturesQuery,
  SummaryQuery,
  TimeseriesQuery,
  PaginationQuery,
  ThreatsQuery,
} from './types.js';

export class StyloBotClient {
  private readonly endpoint: string;
  private readonly apiKey?: string;
  private readonly bearerToken?: string;
  private readonly timeout: number;
  private readonly retries: number;

  constructor(options: StyloBotClientOptions) {
    this.endpoint = options.endpoint.replace(/\/$/, '');
    this.apiKey = options.apiKey;
    this.bearerToken = options.bearerToken;
    this.timeout = options.timeout ?? 5000;
    this.retries = options.retries ?? 1;
  }

  // ---- Detection (Tier 2) ----

  async detect(request: DetectRequest): Promise<DetectResponse> {
    return this.post<DetectResponse>('/api/v1/detect', request);
  }

  async detectBatch(requests: DetectRequest[]): Promise<DetectResponse[]> {
    return this.post<DetectResponse[]>('/api/v1/detect/batch', requests);
  }

  // ---- Read (Tier 2) ----

  async detections(params?: DetectionsQuery): Promise<PaginatedResponse<unknown>> {
    return this.get('/api/v1/detections', params);
  }

  async signatures(params?: SignaturesQuery): Promise<PaginatedResponse<unknown>> {
    return this.get('/api/v1/signatures', params);
  }

  async summary(params?: SummaryQuery): Promise<SingleResponse<Summary>> {
    return this.get('/api/v1/summary', params);
  }

  async timeseries(params?: TimeseriesQuery): Promise<PaginatedResponse<TimeseriesPoint>> {
    return this.get('/api/v1/timeseries', params);
  }

  async countries(params?: PaginationQuery): Promise<PaginatedResponse<CountryStat>> {
    return this.get('/api/v1/countries', params);
  }

  async endpoints(params?: PaginationQuery): Promise<PaginatedResponse<EndpointStat>> {
    return this.get('/api/v1/endpoints', params);
  }

  async topBots(params?: PaginationQuery): Promise<PaginatedResponse<BotStat>> {
    return this.get('/api/v1/topbots', params);
  }

  async threats(params?: ThreatsQuery): Promise<PaginatedResponse<Threat>> {
    return this.get('/api/v1/threats', params);
  }

  async me(): Promise<SingleResponse<ApiKeyInfo>> {
    return this.get('/api/v1/me');
  }

  // ---- Internal ----

  private headers(): Record<string, string> {
    const h: Record<string, string> = { 'content-type': 'application/json' };
    if (this.apiKey) h['x-sb-api-key'] = this.apiKey;
    if (this.bearerToken) h['authorization'] = `Bearer ${this.bearerToken}`;
    return h;
  }

  private async request<T>(method: string, path: string, body?: unknown): Promise<T> {
    const url = `${this.endpoint}${path}`;
    let lastError: Error | undefined;

    for (let attempt = 0; attempt <= this.retries; attempt++) {
      try {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), this.timeout);

        const res = await fetch(url, {
          method,
          headers: this.headers(),
          body: body ? JSON.stringify(body) : undefined,
          signal: controller.signal,
        });

        clearTimeout(timer);

        if (!res.ok) {
          const text = await res.text().catch(() => '');
          throw new StyloBotApiError(res.status, text, url);
        }

        return (await res.json()) as T;
      } catch (err) {
        lastError = err instanceof Error ? err : new Error(String(err));
        if (err instanceof StyloBotApiError && err.status < 500) throw err;
      }
    }

    throw lastError ?? new Error('Request failed');
  }

  private get<T>(path: string, params?: Record<string, unknown>): Promise<T> {
    const qs = params ? toQueryString(params) : '';
    return this.request<T>('GET', qs ? `${path}?${qs}` : path);
  }

  private post<T>(path: string, body: unknown): Promise<T> {
    return this.request<T>('POST', path, body);
  }
}

export class StyloBotApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly body: string,
    public readonly url: string
  ) {
    super(`StyloBot API error ${status}: ${body.slice(0, 200)}`);
    this.name = 'StyloBotApiError';
  }
}

function toQueryString(params: Record<string, unknown>): string {
  const entries = Object.entries(params).filter(([, v]) => v !== undefined && v !== null);
  if (entries.length === 0) return '';
  return entries.map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`).join('&');
}
```

- [ ] **Step 2: Create header parser tests**

Create `sdk/node/packages/core/src/__tests__/headers.test.ts`:

```typescript
import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { parseStyloBotHeaders, STYLOBOT_HEADERS } from '../headers.js';

describe('parseStyloBotHeaders', () => {
  it('returns null when X-StyloBot-IsBot header is missing', () => {
    const result = parseStyloBotHeaders({});
    assert.equal(result, null);
  });

  it('parses a full bot verdict', () => {
    const headers = {
      [STYLOBOT_HEADERS.IS_BOT]: 'true',
      [STYLOBOT_HEADERS.PROBABILITY]: '0.92',
      [STYLOBOT_HEADERS.CONFIDENCE]: '0.87',
      [STYLOBOT_HEADERS.BOT_TYPE]: 'Scraper',
      [STYLOBOT_HEADERS.BOT_NAME]: 'GPTBot',
      [STYLOBOT_HEADERS.RISK_BAND]: 'High',
      [STYLOBOT_HEADERS.ACTION]: 'Block',
      [STYLOBOT_HEADERS.THREAT_SCORE]: '0.15',
      [STYLOBOT_HEADERS.THREAT_BAND]: 'Low',
    };

    const v = parseStyloBotHeaders(headers)!;
    assert.equal(v.isBot, true);
    assert.equal(v.botProbability, 0.92);
    assert.equal(v.confidence, 0.87);
    assert.equal(v.botType, 'Scraper');
    assert.equal(v.botName, 'GPTBot');
    assert.equal(v.riskBand, 'High');
    assert.equal(v.recommendedAction, 'Block');
    assert.equal(v.threatScore, 0.15);
    assert.equal(v.threatBand, 'Low');
  });

  it('parses a human verdict with defaults', () => {
    const headers = {
      [STYLOBOT_HEADERS.IS_BOT]: 'false',
      [STYLOBOT_HEADERS.PROBABILITY]: '0.12',
      [STYLOBOT_HEADERS.CONFIDENCE]: '0.95',
    };

    const v = parseStyloBotHeaders(headers)!;
    assert.equal(v.isBot, false);
    assert.equal(v.botProbability, 0.12);
    assert.equal(v.botType, null);
    assert.equal(v.botName, null);
    assert.equal(v.riskBand, 'Unknown');
    assert.equal(v.recommendedAction, 'Allow');
  });

  it('handles array header values', () => {
    const headers = {
      [STYLOBOT_HEADERS.IS_BOT]: ['true', 'false'],
      [STYLOBOT_HEADERS.PROBABILITY]: ['0.80'],
    };

    const v = parseStyloBotHeaders(headers)!;
    assert.equal(v.isBot, true);
    assert.equal(v.botProbability, 0.80);
  });
});
```

- [ ] **Step 3: Create client tests**

Create `sdk/node/packages/core/src/__tests__/client.test.ts`:

```typescript
import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { createServer, type Server } from 'node:http';
import { StyloBotClient, StyloBotApiError } from '../client.js';

let server: Server;
let port: number;

function startServer(handler: (req: any, res: any) => void): Promise<void> {
  return new Promise((resolve) => {
    server = createServer(handler);
    server.listen(0, () => {
      port = (server.address() as any).port;
      resolve();
    });
  });
}

function stopServer(): Promise<void> {
  return new Promise((resolve) => {
    if (server) server.close(() => resolve());
    else resolve();
  });
}

describe('StyloBotClient', () => {
  afterEach(async () => {
    await stopServer();
  });

  it('sends detect request with API key header', async () => {
    let receivedHeaders: Record<string, string | string[] | undefined> = {};
    let receivedBody = '';

    await startServer((req, res) => {
      receivedHeaders = req.headers;
      let body = '';
      req.on('data', (chunk: string) => { body += chunk; });
      req.on('end', () => {
        receivedBody = body;
        res.writeHead(200, { 'content-type': 'application/json' });
        res.end(JSON.stringify({
          verdict: { isBot: true, botProbability: 0.9, confidence: 0.8,
            botType: 'Scraper', botName: 'TestBot', riskBand: 'High',
            recommendedAction: 'Block', threatScore: 0, threatBand: 'None' },
          reasons: [], signals: {}, meta: { processingTimeMs: 1, detectorsRun: 5, policyName: null, aiRan: false }
        }));
      });
    });

    const client = new StyloBotClient({
      endpoint: `http://localhost:${port}`,
      apiKey: 'SB-TEST-KEY',
    });

    const result = await client.detect({
      method: 'GET', path: '/', headers: { 'user-agent': 'test' }, remoteIp: '127.0.0.1'
    });

    assert.equal(receivedHeaders['x-sb-api-key'], 'SB-TEST-KEY');
    assert.equal(result.verdict.isBot, true);
    assert.equal(result.verdict.botType, 'Scraper');
    const parsed = JSON.parse(receivedBody);
    assert.equal(parsed.method, 'GET');
    assert.equal(parsed.remoteIp, '127.0.0.1');
  });

  it('throws StyloBotApiError on 4xx', async () => {
    await startServer((_req, res) => {
      res.writeHead(403, { 'content-type': 'application/json' });
      res.end('{"title":"Forbidden"}');
    });

    const client = new StyloBotClient({
      endpoint: `http://localhost:${port}`,
      apiKey: 'SB-BAD',
      retries: 0,
    });

    await assert.rejects(
      () => client.detect({ method: 'GET', path: '/', headers: {}, remoteIp: '127.0.0.1' }),
      (err: any) => {
        assert.ok(err instanceof StyloBotApiError);
        assert.equal(err.status, 403);
        return true;
      }
    );
  });

  it('sends bearer token for management operations', async () => {
    let authHeader = '';
    await startServer((req, res) => {
      authHeader = req.headers['authorization'] ?? '';
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ data: { keyName: 'test' }, meta: { generatedAt: new Date().toISOString() } }));
    });

    const client = new StyloBotClient({
      endpoint: `http://localhost:${port}`,
      bearerToken: 'ey.token.here',
    });

    await client.me();
    assert.equal(authHeader, 'Bearer ey.token.here');
  });
});
```

- [ ] **Step 4: Install dependencies and run tests**

```bash
cd sdk/node && npm install && cd packages/core && npx tsc --noEmit && node --test src/__tests__/headers.test.ts
```

Expected: All header tests PASS.

- [ ] **Step 5: Commit**

```bash
git add sdk/
git commit -m "Add @stylobot/core client, header parser tests, and client tests"
```

---

## Task 12: @stylobot/node package - Express middleware and Fastify plugin

**Files:**
- Create: `sdk/node/packages/node/package.json`
- Create: `sdk/node/packages/node/tsconfig.json`
- Create: `sdk/node/packages/node/src/extract.ts`
- Create: `sdk/node/packages/node/src/middleware.ts`
- Create: `sdk/node/packages/node/src/fastify.ts`
- Create: `sdk/node/packages/node/src/index.ts`
- Create: `sdk/node/packages/node/src/__tests__/extract.test.ts`
- Create: `sdk/node/packages/node/src/__tests__/middleware.test.ts`

- [ ] **Step 1: Create package scaffolding**

Create `sdk/node/packages/node/package.json`:

```json
{
  "name": "@stylobot/node",
  "version": "0.1.0",
  "description": "StyloBot SDK for Node.js - Express and Fastify middleware",
  "type": "module",
  "main": "dist/index.js",
  "types": "dist/index.d.ts",
  "files": ["dist"],
  "scripts": {
    "build": "tsc",
    "test": "node --test src/__tests__/*.test.ts",
    "clean": "rm -rf dist"
  },
  "dependencies": {
    "@stylobot/core": "workspace:*"
  },
  "peerDependencies": {
    "express": ">=4.0.0",
    "fastify": ">=4.0.0"
  },
  "peerDependenciesMeta": {
    "express": { "optional": true },
    "fastify": { "optional": true }
  },
  "devDependencies": {
    "typescript": "^5.8.0",
    "@types/node": "^22.0.0",
    "@types/express": "^5.0.0",
    "express": "^5.0.0"
  },
  "license": "MIT"
}
```

Create `sdk/node/packages/node/tsconfig.json`:

```json
{
  "extends": "../../tsconfig.json",
  "compilerOptions": {
    "outDir": "dist",
    "rootDir": "src"
  },
  "include": ["src/**/*.ts"],
  "exclude": ["src/__tests__"]
}
```

- [ ] **Step 2: Create request extractor**

Create `sdk/node/packages/node/src/extract.ts`:

```typescript
import type { DetectRequest } from '@stylobot/core';
import type { IncomingMessage } from 'node:http';

/**
 * Extract a DetectRequest from a Node.js IncomingMessage.
 * Works with raw http, Express, and Fastify request objects.
 */
export function extractDetectRequest(req: IncomingMessage & {
  ip?: string;
  originalUrl?: string;
  protocol?: string;
}): DetectRequest {
  // Headers: flatten to string dict
  const headers: Record<string, string> = {};
  for (const [key, value] of Object.entries(req.headers)) {
    if (value !== undefined) {
      headers[key] = Array.isArray(value) ? value.join(', ') : value;
    }
  }

  // IP: prefer framework-resolved IP, fall back to socket
  const remoteIp =
    req.ip ??
    (headers['x-forwarded-for']?.split(',')[0]?.trim()) ??
    req.socket?.remoteAddress ??
    '0.0.0.0';

  // Path: Express sets originalUrl, raw http uses url
  const path = req.originalUrl ?? req.url ?? '/';

  // Protocol: Express sets this, otherwise infer from socket
  const protocol =
    req.protocol ??
    (headers['x-forwarded-proto']?.split(',')[0]?.trim()) ??
    ((req.socket as any)?.encrypted ? 'https' : 'http');

  return {
    method: req.method ?? 'GET',
    path,
    headers,
    remoteIp,
    protocol,
  };
}
```

- [ ] **Step 3: Create Express middleware**

Create `sdk/node/packages/node/src/middleware.ts`:

```typescript
import type { Request, Response, NextFunction, RequestHandler } from 'express';
import { StyloBotClient, parseStyloBotHeaders, type Verdict, type DetectResponse } from '@stylobot/core';
import { extractDetectRequest } from './extract.js';

export interface StyloBotMiddlewareOptions {
  /** 'headers' reads X-StyloBot-* from request, 'api' calls detection endpoint */
  mode: 'headers' | 'api';
  /** StyloBot endpoint URL (required for 'api' mode) */
  endpoint?: string;
  /** API key (required for 'api' mode) */
  apiKey?: string;
  /** Timeout in ms for API calls. Default 5000. */
  timeout?: number;
}

export interface StyloBotResult {
  isBot: boolean;
  verdict: Verdict;
  signals: Record<string, unknown>;
  reasons: DetectResponse['reasons'];
  meta: DetectResponse['meta'] | null;
}

declare global {
  namespace Express {
    interface Request {
      stylobot: StyloBotResult;
    }
  }
}

const EMPTY_VERDICT: Verdict = {
  isBot: false,
  botProbability: 0,
  confidence: 0,
  botType: null,
  botName: null,
  riskBand: 'Unknown',
  recommendedAction: 'Allow',
  threatScore: 0,
  threatBand: 'None',
};

/**
 * Express middleware that populates req.stylobot with detection results.
 */
export function styloBotMiddleware(options: StyloBotMiddlewareOptions): RequestHandler {
  if (options.mode === 'api') {
    if (!options.endpoint) throw new Error('endpoint is required for api mode');

    const client = new StyloBotClient({
      endpoint: options.endpoint,
      apiKey: options.apiKey,
      timeout: options.timeout,
    });

    return async (req: Request, res: Response, next: NextFunction) => {
      try {
        const detectReq = extractDetectRequest(req);
        const response = await client.detect(detectReq);
        req.stylobot = {
          isBot: response.verdict.isBot,
          verdict: response.verdict,
          signals: response.signals,
          reasons: response.reasons,
          meta: response.meta,
        };
      } catch {
        req.stylobot = {
          isBot: false,
          verdict: EMPTY_VERDICT,
          signals: {},
          reasons: [],
          meta: null,
        };
      }
      next();
    };
  }

  // Header mode: read X-StyloBot-* from request headers
  return (req: Request, _res: Response, next: NextFunction) => {
    const verdict = parseStyloBotHeaders(req.headers as Record<string, string>) ?? EMPTY_VERDICT;
    req.stylobot = {
      isBot: verdict.isBot,
      verdict,
      signals: {},
      reasons: [],
      meta: null,
    };
    next();
  };
}
```

- [ ] **Step 4: Create Fastify plugin**

Create `sdk/node/packages/node/src/fastify.ts`:

```typescript
import { StyloBotClient, parseStyloBotHeaders, type Verdict, type DetectResponse } from '@stylobot/core';
import { extractDetectRequest } from './extract.js';
import type { StyloBotMiddlewareOptions, StyloBotResult } from './middleware.js';

declare module 'fastify' {
  interface FastifyRequest {
    stylobot: StyloBotResult;
  }
}

const EMPTY_VERDICT: Verdict = {
  isBot: false,
  botProbability: 0,
  confidence: 0,
  botType: null,
  botName: null,
  riskBand: 'Unknown',
  recommendedAction: 'Allow',
  threatScore: 0,
  threatBand: 'None',
};

/**
 * Fastify plugin that populates request.stylobot with detection results.
 */
export async function styloBotPlugin(
  fastify: any,
  options: StyloBotMiddlewareOptions
): Promise<void> {
  if (options.mode === 'api') {
    if (!options.endpoint) throw new Error('endpoint is required for api mode');

    const client = new StyloBotClient({
      endpoint: options.endpoint,
      apiKey: options.apiKey,
      timeout: options.timeout,
    });

    fastify.decorateRequest('stylobot', null);
    fastify.addHook('preHandler', async (request: any) => {
      try {
        const detectReq = extractDetectRequest(request.raw);
        const response = await client.detect(detectReq);
        request.stylobot = {
          isBot: response.verdict.isBot,
          verdict: response.verdict,
          signals: response.signals,
          reasons: response.reasons,
          meta: response.meta,
        };
      } catch {
        request.stylobot = {
          isBot: false,
          verdict: EMPTY_VERDICT,
          signals: {},
          reasons: [],
          meta: null,
        };
      }
    });
  } else {
    fastify.decorateRequest('stylobot', null);
    fastify.addHook('preHandler', async (request: any) => {
      const verdict =
        parseStyloBotHeaders(request.raw.headers as Record<string, string>) ?? EMPTY_VERDICT;
      request.stylobot = {
        isBot: verdict.isBot,
        verdict,
        signals: {},
        reasons: [],
        meta: null,
      };
    });
  }
}
```

- [ ] **Step 5: Create index.ts**

Create `sdk/node/packages/node/src/index.ts`:

```typescript
export { styloBotMiddleware, type StyloBotMiddlewareOptions, type StyloBotResult } from './middleware.js';
export { styloBotPlugin } from './fastify.js';
export { extractDetectRequest } from './extract.js';
```

- [ ] **Step 6: Create extractor tests**

Create `sdk/node/packages/node/src/__tests__/extract.test.ts`:

```typescript
import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { IncomingMessage } from 'node:http';
import { Socket } from 'node:net';
import { extractDetectRequest } from '../extract.js';

function mockReq(overrides: Record<string, any> = {}): IncomingMessage & Record<string, any> {
  const socket = new Socket();
  (socket as any).remoteAddress = '192.168.1.1';
  const req = new IncomingMessage(socket);
  req.method = overrides.method ?? 'GET';
  req.url = overrides.url ?? '/test';
  req.headers = overrides.headers ?? { 'user-agent': 'TestAgent/1.0' };
  if (overrides.ip) (req as any).ip = overrides.ip;
  if (overrides.originalUrl) (req as any).originalUrl = overrides.originalUrl;
  if (overrides.protocol) (req as any).protocol = overrides.protocol;
  return req as any;
}

describe('extractDetectRequest', () => {
  it('extracts method, path, and headers', () => {
    const req = mockReq({
      method: 'POST',
      url: '/api/data',
      headers: { 'user-agent': 'Bot/1.0', 'accept': 'application/json' },
    });

    const result = extractDetectRequest(req);

    assert.equal(result.method, 'POST');
    assert.equal(result.path, '/api/data');
    assert.equal(result.headers['user-agent'], 'Bot/1.0');
    assert.equal(result.headers['accept'], 'application/json');
  });

  it('prefers Express ip property', () => {
    const req = mockReq({ ip: '10.0.0.1' });
    const result = extractDetectRequest(req);
    assert.equal(result.remoteIp, '10.0.0.1');
  });

  it('falls back to x-forwarded-for', () => {
    const req = mockReq({ headers: { 'x-forwarded-for': '203.0.113.42, 10.0.0.1' } });
    const result = extractDetectRequest(req);
    assert.equal(result.remoteIp, '203.0.113.42');
  });

  it('falls back to socket remoteAddress', () => {
    const req = mockReq({ headers: {} });
    const result = extractDetectRequest(req);
    assert.equal(result.remoteIp, '192.168.1.1');
  });

  it('uses originalUrl when available (Express)', () => {
    const req = mockReq({ url: '/rewritten', originalUrl: '/original?q=1' });
    const result = extractDetectRequest(req);
    assert.equal(result.path, '/original?q=1');
  });

  it('flattens array header values', () => {
    const req = mockReq({ headers: { 'set-cookie': ['a=1', 'b=2'] } });
    const result = extractDetectRequest(req);
    assert.equal(result.headers['set-cookie'], 'a=1, b=2');
  });
});
```

- [ ] **Step 7: Create Express middleware tests**

Create `sdk/node/packages/node/src/__tests__/middleware.test.ts`:

```typescript
import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { IncomingMessage, ServerResponse } from 'node:http';
import { Socket } from 'node:net';
import { styloBotMiddleware } from '../middleware.js';

function mockExpressReq(headers: Record<string, string> = {}): any {
  const socket = new Socket();
  (socket as any).remoteAddress = '127.0.0.1';
  const req = new IncomingMessage(socket);
  req.method = 'GET';
  req.url = '/test';
  req.headers = headers;
  (req as any).originalUrl = '/test';
  (req as any).ip = '127.0.0.1';
  (req as any).protocol = 'https';
  return req;
}

describe('styloBotMiddleware (header mode)', () => {
  it('parses X-StyloBot-* headers into req.stylobot', (_, done) => {
    const mw = styloBotMiddleware({ mode: 'headers' });
    const req = mockExpressReq({
      'x-stylobot-isbot': 'true',
      'x-stylobot-probability': '0.88',
      'x-stylobot-confidence': '0.75',
      'x-stylobot-bottype': 'AiBot',
      'x-stylobot-botname': 'Claude',
      'x-stylobot-riskband': 'Medium',
      'x-stylobot-action': 'Challenge',
      'x-stylobot-threatscore': '0.05',
      'x-stylobot-threatband': 'None',
    });

    mw(req, {} as any, () => {
      assert.equal(req.stylobot.isBot, true);
      assert.equal(req.stylobot.verdict.botProbability, 0.88);
      assert.equal(req.stylobot.verdict.botType, 'AiBot');
      assert.equal(req.stylobot.verdict.botName, 'Claude');
      assert.equal(req.stylobot.verdict.recommendedAction, 'Challenge');
      done();
    });
  });

  it('returns empty verdict when no headers present', (_, done) => {
    const mw = styloBotMiddleware({ mode: 'headers' });
    const req = mockExpressReq({});

    mw(req, {} as any, () => {
      assert.equal(req.stylobot.isBot, false);
      assert.equal(req.stylobot.verdict.botProbability, 0);
      assert.equal(req.stylobot.verdict.riskBand, 'Unknown');
      done();
    });
  });
});

describe('styloBotMiddleware (api mode)', () => {
  it('throws if endpoint is not provided', () => {
    assert.throws(
      () => styloBotMiddleware({ mode: 'api' }),
      /endpoint is required/
    );
  });
});
```

- [ ] **Step 8: Install and run tests**

```bash
cd sdk/node && npm install && cd packages/node && npx tsc --noEmit && node --test src/__tests__/extract.test.ts && node --test src/__tests__/middleware.test.ts
```

Expected: All tests PASS.

- [ ] **Step 9: Commit**

```bash
git add sdk/
git commit -m "Add @stylobot/node package with Express middleware, Fastify plugin, and tests"
```

---

## Task 13: Run full test suite and verify builds

**Files:** None (verification only)

- [ ] **Step 1: Run all .NET tests**

```bash
dotnet test Mostlylucid.BotDetection.Api.Tests/ -v m
```

Expected: All tests PASS.

- [ ] **Step 2: Build entire solution**

```bash
dotnet build mostlylucid.stylobot.sln
```

Expected: Build succeeded with no errors.

- [ ] **Step 3: Run Node SDK tests**

```bash
cd sdk/node && npm test --workspaces
```

Expected: All tests PASS.

- [ ] **Step 4: Verify TypeScript compiles**

```bash
cd sdk/node && npm run build --workspaces
```

Expected: Clean compile with .d.ts output.

- [ ] **Step 5: Commit any fixes**

If any tests needed fixes, commit them:

```bash
git add -A && git commit -m "Fix test issues from full verification"
```

---

## Task 14: Final commit - update CLAUDE.md and solution docs

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Add API project to CLAUDE.md solution table**

Add to the solution structure table in CLAUDE.md:

```
| `Mostlylucid.BotDetection.Api` | Public REST API for detection & dashboard data |
```

- [ ] **Step 2: Add SDK section to CLAUDE.md**

Add after the Dashboard section:

```markdown
## Node SDK

Two npm packages in `sdk/node/`:
- **`@stylobot/core`** - Zero-dep types, `StyloBotClient`, header parser. Works in Node/Deno/Bun.
- **`@stylobot/node`** - Express middleware (`styloBotMiddleware`), Fastify plugin (`styloBotPlugin`).

Two modes: `headers` (behind Gateway, zero-latency) or `api` (sidecar, calls `POST /api/v1/detect`).

Build: `cd sdk/node && npm install && npm run build --workspaces`
Test: `cd sdk/node && npm test --workspaces`
```

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "Document Public API and Node SDK in CLAUDE.md"
```