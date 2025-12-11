namespace Stylobot.Website.Models;

/// <summary>
/// SEO metadata model for pages
/// </summary>
public class SeoMetadata
{
    // Basic SEO
    public string Title { get; set; } = "Stylobot - Zero-PII Bot Detection & Analytics";
    public string Description { get; set; } = "Advanced bot detection using heuristics and analytics with a zero-PII approach. Protect your platform without compromising user privacy.";
    public string Keywords { get; set; } = "bot detection, zero-PII, privacy-first, web analytics, heuristics, security, anti-bot, web protection";
    public string Author { get; set; } = "Stylobot";
    public string Canonical { get; set; } = string.Empty;

    // Open Graph
    public string OgTitle { get; set; } = string.Empty;
    public string OgDescription { get; set; } = string.Empty;
    public string OgImage { get; set; } = "/img/card.png";
    public string OgImageAlt { get; set; } = "Stylobot - Zero-PII Bot Detection & Analytics";
    public string OgUrl { get; set; } = string.Empty;
    public string OgType { get; set; } = "website";
    public string OgSiteName { get; set; } = "Stylobot";
    public int OgImageWidth { get; set; } = 1200;
    public int OgImageHeight { get; set; } = 630;

    // Twitter Card
    public string TwitterCard { get; set; } = "summary_large_image";
    public string TwitterSite { get; set; } = "@stylobot";
    public string TwitterCreator { get; set; } = "@stylobot";
    public string TwitterTitle { get; set; } = string.Empty;
    public string TwitterDescription { get; set; } = string.Empty;
    public string TwitterImage { get; set; } = "/img/card.png";
    public string TwitterImageAlt { get; set; } = "Stylobot - Zero-PII Bot Detection & Analytics";

    // Additional SEO
    public string Robots { get; set; } = "index, follow";
    public string Language { get; set; } = "en";
    public string? ArticlePublishedTime { get; set; }
    public string? ArticleModifiedTime { get; set; }
    public List<string> ArticleTags { get; set; } = new();

    // Structured Data (JSON-LD)
    public string? JsonLd { get; set; }

    /// <summary>
    /// Gets Open Graph title (falls back to Title if not set)
    /// </summary>
    public string GetOgTitle() => string.IsNullOrWhiteSpace(OgTitle) ? Title : OgTitle;

    /// <summary>
    /// Gets Open Graph description (falls back to Description if not set)
    /// </summary>
    public string GetOgDescription() => string.IsNullOrWhiteSpace(OgDescription) ? Description : OgDescription;

    /// <summary>
    /// Gets Twitter title (falls back to OgTitle or Title if not set)
    /// </summary>
    public string GetTwitterTitle() => string.IsNullOrWhiteSpace(TwitterTitle)
        ? GetOgTitle()
        : TwitterTitle;

    /// <summary>
    /// Gets Twitter description (falls back to OgDescription or Description if not set)
    /// </summary>
    public string GetTwitterDescription() => string.IsNullOrWhiteSpace(TwitterDescription)
        ? GetOgDescription()
        : TwitterDescription;
}
