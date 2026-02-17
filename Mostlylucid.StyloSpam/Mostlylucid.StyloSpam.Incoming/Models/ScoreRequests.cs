using Mostlylucid.StyloSpam.Core.Models;

namespace Mostlylucid.StyloSpam.Incoming.Models;

public sealed record RawMimeScoreRequest(
    string RawMime,
    string? TenantId,
    Dictionary<string, object>? Metadata);

public sealed record SimpleIncomingScoreRequest(
    string From,
    List<string>? To,
    string? Subject,
    string? TextBody,
    string? HtmlBody,
    string? TenantId,
    Dictionary<string, string>? Headers,
    List<EmailAttachment>? Attachments,
    Dictionary<string, object>? Metadata);
