using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.OpenApi;

namespace Mostlylucid.BotDetection.Test.Services.OpenApi;

public class OpenApiStartupSeederServiceTests
{
    [Fact]
    public async Task StartAsync_NoSources_LeavesCatalogEmpty()
    {
        var catalog = new OpenApiCatalog();
        var seeder = new OpenApiStartupSeederService(
            Options.Create(new OpenApiSeedOptions()),
            new StubLoader(),
            catalog,
            NullLogger<OpenApiStartupSeederService>.Instance);

        await seeder.StartAsync(CancellationToken.None);

        Assert.True(catalog.IsSeeded);
        Assert.Empty(catalog.Documents);
        Assert.Equal(0, catalog.OperationCount);
    }

    [Fact]
    public async Task StartAsync_SingleSource_PopulatesCatalog()
    {
        var doc = MakeDoc("/users", "get");
        var catalog = new OpenApiCatalog();
        var seeder = new OpenApiStartupSeederService(
            Options.Create(new OpenApiSeedOptions { SourceUrl = "https://example.test/openapi.json" }),
            new StubLoader(doc),
            catalog,
            NullLogger<OpenApiStartupSeederService>.Instance);

        await seeder.StartAsync(CancellationToken.None);

        Assert.True(catalog.IsSeeded);
        Assert.Single(catalog.Documents);
        Assert.Equal(1, catalog.OperationCount);
        Assert.NotNull(catalog.TryGetByRouteKey("GET:/users"));
    }

    [Fact]
    public async Task StartAsync_MultipleSources_MergedIntoCatalog()
    {
        var docA = MakeDoc("/a", "get");
        var docB = MakeDoc("/b", "post");
        var catalog = new OpenApiCatalog();
        var seeder = new OpenApiStartupSeederService(
            Options.Create(new OpenApiSeedOptions { Sources = new() { "a", "b" } }),
            new StubLoader(docA, docB),
            catalog,
            NullLogger<OpenApiStartupSeederService>.Instance);

        await seeder.StartAsync(CancellationToken.None);

        Assert.Equal(2, catalog.Documents.Count);
        Assert.Equal(2, catalog.OperationCount);
    }

    [Fact]
    public async Task StartAsync_LoaderThrows_WithContinueOnFailure_SeedsEmpty()
    {
        var catalog = new OpenApiCatalog();
        var seeder = new OpenApiStartupSeederService(
            Options.Create(new OpenApiSeedOptions
            {
                SourceUrl = "bad",
                ContinueOnFailure = true
            }),
            new ThrowingLoader(),
            catalog,
            NullLogger<OpenApiStartupSeederService>.Instance);

        await seeder.StartAsync(CancellationToken.None);

        Assert.True(catalog.IsSeeded);
        Assert.Empty(catalog.Documents);
    }

    private static LoadedOpenApiDocument MakeDoc(string path, string method)
        => new("src", "T", "1.0",
            new[]
            {
                new LoadedOpenApiOperation(path, method, null, null, null, Deprecated: false,
                    Tags: Array.Empty<string>(), ResponseStatusCodes: new[] { 200 },
                    SecuritySchemes: Array.Empty<string>())
            },
            DateTime.UtcNow, "{}");

    private sealed class StubLoader : IOpenApiDocumentLoader
    {
        private readonly Queue<LoadedOpenApiDocument> _docs;
        public StubLoader(params LoadedOpenApiDocument[] docs) => _docs = new Queue<LoadedOpenApiDocument>(docs);

        public Task<LoadedOpenApiDocument?> LoadAsync(string source, bool continueOnFailure = false, CancellationToken ct = default)
            => Task.FromResult<LoadedOpenApiDocument?>(_docs.Count > 0 ? _docs.Dequeue() : null);

        public void ClearCache() { }
        public bool Invalidate(string source) => false;
    }

    private sealed class ThrowingLoader : IOpenApiDocumentLoader
    {
        public Task<LoadedOpenApiDocument?> LoadAsync(string source, bool continueOnFailure = false, CancellationToken ct = default)
            => continueOnFailure
                ? Task.FromResult<LoadedOpenApiDocument?>(null)
                : throw new InvalidOperationException("simulated failure");

        public void ClearCache() { }
        public bool Invalidate(string source) => false;
    }
}
