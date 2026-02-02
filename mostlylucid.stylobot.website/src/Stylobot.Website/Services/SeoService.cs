using Stylobot.Website.Models;
using System.Text.Json;

namespace Stylobot.Website.Services;

/// <summary>
/// Service for generating SEO metadata and structured data
/// </summary>
public class SeoService
{
    private readonly IConfiguration _configuration;
    private readonly string _baseUrl;

    public SeoService(IConfiguration configuration)
    {
        _configuration = configuration;
        _baseUrl = _configuration["SiteSettings:BaseUrl"] ?? "https://stylobot.net";
    }

    /// <summary>
    /// Gets default SEO metadata
    /// </summary>
    public SeoMetadata GetDefaultMetadata()
    {
        return new SeoMetadata
        {
            Title = "Stylobot - Zero-PII Bot Detection & Analytics",
            Description = "Advanced bot detection using heuristics and analytics with a zero-PII approach. Protect your platform without compromising user privacy.",
            Keywords = "bot detection, zero-PII, privacy-first, web analytics, heuristics, security, anti-bot, web protection",
            OgUrl = _baseUrl,
            Canonical = _baseUrl,
            OgImage = $"{_baseUrl}/img/card.png",
            TwitterImage = $"{_baseUrl}/img/card.png"
        };
    }

    /// <summary>
    /// Gets SEO metadata for the homepage
    /// </summary>
    public SeoMetadata GetHomeMetadata()
    {
        var metadata = GetDefaultMetadata();
        metadata.JsonLd = GenerateOrganizationJsonLd();
        return metadata;
    }

    /// <summary>
    /// Gets SEO metadata for the Features page
    /// </summary>
    public SeoMetadata GetFeaturesMetadata()
    {
        var metadata = GetDefaultMetadata();
        metadata.Title = "Features - Stylobot Bot Detection";
        metadata.Description = "Explore Stylobot's comprehensive bot detection features: zero-PII tracking, heuristic analysis, real-time monitoring, and privacy-first analytics.";
        metadata.OgUrl = $"{_baseUrl}/Home/Features";
        metadata.Canonical = $"{_baseUrl}/Home/Features";
        return metadata;
    }

    /// <summary>
    /// Gets SEO metadata for the Detectors page
    /// </summary>
    public SeoMetadata GetDetectorsMetadata()
    {
        var metadata = GetDefaultMetadata();
        metadata.Title = "Bot Detectors - Stylobot";
        metadata.Description = "Learn about Stylobot's advanced detection methods: behavioral analysis, fingerprinting, rate limiting, and machine learning-powered bot identification.";
        metadata.OgUrl = $"{_baseUrl}/Home/Detectors";
        metadata.Canonical = $"{_baseUrl}/Home/Detectors";
        return metadata;
    }

    /// <summary>
    /// Gets SEO metadata for the Enterprise page
    /// </summary>
    public SeoMetadata GetEnterpriseMetadata()
    {
        var metadata = GetDefaultMetadata();
        metadata.Title = "Enterprise Solutions - Stylobot";
        metadata.Description = "Stylobot enterprise bot detection: scalable solutions, dedicated support, custom integration, and advanced analytics for large-scale platforms.";
        metadata.OgUrl = $"{_baseUrl}/Home/Enterprise";
        metadata.Canonical = $"{_baseUrl}/Home/Enterprise";
        return metadata;
    }

    /// <summary>
    /// Gets SEO metadata for the Live Demo page
    /// </summary>
    public SeoMetadata GetLiveDemoMetadata()
    {
        var metadata = GetDefaultMetadata();
        metadata.Title = "Live Demo - See Your Detection in Real-Time | Stylobot";
        metadata.Description = "Experience Stylobot's bot detection live. See your own detection results in real-time: probability scores, confidence levels, risk bands, and detector breakdowns.";
        metadata.OgUrl = $"{_baseUrl}/Home/LiveDemo";
        metadata.Canonical = $"{_baseUrl}/Home/LiveDemo";
        metadata.JsonLd = GenerateSoftwareApplicationJsonLd();
        return metadata;
    }

    /// <summary>
    /// Gets SEO metadata for the Contact page
    /// </summary>
    public SeoMetadata GetContactMetadata()
    {
        var metadata = GetDefaultMetadata();
        metadata.Title = "Contact Us - Stylobot";
        metadata.Description = "Get in touch with the Stylobot team for questions, support, or enterprise inquiries. We're here to help protect your platform.";
        metadata.OgUrl = $"{_baseUrl}/Home/Contact";
        metadata.Canonical = $"{_baseUrl}/Home/Contact";
        metadata.Robots = "noindex, follow"; // Usually don't want contact pages indexed
        return metadata;
    }

    /// <summary>
    /// Generates Organization JSON-LD structured data
    /// </summary>
    public string GenerateOrganizationJsonLd()
    {
        var organization = new
        {
            context = "https://schema.org",
            type = "Organization",
            name = "Stylobot",
            url = _baseUrl,
            logo = $"{_baseUrl}/img/stylowall.svg",
            image = $"{_baseUrl}/img/card.png",
            description = "Advanced bot detection using heuristics and analytics with a zero-PII approach",
            sameAs = new[]
            {
                "https://github.com/scottgal/mostlylucid.nugetpackages",
                "https://www.mostlylucid.net"
            },
            contactPoint = new
            {
                type = "ContactPoint",
                contactType = "Customer Service",
                url = $"{_baseUrl}/Home/Contact"
            }
        };

        return JsonSerializer.Serialize(organization, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    /// <summary>
    /// Generates WebSite JSON-LD structured data
    /// </summary>
    public string GenerateWebSiteJsonLd()
    {
        var website = new
        {
            context = "https://schema.org",
            type = "WebSite",
            name = "Stylobot",
            url = _baseUrl,
            description = "Advanced bot detection using heuristics and analytics with a zero-PII approach",
            potentialAction = new
            {
                type = "SearchAction",
                target = new
                {
                    type = "EntryPoint",
                    urlTemplate = $"{_baseUrl}/search?q={{search_term_string}}"
                },
                queryInput = "required name=search_term_string"
            }
        };

        return JsonSerializer.Serialize(website, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    /// <summary>
    /// Generates SoftwareApplication JSON-LD structured data
    /// </summary>
    public string GenerateSoftwareApplicationJsonLd()
    {
        var software = new
        {
            context = "https://schema.org",
            type = "SoftwareApplication",
            name = "Stylobot",
            applicationCategory = "SecurityApplication",
            operatingSystem = "Web-based",
            description = "Zero-PII bot detection and analytics platform",
            url = _baseUrl,
            offers = new
            {
                type = "Offer",
                category = "Enterprise Software"
            }
        };

        return JsonSerializer.Serialize(software, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }
}
