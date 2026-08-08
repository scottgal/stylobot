using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Mostlylucid.BotDetection.Policies.Rules;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies.Rules;

/// <summary>
///     Every shipped seed rule must actually LOAD into the store.
///
///     <para>
///         <b>The gap this closes.</b> <c>YamlPolicyRuleStore</c> maps each YAML file
///         inside a try/catch: a bad <c>scope.kind</c> or <c>action.kind</c> throws,
///         the store logs a warning and <c>continue</c>s, and the rule is silently
///         absent from the corpus. Nothing fails. Nothing surfaces. The file still
///         sits on disk looking authoritative.
///     </para>
///
///     <para>
///         Both <c>known-automation-*</c> rules shipped that way and were inert for
///         two independent reasons at once — <c>kind: endpoint</c> with no
///         <c>domain</c>, and <c>kind: rate-limit</c> which <c>MapAction</c> does not
///         accept (it takes <c>rate_limit</c> with <c>requests_per_minute</c>). Their
///         predicates were never even parsed.
///     </para>
///
///     <para>
///         <b>Why <see cref="PredicateFacetVocabularyTests"/> could not catch it.</b>
///         That suite scrapes <c>predicate:</c> lines out of the raw embedded YAML and
///         validates the facets and values it finds. It never asks whether the rule
///         those predicates belong to survived loading — so a perfectly valid
///         predicate inside a rule the store discarded reads as healthy. Validating
///         the text of a file nobody loads is itself a dead mechanism; this test is
///         the other half.
///     </para>
/// </summary>
public class SeedRuleLoadCoverageTests
{
    private const string SeedResourcePrefix = "Mostlylucid.BotDetection.Policies.Rules.SeedRules.";

    private static IReadOnlyList<string> SeedResourceNames() =>
        typeof(YamlPolicyRuleStore).Assembly
            .GetManifestResourceNames()
            .Where(n => n.StartsWith(SeedResourcePrefix, StringComparison.Ordinal)
                        && n.EndsWith(".yaml", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    ///     THE LOCK: one loaded rule per shipped seed file. A file that fails to map
    ///     is skipped with only a log line, so count equality is what makes the
    ///     failure visible.
    /// </summary>
    [Fact]
    public async Task Every_shipped_seed_rule_file_loads_into_the_store()
    {
        var files = SeedResourceNames();

        // Anti-vacuity: if the resource filter ever stops matching, the count
        // assertion below would trivially pass at 0 == 0 and report "all clear".
        Assert.NotEmpty(files);

        var store = YamlPolicyRuleStore.FromEmbeddedResources(
            typeof(YamlPolicyRuleStore).Assembly,
            SeedResourcePrefix);
        await store.InitializeAsync();

        var loaded = await store.GetAllRulesAsync();

        Assert.True(
            loaded.Count == files.Count,
            $"{files.Count} seed rule file(s) shipped but {loaded.Count} loaded into the store. "
            + "A rule whose scope.kind or action.kind fails to map is skipped with only a "
            + "LogWarning, so it is absent from the corpus while its file still looks "
            + "authoritative on disk. Shipped files:\n  "
            + string.Join("\n  ", files.Select(f => f[SeedResourcePrefix.Length..])));
    }

    /// <summary>
    ///     The two rules that shipped inert, pinned by id so a regression names itself
    ///     rather than showing up as an off-by-one in the count above.
    /// </summary>
    [Theory]
    [InlineData("b2c3d4e5-f6a7-8901-bcde-f12345678901", "known-automation-fediverse")]
    [InlineData("00000000-0000-0000-0000-000000000002", "wildcard-default-block-confirmed-bot")]
    public async Task Named_seed_rules_are_present_in_the_loaded_corpus(string ruleId, string which)
    {
        var store = YamlPolicyRuleStore.FromEmbeddedResources(
            typeof(YamlPolicyRuleStore).Assembly,
            SeedResourcePrefix);
        await store.InitializeAsync();

        var loaded = await store.GetAllRulesAsync();

        Assert.True(
            loaded.Any(r => r.Id == Guid.Parse(ruleId)),
            $"seed rule '{which}' ({ruleId}) is not in the loaded corpus — it failed to map "
            + "and was skipped. Check scope.kind (endpoint requires domain + subdomain + "
            + "path_template) and action.kind (rate_limit, not rate-limit; requests_per_minute, "
            + "not max_rpm).");
    }
}
