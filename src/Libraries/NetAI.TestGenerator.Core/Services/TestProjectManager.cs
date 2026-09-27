using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace DotNet10TestGenerator;

/// <summary>
/// Represents the result of test generation and validation.
/// </summary>
public sealed class TestGenerationResult
{
    public bool IsSuccess { get; }
    public string Message { get; }
    public string[]? CompilerErrors { get; }
    public string[]? ExceptionDetails { get; }

    public TestGenerationResult(
        bool isSuccess,
        string message,
        string[]? compilerErrors = null,
        string[]? exceptionDetails = null)
    {
        IsSuccess = isSuccess;
        Message = message;
        CompilerErrors = compilerErrors;
        ExceptionDetails = exceptionDetails;
    }
}

/// <summary>
/// Creates and validates unit test projects and classes for .NET 10.
/// Determines the solution directory automatically from the source file path
/// (nearest .sln or .slnx file in the directory tree above the file).
/// Intentionally does NOT execute tests – it only verifies that the generated
/// test class compiles.
/// </summary>
public class TestProjectManager
{
    private static readonly TimeSpan DefaultProcessTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Runs the full workflow: determine the solution directory, check/create the
    /// project (including a ProjectReference to the source project), write the test
    /// class and validate compilation.
    /// </summary>
    /// <param name="sourceFilePath">Path to the original C# file.</param>
    /// <param name="testClassCode">The complete C# code of the test class.</param>
    public async Task<TestGenerationResult> SetupAndValidateTestAsync(
        string sourceFilePath,
        string testClassCode,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceFilePath))
        {
            return new TestGenerationResult(false, $"The source file was not found: {sourceFilePath}");
        }

        if (string.IsNullOrWhiteSpace(testClassCode))
        {
            return new TestGenerationResult(false, "The supplied test class code is empty.");
        }

        try
        {
            // 1. Derive the solution directory from the source file path
            //    (nearest .sln or .slnx above the file).
            string? solutionDirectory = FindSolutionDirectory(sourceFilePath);
            if (solutionDirectory is null)
            {
                return new TestGenerationResult(
                    false,
                    $"No .sln or .slnx file was found above '{sourceFilePath}'.");
            }

            // 2. Extract the class name from the code for the file name.
            string testClassName = ExtractClassName(testClassCode);

            // 3. Determine the path for the test project (test/unittest/).
            string testProjectName = "UnitTestProject";
            string testProjectDir = Path.Combine(solutionDirectory, "tests", "UnitTests", testProjectName);
            string testProjectPath = Path.Combine(testProjectDir, $"{testProjectName}.csproj");

            // 4. Determine the project that contains the source file so that a
            //    ProjectReference can be added.
            string? sourceProjectPath = FindContainingProject(sourceFilePath, solutionDirectory);

            // 5. Create the test project if it does not exist.
            if (!File.Exists(testProjectPath))
            {
                var createResult = await CreateTestProjectAsync(
                    solutionDirectory, testProjectDir, testProjectName, sourceProjectPath, cancellationToken);
                if (!createResult.IsSuccess) return createResult;
            }
            else if (sourceProjectPath is not null &&
                     !await ProjectHasReferenceAsync(testProjectPath, sourceProjectPath, cancellationToken))
            {
                // The test project already existed, but the reference is still missing.
                await RunDotNetCliAsync(
                    new[] { "add", testProjectPath, "reference", sourceProjectPath },
                    testProjectDir,
                    cancellationToken);
            }

            // 6. Write the test class file (remember the previous content for a possible rollback).
            string testClassPath = Path.Combine(testProjectDir, $"{SafeFileName(testClassName)}.cs");
            bool isNewFile = !File.Exists(testClassPath);
            string? previousContent = isNewFile ? null : await ReadAllTextAsyncCompat(testClassPath, cancellationToken);
            await WriteAllTextAsyncCompat(testClassPath, testClassCode, Encoding.UTF8, cancellationToken);

            // 7. Verify that the test project compiles with the new class.
            var buildResult = await ValidateProjectCompilesAsync(testProjectPath, cancellationToken);
            if (!buildResult.IsSuccess)
            {
                RestoreOrDeleteTestFile(testClassPath, isNewFile, previousContent);
                return buildResult;
            }

            return new TestGenerationResult(true, $"Test class successfully created and validated in {testClassPath}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TestGenerationResult(
                false,
                $"An unexpected error occurred: {ex.Message}",
                exceptionDetails: FormatExceptionDetails(ex));
        }
    }

    /// <summary>
    /// Walks up from the directory of the source file looking for the nearest
    /// .sln or .slnx file and returns its directory (= solution directory).
    /// Null if none was found.
    /// </summary>
    private static string? FindSolutionDirectory(string sourceFilePath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(sourceFilePath))!);
        while (dir is not null)
        {
            bool hasSolution =
                dir.EnumerateFiles("*.sln").Any() ||
                dir.EnumerateFiles("*.slnx").Any();

            if (hasSolution)
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Extracts the name of the first class from the C# code using a regex.
    /// Single-line comments are ignored.
    /// </summary>
    private static string ExtractClassName(string classCode)
    {
        var codeWithoutLineComments = string.Join(
            "\n",
            classCode.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

        var match = Regex.Match(codeWithoutLineComments, @"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        return $"GeneratedTest_{Guid.NewGuid():N}".Substring(0, 24);
    }

    /// <summary>
    /// Removes invalid file name characters from a class name.
    /// </summary>
    private static string SafeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }

    /// <summary>
    /// Walks up from the directory of the source file (within the solution)
    /// looking for the nearest .csproj file.
    /// </summary>
    private static string? FindContainingProject(string filePath, string solutionDirectory)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        while (dir is not null && dir.FullName.StartsWith(solutionDirectory, StringComparison.OrdinalIgnoreCase))
        {
            var csproj = dir.GetFiles("*.csproj").FirstOrDefault();
            if (csproj is not null)
            {
                return csproj.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Checks whether the test project already contains a ProjectReference to the source project.
    /// </summary>
    private static async Task<bool> ProjectHasReferenceAsync(string testProjectPath, string sourceProjectPath, CancellationToken ct)
    {
        var xml = await ReadAllTextAsyncCompat(testProjectPath, ct);
        var doc = XDocument.Parse(xml);
        var testProjectDir = Path.GetDirectoryName(testProjectPath)!;

        var refs = doc.Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(v => v is not null)
            .Select(v => Path.GetFullPath(Path.Combine(testProjectDir, v!)));

        return refs.Any(r => string.Equals(r, Path.GetFullPath(sourceProjectPath), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<TestGenerationResult> CreateTestProjectAsync(
        string solutionDirectory,
        string directory,
        string projectName,
        string? sourceProjectPath,
        CancellationToken ct)
    {
        Directory.CreateDirectory(directory);

        // Use xUnit as the default test framework for .NET 10.
        //var newResult = await RunDotNetCliAsync(new[] { "new", "xunit", "-n", projectName }, directory, ct);
        var newResult = await RunDotNetCliAsync(
            new[] { "new", "xunit", "-n", projectName, "-o", "." },
            directory, ct);



        if (newResult.ExitCode != 0)
        {
            return new TestGenerationResult(false, "Failed to create the test project via the .NET CLI.", compilerErrors: newResult.Errors);
        }

        string projectPath = Path.Combine(directory, $"{projectName}.csproj");

        // Add the project to the solution if a .sln or .slnx file exists in the root directory.
        var slnFiles = Directory.GetFiles(solutionDirectory, "*.sln")
            .Concat(Directory.GetFiles(solutionDirectory, "*.slnx"))
            .ToArray();

        if (slnFiles.Length > 0)
        {
            await RunDotNetCliAsync(new[] { "sln", slnFiles[0], "add", projectPath }, solutionDirectory, ct);
        }

        // Set a ProjectReference to the source project.
        if (sourceProjectPath is not null)
        {
            var refResult = await RunDotNetCliAsync(new[] { "add", projectPath, "reference", sourceProjectPath }, directory, ct);

            if (refResult.ExitCode != 0)
            {
                return new TestGenerationResult(
                    false,
                    "The test project was created, but the reference to the source project could not be set.",
                    compilerErrors: refResult.Errors);
            }
        }

        // Delete the default generated xUnit sample file if present.
        string unittestClassFile = Path.Combine(directory, "UnitTest1.cs");
        if (File.Exists(unittestClassFile)) File.Delete(unittestClassFile);

        return new TestGenerationResult(true, "Test project created successfully.");
    }

    private static async Task<TestGenerationResult> ValidateProjectCompilesAsync(string projectPath, CancellationToken ct)
    {
        // Force a full rebuild so that stale artifacts cannot hide compile errors.
        // "-t:Rebuild" is stronger than "--no-incremental" and guarantees the
        // compiler actually runs against the current sources.
        var result = await RunDotNetCliAsync(
            new[] { "build", projectPath, "-t:Rebuild", "-v:minimal", "--nologo" },
            Path.GetDirectoryName(projectPath)!,
            ct);

        var allLines = result.Errors.Concat(result.Output).ToArray();

        if (result.ExitCode != 0)
        {
            // The .NET CLI is forced to English (DOTNET_CLI_UI_LANGUAGE=en / VSLANG=1033)
            // inside RunDotNetCliAsync, so "error CSxxxx" is the expected form.
            // The regex fallback also catches any residual localization.
            var compilerErrors = allLines
                .Where(line =>
                    line.Contains("error CS", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Fehler CS", StringComparison.OrdinalIgnoreCase) ||
                    Regex.IsMatch(line, @":\s*(error|Fehler)\s+[A-Z]{2}\d+", RegexOptions.IgnoreCase))
                .ToArray();

            return new TestGenerationResult(
                isSuccess: false,
                message: "The test class could not be compiled (syntax, namespace, or reference error).",
                compilerErrors: compilerErrors.Length > 0
                    ? compilerErrors
                    : allLines.Length > 0
                        ? allLines
                        : new[] { "Unknown compile error. See CLI output." }
            );
        }

        return new TestGenerationResult(true, "Compilation succeeded.");
    }

    private static void RestoreOrDeleteTestFile(string path, bool wasNew, string? previousContent)
    {
        try
        {
            if (wasNew)
            {
                if (File.Exists(path)) File.Delete(path);
            }
            else if (previousContent is not null)
            {
                File.WriteAllText(path, previousContent, Encoding.UTF8);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static Task<string> ReadAllTextAsyncCompat(string path, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return File.ReadAllText(path, Encoding.UTF8);
        }, cancellationToken);
    }

    private static Task WriteAllTextAsyncCompat(string path, string contents, Encoding encoding, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllText(path, contents, encoding);
        }, cancellationToken);
    }

    private static Task WaitForExitAsyncCompat(Process process, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnExited(object? sender, EventArgs e) => tcs.TrySetResult(true);

        process.Exited += OnExited;
        try
        {
            if (process.HasExited)
            {
                return Task.CompletedTask;
            }

            var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return WaitAndCleanupAsync(tcs.Task, registration, process, OnExited);
        }
        catch
        {
            process.Exited -= OnExited;
            throw;
        }
    }

    private static async Task WaitAndCleanupAsync(Task waitTask, CancellationTokenRegistration registration, Process process, EventHandler handler)
    {
        try
        {
            await waitTask.ConfigureAwait(false);
        }
        finally
        {
            registration.Dispose();
            process.Exited -= handler;
        }
    }

    private static string BuildArgumentString(IEnumerable<string> arguments)
    {
        var sb = new StringBuilder();
        foreach (var argument in arguments)
        {
            if (sb.Length > 0) sb.Append(' ');

            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                sb.Append(argument);
                continue;
            }

            sb.Append('"');
            foreach (char c in argument)
            {
                if (c == '"') sb.Append('\\');
                sb.Append(c);
            }
            sb.Append('"');
        }
        return sb.ToString();
    }

    private static async Task<(int ExitCode, string[] Output, string[] Errors)> RunDotNetCliAsync(
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var argumentList = arguments.ToList();

        //// Automatically add '--no-restore' when a 'new' command is used
        //// and the flag has not been passed yet.
        //if (argumentList.Contains("new") && !argumentList.Contains("--no-restore"))
        //{
        //    argumentList.Add("--no-restore");
        //}

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = BuildArgumentString(argumentList),
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Fixes the cryptic characters (Ã„, Ãœ) in the output.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // Force the .NET CLI (and the Roslyn diagnostics) to English so that
        // compiler errors are reliably reported as "error CSxxxx".
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        startInfo.Environment["VSLANG"] = "1033";

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        // ConcurrentQueue is thread-safe: the *DataReceived handlers run on
        // ThreadPool threads and would otherwise race with the reader below.
        var outputList = new ConcurrentQueue<string>();
        var errorList = new ConcurrentQueue<string>();

        process.OutputDataReceived += (s, e) => { if (e.Data != null) outputList.Enqueue(e.Data); };
        process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorList.Enqueue(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout ?? DefaultProcessTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await WaitForExitAsyncCompat(process, linkedCts.Token);

            // IMPORTANT: The async "Exited" event can fire before all
            // OutputDataReceived / ErrorDataReceived callbacks have been
            // flushed. A synchronous WaitForExit() guarantees that every line
            // has been captured before we read the queues below. Without this
            // the final lines (including the compiler errors) are frequently
            // missing.
            process.WaitForExit();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            errorList.Enqueue(
                $"The process 'dotnet {string.Join(" ", argumentList)}' was aborted after " +
                $"{(timeout ?? DefaultProcessTimeout).TotalSeconds}s (timeout).");
            return (-1, outputList.ToArray(), errorList.ToArray());
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return (process.ExitCode, outputList.ToArray(), errorList.ToArray());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private static string[] FormatExceptionDetails(Exception ex)
    {
        var lines = new List<string>();

        var current = ex;
        int depth = 0;
        while (current is not null)
        {
            string label = depth == 0 ? "Error" : $"Inner exception (level {depth})";
            lines.Add($"{label}: {current.GetType().FullName}: {current.Message}");
            current = current.InnerException;
            depth++;
        }

        if (!string.IsNullOrEmpty(ex.StackTrace))
        {
            var relevantFrames = ex.StackTrace
                .Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0 && !l.Contains("System.Runtime.CompilerServices"))
                .ToArray();

            if (relevantFrames.Length > 0)
            {
                lines.Add("Stack trace:");
                lines.AddRange(relevantFrames);
            }
        }

        return lines.ToArray();
    }
}