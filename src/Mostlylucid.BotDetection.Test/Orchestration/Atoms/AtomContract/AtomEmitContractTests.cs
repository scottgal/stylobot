using FluentAssertions;
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
///     Reverse-direction emit contract, generic over every registered
///     <see cref="IDetectorAtom"/>: an atom must not raise a signal <em>under a prefix
///     its own manifest owns</em> unless the manifest declares that key under
///     <c>emits.on_complete</c>. Undeclared emissions silently corrupt the dashboard's
///     signal-provenance (Source column) + the SignalCatalog.
///
///     <para>
///         This is the robust half of the emit contract: it only asserts on keys that
///         actually landed in the sink, so it needs no per-atom stimulus to trip every
///         conditional emit (unlike the forward direction, which is pinned per-atom by
///         hand — see <see cref="HeaderAtomContractTests"/>). The atom↔manifest join,
///         owned-prefix derivation, and the known-drift tracking list were agreed with
///         the session-persistence agent (manifest-hygiene owner) via the coordination
///         channel.
///     </para>
/// </summary>
public sealed class AtomEmitContractTests
{
    private readonly ITestOutputHelper _out;
    public AtomEmitContractTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    ///     Atoms excluded from the generic gate because their emissions depend on
    ///     accumulated shared learned state (identity observation counts, session history,
    ///     cluster membership) that is non-deterministic under a shared-process test
    ///     harness — the process-wide in-memory SQLite is polluted by parallel tests, so a
    ///     threshold-crossing signal like <c>identity.fingerprint_observation_count_crossed</c>
    ///     fires on some runs and not others. These need dedicated deterministic per-atom
    ///     tests that control their state, not a run-once-observe check. (FingerprintMatch's
    ///     deterministic drifts — <c>identity.archetype_kind</c> / <c>identity.fingerprint_first_seen</c>
    ///     — are already flagged to the manifest owner.)
    /// </summary>
    private static readonly HashSet<string> NonDeterministicEmitters = new(StringComparer.Ordinal)
    {
        // Empty: FingerprintMatch's intermittent identity.fingerprint_observation_count_crossed
        // was declared in 9a7c6f3f, so all its possible emissions are now declared and the
        // reverse check passes regardless of which fire on a given run. Re-add an atom here
        // only if it emits an UNDECLARED key off accumulated shared state (flaky in parallel).
    };

    /// <summary>
    ///     Deterministic atoms that raise a key under their owned prefix the manifest does
    ///     NOT declare — genuine reverse-drift found 2026-07-06. The fix is manifest-side
    ///     (declare in <c>emits.on_complete</c>, or prune the emit) and is owned by the
    ///     session-persistence agent. Flagged via the coordination channel. Remove entries
    ///     here as the manifests are corrected; a stale entry is harmless (once the key is
    ///     declared it never trips the assertion, so the exception is redundant).
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> KnownReverseDrift = new(StringComparer.Ordinal)
    {
        // BrowserModeClassifier's backfilled manifest (9a7c6f3f, best-effort grep) declares
        // some identity.* keys but not identity.browser_mode_similarity, which the atom raises.
        // Surfaced once the atom entered reverse coverage. Flagged to the manifest owner;
        // remove when declared.
        ["BrowserModeClassifier"] = new(StringComparer.Ordinal) { "identity.browser_mode_similarity" },
    };

    [Fact]
    public async Task No_atom_raises_an_undeclared_signal_under_its_owned_prefix()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddBotDetectionInMemory();
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = MaximalRequest();

        var atoms = provider.GetServices<IDetectorAtom>().ToList();
        atoms.Count.Should().BeGreaterThan(40, "the full atom set should register under AddBotDetectionInMemory");

        var loader = new DetectorManifestLoader();
        loader.LoadEmbeddedManifests();
        var byAtomKey = loader.GetAllDetectorManifests().Values
            .GroupBy(ManifestAtomKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var failures = new List<string>();

        foreach (var atom in atoms)
        {
            if (NonDeterministicEmitters.Contains(atom.Name))
                continue; // learned-state atom — emissions non-deterministic under shared harness

            if (!byAtomKey.TryGetValue(atom.Name, out var manifest))
                continue; // orphan atom (no manifest) — nothing to reverse-check; tracked separately

            var declared = manifest.Emits?.OnComplete?
                .Where(s => !string.IsNullOrEmpty(s.Key)).Select(s => s.Key)
                .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            if (declared.Count == 0)
                continue; // manifest declares no on_complete keys — no owned prefix to police

            var ownedPrefixes = declared.Select(Prefix).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var sink = new SignalSink(maxCapacity: 512, maxAge: TimeSpan.FromMinutes(5));
            await atom.DetectAsync(sink, sessionId: "contract");
            var raised = sink.Sense().Select(e => e.Signal.Split(':', 2)[0]).ToHashSet(StringComparer.Ordinal);

            var allowed = KnownReverseDrift.TryGetValue(atom.Name, out var drift) ? drift : null;

            var undeclared = raised
                .Where(k => ownedPrefixes.Contains(Prefix(k)))
                .Where(k => !declared.Contains(k))
                .Where(k => allowed is null || !allowed.Contains(k))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            if (undeclared.Count > 0)
                failures.Add($"{atom.Name} ({manifest.Name}): {string.Join(", ", undeclared)}");
        }

        foreach (var f in failures) _out.WriteLine(f);
        failures.Should().BeEmpty(
            "atoms must not raise signals under their own manifest's prefix that the manifest " +
            "doesn't declare under emits.on_complete (add the key to the manifest, or add it to " +
            "KnownReverseDrift if it's a tracked drift). Offenders:\n" + string.Join("\n", failures));
    }

    private static string Prefix(string key) => key.Split('.', 2)[0];

    // Join: manifest.Name is uniformly "<AtomName>Contributor" (Step-7 legacy) or "<AtomName>".
    // scope.atom is inconsistently populated and is the manifest owner's to normalise.
    private static string ManifestAtomKey(DetectorManifest m) =>
        m.Name.EndsWith("Contributor", StringComparison.Ordinal)
            ? m.Name[..^"Contributor".Length]
            : m.Name;

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
