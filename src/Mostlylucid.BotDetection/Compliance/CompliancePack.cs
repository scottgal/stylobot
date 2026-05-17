using VYaml.Annotations;

namespace Mostlylucid.BotDetection.Compliance;

/// <summary>A compliance pack loaded from YAML. Defines retention, anonymization, DSAR, and audit policy.</summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class CompliancePack
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Jurisdiction { get; set; }
    public string? LegalBasis { get; set; }
    public List<string>? LegalReferences { get; set; }
    public string RequiresTier { get; set; } = "oss";
    public string? Position { get; set; }
    public string? Extends { get; set; }
    public RetentionPolicy Retention { get; set; } = new();
    public AnonymizationPolicy Anonymization { get; set; } = new();
    public DsarPolicy Dsar { get; set; } = new();
    public AuditPolicy Audit { get; set; } = new();
    public string? Explain { get; set; }
}

[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class RetentionPolicy
{
    public string Detections { get; set; } = "30d";
    public string Signatures { get; set; } = "90d";
    public string Sessions { get; set; } = "30d";
    public string IpSearchIndex { get; set; } = "30d";
    public string AuditLog { get; set; } = "365d";
    public string ConfigChanges { get; set; } = "365d";
    public string IncidentRecords { get; set; } = "365d";
    public string DsarRequestLog { get; set; } = "365d";

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

[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class AnonymizationPolicy
{
    public string PiiHashing { get; set; } = "required";
    public int KeyRotationDays { get; set; }
    public int IpSubnetMask { get; set; }
    public string StripRawUserAgentAfter { get; set; } = "0";
    public bool RawPayloadStorage { get; set; }
    public string? PurposeLimitation { get; set; }
    public string? EncryptionAtRest { get; set; }
    public string? TlsMinimum { get; set; }
}

[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class DsarPolicy
{
    public bool Enabled { get; set; }
    public string ExportFormat { get; set; } = "json";
    public bool DeletionCascade { get; set; } = true;
    public int ResponseDeadlineDays { get; set; } = 30;
    public int ExtensionAllowedDays { get; set; }
    public int AcknowledgeDeadlineDays { get; set; }
    public int BreachNotificationHours { get; set; }
    public bool HonourGpcSignals { get; set; }
    public bool OptOutEndpoint { get; set; }
    public bool RegulatoryHoldOverride { get; set; }
}

[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class AuditPolicy
{
    public bool LogDeletions { get; set; }
    public bool LogExports { get; set; }
    public bool LogAdminAccess { get; set; }
    public bool LogFailedAccess { get; set; }
    public bool TamperEvident { get; set; }
    public bool LogConfigChanges { get; set; }
    public bool LogSystemEvents { get; set; }
    public bool ComplianceExport { get; set; }
}
