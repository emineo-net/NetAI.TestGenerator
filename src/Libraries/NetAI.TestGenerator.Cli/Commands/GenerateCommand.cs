using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using NetAI.TestGenerator.Cli.Configuration;
using NetAI.TestGenerator.Cli.Infrastructure;
using NetAI.TestGenerator.Cli.Logging;
using NetAI.TestGenerator.Core;
using NetAI.TestGenerator.Core.Config;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NetAI.TestGenerator.Cli.Commands;

public sealed class GenerateCommand : AsyncCommand<GenerateCommandSettings>
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public override async Task<int> ExecuteAsync(CommandContext context, GenerateCommandSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Source) && string.IsNullOrWhiteSpace(settings.ProjectPath))
        {
            AnsiConsole.MarkupLine("[red]Provide a source file/directory or --project.[/]");
            return ExitCodes.ConfigurationError;
        }

        var log = BuildLog(settings);

        // 1) Resolve source files FIRST — the first one becomes the MSBuild target file.
        IReadOnlyList<string> sources;
        try
        {
            sources = SourceCollector.Collect(
                settings.Source,
                settings.ProjectPath,
                compilation: null,
                recursive: settings.Recursive,
                filters: settings.Filter,
                excludes: settings.Exclude,
                maxFiles: settings.MaxFiles,
                log: log);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Source resolution failed:[/] {ex.Message}");
            return ExitCodes.ConfigurationError;
        }

        if (sources.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No source files matched the given input.[/]");
            return ExitCodes.ConfigurationError;
        }

        // 2) Load MSBuild compilation once, keyed on the first source file.
        LoadedCompilation? loaded = null;
        if (!settings.DryRun && !string.IsNullOrWhiteSpace(settings.ProjectPath))
        {
            loaded = await CompilationLoader.TryLoadAsync(
                settings.ProjectPath,
                Path.GetFileName(sources[0]),
                log).ConfigureAwait(false);
        }

        try
        {
            // 3) Load the config once, from the first source file's directory.
            AiTestingConfig coreConfig;
            try
            {
                coreConfig = SettingsLoader.Load(settings.ConfigPath, Path.GetDirectoryName(sources[0])!);
                if (!string.IsNullOrWhiteSpace(settings.TestFramework))
                    coreConfig.Frameworks.TestFramework = settings.TestFramework!;
                if (!string.IsNullOrWhiteSpace(settings.MockingFramework))
                    coreConfig.Frameworks.MockingFramework = settings.MockingFramework!;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Config error:[/] {ex.Message}");
                return ExitCodes.ConfigurationError;
            }

            // 4) Dispatch.
            return settings.DryRun
                ? RunDryRun(settings, sources)
                : await RunGenerateAsync(settings, sources, coreConfig, loaded, log).ConfigureAwait(false);
        }
        finally
        {
            loaded?.Dispose();
        }
    }

    private static Action<string>? BuildLog(GenerateCommandSettings settings)
    {
        if (settings.Json)
            return settings.Verbose ? msg => Console.Error.WriteLine(msg) : null;

        return new SpectreLog(settings.Verbose, settings.NoColor).Info;
    }

    // ---------------------------------------------------------------------
    // Dry-run
    // ---------------------------------------------------------------------

    private static int RunDryRun(GenerateCommandSettings settings, IReadOnlyList<string> sources)
    {
        var sw = Stopwatch.StartNew();
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("File");
        table.AddColumn("Class");
        table.AddColumn("Methods");
        table.AddColumn("Test project");

        foreach (var src in sources)
        {
            var (className, methodCount) = InspectFile(src);
            var testDir = settings.TestProjectDirectory
                ?? ProjectLocator.ResolveTestProjectDirectory(src);

            table.AddRow(
                Markup.Escape(Path.GetFileName(src)),
                Markup.Escape(className ?? "<none>"),
                methodCount.ToString(),
                testDir is null ? "[red]<not found>[/]" : Markup.Escape(RelativePath(testDir)));
        }

        AnsiConsole.MarkupLine($"[blue]Dry run[/] — {sources.Count} file(s) would be processed.");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]Elapsed: {sw.ElapsedMilliseconds} ms[/]");
        return ExitCodes.Success;
    }

    private static (string? ClassName, int MethodCount) InspectFile(string file)
    {
        try
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
            var root = tree.GetCompilationUnitRoot();
            var cls = root.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
                .FirstOrDefault();
            var methods = root.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                .Count();
            return (cls?.Identifier.Text, methods);
        }
        catch
        {
            return (null, 0);
        }
    }

    // ---------------------------------------------------------------------
    // Actual generation
    // ---------------------------------------------------------------------

    private static async Task<int> RunGenerateAsync(
        GenerateCommandSettings settings,
        IReadOnlyList<string> sources,
        AiTestingConfig coreConfig,
        LoadedCompilation? preloaded,
        Action<string>? log)
    {
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += handler;

        var results = new List<FileResult>(sources.Count);
        var orchestrator = new ResxTranslationOrchestrator(coreConfig);

        try
        {
            for (var i = 0; i < sources.Count; i++)
            {
                if (cts.IsCancellationRequested)
                {
                    results.Add(new FileResult(sources[i], "cancelled", "Cancelled by user.", null));
                    continue;
                }

                var src = sources[i];
                var testDir = settings.TestProjectDirectory
                    ?? ProjectLocator.ResolveTestProjectDirectory(src);

                if (testDir is null)
                {
                    results.Add(new FileResult(src, "no-test-project", "Could not resolve test project directory.", null));
                    continue;
                }

                testDir = Path.GetFullPath(testDir);

                if (!settings.Json)
                {
                    AnsiConsole.MarkupLine(
                        $"[blue][[{i + 1}/{sources.Count}]][/] {Markup.Escape(Path.GetFileName(src))}");
                }

                try
                {
                    var result = await orchestrator.ProcessProjectAsync(
                        src,
                        testDir,
                        log,
                        compilation: preloaded?.Compilation,
                        promptOnly: settings.PromptOnly,
                        solutionPath: null,
                        projectPath: preloaded is null ? settings.ProjectPath : null)
                        .ConfigureAwait(false);

                    var status = string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase) ? "ok" : "failed";
                    results.Add(new FileResult(src, status, status == "ok" ? null : result, testDir));
                }
                catch (Exception ex)
                {
                    results.Add(new FileResult(src, "failed", ex.Message, testDir));
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }

        return Report(settings, results);
    }

    private static int Report(GenerateCommandSettings settings, IReadOnlyList<FileResult> results)
    {
        var ok = results.Count(r => r.Status == "ok");
        var failed = results.Count(r => r.Status == "failed");
        var skipped = results.Count(r => r.Status == "no-test-project");
        var cancelled = results.Count(r => r.Status == "cancelled");

        if (settings.Json)
        {
            var payload = new
            {
                success = failed == 0 && skipped == 0 && cancelled == 0,
                total = results.Count,
                ok,
                failed,
                skipped,
                cancelled,
                files = results.Select(r => new
                {
                    file = Path.GetFileName(r.File),
                    status = r.Status,
                    testProjectDirectory = r.TestProjectDirectory,
                    message = r.Message
                })
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, JsonOpts));

            if (cancelled > 0) return ExitCodes.Cancelled;
            return failed == 0 && skipped == 0 ? ExitCodes.Success : ExitCodes.GenerationFailed;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("File");
        table.AddColumn("Status");
        table.AddColumn("Test project");
        table.AddColumn("Message");

        foreach (var r in results)
        {
            var statusMarkup = r.Status switch
            {
                "ok" => "[green]ok[/]",
                "failed" => "[red]failed[/]",
                "no-test-project" => "[yellow]skipped[/]",
                "cancelled" => "[grey]cancelled[/]",
                _ => Markup.Escape(r.Status)
            };

            table.AddRow(
                Markup.Escape(Path.GetFileName(r.File)),
                statusMarkup,
                r.TestProjectDirectory is null ? "-" : Markup.Escape(RelativePath(r.TestProjectDirectory)),
                Markup.Escape(r.Message ?? ""));
        }

        AnsiConsole.Write(new Rule("[blue]Summary[/]"));
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"[grey]Total: {results.Count} · ok: {ok} · failed: {failed} · skipped: {skipped} · cancelled: {cancelled}[/]");

        if (cancelled > 0) return ExitCodes.Cancelled;
        return failed == 0 && skipped == 0 ? ExitCodes.Success : ExitCodes.GenerationFailed;
    }

    private static string RelativePath(string fullPath)
    {
        try { return Path.GetRelativePath(Directory.GetCurrentDirectory(), fullPath); }
        catch { return fullPath; }
    }

    private sealed record FileResult(string File, string Status, string? Message, string? TestProjectDirectory);
}