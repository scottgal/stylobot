using System.Text.Json.Serialization;

namespace Mostlylucid.StyloSpam.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpamVerdict
{
    Allow,
    Tag,
    Warn,
    Quarantine,
    Block
}
