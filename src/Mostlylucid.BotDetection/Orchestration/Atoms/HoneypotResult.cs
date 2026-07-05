namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>Result of a Project Honeypot HTTP:BL DNS-blacklist lookup.</summary>
public class HoneypotResult
{
    public bool IsListed { get; set; }
    public int DaysSinceLastActivity { get; set; }
    public int ThreatScore { get; set; }
    public HoneypotVisitorType VisitorType { get; set; }
}

/// <summary>Visitor types from the Project Honeypot HTTP:BL API.</summary>
public enum HoneypotVisitorType
{
    None = 0,
    Suspicious = 1,
    Harvester = 2,
    CommentSpammer = 4,
    SearchEngine = 256
}
