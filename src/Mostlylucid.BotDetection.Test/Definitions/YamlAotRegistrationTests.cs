using System.Text.RegularExpressions;
using Mostlylucid.BotDetection.Models;
using VYaml.Annotations;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Definitions;

public class YamlAotRegistrationTests
{
    [Fact]
    public void Every_yaml_model_has_a_static_formatter_registration_for_native_AOT()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        FileInfo? bootstrap = null;
        while (directory is not null)
        {
            var candidate = new FileInfo(Path.Combine(directory.FullName,
                "src", "Mostlylucid.BotDetection", "VYamlBootstrap.cs"));
            if (candidate.Exists)
            {
                bootstrap = candidate;
                break;
            }
            directory = directory.Parent;
        }
        Assert.NotNull(bootstrap);
        var source = File.ReadAllText(bootstrap.FullName);
        // JIT deserialization alone cannot catch this: VYaml's reflective fallback
        // works there, but the trimmer drops formatters without static call sites.
        var registered = Regex.Matches(source, @"\b(\w+)\.__RegisterVYamlFormatter\(\)")
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var models = typeof(BotDetectionOptions).Assembly.GetTypes()
            .Where(type => type.IsDefined(typeof(YamlObjectAttribute), inherit: false)).ToArray();
        Assert.NotEmpty(models);
        var missing = models.Where(type => !registered.Contains(type.Name))
            .Select(type => type.FullName).OrderBy(name => name).ToArray();
        Assert.True(missing.Length == 0,
            "Add static formatter calls to VYamlBootstrap.Init for: " + string.Join(", ", missing));
    }
}
