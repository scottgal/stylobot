using FluentAssertions;
using Mostlylucid.BotDetection.StyloExtract.Actions;
using Xunit;

namespace Mostlylucid.BotDetection.StyloExtract.Tests;

public sealed class ExtractPassthroughTests
{
    private readonly ExtractPassthroughActionPolicy _policy = new();
    private readonly FakeExtractor _extractor = new();

    [Fact]
    public async Task ExecuteAsync_returns_Allowed_immediately()
    {
        var context = HttpContextBuilder.CreateHtmlContext();
        var result = await _policy.ExecuteAsync(context, Evidence.Bot());

        result.Continue.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_does_not_call_extractor()
    {
        var context = HttpContextBuilder.CreateHtmlContext();
        _ = await _policy.ExecuteAsync(context, Evidence.Bot());

        _extractor.CallCount.Should().Be(0);
    }

    [Fact]
    public void Name_is_extract_passthrough()
    {
        _policy.Name.Should().Be("extract-passthrough");
    }

    [Fact]
    public void ActionType_is_Custom()
    {
        _policy.ActionType.Should().Be(Mostlylucid.BotDetection.Actions.ActionType.Custom);
    }

    [Fact]
    public void Intent_is_Pass()
    {
        _policy.Intent.Should().Be(Mostlylucid.BotDetection.Actions.PolicyIntent.Pass);
    }
}
