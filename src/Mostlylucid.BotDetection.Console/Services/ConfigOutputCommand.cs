using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Console.Services;

/// <summary>
///     Writes the current effective <see cref="BotDetectionOptions"/> to disk as an
///     appsettings.json fragment. Aids operators editing config: they get a fully-
///     populated tree (including defaults) instead of having to grep the source for
///     valid keys + default values.
///
///     <para>
///     Trigger: <c>stylobot --output-config &lt;filename&gt;</c>. Runs before the host
///     starts, exits immediately after writing the file. The output is a single-section
///     <c>{ "BotDetection": { ... } }</c> document - operators copy the bits they want
///     into their real <c>appsettings.json</c>.
///     </para>
/// </summary>
public static class ConfigOutputCommand
{
    /// <summary>
    ///     Dump the effective configuration to the path encoded in <c>--output-config</c>.
    ///     Returns the path on success (the caller exits the process), or null when the
    ///     flag is absent.
    /// </summary>
    public static string? TryGetOutputPath(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--output-config", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    public static int WriteConfig(string outputPath, string[] args)
    {
        try
        {
            // Build the same configuration sources WebApplicationBuilder would, in the
            // same order: appsettings.json + environment-specific + env vars + CLI args.
            // This way the dumped file matches what the running gateway would actually use.
            var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                   ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                   ?? "Production";
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddJsonFile($"appsettings.{env}.json", optional: true, reloadOnChange: false)
                .AddEnvironmentVariables()
                .AddCommandLine(args)
                .Build();

            var options = new BotDetectionOptions();
            config.GetSection("BotDetection").Bind(options);

            // Source-gen JSON context required: the Console binary is AOT-published,
            // so reflection-based JsonSerializer is off. See ConfigOutputJsonContext.
            var json = JsonSerializer.Serialize(
                new ConfigDumpWrapper { BotDetection = options },
                ConfigOutputJsonContext.Default.ConfigDumpWrapper);

            // Redact secrets before the dump touches disk. The dashboard's
            // EffectiveConfigSerializer does this via [Secret] reflection, which
            // is unavailable here (AOT); a name-rule pass over the JSON tree
            // applies the same suffix rule to every nested section instead.
            var tree = JsonNode.Parse(json);
            RedactSecrets(tree);
            json = tree!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outputPath, json);

            System.Console.WriteLine($"  Wrote effective configuration to {outputPath}");
            System.Console.WriteLine($"  Environment: {env}");
            System.Console.WriteLine($"  Loaded from: appsettings.json + appsettings.{env}.json + env + CLI");
            System.Console.WriteLine("  Secrets (keys/tokens/passwords) are redacted to \"***\" - re-add real values by hand.");
            return 0;
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"  Failed to write configuration: {ex.Message}");
            return 1;
        }
    }

    // Same suffix rule as the dashboard's EffectiveConfigSerializer: any property
    // ending in secret/password/token/key (singular or plural) is credential-shaped.
    private static readonly Regex SecretNameRegex = new("(?i)(secret|password|token|key)s?$");

    private static void RedactSecrets(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(kv => kv.Key).ToList())
                {
                    if (SecretNameRegex.IsMatch(name))
                        obj[name] = RedactValue(obj[name]);
                    else
                        RedactSecrets(obj[name]);
                }
                break;
            case JsonArray arr:
                foreach (var item in arr) RedactSecrets(item);
                break;
        }
    }

    private static JsonNode? RedactValue(JsonNode? value) => value switch
    {
        null => null,
        // Same-shaped masking so operators still see how many entries exist.
        JsonArray arr => new JsonArray(arr.Select(_ => (JsonNode)"***").ToArray()),
        // ApiKeys-style maps are keyed BY the credential; the whole object goes.
        JsonObject => "***",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => "***",
        // Numbers/bools that merely end in "Key"/"Token" carry no credential.
        _ => value
    };

    /// <summary>
    ///     Wrapper preserves the appsettings.json shape so the output can be dropped
    ///     into a real appsettings.json without restructuring. Public for the
    ///     source-generated <see cref="ConfigOutputJsonContext"/>.
    /// </summary>
    public sealed class ConfigDumpWrapper
    {
        public BotDetectionOptions BotDetection { get; set; } = new();
    }
}
