using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Xunit;
using Xunit.Abstractions;

namespace Mostlylucid.BotDetection.Test.Orchestration.Atoms.AtomContract;

/// <summary>
///     One-shot DIAGNOSTIC (not a CI gate) that builds the atom↔manifest emit-drift
///     triage list for task #27. For every registered <see cref="IDetectorAtom"/> it
///     runs the atom in isolation against a broad generic stimulus and reports, per atom:
///     <list type="bullet">
///         <item><b>observed-not-declared</b> (under the atom's owned prefixes) — the
///         RELIABLE signal: a key the atom raises that its manifest doesn't declare.
///         Reliable because it doesn't depend on tripping every conditional emit.</item>
///         <item><b>declared-not-observed</b> — NOISY under a generic stimulus (a
///         conditional emit the stimulus didn't trip looks identical to genuine drift);
///         used only to seed the per-atom forward tests.</item>
///         <item>atoms with no manifest, and atoms whose <c>DetectAsync</c> threw.</item>
///     </list>
///     Run manually (remove the Skip), read the report, then encode the findings into the
///     reverse-generic <c>[Theory]</c> gate + per-atom forward tests. Kept in the tree as a
///     re-runnable baseline diff, per the coordination with the session-persistence agent.
/// </summary>
public sealed class AtomContractDiagnostic
{
    private readonly ITestOutputHelper _out;
    public AtomContractDiagnostic(ITestOutputHelper output) => _out = output;

    [Fact(Skip = "Diagnostic only — remove Skip to regenerate the atom-contract drift report.")]
    public async Task Report_emit_manifest_drift()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddBotDetectionInMemory();
        await using var provider = services.BuildServiceProvider();

        // Shared maximal stimulus — read by whichever atoms use IHttpContextAccessor.
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = MaximalRequest();

        var atoms = ResolveAtoms(provider, out var resolveError);

        var loader = new DetectorManifestLoader();
        loader.LoadEmbeddedManifests();
        // Join on manifest.Name (strip the legacy "Contributor" suffix) == atom.Name.
        // scope.atom is inconsistently populated (atom-name / concern-label / empty) and is
        // the manifest-hygiene owner's to normalise; manifest.Name matches 53/53.
        var byAtomKey = loader.GetAllDetectorManifests().Values
            .GroupBy(ManifestAtomKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var lines = new List<string> { $"# Atom contract drift report ({atoms.Count} atoms resolved)" };
        if (resolveError is not null) lines.Add($"RESOLUTION ERROR (partial list): {resolveError}");

        var noManifest = new List<string>();
        var reverseDrift = new List<string>();  // observed-not-declared (reliable)
        var threw = new List<string>();

        foreach (var atom in atoms.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!byAtomKey.TryGetValue(atom.Name, out var manifest))
            {
                noManifest.Add(atom.Name);
                continue;
            }

            HashSet<string> raised;
            try
            {
                var sink = new SignalSink(maxCapacity: 512, maxAge: TimeSpan.FromMinutes(5));
                await atom.DetectAsync(sink, sessionId: "diag");
                raised = sink.Sense().Select(e => e.Signal.Split(':', 2)[0]).ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                threw.Add($"{atom.Name}: {ex.GetType().Name} {ex.Message}");
                continue;
            }

            var declared = manifest.Emits?.OnComplete?
                .Where(s => !string.IsNullOrEmpty(s.Key)).Select(s => s.Key)
                .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            var ownedPrefixes = declared.Select(k => k.Split('.', 2)[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var observedNotDeclared = raised
                .Where(k => ownedPrefixes.Contains(k.Split('.', 2)[0]))
                .Except(declared)
                .OrderBy(k => k).ToList();
            var declaredNotObserved = declared.Except(raised).OrderBy(k => k).ToList();

            if (observedNotDeclared.Count > 0)
                reverseDrift.Add($"## {atom.Name} ({manifest.Name})\n  observed-not-declared: {string.Join(", ", observedNotDeclared)}");

            lines.Add($"## {atom.Name} ({manifest.Name}) — raised {raised.Count}, declared {declared.Count}");
            if (observedNotDeclared.Count > 0) lines.Add($"  observed-not-declared (REVERSE, reliable): {string.Join(", ", observedNotDeclared)}");
            if (declaredNotObserved.Count > 0) lines.Add($"  declared-not-observed (forward, noisy): {string.Join(", ", declaredNotObserved)}");
        }

        lines.Add("\n# Summary");
        lines.Add($"atoms resolved: {atoms.Count}");
        lines.Add($"ORPHAN atoms (no manifest, join by manifest.Name strip-Contributor): {(noManifest.Count == 0 ? "none" : string.Join(", ", noManifest))}");
        lines.Add($"atoms whose DetectAsync threw: {(threw.Count == 0 ? "none" : "\n  - " + string.Join("\n  - ", threw))}");
        lines.Add($"atoms with REVERSE drift (observed-not-declared): {reverseDrift.Count}");

        var reportPath = Path.Combine(Path.GetTempPath(), "atom-contract-drift.md");
        await File.WriteAllLinesAsync(reportPath, lines);
        foreach (var l in lines) _out.WriteLine(l);
        _out.WriteLine($"\n(report also written to {reportPath})");
    }

    // The reliable atom↔manifest join: manifest.Name is uniformly "<AtomName>Contributor"
    // (Step-7 legacy) or "<AtomName>" (newer atoms). Strip the suffix; match atom.Name exact.
    private static string ManifestAtomKey(DetectorManifest m) =>
        m.Name.EndsWith("Contributor", StringComparison.Ordinal)
            ? m.Name[..^"Contributor".Length]
            : m.Name;

    private static List<IDetectorAtom> ResolveAtoms(IServiceProvider provider, out string? error)
    {
        error = null;
        try
        {
            return provider.GetServices<IDetectorAtom>().ToList();
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return new List<IDetectorAtom>();
        }
    }

    private static HttpContext MaximalRequest()
    {
        var http = new DefaultHttpContext();
        var h = http.Request.Headers;
        h.UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        h["Accept"] = "text/html,application/xhtml+xml";
        h["Accept-Language"] = "en-US,en;q=0.9";
        h["Accept-Encoding"] = "gzip, deflate, br";
        h["Sec-Fetch-Site"] = "same-origin";
        h["Sec-Fetch-Mode"] = "navigate";
        h["Sec-Fetch-Dest"] = "document";
        h["X-Forwarded-For"] = "1.2.3.4";
        h["Referer"] = "https://stylo.bot/";
        h["Cookie"] = "sb=1";
        http.Request.Method = "GET";
        http.Request.Path = "/";
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("1.2.3.4");
        return http;
    }
}
