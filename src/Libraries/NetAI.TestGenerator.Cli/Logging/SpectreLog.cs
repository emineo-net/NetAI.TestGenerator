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

        // Only surface [NetAI]-prefixed messages unless --verbose.
        if (!_verbose && !message.StartsWith("[NetAI", StringComparison.Ordinal)) return;

        if (_noColor)
        {
            // Raw write: no markup parsing at all.
            Console.WriteLine(message);
        }
        else
        {
            // Always escape: messages contain literal [NetAI] which Spectre
            // would otherwise interpret as a style tag.
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(message)}[/]");
        }
    }
}