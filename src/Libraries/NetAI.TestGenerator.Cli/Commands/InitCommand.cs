using System.Text.Json;
using System.Text.Json.Nodes;
using NetAI.TestGenerator.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NetAI.TestGenerator.Cli.Commands;

public sealed class InitCommandSettings : CommandSettings
{
    [CommandArgument(0, "[directory]")]
    public string? Directory { get; init; }
}

public sealed class InitCommand : Command<InitCommandSettings>
{
    public override int Execute(CommandContext context, InitCommandSettings settings)
    {
        var dir = Path.GetFullPath(settings.Directory ?? Directory.GetCurrentDirectory());
        var target = Path.Combine(dir, "aisettings.json");

        if (File.Exists(target))
        {
            AnsiConsole.MarkupLine($"[yellow]aisettings.json already exists:[/] {target}");
            return 0;
        }

        var defaultJson = """
                          {
                            "$schema": "aisettings-schema.json",
                            "version": "1.1",
                            "environment": { "targetDotNetVersion": "net9.0", "testProjectName": "{ProjectName}.Tests" },
                            "frameworks": {
                              "testFramework": "xunit",
                              "mockingFramework": "moq",
                              "useFluentAssertions": true,
                              "useAutoFixture": false,
                              "verbosePrompt": true
                            }
                          }
                          """;

        File.WriteAllText(target, defaultJson);
        AnsiConsole.MarkupLine($"[green]✓[/] Created {target}");
        return 0;
    }
}