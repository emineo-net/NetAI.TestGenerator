using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetAI.TestGenerator.Cli.Configuration;
using NetAI.TestGenerator.Cli.Infrastructure;
using NetAI.TestGenerator.Core.Analysis;
using NetAI.TestGenerator.Core.Config;
using NetAI.TestGenerator.Core.Models;
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
            return Fail(settings, $"Source file not found: {settings.SourceFile}", ExitCodes.ConfigurationError);
        }

        var sourceFile = Path.GetFullPath(settings.SourceFile);
        var sourceDir = Path.GetDirectoryName(sourceFile)!;

        AiTestingConfig config;
        TestFrameworkProfile profile;
        try
        {
            config = SettingsLoader.Load(settings.ConfigPath, sourceDir);

            // Same package source as 'generate': the test project, not the source directory.
            var testProjectDir = ProjectLocator.ResolveTestProjectDirectory(sourceFile, config);
            profile = new TestFrameworkProfile(
                FrameworkParser.ParseTest(config.Frameworks?.TestFramework),
                FrameworkParser.ParseMock(config.Frameworks?.MockingFramework),
                config.Frameworks?.UseFluentAssertions ?? true,
                config.Frameworks?.UseAutoFixture ?? false,
                TestProjectInfo.ReadPackageIds(testProjectDir));
        }
        catch (Exception ex)
        {
            return Fail(settings, $"Config error: {ex.Message}", ExitCodes.ConfigurationError);
        }

        // With --project the real project compilation is used (all references resolved), like 'generate'.
        using var loaded = await CompilationLoader.TryLoadAsync(settings.ProjectPath, Path.GetFileName(sourceFile), null)
            .ConfigureAwait(false);

        var compilation = loaded?.Compilation ?? BuildStandaloneCompilation(sourceFile);
        var tree = compilation.SyntaxTrees.FirstOrDefault(t =>
                       !string.IsNullOrEmpty(t.FilePath) &&
                       string.Equals(Path.GetFullPath(t.FilePath), sourceFile, StringComparison.OrdinalIgnoreCase))
                   ?? compilation.SyntaxTrees.First();

        // Same selection as the generator: top-level classes/records, methods declared directly on them.
        var classes = tree.GetCompilationUnitRoot().DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t is ClassDeclarationSyntax or RecordDeclarationSyntax)
            .Where(t => t.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
            .ToList();

        if (classes.Count == 0)
        {
            return Fail(settings, "No class found in source file.", ExitCodes.AnalysisFailed);
        }

        var analyzer = new RoslynDllTestabilityAnalyzer(new AnalyzerOptions { TestFrameworkProfile = profile });
        var results = new List<ClassAnalysis>();

        foreach (var cls in classes)
        {
            var methods = new List<MethodAnalysis>();
            foreach (var method in cls.Members.OfType<MethodDeclarationSyntax>())
            {
                try
                {
                    var report = await analyzer.AnalyzeFromCompilationAsync(compilation, method).ConfigureAwait(false);
                    methods.Add(new MethodAnalysis(method.Identifier.Text, report.Strategy.ToString(), report.Verdict,
                        report.IsDirectlyTestable, report.RequiresSta, report.Blockers.ToArray()));
                }
                catch (Exception ex)
                {
                    methods.Add(new MethodAnalysis(method.Identifier.Text, "Error", ex.Message, false, false, Array.Empty<string>()));
                }
            }

            results.Add(new ClassAnalysis(cls.Identifier.Text, methods));
        }

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success = true,
                sourceFile = Path.GetFileName(sourceFile),
                semanticContext = loaded is not null ? "project" : "standalone",
                classes = results
            }, JsonOpts));
            return ExitCodes.Success;
        }

        if (loaded is null)
        {
            AnsiConsole.MarkupLine("[grey]Analysed without project context (types from other files/packages are unresolved). " +
                                   "Use --project for exact results.[/]");
        }

        foreach (var cls in results)
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Method");
            table.AddColumn("Strategy");
            table.AddColumn("Testable");
            table.AddColumn("STA");
            table.AddColumn("Verdict");

            foreach (var r in cls.Methods)
            {
                table.AddRow(
                    Markup.Escape(r.Name),
                    Markup.Escape(r.Strategy),
                    r.IsDirectlyTestable ? "[green]yes[/]" : "[red]no[/]",
                    r.RequiresSta ? "[yellow]yes[/]" : "[grey]-[/]",
                    Markup.Escape(r.Verdict));
            }

            AnsiConsole.Write(new Rule($"[blue]{Markup.Escape(cls.Name)}[/]"));
            AnsiConsole.Write(table);

            foreach (var r in cls.Methods.Where(x => x.Blockers.Length > 0))
            {
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(r.Name)}[/]: {Markup.Escape(string.Join(" | ", r.Blockers))}");
            }
        }

        return ExitCodes.Success;
    }

    /// <summary>Compiles the single file against the runtime's framework assemblies.</summary>
    private static Compilation BuildStandaloneCompilation(string sourceFile)
    {
        var references = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(File.Exists);

        return RoslynDllTestabilityAnalyzer.BuildCompilation(new[] { sourceFile }, references, "NetAI_Analyse",
            new CSharpParseOptions(LanguageVersion.Latest));
    }

    private static int Fail(AnalyseCommandSettings settings, string message, int exitCode)
    {
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { success = false, error = message }, JsonOpts));
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        }

        return exitCode;
    }

    private sealed record ClassAnalysis(string Name, IReadOnlyList<MethodAnalysis> Methods);

    private sealed record MethodAnalysis(
        string Name,
        string Strategy,
        string Verdict,
        bool IsDirectlyTestable,
        bool RequiresSta,
        string[] Blockers);
}
