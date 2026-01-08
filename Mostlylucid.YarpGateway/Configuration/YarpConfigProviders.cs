using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;

namespace Mostlylucid.YarpGateway.Configuration;

/// <summary>
/// Config provider for when DEFAULT_UPSTREAM is set.
/// Creates a single catch-all route to the upstream.
/// </summary>
public class DefaultUpstreamConfigProvider : IProxyConfigProvider
{
    private readonly string _upstreamUrl;
    private volatile DefaultUpstreamConfig _config;

    public DefaultUpstreamConfigProvider(string upstreamUrl)
    {
        _upstreamUrl = upstreamUrl;
        _config = new DefaultUpstreamConfig(upstreamUrl);
    }

    public IProxyConfig GetConfig() => _config;

    private class DefaultUpstreamConfig : IProxyConfig
    {
        public DefaultUpstreamConfig(string upstreamUrl)
        {
            Routes = new[]
            {
                new RouteConfig
                {
                    RouteId = "default-catch-all",
                    ClusterId = "default-upstream",
                    Match = new RouteMatch
                    {
                        Path = "/{**catch-all}"
                    }
                }
            };

            Clusters = new[]
            {
                new ClusterConfig
                {
                    ClusterId = "default-upstream",
                    Destinations = new Dictionary<string, DestinationConfig>
                    {
                        ["primary"] = new DestinationConfig
                        {
                            Address = upstreamUrl
                        }
                    }
                }
            };
        }

        public IReadOnlyList<RouteConfig> Routes { get; }
        public IReadOnlyList<ClusterConfig> Clusters { get; }

        public IChangeToken ChangeToken => new CancellationChangeToken(CancellationToken.None);
    }
}

/// <summary>
/// Empty config provider for when no routes are configured.
/// </summary>
public class EmptyConfigProvider : IProxyConfigProvider
{
    private readonly EmptyConfig _config = new();

    public IProxyConfig GetConfig() => _config;

    private class EmptyConfig : IProxyConfig
    {
        public IReadOnlyList<RouteConfig> Routes { get; } = Array.Empty<RouteConfig>();
        public IReadOnlyList<ClusterConfig> Clusters { get; } = Array.Empty<ClusterConfig>();
        public IChangeToken ChangeToken => new CancellationChangeToken(CancellationToken.None);
    }
}
