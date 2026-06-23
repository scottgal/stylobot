using System.Text;
using FluentAssertions;
using Xunit;

namespace Mostlylucid.BotDetection.StyloExtract.Tests;

/// <summary>
/// Verifies that JSON, image-type, and no-body status responses pass through unchanged
/// when the extract-markdown policy is installed.
/// </summary>
public sealed class PassthroughForNonHtmlTests
{
    [Fact]
    public async Task Json_response_body_is_unchanged()
    {
        const string json = """{"key":"value"}""";
        var fake = new FakeExtractor();
        var policy = PolicyFactory.Markdown(fake);

        var originalBody = new MemoryStream();
        var context = HttpContextBuilder.CreateJsonContext();
        context.Response.Body = originalBody;

        // Install interceptor.
        await policy.ExecuteAsync(context, Evidence.Bot());

        // Simulate downstream writing JSON.
        var bytes = Encoding.UTF8.GetBytes(json);
        await context.Response.Body.WriteAsync(bytes);
        await context.Response.Body.FlushAsync();

        originalBody.Seek(0, SeekOrigin.Begin);
        var body = Encoding.UTF8.GetString(originalBody.ToArray());

        body.Should().Be(json);
        fake.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData(204)]
    [InlineData(304)]
    [InlineData(301)]
    [InlineData(302)]
    public async Task Status_without_body_passes_through(int status)
    {
        var fake = new FakeExtractor();
        var policy = PolicyFactory.Markdown(fake);

        var originalBody = new MemoryStream();
        var context = HttpContextBuilder.CreateStatusContext(status);
        context.Response.Body = originalBody;

        await policy.ExecuteAsync(context, Evidence.Bot());

        // Simulate downstream (no body for these statuses).
        await context.Response.Body.FlushAsync();

        fake.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Binary_response_image_type_passes_through_unchanged()
    {
        var fakeBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }; // JPEG header
        var fake = new FakeExtractor();
        var policy = PolicyFactory.Markdown(fake);

        var originalBody = new MemoryStream();
        var context = HttpContextBuilder.CreateHtmlContext();
        context.Response.ContentType = "image/jpeg"; // override to non-HTML
        context.Response.Body = originalBody;

        await policy.ExecuteAsync(context, Evidence.Bot());
        await context.Response.Body.WriteAsync(fakeBytes);
        await context.Response.Body.FlushAsync();

        originalBody.Seek(0, SeekOrigin.Begin);
        var actualBytes = originalBody.ToArray();

        actualBytes.Should().Equal(fakeBytes);
        fake.CallCount.Should().Be(0);
    }
}
