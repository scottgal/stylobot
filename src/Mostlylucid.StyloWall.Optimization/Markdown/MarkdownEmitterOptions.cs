namespace Mostlylucid.StyloWall.Optimization.Markdown;

public sealed class MarkdownEmitterOptions
{
    public const string SectionName = "StyloWall:Optimization:Markdown";

    public bool PreferMainContent { get; set; } = true;

    public bool EmitTables { get; set; } = true;

    public bool EmitFrontMatter { get; set; } = false;

    public string LinkStyle { get; set; } = "inline";

    public int MaxNestedListDepth { get; set; } = 8;
}
