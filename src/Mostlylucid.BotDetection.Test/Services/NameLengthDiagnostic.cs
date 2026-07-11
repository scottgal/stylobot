using System;
using System.Collections.Generic;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Xunit;
using Xunit.Abstractions;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Shows the composed display name + its length for a range of UAs, so a fleet of
///     Chrome visitors stays SHORT but DISTINCT and no unidentified UA blurts its raw
///     string into the name column (operator 2026-07-10).
/// </summary>
public class NameLengthDiagnostic
{
    private readonly ITestOutputHelper _out;
    public NameLengthDiagnostic(ITestOutputHelper o) => _out = o;

    [Theory]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1")]
    [InlineData("Mozilla/5.0 (compatible; SemrushBot/7~bl; +http://www.semrush.com/bot.html)")]
    [InlineData("welcome to cupertino")]
    [InlineData("Some Weird Long Client String With No Slash That Would Have Blurted")]
    // Operator 2026-07-11: "Meta-ExternalAgent developers.facebook.com" (42 chars) was
    // blowing the list width. developers.facebook.com is a vendor-home subdomain, dropped
    // now so the name is just "Meta-ExternalAgent".
    [InlineData("meta-externalagent/1.1 (+https://developers.facebook.com/docs/sharing/webmasters/crawler/)")]
    public void Show(string ua)
    {
        var name = FingerprintNameComposer.Compose(
            new Dictionary<string, object> { [SignalKeys.UserAgent] = ua }, userAgent: ua);
        _out.WriteLine($"len={name?.Length,3}  '{name}'   <=  {ua[..Math.Min(46, ua.Length)]}");
        // The whole point: no name should be a long UA blurt.
        Assert.True((name?.Length ?? 0) <= 28, $"name too long for the list: '{name}' ({name?.Length} chars)");
    }
}
