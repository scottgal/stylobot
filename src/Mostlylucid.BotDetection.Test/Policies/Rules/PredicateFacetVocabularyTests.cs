using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Xunit;

// This test's own namespace (…Test.Policies.Predicate) shadows the product's
// Policies.Predicate namespace, so the AST root needs an unambiguous alias.
using PredicateNode = Mostlylucid.BotDetection.Policies.Predicate.Predicate;

namespace Mostlylucid.BotDetection.Test.Policies.Rules;

/// <summary>
///     THE DURABLE LOCK for 8.8.1's dead-predicate class. Every facet named by a
///     shipped predicate must be a key some layer actually PRODUCES, and every
///     bot-type value must parse to a real <see cref="BotType"/> member.
///
///     <para>
///         <b>Why both axes.</b> Facet first: six of eight seed rules were inert
///         because their facet names (<c>bot.type</c>, <c>is_human</c>,
///         <c>score.bot_probability</c>) were produced by nothing, and a value-only
///         check would have passed all six. Value second, and not redundant: two
///         SHIPPED customer templates ("Protect search", "Protect content from
///         scraping") had a live facet and a dead value -- <c>ua.bot_type = "scraper"</c>
///         never matches because comparison is ordinal and the produced value is
///         <c>BotType.ToString()</c>, i.e. <c>"Scraper"</c>. Each axis catches
///         defects the other cannot.
///     </para>
///
///     <para>
///         <b>Why this must stay a test.</b> <c>PredicateEvaluator.EvaluateTerm</c>
///         returns <c>false</c> on an unknown facet, so a dead predicate is
///         indistinguishable at runtime from a legitimately-false one. Nothing else
///         will ever tell us. Silence must not read as health.
///     </para>
///
///     <para>
///         The producible set is derived by REFLECTION over <c>SignalKeys</c> and
///         <c>PolicyScopeMatcher</c>, not hand-listed, so it stays true as those grow.
///     </para>
/// </summary>
public class PredicateFacetVocabularyTests
{
    /// <summary>
    ///     Namespaces owned by an <c>ISignalContributor</c>. Contributors populate
    ///     these dynamically, so membership is by prefix rather than by constant.
    /// </summary>
    private static readonly string[] ContributorNamespaces = ["meter.", "pressure."];

    /// <summary>
    ///     Every key any layer can produce: <c>SignalKeys</c> constants (evidence
    ///     signals) plus <c>PolicyScopeMatcher</c> constants (scope-derived).
    /// </summary>
    private static readonly HashSet<string> ProducibleFacets = BuildProducibleFacets();

    private static HashSet<string> BuildProducibleFacets()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in new[] { typeof(SignalKeys), typeof(PolicyScopeMatcher) })
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                if (f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string)
                    && f.GetRawConstantValue() is string v && v.Length > 0)
                    set.Add(v);
        return set;
    }

    private static bool IsProducible(string facet) =>
        ProducibleFacets.Contains(facet)
        || ContributorNamespaces.Any(ns => facet.StartsWith(ns, StringComparison.Ordinal));

    /// <summary>Every embedded shipped predicate, as (resource name, predicate text).</summary>
    public static TheoryData<string, string> ShippedPredicates()
    {
        var data = new TheoryData<string, string>();
        var asm = typeof(YamlPolicyRuleStore).Assembly;
        var predicateLine = new Regex(
            """^\s*predicate:\s*(?<q>['"])(?<body>.*)\k<q>\s*$""",
            RegexOptions.Multiline);

        foreach (var name in asm.GetManifestResourceNames()
                     .Where(n => n.EndsWith(".yaml", StringComparison.Ordinal)
                                 && (n.Contains(".SeedRules.", StringComparison.Ordinal)
                                     || n.Contains(".Templates.Catalog.", StringComparison.Ordinal)))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();

            foreach (System.Text.RegularExpressions.Match m in predicateLine.Matches(text))
            {
                // YAML double-quoted scalars escape inner quotes; unescape so the
                // predicate parser sees what the loader would hand it.
                var body = m.Groups["body"].Value.Replace("\\\"", "\"");
                if (!string.IsNullOrWhiteSpace(body))
                    data.Add(name, body);
            }

        }

        return data;
    }

    /// <summary>
    ///     Template catalog predicates are AUTHORING sources: thresholds appear as
    ///     unmaterialized <c>{{ param }}</c> placeholders that only become literals when
    ///     a customer applies the template. The predicate grammar rejects them, so
    ///     substitute a neutral numeric literal to reach the facets underneath.
    ///
    ///     <para>
    ///         Facet names are never parameterized -- only thresholds are -- so AXIS 1
    ///         keeps full coverage over templates. AXIS 2 deliberately skips these,
    ///         because a substituted value is synthetic and asserting on it would be
    ///         testing this helper rather than the corpus.
    ///     </para>
    /// </summary>
    private static readonly Regex TemplatePlaceholder = new(@"\{\{[^}]*\}\}");

    private static bool HasPlaceholder(string predicate) => TemplatePlaceholder.IsMatch(predicate);

    /// <summary>
    ///     A placeholder in <c>for {{ window }}</c> position is a Sustain DURATION and
    ///     the grammar demands a duration literal there, so it needs a different stand-in
    ///     from a numeric threshold. Substituted first, then everything else becomes 0.
    /// </summary>
    private static readonly Regex SustainDurationPlaceholder = new(@"\bfor\s*\{\{[^}]*\}\}");

    private static PredicateNode ParseForFacets(string predicate)
    {
        var normalized = SustainDurationPlaceholder.Replace(predicate, "for 30s");
        return PredicateParser.Parse(TemplatePlaceholder.Replace(normalized, "0"));
    }

    private static IEnumerable<PredicateNode.Term> Terms(PredicateNode node)
    {
        switch (node)
        {
            case PredicateNode.Term t:
                yield return t;
                break;
            case PredicateNode.And a:
                foreach (var c in a.Children)
                    foreach (var t in Terms(c)) yield return t;
                break;
            case PredicateNode.Or o:
                foreach (var c in o.Children)
                    foreach (var t in Terms(c)) yield return t;
                break;
            case PredicateNode.Sustain s:
                foreach (var t in Terms(s.Inner)) yield return t;
                break;
        }
    }

    /// <summary>
    ///     Sanity guard: if the harness ever stops finding predicates, every
    ///     assertion below becomes vacuously true. A sweep that silently matches
    ///     nothing is worse than no sweep -- it reports "all clear".
    /// </summary>
    [Fact]
    public void Harness_actually_finds_the_shipped_predicates()
    {
        var found = ShippedPredicates().Select(row => (string)row[0]).Distinct().ToList();

        Assert.NotEmpty(found);
        Assert.Contains(found, n => n.Contains(".SeedRules.", StringComparison.Ordinal));
        Assert.Contains(found, n => n.Contains(".Templates.Catalog.", StringComparison.Ordinal));
    }

    /// <summary>
    ///     CONTROL -- proves both axes have teeth. A validator that cannot fail is
    ///     worth nothing, and a green corpus is only meaningful if the checks would
    ///     actually reject the exact defects that shipped. These are the real 8.8.1
    ///     values, asserted to be rejected.
    /// </summary>
    [Fact]
    public void Both_axes_reject_the_defects_that_actually_shipped()
    {
        // AXIS 1 would have caught the six inert seed rules.
        Assert.False(IsProducible("bot.type"));
        Assert.False(IsProducible("is_human_typo"));

        // ...without rejecting the names that replaced them.
        Assert.True(IsProducible(SignalKeys.UserAgentBotType));
        Assert.True(IsProducible(SignalKeys.IsHuman));
        Assert.True(IsProducible(SignalKeys.ScoreBotProbability));
        Assert.True(IsProducible(PolicyScopeMatcher.GeoCountryKey));
        Assert.True(IsProducible("meter.gateway.cpu_load.current"));

        // AXIS 2 would have caught the two shipped customer templates: right facet,
        // wrong case. Case-insensitive parsing would hide exactly this.
        Assert.False(Enum.TryParse<BotType>("scraper", ignoreCase: false, out _));
        Assert.False(Enum.TryParse<BotType>("registry_client", ignoreCase: false, out _));
        Assert.False(Enum.TryParse<BotType>("fediverse_bot", ignoreCase: false, out _));
        Assert.False(Enum.TryParse<BotType>("crawler", ignoreCase: false, out _));
        Assert.True(Enum.TryParse<BotType>("Scraper", ignoreCase: false, out _));
        Assert.True(Enum.TryParse<BotType>("RegistryClient", ignoreCase: false, out _));
    }

    /// <summary>
    ///     AXIS 1 (facet): every facet in every shipped predicate must be produced by
    ///     some layer. This is the check that would have caught all six dead seed rules.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedPredicates))]
    public void Every_shipped_predicate_facet_is_actually_produced(string resource, string predicate)
    {
        var ast = ParseForFacets(predicate);

        var dead = Terms(ast).Select(t => t.Facet).Where(f => !IsProducible(f)).Distinct().ToList();

        Assert.True(
            dead.Count == 0,
            $"{resource}: predicate references facet(s) nothing produces: {string.Join(", ", dead)}. "
            + $"Predicate: {predicate}. An unknown facet evaluates FALSE silently, so this rule would "
            + "never fire and never complain. Use a SignalKeys / PolicyScopeMatcher constant, or add a "
            + "producer first.");
    }

    /// <summary>
    ///     AXIS 2 (value): a bot-type facet's values must parse to real
    ///     <see cref="BotType"/> members, case-sensitively -- the evaluator compares
    ///     ordinally and the produced value is <c>BotType.ToString()</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedPredicates))]
    public void Bot_type_predicate_values_parse_to_real_enum_members(string resource, string predicate)
    {
        // Parameterized template sources carry synthetic values after substitution;
        // their real values are only known at materialization. AXIS 1 still covers
        // their facets. See TemplatePlaceholder.
        if (HasPlaceholder(predicate)) return;

        var ast = PredicateParser.Parse(predicate);

        var bad = new List<string>();
        foreach (var term in Terms(ast).Where(t =>
                     string.Equals(t.Facet, SignalKeys.UserAgentBotType, StringComparison.Ordinal)))
        foreach (var raw in term.Value switch
                 {
                     string s => [s],
                     IEnumerable<string> many => many,
                     _ => Array.Empty<string>()
                 })
        {
            // Ordinal on purpose: Enum.TryParse's case-insensitive overload would
            // accept "scraper" and hide exactly the template defect this catches.
            if (!Enum.TryParse<BotType>(raw, ignoreCase: false, out _))
                bad.Add(raw);
        }

        Assert.True(
            bad.Count == 0,
            $"{resource}: {SignalKeys.UserAgentBotType} compared against value(s) that are not BotType "
            + $"members (ordinal, case-sensitive): {string.Join(", ", bad)}. Predicate: {predicate}. "
            + "Values must be the enum's ToString() projection, e.g. \"Scraper\" not \"scraper\".");
    }
}
