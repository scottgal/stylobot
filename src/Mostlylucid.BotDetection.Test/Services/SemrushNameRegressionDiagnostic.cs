using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Definitions.BotPatterns;
using Mostlylucid.BotDetection.Definitions.WellKnownBots;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Test.Orchestration.Atoms.AtomContract;
using Mostlylucid.Ephemeral;
using Mostlylucid.BotDetection.Services;
using Xunit;
using Xunit.Abstractions;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Diagnostic: pins WHICH layer drops the SemrushBot catalog name so a known bot
///     renders "Unknown" on the dashboard. Reproduces the staging regression on
///     signature f69ff9c7e71f15db (Semrush shown as "unknown").
/// </summary>
public class SemrushNameRegressionDiagnostic
{
    private readonly ITestOutputHelper _out;
    public SemrushNameRegressionDiagnostic(ITestOutputHelper output) => _out = output;

    private const string SemrushUa =
        "Mozilla/5.0 (compatible; SemrushBot/7~bl; +http://www.semrush.com/bot.html)";

    [Fact]
    public void Layer1_BotPatternLoader_matches_SemrushBot_from_the_YAML_catalog()
    {
        var (_, botName) = BotPatternLoader.Default.MatchUserAgent(SemrushUa, WellKnownBotIndex.Default);
        _out.WriteLine($"BotPatternLoader.MatchUserAgent -> botName='{botName}'");
        Assert.Equal("SemrushBot", botName); // catalog match WORKS
    }

    [Fact]
    public void Layer2_Compose_with_raw_UA_only_names_SemrushBot()
    {
        // ResolveDisplayName's tier-3 when Identity off + ledger.BotName null:
        // Compose(signals) with only what survives into preSignals.
        var withRawUa = new Dictionary<string, object> { [SignalKeys.UserAgent] = SemrushUa };
        var name = FingerprintNameComposer.Compose(withRawUa);
        _out.WriteLine($"Compose({{UserAgent}}) -> '{name}'");
        Assert.StartsWith("SemrushBot", name); // composer WORKS given the raw UA (appends +URL discriminator)
    }

    [Fact]
    public void Layer3_Compose_with_cached_bot_name_signal_names_SemrushBot()
    {
        var withCached = new Dictionary<string, object>
        {
            [SignalKeys.UserAgentBotName] = "SemrushBot",
            [SignalKeys.UserAgent] = SemrushUa
        };
        var name = FingerprintNameComposer.Compose(withCached);
        _out.WriteLine($"Compose({{ua.bot_name,UserAgent}}) -> '{name}'");
        Assert.StartsWith("SemrushBot", name); // composer WORKS given ua.bot_name
    }

    [Fact]
    public void Layer4_Compose_with_NO_UA_signal_synthesises_a_terminal_never_Unknown()
    {
        // preSignals strips the raw UA (PII) AND ua.bot_name was never raised -> Compose has
        // nothing to match. Under "Unknown is not a valid state" (2026-07-30) the terminal is
        // the synthesised "Unclassified", never the word "Unknown".
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>());
        _out.WriteLine($"Compose({{}}) -> '{name}'");
        Assert.False(string.IsNullOrEmpty(name), "terminal must never be null/empty");
        Assert.DoesNotContain("Unknown", name!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Unclassified", name);
    }

    [Fact]
    public async Task Layer0_does_UserAgentAtom_actually_RAISE_ua_bot_name_for_Semrush()
    {
        // THE pivotal question: does the atom emit ua.bot_name=SemrushBot at all?
        // If YES  -> the bug is downstream (the signal doesn't reach preSignals /
        //            ResolveDisplayName skips the UserAgentBotName tier).
        // If NO   -> the bug is the atom (Semrush not recognised on the emit path).
        var http = new DefaultHttpContext();
        http.Request.Headers.UserAgent = SemrushUa;

        var atom = new UserAgentAtom(
            NullLogger<UserAgentAtom>.Instance,
            Options.Create(new BotDetectionOptions()),
            new StubDetectorConfigProvider(),
            new StaticHttpContextAccessor(http),
            WellKnownBotIndex.Default,
            BotPatternLoader.Default);

        var sink = new SignalSink(maxCapacity: 256, maxAge: TimeSpan.FromMinutes(5));
        await atom.DetectAsync(sink, sessionId: "test");

        var raised = sink.Sense().Select(e => e.Signal).ToList();
        foreach (var s in raised.Where(s => s.Contains("bot", StringComparison.OrdinalIgnoreCase)))
            _out.WriteLine($"raised: {s}");

        var botNameSignal = raised.FirstOrDefault(s => s.StartsWith(SignalKeys.UserAgentBotName + ":"));
        _out.WriteLine($"ua.bot_name signal = '{botNameSignal}'");
        Assert.NotNull(botNameSignal);
        Assert.Contains("SemrushBot", botNameSignal!);
    }
}
