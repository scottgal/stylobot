using System.Collections.Frozen;
using System.Reflection;

namespace Mostlylucid.BotDetection.Domains;

/// <summary>
/// Minimal Public Suffix List (https://publicsuffix.org) implementation. Loads the
/// list once at construction, then answers PSL queries in O(number of host labels).
/// </summary>
public sealed class PublicSuffixList
{
    private readonly FrozenSet<string> _rules;
    private readonly FrozenSet<string> _exceptions;

    private PublicSuffixList(FrozenSet<string> rules, FrozenSet<string> exceptions)
    {
        _rules = rules;
        _exceptions = exceptions;
    }

    /// <summary>Load the PSL from an embedded resource in the calling assembly.</summary>
    public static PublicSuffixList LoadEmbedded()
    {
        using var stream = typeof(PublicSuffixList).Assembly
            .GetManifestResourceStream("Mostlylucid.BotDetection.Domains.PublicSuffixList.dat")
            ?? throw new InvalidOperationException(
                "PublicSuffixList.dat not embedded. Check .csproj EmbeddedResource entry.");
        return LoadFrom(stream);
    }

    /// <summary>Load the PSL from any stream.</summary>
    public static PublicSuffixList LoadFrom(Stream stream)
    {
        var rules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exceptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("//")) continue;

            if (trimmed.StartsWith('!'))
                exceptions.Add(trimmed[1..].ToLowerInvariant());
            else
                rules.Add(trimmed.ToLowerInvariant());
        }

        return new PublicSuffixList(rules.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
                                    exceptions.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Get the public suffix ("com", "co.uk", "azurewebsites.net") for a host.
    /// Returns the input's last label if no suffix matches.
    /// </summary>
    public string GetPublicSuffix(string host)
    {
        if (string.IsNullOrEmpty(host)) return "";
        var labels = host.ToLowerInvariant().Split('.');

        // Exception rules take precedence.
        for (var i = 0; i < labels.Length; i++)
        {
            var candidate = string.Join('.', labels[i..]);
            if (_exceptions.Contains(candidate))
                return string.Join('.', labels[(i + 1)..]);
        }

        // Longest matching rule wins.
        for (var i = 0; i < labels.Length; i++)
        {
            var candidate = string.Join('.', labels[i..]);
            if (_rules.Contains(candidate))
                return candidate;

            // Wildcard rule: *.<parent> matches anything one label deeper.
            if (i + 1 < labels.Length)
            {
                var wildcardParent = "*." + string.Join('.', labels[(i + 1)..]);
                if (_rules.Contains(wildcardParent))
                    return candidate;
            }
        }

        return labels[^1];
    }

    /// <summary>
    /// Get the registrable domain (eTLD+1) for a host. "www.acme.co.uk" returns "acme.co.uk".
    /// Returns the input if the host is shorter than or equal to its public suffix.
    /// </summary>
    public string GetRegistrableDomain(string host)
    {
        if (string.IsNullOrEmpty(host)) return "";
        var suffix = GetPublicSuffix(host);
        if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase)) return host;

        var suffixLabels = suffix.Split('.').Length;
        var labels = host.ToLowerInvariant().Split('.');
        if (labels.Length <= suffixLabels) return host;

        return string.Join('.', labels[(labels.Length - suffixLabels - 1)..]);
    }
}