using System.Reflection;
using FluentAssertions;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using VYaml.Serialization;
using Xunit;
using Xunit.Abstractions;

namespace Mostlylucid.BotDetection.Test.Orchestration.Atoms.AtomContract;

/// <summary>
///     CI gate on detector-manifest validity. <see cref="DetectorManifestLoader"/> parses
///     every embedded <c>.detector.yaml</c> and <b>silently swallows</b> any that fail
///     (a broad catch), so a malformed manifest just vanishes — its detector runs with no
///     config + no signal provenance and nothing surfaces the error. This gate asserts
///     every manifest deserializes, so a new malformed manifest fails the build instead of
///     disappearing.
///
///     <para>
///         Ten manifests currently fail VYaml parse (found 2026-07-06): list-of-object
///         fields (<c>triggers.requires</c> as bare strings, <c>emits</c> as a bare-string
///         list instead of the EmissionConfig object, bare-string <c>output.signals</c>,
///         <c>- signal_exists:</c> instead of <c>- signal:</c>). The fix is manifest-side
///         and owned by the manifest-hygiene owner; flagged via the coordination channel.
///         They're tracked in <see cref="KnownUnparseable"/> so this gate stays green while
///         still catching NEW breakage. Remove an entry as its manifest is fixed — a stale
///         entry is harmless (the subset assertion still holds).
///     </para>
/// </summary>
public sealed class ManifestParseContractTests
{
    private readonly ITestOutputHelper _out;
    public ManifestParseContractTests(ITestOutputHelper output) => _out = output;

    private static readonly HashSet<string> KnownUnparseable = new(StringComparer.Ordinal)
    {
        // Empty: the 10 previously-broken manifests were fixed in 9a7c6f3f (61/61 parse now).
        // The gate fails on ANY unparseable manifest — add an entry only to temporarily
        // tolerate a known-broken manifest while its fix is pending.
    };

    [Fact]
    public void Every_detector_manifest_parses_except_the_tracked_broken_set()
    {
        var asm = typeof(DetectorManifest).Assembly;
        var resources = asm.GetManifestResourceNames()
            .Where(n => n.EndsWith(".detector.yaml", StringComparison.OrdinalIgnoreCase))
            .ToList();
        resources.Count.Should().BeGreaterThan(50, "the embedded detector manifests should be present");

        var failing = new List<string>();
        foreach (var n in resources)
        {
            using var stream = asm.GetManifestResourceStream(n)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            try
            {
                _ = YamlSerializer.Deserialize<DetectorManifest>(ms.ToArray());
            }
            catch
            {
                failing.Add(ManifestName(n));
            }
        }

        foreach (var f in failing) _out.WriteLine($"unparseable: {f}");

        // A manifest that fails parse and ISN'T in the tracked-broken set is a new regression
        // (the loader would silently drop it). Fix the manifest, don't extend the set casually.
        var unexpected = failing.Except(KnownUnparseable).OrderBy(x => x, StringComparer.Ordinal).ToList();
        unexpected.Should().BeEmpty(
            "these detector manifests fail VYaml parse and the loader silently drops them — " +
            "their detectors run unconfigured. Malformed: {0}", string.Join(", ", unexpected));
    }

    // Resource name "...Manifests.detectors.<name>.detector.yaml" → "<name>".
    private static string ManifestName(string resource)
        => resource.Split('.').Reverse().Skip(2).FirstOrDefault() ?? resource;
}
