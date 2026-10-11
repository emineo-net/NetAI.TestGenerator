using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetAI.TestGenerator.Cli.Configuration;
using NetAI.TestGenerator.Cli.Infrastructure;
using NetAI.TestGenerator.Core.Analysis;
using NetAI.TestGenerator.Core.Config;
using NetAI.TestGenerator.Core.Models;
using NetAI.TestGenerator.Core.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NetAI.TestGenerator.Cli.Commands;

public sealed class AnalyseCommand : AsyncCommand<AnalyseCommandSettings>
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public override async Task<int> ExecuteAsync(CommandContext context, AnalyseCommandSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SourceFile) || !File.Exists(settings.SourceFile))
        {
            AnsiConsole.MarkupLine($"[red]Source file not found:[/] {settings.SourceFile}");
            return ExitCodes.ConfigurationError;
        }

        var sourceFile = Path.GetFullPath(settings.SourceFile);
        var sourceDir = Path.GetDirectoryName(sourceFile)!;

        AiTestingConfig config;
        try
        {
            config = SettingsLoader.Load(settings.ConfigPath, sourceDir);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Config error:[/] {ex.Message}");
            return ExitCodes.ConfigurationError;
        }

        var tree = CSharpSyntaxTree.ParseText(
            await File.ReadAllTextAsync(sourceFile).ConfigureAwait(false),
            path: sourceFile);

        var root = tree.GetCompilationUnitRoot();
        var targetClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (targetClass is null)
        {
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { success = false, error = "No class found in source file." }, JsonOpts));
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]No class found in source file.[/]");
            }
            return ExitCodes.AnalysisFailed;
        }

        var compilation = CSharpCompilation.Create(
            "NetAI_Analyse",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location)
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        TestFrameworkProfile profile;
        try
        {
            profile = new TestFrameworkProfile(
                FrameworkParser.ParseTest(config.Frameworks?.TestFramework),
                FrameworkParser.ParseMock(config.Frameworks?.MockingFramework),
                config.Frameworks?.UseFluentAssertions ?? true,
                config.Frameworks?.UseAutoFixture ?? false,
                TestProjectInfo.ReadPackageIds(sourceDir));
        }
        catch (Exception ex)
        {
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOpts));
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Could not build analyzer profile:[/] {ex.Message}");
            }
            return ExitCodes.ConfigurationError;
        }

        var analyzer = new RoslynDllTestabilityAnalyzer(new AnalyzerOptions { TestFrameworkProfile = profile });
        var results = new List<MethodAnalysis>();

        foreach (var method in targetClass.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            try
            {
                var report = await analyzer.AnalyzeFromCompilationAsync(compilation, method).ConfigureAwait(false);
                results.Add(new MethodAnalysis(
                    Name: method.Identifier.Text,
                    Strategy: report.Strategy.ToString(),
                    Verdict: report.Verdict,
                    IsDirectlyTestable: report.IsDirectlyTestable,
                    RequiresSta: report.RequiresSta,
                    Blockers: report.Blockers.ToArray()));
            }
            catch (Exception ex)
            {
                results.Add(new MethodAnalysis(
                    Name: method.Identifier.Text,
                    Strategy: "Error",
                    Verdict: ex.Message,
                    IsDirectlyTestable: false,
                    RequiresSta: false,
                    Blockers: Array.Empty<string>()));
            }
        }

        if (settings.Json)
        {
            var payload = new
            {
                success = true,
                sourceFile = Path.GetFileName(sourceFile),
                @class = targetClass.Identifier.Text,
                methods = results
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, JsonOpts));
            return ExitCodes.Success;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Method");
        table.AddColumn("Strategy");
        table.AddColumn("Testable");
        table.AddColumn("STA");
        table.AddColumn("Verdict");

        foreach (var r in results)
        {
            table.AddRow(
                Markup.Escape(r.Name),
                Markup.Escape(r.Strategy),
                r.IsDirectlyTestable ? "[green]yes[/]" : "[red]no[/]",
                r.RequiresSta ? "[yellow]yes[/]" : "[grey]-[/]",
                Markup.Escape(r.Verdict));
        }

        AnsiConsole.Write(new Rule($"[blue]{Markup.Escape(targetClass.Identifier.Text)}[/]"));
        AnsiConsole.Write(table);

        foreach (var r in results.Where(x => x.Blockers.Length > 0))
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(r.Name)}[/]: {Markup.Escape(string.Join(" | ", r.Blockers))}");
        }

        return ExitCodes.Success;
    }

    private sealed record MethodAnalysis(
        string Name,
        string Strategy,
        string Verdict,
        bool IsDirectlyTestable,
        bool RequiresSta,
        string[] Blockers);
}