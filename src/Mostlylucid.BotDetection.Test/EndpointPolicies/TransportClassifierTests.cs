using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.EndpointPolicies;

namespace Mostlylucid.BotDetection.Test.EndpointPolicies;

public class TransportClassifierTests
{
    [Fact]
    public void WebSocketUpgrade_ClassifiedAsWebSocket()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Upgrade"] = "websocket";
        Assert.Equal("websocket", TransportClassifier.Classify(ctx.Request));
    }

    [Fact]
    public void GrpcContentType_ClassifiedAsGrpc()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.ContentType = "application/grpc";
        Assert.Equal("grpc", TransportClassifier.Classify(ctx.Request));
    }

    [Fact]
    public void GrpcWebContentType_ClassifiedAsGrpcWeb()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.ContentType = "application/grpc-web";
        Assert.Equal("grpc-web", TransportClassifier.Classify(ctx.Request));
    }

    [Fact]
    public void TextEventStream_ClassifiedAsSse()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Accept"] = "text/event-stream";
        Assert.Equal("sse", TransportClassifier.Classify(ctx.Request));
    }

    [Fact]
    public void ApiPathPrefix_ClassifiedAsApi()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/v1/users";
        Assert.Equal("api", TransportClassifier.Classify(ctx.Request));
    }

    [Fact]
    public void ApplicationJsonAccept_ClassifiedAsApi()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/something";
        ctx.Request.Headers["Accept"] = "application/json";
        Assert.Equal("api", TransportClassifier.Classify(ctx.Request));
    }

    [Theory]
    [InlineData("/style.css")]
    [InlineData("/script.js")]
    [InlineData("/logo.svg")]
    [InlineData("/font.woff2")]
    public void StaticExtensions_ClassifiedAsStatic(string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        Assert.Equal("static", TransportClassifier.Classify(ctx.Request));
    }

    [Fact]
    public void HtmlPage_DefaultsToDocument()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/index.html";
        ctx.Request.Headers["Accept"] = "text/html";
        // index.html ends in .html which isn't in static list -> document.
        Assert.Equal("document", TransportClassifier.Classify(ctx.Request));
    }

    [Fact]
    public void EmptyRequest_DefaultsToDocument()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/";
        Assert.Equal("document", TransportClassifier.Classify(ctx.Request));
    }

    [Fact]
    public void ProtocolVersion_FromRequestProtocol()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Protocol = "HTTP/1.1";
        Assert.Equal("1.1", TransportClassifier.ClassifyProtocolVersion(ctx.Request));
    }

    [Fact]
    public void ProtocolVersion_PrefersInjectedXClientHttpVersion()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Protocol = "HTTP/1.1";   // proxy-to-origin hop
        ctx.Request.Headers["X-Client-HTTP-Version"] = "h2";
        Assert.Equal("h2", TransportClassifier.ClassifyProtocolVersion(ctx.Request));
    }

    [Fact]
    public void ProtocolVersion_FallsBackToSbHttpVersionShim()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Protocol = "HTTP/1.1";
        ctx.Request.Headers["Sb-Http-Version"] = "3";
        Assert.Equal("3", TransportClassifier.ClassifyProtocolVersion(ctx.Request));
    }
}
