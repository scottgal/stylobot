using System.Reflection;
using System.Text.RegularExpressions;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Locks the invariant behind a class of bug found 2026-08-17: <c>sink.Raise(SignalKeys.X,
///     sessionId)</c> (bare -- no <c>:value</c> suffix) only ever lands the key in the post-hoc
///     merged-signals dict. It does NOT populate the same-request hint cache that
///     <c>ReadHint</c>/<c>ReadBoolHint</c>/<c>ReadIntHint</c>/<c>ReadDoubleHint</c> read (only
///     colon-encoded <c>"key:value"</c> raises do -- confirmed via direct instrumentation
///     against a live gateway). A signal raised only bare but read via a hint method is
///     permanently invisible to that reader, regardless of the real condition -- this silently
///     killed ClickFraudAtom's entire paid-traffic detection arm and left two CveFingerprintAtom
///     radar dimensions permanently zero.
///     <para>
///         Source-scans <c>src/Mostlylucid.BotDetection</c> (text, not IL/reflection for the
///         call-graph -- there is no manifest of "signals this atom reads via hint" to reflect
///         over) rather than exercising the atom pipeline: the bug is about which RAISE FORM a
///         call site uses, a property of the source text, not of any single request's outcome.
///         Reflection IS used for one thing: resolving each <see cref="SignalKeys"/> constant's
///         string VALUE, so a colon-raise written as a raw literal (<c>"response.from_upstream:"</c>)
///         is recognised exactly like one written via the symbol
///         (<c>$"{SignalKeys.ResponseFromUpstream}:"</c>) -- both compile to the identical runtime
///         signal key, and the text scan should not care which spelling the author used.
///     </para>
/// </summary>
public class SignalRaiseHintReaderInvariantTests
{
    /// <summary>
    ///     Keys read via a hint method that this pass's text scan cannot resolve to a raise site
    ///     with confidence -- either a genuine gap or (more likely, based on the ones audited so
    ///     far) fed through a mechanism the regex can't trace (a third-party layer, a computed
    ///     key built from something other than a local variable, HttpContext.Items directly,
    ///     etc.). Each entry is a real open question, not a verified-safe exception -- do NOT
    ///     add a new key here without checking it first; if you can positively confirm it's
    ///     colon-raised, add it to <see cref="VerifiedIndirectRaisers"/> instead with the
    ///     evidence, not here.
    /// </summary>
    private static readonly HashSet<string> KnownGaps = new(StringComparer.Ordinal)
    {
        // Found by this test's first run (2026-08-18), not yet individually root-caused --
        // no raise site (symbolic OR raw-literal, colon-encoded OR bare) exists anywhere in
        // src/Mostlylucid.BotDetection for any of these. Candidates: genuinely dead signals,
        // fed from Stylobot.Gateway/StyloExtract/Console (outside this scan's source dir),
        // or set directly on HttpContext.Items rather than through the sink.
        "AiConfidence", "AiPrediction", "BrowserVersionAge", "ClientKeyboardEvents",
        "EnforcementMode", "FingerprintHeadlessScore", "FingerprintIntegrityScore",
        "GatewayWarmup", "GeoChangeDriftDetected", "HeaderHashes", "IdentityVectorQuality",
        "InconsistencyScore", "PolicyRevision", "Shed", "UpstreamHealthy",
        "UserAgentBrowser", "UserAgentOs",
    };

    /// <summary>
    ///     Keys manually audited and confirmed to be colon-raised through an indirection the
    ///     text scan cannot trace (a helper method, or a raise inside a loop over a
    ///     category-to-key lookup dictionary) -- genuinely NOT a gap. Evidence is inline; keep
    ///     it current if the indirection changes shape.
    /// </summary>
    private static readonly Dictionary<string, string> VerifiedIndirectRaisers = new(StringComparer.Ordinal)
    {
        // GeoLocationSignalEmitter.RaiseBool(sink, key, prop, geo, sessionId) does
        // sink.Raise($"{key}:{...}", sessionId) internally -- verified 2026-08-18.
        ["GeoIsVpn"] = "GeoLocationSignalEmitter.RaiseBool",
        ["GeoIsProxy"] = "GeoLocationSignalEmitter.RaiseBool",
        ["GeoIsTor"] = "GeoLocationSignalEmitter.RaiseBool",
        ["GeoIsHosting"] = "GeoLocationSignalEmitter.RaiseBool",
        // HaxxorAtom.cs builds a category->SignalKeys.AttackX dictionary, then does
        // sink.Raise($"{signalKey}:true", sessionId) in a loop over matched categories
        // (line ~219) -- verified 2026-08-18.
        ["AttackAdminScan"] = "HaxxorAtom category-map loop",
        ["AttackBackupScan"] = "HaxxorAtom category-map loop",
        ["AttackCmdi"] = "HaxxorAtom category-map loop",
        ["AttackConfigExposure"] = "HaxxorAtom category-map loop",
        ["AttackDebugExposure"] = "HaxxorAtom category-map loop",
        ["AttackPathProbe"] = "HaxxorAtom category-map loop",
        ["AttackSqli"] = "HaxxorAtom category-map loop",
        ["AttackSsrf"] = "HaxxorAtom category-map loop",
        ["AttackSsti"] = "HaxxorAtom category-map loop",
        ["AttackWebshellProbe"] = "HaxxorAtom category-map loop",
        ["AttackXss"] = "HaxxorAtom category-map loop",
    };

    private static readonly Regex SymbolicColonRaisePattern = new(
        @"Raise\(\$""\{SignalKeys\.(\w+)\}:", RegexOptions.Compiled);

    private static readonly Regex BareRaisePattern = new(
        @"Raise\(SignalKeys\.(\w+),", RegexOptions.Compiled);

    private static readonly Regex HintReadPattern = new(
        @"Read(?:Bool|Int|Double)?Hint\(SignalKeys\.(\w+)", RegexOptions.Compiled);

    /// <summary>Every SignalKeys constant's runtime string value, e.g. AiConfidence -> "ai.confidence".</summary>
    private static readonly IReadOnlyDictionary<string, string> SignalKeyValues =
        typeof(SignalKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!, StringComparer.Ordinal);

    private static IEnumerable<string> SourceLines(string sourceDir)
    {
        var files = Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        foreach (var file in files)
        foreach (var rawLine in File.ReadLines(file))
        {
            var line = rawLine.TrimStart();
            if (!line.StartsWith("//", StringComparison.Ordinal)) // skip full-line comments
                yield return line;
        }
    }

    /// <summary>Keys with a colon-encoded raise found via EITHER the symbolic form
    /// ($"{SignalKeys.X}:") OR a raw string literal matching X's runtime value ("x.y:").</summary>
    private static HashSet<string> FindColonRaisedKeys(IEnumerable<string> lines)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var valueToName = SignalKeyValues
            .GroupBy(kv => kv.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);

        foreach (var line in lines)
        {
            foreach (System.Text.RegularExpressions.Match m in SymbolicColonRaisePattern.Matches(line))
                found.Add(m.Groups[1].Value);

            if (!line.Contains("Raise(", StringComparison.Ordinal)) continue;
            foreach (var (value, name) in valueToName)
                if (line.Contains($"\"{value}:", StringComparison.Ordinal))
                    found.Add(name);
        }
        return found;
    }

    [Fact]
    public void Every_key_read_via_a_hint_method_has_a_colon_encoded_raiser_somewhere()
    {
        var sourceDir = LocateSourceDir();
        var lines = SourceLines(sourceDir).ToList();
        Assert.True(lines.Count > 10_000, $"Expected a real source tree under {sourceDir}, found {lines.Count} lines.");

        var colonRaised = FindColonRaisedKeys(lines);
        var hintReaders = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var lineNo = 0;
        foreach (var line in lines)
        {
            lineNo++;
            foreach (System.Text.RegularExpressions.Match m in HintReadPattern.Matches(line))
            {
                var key = m.Groups[1].Value;
                if (!hintReaders.TryGetValue(key, out var sites))
                    hintReaders[key] = sites = new List<int>();
                sites.Add(lineNo);
            }
        }

        var violations = hintReaders.Keys
            .Where(key => !colonRaised.Contains(key)
                       && !VerifiedIndirectRaisers.ContainsKey(key)
                       && !KnownGaps.Contains(key))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(violations.Count == 0,
            "Signal(s) read via ReadHint/ReadBoolHint/ReadIntHint/ReadDoubleHint with NO colon-encoded " +
            "raiser found (symbolic, raw-literal, or a documented verified indirection) -- the reader " +
            "always sees null/false regardless of the real condition:\n  " +
            string.Join("\n  ", violations) +
            "\nFix: change the raise site to sink.Raise($\"{SignalKeys.X}:value\", sessionId). If it's " +
            "raised through a helper/indirection you've verified, add it to VerifiedIndirectRaisers with " +
            "the evidence. If it's a real open question, add it to KnownGaps -- never silently.");
    }

    /// <summary>
    ///     Companion assertion: a key that's ONLY ever bare-raised (never colon-encoded ANYWHERE,
    ///     including by an atom nothing currently reads via hint) is still worth surfacing --
    ///     it's dead-on-arrival for any FUTURE hint reader, silently. This is a warning-shaped
    ///     list, not a failure: bare raises are legitimate for keys only ever consumed via the
    ///     post-hoc merged-signals dict (contribution.Signals / evidence.Signals), which is most
    ///     of them. Kept informational (Assert.True with a permissive threshold) rather than
    ///     failing so it doesn't become a de-facto ban on ever adding a bare raise.
    /// </summary>
    [Fact]
    public void Bare_only_keys_are_visible_for_review_not_silently_growing_unbounded()
    {
        var sourceDir = LocateSourceDir();
        var lines = SourceLines(sourceDir).ToList();

        var colonRaised = FindColonRaisedKeys(lines);
        var bareRaised = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        foreach (System.Text.RegularExpressions.Match m in BareRaisePattern.Matches(line))
            bareRaised.Add(m.Groups[1].Value);

        var bareOnly = bareRaised.Except(colonRaised).Count();
        // Informational ceiling, not a hard design limit -- bump it deliberately (with a look
        // at what grew) rather than letting this test silently stop meaning anything.
        Assert.True(bareOnly <= 60,
            $"{bareOnly} signal keys are ONLY ever bare-raised (never colon-encoded). That's fine when " +
            "nothing reads them via a hint method (checked by the other test in this file) -- this is just " +
            "a growth tripwire so a future hint-reader added against one of them gets noticed in review.");
    }

    private static string LocateSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Mostlylucid.BotDetection");
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, "Orchestration")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate src/Mostlylucid.BotDetection from " + AppContext.BaseDirectory);
    }
}
