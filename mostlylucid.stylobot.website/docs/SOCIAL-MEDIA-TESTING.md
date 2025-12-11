# Social Media Card Testing Guide

## Your Social Media Card Setup

✅ **Card Image**: `/wwwroot/img/card.png`
✅ **Full URL**: `https://stylobot.net/img/card.png`

This image will automatically appear when anyone shares your site on social media!

## What Gets Shared

### Homepage (https://stylobot.net)
- **Title**: Stylobot - Zero-PII Bot Detection & Analytics
- **Description**: Advanced bot detection using heuristics and analytics with a zero-PII approach. Protect your platform without compromising user privacy.
- **Image**: card.png
- **Type**: Website with Organization schema

### Features Page (https://stylobot.net/Home/Features)
- **Title**: Features - Stylobot Bot Detection
- **Description**: Explore Stylobot's comprehensive bot detection features: zero-PII tracking, heuristic analysis, real-time monitoring, and privacy-first analytics.
- **Image**: card.png

### Detectors Page (https://stylobot.net/Home/Detectors)
- **Title**: Bot Detectors - Stylobot
- **Description**: Learn about Stylobot's advanced detection methods: behavioral analysis, fingerprinting, rate limiting, and machine learning-powered bot identification.
- **Image**: card.png

### Enterprise Page (https://stylobot.net/Home/Enterprise)
- **Title**: Enterprise Solutions - Stylobot
- **Description**: Stylobot enterprise bot detection: scalable solutions, dedicated support, custom integration, and advanced analytics for large-scale platforms.
- **Image**: card.png

## Testing Tools

### 1. Facebook Sharing Debugger
**URL**: https://developers.facebook.com/tools/debug/

Steps:
1. Enter your URL: `https://stylobot.net`
2. Click "Debug"
3. View how your card appears
4. Click "Scrape Again" to refresh the cache

**What to check**:
- Image displays correctly (1200x630px)
- Title and description appear
- No errors or warnings

### 2. Twitter Card Validator
**URL**: https://cards-dev.twitter.com/validator

Steps:
1. Enter your URL: `https://stylobot.net`
2. Click "Preview card"
3. View the large image card

**What to check**:
- Card type: "summary_large_image"
- Image displays correctly
- Title and description truncated properly

### 3. LinkedIn Post Inspector
**URL**: https://www.linkedin.com/post-inspector/

Steps:
1. Enter your URL: `https://stylobot.net`
2. Click "Inspect"
3. View the preview

**What to check**:
- Professional appearance
- Image quality
- Text formatting

### 4. Open Graph Check (Multi-Platform)
**URL**: https://opengraphcheck.com/

Tests multiple platforms at once:
- Facebook
- Twitter
- LinkedIn
- Pinterest
- Slack

## Common Issues & Solutions

### Image Not Appearing

**Problem**: Social platform shows no image or wrong image

**Solutions**:
1. Clear the platform's cache using debugger tools
2. Verify image exists at: `https://stylobot.net/img/card.png`
3. Check image file size (should be under 8MB)
4. Verify image dimensions (1200x630px recommended)
5. Ensure image is publicly accessible (not behind auth)

### Wrong Title/Description

**Problem**: Old or incorrect metadata showing

**Solutions**:
1. Use platform debugger to "scrape again" / "refresh cache"
2. Verify ViewData["SeoMetadata"] is set in controller
3. Check browser cache (hard refresh with Ctrl+Shift+R)

### Card Not Updating

**Problem**: Made changes but still seeing old card

**Solutions**:
1. Social platforms cache aggressively - use debugger tools
2. Wait 24-48 hours for natural cache expiration
3. Change the image filename if urgent
4. Some platforms respect `og:updated_time` meta tag

## Testing Checklist

Before going live, test all pages:

- [ ] Homepage: https://stylobot.net
- [ ] Features: https://stylobot.net/Home/Features
- [ ] Detectors: https://stylobot.net/Home/Detectors
- [ ] Enterprise: https://stylobot.net/Home/Enterprise

For each page, verify:
- [ ] Facebook card looks correct
- [ ] Twitter card looks correct
- [ ] LinkedIn card looks correct
- [ ] Image loads fast (< 1 second)
- [ ] Title is readable and accurate
- [ ] Description is compelling
- [ ] No broken images or errors

## Real-World Testing

After debugger testing, do real-world tests:

### Slack
1. Paste URL in a Slack channel
2. Wait for preview to generate
3. Verify card appears correctly

### Discord
1. Paste URL in a Discord channel
2. Verify embed displays properly
3. Check image quality

### WhatsApp
1. Share URL via WhatsApp
2. Preview should show your card
3. Verify on mobile devices

## Meta Tags Generated

Your pages automatically include:

```html
<!-- Open Graph -->
<meta property="og:type" content="website" />
<meta property="og:url" content="https://stylobot.net" />
<meta property="og:title" content="Stylobot - Zero-PII Bot Detection & Analytics" />
<meta property="og:description" content="..." />
<meta property="og:image" content="https://stylobot.net/img/card.png" />
<meta property="og:image:width" content="1200" />
<meta property="og:image:height" content="630" />

<!-- Twitter -->
<meta name="twitter:card" content="summary_large_image" />
<meta name="twitter:title" content="Stylobot - Zero-PII Bot Detection & Analytics" />
<meta name="twitter:description" content="..." />
<meta name="twitter:image" content="https://stylobot.net/img/card.png" />
```

## Support

If you encounter issues:
1. Check the SEO-SETUP.md documentation
2. Verify image dimensions and file size
3. Use multiple testing tools to isolate issues
4. Test on different pages (homepage vs. subpages)
5. Clear all caches (browser + social platform)

## Next Steps

Once testing is complete:
1. Share your site on your social channels
2. Monitor how the cards appear
3. Gather feedback on card design
4. Iterate on card.png design if needed
5. Consider creating page-specific cards for different sections
