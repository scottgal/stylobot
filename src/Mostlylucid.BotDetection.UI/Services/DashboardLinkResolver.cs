using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Resolves a dashboard-relative subpath (e.g. <c>/policies</c>,
///     <c>/aspnet-hub</c>, <c>/signature/{id}</c>) into a host-absolute URL
///     prefixed by the configured
///     <see cref="StyloBotDashboardOptions.BasePath" /> (or
///     <see cref="StyloBotDashboardOptions.NavBasePath" /> when set).
///     <para>
///         Existed because health-summary providers, page builders, and a
///         handful of dashboard cshtml partials previously hard-coded
///         <c>/dashboard/...</c> URLs into rendered links. Anywhere the
///         middleware was mounted at a non-default <c>BasePath</c>
///         (the Trailblazor / ASP.NET demo mounts it at <c>/_stylobot</c>,
///         per the AGPL FOSS package's option) those hard-coded links
///         404'd. Per <c>feedback_all_settings_configurable</c> +
///         <c>feedback_no_word_lists</c> the prefix lives on
///         <see cref="StyloBotDashboardOptions" /> already; this resolver
///         is the single chokepoint that reads it.
///     </para>
///     <para>
///         Subpaths are normalised: a leading <c>/</c> is optional, a
///         trailing <c>/</c> is preserved. Passing an empty string returns
///         the bare nav base path (useful for "back to dashboard" links).
///     </para>
/// </summary>
public interface IDashboardLinkResolver
{
    /// <summary>
    ///     Resolved nav base path, trimmed of any trailing slash. Equal to
    ///     <see cref="StyloBotDashboardOptions.NavBasePath" /> when set,
    ///     else <see cref="StyloBotDashboardOptions.BasePath" />.
    /// </summary>
    string NavBasePath { get; }

    /// <summary>
    ///     Resolve <paramref name="subpath" /> against
    ///     <see cref="NavBasePath" />. A leading slash on the input is
    ///     optional; the resolver always emits exactly one between the
    ///     base path and the subpath.
    /// </summary>
    string Resolve(string subpath);
}

/// <inheritdoc cref="IDashboardLinkResolver" />
public sealed class DashboardLinkResolver : IDashboardLinkResolver
{
    private readonly string _navBasePath;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public DashboardLinkResolver(IOptions<StyloBotDashboardOptions> options)
    {
        var opts = options.Value;
        var nav = string.IsNullOrEmpty(opts.NavBasePath) ? opts.BasePath : opts.NavBasePath;
        _navBasePath = (nav ?? string.Empty).TrimEnd('/');
    }

    /// <summary>
    ///     Test seam: bypass DI and construct the resolver from a raw
    ///     options value. Production code always uses the IOptions overload.
    /// </summary>
    public DashboardLinkResolver(StyloBotDashboardOptions options)
        : this(Microsoft.Extensions.Options.Options.Create(options))
    {
    }

    /// <inheritdoc />
    public string NavBasePath => _navBasePath;

    /// <inheritdoc />
    public string Resolve(string subpath)
    {
        if (string.IsNullOrEmpty(subpath)) return _navBasePath;
        var s = subpath[0] == '/' ? subpath : "/" + subpath;
        return _navBasePath + s;
    }
}