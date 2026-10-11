using System.ComponentModel;
using Spectre.Console.Cli;

namespace NetAI.TestGenerator.Cli.Commands;

public sealed class GenerateCommandSettings : CommandSettings
{
    [CommandArgument(0, "[source]")]
    [Description("Source file or directory. Optional if --project is given.")]
    public string? Source { get; init; }

    [CommandOption("--project <FILE>")]
    [Description("Path to a .csproj. Uses its Roslyn compilation to enumerate sources and enables semantic analysis (loaded once per run).")]
    public string? ProjectPath { get; init; }

    [CommandOption("--test-project <DIR>")]
    [Description("Directory of the test project. Default: sibling '<Project>.Tests' or 'tests/<Project>.Tests' searched upward from each source file.")]
    public string? TestProjectDirectory { get; init; }

    [CommandOption("--config <FILE>")]
    [Description("Path to aisettings.json. If omitted, the CLI searches upward from the first source file.")]
    public string? ConfigPath { get; init; }

    [CommandOption("--recursive")]
    [Description("If [source] is a directory, walk subdirectories. obj/ and bin/ are always excluded.")]
    public bool Recursive { get; init; }

    [CommandOption("--filter <GLOB>")]
    [Description("Only include file names matching this glob (e.g. '*.Services.cs'). Repeatable.")]
    public string[] Filter { get; init; } = Array.Empty<string>();

    [CommandOption("--exclude <GLOB>")]
    [Description("Exclude file names matching this glob. Repeatable.")]
    public string[] Exclude { get; init; } = Array.Empty<string>();

    [CommandOption("--max-files <N>")]
    [Description("Abort if more than N source files would be processed.")]
    public int? MaxFiles { get; init; }

    [CommandOption("--dry-run")]
    [Description("List what would be processed without calling the LLM or writing files.")]
    public bool DryRun { get; init; }

    [CommandOption("--prompt-only")]
    [Description("Save prompts to obj/netai/prompts and skip the LLM call.")]
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
    [Description("Machine-readable JSON on stdout, logs on stderr.")]
    public bool Json { get; init; }
}
