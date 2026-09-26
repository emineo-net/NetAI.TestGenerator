using System;
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
/// Repräsentiert das Ergebnis der Testgenerierung und -validierung.
/// Bewusst eine einfache Klasse (kein "record"), da hier weder Wertgleichheit
/// noch "with"-Expressions gebraucht werden – so bleibt der Typ ohne jeden
/// Kompromiss netstandard2.0-kompatibel, ganz ohne Polyfill.
/// </summary>
public sealed class TestGenerationResult
{
    public bool IsSuccess { get; }
    public string Message { get; }

    /// <summary>
    /// Fehler aus dem Build-Workflow: Compiler-Fehler (CSxxxx) aus "dotnet build",
    /// oder Fehlerausgaben von "dotnet new"/"dotnet add reference"/"dotnet sln".
    /// Null, wenn kein solcher Fehler vorliegt.
    /// </summary>
    public string[]? CompilerErrors { get; }

    /// <summary>
    /// Nachricht, InnerException-Kette und (gefilterter) Stacktrace einer unerwarteten
    /// .NET-Exception (z. B. IOException beim Dateizugriff). Null, wenn keine Exception
    /// aufgetreten ist – ein Compiler-Fehlschlag allein füllt dieses Feld nicht.
    /// </summary>
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
/// Erstellt und validiert Unit-Test-Projekte und -Klassen für .NET 10.
/// Führt bewusst KEINE Tests aus – es wird nur geprüft, ob die generierte
/// Testklasse kompiliert.
/// </summary>
public class TestProjectManager
{
    private readonly string _solutionDirectory;
    private static readonly TimeSpan DefaultProcessTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Initialisiert eine neue Instanz des TestProjectManagers.
    /// </summary>
    /// <param name="solutionDirectory">Der Basispfad zur aktuellen .NET Solution.</param>
    public TestProjectManager(string solutionDirectory)
    {
        if (string.IsNullOrWhiteSpace(solutionDirectory) || !Directory.Exists(solutionDirectory))
        {
            throw new DirectoryNotFoundException($"Das Solution-Verzeichnis existiert nicht: {solutionDirectory}");
        }
        _solutionDirectory = Path.GetFullPath(solutionDirectory);
    }

    /// <summary>
    /// Führt den gesamten Workflow aus: Projekt prüfen/erstellen (inkl. ProjectReference
    /// auf das Quellprojekt), Testklasse schreiben und die Kompilierung validieren.
    /// </summary>
    /// <param name="sourceFilePath">Pfad zur originalen C#-Datei.</param>
    /// <param name="testClassCode">Der vollständige C#-Code der Testklasse.</param>
    public async Task<TestGenerationResult> SetupAndValidateTestAsync(
        string sourceFilePath,
        string testClassCode,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceFilePath))
        {
            return new TestGenerationResult(false, $"Die Quelldatei wurde nicht gefunden: {sourceFilePath}");
        }

        if (string.IsNullOrWhiteSpace(testClassCode))
        {
            return new TestGenerationResult(false, "Der übergebene Testklassen-Code ist leer.");
        }

        try
        {
            // 1. Klassennamen aus dem Code extrahieren für den Dateinamen
            string testClassName = ExtractClassName(testClassCode);

            // 2. Pfad für das Testprojekt bestimmen (test/unittest/)
            string testProjectDir = Path.Combine(_solutionDirectory, "test", "unittest");
            string testProjectName = "UnitTestProject";
            string testProjectPath = Path.Combine(testProjectDir, $"{testProjectName}.csproj");

            // 3. Projekt ermitteln, das die Quelldatei enthält, um eine ProjectReference
            //    zu setzen. Ohne diese Referenz kann die Testklasse die zu testende
            //    Klasse in aller Regel nicht auflösen, und der Build schlägt fehl.
            string? sourceProjectPath = FindContainingProject(sourceFilePath);

            // 4. Testprojekt erstellen, falls es nicht existiert
            if (!File.Exists(testProjectPath))
            {
                var createResult = await CreateTestProjectAsync(testProjectDir, testProjectName, sourceProjectPath, cancellationToken);
                if (!createResult.IsSuccess) return createResult;
            }
            else if (sourceProjectPath is not null &&
                     !await ProjectHasReferenceAsync(testProjectPath, sourceProjectPath, cancellationToken))
            {
                // Testprojekt existierte schon, aber die Referenz fehlt noch.
                await RunDotNetCliAsync(
                    new[] { "add", testProjectPath, "reference", sourceProjectPath },
                    testProjectDir,
                    cancellationToken);
            }

            // 5. Testklassendatei schreiben (vorherigen Inhalt für eventuellen Rollback merken)
            string testClassPath = Path.Combine(testProjectDir, $"{SafeFileName(testClassName)}.cs");
            bool isNewFile = !File.Exists(testClassPath);
            string? previousContent = isNewFile ? null : await ReadAllTextAsyncCompat(testClassPath, cancellationToken);
            await WriteAllTextAsyncCompat(testClassPath, testClassCode, Encoding.UTF8, cancellationToken);

            // 6. Prüfen, ob das Testprojekt mit der neuen Klasse kompiliert
            var buildResult = await ValidateProjectCompilesAsync(testProjectPath, cancellationToken);
            if (!buildResult.IsSuccess)
            {
                // Bei Fehlschlag: neue Datei löschen bzw. überschriebene Datei wiederherstellen
                RestoreOrDeleteTestFile(testClassPath, isNewFile, previousContent);
                return buildResult;
            }

            return new TestGenerationResult(true, $"Testklasse erfolgreich in {testClassPath} erstellt und validiert.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TestGenerationResult(
                false,
                $"Ein unerwarteter Fehler ist aufgetreten: {ex.Message}",
                exceptionDetails: FormatExceptionDetails(ex));
        }
    }

    /// <summary>
    /// Extrahiert den Namen der ersten Klasse aus dem C#-Code mithilfe von Regex.
    /// Einzeilige Kommentare werden dabei ignoriert. Fällt auf einen Standardnamen
    /// zurück, wenn kein Name gefunden wird.
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
    /// Entfernt ungültige Dateinamenzeichen aus einem Klassennamen.
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
    /// Sucht ausgehend vom Verzeichnis der Quelldatei nach oben (innerhalb der Solution)
    /// nach der nächstgelegenen .csproj-Datei.
    /// </summary>
    private string? FindContainingProject(string filePath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        while (dir is not null && dir.FullName.StartsWith(_solutionDirectory, StringComparison.OrdinalIgnoreCase))
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
    /// Prüft, ob das Testprojekt bereits eine ProjectReference auf das Quellprojekt enthält.
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
        string directory, string projectName, string? sourceProjectPath, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);

        // Nutzt xUnit als Standard-Testframework für .NET 10
        var newResult = await RunDotNetCliAsync(new[] { "new", "xunit", "-n", projectName }, directory, ct);
        if (newResult.ExitCode != 0)
        {
            return new TestGenerationResult(false, "Fehler beim Erstellen des Testprojekts via .NET CLI.", compilerErrors: newResult.Errors);
        }

        string projectPath = Path.Combine(directory, $"{projectName}.csproj");

        // Fügt das Projekt zur Solution hinzu, falls eine .sln Datei im Hauptverzeichnis existiert
        var slnFiles = Directory.GetFiles(_solutionDirectory, "*.sln");
        if (slnFiles.Length > 0)
        {
            await RunDotNetCliAsync(new[] { "sln", slnFiles[0], "add", projectPath }, _solutionDirectory, ct);
        }

        // ProjectReference auf das Quellprojekt setzen, damit die zu testende Klasse
        // für den Testcode überhaupt sichtbar ist.
        if (sourceProjectPath is not null)
        {
            var refResult = await RunDotNetCliAsync(new[] { "add", projectPath, "reference", sourceProjectPath }, directory, ct);
            if (refResult.ExitCode != 0)
            {
                return new TestGenerationResult(
                    false,
                    "Testprojekt wurde erstellt, aber die Referenz auf das Quellprojekt konnte nicht gesetzt werden.",
                    compilerErrors: refResult.Errors);
            }
        }

        // Standardmäßig generierte xUnit-Beispiel-Datei löschen, falls vorhanden
        string unittestClassFile = Path.Combine(directory, "UnitTest1.cs");
        if (File.Exists(unittestClassFile)) File.Delete(unittestClassFile);

        return new TestGenerationResult(true, "Testprojekt erfolgreich erstellt.");
    }

    private static async Task<TestGenerationResult> ValidateProjectCompilesAsync(string projectPath, CancellationToken ct)
    {
        // Führt 'dotnet build' aus, um Syntax- und Kompilierfehler zu fangen
        var result = await RunDotNetCliAsync(
            new[] { "build", projectPath, "--no-incremental" },
            Path.GetDirectoryName(projectPath)!,
            ct);

        if (result.ExitCode != 0)
        {
            // Filtert die Ausgabe, um nur echte Compiler-Fehler (CSxxxx) zurückzugeben
            var compilerErrors = result.Errors
                .Concat(result.Output)
                .Where(line => line.Contains("error CS"))
                .ToArray();

            return new TestGenerationResult(
                isSuccess: false,
                message: "Die Testklasse konnte nicht kompiliert werden (Syntax-, Namespace- oder Referenzfehler).",
                compilerErrors: compilerErrors.Length > 0 ? compilerErrors : new[] { "Unbekannter Kompilierfehler. Siehe CLI Ausgabe." }
            );
        }

        return new TestGenerationResult(true, "Kompilierung erfolgreich.");
    }

    /// <summary>
    /// Stellt bei einem Build-Fehlschlag den ursprünglichen Zustand der Testdatei wieder her:
    /// Bei einer neu angelegten Datei wird diese gelöscht, bei einer überschriebenen
    /// bestehenden Datei wird der vorherige Inhalt zurückgeschrieben.
    /// </summary>
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
            // Best-effort Cleanup – ein Fehler hier soll das eigentliche Ergebnis nicht verschlucken.
        }
    }

    /// <summary>
    /// netstandard2.0-Ersatz für File.ReadAllTextAsync (erst ab .NET Standard 2.1 / .NET Core 3.0 verfügbar).
    /// </summary>
    private static Task<string> ReadAllTextAsyncCompat(string path, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return File.ReadAllText(path, Encoding.UTF8);
        }, cancellationToken);
    }

    /// <summary>
    /// netstandard2.0-Ersatz für File.WriteAllTextAsync (erst ab .NET Standard 2.1 / .NET Core 3.0 verfügbar).
    /// </summary>
    private static Task WriteAllTextAsyncCompat(string path, string contents, Encoding encoding, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllText(path, contents, encoding);
        }, cancellationToken);
    }

    /// <summary>
    /// netstandard2.0-Ersatz für Process.WaitForExitAsync (erst ab .NET 5 verfügbar).
    /// Wartet über das "Exited"-Event statt über die native Async-API.
    /// </summary>
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

    /// <summary>
    /// netstandard2.0-taugliches, einfaches Quoting für die Kommandozeilen-Argumente
    /// (Ersatz für ProcessStartInfo.ArgumentList, das erst ab .NET Core 2.1 existiert).
    /// Deckt die üblichen Fälle (Leerzeichen, Anführungszeichen) ab, ist aber kein
    /// vollständiger Ersatz für das native Win32-Argument-Parsing.
    /// </summary>
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
        var argumentList = arguments as IList<string> ?? arguments.ToList();

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            // ArgumentList existiert erst ab .NET Core 2.1 (nicht in netstandard2.0) –
            // stattdessen ein manuell gequoteter Arguments-String.
            Arguments = BuildArgumentString(argumentList),
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var outputList = new List<string>();
        var errorList = new List<string>();

        process.OutputDataReceived += (s, e) => { if (e.Data != null) outputList.Add(e.Data); };
        process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorList.Add(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout ?? DefaultProcessTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            // Process.WaitForExitAsync existiert erst ab .NET 5 – netstandard2.0-Ersatz.
            await WaitForExitAsyncCompat(process, linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout, nicht externe Cancellation
            TryKill(process);
            errorList.Add(
                $"Der Prozess 'dotnet {string.Join(" ", argumentList)}' wurde nach " +
                $"{(timeout ?? DefaultProcessTimeout).TotalSeconds}s abgebrochen (Timeout).");
            return (-1, outputList.ToArray(), errorList.ToArray());
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return (process.ExitCode, outputList.ToArray(), errorList.ToArray());
    }

    /// <summary>
    /// Beendet den Prozess bei Timeout/Abbruch. Hinweis: Process.Kill(entireProcessTree: true)
    /// existiert erst ab .NET 5 und steht auf netstandard2.0 nicht zur Verfügung – hier wird
    /// daher nur der Hauptprozess beendet. Falls "dotnet build"/"dotnet new" selbst Kindprozesse
    /// startet (z. B. den Compiler-Worker), können diese als Waisenprozesse zurückbleiben. Falls
    /// das relevant wird, müsste hier plattformspezifisch nachgeholfen werden (Windows:
    /// "taskkill /T /F /PID <pid>", Unix: eigene Prozessgruppe + Signal an die Gruppe).
    /// </summary>
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
            // Best effort – der Prozess ist eventuell schon beendet.
        }
    }

    /// <summary>
    /// Bereitet eine Exception für die Rückgabe auf: Nachricht, komplette InnerException-Kette
    /// und Stacktrace. Rein compiler-generierte Async-State-Machine-Frames
    /// (System.Runtime.CompilerServices.*) werden herausgefiltert, da sie für die
    /// Fehlersuche keinen Mehrwert bieten.
    /// </summary>
    private static string[] FormatExceptionDetails(Exception ex)
    {
        var lines = new List<string>();

        var current = ex;
        int depth = 0;
        while (current is not null)
        {
            string label = depth == 0 ? "Fehler" : $"Innere Ausnahme (Ebene {depth})";
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
                lines.Add("Stacktrace:");
                lines.AddRange(relevantFrames);
            }
        }

        return lines.ToArray();
    }
}