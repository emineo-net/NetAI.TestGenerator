using System.Reflection;
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
    // Embedded from NetAI.TestGenerator.Tasks so the CLI and the NuGet package scaffold the very same files.
    private static readonly string[] TemplateFiles = { "aisettings.json", "aisettings-schema.json" };

    public override int Execute(CommandContext context, InitCommandSettings settings)
    {
        var dir = Path.GetFullPath(settings.Directory ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(dir))
        {
            AnsiConsole.MarkupLine($"[red]Directory not found:[/] {Markup.Escape(dir)}");
            return ExitCodes.ConfigurationError;
        }

        foreach (var fileName in TemplateFiles)
        {
            var target = Path.Combine(dir, fileName);
            if (File.Exists(target))
            {
                AnsiConsole.MarkupLine($"[yellow]{fileName} already exists:[/] {Markup.Escape(target)}");
                continue;
            }

            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(fileName)
                                 ?? throw new InvalidOperationException($"Embedded template '{fileName}' is missing.");
            using var output = File.Create(target);
            resource.CopyTo(output);

            AnsiConsole.MarkupLine($"[green]Created[/] {Markup.Escape(target)}");
        }

        return ExitCodes.Success;
    }
}
