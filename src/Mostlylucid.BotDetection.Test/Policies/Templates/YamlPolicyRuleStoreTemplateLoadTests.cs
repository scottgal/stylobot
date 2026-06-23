using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Templates;

namespace Mostlylucid.BotDetection.Test.Policies.Templates;

/// <summary>
///     Integration tests for the YAML-policy-store template-load path
///     (T2): bare rules plus template-application materialized rules merge
///     in the same corpus; override-divergence detection survives reloads.
/// </summary>
public class YamlPolicyRuleStoreTemplateLoadTests
{
    private const string SeedPrefix = "Mostlylucid.BotDetection.Policies.Rules.SeedRules.";

    private static TemplateMaterializer Materializer(IEnumerable<Template> templates)
        => new(new TemplateRegistry(templates), new TemplateResolver());

    private static Template SimpleTemplate(string id = "t-simple") => new(
        Id: id,
        DisplayName: id,
        Description: "",
        Category: "test",
        Parameters: Array.Empty<TemplateParameter>(),
        Expansion: new[]
        {
            new TemplateExpansionEntry("only", "ua.is_bot = true", "{ kind: block }", 100, "live"),
        });

    [Fact]
    public async Task Empty_applications_loader_returns_bare_rules_only()
    {
        var store = YamlPolicyRuleStore.FromEmbeddedResourcesWithTemplates(
            typeof(PolicyRule).Assembly,
            SeedPrefix,
            Materializer(new[] { SimpleTemplate() }),
            applicationsLoader: () => Array.Empty<TemplateApplication>());

        await store.InitializeAsync();

        var all = await store.GetAllRulesAsync();
        // FOSS seed rules ship 5 yaml files under SeedRules/.
        Assert.NotEmpty(all);
        // No origin-tagged rules.
        Assert.DoesNotContain(all, r => r.Origin is not null);
        Assert.Empty(store.DivergedRuleIds);
    }

    [Fact]
    public async Task Single_application_yields_bare_rules_plus_materialized_rules()
    {
        var template = SimpleTemplate();
        var app = new TemplateApplication(
            Id: "test-app",
            TemplateId: template.Id,
            AppliedTo: new[] { PolicyScope.Domain("example.com") },
            Parameters: new Dictionary<string, object?>());

        var store = YamlPolicyRuleStore.FromEmbeddedResourcesWithTemplates(
            typeof(PolicyRule).Assembly,
            SeedPrefix,
            Materializer(new[] { template }),
            applicationsLoader: () => new[] { app });

        await store.InitializeAsync();

        var all = await store.GetAllRulesAsync();
        var materialized = all.Where(r => r.Origin is not null).ToArray();
        Assert.Single(materialized);
        Assert.Equal(template.Id, materialized[0].Origin!.TemplateId);
        Assert.Equal("test-app", materialized[0].Origin!.ApplicationId);
        Assert.Empty(store.DivergedRuleIds);
    }

    [Fact]
    public async Task Catalog_templates_zero_applications_loads_without_errors()
    {
        // Loading the FOSS catalog templates with zero applications should
        // load the bare seed rules and emit zero materialized rules, with no
        // errors logged into store state. We intentionally do NOT assert an
        // exact template count here -- the catalog grows over time and
        // YamlTemplateStoreTests.Embedded_catalog_loads_every_yaml_without_error
        // owns that contract.
        var templateStore = new YamlTemplateStore();
        var templates = templateStore.LoadEmbeddedCatalog();
        Assert.NotEmpty(templates);

        var store = YamlPolicyRuleStore.FromEmbeddedResourcesWithTemplates(
            typeof(PolicyRule).Assembly,
            SeedPrefix,
            Materializer(templates),
            applicationsLoader: () => Array.Empty<TemplateApplication>());

        await store.InitializeAsync();

        var all = await store.GetAllRulesAsync();
        Assert.NotEmpty(all);
        Assert.DoesNotContain(all, r => r.Origin is not null);
    }

    [Fact]
    public async Task Manual_edit_of_materialized_rule_between_reloads_preserves_the_edit()
    {
        // Drive two reloads in the same process: first materialization sees
        // no bare-rule override and emits the template's "block" action; then
        // we drop a bare YAML file with the same deterministic id and a
        // modified action, wait for the FS-watcher debounced reload, and
        // confirm the operator's edit persists.
        var template = SimpleTemplate();
        var app = new TemplateApplication(
            Id: "edit-app",
            TemplateId: template.Id,
            AppliedTo: new[] { PolicyScope.Domain("example.com") },
            Parameters: new Dictionary<string, object?>());

        // The deterministic id for this materialization.
        var expectedId = TemplateResolver.ComputeStableRuleId(
            template.Id, app.Id, "only", PolicyScope.Domain("example.com"));

        var tmp = Directory.CreateTempSubdirectory("policy-rule-template-load").FullName;
        try
        {
            var store = YamlPolicyRuleStore.FromDirectoryWithTemplates(
                tmp,
                Materializer(new[] { template }),
                applicationsLoader: () => new[] { app });

            await store.InitializeAsync();

            var firstAll = await store.GetAllRulesAsync();
            var materialized = firstAll.Single(r => r.Id == expectedId);
            Assert.IsType<PolicyAction.Block>(materialized.Action);

            // Attach the handler BEFORE writing so we never race the watcher.
            var changed = new TaskCompletionSource<PolicyScope>();
            store.Changed += (_, e) => changed.TrySetResult(e.ChangedScope);

            // Brief settle so the debounce window doesn't swallow the rewrite.
            await Task.Delay(350);

            // Operator opens the rule in the dashboard and saves it back to
            // disk with an edited priority and an Allow action. FOSS shape:
            // the override is a bare YAML file with the SAME id.
            //
            // Write-then-rename: on Linux, File.WriteAllTextAsync into the
            // watched directory fires inotify Created/Changed during the write
            // itself, so the watcher's Reload races the write and can read a
            // partial / empty file. Writing to a sibling (sub-directory the
            // watcher ignores) and then File.Move atomic-renames the fully
            // written file into the watched directory in one syscall, so the
            // watcher only ever sees the final state.
            await AtomicWriteYamlAsync(tmp, "edit.yaml",
                $$"""
                id: {{expectedId}}
                scope:
                  kind: domain
                  domain: example.com
                priority: 999
                predicate: "ua.is_bot = true"
                action:
                  kind: allow
                mode: live
                notes: "operator-edited"
                """);

            await Task.WhenAny(changed.Task, Task.Delay(5000));

            // Fall back to a forced reload on slow CI runners where the
            // inotify event hasn't been delivered yet.
            if (!changed.Task.IsCompleted)
                await store.InitializeAsync();

            var secondAll = await store.GetAllRulesAsync();
            var afterEdit = secondAll.Single(r => r.Id == expectedId);

            // The operator's edit wins: action is Allow, priority is 999.
            Assert.IsType<PolicyAction.Allow>(afterEdit.Action);
            Assert.Equal(999, afterEdit.Priority);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task DivergedRuleIds_reflects_the_manually_edited_rule_after_reload()
    {
        var template = SimpleTemplate();
        var app = new TemplateApplication(
            Id: "divtest-app",
            TemplateId: template.Id,
            AppliedTo: new[] { PolicyScope.Domain("example.com") },
            Parameters: new Dictionary<string, object?>());

        var expectedId = TemplateResolver.ComputeStableRuleId(
            template.Id, app.Id, "only", PolicyScope.Domain("example.com"));

        var tmp = Directory.CreateTempSubdirectory("policy-rule-diverged-set").FullName;
        try
        {
            var store = YamlPolicyRuleStore.FromDirectoryWithTemplates(
                tmp,
                Materializer(new[] { template }),
                applicationsLoader: () => new[] { app });

            await store.InitializeAsync();
            Assert.Empty(store.DivergedRuleIds);

            var changed = new TaskCompletionSource<PolicyScope>();
            store.Changed += (_, e) => changed.TrySetResult(e.ChangedScope);

            await Task.Delay(350);

            // See the matching write-then-rename comment in the test above.
            await AtomicWriteYamlAsync(tmp, "override.yaml",
                $$"""
                id: {{expectedId}}
                scope:
                  kind: domain
                  domain: example.com
                priority: 7
                predicate: "ua.is_bot = true"
                action:
                  kind: observe
                mode: live
                notes: ""
                """);

            await Task.WhenAny(changed.Task, Task.Delay(5000));

            // Fall back to forced reload on slow CI runners where inotify
            // takes longer than the debounce window. Do NOT force a second
            // reload when the watcher already fired -- divergence detection
            // is one-shot (a second reload converges priorHashes to the bare
            // rule's hash and wipes the diverged flag).
            if (!changed.Task.IsCompleted)
                await store.InitializeAsync();

            Assert.Contains(expectedId, store.DivergedRuleIds);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    /// <summary>
    ///     Write <paramref name="content"/> to a sibling staging path then
    ///     <see cref="File.Move(string, string, bool)"/> it into
    ///     <paramref name="watchedDir"/> under <paramref name="fileName"/>.
    ///     Rename is atomic on Linux (rename(2)) and macOS, so the
    ///     <see cref="FileSystemWatcher"/> never sees a partial write -- the
    ///     file appears in the watched directory in its final, fully-written
    ///     form. Avoids the inotify mid-write race that flakes these tests on
    ///     CI when written directly via <see cref="File.WriteAllTextAsync"/>.
    /// </summary>
    private static async Task AtomicWriteYamlAsync(string watchedDir, string fileName, string content)
    {
        // Sibling staging dir -- NOT inside watchedDir, because the watcher's
        // filter would still see the write events even with IncludeSubdirectories=false
        // depending on platform quirks; safer to keep it fully out of the tree.
        var staging = Path.Combine(Path.GetTempPath(), $"stylobot-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        var stagedPath = Path.Combine(staging, fileName);
        try
        {
            await File.WriteAllTextAsync(stagedPath, content);
            File.Move(stagedPath, Path.Combine(watchedDir, fileName), overwrite: true);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }
}
