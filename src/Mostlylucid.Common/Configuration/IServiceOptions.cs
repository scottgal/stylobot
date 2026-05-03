namespace Mostlylucid.Common.Configuration;

/// <summary>
///     Base interface for service options
/// </summary>
public interface IServiceOptions
{
    /// <summary>
    ///     Whether the service is enabled
    /// </summary>
    bool Enabled { get; set; }
}

/// <summary>
///     Options for services that support caching
/// </summary>
public interface ICacheableOptions : IServiceOptions
{
    /// <summary>
    ///     How long to cache entries
    /// </summary>
    TimeSpan CacheDuration { get; set; }

    /// <summary>
    ///     Maximum number of cache entries
    /// </summary>
    int MaxCacheEntries { get; set; }
}

/// <summary>
///     Options for services that use a database
/// </summary>
public interface IDatabaseOptions : IServiceOptions
{
    /// <summary>
    ///     Database connection string
    /// </summary>
    string? ConnectionString { get; set; }

    /// <summary>
    ///     Path to the database file (for file-based databases)
    /// </summary>
    string? DatabasePath { get; set; }

    /// <summary>
    ///     Whether to automatically migrate/create the database
    /// </summary>
    bool AutoMigrateDatabase { get; set; }
}

/// <summary>
///     Options for services that support test mode
/// </summary>
public interface ITestableOptions : IServiceOptions
{
    /// <summary>
    ///     Enable test mode (allows simulating different scenarios via headers)
    /// </summary>
    bool EnableTestMode { get; set; }

    /// <summary>
    ///     Header name for test mode override
    /// </summary>
    string TestModeHeader { get; }
}

/// <summary>
///     Options for services that auto-update their data
/// </summary>
public interface IAutoUpdateOptions : IServiceOptions
{
    /// <summary>
    ///     Enable automatic updates
    /// </summary>
    bool EnableAutoUpdate { get; set; }

    /// <summary>
    ///     How often to check for updates
    /// </summary>
    TimeSpan UpdateCheckInterval { get; set; }
}