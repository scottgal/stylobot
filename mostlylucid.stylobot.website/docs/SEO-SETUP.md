# SEO & Social Media Card Setup

This document explains the SEO features implemented in the Stylobot website and how to configure social media cards.

## Features Implemented

### 1. Meta Tags
- **Basic SEO**: Title, description, keywords, author, canonical URLs
- **Open Graph (Facebook)**: Full OG tag support for rich social media previews
- **Twitter Cards**: Summary large image cards with proper metadata
- **Additional**: Robots directives, language tags, theme colors

### 2. Structured Data (JSON-LD)
- Organization schema
- WebSite schema
- SoftwareApplication schema

### 3. Sitemap & Robots
- **Sitemap.xml**: Automatically generated at `/sitemap.xml`
- **Robots.txt**: Automatically generated at `/robots.txt`

## Social Media Card Configuration

### Current Setup
The site is configured to use `/img/card.png` for social media cards. This image will appear when your site is shared on:
- Facebook
- Twitter/X
- LinkedIn
- Slack
- Discord
- WhatsApp
- And other social platforms

### Recommended Image Specifications

#### For Open Graph (Facebook, LinkedIn, etc.)
- **Optimal size**: 1200x630 pixels
- **Minimum size**: 600x315 pixels
- **Aspect ratio**: 1.91:1
- **File format**: PNG or JPG
- **Max file size**: 8MB

#### For Twitter Cards
- **Optimal size**: 1200x628 pixels
- **Minimum size**: 300x157 pixels
- **Aspect ratio**: 1.91:1
- **File format**: PNG, JPG, or WebP
- **Max file size**: 5MB

### Your Social Media Card Image

✅ **Already configured!** Your custom social media card is set up at `/wwwroot/img/card.png`.

This image is now used across all social media platforms. The card should be:
- **Size**: 1200x630px (optimal for all platforms)
- **Format**: PNG (currently used)
- **Content**: Your logo + tagline + branding

#### To Update the Card Image in the Future

1. Create/edit a 1200x630px image with:
   - Your logo
   - Tagline: "Zero-PII Bot Detection & Analytics"
   - Brand colors and styling
   - Optional: Icons representing key features

2. Save it as `/wwwroot/img/card.png` (replace existing)

3. No code changes needed - it's already wired up!

#### Using a Different Image

If you want to use a different filename, update `Services/SeoService.cs`:
```csharp
OgImage = $"{_baseUrl}/img/your-new-card.png",
TwitterImage = $"{_baseUrl}/img/your-new-card.png"
```

### Testing Your Social Media Cards

#### Facebook
1. Visit [Facebook Sharing Debugger](https://developers.facebook.com/tools/debug/)
2. Enter your URL (e.g., `https://stylobot.net`)
3. Click "Debug" to see how your card looks
4. Click "Scrape Again" if you make changes

#### Twitter/X
1. Visit [Twitter Card Validator](https://cards-dev.twitter.com/validator)
2. Enter your URL
3. Preview how your card appears

#### LinkedIn
1. Visit [LinkedIn Post Inspector](https://www.linkedin.com/post-inspector/)
2. Enter your URL
3. Preview and refresh cache if needed

#### General Testing
Use [Open Graph Check](https://opengraphcheck.com/) to test multiple platforms at once.

## Current Configuration

### Base URL
Configured in `appsettings.json`:
```json
"SiteSettings": {
  "BaseUrl": "https://stylobot.net"
}
```

### Page-Specific SEO
Each page has customized metadata:

- **Homepage**: Full organization schema, primary keywords
- **Features**: Highlights bot detection features
- **Detectors**: Focuses on detection methods
- **Enterprise**: Enterprise-focused description
- **Contact**: Set to `noindex` (common practice)

## Customizing SEO per Page

In any controller action, you can customize SEO:

```csharp
public IActionResult MyPage()
{
    var seoMetadata = _seoService.GetDefaultMetadata();
    seoMetadata.Title = "Custom Page Title";
    seoMetadata.Description = "Custom description";
    seoMetadata.OgImage = $"{_baseUrl}/img/custom-card.png";

    ViewData["SeoMetadata"] = seoMetadata;
    return View();
}
```

## File Locations

- **SEO Model**: `Models/SeoMetadata.cs`
- **SEO Service**: `Services/SeoService.cs`
- **Meta Tags Partial**: `Views/Shared/_SeoMetaTags.cshtml`
- **Layout**: `Views/Shared/_Layout.cshtml`
- **Sitemap Controller**: `Controllers/SitemapController.cs`
- **Configuration**: `appsettings.json`

## URLs Available

- Main site: `https://stylobot.net`
- Sitemap: `https://stylobot.net/sitemap.xml`
- Robots: `https://stylobot.net/robots.txt`

## Notes

- All images should use absolute URLs for social media (already handled automatically)
- The site supports both light and dark theme - consider this when designing cards
- Cache is enabled for sitemap (1 hour) and robots.txt (24 hours)
- OpenGraph images are cached by social platforms - use debugger tools to refresh
