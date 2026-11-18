using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using NetAI.TestGenerator.Cli.Configuration;
using NetAI.TestGenerator.Cli.Infrastructure;
using NetAI.TestGenerator.Cli.Logging;
using NetAI.TestGenerator.Core;
using NetAI.TestGenerator.Core.Config;
using NetAI.TestGenerator.Core.Models;
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
            return ReportError(settings, "Provide a source file/directory or --project.", ExitCodes.ConfigurationError);
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
                log: log.AsInfoAction());
        }
        catch (Exception ex)
        {
            return ReportError(settings, $"Source resolution failed: {ex.Message}", ExitCodes.ConfigurationError);
        }

        if (sources.Count == 0)
        {
            return ReportError(settings, "No source files matched the given input.", ExitCodes.ConfigurationError);
        }

        // 2) Load and validate the config before doing anything expensive.
        AiTestingConfig coreConfig;
        try
        {
            coreConfig = SettingsLoader.Load(settings.ConfigPath, Path.GetDirectoryName(sources[0])!);

            if (!string.IsNullOrWhiteSpace(settings.TestFramework))
            {
                FrameworkParser.ParseTest(settings.TestFramework);
                coreConfig.Frameworks.TestFramework = settings.TestFramework!;
            }

            if (!string.IsNullOrWhiteSpace(settings.MockingFramework))
            {
                FrameworkParser.ParseMock(settings.MockingFramework);
                coreConfig.Frameworks.MockingFramework = settings.MockingFramework!;
            }
        }
        catch (Exception ex)
        {
            return ReportError(settings, $"Config error: {ex.Message}", ExitCodes.ConfigurationError);
        }

        if (settings.DryRun)
        {
            return RunDryRun(settings, sources, coreConfig);
        }

        using var cts = new CancellationTokenSource();

        // First Ctrl+C: cancel gracefully (test files are restored). Second Ctrl+C: let the process terminate.
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            if (cts.IsCancellationRequested) return;
            e.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine("Cancelling… press Ctrl+C again to abort immediately.");
        };
        Console.CancelKeyPress += handler;

        // 3) Load the MSBuild compilation once, keyed on the first source file.
        LoadedCompilation? loaded = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.ProjectPath))
            {
                loaded = await CompilationLoader.TryLoadAsync(settings.ProjectPath, Path.GetFileName(sources[0]), log.AsInfoAction(),
                    cts.Token).ConfigureAwait(false);
            }

            return await RunGenerateAsync(settings, sources, coreConfig, loaded, log, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return ReportError(settings, "Cancelled by user.", ExitCodes.Cancelled);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
            loaded?.Dispose();
        }
    }

    private static GeneratorLogger? BuildLog(GenerateCommandSettings settings)
    {
        if (settings.Json)
        {
            // JSON mode: stdout is reserved for the result; logs go to stderr.
            return settings.Verbose
                ? (level, msg) => Console.Error.WriteLine($"{level}: {msg}")
                : (level, msg) =>
                {
                    if (level >= GeneratorLogLevel.Warning) Console.Error.WriteLine($"{level}: {msg}");
                };
        }

        return new SpectreLog(settings.Verbose, settings.NoColor).Log;
    }

    private static int ReportError(GenerateCommandSettings settings, string message, int exitCode)
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

    // ---------------------------------------------------------------------
    // Dry-run
    // ---------------------------------------------------------------------

    private static int RunDryRun(GenerateCommandSettings settings, IReadOnlyList<string> sources, AiTestingConfig config)
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
                ?? ProjectLocator.ResolveTestProjectDirectory(src, config);

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
        AiTestingConfig config,
        LoadedCompilation? preloaded,
        GeneratorLogger? log,
        CancellationToken cancellationToken)
    {
        TetsGeneratorOrchestrator orchestrator;
        try
        {
            orchestrator = new TetsGeneratorOrchestrator(config);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ReportError(settings, $"Config error: {ex.Message}", ExitCodes.ConfigurationError);
        }

        using var _ = orchestrator;
        var results = new List<FileResult>(sources.Count);

        for (var i = 0; i < sources.Count; i++)
        {
            var src = sources[i];

            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(new FileResult(src, "cancelled", "Cancelled by user.", null));
                continue;
            }

            var testDir = settings.TestProjectDirectory
                ?? ProjectLocator.ResolveTestProjectDirectory(src, config);

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
                // The MSBuild compilation was loaded once above; never let the orchestrator reload it per file.
                var result = await orchestrator.ProcessProjectAsync(
                        src,
                        testDir,
                        log,
                        compilation: preloaded?.Compilation,
                        promptOnly: settings.PromptOnly,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var status = result.Status switch
                {
                    FileGenerationStatus.Success or FileGenerationStatus.NothingToDo => "ok",
                    FileGenerationStatus.PartialSuccess => "partial",
                    _ => "failed"
                };
                results.Add(new FileResult(src, status, result.Message, testDir));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                results.Add(new FileResult(src, "cancelled", "Cancelled by user.", testDir));
            }
            catch (Exception ex)
            {
                results.Add(new FileResult(src, "failed", ex.Message, testDir));
            }
        }

        return Report(settings, results);
    }

    private static int Report(GenerateCommandSettings settings, IReadOnlyList<FileResult> results)
    {
        var ok = results.Count(r => r.Status == "ok");
        var partial = results.Count(r => r.Status == "partial");
        var failed = results.Count(r => r.Status == "failed");
        var skipped = results.Count(r => r.Status == "no-test-project");
        var cancelled = results.Count(r => r.Status == "cancelled");

        var exitCode = cancelled > 0
            ? ExitCodes.Cancelled
            : failed == 0 && partial == 0 && skipped == 0 ? ExitCodes.Success : ExitCodes.GenerationFailed;

        if (settings.Json)
        {
            var payload = new
            {
                success = exitCode == ExitCodes.Success,
                total = results.Count,
                ok,
                partial,
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
            return exitCode;
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
                "partial" => "[yellow]partial[/]",
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
            $"[grey]Total: {results.Count} · ok: {ok} · partial: {partial} · failed: {failed} · skipped: {skipped} · cancelled: {cancelled}[/]");

        return exitCode;
    }

    private static string RelativePath(string fullPath)
    {
        try { return Path.GetRelativePath(Directory.GetCurrentDirectory(), fullPath); }
        catch { return fullPath; }
    }

    private sealed record FileResult(string File, string Status, string? Message, string? TestProjectDirectory);
}
