using System.ComponentModel;
using Spectre.Console.Cli;

namespace NetAI.TestGenerator.Cli.Commands;

public sealed class AnalyseCommandSettings : CommandSettings
{
    [CommandArgument(0, "<sourceFile>")]
    [Description("Path to the C# source file to analyse.")]
    public string SourceFile { get; init; } = "";

    [CommandOption("--config <FILE>")]
    [Description("Path to aisettings.json. If omitted, the CLI searches upward from the source file.")]
    public string? ConfigPath { get; init; }

    [CommandOption("--json")]
    [Description("Emit machine-readable JSON instead of a table.")]
    public bool Json { get; init; }
}