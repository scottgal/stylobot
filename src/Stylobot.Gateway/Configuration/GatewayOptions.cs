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
    /// TLS/HTTPS configuration. Disabled by default (expects termination upstream).
    /// </summary>
    public TlsOptions Tls { get; set; } = new();
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
/// TLS/HTTPS termination options.
///
/// Three modes (mutually exclusive, in priority order):
///   1. ACME auto-cert  - set Domain (+ AcmeEmail); certificates managed automatically via Let's Encrypt.
///   2. Cert-from-file  - set CertPath; load a PFX or PEM cert at startup.
///   3. Disabled        - default; expect TLS termination upstream (Caddy, nginx, Cloudflare).
///
/// In modes 1 and 2 the gateway terminates TLS itself, enabling JA3/JA4 fingerprinting.
/// </summary>
public class TlsOptions
{
    public bool Enabled => !string.IsNullOrWhiteSpace(CertPath) || IsAcme;
    public bool IsAcme => !string.IsNullOrWhiteSpace(Domain);

    /// <summary>HTTPS listener port. Default 8443 (non-root-safe in Docker).</summary>
    public int Port { get; set; } = 8443;

    // --- Cert-from-file (PFX or PEM pair) ---
    /// <summary>Path to PFX file or PEM certificate. Required for cert-from-file mode.</summary>
    public string? CertPath { get; set; }
    /// <summary>Path to PEM private key. Only needed for PEM pairs (not PFX).</summary>
    public string? CertKeyPath { get; set; }
    /// <summary>Password for PFX certificate. Leave blank for PEM pairs.</summary>
    public string? CertPassword { get; set; }

    // --- ACME auto-cert (LettuceEncrypt / Let's Encrypt) ---
    /// <summary>Public domain name. Setting this enables ACME auto-cert mode.</summary>
    public string? Domain { get; set; }
    /// <summary>Contact email for ACME registration. Recommended for expiry notifications.</summary>
    public string? AcmeEmail { get; set; }
    /// <summary>Directory to persist ACME account keys and certificates.</summary>
    public string AcmeCertStorePath { get; set; } = "/app/data/certs";
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
