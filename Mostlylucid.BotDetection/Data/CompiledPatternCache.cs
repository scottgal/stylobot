using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Thread-safe cache for compiled regex patterns and parsed CIDR ranges.
///     Patterns are compiled once on first use for optimal performance.
/// </summary>
public class CompiledPatternCache : ICompiledPatternCache
{
    private readonly ConcurrentDictionary<string, ParsedCidrRange?> _cidrCache = new();
    private readonly ILogger<CompiledPatternCache> _logger;
    private readonly ConcurrentDictionary<string, Regex?> _regexCache = new();
    private volatile IReadOnlyList<ParsedCidrRange> _downloadedCidrRanges = Array.Empty<ParsedCidrRange>();

    // Pre-compiled patterns from downloaded sources (isbot, crawler-user-agents)
    private volatile IReadOnlyList<Regex> _downloadedPatterns = Array.Empty<Regex>();

    public CompiledPatternCache(ILogger<CompiledPatternCache> logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     Gets all compiled patterns from downloaded sources.
    /// </summary>
    public IReadOnlyList<Regex> DownloadedPatterns => _downloadedPatterns;

    /// <summary>
    ///     Gets all parsed CIDR ranges from downloaded sources.
    /// </summary>
    public IReadOnlyList<ParsedCidrRange> DownloadedCidrRanges => _downloadedCidrRanges;

    /// <summary>
    ///     Gets or creates a compiled regex for the given pattern.
    ///     Returns null if the pattern is invalid.
    /// </summary>
    public Regex? GetOrCompileRegex(string pattern)
    {
        return _regexCache.GetOrAdd(pattern, p =>
        {
            try
            {
                return new Regex(p,
                    RegexOptions.IgnoreCase | RegexOptions.Compiled,
                    TimeSpan.FromMilliseconds(100));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to compile regex pattern: {Pattern}", p);
                return null;
            }
        });
    }

    /// <summary>
    ///     Gets or creates a parsed CIDR range.
    ///     Returns null if the CIDR is invalid.
    /// </summary>
    public ParsedCidrRange? GetOrParseCidr(string cidr)
    {
        return _cidrCache.GetOrAdd(cidr, c =>
        {
            try
            {
                return ParsedCidrRange.TryParse(c, out var range) ? range : null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse CIDR: {Cidr}", c);
                return null;
            }
        });
    }

    /// <summary>
    ///     Updates the downloaded patterns cache with new patterns.
    ///     Compiles all patterns in parallel for efficiency.
    /// </summary>
    public void UpdateDownloadedPatterns(IEnumerable<string> patterns)
    {
        var compiled = new List<Regex>();

        foreach (var pattern in patterns)
        {
            var regex = GetOrCompileRegex(pattern);
            if (regex != null) compiled.Add(regex);
        }

        _downloadedPatterns = compiled;
        _logger.LogInformation("Updated downloaded patterns cache with {Count} compiled patterns", compiled.Count);
    }

    /// <summary>
    ///     Updates the downloaded CIDR ranges cache.
    /// </summary>
    public void UpdateDownloadedCidrRanges(IEnumerable<string> cidrs)
    {
        var parsed = new List<ParsedCidrRange>();

        foreach (var cidr in cidrs)
        {
            var range = GetOrParseCidr(cidr);
            if (range != null) parsed.Add(range);
        }

        _downloadedCidrRanges = parsed;
        _logger.LogInformation("Updated downloaded CIDR cache with {Count} parsed ranges", parsed.Count);
    }

    /// <summary>
    ///     Checks if a user agent matches any compiled pattern.
    /// </summary>
    public bool MatchesAnyPattern(string userAgent, out string? matchedPattern)
    {
        matchedPattern = null;

        // First check source-generated patterns (fastest)
        foreach (var regex in BotSignatures.CompiledBotPatterns)
            try
            {
                if (regex.IsMatch(userAgent))
                {
                    matchedPattern = regex.ToString();
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Skip timed out patterns
            }

        // Then check downloaded patterns
        foreach (var regex in _downloadedPatterns)
            try
            {
                if (regex.IsMatch(userAgent))
                {
                    matchedPattern = regex.ToString();
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Skip timed out patterns
            }

        return false;
    }

    /// <summary>
    ///     Checks if an IP address is in any cached CIDR range.
    /// </summary>
    public bool IsInAnyCidrRange(IPAddress ipAddress, out string? matchedRange)
    {
        matchedRange = null;

        foreach (var range in _downloadedCidrRanges)
            if (range.Contains(ipAddress))
            {
                matchedRange = range.OriginalCidr;
                return true;
            }

        return false;
    }

    /// <summary>
    ///     Clears all cached patterns and ranges.
    /// </summary>
    public void Clear()
    {
        _regexCache.Clear();
        _cidrCache.Clear();
        _downloadedPatterns = Array.Empty<Regex>();
        _downloadedCidrRanges = Array.Empty<ParsedCidrRange>();
    }
}

/// <summary>
///     Interface for compiled pattern cache.
/// </summary>
public interface ICompiledPatternCache
{
    IReadOnlyList<Regex> DownloadedPatterns { get; }
    IReadOnlyList<ParsedCidrRange> DownloadedCidrRanges { get; }
    Regex? GetOrCompileRegex(string pattern);
    ParsedCidrRange? GetOrParseCidr(string cidr);
    void UpdateDownloadedPatterns(IEnumerable<string> patterns);
    void UpdateDownloadedCidrRanges(IEnumerable<string> cidrs);
    bool MatchesAnyPattern(string userAgent, out string? matchedPattern);
    bool IsInAnyCidrRange(IPAddress ipAddress, out string? matchedRange);
    void Clear();
}

/// <summary>
///     Pre-parsed CIDR range for fast IP matching.
///     Avoids repeated string parsing on every request.
/// </summary>
public sealed class ParsedCidrRange
{
    private readonly int _fullBytes;
    private readonly byte _lastByteMask;

    // Pre-computed values for fast matching
    private readonly byte[] _networkBytes;
    private readonly int _remainingBits;

    private ParsedCidrRange(string cidr, IPAddress networkAddress, int prefixLength)
    {
        OriginalCidr = cidr;
        NetworkAddress = networkAddress;
        PrefixLength = prefixLength;

        _networkBytes = networkAddress.GetAddressBytes();
        _fullBytes = prefixLength / 8;
        _remainingBits = prefixLength % 8;
        _lastByteMask = _remainingBits > 0 ? (byte)(0xFF << (8 - _remainingBits)) : (byte)0;
    }

    public string OriginalCidr { get; }
    public IPAddress NetworkAddress { get; }
    public int PrefixLength { get; }

    /// <summary>
    ///     Tries to parse a CIDR string into a ParsedCidrRange.
    /// </summary>
    public static bool TryParse(string cidr, out ParsedCidrRange? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(cidr))
            return false;

        var parts = cidr.Split('/');
        if (parts.Length != 2)
            return false;

        if (!IPAddress.TryParse(parts[0], out var networkAddress))
            return false;

        if (!int.TryParse(parts[1], out var prefixLength))
            return false;

        var maxPrefix = networkAddress.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maxPrefix)
            return false;

        result = new ParsedCidrRange(cidr, networkAddress, prefixLength);
        return true;
    }

    /// <summary>
    ///     Checks if an IP address is contained in this CIDR range.
    ///     Optimized to avoid allocations and repeated parsing.
    /// </summary>
    public bool Contains(IPAddress ipAddress)
    {
        var ipBytes = ipAddress.GetAddressBytes();

        // IPv4 and IPv6 must match
        if (ipBytes.Length != _networkBytes.Length)
            return false;

        // Check full bytes
        for (var i = 0; i < _fullBytes; i++)
            if (ipBytes[i] != _networkBytes[i])
                return false;

        // Check remaining bits with pre-computed mask
        if (_remainingBits > 0 && _fullBytes < ipBytes.Length)
            if ((ipBytes[_fullBytes] & _lastByteMask) != (_networkBytes[_fullBytes] & _lastByteMask))
                return false;

        return true;
    }
}