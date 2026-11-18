using NetAI.TestGenerator.Core.Models;
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

    public void Info(string message) => Log(GeneratorLogLevel.Info, message);

    public void Log(GeneratorLogLevel level, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        // Warnings and errors are always shown; info only for [NetAI] messages; debug only with --verbose.
        if (!_verbose && level == GeneratorLogLevel.Debug) return;
        if (!_verbose && level == GeneratorLogLevel.Info && !message.StartsWith("[NetAI", StringComparison.Ordinal)) return;

        if (_noColor)
        {
            // Raw write: no markup parsing at all.
            Console.WriteLine(message);
            return;
        }

        var color = level switch
        {
            GeneratorLogLevel.Error => "red",
            GeneratorLogLevel.Warning => "yellow",
            _ => "grey"
        };

        // Always escape: messages contain literal [NetAI] which Spectre would otherwise interpret as a style tag.
        AnsiConsole.MarkupLine($"[{color}]{Markup.Escape(message)}[/]");
    }
}
