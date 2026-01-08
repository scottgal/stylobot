using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Mostlylucid.BotDetection.SignatureStore.Models;

/// <summary>
/// Entity for storing bot detection signatures in Postgres.
/// Includes JSONB column for full signature data with GIN index for fast querying.
/// </summary>
[Table("bot_signatures")]
public class SignatureEntity
{
    /// <summary>
    /// Unique signature ID (primary key)
    /// </summary>
    [Key]
    [Column("signature_id")]
    [MaxLength(128)]
    public required string SignatureId { get; set; }

    /// <summary>
    /// Timestamp when signature was created
    /// </summary>
    [Column("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Bot probability (0.0 to 1.0) - indexed for sorting
    /// </summary>
    [Column("bot_probability")]
    public double BotProbability { get; set; }

    /// <summary>
    /// Detection confidence (0.0 to 1.0) - indexed for sorting
    /// </summary>
    [Column("confidence")]
    public double Confidence { get; set; }

    /// <summary>
    /// Risk band (VeryLow, Low, Elevated, Medium, High, VeryHigh)
    /// </summary>
    [Column("risk_band")]
    [MaxLength(32)]
    public string RiskBand { get; set; } = "Unknown";

    /// <summary>
    /// Request path - indexed for filtering
    /// </summary>
    [Column("request_path")]
    [MaxLength(512)]
    public string? RequestPath { get; set; }

    /// <summary>
    /// Request method (GET, POST, etc.)
    /// </summary>
    [Column("request_method")]
    [MaxLength(16)]
    public string? RequestMethod { get; set; }

    /// <summary>
    /// REMOVED: Remote IP address - NEVER store raw PII!
    /// Use IpSignature (HMAC hash) instead.
    /// </summary>
    [Obsolete("DO NOT USE - Raw IP is PII and must never be stored. Use multi-factor signatures instead.")]
    [Column("remote_ip")]
    [MaxLength(64)]
    public string? RemoteIp { get; set; }

    /// <summary>
    /// REMOVED: User-Agent string - NEVER store raw PII!
    /// Use UaSignature (HMAC hash) instead.
    /// </summary>
    [Obsolete("DO NOT USE - Raw UA is PII and must never be stored. Use multi-factor signatures instead.")]
    [Column("user_agent")]
    [MaxLength(1024)]
    public string? UserAgent { get; set; }

    /// <summary>
    /// Multi-factor signatures (JSONB) - privacy-safe HMAC hashes
    /// Format: { "primary": "hash", "ip": "hash", "ua": "hash", "clientSide": "hash", "plugin": "hash" }
    /// </summary>
    [Column("signatures", TypeName = "jsonb")]
    public string? Signatures { get; set; }

    /// <summary>
    /// Primary bot name detected (e.g., "Googlebot", "HeadlessChrome")
    /// </summary>
    [Column("bot_name")]
    [MaxLength(128)]
    public string? BotName { get; set; }

    /// <summary>
    /// Policy name that was used for detection
    /// </summary>
    [Column("policy_name")]
    [MaxLength(64)]
    public string? PolicyName { get; set; }

    /// <summary>
    /// Number of detector contributions
    /// </summary>
    [Column("detector_count")]
    public int DetectorCount { get; set; }

    /// <summary>
    /// Processing time in milliseconds
    /// </summary>
    [Column("processing_time_ms")]
    public double ProcessingTimeMs { get; set; }

    /// <summary>
    /// Full signature data as JSONB (indexed with GIN for fast querying by any signal)
    /// </summary>
    [Column("signature_json", TypeName = "jsonb")]
    public required string SignatureJson { get; set; }

    /// <summary>
    /// Signals extracted for indexing (stored as JSONB for filtering)
    /// Format: { "signal_name": value, ... }
    /// </summary>
    [Column("signals_json", TypeName = "jsonb")]
    public string? SignalsJson { get; set; }

    /// <summary>
    /// Created at timestamp (for TTL/cleanup)
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Expires at timestamp (for automatic cleanup)
    /// </summary>
    [Column("expires_at")]
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// Queryable signature view for efficient querying
/// </summary>
public class SignatureQueryResult
{
    public required string SignatureId { get; set; }
    public DateTime Timestamp { get; set; }
    public double BotProbability { get; set; }
    public double Confidence { get; set; }
    public string RiskBand { get; set; } = "Unknown";
    public string? RequestPath { get; set; }
    public string? RemoteIp { get; set; }
    public string? UserAgent { get; set; }
    public string? BotName { get; set; }
    public int DetectorCount { get; set; }
    public required string SignatureJson { get; set; }
}
