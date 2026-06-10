using Mostlylucid.BotDetection.Data.Schema;

namespace Mostlylucid.BotDetection.UI.Data.Schema;

/// <summary>
///     Thin facade over the core <see cref="SchemaLoader"/> that pre-binds
///     this assembly + the UI-specific resource prefix. UI persistence
///     schemas live under <c>Data/Schema/*.sql</c> in this project; call
///     sites stay one-liners: <c>UiSchemaLoader.Load("dashboard_users")</c>.
/// </summary>
public static class UiSchemaLoader
{
    private const string ResourcePrefix = "Mostlylucid.BotDetection.UI.Data.Schema.";

    public static string Load(string name)
        => SchemaLoader.Load(typeof(UiSchemaLoader).Assembly, ResourcePrefix, name);
}
