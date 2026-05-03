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
        var request = CreateRequest(tls: new TlsInfo { Version = "TLSv1.3", Cipher = "TLS_AES_256_GCM_SHA384", Ja3 = "abc123" });
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
        string method = "GET", string path = "/", Dictionary<string, string>? headers = null,
        string remoteIp = "127.0.0.1", string protocol = "https", TlsInfo? tls = null) =>
        new()
        {
            Method = method, Path = path,
            Headers = headers ?? new Dictionary<string, string> { ["user-agent"] = "Mozilla/5.0 (Test)" },
            RemoteIp = remoteIp, Protocol = protocol, Tls = tls
        };
}
