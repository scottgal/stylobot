using Mostlylucid.BotDetection.Data.Schema;

namespace Mostlylucid.BotDetection.ApiHolodeck.Data.Schema;

/// <summary>
///     Pre-binds the core <see cref="SchemaLoader"/> to this assembly + the
///     ApiHolodeck namespace prefix. Same convention as <c>UiSchemaLoader</c>
///     in the dashboard project.
/// </summary>
public static class ApiHolodeckSchemaLoader
{
    private const string ResourcePrefix = "Mostlylucid.BotDetection.ApiHolodeck.Data.Schema.";

    public static string Load(string name)
        => SchemaLoader.Load(typeof(ApiHolodeckSchemaLoader).Assembly, ResourcePrefix, name);
}
