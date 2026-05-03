namespace Mostlylucid.BotDetection.Licensing;

/// <summary>
///     Shared-tenancy hostnames that many customers host apps under. A StyloBot license
///     with one of these as its PRIMARY domain would functionally cover thousands of
///     unrelated customer deployments, which isn't how we price the product. The portal's
///     domain picker rejects these as primary; the runtime validator still accepts them
///     as ADDITIONAL hosts in the license (e.g., a customer running on acme.com with a
///     staging deploy at acme-stg.azurewebsites.net).
///
///     List drawn from the public suffix list's PRIVATE section
///     (https://publicsuffix.org/list/effective_tld_names.dat) - specifically the entries
///     marked as multi-tenant platform hosting. Refresh quarterly.
///
///     A match is a suffix check: <c>IsCloudPoolHost("acme.azurewebsites.net")</c> = true
///     because it ends with <c>.azurewebsites.net</c>. The leaf subdomain doesn't matter.
/// </summary>
public static class CloudPoolHosts
{
    private static readonly HashSet<string> Suffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Microsoft
        "azurewebsites.net",
        "azureedge.net",
        "cloudapp.azure.com",
        "trafficmanager.net",
        "azurestaticapps.net",

        // Amazon
        "amazonaws.com",
        "elasticbeanstalk.com",
        "cloudfront.net",
        "elb.amazonaws.com",
        "awsapprunner.com",
        "lambda-url.on.aws",

        // Google
        "appspot.com",
        "run.app",
        "cloudfunctions.net",
        "firebaseapp.com",
        "web.app",
        "pages.dev",           // also Cloudflare - safe to list both times; HashSet de-dupes

        // Cloudflare
        "workers.dev",

        // Heroku / Salesforce
        "herokuapp.com",
        "herokudns.com",
        "force.com",

        // Vercel / Netlify / Fly / Render / Railway / etc.
        "vercel.app",
        "vercel.dev",
        "netlify.app",
        "netlify.com",
        "fly.dev",
        "railway.app",
        "up.railway.app",
        "render.com",
        "onrender.com",

        // GitHub / GitLab Pages
        "github.io",
        "gitlab.io",

        // Static hosting / platform-as-a-service
        "pythonanywhere.com",
        "repl.co",
        "glitch.me",
        "gitpod.io",
    };

    /// <summary>
    ///     True if <paramref name="host"/> ends with a known multi-tenant platform suffix.
    ///     Host should be already lowercased and stripped of port; this method defends
    ///     against untidy input anyway.
    /// </summary>
    public static bool IsCloudPoolHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = NormalizeHost(host);
        foreach (var suffix in Suffixes)
        {
            if (h.Length <= suffix.Length) continue;
            if (h.EndsWith("." + suffix, StringComparison.Ordinal)) return true;
            if (string.Equals(h, suffix, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Normalize to lowercase and strip any port (:8080).</summary>
    public static string NormalizeHost(string host)
    {
        var h = host.Trim().ToLowerInvariant();
        var colon = h.IndexOf(':');
        if (colon > 0) h = h[..colon];
        return h;
    }

    /// <summary>Read-only snapshot of the suffix set (for admin UI / docs).</summary>
    public static IReadOnlyCollection<string> AllSuffixes => Suffixes;
}