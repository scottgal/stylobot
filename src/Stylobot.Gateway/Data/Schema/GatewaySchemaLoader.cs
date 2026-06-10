using Mostlylucid.BotDetection.Data.Schema;

namespace Stylobot.Gateway.Data.Schema;

/// <summary>
///     Pre-binds the core <see cref="SchemaLoader"/> to this assembly + the
///     Gateway namespace prefix. Same convention as <c>UiSchemaLoader</c>
///     and <c>ApiHolodeckSchemaLoader</c>.
/// </summary>
public static class GatewaySchemaLoader
{
    private const string ResourcePrefix = "Stylobot.Gateway.Data.Schema.";

    public static string Load(string name)
        => SchemaLoader.Load(typeof(GatewaySchemaLoader).Assembly, ResourcePrefix, name);
}
