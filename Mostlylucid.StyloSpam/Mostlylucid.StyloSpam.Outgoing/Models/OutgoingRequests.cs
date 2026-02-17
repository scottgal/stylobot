using Mostlylucid.StyloSpam.Core.Models;

namespace Mostlylucid.StyloSpam.Outgoing.Models;

public sealed record OutgoingRawFilterRequest(
    string UserId,
    string? TenantId,
    string RawMime,
    Dictionary<string, object>? Metadata);

public sealed record OutgoingSimpleFilterRequest(
    string UserId,
    string? TenantId,
    string From,
    List<string>? To,
    string? Subject,
    string? TextBody,
    string? HtmlBody,
    Dictionary<string, string>? Headers,
    List<EmailAttachment>? Attachments,
    Dictionary<string, object>? Metadata);

public sealed record OutgoingFilterDecision(
    string UserId,
    string? TenantId,
    SpamVerdict Verdict,
    string RecommendedAction,
    double SpamScore,
    double Confidence,
    IReadOnlyList<string> Reasons,
    int MessagesSentLastHour);
