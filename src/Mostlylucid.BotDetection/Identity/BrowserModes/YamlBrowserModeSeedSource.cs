using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VYaml.Annotations;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.Identity.BrowserModes;

/// <summary>
///     Operator-facing seed source for the mode-centroid catalogue. Reads
///     <c>Definitions/BrowserModes/*.yaml</c> (the same files
///     <see cref="BrowserModeRegistry"/> uses), pulls the <c>seed:</c> block
///     from each, and runs the canonical signal values through
///     <see cref="ModeVectorEncoder"/> to produce layout-sized centroid
///     vectors that live in the same vector space as live observations.
///
///     <para>
///     This is the destination for the catalogue source-of-truth per
///     <c>feedback_no_word_lists</c>: YAML is the place operators read and
///     edit the seed table, and the C# stays oblivious to the values it
///     loads. <see cref="HardcodedBrowserModeSeedSource"/> still ships for
///     test usage (simplest fake seed source -- no embedded resource setup
///     required in unit tests) but the runtime DI path goes through this
///     loader.
///     </para>
///
///     <para>
///     YAML carries semantic signal values
///     (<c>method</c> / <c>sec_fetch_mode</c> / <c>upgrade_websocket</c>),
///     never raw centroid floats. That keeps the YAML readable and stable
///     across layout changes -- the day we widen the layout, the encoder
///     learns the new slot but the YAML files stay untouched. Rows with no
///     <c>seed:</c> block emit an all-zero centroid (the
///     <c>unknown</c> sentinel pattern).
///     </para>
/// </summary>
public sealed class YamlBrowserModeSeedSource : IBrowserModeSeedSource
{
    private readonly IdentityVectorLayout _layout;
    private readonly ILogger _logger;
    private readonly Assembly _assembly;

    public YamlBrowserModeSeedSource(
        IdentityVectorLayout layout,
        ILogger<YamlBrowserModeSeedSource>? logger = null)
    {
        _layout = layout;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        // Same embedded-resource convention BrowserModeRegistry uses; the
        // YAML files are baked into this assembly so the seed source is
        // location-agnostic at runtime.
        _assembly = typeof(YamlBrowserModeSeedSource).Assembly;
    }

    public IReadOnlyList<ModeSeed> LoadYamlSeeds()
    {
        var results = new List<ModeSeed>();
        foreach (var resourceName in _assembly.GetManifestResourceNames())
        {
            if (!resourceName.Contains("BrowserModes", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!resourceName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                using var stream = _assembly.GetManifestResourceStream(resourceName);
                if (stream is null) continue;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);

                var dto = YamlSerializer.Deserialize<BrowserModeSeedYaml>(ms.ToArray());
                if (dto is null || string.IsNullOrEmpty(dto.ModeId)) continue;

                var centroid = dto.Seed is { } seed
                    ? ModeVectorEncoder.Encode(
                        _layout,
                        method:           seed.Method,
                        secFetchMode:     seed.SecFetchMode,
                        upgradeWebsocket: seed.UpgradeWebsocket)
                    : new float[_layout.Dimension];

                results.Add(new ModeSeed(dto.ModeId, centroid));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load browser mode seed from {Resource}", resourceName);
            }
        }

        return results;
    }
}

/// <summary>
///     Narrow read-only DTO scoped to the fields this loader cares about
///     -- mode id + the seed block. Separate from
///     <c>BrowserModeYaml</c> so the registry's compile path stays
///     uncoupled from the centroid seed shape; VYaml tolerates unknown
///     fields, so deserialising the same file twice (once per DTO) is a
///     non-issue.
/// </summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class BrowserModeSeedYaml
{
    public string? ModeId { get; set; }
    public BrowserModeSeedBlock? Seed { get; set; }
}

/// <summary>
///     The <c>seed:</c> block from a browser-mode YAML. Carries canonical
///     signal values (semantics, not vectors); the loader passes these
///     through <see cref="ModeVectorEncoder.Encode"/> to produce the
///     layout-sized centroid at load time. Adding new fields here requires
///     a matching encoder input -- the encoder is the single place that
///     knows how a signal lands on the identity layout.
/// </summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class BrowserModeSeedBlock
{
    public string? Method { get; set; }
    public string? SecFetchMode { get; set; }
    public bool UpgradeWebsocket { get; set; }
}
