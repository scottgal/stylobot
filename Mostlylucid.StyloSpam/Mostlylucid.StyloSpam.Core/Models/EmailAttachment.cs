namespace Mostlylucid.StyloSpam.Core.Models;

public sealed record EmailAttachment(
    string FileName,
    string? ContentType,
    long SizeBytes);
