using Spectre.Console;

namespace NetAI.TestGenerator.Cli.Logging;

public sealed class SpectreLog
{
    private readonly bool _verbose;
    private readonly bool _noColor;

    public SpectreLog(bool verbose, bool noColor)
    {
        _verbose = verbose;
        _noColor = noColor;
    }

    public void Info(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (!_verbose && !message.StartsWith("[NetAI", StringComparison.Ordinal)) return;

        var text = _noColor ? Markup.Escape(message) : message;
        AnsiConsole.MarkupLine($"[grey]{text}[/]");
    }
}