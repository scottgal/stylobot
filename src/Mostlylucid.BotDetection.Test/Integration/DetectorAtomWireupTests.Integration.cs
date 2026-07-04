using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;

namespace Mostlylucid.BotDetection.Test.Integration;

/// <summary>
///     Wire-up tests for the native <see cref="IDetectorAtom"/> registrations
///     added by <c>AddNativeDetectorAtoms</c>. These prove the DI graph
///     resolves cleanly for every atom the pack path expects to run.
/// </summary>
/// <remarks>
///     A "does the DI graph work?" smoke check: catches missing dependencies,
///     ambiguous constructors, and null service resolutions at boot. Cheaper
///     than a full <c>WebApplicationFactory</c> test and complements the
///     per-atom behavioural tests that will follow.
/// </remarks>
[Trait("Category", "Integration")]
public class DetectorAtomWireupTests
{
    private readonly IServiceProvider _sp;

    public DetectorAtomWireupTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:EnableUserAgentDetection"] = "true",
                ["BotDetection:EnableHeaderAnalysis"] = "true",
                ["BotDetection:EnableIpDetection"] = "true",
                ["BotDetection:EnableBehavioralAnalysis"] = "true",
                ["BotDetection:EnableLlmDetection"] = "false",
                ["BotDetection:EnableTestMode"] = "true",
                ["BotDetection:UseAtomOrchestrator"] = "true",
                // BrowserModeClassifierAtom.IsEnabled depends on Identity being on
                ["BotDetection:Identity:Enabled"] = "true",
                ["BotDetection:Identity:BrowserMode:Enabled"] = "true"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddMemoryCache();
        services.AddHttpClient();
        services.AddHttpContextAccessor();
        services.AddBotDetection();
        // AddBotDetection registers the legacy contributor path; the
        // atom-orchestrator path (native atoms via AddNativeDetectorAtoms)
        // requires an explicit opt-in until the module wire-up is universal.
        services.AddBotDetectionOrchestrator();
        _sp = services.BuildServiceProvider();
    }

    /// <summary>
    ///     Every <see cref="INativeAtomNameMarker"/> registered by
    ///     <c>AddDetectorAtom&lt;T&gt;()</c> must have a corresponding
    ///     enabled <see cref="IDetectorAtom"/> whose <c>Name</c> matches.
    ///     Catches "atom registered without marker" or vice versa drift.
    /// </summary>
    [Fact]
    public void NativeAtomMarkers_MatchResolvedAtoms()
    {
        var markerNames = _sp
            .GetServices<INativeAtomNameMarker>()
            .Select(m => m.AtomName)
            .ToHashSet(StringComparer.Ordinal);

        var enabledAtomNames = _sp
            .GetServices<IDetectorAtom>()
            .Where(a => a.IsEnabled)
            .Select(a => a.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(markerNames);

        var markersMissingAtom = markerNames.Where(n => !enabledAtomNames.Contains(n)).ToArray();
        Assert.True(markersMissingAtom.Length == 0,
            "Every native atom marker must resolve to an enabled atom: "
            + string.Join(", ", markersMissingAtom));
    }

    /// <summary>
    ///     Every registered atom must have a non-empty <see cref="IDetectorAtom.Name"/>
    ///     and a non-negative <see cref="IDetectorAtom.Priority"/>. Trivial but
    ///     catches base-class regressions where a subclass forgets to set them.
    /// </summary>
    [Fact]
    public void EveryRegisteredAtom_HasNameAndPriority()
    {
        var atoms = _sp.GetServices<IDetectorAtom>().ToList();
        Assert.NotEmpty(atoms);

        foreach (var atom in atoms)
        {
            Assert.False(string.IsNullOrWhiteSpace(atom.Name),
                $"Atom {atom.GetType().Name} must have a non-empty Name");
            Assert.True(atom.Priority >= 0,
                $"Atom {atom.Name} must have a non-negative Priority (was {atom.Priority})");
        }
    }

    /// <summary>
    ///     Every legacy <see cref="Mostlylucid.BotDetection.Orchestration.IContributingDetector"/>
    ///     whose Name is not already claimed by a native atom must resolve
    ///     as an <see cref="IDetectorAtom"/> under the pack path -- proving
    ///     the migration adapter closes the coverage gap while natives are
    ///     being landed one at a time. Claimed names come from the
    ///     <see cref="INativeAtomNameMarker"/> registrations that
    ///     <c>AddDetectorAtom&lt;T&gt;()</c> adds alongside each native atom.
    /// </summary>
    [Fact]
    public void EveryUnmigratedContributor_HasAdapterAtom()
    {
        var claimedByNative = _sp
            .GetServices<INativeAtomNameMarker>()
            .Select(m => m.AtomName)
            .ToHashSet(StringComparer.Ordinal);

        var contributors = _sp
            .GetServices<Mostlylucid.BotDetection.Orchestration.IContributingDetector>()
            .Where(c => !claimedByNative.Contains(c.Name))
            .Select(c => c.Name)
            .ToHashSet(StringComparer.Ordinal);

        var atomNames = _sp
            .GetServices<IDetectorAtom>()
            .Where(a => a.IsEnabled)
            .Select(a => a.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = contributors.Where(c => !atomNames.Contains(c)).ToArray();

        Assert.True(missing.Length == 0,
            $"{missing.Length} legacy contributors have no adapter coverage under the pack path: "
            + string.Join(", ", missing));
    }

    /// <summary>
    ///     No native atom Name should appear twice as an enabled atom -- the
    ///     adapter must skip contributors whose Name is in
    ///     <c>NativeAtomNames</c>, otherwise the pack path would double-count
    ///     their contributions.
    /// </summary>
    [Fact]
    public void NativeAtomNames_AreNotDuplicatedByAdapters()
    {
        var enabledAtoms = _sp
            .GetServices<IDetectorAtom>()
            .Where(a => a.IsEnabled)
            .Select(a => a.Name)
            .ToList();

        var duplicates = enabledAtoms
            .GroupBy(n => n, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToArray();

        Assert.True(duplicates.Length == 0,
            "Native atoms should not be double-registered by the adapter path: "
            + string.Join(", ", duplicates.Select(d => $"{d.Key} x{d.Count}")));
    }

    /// <summary>
    ///     Atom Priority values partition into the Wave layout the pack
    ///     orchestrator expects. Uses <see cref="INativeAtomNameMarker"/> to
    ///     scope this check to native atoms only (adapter-wrapped legacy
    ///     contributors keep their own Priority values, which we don't want
    ///     to gate on here).
    /// </summary>
    [Fact]
    public void NativeAtoms_ArePartitionedAcrossWaves()
    {
        var markerNames = _sp
            .GetServices<INativeAtomNameMarker>()
            .Select(m => m.AtomName)
            .ToHashSet(StringComparer.Ordinal);

        var nativeAtoms = _sp
            .GetServices<IDetectorAtom>()
            .Where(a => markerNames.Contains(a.Name))
            .ToList();

        Assert.Contains(nativeAtoms, a => a.Priority < 10);   // at least one Wave 0
        Assert.Contains(nativeAtoms, a => a.Priority >= 20);  // at least one downstream
    }
}