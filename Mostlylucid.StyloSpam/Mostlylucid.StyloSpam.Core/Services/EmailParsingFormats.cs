using MimeKit;

namespace Mostlylucid.StyloSpam.Core.Services;

public static class EmailParsingFormats
{
    public static readonly HashSet<string> SupportedFileExtensions =
    [
        ".eml", ".mime", ".mbox", ".mbx"
    ];

    public static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path);
        return !string.IsNullOrWhiteSpace(extension) && SupportedFileExtensions.Contains(extension.ToLowerInvariant());
    }

    public static MimeFormat ResolveMimeFormat(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".mbox" or ".mbx" => MimeFormat.Mbox,
            _ => MimeFormat.Entity
        };
    }
}
