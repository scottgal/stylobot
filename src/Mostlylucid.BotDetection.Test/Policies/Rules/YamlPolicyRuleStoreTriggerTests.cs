using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Test.Policies.Rules;

/// <summary>
///     YAML round-trip tests for the Phase G data additions: the
///     <c>trigger:</c> block on a rule and the <c>action: { kind: throttle }</c>
///     variant. Mirrors the layout of <see cref="YamlPolicyRuleStoreTests"/>
///     -- each test writes a fresh YAML file to a temp directory, loads via
///     <see cref="YamlPolicyRuleStore.FromDirectory"/>, then asserts the
///     parsed domain object reflects the on-disk shape.
/// </summary>
public class YamlPolicyRuleStoreTriggerTests
{
    [Fact]
    public async Task Rule_without_trigger_block_parses_with_null_Trigger()
    {
        var tmp = Directory.CreateTempSubdirectory("policy-rule-trigger-test").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, "rule.yaml"),
                """
                id: aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa
                scope:
                  kind: wildcard
                priority: 100
                predicate: "is_human = true"
                action:
                  kind: allow
                mode: live
                notes: ""
                """);

            var store = YamlPolicyRuleStore.FromDirectory(tmp);
            await store.InitializeAsync();

            var rules = await store.GetRulesAtAsync(PolicyScope.Wildcard());
            Assert.Single(rules);
            Assert.Null(rules[0].Trigger);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task Rule_with_trigger_block_parses_with_explicit_durations()
    {
        var tmp = Directory.CreateTempSubdirectory("policy-rule-trigger-test").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, "rule.yaml"),
                """
                id: aaaaaaaa-2222-2222-2222-aaaaaaaaaaaa
                scope:
                  kind: wildcard
                priority: 100
                predicate: "is_human = true"
                action:
                  kind: observe
                trigger:
                  sustain_for: 30s
                  recover_after: 60s
                mode: live
                notes: ""
                """);

            var store = YamlPolicyRuleStore.FromDirectory(tmp);
            await store.InitializeAsync();

            var rules = await store.GetRulesAtAsync(PolicyScope.Wildcard());
            Assert.Single(rules);
            var trigger = Assert.IsType<RuleTriggerOptions>(rules[0].Trigger);
            Assert.Equal(TimeSpan.FromSeconds(30), trigger.EffectiveSustainFor);
            Assert.Equal(TimeSpan.FromSeconds(60), trigger.EffectiveRecoverAfter);
            Assert.Null(trigger.ActionWhileArmed);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task Rule_with_empty_trigger_block_parses_and_falls_back_to_defaults()
    {
        var tmp = Directory.CreateTempSubdirectory("policy-rule-trigger-test").FullName;
        try
        {
            // trigger: present but with no fields -- spec default durations engage.
            await File.WriteAllTextAsync(Path.Combine(tmp, "rule.yaml"),
                """
                id: aaaaaaaa-3333-3333-3333-aaaaaaaaaaaa
                scope:
                  kind: wildcard
                priority: 100
                predicate: "is_human = true"
                action:
                  kind: observe
                trigger: {}
                mode: live
                notes: ""
                """);

            var store = YamlPolicyRuleStore.FromDirectory(tmp);
            await store.InitializeAsync();

            var rules = await store.GetRulesAtAsync(PolicyScope.Wildcard());
            Assert.Single(rules);
            var trigger = Assert.IsType<RuleTriggerOptions>(rules[0].Trigger);
            Assert.Equal(RuleTriggerOptions.DefaultSustainFor, trigger.EffectiveSustainFor);
            Assert.Equal(RuleTriggerOptions.DefaultRecoverAfter, trigger.EffectiveRecoverAfter);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task Rule_with_throttle_action_parses_with_rps_and_reason()
    {
        var tmp = Directory.CreateTempSubdirectory("policy-rule-trigger-test").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, "rule.yaml"),
                """
                id: aaaaaaaa-4444-4444-4444-aaaaaaaaaaaa
                scope:
                  geo: RU
                priority: 100
                predicate: "is_human = true"
                action:
                  kind: throttle
                  rps: 10
                  reason: "geo-cap-RU"
                mode: live
                notes: ""
                """);

            var store = YamlPolicyRuleStore.FromDirectory(tmp);
            await store.InitializeAsync();

            var rules = await store.GetEffectiveRulesAsync(new[] { PolicyScope.Wildcard() });
            Assert.Single(rules);
            var throttle = Assert.IsType<PolicyAction.Throttle>(rules[0].Action);
            Assert.Equal(10, throttle.RequestsPerSecond);
            Assert.Equal("geo-cap-RU", throttle.Reason);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task Rule_with_throttle_armed_action_inside_trigger_parses()
    {
        var tmp = Directory.CreateTempSubdirectory("policy-rule-trigger-test").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, "rule.yaml"),
                """
                id: aaaaaaaa-5555-5555-5555-aaaaaaaaaaaa
                scope:
                  kind: wildcard
                priority: 100
                predicate: "is_human = true"
                action:
                  kind: observe
                trigger:
                  sustain_for: 30s
                  recover_after: 60s
                  action_while_armed:
                    kind: throttle
                    rps: 5
                    reason: "armed-cap"
                mode: live
                notes: ""
                """);

            var store = YamlPolicyRuleStore.FromDirectory(tmp);
            await store.InitializeAsync();

            var rules = await store.GetRulesAtAsync(PolicyScope.Wildcard());
            Assert.Single(rules);
            var trigger = Assert.IsType<RuleTriggerOptions>(rules[0].Trigger);
            var armed = Assert.IsType<PolicyAction.Throttle>(trigger.ActionWhileArmed);
            Assert.Equal(5, armed.RequestsPerSecond);
            Assert.Equal("armed-cap", armed.Reason);
            // The regular action stays untouched.
            Assert.IsType<PolicyAction.Observe>(rules[0].Action);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("5m", 300)]
    [InlineData("1h", 3600)]
    public async Task Trigger_durations_accept_short_unit_grammar(string input, int expectedSeconds)
    {
        var tmp = Directory.CreateTempSubdirectory("policy-rule-trigger-test").FullName;
        try
        {
            var yaml = $"""
                id: aaaaaaaa-6666-6666-6666-aaaaaaaaaaaa
                scope:
                  kind: wildcard
                priority: 100
                predicate: "is_human = true"
                action:
                  kind: observe
                trigger:
                  sustain_for: {input}
                  recover_after: {input}
                mode: live
                notes: ""
                """;
            await File.WriteAllTextAsync(Path.Combine(tmp, "rule.yaml"), yaml);

            var store = YamlPolicyRuleStore.FromDirectory(tmp);
            await store.InitializeAsync();

            var rules = await store.GetRulesAtAsync(PolicyScope.Wildcard());
            Assert.Single(rules);
            var trigger = Assert.IsType<RuleTriggerOptions>(rules[0].Trigger);
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), trigger.EffectiveSustainFor);
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), trigger.EffectiveRecoverAfter);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void Duration_parser_accepts_short_unit_grammar()
    {
        // Belt-and-braces on the helper itself so the failure mode is obvious
        // if the YAML test fails (parser vs deserialiser ambiguity).
        Assert.True(DurationParser.TryParse("30s", out var s));
        Assert.Equal(TimeSpan.FromSeconds(30), s);

        Assert.True(DurationParser.TryParse("5m", out var m));
        Assert.Equal(TimeSpan.FromMinutes(5), m);

        Assert.True(DurationParser.TryParse("1h", out var h));
        Assert.Equal(TimeSpan.FromHours(1), h);

        Assert.True(DurationParser.TryParse("1.5m", out var fractional));
        Assert.Equal(TimeSpan.FromSeconds(90), fractional);

        Assert.True(DurationParser.TryParse("00:00:30", out var canonical));
        Assert.Equal(TimeSpan.FromSeconds(30), canonical);

        Assert.False(DurationParser.TryParse("", out _));
        Assert.False(DurationParser.TryParse("garbage", out _));
        Assert.False(DurationParser.TryParse(null, out _));
    }
}
