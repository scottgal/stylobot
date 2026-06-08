using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Policies.Signals;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies.Signals;

/// <summary>
///     End-to-end HTTP tests for the <c>/dashboard/signals/*</c> autocomplete API
///     surface added by Task A3b of the Policy Stack Control plan.
///     <para>
///     Drives the real <see cref="StyloBotDashboardMiddleware"/> through a
///     <see cref="DefaultHttpContext"/> so the response status / body / headers
///     reflect what an HTTP client would see. The dashboard middleware has a
///     long ctor list -- only the fields the <c>signals/*</c> routes touch are
///     populated; the rest are nulled out because the routes never reference
///     them and adding live wiring would make these tests load SignalR, Razor,
///     SQLite, OpenAPI, and the broadcaster background service for no payoff.
///     </para>
/// </summary>
public class SignalCatalogApiTests
{
    private const string BasePath = "/dashboard";

    private static readonly JsonSerializerOptions ReadJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ----- Harness -----

    private static async Task<(int status, string body, HeaderDictionary headers)> CallAsync(
        string relativeUrl)
    {
        // Build a service collection with just the dependencies the signal routes
        // resolve at runtime: the catalogue itself. Add IMemoryCache because the
        // middleware constructor pulls one and we want to mirror the real DI shape.
        var services = new ServiceCollection();
        var catalog = await SignalCatalog.LoadAsync(typeof(SignalKeys).Assembly);
        services.AddSingleton<ISignalCatalog>(catalog);
        services.AddMemoryCache();
        var sp = services.BuildServiceProvider();

        var options = new StyloBotDashboardOptions
        {
            BasePath = BasePath,
            // Auth-off: middleware allows anonymous access when
            // RequireAuthentication == false and env == Development.
            RequireAuthentication = false
        };

        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new StyloBotDashboardMiddleware(
            next: next,
            options: options,
            eventStore: null!,
            aggregateCache: new DashboardAggregateCache(),
            signatureCache: new SignatureAggregateCache(options),
            razorViewRenderer: null!,
            widgetCache: sp.GetRequiredService<IMemoryCache>(),
            env: new TestEnv(),
            logger: NullLogger<StyloBotDashboardMiddleware>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.RequestServices = sp;
        ctx.Request.Method = "GET";

        // Split relative URL into path + query so DefaultHttpContext parses the
        // query string into IQueryCollection. Bare assignment of a path with '?'
        // would otherwise put the literal '?' into PathBase.
        var qIdx = relativeUrl.IndexOf('?');
        if (qIdx >= 0)
        {
            ctx.Request.Path = BasePath + relativeUrl[..qIdx];
            ctx.Request.QueryString = new QueryString(relativeUrl[qIdx..]);
        }
        else
        {
            ctx.Request.Path = BasePath + relativeUrl;
        }

        var responseStream = new MemoryStream();
        ctx.Response.Body = responseStream;

        await middleware.InvokeAsync(ctx);

        // If the route fell through to _next (i.e. middleware did not recognise
        // it), surface that as a 404 so tests can detect routing regressions.
        if (nextCalled && ctx.Response.StatusCode == 200)
            ctx.Response.StatusCode = 404;

        responseStream.Position = 0;
        var body = Encoding.UTF8.GetString(responseStream.ToArray());

        return (ctx.Response.StatusCode, body, (HeaderDictionary)ctx.Response.Headers);
    }

    private static async Task<T> GetJson<T>(string relativeUrl)
    {
        var (status, body, _) = await CallAsync(relativeUrl);
        Assert.True(status >= 200 && status < 300, $"Expected 2xx, got {status}. Body: {body}");
        var parsed = JsonSerializer.Deserialize<T>(body, ReadJson);
        Assert.NotNull(parsed);
        return parsed!;
    }

    private static async Task<int> GetStatus(string relativeUrl)
    {
        var (status, _, _) = await CallAsync(relativeUrl);
        return status;
    }

    private static async Task<(int status, string body)> GetStatusWithBody(string relativeUrl)
    {
        var (status, body, _) = await CallAsync(relativeUrl);
        return (status, body);
    }

    // ----- Tests -----

    [Fact]
    public async Task GET_signals_namespaces_returns_namespace_heads_with_counts()
    {
        var resp = await GetJson<SignalNamespacesResponseDto>("/signals/namespaces");
        Assert.Contains(resp.Items, n => n.Name == "risk");
        Assert.Contains(resp.Items, n => n.Name == "ip");
        Assert.All(resp.Items, n => Assert.True(n.Count > 0));
        // alphabetical
        var sorted = resp.Items.OrderBy(i => i.Name, StringComparer.Ordinal).Select(i => i.Name).ToList();
        Assert.Equal(sorted, resp.Items.Select(i => i.Name).ToList());
    }

    [Fact]
    public async Task GET_signals_namespaces_sets_cache_control()
    {
        var (status, _, headers) = await CallAsync("/signals/namespaces");
        Assert.Equal(200, status);
        var cc = headers["Cache-Control"].ToString();
        Assert.Contains("max-age=300", cc);
        Assert.Contains("public", cc);
    }

    [Fact]
    public async Task GET_signals_search_q_returns_ranked_matches()
    {
        var resp = await GetJson<SignalSearchResponseDto>("/signals/search?q=risk.");
        Assert.NotEmpty(resp.Items);
        Assert.All(resp.Items, i => Assert.StartsWith("risk.", i.Key));
        Assert.NotNull(resp.Items[0].Short);
        Assert.NotEmpty(resp.Items[0].Operators);
    }

    [Fact]
    public async Task GET_signals_search_without_q_is_400()
    {
        var status = await GetStatus("/signals/search");
        Assert.Equal(400, status);
    }

    [Fact]
    public async Task GET_signals_search_empty_q_is_400()
    {
        var status = await GetStatus("/signals/search?q=");
        Assert.Equal(400, status);
    }

    [Fact]
    public async Task GET_signal_returns_full_descriptor_with_long_doc()
    {
        var resp = await GetJson<SignalFullDto>("/signals/risk.justification");
        Assert.Equal("risk.justification", resp.Key);
        Assert.Contains("human-readable explanation", resp.Long, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(resp.Operators);
        Assert.False(string.IsNullOrEmpty(resp.DeclaringType));
        Assert.False(string.IsNullOrEmpty(resp.Kind));
    }

    [Fact]
    public async Task GET_signal_unknown_returns_404_with_suggestions()
    {
        var (status, body) = await GetStatusWithBody("/signals/risk.justificatio");
        Assert.Equal(404, status);
        // Body must surface the Levenshtein-nearest known key. The error envelope
        // is { error, suggestions[] } -- assert against the JSON text so the test
        // is agnostic to property casing.
        Assert.Contains("risk.justification", body);
    }

    [Fact]
    public async Task GET_signal_search_max_n_is_clamped()
    {
        // n=999 should be silently clamped to 100. risk. prefix has fewer than 100
        // results but the clamp should still apply -- this asserts the count never
        // exceeds the upper bound, not that the catalogue is exactly 100.
        var resp = await GetJson<SignalSearchResponseDto>("/signals/search?q=risk.&n=999");
        Assert.True(resp.Items.Count <= 100);
    }

    [Fact]
    public async Task GET_signal_search_default_n_is_20()
    {
        // Fuzzy search returns up to N matches across the whole vocabulary,
        // so a single-char query is the most reliable way to hit the cap.
        var resp = await GetJson<SignalSearchResponseDto>("/signals/search?q=r");
        Assert.True(resp.Items.Count <= 20, $"expected default cap of 20, got {resp.Items.Count}");
    }

    [Fact]
    public async Task GET_signal_kind_is_serialized_as_string()
    {
        // SignalKind is an enum; the DTO serialises it as the enum name. Keep this
        // explicit -- editor clients expect "Number" / "String" / "Bool", not 0/1.
        var resp = await GetJson<SignalFullDto>("/signals/risk.justification");
        Assert.Equal("String", resp.Kind);
    }

    // ----- Stubs -----

    private sealed class TestEnv : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Test";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
