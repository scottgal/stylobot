# WebMCP Increment 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a working read-only MCP server that gives any StyloBot-proxied site full-text search over its own content, with zero changes to that site.

**Architecture:** A new FOSS pack, `Mostlylucid.BotDetection.WebMcp`. An `IActionPolicy` captures proxied HTML (the same interception pattern StyloExtract already ships), converts it to Markdown, and enqueues it to a write-behind drain that populates a SQLite FTS5 index. A JSON-RPC 2.0 endpoint speaks MCP over Streamable HTTP and exposes two tools — `search_site` (served from the index) and `fetch_page` (a revalidating loopback GET, never a stored body).

**Tech Stack:** .NET 10, `Microsoft.Data.Sqlite` (FTS5), `System.Text.Json` with source-generated contexts, xUnit + FluentAssertions, ASP.NET Core minimal APIs.

**Spec:** `docs/superpowers/specs/2026-08-06-webmcp-surface-design.md` (§13 increment 1).

## Global Constraints

- **Target framework:** `net10.0`. Nullable enabled, implicit usings enabled.
- **Pack csproj properties (copy from `Mostlylucid.BotDetection.StyloExtract.csproj`):** `IsPackable=true`, `IsAotCompatible=true`, `TreatWarningsAsErrors=true`, `EnableConfigurationBindingGenerator=true`. `TreatWarningsAsErrors` means IL2026/IL3050 trim warnings **fail the build** — every `JsonSerializer` call must use a source-generated `JsonTypeInfo`, never a reflection overload.
- **No magic numbers.** Every threshold, cap, interval and path is a property on an Options class bound from `BotDetection:WebMcp`. No `IConfiguration.GetValue(...)` outside the Options binder.
- **No in-memory persistence.** All durable state is SQLite. `ConcurrentDictionary` is permitted only for transient per-request state and the hot tier of a write-behind store.
- **Never skip detection.** The MCP endpoint is a normal endpoint; requests to it run the full pipeline. No skip path, no bypass key, no allowlist. This is a Critical Rule in `CLAUDE.md` and Task 8 has a test asserting it.
- **Fail open, always.** No failure in this pack may alter or break normal proxied traffic. Capture failures log at Warning and leave the response byte-identical.
- **Default off.** `BotDetection:WebMcp:Enabled` defaults to `false`.
- **Zero-PII.** Only public page content and URLs are persisted. No IP, no UA, no header values in `webmcp.db`.
- **MCP protocol version:** advertise `"2025-06-18"`.
- **Test conventions:** xUnit `[Fact]`/`[Theory]`, FluentAssertions (`.Should()`), test class per unit, namespace `Mostlylucid.BotDetection.WebMcp.Tests`.
- **Commit style:** conventional commits (`feat:`, `test:`, `fix:`, `docs:`). Do not add a Co-Authored-By trailer unless asked.

## File Structure

| File | Responsibility |
|---|---|
| `src/Mostlylucid.BotDetection.WebMcp/Mostlylucid.BotDetection.WebMcp.csproj` | Pack definition |
| `Options/WebMcpOptions.cs` | Every knob, bound from `BotDetection:WebMcp` |
| `Index/Fts5QuerySanitiser.cs` | Raw user query → safe FTS5 MATCH expression |
| `Index/ISiteIndex.cs` | Index contract + `IndexedDocument` / `SearchHit` records |
| `Index/Fts5SiteIndex.cs` | SQLite FTS5 implementation |
| `Corpus/CapturedPage.cs` | The unit enqueued by capture, drained by the writer |
| `Corpus/SiteCorpusWriter.cs` | Bounded channel + single background drain into `ISiteIndex` |
| `Actions/WebMcpIndexActionPolicy.cs` | `IActionPolicy` named `webmcp-index`; captures HTML, enqueues |
| `Protocol/JsonRpc.cs` | JSON-RPC 2.0 envelope records + error codes |
| `Protocol/McpModels.cs` | `initialize` / `tools/list` / `tools/call` payload records |
| `Protocol/WebMcpJsonContext.cs` | Source-generated `JsonSerializerContext` |
| `Protocol/McpJsonRpcHandler.cs` | Method dispatch |
| `Tools/IToolExecutor.cs` + `ToolExecutor.cs` | Executes `search_site` / `fetch_page` |
| `Tools/IUpstreamFetcher.cs` + `HttpUpstreamFetcher.cs` | Loopback GET for `fetch_page` |
| `Endpoints/WebMcpEndpoints.cs` | `MapWebMcp()` — routes POST/GET |
| `Extensions/ServiceCollectionExtensions.cs` | `AddWebMcp()` |
| `README.md` | Pack docs |
| `tests/Mostlylucid.BotDetection.WebMcp.Test/…` | One test class per unit + `TestHelpers.cs` |

---

### Task 1: Pack skeleton, options, and test project

**Files:**
- Create: `src/Mostlylucid.BotDetection.WebMcp/Mostlylucid.BotDetection.WebMcp.csproj`
- Create: `src/Mostlylucid.BotDetection.WebMcp/Options/WebMcpOptions.cs`
- Create: `tests/Mostlylucid.BotDetection.WebMcp.Test/Mostlylucid.BotDetection.WebMcp.Test.csproj`
- Create: `tests/Mostlylucid.BotDetection.WebMcp.Test/WebMcpOptionsTests.cs`
- Modify: `mostlylucid.stylobot.sln`

**Interfaces:**
- Produces: `WebMcpOptions` with nested `IndexOptions`, `CorpusOptions`, `TierOptions`. Every later task reads its knobs from here.

- [ ] **Step 1: Create the pack csproj**

`src/Mostlylucid.BotDetection.WebMcp/Mostlylucid.BotDetection.WebMcp.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <Nullable>enable</Nullable>
        <ImplicitUsings>enable</ImplicitUsings>
        <IsPackable>true</IsPackable>
        <IsAotCompatible>true</IsAotCompatible>
        <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
        <EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>

        <PackageId>Mostlylucid.BotDetection.WebMcp</PackageId>
        <Description>WebMCP surface for Mostlylucid.BotDetection. Synthesises an MCP server for a proxied upstream site: FTS5 full-text search over passively captured content, exposed as read-only MCP tools.</Description>
        <PackageTags>bot-detection;mcp;model-context-protocol;search;fts5;agent</PackageTags>
        <PackageReadmeFile>README.md</PackageReadmeFile>
    </PropertyGroup>

    <ItemGroup>
        <None Include="README.md" Pack="true" PackagePath="\"/>
    </ItemGroup>

    <ItemGroup>
        <FrameworkReference Include="Microsoft.AspNetCore.App" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\Mostlylucid.BotDetection\Mostlylucid.BotDetection.csproj"/>
    </ItemGroup>

    <ItemGroup>
        <PackageReference Include="Mostlylucid.StyloExtract.AspNetCore" Version="1.6.1" />
    </ItemGroup>

</Project>
```

Also create a one-line `src/Mostlylucid.BotDetection.WebMcp/README.md` containing `# Mostlylucid.BotDetection.WebMcp` — the csproj references it, so the build fails without it. Task 8 fills it in.

- [ ] **Step 2: Write the failing options test**

`tests/Mostlylucid.BotDetection.WebMcp.Test/WebMcpOptionsTests.cs`:

```csharp
using FluentAssertions;
using Mostlylucid.BotDetection.WebMcp.Options;
using Xunit;

namespace Mostlylucid.BotDetection.WebMcp.Tests;

public sealed class WebMcpOptionsTests
{
    [Fact]
    public void Defaults_are_off_and_safe()
    {
        var options = new WebMcpOptions();

        options.Enabled.Should().BeFalse("the pack must not activate without an explicit opt-in");
        options.Path.Should().Be("/_stylobot/mcp");
        options.Index.StorePath.Should().Be("webmcp.db");
        options.Index.MaxDocuments.Should().Be(50_000);
        options.Index.MaxExcerptBytes.Should().Be(8192);
        options.Corpus.PassiveCapture.Should().BeTrue();
        options.Corpus.QueueCapacity.Should().Be(1024);
        options.Tiers.Anonymous.MaxResults.Should().Be(5);
    }
}
```

- [ ] **Step 3: Create the test csproj and add both projects to the solution**

`tests/Mostlylucid.BotDetection.WebMcp.Test/Mostlylucid.BotDetection.WebMcp.Test.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.6.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="FluentAssertions" Version="8.10.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Mostlylucid.BotDetection.WebMcp\Mostlylucid.BotDetection.WebMcp.csproj" />
  </ItemGroup>
</Project>
```

Run:
```bash
dotnet sln mostlylucid.stylobot.sln add src/Mostlylucid.BotDetection.WebMcp/Mostlylucid.BotDetection.WebMcp.csproj
dotnet sln mostlylucid.stylobot.sln add tests/Mostlylucid.BotDetection.WebMcp.Test/Mostlylucid.BotDetection.WebMcp.Test.csproj
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test tests/Mostlylucid.BotDetection.WebMcp.Test/ --filter "FullyQualifiedName~WebMcpOptionsTests"`
Expected: FAIL — compile error, `WebMcpOptions` does not exist.

- [ ] **Step 5: Write the options class**

`src/Mostlylucid.BotDetection.WebMcp/Options/WebMcpOptions.cs`:

```csharp
namespace Mostlylucid.BotDetection.WebMcp.Options;

/// <summary>
///     Root options for the WebMCP pack, bound from <c>BotDetection:WebMcp</c>.
///     Defaults are deliberately inert: the pack does nothing until
///     <see cref="Enabled"/> is set true.
/// </summary>
public sealed class WebMcpOptions
{
    /// <summary>Master switch. False = no endpoint mapped, no capture, no index file created.</summary>
    public bool Enabled { get; set; }

    /// <summary>Route the MCP JSON-RPC endpoint is mapped at.</summary>
    public string Path { get; set; } = "/_stylobot/mcp";

    /// <summary>Advertised MCP <c>serverInfo.name</c>. Defaults to the request host when empty.</summary>
    public string ServerName { get; set; } = string.Empty;

    public IndexOptions Index { get; set; } = new();
    public CorpusOptions Corpus { get; set; } = new();
    public TierOptions Tiers { get; set; } = new();
}

public sealed class IndexOptions
{
    /// <summary>Path to the SQLite database file holding the document index.</summary>
    public string StorePath { get; set; } = "webmcp.db";

    /// <summary>Hard cap on indexed documents. Oldest-indexed rows are pruned past this.</summary>
    public int MaxDocuments { get; set; } = 50_000;

    /// <summary>Per-document cap on indexed body text. Longer bodies are truncated.</summary>
    public int MaxExcerptBytes { get; set; } = 8192;

    /// <summary>Maximum tokens accepted from one search query — bounds worst-case query cost.</summary>
    public int MaxQueryTokens { get; set; } = 16;
}

public sealed class CorpusOptions
{
    /// <summary>Index HTML that flows through the gateway as a side effect of normal traffic.</summary>
    public bool PassiveCapture { get; set; } = true;

    /// <summary>Bounded queue depth between capture and the drain. Full = drop, never block.</summary>
    public int QueueCapacity { get; set; } = 1024;

    /// <summary>Maximum wait before the drain flushes a partial batch.</summary>
    public TimeSpan DrainInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum documents written in one drain batch.</summary>
    public int DrainBatchSize { get; set; } = 32;
}

public sealed class TierOptions
{
    public TierBudget Anonymous { get; set; } = new() { CallsPerMinute = 10, MaxResults = 5 };
    public TierBudget ApiKey { get; set; } = new() { CallsPerMinute = 120, MaxResults = 25 };
    public TierBudget VerifiedAgent { get; set; } = new() { CallsPerMinute = 600, MaxResults = 50 };
}

public sealed class TierBudget
{
    public int CallsPerMinute { get; set; }
    public int MaxResults { get; set; }
}
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/Mostlylucid.BotDetection.WebMcp.Test/ --filter "FullyQualifiedName~WebMcpOptionsTests"`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add src/Mostlylucid.BotDetection.WebMcp tests/Mostlylucid.BotDetection.WebMcp.Test mostlylucid.stylobot.sln
git commit -m "feat(webmcp): pack skeleton and options"
```

---

### Task 2: FTS5 query sanitiser

FTS5 `MATCH` takes a query *language*, not a literal string. An unsanitised user query
containing `"`, `*`, `NEAR`, `AND`, or `(` is either a syntax error or a way to steer the
query. The sanitiser reduces any input to a bag of quoted literal tokens joined by implicit
AND, which has no syntax surface at all.

**Files:**
- Create: `src/Mostlylucid.BotDetection.WebMcp/Index/Fts5QuerySanitiser.cs`
- Test: `tests/Mostlylucid.BotDetection.WebMcp.Test/Fts5QuerySanitiserTests.cs`

**Interfaces:**
- Produces: `static string? Fts5QuerySanitiser.ToMatchExpression(string? raw, int maxTokens)` — returns `null` when the query has no usable tokens.

- [ ] **Step 1: Write the failing tests**

`tests/Mostlylucid.BotDetection.WebMcp.Test/Fts5QuerySanitiserTests.cs`:

```csharp
using FluentAssertions;
using Mostlylucid.BotDetection.WebMcp.Index;
using Xunit;

namespace Mostlylucid.BotDetection.WebMcp.Tests;

public sealed class Fts5QuerySanitiserTests
{
    [Fact]
    public void Plain_words_become_quoted_and_anded()
    {
        Fts5QuerySanitiser.ToMatchExpression("bot detection", 16)
            .Should().Be("\"bot\" \"detection\"");
    }

    [Theory]
    [InlineData("cat AND dog", "\"cat\" \"AND\" \"dog\"")]   // operator neutralised by quoting
    [InlineData("foo* bar", "\"foo\" \"bar\"")]              // prefix operator stripped
    [InlineData("a NEAR/3 b", "\"a\" \"NEAR\" \"3\" \"b\"")] // NEAR neutralised
    [InlineData("(x OR y)", "\"x\" \"OR\" \"y\"")]           // grouping stripped
    public void Fts5_syntax_is_neutralised(string raw, string expected)
        => Fts5QuerySanitiser.ToMatchExpression(raw, 16).Should().Be(expected);

    [Fact]
    public void Embedded_quotes_are_doubled_not_dropped()
        => Fts5QuerySanitiser.ToMatchExpression("say \"hi\"", 16).Should().Be("\"say\" \"hi\"");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! ??? ***")]
    public void Queries_with_no_usable_tokens_return_null(string? raw)
        => Fts5QuerySanitiser.ToMatchExpression(raw, 16).Should().BeNull();

    [Fact]
    public void Token_count_is_capped()
        => Fts5QuerySanitiser.ToMatchExpression("a b c d e", 3).Should().Be("\"a\" \"b\" \"c\"");

    [Fact]
    public void Unicode_letters_and_digits_survive()
        => Fts5QuerySanitiser.ToMatchExpression("café 2026", 16).Should().Be("\"café\" \"2026\"");
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Mostlylucid.BotDetection.WebMcp.Test/ --filter "FullyQualifiedName~Fts5QuerySanitiserTests"`
Expected: FAIL — compile error, `Fts5QuerySanitiser` does not exist.

- [ ] **Step 3: Write the implementation**

`src/Mostlylucid.BotDetection.WebMcp/Index/Fts5QuerySanitiser.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace Mostlylucid.BotDetection.WebMcp.Index;

/// <summary>
///     Converts an arbitrary caller-supplied search string into an FTS5 MATCH expression
///     that has no syntax surface.
///     <para>
///         FTS5 MATCH accepts a query language (AND / OR / NOT / NEAR / prefix <c>*</c> /
///         column filters / parentheses / phrase quoting). Passing raw caller input to it is
///         both a syntax-error source and a query-steering vector. Every token is therefore
///         extracted as a run of Unicode letters or digits, wrapped in double quotes (making
///         it a literal string token that FTS5 never interprets as an operator), and joined
///         by whitespace — which FTS5 reads as implicit AND.
///     </para>
/// </summary>
public static class Fts5QuerySanitiser
{
    /// <summary>
    ///     Returns a safe MATCH expression, or <c>null</c> when <paramref name="raw"/>
    ///     contains no indexable token. A null return means "no results", not "error".
    /// </summary>
    /// <param name="raw">Caller-supplied query text.</param>
    /// <param name="maxTokens">Upper bound on tokens, so one query cannot be unboundedly costly.</param>
    public static string? ToMatchExpression(string? raw, int maxTokens)
    {
        if (string.IsNullOrWhiteSpace(raw) || maxTokens <= 0) return null;

        var builder = new StringBuilder();
        var token = new StringBuilder();
        var emitted = 0;

        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch))
            {
                token.Append(ch);
                continue;
            }

            if (!TryEmit(builder, token, ref emitted, maxTokens)) break;
        }

        TryEmit(builder, token, ref emitted, maxTokens);

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static bool TryEmit(StringBuilder builder, StringBuilder token, ref int emitted, int maxTokens)
    {
        if (token.Length == 0) return true;

        var value = token.ToString();
        token.Clear();

        if (emitted >= maxTokens) return false;

        if (builder.Length > 0) builder.Append(' ');
        builder.Append('"').Append(value).Append('"');
        emitted++;
        return emitted < maxTokens;
    }
}
```

Note: because tokens are runs of letters/digits only, a `"` can never appear *inside* a
token, so no quote-doubling is needed — the `Embedded_quotes_are_doubled_not_dropped` test
passes because the quote is a delimiter, not part of a token.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Mostlylucid.BotDetection.WebMcp.Test/ --filter "FullyQualifiedName~Fts5QuerySanitiserTests"`
Expected: PASS (12 cases — the two `[Theory]` blocks contribute 4 each)

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection.WebMcp/Index tests/Mostlylucid.BotDetection.WebMcp.Test/Fts5QuerySanitiserTests.cs
git commit -m "feat(webmcp): FTS5 query sanitiser"
```

---

### Task 3: `ISiteIndex` + `Fts5SiteIndex`

**Files:**
- Create: `src/Mostlylucid.BotDetection.WebMcp/Index/ISiteIndex.cs`
- Create: `src/Mostlylucid.BotDetection.WebMcp/Index/Fts5SiteIndex.cs`
- Test: `tests/Mostlylucid.BotDetection.WebMcp.Test/Fts5SiteIndexTests.cs`
- Test: `tests/Mostlylucid.BotDetection.WebMcp.Test/TestHelpers.cs`

**Interfaces:**
- Consumes: `Fts5QuerySanitiser.ToMatchExpression` (Task 2), `IndexOptions` (Task 1).
- Produces:
  - `record IndexedDocument(string Host, string Url, string Path, string Title, string Body, string ContentHash, string? ETag, string? LastModified, string Source)`
  - `record SearchHit(string Url, string Title, string Snippet, double Score)`
  - `record DocumentRef(string Url, string? ETag, string? LastModified)`
  - `interface ISiteIndex { Task IndexAsync(IndexedDocument, CancellationToken); Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, CancellationToken); Task<DocumentRef?> LookupAsync(string url, CancellationToken); Task<int> CountAsync(CancellationToken); }`

- [ ] **Step 1: Write the failing tests**

`tests/Mostlylucid.BotDetection.WebMcp.Test/TestHelpers.cs`:

```csharp
using Mostlylucid.BotDetection.WebMcp.Index;

namespace Mostlylucid.BotDetection.WebMcp.Tests;

/// <summary>Creates a throwaway SQLite file per test and deletes it on dispose.</summary>
internal sealed class TempDb : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"webmcp-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = Path + suffix;
            if (File.Exists(file)) try { File.Delete(file); } catch { /* best effort */ }
        }
    }
}

internal static class Docs
{
    internal static IndexedDocument Page(
        string url = "https://example.test/docs/intro",
        string title = "Introduction",
        string body = "StyloBot detects bots using a blackboard architecture.",
        string hash = "hash-1") =>
        new(
            Host: new Uri(url).Host,
            Url: url,
            Path: new Uri(url).AbsolutePath,
            Title: title,
            Body: body,
            ContentHash: hash,
            ETag: "\"etag-1\"",
            LastModified: null,
            Source: "passive");
}
```

`tests/Mostlylucid.BotDetection.WebMcp.Test/Fts5SiteIndexTests.cs`:

```csharp
using FluentAssertions;
using Mostlylucid.BotDetection.WebMcp.Index;
using Mostlylucid.BotDetection.WebMcp.Options;
using Xunit;

namespace Mostlylucid.BotDetection.WebMcp.Tests;

public sealed class Fts5SiteIndexTests
{
    private static Fts5SiteIndex Create(TempDb db, IndexOptions? options = null)
        => new(new IndexOptions
        {
            StorePath = db.Path,
            MaxDocuments = options?.MaxDocuments ?? 50_000,
            MaxExcerptBytes = options?.MaxExcerptBytes ?? 8192,
            MaxQueryTokens = options?.MaxQueryTokens ?? 16
        });

    [Fact]
    public async Task Indexed_document_is_findable_by_body_term()
    {
        using var db = new TempDb();
        var index = Create(db);

        await index.IndexAsync(Docs.Page(), CancellationToken.None);
        var hits = await index.SearchAsync("blackboard", 10, CancellationToken.None);

        hits.Should().ContainSingle();
        hits[0].Url.Should().Be("https://example.test/docs/intro");
        hits[0].Title.Should().Be("Introduction");
        hits[0].Snippet.Should().Contain("blackboard");
        hits[0].Score.Should().BeGreaterThan(0, "score is exposed as positive relevance");
    }

    [Fact]
    public async Task Title_matches_are_findable()
    {
        using var db = new TempDb();
        var index = Create(db);

        await index.IndexAsync(Docs.Page(), CancellationToken.None);
        var hits = await index.SearchAsync("Introduction", 10, CancellationToken.None);

        hits.Should().ContainSingle();
    }

    [Fact]
    public async Task Reindexing_same_url_replaces_rather_than_duplicates()
    {
        using var db = new TempDb();
        var index = Create(db);

        await index.IndexAsync(Docs.Page(body: "original text", hash: "h1"), CancellationToken.None);
        await index.IndexAsync(Docs.Page(body: "replacement text", hash: "h2"), CancellationToken.None);

        (await index.CountAsync(CancellationToken.None)).Should().Be(1);
        (await index.SearchAsync("original", 10, CancellationToken.None)).Should().BeEmpty();
        (await index.SearchAsync("replacement", 10, CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task Search_respects_limit()
    {
        using var db = new TempDb();
        var index = Create(db);

        for (var i = 0; i < 5; i++)
            await index.IndexAsync(
                Docs.Page(url: $"https://example.test/p{i}", body: "shared term here", hash: $"h{i}"),
                CancellationToken.None);

        (await index.SearchAsync("shared", 3, CancellationToken.None)).Should().HaveCount(3);
    }

    [Fact]
    public async Task Unusable_query_returns_empty_not_throws()
    {
        using var db = new TempDb();
        var index = Create(db);
        await index.IndexAsync(Docs.Page(), CancellationToken.None);

        (await index.SearchAsync("!!!", 10, CancellationToken.None)).Should().BeEmpty();
        (await index.SearchAsync("", 10, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Fts5_operators_in_query_do_not_throw()
    {
        using var db = new TempDb();
        var index = Create(db);
        await index.IndexAsync(Docs.Page(), CancellationToken.None);

        var act = async () => await index.SearchAsync("\"unclosed AND (x NEAR/2", 10, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Body_is_truncated_to_max_excerpt_bytes()
    {
        using var db = new TempDb();
        var index = Create(db, new IndexOptions { MaxExcerptBytes = 32 });

        await index.IndexAsync(Docs.Page(body: new string('a', 500) + " needle"), CancellationToken.None);

        (await index.SearchAsync("needle", 10, CancellationToken.None))
            .Should().BeEmpty("the needle sits past the truncation point");
    }

    [Fact]
    public async Task Lookup_returns_revalidation_metadata()
    {
        using var db = new TempDb();
        var index = Create(db);
        await index.IndexAsync(Docs.Page(), CancellationToken.None);

        var found = await index.LookupAsync("https://example.test/docs/intro", CancellationToken.None);
        found.Should().NotBeNull();
        found!.ETag.Should().Be("\"etag-1\"");

        (await index.LookupAsync("https://example.test/nope", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Document_count_is_pruned_to_max_documents()
    {
        using var db = new TempDb();
        var index = Create(db, new IndexOptions { MaxDocuments = 3 });

        for (var i = 0; i < 6; i++)
            await index.IndexAsync(
                Docs.Page(url: $"https://example.test/p{i}", hash: $"h{i}"), CancellationToken.None);

        (await index.CountAsync(CancellationToken.None)).Should().BeLessThanOrEqualTo(3);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Mostlylucid.BotDetection.WebMcp.Test/ --filter "FullyQualifiedName~Fts5SiteIndexTests"`
Expected: FAIL — compile error, `Fts5SiteIndex` does not exist.

- [ ] **Step 3: Write the contract**

`src/Mostlylucid.BotDetection.WebMcp/Index/ISiteIndex.cs`:

```csharp
namespace Mostlylucid.BotDetection.WebMcp.Index;

/// <summary>A page as handed to the index. <paramref name="Body"/> is clean text (Markdown).</summary>
public sealed record IndexedDocument(
    string Host,
    string Url,
    string Path,
    string Title,
    string Body,
    string ContentHash,
    string? ETag,
    string? LastModified,
    string Source);

/// <summary>One search result. <paramref name="Score"/> is positive; higher is more relevant.</summary>
public sealed record SearchHit(string Url, string Title, string Snippet, double Score);

/// <summary>Just enough of an indexed document to issue a conditional GET for it.</summary>
public sealed record DocumentRef(string Url, string? ETag, string? LastModified);

/// <summary>
///     Retrieval seam over the site corpus. FOSS ships exactly one implementation
///     (<see cref="Fts5SiteIndex"/>); the interface exists so commercial can add scale
///     (pgvector / HNSW) without changing any caller.
/// </summary>
public interface ISiteIndex
{
    /// <summary>Insert or replace the document for this URL. Idempotent per URL.</summary>
    Task IndexAsync(IndexedDocument document, CancellationToken ct);

    /// <summary>
    ///     Ranked search. Returns empty (never throws) when the query yields no usable
    ///     tokens or matches nothing.
    /// </summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, CancellationToken ct);

    /// <summary>Revalidation metadata for a URL, or null when it was never indexed.</summary>
    Task<DocumentRef?> LookupAsync(string url, CancellationToken ct);

    /// <summary>Current indexed document count.</summary>
    Task<int> CountAsync(CancellationToken ct);
}
```

- [ ] **Step 4: Write the FTS5 implementation**

`src/Mostlylucid.BotDetection.WebMcp/Index/Fts5SiteIndex.cs`:

```csharp
using System.Text;
using Microsoft.Data.Sqlite;
using Mostlylucid.BotDetection.WebMcp.Options;

namespace Mostlylucid.BotDetection.WebMcp.Index;

/// <summary>
///     SQLite FTS5 site index. One storage engine, no native extensions — the FOSS
///     zero-dependency and AOT posture holds.
///     <para>
///         <c>documents_fts</c> is a STANDARD FTS5 table, not <c>content='documents'</c>
///         external-content. External-content tables require manual index sync (issuing
///         <c>'delete'</c> command rows carrying the exact previous column values), and the
///         contentless variant cannot serve <c>snippet()</c>. A standard table owns
///         title/body directly, so re-indexing is a plain delete-then-insert by rowid.
///         Body text therefore lives only in the FTS5 table; <c>documents</c> holds metadata.
///     </para>
/// </summary>
public sealed class Fts5SiteIndex : ISiteIndex
{
    private readonly IndexOptions _options;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _initialised;

    public Fts5SiteIndex(IndexOptions options)
    {
        _options = options;
        var dir = System.IO.Path.GetDirectoryName(options.StorePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = $"Data Source={options.StorePath}";
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_initialised) return;
        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialised) return;

            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS documents (
                    id            INTEGER PRIMARY KEY,
                    host          TEXT NOT NULL,
                    url           TEXT NOT NULL UNIQUE,
                    path          TEXT NOT NULL,
                    title         TEXT NOT NULL,
                    content_hash  TEXT NOT NULL,
                    etag          TEXT NULL,
                    last_modified TEXT NULL,
                    byte_len      INTEGER NOT NULL,
                    indexed_utc   TEXT NOT NULL,
                    source        TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_documents_host ON documents(host);
                CREATE INDEX IF NOT EXISTS ix_documents_indexed ON documents(indexed_utc);
                CREATE VIRTUAL TABLE IF NOT EXISTS documents_fts
                    USING fts5(title, body, tokenize='porter unicode61');
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _initialised = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>Truncates to a byte budget without splitting a UTF-8 sequence.</summary>
    internal static string TruncateUtf8(string text, int maxBytes)
    {
        if (maxBytes <= 0) return string.Empty;
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;

        var bytes = Encoding.UTF8.GetBytes(text);
        var length = maxBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80) length--;
        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    public async Task IndexAsync(IndexedDocument document, CancellationToken ct)
    {
        var body = TruncateUtf8(document.Body, _options.MaxExcerptBytes);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        long rowId;
        await using (var upsert = conn.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO documents (host, url, path, title, content_hash, etag, last_modified,
                                       byte_len, indexed_utc, source)
                VALUES (@host, @url, @path, @title, @hash, @etag, @lastmod, @len, @now, @source)
                ON CONFLICT(url) DO UPDATE SET
                    title = excluded.title, content_hash = excluded.content_hash,
                    etag = excluded.etag, last_modified = excluded.last_modified,
                    byte_len = excluded.byte_len, indexed_utc = excluded.indexed_utc,
                    source = excluded.source
                RETURNING id;
                """;
            upsert.Parameters.AddWithValue("@host", document.Host);
            upsert.Parameters.AddWithValue("@url", document.Url);
            upsert.Parameters.AddWithValue("@path", document.Path);
            upsert.Parameters.AddWithValue("@title", document.Title);
            upsert.Parameters.AddWithValue("@hash", document.ContentHash);
            upsert.Parameters.AddWithValue("@etag", (object?)document.ETag ?? DBNull.Value);
            upsert.Parameters.AddWithValue("@lastmod", (object?)document.LastModified ?? DBNull.Value);
            upsert.Parameters.AddWithValue("@len", Encoding.UTF8.GetByteCount(body));
            upsert.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            upsert.Parameters.AddWithValue("@source", document.Source);
            rowId = Convert.ToInt64(await upsert.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }

        await using (var replace = conn.CreateCommand())
        {
            replace.Transaction = tx;
            replace.CommandText = """
                DELETE FROM documents_fts WHERE rowid = @id;
                INSERT INTO documents_fts (rowid, title, body) VALUES (@id, @title, @body);
                """;
            replace.Parameters.AddWithValue("@id", rowId);
            replace.Parameters.AddWithValue("@title", document.Title);
            replace.Parameters.AddWithValue("@body", body);
            await replace.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var prune = conn.CreateCommand())
        {
            prune.Transaction = tx;
            prune.CommandText = """
                DELETE FROM documents_fts WHERE rowid IN (
                    SELECT id FROM documents
                    ORDER BY indexed_utc DESC
                    LIMIT -1 OFFSET @max);
                DELETE FROM documents WHERE id IN (
                    SELECT id FROM documents
                    ORDER BY indexed_utc DESC
                    LIMIT -1 OFFSET @max);
                """;
            prune.Parameters.AddWithValue("@max", _options.MaxDocuments);
            await prune.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var match = Fts5QuerySanitiser.ToMatchExpression(query, _options.MaxQueryTokens);
        if (match is null || limit <= 0) return Array.Empty<SearchHit>();

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        // bm25() returns a NEGATIVE score where more-negative is more relevant, so ASC is
        // "best first" and negating it gives callers a positive, higher-is-better number.
        cmd.CommandText = """
            SELECT d.url, d.title,
                   snippet(documents_fts, 1, '', '', '…', 20) AS excerpt,
                   -bm25(documents_fts) AS relevance
            FROM documents_fts
            JOIN documents d ON d.id = documents_fts.rowid
            WHERE documents_fts MATCH @match
            ORDER BY bm25(documents_fts)
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@match", match);
        cmd.Parameters.AddWithValue("@limit", limit);

        var hits = new List<SearchHit>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            hits.Add(new SearchHit(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3)));

        return hits;
    }

    public async Task<DocumentRef?> LookupAsync(string url, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT url, etag, last_modified FROM documents WHERE url = @url;";
        cmd.Parameters.AddWithValue("@url", url);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

        return new DocumentRef(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM documents;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Mostlylucid.BotDetection.WebMcp.Test/ --filter "FullyQualifiedName~Fts5SiteIndexTests"`
Expected: PASS (9 tests)

If `Fts5_operators_in_query_do_not_throw` fails, the sanitiser is being bypassed — check that `SearchAsync` calls it before touching SQL.

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection.WebMcp/Index tests/Mostlylucid.BotDetection.WebMcp.Test
git commit -m "feat(webmcp): FTS5 site index"
```

---

### Task 4: `SiteCorpusWriter` — the write-behind drain

Indexing must never touch the request thread. This is a bounded channel plus one background
drain task, following the `WriteBehindLfuStore` posture in
`src/Mostlylucid.BotDetection/Storage/WriteBehindLfuStore.cs`: enqueue returns in
microseconds, a full queue drops rather than blocks, and one writer owns the connection.

**Files:**
- Create: `src/Mostlylucid.BotDetection.WebMcp/Corpus/CapturedPage.cs`
- Create: `src/Mostlylucid.BotDetection.WebMcp/Corpus/SiteCorpusWriter.cs`
- Test: `tests/Mostlylucid.BotDetection.WebMcp.Test/SiteCorpusWriterTests.cs`

**Interfaces:**
- Consumes: `ISiteIndex` (Task 3), `CorpusOptions` (Task 1).
- Produces: `SiteCorpusWriter` with `bool TryEnqueue(IndexedDocument)`, `Task FlushAsync(CancellationToken)` (test seam — drains everything queued), `ValueTask DisposeAsync()`. Also `long Dropped { get; }`.

- [ ] **Step 1: Write the failing tests**

`tests/Mostlylucid.BotDetection.WebMcp.Test/SiteCorpusWriterTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.WebMcp.Corpus;
using Mostlylucid.BotDetection.WebMcp.Index;
using Mostlylucid.BotDetection.WebMcp.Options;
using Xunit;

namespace Mostlylucid.BotDetection.WebMcp.Tests;

internal sealed class RecordingIndex : ISiteIndex
{
    public List<IndexedDocument> Indexed { get; } = new();
    public bool ThrowOnIndex { get; set; }

    public Task IndexAsync(IndexedDocument document, CancellationToken ct)
    {
        if (ThrowOnIndex) throw new InvalidOperationException("simulated index failure");
        lock (Indexed) Indexed.Add(document);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SearchHit>>(Array.Empty<SearchHit>());
    public Task<DocumentRef?> LookupAsync(string url, CancellationToken ct) => Task.FromResult<DocumentRef?>(null);
    public Task<int> CountAsync(CancellationToken ct) => Task.FromResult(Indexed.Count);
}

public sealed class SiteCorpusWriterTests
{
    private static SiteCorpusWriter Create(ISiteIndex index, int capacity = 1024)
        => new(index,
               new CorpusOptions
               {
                   QueueCapacity = capacity,
                   DrainInterval = TimeSpan.FromMilliseconds(20),
                   DrainBatchSize = 32
               },
               NullLogger<SiteCorpusWriter>.Instance);

    [Fact]
    public async Task Enqueued_document_reaches_the_index()
    {
        var index = new RecordingIndex();
        await using var writer = Create(index);

        writer.TryEnqueue(Docs.Page()).Should().BeTrue();
        await writer.FlushAsync(CancellationToken.None);

        index.Indexed.Should().ContainSingle().Which.Url.Should().Be("https://example.test/docs/intro");
    }

    [Fact]
    public async Task Enqueue_returns_false_when_queue_is_full_and_never_blocks()
    {
        var index = new RecordingIndex();
        await using var writer = Create(index, capacity: 1);

        // Fill well past capacity; DropWrite means excess is refused, not awaited.
        var results = Enumerable.Range(0, 200)
            .Select(i => writer.TryEnqueue(Docs.Page(url: $"https://example.test/p{i}", hash: $"h{i}")))
            .ToList();

        results.Should().Contain(false, "a full bounded queue must shed rather than block the request thread");
        writer.Dropped.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Index_failure_does_not_kill_the_drain_loop()
    {
        var index = new RecordingIndex { ThrowOnIndex = true };
        await using var writer = Create(index);

        writer.TryEnqueue(Docs.Page(url: "https://example.test/bad", hash: "h-bad")).Should().BeTrue();
        await writer.FlushAsync(CancellationToken.None);

        index.ThrowOnIndex = false;
        writer.TryEnqueue(Docs.Page(url: "https://example.test/good", hash: "h-good")).Should().BeTrue();
        await writer.FlushAsync(CancellationToken.None);

        index.Indexed.Should().ContainSingle().Which.Url.Should().Be("https://example.test/good");
    }

    [Fact]
    public async Task Flush_drains_everything_queued()
    {
        var index = new RecordingIndex();
        await using var writer = Create(index);

        for (var i = 0; i < 50; i++)
            writer.TryEnqueue(Docs.Page(url: $"https://example.test/p{i}", hash: $"h{i}"));

        await writer.FlushAsync(CancellationToken.None);

        index.Indexed.Should().HaveCount(50);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Mostlylucid.BotDetection.WebMcp.Test/ --filter "FullyQualifiedName~SiteCorpusWriterTests"`
Expected: FAIL — compile error, `SiteCorpusWriter` does not exist.

- [ ] **Step 3: Write `CapturedPage`**

`src/Mostlylucid.BotDetection.WebMcp/Corpus/CapturedPage.cs`:

```csharp
namespace Mostlylucid.BotDetection.WebMcp.Corpus;

/// <summary>
///     Raw HTML lifted off a proxied response, before extraction. Kept separate from
///     <c>IndexedDocument</c> so the capture policy does no extraction work on the
///     request thread — conversion happens on the drain.
/// </summary>
public sealed record CapturedPage(string Url, string Html, string? ETag, string? LastModified);
```

- [ ] **Step 4: Write `SiteCorpusWriter`**

`src/Mostlylucid.BotDetection.WebMcp/Corpus/SiteCorpusWriter.cs`:

```csharp
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.WebMcp.Index;
using Mostlylucid.BotDetection.WebMcp.Options;

namespace Mostlylucid.BotDetection.WebMcp.Corpus;

/// <summary>
///     Bounded write-behind drain between request-thread capture and the durable index.
///     <para>
///         Follows the <c>WriteBehindLfuStore</c> posture: <see cref="TryEnqueue"/> returns in
///         microseconds and NEVER blocks or awaits, a full queue sheds the write rather than
///         applying backpressure to a live request, and exactly one background task owns the
///         index connection. Losing an enqueue costs one page of index freshness, which is a
///         far better trade than adding latency to proxied traffic.
///     </para>
/// </summary>
public sealed class SiteCorpusWriter : IAsyncDisposable
{
    private readonly ISiteIndex _index;
    private readonly CorpusOptions _options;
    private readonly ILogger<SiteCorpusWriter> _logger;
    private readonly Channel<IndexedDocument> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _drain;
    private long _dropped;

    /// <summary>Count of documents shed because the queue was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public SiteCorpusWriter(ISiteIndex index, CorpusOptions options, ILogger<SiteCorpusWriter> logger)
    {
        _index = index;
        _options = options;
        _logger = logger;
        _queue = Channel.CreateBounded<IndexedDocument>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
        _drain = Task.Run(() => DrainLoopAsync(_shutdown.Token));
    }

    /// <summary>
    ///     Non-blocking enqueue. Returns false when the queue is full (the document is
    ///     dropped) — callers must treat false as "fine, moving on", never as an error.
    /// </summary>
    public bool TryEnqueue(IndexedDocument document)
    {
        if (_queue.Writer.TryWrite(document)) return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }

    private async Task DrainLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                await DrainAvailableAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebMCP corpus drain loop terminated unexpectedly.");
        }
    }

    private async Task DrainAvailableAsync(CancellationToken ct)
    {
        var written = 0;
        while (written < _options.DrainBatchSize && _queue.Reader.TryRead(out var document))
        {
            written++;
            try
            {
                await _index.IndexAsync(document, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One bad document must never stop the drain — log and keep going.
                _logger.LogWarning(ex, "WebMCP indexing failed for {Url}; skipping.", document.Url);
            }
        }
    }

    /// <summary>
    ///     Drains everything currently queued. Test seam and shutdown helper — production
    ///     traffic relies on the background loop, not this.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        // The background loop may be mid-batch; give it a moment to settle, then drain
        // whatever remains on this thread.
        for (var attempt = 0; attempt < 50 && _queue.Reader.Count > 0; attempt++)
            await Task.Delay(_options.DrainInterval, ct).ConfigureAwait(false);

        while (_queue.Reader.TryRead(out var document))
        {
            try
            {
                await _index.IndexAsync(document, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WebMCP indexing failed for {Url} during flush; skipping.", document.Url);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try { await _drain.ConfigureAwait(false); } catch { /* shutdown */ }
        _shutdown.Dispose();
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Mostlylucid.BotDetection.WebMcp.Test/ --filter "FullyQualifiedName~SiteCorpusWriterTests"`
Expected: PASS (4 tests)

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection.WebMcp/Corpus tests/Mostlylucid.BotDetection.WebMcp.Test/SiteCorpusWriterTests.cs
git commit -m "feat(webmcp): write-behind corpus drain"
```

---

### Task 5: `WebMcpIndexActionPolicy` — passive capture

**Files:**
- Create: `src/Mostlylucid.BotDetection.WebMcp/Actions/WebMcpIndexActionPolicy.cs`
- Test: `tests/Mostlylucid.BotDetection.WebMcp.Test/WebMcpIndexActionPolicyTests.cs`
- Modify: `tests/Mostlylucid.BotDetection.WebMcp.Test/TestHelpers.cs` (add HTTP + policy helpers)

**Interfaces:**
- Consumes: `SiteCorpusWriter.TryEnqueue` (Task 4), `WebMcpOptions` (Task 1), `ILayoutExtractor` (StyloExtract), `ResponseBodyCapture` + `BodyInterceptStream` (from `Mostlylucid.BotDetection.StyloExtract.Internals`).
- Produces: `WebMcpIndexActionPolicy` — `IActionPolicy` with `Name = "webmcp-index"`, `ActionType = ActionType.Escalate`.

`ActionType.Escalate` is correct here: the policy raises out-of-band work and does not
interfere with the request. It always returns `ActionResult.Allowed()`.

- [ ] **Step 1: Add the shared test helpers**

Append to `tests/Mostlylucid.BotDetection.WebMcp.Test/TestHelpers.cs`:

```csharp
// --- appended: HTTP + extractor helpers -----------------------------------

using System.Text;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using StyloExtract.Abstractions;

internal static class Evidence
{
    internal static AggregatedEvidence Bot() => new()
    {
        BotProbability = 0.95, Confidence = 0.9, RiskBand = RiskBand.High, PrimaryBotType = BotType.AiBot
    };

    internal static AggregatedEvidence Human() => new()
    {
        BotProbability = 0.05, Confidence = 0.9, RiskBand = RiskBand.VeryLow
    };
}

internal sealed class FakeExtractor : ILayoutExtractor
{
    public string MarkdownToReturn { get; set; } = "Extracted body text.";
    public string TitleToReturn { get; set; } = "Extracted Title";
    public int CallCount { get; private set; }

    public Task<ExtractionResult> ExtractAsync(
        string html, Uri? sourceUri = null, ExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(new ExtractionResult
        {
            SourceUri = sourceUri,
            Title = TitleToReturn,
            Markdown = MarkdownToReturn,
            Blocks = [],
            Stats = new ExtractionStats
            {
                BlockCount = 1, FingerprintShingleCount = 1,
                ParseTime = TimeSpan.Zero, FingerprintTime = TimeSpan.Zero,
                MatchTime = TimeSpan.Zero, RenderTime = TimeSpan.Zero
            },
            Match = new LayoutMatch
            {
                TemplateId = Guid.Empty, TemplateVersion = 1, FingerprintHex = "abc",
                Status = MatchStatus.FastPathHit, Similarity = 1.0, ObservationCount = 1,
                LatencyMatch = TimeSpan.Zero, LatencyTotal = TimeSpan.Zero
            }
        });
    }
}

internal sealed class ThrowingExtractor : ILayoutExtractor
{
    public Task<ExtractionResult> ExtractAsync(
        string html, Uri? sourceUri = null, ExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("simulated extractor failure");
}

internal static class Http
{
    internal static DefaultHttpContext HtmlContext(string path = "/docs/intro")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("example.test");
        context.Request.Path = path;
        context.Request.Method = "GET";
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.StatusCode = 200;
        context.Response.Body = new MemoryStream();
        return context;
    }

    /// <summary>
    ///     Reproduces the StyloBot middleware call order: run the policy (which installs the
    ///     interceptor), let "downstream" write the body, then flush to fire the transform.
    ///     Returns the bytes that reached the original stream.
    /// </summary>
    internal static async Task<string> RunAndFlushAsync(
        DefaultHttpContext context,
        Func<DefaultHttpContext, Task> executePolicy,
        string downstreamBody,
        MemoryStream originalBody)
    {
        await executePolicy(context);
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(downstreamBody));
        await context.Response.Body.FlushAsync();
        return Encoding.UTF8.GetString(originalBody.ToArray());
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/Mostlylucid.BotDetection.WebMcp.Test/WebMcpIndexActionPolicyTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.WebMcp.Actions;
using Mostlylucid.BotDetection.WebMcp.Corpus;
using Mostlylucid.BotDetection.WebMcp.Options;
using Mostlylucid.BotDetection.StyloExtract.Internals;
using StyloExtract.Abstractions;
using Xunit;

namespace Mostlylucid.BotDetection.WebMcp.Tests;

public sealed class WebMcpIndexActionPolicyTests
{
    private static (WebMcpIndexActionPolicy Policy, RecordingIndex Index, SiteCorpusWriter Writer)
        Create(ILayoutExtractor? extractor = null, bool enabled = true)
    {
        var index = new RecordingIndex();
        var options = new WebMcpOptions { Enabled = enabled };
        options.Corpus.DrainInterval = TimeSpan.FromMilliseconds(10);
        var writer = new SiteCorpusWriter(index, options.Corpus, NullLogger<SiteCorpusWriter>.Instance);
        var policy = new WebMcpIndexActionPolicy(
            extractor ?? new FakeExtractor(),
            writer,
            new ResponseBodyCapture(),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<WebMcpIndexActionPolicy>.Instance);
        return (policy, index, writer);
    }

    [Fact]
    public async Task Policy_identity_is_stable()
    {
        // SiteCorpusWriter is IAsyncDisposable only — `using var` on it is a compile error.
        var (policy, _, writer) = Create();
        await using var _w = writer;

        policy.Name.Should().Be("webmcp-index");
        policy.ActionType.Should().Be(Mostlylucid.BotDetection.Actions.ActionType.Escalate);
    }

    [Fact]
    public async Task Html_response_is_captured_and_indexed()
    {
        var (policy, index, writer) = Create();
        await using var _w = writer;

        var context = Http.HtmlContext("/docs/intro");
        var original = (MemoryStream)context.Response.Body;

        var body = await Http.RunAndFlushAsync(
            context, c => policy.ExecuteAsync(c, Evidence.Human()),
            "<html><body>hello</body></html>", original);

        body.Should().Be("<html><body>hello</body></html>", "capture must be byte-transparent");

        await writer.FlushAsync(CancellationToken.None);
        index.Indexed.Should().ContainSingle();
        index.Indexed[0].Url.Should().Be("https://example.test/docs/intro");
        index.Indexed[0].Title.Should().Be("Extracted Title");
        index.Indexed[0].Body.Should().Be("Extracted body text.");
        index.Indexed[0].Source.Should().Be("passive");
    }

    [Fact]
    public async Task Policy_always_allows_the_request_to_continue()
    {
        var (policy, _, writer) = Create();
        await using var _w = writer;

        var result = await policy.ExecuteAsync(Http.HtmlContext(), Evidence.Bot());

        result.Continue.Should().BeTrue("indexing must never interfere with traffic");
    }

    [Fact]
    public async Task Non_html_response_is_not_indexed_and_passes_through_unchanged()
    {
        var (policy, index, writer) = Create();
        await using var _w = writer;

        var context = Http.HtmlContext();
        context.Response.ContentType = "application/json";
        var original = (MemoryStream)context.Response.Body;

        var body = await Http.RunAndFlushAsync(
            context, c => policy.ExecuteAsync(c, Evidence.Human()), "{\"a\":1}", original);

        body.Should().Be("{\"a\":1}");
        await writer.FlushAsync(CancellationToken.None);
        index.Indexed.Should().BeEmpty();
    }

    [Fact]
    public async Task Extractor_failure_leaves_the_response_intact()
    {
        var (policy, index, writer) = Create(new ThrowingExtractor());
        await using var _w = writer;

        var context = Http.HtmlContext();
        var original = (MemoryStream)context.Response.Body;

        var body = await Http.RunAndFlushAsync(
            context, c => policy.ExecuteAsync(c, Evidence.Human()),
            "<html><body>intact</body></html>", original);

        body.Should().Be("<html><body>intact</body></html>", "fail-open is absolute");
        await writer.FlushAsync(CancellationToken.None);
        index.Indexed.Should().BeEmpty();
    }

    [Fact]
    public async Task Disabled_pack_captures_nothing()
    {
        var (policy, index, writer) = Create(enabled: false);
        await using var _w = writer;

        var context = Http.HtmlContext();
        var original = (MemoryStream)context.Response.Body;

        await Http.RunAndFlushAsync(
            context, c => policy.ExecuteAsync(c, Evidence.Human()),
            "<html><body>x</body></html>", original);

        await writer.FlushAsync(CancellationToken.None);
        index.Indexed.Should().BeEmpty();
    }

    [Fact]
    public async Task Error_status_responses_are_not_indexed()
    {
        var (policy, index, writer) = Create();
        await using var _w = writer;

        var context = Http.HtmlContext();
        context.Response.StatusCode = 404;
        var original = (MemoryStream)context.Response.Body;

        await Http.RunAndFlushAsync(
            context, c => policy.ExecuteAsync(c, Evidence.Human()),
            "<html><body>not found</body></html>", original);

        await writer.FlushAsync(CancellationToken.None);
        index.Indexed.Should().BeEmpty("a 404 body is not site content");
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Mostlylucid.BotDetection.WebMcp.Test/ --filter "FullyQualifiedName~WebMcpIndexActionPolicyTests"`
Expected: FAIL — compile error, `WebMcpIndexActionPolicy` does not exist.

- [ ] **Step 4: Write the policy**

`src/Mostlylucid.BotDetection.WebMcp/Actions/WebMcpIndexActionPolicy.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.StyloExtract.Internals;
using Mostlylucid.BotDetection.WebMcp.Corpus;
using Mostlylucid.BotDetection.WebMcp.Index;
using Mostlylucid.BotDetection.WebMcp.Options;
using StyloExtract.Abstractions;

namespace Mostlylucid.BotDetection.WebMcp.Actions;

/// <summary>
///     Named action policy <c>webmcp-index</c>. Observes proxied HTML and feeds it to the
///     site index. It never alters the response: the interceptor's transform returns null,
///     which is <see cref="BodyInterceptStream"/>'s pass-through contract (original bytes
///     written back verbatim, BOM and encoding preserved).
///     <para>
///         <see cref="ActionType.Escalate"/> because it raises out-of-band work and leaves
///         the request alone. Always returns <see cref="ActionResult.Allowed"/>.
///     </para>
/// </summary>
public sealed class WebMcpIndexActionPolicy : IActionPolicy
{
    private readonly ILayoutExtractor _extractor;
    private readonly SiteCorpusWriter _writer;
    private readonly ResponseBodyCapture _capture;
    private readonly WebMcpOptions _options;
    private readonly ILogger<WebMcpIndexActionPolicy> _logger;

    public WebMcpIndexActionPolicy(
        ILayoutExtractor extractor,
        SiteCorpusWriter writer,
        ResponseBodyCapture capture,
        IOptions<WebMcpOptions> options,
        ILogger<WebMcpIndexActionPolicy> logger)
    {
        _extractor = extractor;
        _writer = writer;
        _capture = capture;
        _options = options.Value;
        _logger = logger;
    }

    public string Name => "webmcp-index";
    public ActionType ActionType => ActionType.Escalate;

    public Task<ActionResult> ExecuteAsync(
        HttpContext context, AggregatedEvidence evidence, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || !_options.Corpus.PassiveCapture)
            return Task.FromResult(ActionResult.Allowed("WebMCP indexing disabled"));

        _capture.InstallInterceptor(context, async html =>
        {
            try
            {
                await CaptureAsync(context, html).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Fail-open is absolute: a capture failure must never change the response.
                _logger.LogWarning(ex, "WebMCP capture failed for {Path}; response unchanged.", context.Request.Path);
            }

            // null => BodyInterceptStream writes the ORIGINAL bytes back unchanged.
            return null;
        });

        return Task.FromResult(ActionResult.Allowed("WebMCP indexing armed"));
    }

    private async Task CaptureAsync(HttpContext context, string html)
    {
        if (context.Response.StatusCode != 200) return;
        if (string.IsNullOrWhiteSpace(html)) return;

        var url = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;

        var result = await _extractor
            .ExtractAsync(html, uri, new ExtractionOptions { Profile = ExtractionProfile.RagFull }, CancellationToken.None)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result.Markdown)) return;

        var document = new IndexedDocument(
            Host: uri.Host,
            Url: url,
            Path: uri.AbsolutePath,
            Title: string.IsNullOrWhiteSpace(result.Title) ? uri.AbsolutePath : result.Title,
            Body: result.Markdown,
            ContentHash: Hash(result.Markdown),
            ETag: context.Response.Headers.ETag.ToString() is { Length: > 0 } etag ? etag : null,
            LastModified: context.Response.Headers.LastModified.ToString() is { Length: > 0 } lm ? lm : null,
            Source: "passive");

        _writer.TryEnqueue(document);
    }

    /// <summary>Content hash for change detection. Not security-sensitive, not PII.</summary>
    private static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Mostlylucid.BotDetection.WebMcp.Test/ --filter "FullyQualifiedName~WebMcpIndexActionPolicyTests"`
Expected: PASS (7 tests)

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection.WebMcp/Actions tests/Mostlylucid.BotDetection.WebMcp.Test
git commit -m "feat(webmcp): passive corpus capture action policy"
```

---

### Task 6: JSON-RPC envelope, MCP models, and the handler (`initialize` / `ping` / `tools/list`)

**Files:**
- Create: `src/Mostlylucid.BotDetection.WebMcp/Protocol/JsonRpc.cs`
- Create: `src/Mostlylucid.BotDetection.WebMcp/Protocol/McpModels.cs`
- Create: `src/Mostlylucid.BotDetection.WebMcp/Protocol/WebMcpJsonContext.cs`
- Create: `src/Mostlylucid.BotDetection.WebMcp/Protocol/McpJsonRpcHandler.cs`
- Test: `tests/Mostlylucid.BotDetection.WebMcp.Test/McpJsonRpcHandlerTests.cs`

**Interfaces:**
- Consumes: `WebMcpOptions` (Task 1).
- Produces: `McpJsonRpcHandler.HandleAsync(JsonElement request, McpCallContext ctx, CancellationToken) → JsonRpcResponse?` (null = notification, no response body). `record McpCallContext(string Host, int MaxResults)`. Task 7 adds `tools/call` to this handler.

**Protocol notes the implementer must not get wrong:**
- A request with no `id` is a **notification**: process it and return no response (HTTP 202, empty body).
- **Protocol** failures (unknown method, malformed params) return a JSON-RPC `error` object.
- **Tool execution** failures do NOT. Per the MCP spec they return a normal `result` with
  `isError: true`, so the model can see and react to the failure. Task 7 depends on this.

- [ ] **Step 1: Write the failing tests**

`tests/Mostlylucid.BotDetection.WebMcp.Test/McpJsonRpcHandlerTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.WebMcp.Options;
using Mostlylucid.BotDetection.WebMcp.Protocol;
using Xunit;

namespace Mostlylucid.BotDetection.WebMcp.Tests;

public sealed class McpJsonRpcHandlerTests
{
    private static McpJsonRpcHandler Create()
        => new(new NoopToolExecutor(),
               Microsoft.Extensions.Options.Options.Create(new WebMcpOptions { Enabled = true }),
               NullLogger<McpJsonRpcHandler>.Instance);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static readonly McpCallContext Ctx = new("example.test", MaxResults: 10);

    [Fact]
    public async Task Initialize_returns_protocol_version_and_server_info()
    {
        var response = await Create().HandleAsync(Parse("""
            {"jsonrpc":"2.0","id":1,"method":"initialize",
             "params":{"protocolVersion":"2025-06-18","capabilities":{},
                       "clientInfo":{"name":"test","version":"1"}}}
            """), Ctx, CancellationToken.None);

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        var result = (InitializeResult)response.Result!;
        result.ProtocolVersion.Should().Be("2025-06-18");
        result.ServerInfo.Name.Should().Be("example.test");
        result.Capabilities.Tools.Should().NotBeNull();
    }

    [Fact]
    public async Task Ping_returns_empty_result()
    {
        var response = await Create().HandleAsync(
            Parse("""{"jsonrpc":"2.0","id":2,"method":"ping"}"""), Ctx, CancellationToken.None);

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        response.Id.Should().NotBeNull();
    }

    [Fact]
    public async Task Notification_without_id_produces_no_response()
    {
        var response = await Create().HandleAsync(
            Parse("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""), Ctx, CancellationToken.None);

        response.Should().BeNull("notifications must not be answered");
    }

    [Fact]
    public async Task Tools_list_advertises_search_site_and_fetch_page()
    {
        var response = await Create().HandleAsync(
            Parse("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}"""), Ctx, CancellationToken.None);

        var result = (ToolsListResult)response!.Result!;
        result.Tools.Should().HaveCount(2);
        result.Tools.Select(t => t.Name).Should().BeEquivalentTo(["search_site", "fetch_page"]);
        result.Tools.Should().OnlyContain(t => !string.IsNullOrWhiteSpace(t.Description));
        result.Tools.Should().OnlyContain(t => t.InputSchema.ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task Unknown_method_returns_method_not_found()
    {
        var response = await Create().HandleAsync(
            Parse("""{"jsonrpc":"2.0","id":4,"method":"does/not/exist"}"""), Ctx, CancellationToken.None);

        response!.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(JsonRpcErrorCodes.MethodNotFound);
    }

    [Fact]
    public async Task Missing_method_field_returns_invalid_request()
    {
        var response = await Create().HandleAsync(
            Parse("""{"jsonrpc":"2.0","id":5}"""), Ctx, CancellationToken.None);

        response!.Error!.Code.Should().Be(JsonRpcErrorCodes.InvalidRequest);
    }
}

/// <summary>Tool executor stub — Task 7 supplies the real one.</summary>
internal sealed class NoopToolExecutor : IToolExecutor
{
    public Task<ToolCallResult> CallAsync(string name, JsonElement? arguments, McpCallContext ctx, CancellationToken ct)
        => Task.FromResult(ToolCallResult.Text("noop"));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Mostlylucid.BotDetection.WebMcp.Test/ --filter "FullyQualifiedName~McpJsonRpcHandlerTests"`
Expected: FAIL — compile error, protocol types do not exist.

- [ ] **Step 3: Write the JSON-RPC envelope**

`src/Mostlylucid.BotDetection.WebMcp/Protocol/JsonRpc.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mostlylucid.BotDetection.WebMcp.Protocol;

/// <summary>JSON-RPC 2.0 error codes used by this server.</summary>
public static class JsonRpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    /// <summary>Implementation-defined server error (the -32000..-32099 band).</summary>
    public const int ServerError = -32000;
}

public sealed record JsonRpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
///     A JSON-RPC response. Exactly one of <see cref="Result"/> / <see cref="Error"/> is set.
///     <see cref="Id"/> is carried through verbatim from the request.
/// </summary>
public sealed record JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc => "2.0";
    [JsonPropertyName("id")] public JsonElement? Id { get; init; }
    [JsonPropertyName("result")] public object? Result { get; init; }
    [JsonPropertyName("error")] public JsonRpcError? Error { get; init; }

    public static JsonRpcResponse Ok(JsonElement? id, object result) => new() { Id = id, Result = result };

    public static JsonRpcResponse Fail(JsonElement? id, int code, string message)
        => new() { Id = id, Error = new JsonRpcError(code, message) };
}
```

- [ ] **Step 4: Write the MCP payload models**

`src/Mostlylucid.BotDetection.WebMcp/Protocol/McpModels.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mostlylucid.BotDetection.WebMcp.Protocol;

/// <summary>Per-call context resolved from the HTTP request before dispatch.</summary>
public sealed record McpCallContext(string Host, int MaxResults);

public sealed record ServerInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version);

public sealed record ToolsCapability(
    [property: JsonPropertyName("listChanged")] bool ListChanged);

public sealed record ServerCapabilities(
    [property: JsonPropertyName("tools")] ToolsCapability Tools);

public sealed record InitializeResult(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("capabilities")] ServerCapabilities Capabilities,
    [property: JsonPropertyName("serverInfo")] ServerInfo ServerInfo);

public sealed record ToolDescriptor(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("inputSchema")] JsonElement InputSchema);

public sealed record ToolsListResult(
    [property: JsonPropertyName("tools")] IReadOnlyList<ToolDescriptor> Tools);

public sealed record ToolContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

/// <summary>
///     Result of <c>tools/call</c>. Note <see cref="IsError"/>: per the MCP spec a tool that
///     fails returns a normal result with this flag set, NOT a JSON-RPC error — the model
///     needs to see the failure text to react to it. Only protocol-level faults use
///     <see cref="JsonRpcError"/>.
/// </summary>
public sealed record ToolCallResult(
    [property: JsonPropertyName("content")] IReadOnlyList<ToolContent> Content,
    [property: JsonPropertyName("isError")] bool IsError)
{
    public static ToolCallResult Text(string text) => new([new ToolContent("text", text)], false);
    public static ToolCallResult Error(string message) => new([new ToolContent("text", message)], true);
}

/// <summary>Executes a named tool. Implemented in Task 7.</summary>
public interface IToolExecutor
{
    Task<ToolCallResult> CallAsync(string name, JsonElement? arguments, McpCallContext ctx, CancellationToken ct);
}

public sealed record EmptyResult;
```

- [ ] **Step 5: Write the source-generated JSON context**

`src/Mostlylucid.BotDetection.WebMcp/Protocol/WebMcpJsonContext.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mostlylucid.BotDetection.WebMcp.Protocol;

/// <summary>
///     Source-generated serialisation metadata. The pack sets
///     <c>IsAotCompatible</c> + <c>TreatWarningsAsErrors</c>, so any reflection-based
///     <c>JsonSerializer</c> overload raises IL2026/IL3050 and FAILS THE BUILD. Every
///     serialise call must go through this context.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonRpcResponse))]
[JsonSerializable(typeof(JsonRpcError))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(ToolsListResult))]
[JsonSerializable(typeof(ToolCallResult))]
[JsonSerializable(typeof(ToolDescriptor))]
[JsonSerializable(typeof(EmptyResult))]
[JsonSerializable(typeof(JsonElement))]
// JsonRpcResponse.Result is declared `object?`, so serialising it needs the RUNTIME type
// resolved through this context. Registering `object` enables that polymorphic lookup;
// every concrete result type above must stay registered or its payload serialises as `{}`.
[JsonSerializable(typeof(object))]
public sealed partial class WebMcpJsonContext : JsonSerializerContext;
```

If a later increment adds a new `tools/*` result type, it MUST get a `[JsonSerializable]`
line here. The Task 8 endpoint tests assert on serialised JSON shape and will catch an
omission.

- [ ] **Step 6: Write the handler**

`src/Mostlylucid.BotDetection.WebMcp/Protocol/McpJsonRpcHandler.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.WebMcp.Options;

namespace Mostlylucid.BotDetection.WebMcp.Protocol;

/// <summary>
///     Dispatches the MCP method surface. Pure over its inputs — all IO lives behind
///     <see cref="IToolExecutor"/>.
/// </summary>
public sealed class McpJsonRpcHandler
{
    /// <summary>MCP revision this server advertises.</summary>
    public const string ProtocolVersion = "2025-06-18";

    private readonly IToolExecutor _executor;
    private readonly WebMcpOptions _options;
    private readonly ILogger<McpJsonRpcHandler> _logger;

    public McpJsonRpcHandler(
        IToolExecutor executor, IOptions<WebMcpOptions> options, ILogger<McpJsonRpcHandler> logger)
    {
        _executor = executor;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    ///     Handles one JSON-RPC request. Returns null for notifications (no <c>id</c>),
    ///     which the caller must translate to HTTP 202 with an empty body.
    /// </summary>
    public async Task<JsonRpcResponse?> HandleAsync(
        JsonElement request, McpCallContext ctx, CancellationToken ct)
    {
        JsonElement? id = request.TryGetProperty("id", out var idElement) ? idElement.Clone() : null;

        if (!request.TryGetProperty("method", out var methodElement) ||
            methodElement.ValueKind != JsonValueKind.String)
            return id is null
                ? null
                : JsonRpcResponse.Fail(id, JsonRpcErrorCodes.InvalidRequest, "Missing or non-string 'method'.");

        var method = methodElement.GetString()!;
        var isNotification = id is null;

        // Increment 1 has no notification side effects (the only one a client sends is
        // notifications/initialized), so they are acknowledged and dropped. JSON-RPC
        // forbids answering a request with no id either way.
        if (isNotification) return null;

        try
        {
            return method switch
            {
                "initialize" => JsonRpcResponse.Ok(id, BuildInitialize(ctx)),
                "ping" => JsonRpcResponse.Ok(id, new EmptyResult()),
                "tools/list" => JsonRpcResponse.Ok(id, new ToolsListResult(ToolCatalog.Descriptors)),
                "tools/call" => await HandleToolCallAsync(id, request, ctx, ct).ConfigureAwait(false),
                _ => JsonRpcResponse.Fail(id, JsonRpcErrorCodes.MethodNotFound, $"Unknown method '{method}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebMCP handler faulted on method {Method}.", method);
            return JsonRpcResponse.Fail(id, JsonRpcErrorCodes.InternalError, "Internal error.");
        }
    }

    private InitializeResult BuildInitialize(McpCallContext ctx)
        => new(
            ProtocolVersion,
            new ServerCapabilities(new ToolsCapability(ListChanged: false)),
            new ServerInfo(
                string.IsNullOrWhiteSpace(_options.ServerName) ? ctx.Host : _options.ServerName,
                "1.0.0"));

    private async Task<JsonRpcResponse> HandleToolCallAsync(
        JsonElement? id, JsonElement request, McpCallContext ctx, CancellationToken ct)
    {
        if (!request.TryGetProperty("params", out var parameters) ||
            !parameters.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
            return JsonRpcResponse.Fail(id, JsonRpcErrorCodes.InvalidParams, "params.name is required.");

        JsonElement? arguments = parameters.TryGetProperty("arguments", out var args) ? args : null;

        // Tool failures are RESULTS with isError:true, not JSON-RPC errors — the model must
        // be able to read the failure and react. Only protocol faults use the error channel.
        var result = await _executor
            .CallAsync(nameElement.GetString()!, arguments, ctx, ct)
            .ConfigureAwait(false);

        return JsonRpcResponse.Ok(id, result);
    }
}
```

- [ ] **Step 7: Write the tool catalog descriptors**

Append to `src/Mostlylucid.BotDetection.WebMcp/Protocol/McpModels.cs`:

```csharp
/// <summary>
///     The static Increment-1 tool surface. Later increments replace this with a
///     promotion-gated catalog read from SQLite.
/// </summary>
public static class ToolCatalog
{
    public const string SearchSite = "search_site";
    public const string FetchPage = "fetch_page";

    private const string SearchSchema = """
        {"type":"object",
         "properties":{
           "query":{"type":"string","description":"Search terms."},
           "limit":{"type":"integer","description":"Maximum results.","minimum":1,"maximum":50}},
         "required":["query"]}
        """;

    private const string FetchSchema = """
        {"type":"object",
         "properties":{
           "url":{"type":"string","description":"Absolute URL of an indexed page."}},
         "required":["url"]}
        """;

    public static IReadOnlyList<ToolDescriptor> Descriptors { get; } =
    [
        new(SearchSite,
            "Full-text search over this site's content. Returns matching pages with URL, title and a relevance-ranked snippet.",
            JsonDocument.Parse(SearchSchema).RootElement.Clone()),
        new(FetchPage,
            "Fetch the full text of one indexed page as Markdown. The URL must have appeared in a search_site result.",
            JsonDocument.Parse(FetchSchema).RootElement.Clone())
    ];
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/Mostlylucid.BotDetection.WebMcp.Test/ --filter "FullyQualifiedName~McpJsonRpcHandlerTests"`
Expected: PASS (6 tests)

- [ ] **Step 9: Commit**

```bash
git add src/Mostlylucid.BotDetection.WebMcp/Protocol tests/Mostlylucid.BotDetection.WebMcp.Test/McpJsonRpcHandlerTests.cs
git commit -m "feat(webmcp): JSON-RPC envelope and MCP method dispatch"
```

---

### Task 7: `ToolExecutor` — `search_site` and `fetch_page`

**Files:**
- Create: `src/Mostlylucid.BotDetection.WebMcp/Tools/IUpstreamFetcher.cs`
- Create: `src/Mostlylucid.BotDetection.WebMcp/Tools/HttpUpstreamFetcher.cs`
- Create: `src/Mostlylucid.BotDetection.WebMcp/Tools/ToolExecutor.cs`
- Test: `tests/Mostlylucid.BotDetection.WebMcp.Test/ToolExecutorTests.cs`

**Interfaces:**
- Consumes: `ISiteIndex` (Task 3), `IToolExecutor` / `ToolCallResult` / `McpCallContext` / `ToolCatalog` (Task 6), `ILayoutExtractor`.
- Produces: `ToolExecutor : IToolExecutor`; `interface IUpstreamFetcher { Task<string?> GetHtmlAsync(string url, CancellationToken ct); }`.

**SSRF guard:** `fetch_page` only accepts a URL that already exists in the index
(`ISiteIndex.LookupAsync` returns non-null). The index is populated exclusively from
responses the gateway itself proxied, so the reachable set is by construction "pages this
site already served." An unknown URL is a tool error, never a fetch.

- [ ] **Step 1: Write the failing tests**

`tests/Mostlylucid.BotDetection.WebMcp.Test/ToolExecutorTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.WebMcp.Index;
using Mostlylucid.BotDetection.WebMcp.Protocol;
using Mostlylucid.BotDetection.WebMcp.Tools;
using Xunit;

namespace Mostlylucid.BotDetection.WebMcp.Tests;

internal sealed class StubFetcher : IUpstreamFetcher
{
    public string? Html { get; set; } = "<html><body>fetched</body></html>";
    public List<string> Requested { get; } = new();

    public Task<string?> GetHtmlAsync(string url, CancellationToken ct)
    {
        Requested.Add(url);
        return Task.FromResult(Html);
    }
}

public sealed class ToolExecutorTests
{
    private static readonly McpCallContext Ctx = new("example.test", MaxResults: 10);
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static (ToolExecutor Executor, Fts5SiteIndex Index, StubFetcher Fetcher) Create(TempDb db)
    {
        var index = new Fts5SiteIndex(new Mostlylucid.BotDetection.WebMcp.Options.IndexOptions
        {
            StorePath = db.Path
        });
        var fetcher = new StubFetcher();
        var executor = new ToolExecutor(index, fetcher, new FakeExtractor(), NullLogger<ToolExecutor>.Instance);
        return (executor, index, fetcher);
    }

    [Fact]
    public async Task Search_site_returns_matching_pages()
    {
        using var db = new TempDb();
        var (executor, index, _) = Create(db);
        await index.IndexAsync(Docs.Page(), CancellationToken.None);

        var result = await executor.CallAsync(
            ToolCatalog.SearchSite, Args("""{"query":"blackboard"}"""), Ctx, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().ContainSingle();
        result.Content[0].Text.Should().Contain("https://example.test/docs/intro");
        result.Content[0].Text.Should().Contain("Introduction");
    }

    [Fact]
    public async Task Search_site_with_no_matches_is_not_an_error()
    {
        using var db = new TempDb();
        var (executor, index, _) = Create(db);
        await index.IndexAsync(Docs.Page(), CancellationToken.None);

        var result = await executor.CallAsync(
            ToolCatalog.SearchSite, Args("""{"query":"nonexistentterm"}"""), Ctx, CancellationToken.None);

        result.IsError.Should().BeFalse("an empty result set is a valid answer, not a failure");
        result.Content[0].Text.Should().Contain("No matching");
    }

    [Fact]
    public async Task Search_site_without_query_argument_is_a_tool_error()
    {
        using var db = new TempDb();
        var (executor, _, _) = Create(db);

        var result = await executor.CallAsync(
            ToolCatalog.SearchSite, Args("""{}"""), Ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content[0].Text.Should().Contain("query");
    }

    [Fact]
    public async Task Search_limit_is_clamped_to_the_tier_maximum()
    {
        using var db = new TempDb();
        var (executor, index, _) = Create(db);
        for (var i = 0; i < 12; i++)
            await index.IndexAsync(
                Docs.Page(url: $"https://example.test/p{i}", body: "common term", hash: $"h{i}"),
                CancellationToken.None);

        var ctx = new McpCallContext("example.test", MaxResults: 3);
        var result = await executor.CallAsync(
            ToolCatalog.SearchSite, Args("""{"query":"common","limit":50}"""), ctx, CancellationToken.None);

        // One line per hit in the rendered text block.
        result.Content[0].Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(l => l.StartsWith("https://")).Should().Be(3);
    }

    [Fact]
    public async Task Fetch_page_returns_markdown_for_an_indexed_url()
    {
        using var db = new TempDb();
        var (executor, index, fetcher) = Create(db);
        await index.IndexAsync(Docs.Page(), CancellationToken.None);

        var result = await executor.CallAsync(
            ToolCatalog.FetchPage,
            Args("""{"url":"https://example.test/docs/intro"}"""), Ctx, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content[0].Text.Should().Be("Extracted body text.");
        fetcher.Requested.Should().ContainSingle().Which.Should().Be("https://example.test/docs/intro");
    }

    [Fact]
    public async Task Fetch_page_refuses_a_url_that_was_never_indexed()
    {
        using var db = new TempDb();
        var (executor, _, fetcher) = Create(db);

        var result = await executor.CallAsync(
            ToolCatalog.FetchPage,
            Args("""{"url":"https://evil.test/internal"}"""), Ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        fetcher.Requested.Should().BeEmpty("un-indexed URLs must never be fetched — this is the SSRF guard");
    }

    [Fact]
    public async Task Fetch_page_reports_an_upstream_miss_as_a_tool_error()
    {
        using var db = new TempDb();
        var (executor, index, fetcher) = Create(db);
        await index.IndexAsync(Docs.Page(), CancellationToken.None);
        fetcher.Html = null;

        var result = await executor.CallAsync(
            ToolCatalog.FetchPage,
            Args("""{"url":"https://example.test/docs/intro"}"""), Ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Unknown_tool_name_is_a_tool_error()
    {
        using var db = new TempDb();
        var (executor, _, _) = Create(db);

        var result = await executor.CallAsync("no_such_tool", Args("{}"), Ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content[0].Text.Should().Contain("no_such_tool");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Mostlylucid.BotDetection.WebMcp.Test/ --filter "FullyQualifiedName~ToolExecutorTests"`
Expected: FAIL — compile error, `ToolExecutor` does not exist.

- [ ] **Step 3: Write the upstream fetcher**

`src/Mostlylucid.BotDetection.WebMcp/Tools/IUpstreamFetcher.cs`:

```csharp
namespace Mostlylucid.BotDetection.WebMcp.Tools;

/// <summary>
///     Fetches live HTML for one URL. Exists so <c>fetch_page</c> serves current content
///     rather than a stored body — the index affects ranking, never what a caller receives.
/// </summary>
public interface IUpstreamFetcher
{
    /// <summary>Returns the HTML, or null when the fetch fails or returns a non-success status.</summary>
    Task<string?> GetHtmlAsync(string url, CancellationToken ct);
}
```

`src/Mostlylucid.BotDetection.WebMcp/Tools/HttpUpstreamFetcher.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.WebMcp.Tools;

/// <summary>
///     <see cref="HttpClient"/>-backed fetcher. Registered against the named client
///     <see cref="ClientName"/> so operators can configure timeout, proxy and handler
///     policy through the standard <c>IHttpClientFactory</c> surface.
/// </summary>
public sealed class HttpUpstreamFetcher : IUpstreamFetcher
{
    public const string ClientName = "webmcp-upstream";

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<HttpUpstreamFetcher> _logger;

    public HttpUpstreamFetcher(IHttpClientFactory factory, ILogger<HttpUpstreamFetcher> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<string?> GetHtmlAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = _factory.CreateClient(ClientName);
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "WebMCP upstream fetch for {Url} returned {Status}.", url, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebMCP upstream fetch for {Url} failed.", url);
            return null;
        }
    }
}
```

- [ ] **Step 4: Write the tool executor**

`src/Mostlylucid.BotDetection.WebMcp/Tools/ToolExecutor.cs`:

```csharp
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.WebMcp.Index;
using Mostlylucid.BotDetection.WebMcp.Protocol;
using StyloExtract.Abstractions;

namespace Mostlylucid.BotDetection.WebMcp.Tools;

/// <summary>
///     Executes the Increment-1 read-only tool surface.
///     <para>
///         Every failure below is returned as a <see cref="ToolCallResult"/> with
///         <c>isError: true</c>, never as a JSON-RPC error — the MCP spec routes tool
///         failures back to the model so it can react to them.
///     </para>
/// </summary>
public sealed class ToolExecutor : IToolExecutor
{
    private readonly ISiteIndex _index;
    private readonly IUpstreamFetcher _fetcher;
    private readonly ILayoutExtractor _extractor;
    private readonly ILogger<ToolExecutor> _logger;

    public ToolExecutor(
        ISiteIndex index, IUpstreamFetcher fetcher, ILayoutExtractor extractor, ILogger<ToolExecutor> logger)
    {
        _index = index;
        _fetcher = fetcher;
        _extractor = extractor;
        _logger = logger;
    }

    public async Task<ToolCallResult> CallAsync(
        string name, JsonElement? arguments, McpCallContext ctx, CancellationToken ct)
    {
        try
        {
            return name switch
            {
                ToolCatalog.SearchSite => await SearchAsync(arguments, ctx, ct).ConfigureAwait(false),
                ToolCatalog.FetchPage => await FetchAsync(arguments, ct).ConfigureAwait(false),
                _ => ToolCallResult.Error($"Unknown tool '{name}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebMCP tool {Tool} threw.", name);
            return ToolCallResult.Error($"Tool '{name}' failed.");
        }
    }

    private async Task<ToolCallResult> SearchAsync(JsonElement? arguments, McpCallContext ctx, CancellationToken ct)
    {
        if (!TryGetString(arguments, "query", out var query))
            return ToolCallResult.Error("Argument 'query' is required and must be a string.");

        var limit = TryGetInt(arguments, "limit", out var requested)
            ? Math.Clamp(requested, 1, ctx.MaxResults)
            : ctx.MaxResults;

        var hits = await _index.SearchAsync(query, limit, ct).ConfigureAwait(false);
        if (hits.Count == 0)
            return ToolCallResult.Text($"No matching pages for \"{query}\".");

        var builder = new StringBuilder();
        foreach (var hit in hits)
        {
            builder.Append(hit.Url).Append(" — ").AppendLine(hit.Title);
            if (!string.IsNullOrWhiteSpace(hit.Snippet)) builder.AppendLine(hit.Snippet);
            builder.AppendLine();
        }

        return ToolCallResult.Text(builder.ToString().TrimEnd());
    }

    private async Task<ToolCallResult> FetchAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!TryGetString(arguments, "url", out var url))
            return ToolCallResult.Error("Argument 'url' is required and must be a string.");

        // SSRF guard: only URLs already in the index are reachable, and the index is fed
        // exclusively by responses this gateway itself proxied.
        var known = await _index.LookupAsync(url, ct).ConfigureAwait(false);
        if (known is null)
            return ToolCallResult.Error(
                $"'{url}' is not an indexed page on this site. Use search_site to find valid URLs.");

        var html = await _fetcher.GetHtmlAsync(url, ct).ConfigureAwait(false);
        if (html is null) return ToolCallResult.Error($"Upstream did not return content for '{url}'.");

        var extracted = await _extractor
            .ExtractAsync(html, new Uri(url), new ExtractionOptions { Profile = ExtractionProfile.RagFull }, ct)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(extracted.Markdown)
            ? ToolCallResult.Error($"No extractable content at '{url}'.")
            : ToolCallResult.Text(extracted.Markdown);
    }

    private static bool TryGetString(JsonElement? arguments, string property, out string value)
    {
        value = string.Empty;
        if (arguments is not { } args || args.ValueKind != JsonValueKind.Object) return false;
        if (!args.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String) return false;

        var text = element.GetString();
        if (string.IsNullOrWhiteSpace(text)) return false;

        value = text;
        return true;
    }

    private static bool TryGetInt(JsonElement? arguments, string property, out int value)
    {
        value = 0;
        if (arguments is not { } args || args.ValueKind != JsonValueKind.Object) return false;
        return args.TryGetProperty(property, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Mostlylucid.BotDetection.WebMcp.Test/ --filter "FullyQualifiedName~ToolExecutorTests"`
Expected: PASS (8 tests)

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection.WebMcp/Tools tests/Mostlylucid.BotDetection.WebMcp.Test/ToolExecutorTests.cs
git commit -m "feat(webmcp): search_site and fetch_page tool executor"
```

---

### Task 8: Endpoint, DI wiring, gateway registration, and docs

**Files:**
- Create: `src/Mostlylucid.BotDetection.WebMcp/Endpoints/WebMcpEndpoints.cs`
- Create: `src/Mostlylucid.BotDetection.WebMcp/Extensions/ServiceCollectionExtensions.cs`
- Modify: `src/Mostlylucid.BotDetection.WebMcp/README.md`
- Modify: `src/Stylobot.Gateway/Stylobot.Gateway.csproj`
- Modify: `src/Stylobot.Gateway/Program.cs`
- Test: `tests/Mostlylucid.BotDetection.WebMcp.Test/WebMcpEndpointTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–7.
- Produces: `IServiceCollection.AddWebMcp(IConfiguration)`, `IEndpointRouteBuilder.MapWebMcp()`.

- [ ] **Step 1: Write the failing endpoint tests**

`tests/Mostlylucid.BotDetection.WebMcp.Test/WebMcpEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mostlylucid.BotDetection.WebMcp.Endpoints;
using Mostlylucid.BotDetection.WebMcp.Extensions;
using Mostlylucid.BotDetection.WebMcp.Index;
using Xunit;

namespace Mostlylucid.BotDetection.WebMcp.Tests;

public sealed class WebMcpEndpointTests : IAsyncLifetime
{
    private IHost _host = null!;
    private HttpClient _client = null!;
    private TempDb _db = null!;

    public async Task InitializeAsync()
    {
        _db = new TempDb();
        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BotDetection:WebMcp:Enabled"] = "true",
                    ["BotDetection:WebMcp:Index:StorePath"] = _db.Path,
                    ["BotDetection:WebMcp:ServerName"] = "example.test"
                }));
                web.ConfigureServices((ctx, services) =>
                {
                    services.AddRouting();
                    services.AddWebMcp(ctx.Configuration);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapWebMcp());
                });
            })
            .StartAsync();

        _client = _host.GetTestClient();

        var index = _host.Services.GetRequiredService<ISiteIndex>();
        await index.IndexAsync(Docs.Page(), CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        _db.Dispose();
    }

    private async Task<JsonElement> PostAsync(string json)
    {
        var response = await _client.PostAsync(
            "/_stylobot/mcp", new StringContent(json, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task Initialize_round_trips()
    {
        var body = await PostAsync("""
            {"jsonrpc":"2.0","id":1,"method":"initialize",
             "params":{"protocolVersion":"2025-06-18","capabilities":{},
                       "clientInfo":{"name":"t","version":"1"}}}
            """);

        body.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        body.GetProperty("result").GetProperty("protocolVersion").GetString().Should().Be("2025-06-18");
        body.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString()
            .Should().Be("example.test");
    }

    [Fact]
    public async Task Tools_list_round_trips()
    {
        var body = await PostAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        body.GetProperty("result").GetProperty("tools").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Tools_call_search_site_round_trips()
    {
        var body = await PostAsync("""
            {"jsonrpc":"2.0","id":3,"method":"tools/call",
             "params":{"name":"search_site","arguments":{"query":"blackboard"}}}
            """);

        var result = body.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        result.GetProperty("content")[0].GetProperty("text").GetString()
            .Should().Contain("https://example.test/docs/intro");
    }

    [Fact]
    public async Task Notification_returns_202_with_no_body()
    {
        var response = await _client.PostAsync(
            "/_stylobot/mcp",
            new StringContent("""{"jsonrpc":"2.0","method":"notifications/initialized"}""",
                Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Malformed_json_returns_parse_error()
    {
        var response = await _client.PostAsync(
            "/_stylobot/mcp", new StringContent("{not json", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32700);
    }

    [Fact]
    public async Task Get_returns_405_because_no_sse_stream_is_offered()
    {
        var response = await _client.GetAsync("/_stylobot/mcp");
        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Endpoint_is_not_exempted_from_detection()
    {
        // CLAUDE.md Critical Rule: "NEVER skip detection." This asserts the route carries no
        // opt-out metadata. If a future change adds a bypass attribute here, this fails.
        var endpoints = _host.Services
            .GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints;
        var mcp = endpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/_stylobot/mcp" &&
                         e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!
                             .HttpMethods.Contains("POST"));

        mcp.Metadata.Should().NotContain(m =>
            m.GetType().Name.Contains("SkipDetection") || m.GetType().Name.Contains("BypassBotDetection"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Mostlylucid.BotDetection.WebMcp.Test/ --filter "FullyQualifiedName~WebMcpEndpointTests"`
Expected: FAIL — compile error, `AddWebMcp` / `MapWebMcp` do not exist.

- [ ] **Step 3: Write the endpoint**

`src/Mostlylucid.BotDetection.WebMcp/Endpoints/WebMcpEndpoints.cs`:

```csharp
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.WebMcp.Options;
using Mostlylucid.BotDetection.WebMcp.Protocol;

namespace Mostlylucid.BotDetection.WebMcp.Endpoints;

/// <summary>
///     Maps the MCP Streamable-HTTP endpoint. POST carries JSON-RPC; GET returns 405
///     because v1 offers no server-initiated messages and therefore no SSE stream — the
///     transport spec permits a server to decline the stream.
///     <para>
///         The route is deliberately ORDINARY: no detection opt-out, no bypass metadata.
///         Requests here run the full pipeline like any other endpoint (CLAUDE.md Critical Rule).
///     </para>
/// </summary>
public static class WebMcpEndpoints
{
    public static IEndpointRouteBuilder MapWebMcp(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<WebMcpOptions>>().Value;
        if (!options.Enabled) return endpoints;

        endpoints.MapPost(options.Path, HandlePostAsync).WithName("WebMcpRpc");
        endpoints.MapGet(options.Path, static (HttpContext http) =>
        {
            http.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return Task.CompletedTask;
        }).WithName("WebMcpStreamUnsupported");

        return endpoints;
    }

    private static async Task HandlePostAsync(HttpContext http)
    {
        var handler = http.RequestServices.GetRequiredService<McpJsonRpcHandler>();
        var options = http.RequestServices.GetRequiredService<IOptions<WebMcpOptions>>().Value;
        var ct = http.RequestAborted;

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteAsync(http,
                JsonRpcResponse.Fail(null, JsonRpcErrorCodes.ParseError, "Invalid JSON."), ct)
                .ConfigureAwait(false);
            return;
        }

        using (document)
        {
            // Increment 1 resolves everyone to the anonymous budget. Increment 5 reads the
            // API-key / identity.verified_bot_signed tiers here.
            var context = new McpCallContext(http.Request.Host.Host, options.Tiers.Anonymous.MaxResults);

            var response = await handler.HandleAsync(document.RootElement, context, ct).ConfigureAwait(false);

            if (response is null)
            {
                // Notification: acknowledged, no body.
                http.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }

            await WriteAsync(http, response, ct).ConfigureAwait(false);
        }
    }

    private static async Task WriteAsync(HttpContext http, JsonRpcResponse response, CancellationToken ct)
    {
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/json";
        // Source-generated type info: the pack is AOT-clean and reflection overloads fail the build.
        await JsonSerializer.SerializeAsync(
            http.Response.Body, response, WebMcpJsonContext.Default.JsonRpcResponse, ct).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Write the DI registration**

`src/Mostlylucid.BotDetection.WebMcp/Extensions/ServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.StyloExtract.Internals;
using Mostlylucid.BotDetection.WebMcp.Actions;
using Mostlylucid.BotDetection.WebMcp.Corpus;
using Mostlylucid.BotDetection.WebMcp.Index;
using Mostlylucid.BotDetection.WebMcp.Options;
using Mostlylucid.BotDetection.WebMcp.Protocol;
using Mostlylucid.BotDetection.WebMcp.Tools;

namespace Mostlylucid.BotDetection.WebMcp.Extensions;

/// <summary>
///     DI registration for the WebMCP pack. Call after <c>AddStyloExtract()</c> and
///     <c>AddBotDetection()</c> / <c>AddStyloBot()</c>, then <c>MapWebMcp()</c> on the
///     endpoint builder.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWebMcp(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<WebMcpOptions>().BindConfiguration("BotDetection:WebMcp");

        services.TryAddSingleton<ISiteIndex>(sp =>
            new Fts5SiteIndex(sp.GetRequiredService<IOptions<WebMcpOptions>>().Value.Index));

        services.TryAddSingleton(sp => new SiteCorpusWriter(
            sp.GetRequiredService<ISiteIndex>(),
            sp.GetRequiredService<IOptions<WebMcpOptions>>().Value.Corpus,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SiteCorpusWriter>>()));

        services.TryAddSingleton<ResponseBodyCapture>();
        services.AddHttpClient(HttpUpstreamFetcher.ClientName);
        services.TryAddSingleton<IUpstreamFetcher, HttpUpstreamFetcher>();
        services.TryAddSingleton<IToolExecutor, ToolExecutor>();
        services.TryAddSingleton<McpJsonRpcHandler>();

        // Named action policy `webmcp-index`, resolvable from EndpointPolicy rules.
        services.AddSingleton<IActionPolicy, WebMcpIndexActionPolicy>();

        return services;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Mostlylucid.BotDetection.WebMcp.Test/ --filter "FullyQualifiedName~WebMcpEndpointTests"`
Expected: PASS (7 tests)

- [ ] **Step 6: Run the whole pack test suite and the solution build**

Run:
```bash
dotnet test tests/Mostlylucid.BotDetection.WebMcp.Test/
dotnet build mostlylucid.stylobot.sln
```
Expected: all WebMcp tests PASS; solution builds with no new warnings. `TreatWarningsAsErrors`
means any IL2026/IL3050 trim warning fails the pack build — if that happens, a
`JsonSerializer` call is using a reflection overload instead of `WebMcpJsonContext.Default`.

- [ ] **Step 7: Wire the gateway**

Add to `src/Stylobot.Gateway/Stylobot.Gateway.csproj`, in the same `ItemGroup` as the
StyloExtract pack reference:

```xml
<ProjectReference Include="..\Mostlylucid.BotDetection.WebMcp\Mostlylucid.BotDetection.WebMcp.csproj" />
```

In `src/Stylobot.Gateway/Program.cs`, immediately after the existing
`builder.Services.AddStyloExtractActionPolicies();` line (around line 249):

```csharp
// WebMCP pack: FTS5 site index + read-only MCP endpoint. Inert unless
// BotDetection:WebMcp:Enabled is true.
builder.Services.AddWebMcp(builder.Configuration);
```

Add `using Mostlylucid.BotDetection.WebMcp.Extensions;` and
`using Mostlylucid.BotDetection.WebMcp.Endpoints;` to the top of the file.

Then, wherever the gateway maps its endpoints (search for `MapReverseProxy` or
`UseEndpoints`), add before the reverse-proxy mapping so the MCP route wins over the
catch-all proxy route:

```csharp
app.MapWebMcp();
```

- [ ] **Step 8: Verify the gateway builds and the endpoint stays inert by default**

Run:
```bash
dotnet build src/Stylobot.Gateway/Stylobot.Gateway.csproj
```
Expected: builds clean.

Then confirm default-off behaviour — with no `BotDetection:WebMcp` config, `MapWebMcp()`
maps nothing:

```bash
dotnet run --project src/Stylobot.Gateway &
sleep 5
curl -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:5080/_stylobot/mcp \
  -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
kill %1
```
Expected: `404` — the pack is off, so the route does not exist. (A `200` means
`Enabled` defaulted true somewhere; fix before proceeding.)

- [ ] **Step 9: Write the pack README**

Replace `src/Mostlylucid.BotDetection.WebMcp/README.md`:

````markdown
# Mostlylucid.BotDetection.WebMcp

Synthesises an MCP server for a site StyloBot proxies — without modifying that site.

HTML flowing through the gateway is extracted to Markdown and indexed into SQLite FTS5.
An MCP endpoint exposes that corpus as two read-only tools.

## Setup

```csharp
builder.Services.AddStyloExtract();
builder.Services.AddBotDetection();          // or AddStyloBot()
builder.Services.AddWebMcp(builder.Configuration);

app.UseRouting();
app.MapWebMcp();                             // before MapReverseProxy()
```

Then reference the capture policy from an `EndpointPolicy` rule so proxied HTML is indexed:

```json
{
  "BotDetection": {
    "Policies": {
      "site-content": { "Endpoints": ["/**"], "ActionPolicyName": "webmcp-index" }
    }
  }
}
```

## Tools

| Tool | Behaviour |
|---|---|
| `search_site` | Full-text (BM25) search over indexed pages. Returns URL, title, snippet. |
| `fetch_page` | Full Markdown for one indexed page. Fetched live from upstream, never served from the index. Un-indexed URLs are refused. |

## Configuration

All keys sit under `BotDetection:WebMcp`. The pack is **off by default**.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Master switch. No endpoint, no capture, no database file. |
| `Path` | `/_stylobot/mcp` | Route for the JSON-RPC endpoint. |
| `ServerName` | *(host)* | Advertised MCP `serverInfo.name`. |
| `Index:StorePath` | `webmcp.db` | SQLite file. |
| `Index:MaxDocuments` | `50000` | Cap; oldest-indexed rows are pruned. |
| `Index:MaxExcerptBytes` | `8192` | Per-page indexed body cap. |
| `Index:MaxQueryTokens` | `16` | Query token cap. |
| `Corpus:PassiveCapture` | `true` | Index HTML seen as normal traffic. |
| `Corpus:QueueCapacity` | `1024` | Bounded queue; full = shed, never block. |
| `Corpus:DrainInterval` | `00:00:05` | Max wait before a partial batch flushes. |
| `Corpus:DrainBatchSize` | `32` | Max documents per batch. |

## Guarantees

- **Never alters traffic.** Capture is transparent; the original bytes are written back verbatim.
- **Never skips detection.** The MCP endpoint runs the full pipeline like any other route.
- **Read-only.** No tool mutates upstream state.
- **Zero-PII.** Only public page content and URLs are persisted.

## Protocol

JSON-RPC 2.0 over Streamable HTTP, MCP revision `2025-06-18`. `POST` carries requests;
`GET` returns 405 (no server-initiated messages, so no SSE stream). Notifications are
answered with 202 and an empty body.
````

- [ ] **Step 10: Full verification and commit**

Run:
```bash
dotnet build mostlylucid.stylobot.sln
dotnet test tests/Mostlylucid.BotDetection.WebMcp.Test/
```
Expected: build clean; 54 tests PASS (Task 1: 1, Task 2: 12, Task 3: 9, Task 4: 4,
Task 5: 7, Task 6: 6, Task 7: 8, Task 8: 7 — `[Theory]` cases count individually).

```bash
git add src/Mostlylucid.BotDetection.WebMcp src/Stylobot.Gateway tests/Mostlylucid.BotDetection.WebMcp.Test
git commit -m "feat(webmcp): MCP endpoint, DI wiring, gateway registration"
```

---

## Done when

- `dotnet build mostlylucid.stylobot.sln` is clean.
- All WebMcp tests pass.
- With `BotDetection:WebMcp:Enabled=false` (the default) the gateway behaves exactly as before and `/_stylobot/mcp` 404s.
- With it enabled and `webmcp-index` referenced from an endpoint policy, browsing the proxied site populates `webmcp.db`, and an MCP client connecting to `/_stylobot/mcp` can `tools/list` then `search_site` and get real hits.

## Deliberately deferred

Per spec §13, these belong to later increments and must **not** be built here: OpenAPI-derived
tools (2), route/form candidates and dashboard promotion (3), in-page WebMCP injection (4),
the `offer-mcp` policy and metering (5), sitemap warm crawl (6). Tier resolution beyond the
anonymous budget is Increment 5 — Task 8 Step 3 marks the exact seam.
