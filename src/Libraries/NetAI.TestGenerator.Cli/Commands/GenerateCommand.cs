using System.Text.Json;
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
        if (string.IsNullOrWhiteSpace(settings.SourceFile) || !File.Exists(settings.SourceFile))
        {
            AnsiConsole.MarkupLine($"[red]Source file not found:[/] {settings.SourceFile}");
            return ExitCodes.ConfigurationError;
        }

        var sourceFile = Path.GetFullPath(settings.SourceFile);
        var sourceDir = Path.GetDirectoryName(sourceFile)!;

        AiTestingConfig coreConfig;
        try
        {
            coreConfig = SettingsLoader.Load(settings.ConfigPath, sourceDir);

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

        var testProjectDir = settings.TestProjectDirectory
            ?? ProjectLocator.ResolveTestProjectDirectory(sourceFile);

        if (testProjectDir is null)
        {
            AnsiConsole.MarkupLine("[red]Could not determine test project directory. Use --test-project.[/]");
            return ExitCodes.ConfigurationError;
        }
        testProjectDir = Path.GetFullPath(testProjectDir);

        var solution = settings.SolutionPath ?? ProjectLocator.FindUpwards(sourceDir, "*.sln");
        var project = settings.ProjectPath ?? ProjectLocator.FindUpwards(sourceDir, "*.csproj");

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            var orchestrator = new ResxTranslationOrchestrator(coreConfig);

            // JSON mode: no Spectre spinner, logs to stderr, final JSON on stdout.
            if (settings.Json)
            {
                Action<string>? stderrLog = settings.Verbose
                    ? msg => Console.Error.WriteLine(msg)
                    : null;

                string result;
                try
                {
                    result = await orchestrator.ProcessProjectAsync(
                        sourceFile,
                        testProjectDir,
                        stderrLog,
                        compilation: null,
                        promptOnly: settings.PromptOnly,
                        solutionPath: solution,
                        projectPath: project).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        success = false,
                        sourceFile = Path.GetFileName(sourceFile),
                        testProjectDirectory = testProjectDir,
                        error = ex.Message
                    }, JsonOpts));
                    return ExitCodes.UnhandledError;
                }

                var success = string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    success,
                    sourceFile = Path.GetFileName(sourceFile),
                    testProjectDirectory = testProjectDir,
                    result
                }, JsonOpts));

                return success ? ExitCodes.Success : ExitCodes.GenerationFailed;
            }

            // Human mode: Spectre spinner + markup.
            var log = new SpectreLog(settings.Verbose, settings.NoColor);
            var exitCode = ExitCodes.Success;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync($"Generating tests for [yellow]{Path.GetFileName(sourceFile)}[/]…", async _ =>
                {
                    try
                    {
                        var result = await orchestrator.ProcessProjectAsync(
                            sourceFile,
                            testProjectDir,
                            log.Info,
                            compilation: null,
                            promptOnly: settings.PromptOnly,
                            solutionPath: solution,
                            projectPath: project).ConfigureAwait(false);

                        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                        {
                            AnsiConsole.MarkupLine($"[red]Failed:[/] {result}");
                            exitCode = ExitCodes.GenerationFailed;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
                        exitCode = ExitCodes.Cancelled;
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Unhandled:[/] {ex.Message}");
                        exitCode = ExitCodes.UnhandledError;
                    }
                }).ConfigureAwait(false);

            if (exitCode == ExitCodes.Success)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Done. Test project: [grey]{testProjectDir}[/]");
            }

            return exitCode;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
}