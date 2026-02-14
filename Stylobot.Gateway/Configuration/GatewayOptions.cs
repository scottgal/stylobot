namespace Stylobot.Gateway.Configuration;

/// <summary>
/// Core gateway configuration options.
/// </summary>
public class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>
    /// HTTP port for the gateway. Default: 8080
    /// </summary>
    public int HttpPort { get; set; } = 8080;

    /// <summary>
    /// Default upstream URL for catch-all routing.
    /// If set, creates a route: /{**path} -> this URL
    /// </summary>
    public string? DefaultUpstream { get; set; }

    /// <summary>
    /// Admin API base path. Default: /admin
    /// </summary>
    public string AdminBasePath { get; set; } = "/admin";

    /// <summary>
    /// Secret for admin API access. If set, requires X-Admin-Secret header.
    /// </summary>
    public string? AdminSecret { get; set; }

    /// <summary>
    /// Allow admin API access without a secret.
    /// Default: false (fail closed).
    /// </summary>
    public bool AllowInsecureAdminAccess { get; set; }

    /// <summary>
    /// Log level. Default: Information
    /// </summary>
    public string LogLevel { get; set; } = "Information";

    /// <summary>
    /// Demo mode configuration.
    /// </summary>
    public DemoModeOptions DemoMode { get; set; } = new();
}

/// <summary>
/// Database configuration options.
/// </summary>
public class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Database provider: none, Postgres, SqlServer
    /// </summary>
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.None;

    /// <summary>
    /// Connection string for the database.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Whether to run migrations on startup. Default: true (if DB enabled)
    /// </summary>
    public bool MigrateOnStartup { get; set; } = true;

    /// <summary>
    /// Whether the database is enabled.
    /// </summary>
    public bool IsEnabled => Provider != DatabaseProvider.None && !string.IsNullOrWhiteSpace(ConnectionString);
}

/// <summary>
/// Supported database providers.
/// </summary>
public enum DatabaseProvider
{
    None,
    Postgres,
    SqlServer
}

/// <summary>
/// Well-known paths in the container.
/// </summary>
public static class GatewayPaths
{
    /// <summary>
    /// Configuration files directory.
    /// </summary>
    public static string Config => GetPath("GATEWAY_CONFIG_PATH", "/app/config");

    /// <summary>
    /// Data files directory.
    /// </summary>
    public static string Data => GetPath("GATEWAY_DATA_PATH", "/app/data");

    /// <summary>
    /// Log files directory.
    /// </summary>
    public static string Logs => GetPath("GATEWAY_LOGS_PATH", "/app/logs");

    /// <summary>
    /// Plugin assemblies directory.
    /// </summary>
    public static string Plugins => GetPath("GATEWAY_PLUGINS_PATH", "/app/plugins");

    /// <summary>
    /// YARP configuration file path.
    /// </summary>
    public static string YarpConfig => Environment.GetEnvironmentVariable("YARP_CONFIG_FILE")
                                       ?? Path.Combine(Config, "yarp.json");

    private static string GetPath(string envVar, string defaultPath)
    {
        var envValue = Environment.GetEnvironmentVariable(envVar);
        return !string.IsNullOrWhiteSpace(envValue) ? envValue : defaultPath;
    }

    /// <summary>
    /// Get all logical directories.
    /// </summary>
    public static IReadOnlyDictionary<string, string> All => new Dictionary<string, string>
    {
        ["config"] = Config,
        ["data"] = Data,
        ["logs"] = Logs,
        ["plugins"] = Plugins
    };
}
