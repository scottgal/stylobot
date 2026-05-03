namespace Mostlylucid.BotDetection.Proxy;

/// <summary>The type of proxy layer detected in front of the application.</summary>
public enum ProxyTopology
{
    /// <summary>No proxy: connection arrives directly from the client.</summary>
    Direct,

    /// <summary>Cloudflare CDN or Cloudflare Tunnel (CF-Connecting-IP / CF-Ray present).</summary>
    Cloudflare,

    /// <summary>AWS CloudFront (CloudFront-Viewer-Address / X-Amz-Cf-Id present).</summary>
    CloudFront,

    /// <summary>Fastly CDN (Fastly-Client-IP present).</summary>
    Fastly,

    /// <summary>Nginx, Caddy, HAProxy, or other proxy emitting X-Real-IP.</summary>
    Nginx,

    /// <summary>Generic proxy emitting X-Forwarded-For only.</summary>
    Generic
}
