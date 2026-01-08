using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Mostlylucid.YarpGateway.Tests.Fixtures;
using Xunit;

namespace Mostlylucid.YarpGateway.Tests.Integration;

/// <summary>
/// Tests for YARP reverse proxy routing functionality.
/// </summary>
public class ProxyRoutingTests : IClassFixture<GatewayTestFixture>
{
    private readonly GatewayTestFixture _fixture;

    public ProxyRoutingTests(GatewayTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.ClearRecordedRequests();
    }

    [Fact]
    public async Task CatchAllRoute_ForwardsToUpstream()
    {
        // Act
        var response = await _fixture.GatewayClient.GetAsync("/some/path/here");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("upstream received");

        // Verify upstream received the request
        _fixture.RecordedRequests.Should().ContainSingle();
        _fixture.RecordedRequests[0].Path.Should().Be("/some/path/here");
        _fixture.RecordedRequests[0].Method.Should().Be("GET");
    }

    [Fact]
    public async Task ApiPath_ForwardsWithCorrectPath()
    {
        // Act
        var response = await _fixture.GatewayClient.GetAsync("/api/echo");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var echoResponse = await response.Content.ReadFromJsonAsync<EchoResponse>();
        echoResponse.Should().NotBeNull();
        echoResponse!.Path.Should().Be("/api/echo");
        echoResponse.Method.Should().Be("GET");
    }

    [Fact]
    public async Task Post_ForwardsMethodCorrectly()
    {
        // Arrange
        var content = new StringContent("{\"test\":\"data\"}", System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.GatewayClient.PostAsync("/api/echo", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var echoResponse = await response.Content.ReadFromJsonAsync<EchoResponse>();
        echoResponse!.Method.Should().Be("POST");
    }

    [Fact]
    public async Task QueryString_IsForwarded()
    {
        // Act
        var response = await _fixture.GatewayClient.GetAsync("/api/echo?param1=value1&param2=value2");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var echoResponse = await response.Content.ReadFromJsonAsync<EchoResponse>();
        echoResponse!.Query.Should().Be("?param1=value1&param2=value2");
    }

    [Fact]
    public async Task CustomHeaders_AreForwarded()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/echo");
        request.Headers.Add("X-Custom-Header", "custom-value");
        request.Headers.Add("X-Another-Header", "another-value");

        // Act
        var response = await _fixture.GatewayClient.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify upstream received the headers
        _fixture.RecordedRequests.Should().ContainSingle();
        _fixture.RecordedRequests[0].Headers.Should().ContainKey("X-Custom-Header");
        _fixture.RecordedRequests[0].Headers["X-Custom-Header"].Should().Be("custom-value");
    }

    [Fact]
    public async Task UpstreamError_IsProxied()
    {
        // Act
        var response = await _fixture.GatewayClient.GetAsync("/api/error");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("internal error");
    }

    [Fact]
    public async Task SlowUpstream_CompletesSuccessfully()
    {
        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await _fixture.GatewayClient.GetAsync("/api/slow");
        stopwatch.Stop();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("slow response");

        // Should take at least 500ms (the upstream delay)
        stopwatch.ElapsedMilliseconds.Should().BeGreaterOrEqualTo(400); // Allow some tolerance
    }

    [Fact]
    public async Task MultipleRequests_AllForwarded()
    {
        // Act - send requests sequentially to avoid race conditions in CI
        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 10; i++)
        {
            var response = await _fixture.GatewayClient.GetAsync($"/api/echo?req={i}");
            responses.Add(response);
        }

        // Assert - all requests should succeed
        responses.Should().AllSatisfy(r => r.StatusCode.Should().Be(HttpStatusCode.OK));
        // All 10 sequential requests should be recorded
        _fixture.RecordedRequests.Should().HaveCount(10);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task HttpMethods_AreForwardedCorrectly(string method)
    {
        // Arrange
        var request = new HttpRequestMessage(new HttpMethod(method), "/api/echo");

        // Act
        var response = await _fixture.GatewayClient.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var echoResponse = await response.Content.ReadFromJsonAsync<EchoResponse>();
        echoResponse!.Method.Should().Be(method);
    }

    [Fact]
    public async Task LargePath_IsHandled()
    {
        // Arrange
        var longPath = "/api/echo/" + string.Join("/", Enumerable.Range(0, 50).Select(i => $"segment{i}"));

        // Act
        var response = await _fixture.GatewayClient.GetAsync(longPath);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        _fixture.RecordedRequests.Should().ContainSingle();
        _fixture.RecordedRequests[0].Path.Should().StartWith("/api/echo/");
    }

    private record EchoResponse(string Method, string Path, string Query);
}
