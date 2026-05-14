using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.OpenApi;

namespace Mostlylucid.BotDetection.Test.Services.OpenApi;

public class OpenApiDocumentLoaderTests : IDisposable
{
    private const string MinimalSpec = """
        {
          "openapi": "3.0.1",
          "info": { "title": "Test API", "version": "1.0.0" },
          "paths": {
            "/users": {
              "get": {
                "operationId": "ListUsers",
                "summary": "List users",
                "tags": ["users"],
                "responses": { "200": { "description": "ok" }, "401": { "description": "auth" } }
              }
            },
            "/users/{id}": {
              "delete": {
                "operationId": "DeleteUser",
                "responses": { "204": { "description": "ok" } }
              }
            }
          }
        }
        """;

    private readonly string _tempFile;

    public OpenApiDocumentLoaderTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"sb-openapi-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(_tempFile, MinimalSpec, Encoding.UTF8);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    [Fact]
    public async Task LoadAsync_LocalFile_ParsesAllOperations()
    {
        var loader = new OpenApiDocumentLoader(new StubHttpClientFactory(), NullLogger<OpenApiDocumentLoader>.Instance);
        var doc = await loader.LoadAsync(_tempFile);

        Assert.NotNull(doc);
        Assert.Equal("Test API", doc!.Title);
        Assert.Equal("1.0.0", doc.Version);
        Assert.Equal(2, doc.Operations.Count);

        var list = Assert.Single(doc.Operations, o => o.Method == "get" && o.Path == "/users");
        Assert.Equal("ListUsers", list.OperationId);
        Assert.Equal("List users", list.Summary);
        Assert.Contains("users", list.Tags);
        Assert.Contains(200, list.ResponseStatusCodes);
        Assert.Contains(401, list.ResponseStatusCodes);
    }

    [Fact]
    public async Task LoadAsync_CachesResult()
    {
        var loader = new OpenApiDocumentLoader(new StubHttpClientFactory(), NullLogger<OpenApiDocumentLoader>.Instance);
        var first = await loader.LoadAsync(_tempFile);
        var second = await loader.LoadAsync(_tempFile);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ThrowsByDefault()
    {
        var loader = new OpenApiDocumentLoader(new StubHttpClientFactory(), NullLogger<OpenApiDocumentLoader>.Instance);
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => loader.LoadAsync("/does/not/exist.json"));
    }

    [Fact]
    public async Task LoadAsync_MissingFile_WithContinueOnFailure_ReturnsNull()
    {
        var loader = new OpenApiDocumentLoader(new StubHttpClientFactory(), NullLogger<OpenApiDocumentLoader>.Instance);
        var result = await loader.LoadAsync("/does/not/exist.json", continueOnFailure: true);
        Assert.Null(result);
    }

    [Fact]
    public async Task Invalidate_ForcesReload()
    {
        var loader = new OpenApiDocumentLoader(new StubHttpClientFactory(), NullLogger<OpenApiDocumentLoader>.Instance);
        var first = await loader.LoadAsync(_tempFile);
        Assert.True(loader.Invalidate(_tempFile));
        var second = await loader.LoadAsync(_tempFile);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task LoadAsync_HttpSource_UsesHttpClient()
    {
        var stub = new StubHttpClientFactory(MinimalSpec);
        var loader = new OpenApiDocumentLoader(stub, NullLogger<OpenApiDocumentLoader>.Instance);

        var doc = await loader.LoadAsync("https://example.test/openapi/v1.json");

        Assert.NotNull(doc);
        Assert.Equal(2, doc!.Operations.Count);
        Assert.Equal(1, stub.RequestCount);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly string? _body;
        public int RequestCount { get; private set; }

        public StubHttpClientFactory(string? body = null) => _body = body;

        public HttpClient CreateClient(string name)
            => new(new StubHandler(this));

        private sealed class StubHandler(StubHttpClientFactory parent) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                parent.RequestCount++;
                if (parent._body == null)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(parent._body, Encoding.UTF8, "application/json")
                });
            }
        }
    }
}
