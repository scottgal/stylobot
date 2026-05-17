using VYaml.Annotations;

namespace Mostlylucid.BotDetection.Definitions.VendorHomeHosts;

/// <summary>
///     YAML shape for <c>Definitions/VendorHomeHosts/vendor-home-hosts.yaml</c>.
///     A flat list of hostnames that appear in friendly-bot UAs as documentation
///     references rather than per-instance identifiers - see the YAML file's own
///     description for the full rationale.
/// </summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class VendorHomeHostsFile
{
    public string Description { get; set; } = "";

    public List<string> Hosts { get; set; } = [];
}
