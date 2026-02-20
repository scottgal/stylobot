using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Standalone service for Project Honeypot HTTP:BL DNS lookups.
///     Extracted from ProjectHoneypotContributor so it can be shared between
///     the synchronous contributor and the background enrichment service.
///     Maintains a static cache with 30-minute TTL.
/// </summary>
public class ProjectHoneypotLookupService
{
    private static readonly ConcurrentDictionary<string, (HoneypotResult Result, DateTime Expires)> Cache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private const int MaxCacheSize = 10_000;
    private static DateTime _lastCleanup = DateTime.UtcNow;
    private static readonly object CleanupLock = new();

    private readonly ILogger<ProjectHoneypotLookupService> _logger;
    private readonly BotDetectionOptions _options;

    public ProjectHoneypotLookupService(
        ILogger<ProjectHoneypotLookupService> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>
    ///     Whether Project Honeypot lookups are enabled and configured.
    /// </summary>
    public bool IsConfigured =>
        _options.ProjectHoneypot.Enabled &&
        !string.IsNullOrWhiteSpace(_options.ProjectHoneypot.AccessKey);

    /// <summary>
    ///     Look up an IPv4 address against Project Honeypot's HTTP:BL database.
    ///     Results are cached for 30 minutes.
    /// </summary>
    /// <param name="ip">IPv4 address to look up</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Honeypot result, or null if not listed / lookup failed</returns>
    public async Task<HoneypotResult?> LookupIpAsync(string ip, CancellationToken cancellationToken)
    {
        // Check cache first
        if (Cache.TryGetValue(ip, out var cached))
        {
            if (cached.Expires > DateTime.UtcNow)
            {
                CleanupExpiredEntries();
                return cached.Result;
            }

            Cache.TryRemove(ip, out _);
        }

        CleanupExpiredEntries();

        // Build DNS query: [key].[reversed-ip].dnsbl.httpbl.org
        var parts = ip.Split('.');
        Array.Reverse(parts);
        var reversedIp = string.Join(".", parts);
        var query = $"{_options.ProjectHoneypot.AccessKey}.{reversedIp}.dnsbl.httpbl.org";

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(query, cancellationToken);

            if (addresses.Length == 0)
            {
                Cache[ip] = (new HoneypotResult { IsListed = false }, DateTime.UtcNow.Add(CacheDuration));
                return null;
            }

            // Parse the response: 127.[days].[threat].[type]
            var response = addresses[0].GetAddressBytes();

            if (response[0] != 127)
            {
                _logger.LogWarning("Invalid Project Honeypot response for {Ip}: first octet is {Octet}",
                    MaskIp(ip), response[0]);
                return null;
            }

            var result = new HoneypotResult
            {
                IsListed = true,
                DaysSinceLastActivity = response[1],
                ThreatScore = response[2],
                VisitorType = ParseVisitorType(response[3])
            };

            Cache[ip] = (result, DateTime.UtcNow.Add(CacheDuration));
            return result;
        }
        catch (SocketException)
        {
            // NXDOMAIN means IP is not in the database
            Cache[ip] = (new HoneypotResult { IsListed = false }, DateTime.UtcNow.Add(CacheDuration));
            return null;
        }
    }

    internal static void CleanupExpiredEntries()
    {
        var now = DateTime.UtcNow;
        var forceCleanup = Cache.Count > MaxCacheSize;

        if (!forceCleanup && now - _lastCleanup < CleanupInterval)
            return;

        if (!Monitor.TryEnter(CleanupLock))
            return;

        try
        {
            if (!forceCleanup && now - _lastCleanup < CleanupInterval)
                return;

            foreach (var kvp in Cache)
            {
                if (kvp.Value.Expires <= now)
                    Cache.TryRemove(kvp.Key, out _);
            }

            _lastCleanup = now;
        }
        finally
        {
            Monitor.Exit(CleanupLock);
        }
    }

    private static HoneypotVisitorType ParseVisitorType(byte typeByte)
    {
        if (typeByte == 0)
            return HoneypotVisitorType.SearchEngine;

        var type = HoneypotVisitorType.None;

        if ((typeByte & 1) != 0)
            type |= HoneypotVisitorType.Suspicious;
        if ((typeByte & 2) != 0)
            type |= HoneypotVisitorType.Harvester;
        if ((typeByte & 4) != 0)
            type |= HoneypotVisitorType.CommentSpammer;

        return type;
    }

    internal static string MaskIp(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length == 4)
            return $"{parts[0]}.{parts[1]}.{parts[2]}.xxx";
        return ip.Length > 10 ? ip[..10] + "..." : ip;
    }
}
