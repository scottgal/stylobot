using Microsoft.Extensions.Options;
using Stylobot.Gateway.Configuration;

namespace Stylobot.Gateway.Services;

/// <summary>
/// Service for managing gateway configuration.
/// </summary>
public class ConfigurationService
{
    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly IOptions<DatabaseOptions> _dbOptions;
    private readonly ILogger<ConfigurationService> _logger;

    public ConfigurationService(
        IOptions<GatewayOptions> gatewayOptions,
        IOptions<DatabaseOptions> dbOptions,
        ILogger<ConfigurationService> logger)
    {
        _gatewayOptions = gatewayOptions;
        _dbOptions = dbOptions;
        _logger = logger;
    }

    /// <summary>
    /// Get the current gateway options.
    /// </summary>
    public GatewayOptions GatewayOptions => _gatewayOptions.Value;

    /// <summary>
    /// Get the current database options.
    /// </summary>
    public DatabaseOptions DatabaseOptions => _dbOptions.Value;

    /// <summary>
    /// Check if the gateway is configured for production use.
    /// </summary>
    public bool IsProductionReady()
    {
        var issues = GetConfigurationIssues().ToList();
        return !issues.Any(i => i.Severity == ConfigIssueSeverity.Error);
    }

    /// <summary>
    /// Get configuration issues and warnings.
    /// </summary>
    public IEnumerable<ConfigurationIssue> GetConfigurationIssues()
    {
        // Check admin secret
        if (string.IsNullOrEmpty(_gatewayOptions.Value.AdminSecret))
        {
            yield return new ConfigurationIssue
            {
                Key = "AdminSecret",
                Message = "No admin secret configured. Admin API is unprotected.",
                Severity = ConfigIssueSeverity.Warning
            };
        }

        // Check database
        if (_dbOptions.Value.Provider != DatabaseProvider.None &&
            string.IsNullOrEmpty(_dbOptions.Value.ConnectionString))
        {
            yield return new ConfigurationIssue
            {
                Key = "DatabaseConnectionString",
                Message = $"Database provider {_dbOptions.Value.Provider} selected but no connection string provided.",
                Severity = ConfigIssueSeverity.Error
            };
        }

        // Check paths
        foreach (var (name, path) in GatewayPaths.All)
        {
            if (!Directory.Exists(path))
            {
                yield return new ConfigurationIssue
                {
                    Key = $"Path:{name}",
                    Message = $"Directory '{path}' does not exist.",
                    Severity = ConfigIssueSeverity.Warning
                };
            }
        }

        // Check YARP config
        if (!File.Exists(GatewayPaths.YarpConfig) &&
            string.IsNullOrEmpty(_gatewayOptions.Value.DefaultUpstream))
        {
            yield return new ConfigurationIssue
            {
                Key = "YarpConfig",
                Message = "No YARP configuration found and no DEFAULT_UPSTREAM set. Gateway will return 503.",
                Severity = ConfigIssueSeverity.Warning
            };
        }
    }

    /// <summary>
    /// Log configuration summary on startup.
    /// </summary>
    public void LogStartupConfiguration()
    {
        _logger.LogInformation("Gateway Configuration:");
        _logger.LogInformation("  HTTP Port: {Port}", _gatewayOptions.Value.HttpPort);
        _logger.LogInformation("  Admin Base: {AdminBase}", _gatewayOptions.Value.AdminBasePath);
        _logger.LogInformation("  Admin Protected: {Protected}", !string.IsNullOrEmpty(_gatewayOptions.Value.AdminSecret));
        _logger.LogInformation("  Default Upstream: {Upstream}", _gatewayOptions.Value.DefaultUpstream ?? "(none)");
        _logger.LogInformation("  Database: {Provider}", _dbOptions.Value.Provider);

        foreach (var issue in GetConfigurationIssues())
        {
            switch (issue.Severity)
            {
                case ConfigIssueSeverity.Warning:
                    _logger.LogWarning("Config Warning [{Key}]: {Message}", issue.Key, issue.Message);
                    break;
                case ConfigIssueSeverity.Error:
                    _logger.LogError("Config Error [{Key}]: {Message}", issue.Key, issue.Message);
                    break;
            }
        }
    }
}

/// <summary>
/// Configuration issue.
/// </summary>
public class ConfigurationIssue
{
    public required string Key { get; init; }
    public required string Message { get; init; }
    public ConfigIssueSeverity Severity { get; init; }
}

/// <summary>
/// Configuration issue severity.
/// </summary>
public enum ConfigIssueSeverity
{
    Info,
    Warning,
    Error
}
