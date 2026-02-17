using MimeKit;

namespace Mostlylucid.StyloSpam.Core.Models;

public sealed class EmailEnvelope
{
    public required string MessageId { get; init; }
    public required EmailFlowMode Mode { get; init; }
    public DateTimeOffset ReceivedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? TenantId { get; init; }
    public string? UserId { get; init; }
    public string From { get; init; } = string.Empty;
    public IReadOnlyList<string> To { get; init; } = [];
    public IReadOnlyList<string> Cc { get; init; } = [];
    public IReadOnlyList<string> Bcc { get; init; } = [];
    public string? Subject { get; init; }
    public string? TextBody { get; init; }
    public string? HtmlBody { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<EmailAttachment> Attachments { get; init; } = [];
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

    public int TotalRecipientCount => To.Count + Cc.Count + Bcc.Count;

    public IEnumerable<string> EnumerateBodies()
    {
        if (!string.IsNullOrWhiteSpace(TextBody))
        {
            yield return TextBody;
        }

        if (!string.IsNullOrWhiteSpace(HtmlBody))
        {
            yield return HtmlBody;
        }
    }

    public static EmailEnvelope FromMimeMessage(MimeMessage message, EmailFlowMode mode, string? tenantId = null, string? userId = null,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in message.Headers)
        {
            if (!headers.ContainsKey(header.Field))
            {
                headers[header.Field] = header.Value;
            }
        }

        var attachments = message.Attachments
            .Select(a =>
            {
                var fileName = a.ContentDisposition?.FileName ?? a.ContentType.Name ?? "attachment";
                var size = a is MimePart mp ? (long?)mp.Content?.Stream?.Length : null;
                return new EmailAttachment(fileName, a.ContentType?.MimeType, size ?? 0);
            })
            .ToList();

        var to = message.To.Mailboxes.Select(m => m.Address).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        var cc = message.Cc.Mailboxes.Select(m => m.Address).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        var bcc = message.Bcc.Mailboxes.Select(m => m.Address).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

        return new EmailEnvelope
        {
            MessageId = string.IsNullOrWhiteSpace(message.MessageId) ? Guid.NewGuid().ToString("N") : message.MessageId,
            Mode = mode,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            TenantId = tenantId,
            UserId = userId,
            From = message.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty,
            To = to,
            Cc = cc,
            Bcc = bcc,
            Subject = message.Subject,
            TextBody = message.TextBody,
            HtmlBody = message.HtmlBody,
            Headers = headers,
            Attachments = attachments,
            Metadata = metadata ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        };
    }
}
