namespace Mostlylucid.Common.Telemetry;

/// <summary>
///     Standard attribute names for telemetry following OpenTelemetry semantic conventions
/// </summary>
public static class TelemetryConstants
{
    // Common attributes
    public const string ServiceName = "service.name";
    public const string ServiceVersion = "service.version";

    // HTTP attributes
    public const string HttpMethod = "http.method";
    public const string HttpUrl = "http.url";
    public const string HttpStatusCode = "http.status_code";
    public const string HttpUserAgent = "http.user_agent";
    public const string HttpClientIp = "http.client_ip";

    // Custom Mostlylucid attributes
    public const string MostlylucidPrefix = "mostlylucid.";

    // Bot Detection
    public const string BotDetectionIsBot = "mostlylucid.botdetection.is_bot";
    public const string BotDetectionConfidence = "mostlylucid.botdetection.confidence";
    public const string BotDetectionBotType = "mostlylucid.botdetection.bot_type";
    public const string BotDetectionMethod = "mostlylucid.botdetection.method";
    public const string BotDetectionProcessingTimeMs = "mostlylucid.botdetection.processing_time_ms";

    // Geo Detection
    public const string GeoDetectionCountryCode = "mostlylucid.geodetection.country_code";
    public const string GeoDetectionCountryName = "mostlylucid.geodetection.country_name";
    public const string GeoDetectionCity = "mostlylucid.geodetection.city";
    public const string GeoDetectionCacheHit = "mostlylucid.geodetection.cache_hit";

    // LLM Common
    public const string LlmModel = "mostlylucid.llm.model";
    public const string LlmProvider = "mostlylucid.llm.provider";
    public const string LlmTokensInput = "mostlylucid.llm.tokens_input";
    public const string LlmTokensOutput = "mostlylucid.llm.tokens_output";
    public const string LlmDurationMs = "mostlylucid.llm.duration_ms";

    // Alt Text
    public const string AltTextImageSize = "mostlylucid.alttext.image_size_bytes";
    public const string AltTextImageFormat = "mostlylucid.alttext.image_format";
    public const string AltTextCacheHit = "mostlylucid.alttext.cache_hit";

    // Content Moderation
    public const string ContentModerationIsFlagged = "mostlylucid.moderation.is_flagged";
    public const string ContentModerationCategories = "mostlylucid.moderation.categories";
    public const string ContentModerationContentLength = "mostlylucid.moderation.content_length";

    // PII Redaction
    public const string PiiRedactionTokensRedacted = "mostlylucid.pii.tokens_redacted";
    public const string PiiRedactionTypesFound = "mostlylucid.pii.types_found";

    // Accessibility Auditor
    public const string AccessibilityIssueCount = "mostlylucid.accessibility.issue_count";
    public const string AccessibilitySeverity = "mostlylucid.accessibility.max_severity";

    // SEO Metadata
    public const string SeoMetadataType = "mostlylucid.seo.metadata_type";
    public const string SeoCacheHit = "mostlylucid.seo.cache_hit";

    // Log Summarizer
    public const string LogSummarizerLogCount = "mostlylucid.logsummarizer.log_count";
    public const string LogSummarizerTimeRangeHours = "mostlylucid.logsummarizer.time_range_hours";

    // RAG Search
    public const string RagSearchProvider = "mostlylucid.rag.search_provider";
    public const string RagSearchResultCount = "mostlylucid.rag.result_count";
    public const string RagSearchQuery = "mostlylucid.rag.query";

    // I18n Assistant
    public const string I18nSourceLanguage = "mostlylucid.i18n.source_language";
    public const string I18nTargetLanguage = "mostlylucid.i18n.target_language";
    public const string I18nKeyCount = "mostlylucid.i18n.key_count";

    // Slide Translator
    public const string SlideTranslatorChunkCount = "mostlylucid.slidetranslator.chunk_count";
    public const string SlideTranslatorSourceLanguage = "mostlylucid.slidetranslator.source_language";
    public const string SlideTranslatorTargetLanguage = "mostlylucid.slidetranslator.target_language";

    // Cache operations
    public const string CacheOperation = "mostlylucid.cache.operation";
    public const string CacheHit = "mostlylucid.cache.hit";
    public const string CacheKey = "mostlylucid.cache.key";

    // Error tracking
    public const string ErrorType = "error.type";
    public const string ErrorMessage = "error.message";
}