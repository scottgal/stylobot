using MimeKit;
using Mostlylucid.StyloSpam.Core.Models;

namespace Mostlylucid.StyloSpam.Core.Services;

public static class EmailEnvelopeFactory
{
    public static EmailEnvelope FromRawMime(
        string rawMime,
        EmailFlowMode mode,
        string? tenantId = null,
        string? userId = null,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawMime));
        var message = MimeMessage.Load(stream);
        return EmailEnvelope.FromMimeMessage(message, mode, tenantId, userId, metadata);
    }

    public static EmailEnvelope FromEmlFile(
        string path,
        EmailFlowMode mode,
        string? tenantId = null,
        string? userId = null,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        return FromFile(path, mode, tenantId, userId, metadata);
    }

    public static EmailEnvelope FromFile(
        string path,
        EmailFlowMode mode,
        string? tenantId = null,
        string? userId = null,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        if (!EmailParsingFormats.IsSupported(path))
        {
            var ext = Path.GetExtension(path);
            throw new NotSupportedException(
                $"Unsupported email file format '{ext}'. Supported: {string.Join(", ", EmailParsingFormats.SupportedFileExtensions)}");
        }

        using var stream = File.OpenRead(path);
        var format = EmailParsingFormats.ResolveMimeFormat(path);

        MimeMessage message;
        if (format == MimeFormat.Mbox)
        {
            var parser = new MimeParser(stream, MimeFormat.Mbox);
            message = parser.ParseMessage();
        }
        else
        {
            message = MimeMessage.Load(stream);
        }

        return EmailEnvelope.FromMimeMessage(message, mode, tenantId, userId, metadata);
    }

    public static EmailEnvelope FromSimpleFields(
        EmailFlowMode mode,
        string from,
        IReadOnlyList<string> to,
        string? subject,
        string? textBody,
        string? htmlBody,
        string? tenantId = null,
        string? userId = null,
        IReadOnlyDictionary<string, object>? metadata = null,
        IReadOnlyDictionary<string, string>? headers = null,
        IReadOnlyList<EmailAttachment>? attachments = null)
    {
        return new EmailEnvelope
        {
            MessageId = Guid.NewGuid().ToString("N"),
            Mode = mode,
            TenantId = tenantId,
            UserId = userId,
            From = from,
            To = to,
            Subject = subject,
            TextBody = textBody,
            HtmlBody = htmlBody,
            Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Attachments = attachments ?? [],
            Metadata = metadata ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        };
    }
}
