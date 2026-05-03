using YamlDotNet.Serialization;

namespace Mostlylucid.BotDetection.Compliance;

/// <summary>A compliance pack loaded from YAML. Defines retention, anonymization, DSAR, and audit policy.</summary>
public sealed record CompliancePack
{
    [YamlMember(Alias = "id")]
    public required string Id { get; init; }

    [YamlMember(Alias = "name")]
    public required string Name { get; init; }

    [YamlMember(Alias = "description")]
    public string? Description { get; init; }

    [YamlMember(Alias = "jurisdiction")]
    public string? Jurisdiction { get; init; }

    [YamlMember(Alias = "legal_basis")]
    public string? LegalBasis { get; init; }

    [YamlMember(Alias = "legal_references")]
    public List<string>? LegalReferences { get; init; }

    [YamlMember(Alias = "requires_tier")]
    public string RequiresTier { get; init; } = "oss";

    [YamlMember(Alias = "position")]
    public string? Position { get; init; }

    [YamlMember(Alias = "extends")]
    public string? Extends { get; init; }

    [YamlMember(Alias = "retention")]
    public required RetentionPolicy Retention { get; init; }

    [YamlMember(Alias = "anonymization")]
    public required AnonymizationPolicy Anonymization { get; init; }

    [YamlMember(Alias = "dsar")]
    public DsarPolicy Dsar { get; init; } = new();

    [YamlMember(Alias = "audit")]
    public AuditPolicy Audit { get; init; } = new();

    [YamlMember(Alias = "explain")]
    public string? Explain { get; init; }
}

public sealed record RetentionPolicy
{
    [YamlMember(Alias = "detections")]
    public string Detections { get; init; } = "30d";

    [YamlMember(Alias = "signatures")]
    public string Signatures { get; init; } = "90d";

    [YamlMember(Alias = "sessions")]
    public string Sessions { get; init; } = "30d";

    [YamlMember(Alias = "ip_search_index")]
    public string IpSearchIndex { get; init; } = "30d";

    [YamlMember(Alias = "audit_log")]
    public string AuditLog { get; init; } = "365d";

    [YamlMember(Alias = "config_changes")]
    public string ConfigChanges { get; init; } = "365d";

    [YamlMember(Alias = "incident_records")]
    public string IncidentRecords { get; init; } = "365d";

    [YamlMember(Alias = "dsar_request_log")]
    public string DsarRequestLog { get; init; } = "365d";

    /// <summary>Parse a retention string like "30d", "180d", "365d" to days.</summary>
    public static int ParseDays(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 30;
        var trimmed = value.Trim().ToLowerInvariant();
        if (trimmed == "unlimited") return 0; // 0 = no cleanup
        if (trimmed.EndsWith('d') && int.TryParse(trimmed[..^1], out var days)) return days;
        if (int.TryParse(trimmed, out var raw)) return raw;
        return 30;
    }
}

public sealed record AnonymizationPolicy
{
    [YamlMember(Alias = "pii_hashing")]
    public string PiiHashing { get; init; } = "required";

    [YamlMember(Alias = "key_rotation_days")]
    public int KeyRotationDays { get; init; }

    [YamlMember(Alias = "ip_subnet_mask")]
    public int IpSubnetMask { get; init; }

    [YamlMember(Alias = "strip_raw_user_agent_after")]
    public string StripRawUserAgentAfter { get; init; } = "0";

    [YamlMember(Alias = "raw_payload_storage")]
    public bool RawPayloadStorage { get; init; }

    [YamlMember(Alias = "purpose_limitation")]
    public string? PurposeLimitation { get; init; }

    [YamlMember(Alias = "encryption_at_rest")]
    public string? EncryptionAtRest { get; init; }

    [YamlMember(Alias = "tls_minimum")]
    public string? TlsMinimum { get; init; }
}

public sealed record DsarPolicy
{
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; init; }

    [YamlMember(Alias = "export_format")]
    public string ExportFormat { get; init; } = "json";

    [YamlMember(Alias = "deletion_cascade")]
    public bool DeletionCascade { get; init; } = true;

    [YamlMember(Alias = "response_deadline_days")]
    public int ResponseDeadlineDays { get; init; } = 30;

    [YamlMember(Alias = "extension_allowed_days")]
    public int ExtensionAllowedDays { get; init; }

    [YamlMember(Alias = "acknowledge_deadline_days")]
    public int AcknowledgeDeadlineDays { get; init; }

    [YamlMember(Alias = "breach_notification_hours")]
    public int BreachNotificationHours { get; init; }

    [YamlMember(Alias = "honour_gpc_signals")]
    public bool HonourGpcSignals { get; init; }

    [YamlMember(Alias = "opt_out_endpoint")]
    public bool OptOutEndpoint { get; init; }

    [YamlMember(Alias = "regulatory_hold_override")]
    public bool RegulatoryHoldOverride { get; init; }
}

public sealed record AuditPolicy
{
    [YamlMember(Alias = "log_deletions")]
    public bool LogDeletions { get; init; }

    [YamlMember(Alias = "log_exports")]
    public bool LogExports { get; init; }

    [YamlMember(Alias = "log_admin_access")]
    public bool LogAdminAccess { get; init; }

    [YamlMember(Alias = "log_failed_access")]
    public bool LogFailedAccess { get; init; }

    [YamlMember(Alias = "tamper_evident")]
    public bool TamperEvident { get; init; }

    [YamlMember(Alias = "log_config_changes")]
    public bool LogConfigChanges { get; init; }

    [YamlMember(Alias = "log_system_events")]
    public bool LogSystemEvents { get; init; }

    [YamlMember(Alias = "compliance_export")]
    public bool ComplianceExport { get; init; }
}
