using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.StyloSpam.Core.Extensions;
using Mostlylucid.StyloSpam.Core.Models;
using Mostlylucid.StyloSpam.Core.Services;

ParsedArgs parsed;
try
{
    parsed = ParseArgs(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    ShowHelp();
    return 1;
}

if (parsed.ShowHelp)
{
    ShowHelp();
    return 0;
}

if (string.IsNullOrWhiteSpace(parsed.InputPath) && string.IsNullOrWhiteSpace(parsed.RawMime) && !parsed.UseStdin)
{
    Console.Error.WriteLine("error: input is required (file path, --raw, or --stdin)");
    ShowHelp();
    return 1;
}

var services = new ServiceCollection();
services.AddStyloSpamScoring(options => options.DefaultMode = parsed.Mode);
var provider = services.BuildServiceProvider();

var engine = provider.GetRequiredService<EmailScoringEngine>();

EmailEnvelope envelope;
try
{
    envelope = await BuildEnvelopeAsync(parsed);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: failed to parse email input: {ex.Message}");
    return 2;
}

var result = await engine.EvaluateAsync(envelope);

if (parsed.Json)
{
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    }));
}
else
{
    PrintHumanReadable(result);
}

return 0;

static async Task<EmailEnvelope> BuildEnvelopeAsync(ParsedArgs parsed)
{
    if (!string.IsNullOrWhiteSpace(parsed.RawMime))
    {
        return EmailEnvelopeFactory.FromRawMime(parsed.RawMime, parsed.Mode);
    }

    if (parsed.UseStdin)
    {
        var stdin = await Console.In.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(stdin))
        {
            throw new InvalidOperationException("stdin was empty");
        }

        return EmailEnvelopeFactory.FromRawMime(stdin, parsed.Mode);
    }

    if (string.IsNullOrWhiteSpace(parsed.InputPath))
    {
        throw new InvalidOperationException("missing input path");
    }

    return EmailEnvelopeFactory.FromFile(parsed.InputPath, parsed.Mode);
}

static void PrintHumanReadable(EmailScoreResult result)
{
    Console.WriteLine("StyloSpam Email Score");
    Console.WriteLine("--------------------");
    Console.WriteLine($"Mode:       {result.Mode}");
    Console.WriteLine($"SpamScore:  {result.SpamScore:F3}");
    Console.WriteLine($"Confidence: {result.Confidence:F3}");
    Console.WriteLine($"Verdict:    {result.Verdict}");
    Console.WriteLine();

    Console.WriteLine("Top Reasons:");
    if (result.TopReasons.Count == 0)
    {
        Console.WriteLine("  - (none)");
    }
    else
    {
        foreach (var reason in result.TopReasons)
        {
            Console.WriteLine($"  - {reason}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("Contributions:");
    if (result.Contributions.Count == 0)
    {
        Console.WriteLine("  - (none)");
        return;
    }

    foreach (var contribution in result.Contributions.OrderByDescending(c => c.WeightedDelta))
    {
        Console.WriteLine(
            $"  - {contribution.Contributor,-24} delta={contribution.ScoreDelta,6:F3} weight={contribution.Weight,5:F2} weighted={contribution.WeightedDelta,6:F3} :: {contribution.Reason}");
    }
}

static ParsedArgs ParseArgs(string[] args)
{
    var parsed = new ParsedArgs();

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        switch (arg)
        {
            case "-h":
            case "--help":
                parsed.ShowHelp = true;
                break;
            case "--json":
                parsed.Json = true;
                break;
            case "--stdin":
                parsed.UseStdin = true;
                break;
            case "--mode":
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("--mode requires a value: incoming|outgoing");
                }

                var mode = args[++i];
                parsed.Mode = mode.Equals("outgoing", StringComparison.OrdinalIgnoreCase)
                    ? EmailFlowMode.Outgoing
                    : EmailFlowMode.Incoming;
                break;
            case "--raw":
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("--raw requires a MIME string");
                }

                parsed.RawMime = args[++i];
                break;
            default:
                if (arg.StartsWith("-", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"unknown option: {arg}");
                }

                parsed.InputPath = arg;
                break;
        }
    }

    return parsed;
}

static void ShowHelp()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  stylospam-score <email.eml|email.mime|mailbox.mbox> [--mode incoming|outgoing] [--json]");
    Console.WriteLine("  stylospam-score --stdin [--mode incoming|outgoing] [--json]");
    Console.WriteLine("  stylospam-score --raw \"<mime content>\" [--mode incoming|outgoing] [--json]");
    Console.WriteLine();
    Console.WriteLine("Notes:");
    Console.WriteLine("  - Parses RFC822 MIME email content with MimeKit/MailKit-compatible formats.");
    Console.WriteLine("  - File support: .eml, .mime, and .mbox (first message)." );
    Console.WriteLine("  - Uses the same scoring engine as StyloSpam Incoming/Outgoing services.");
}

file sealed class ParsedArgs
{
    public string? InputPath { get; set; }
    public string? RawMime { get; set; }
    public bool UseStdin { get; set; }
    public bool Json { get; set; }
    public bool ShowHelp { get; set; }
    public EmailFlowMode Mode { get; set; } = EmailFlowMode.Incoming;
}
