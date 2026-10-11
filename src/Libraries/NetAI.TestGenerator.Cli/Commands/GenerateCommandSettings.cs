using System.ComponentModel;
using Spectre.Console.Cli;

namespace NetAI.TestGenerator.Cli.Commands;

public sealed class GenerateCommandSettings : CommandSettings
{
    [CommandArgument(0, "<sourceFile>")]
    [Description("Path to the C# source file to generate tests for.")]
    public string SourceFile { get; init; } = "";

    [CommandOption("--test-project <DIR>")]
    [Description("Directory of the test project. Defaults to a sibling '<Project>.Tests' folder.")]
    public string? TestProjectDirectory { get; init; }

    [CommandOption("--config <FILE>")]
    [Description("Path to aisettings.json. If omitted, the CLI searches upward from the source file.")]
    public string? ConfigPath { get; init; }

    [CommandOption("--solution <FILE>")]
    [Description("Path to the .sln to load via MSBuild (enables semantic analysis).")]
    public string? SolutionPath { get; init; }

    [CommandOption("--project <FILE>")]
    [Description("Path to the .csproj to load via MSBuild.")]
    public string? ProjectPath { get; init; }

    [CommandOption("--prompt-only")]
    [Description("Save the prompt to obj/netai/prompts but do not call the LLM.")]
    public bool PromptOnly { get; init; }

    [CommandOption("--framework <NAME>")]
    [Description("Override test framework (xunit|nunit|mstest).")]
    public string? TestFramework { get; init; }

    [CommandOption("--mocking <NAME>")]
    [Description("Override mocking framework (moq|nsubstitute|fakeiteasy|none).")]
    public string? MockingFramework { get; init; }

    [CommandOption("-v|--verbose")]
    public bool Verbose { get; init; }

    [CommandOption("--no-color")]
    public bool NoColor { get; init; }

    [CommandOption("--json")]
    [Description("Emit machine-readable JSON instead of console output.")]
    public bool Json { get; init; }
}