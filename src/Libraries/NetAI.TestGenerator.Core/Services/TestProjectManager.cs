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
    /// <summary>
    /// Default name used for the sample test file that the "dotnet new" test
    /// templates (xunit/nunit/mstest) generate. It is removed after project
    /// creation since we write our own generated test class instead.
    /// </summary>
    private const string DefaultSampleFileName = "UnitTest1.cs";

    /// <summary>
    /// Upper bound for how many "detect missing packages -> add -> rebuild"
    /// rounds <see cref="TryResolveMissingPackagesAndRebuildAsync"/> will run.
    /// Guards against pathological cases (e.g. a using directive that can
    /// never be resolved to a real package) looping forever.
    /// </summary>
    private const int MaxPackageResolutionIterations = 5;

    private readonly TimeSpan _defaultProcessTimeout;
    private readonly string _dotnetExecutable;

    public TestProjectManager(TimeSpan? defaultProcessTimeout = null, string dotnetExecutable = "dotnet")
    {
        _defaultProcessTimeout = defaultProcessTimeout ?? TimeSpan.FromMinutes(5);
        _dotnetExecutable = string.IsNullOrWhiteSpace(dotnetExecutable) ? "dotnet" : dotnetExecutable;
    }

    /// <summary>
    /// Maps a namespace (as it appears in a "using ...;" directive) to the NuGet
    /// package id that provides it, for cases where the package id does not
    /// simply equal the namespace (or one of its leading segments). Callers may
    /// add further entries before invoking <see cref="SetupAndValidateTestAsync"/>.
    /// Lookup in <see cref="GetPackageCandidates"/> also tries the namespace
    /// itself (and shortened prefixes of it) as a package id, so this map only
    /// needs entries where that default guess would be wrong.
    /// </summary>
    public IDictionary<string, string> KnownNamespaceToPackageMap { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["NSubstitute"] = "NSubstitute",
        ["NSubstitute.ExceptionExtensions"] = "NSubstitute",
        ["NSubstitute.ReturnsExtensions"] = "NSubstitute",
        ["Moq"] = "Moq",
        ["Moq.Protected"] = "Moq",
        ["FluentAssertions"] = "FluentAssertions",
        ["AutoFixture"] = "AutoFixture",
        ["AutoFixture.Xunit2"] = "AutoFixture.Xunit2",
        ["AutoFixture.AutoNSubstitute"] = "AutoFixture.AutoNSubstitute",
        ["AutoFixture.AutoMoq"] = "AutoFixture.AutoMoq",
        ["Bogus"] = "Bogus",
        ["Shouldly"] = "Shouldly",
        ["FakeItEasy"] = "FakeItEasy",
        ["Newtonsoft.Json"] = "Newtonsoft.Json",
        ["RichardSzalay.MockHttp"] = "RichardSzalay.MockHttp",
        ["WireMock"] = "WireMock.Net",
        ["WireMock.RequestBuilders"] = "WireMock.Net",
        ["WireMock.ResponseBuilders"] = "WireMock.Net",
        ["Respawn"] = "Respawn",
        ["DotNet.Testcontainers"] = "Testcontainers",
    };

    /// <summary>
    /// Runs the full workflow: determine the solution directory, check/create the
    /// project (including a ProjectReference to the source project), write the test
    /// class and validate compilation. If compilation fails because a NuGet package
    /// referenced via a "using ...;" directive in <paramref name="testClassCode"/> is
    /// missing from the test project - whether that project was just created or
    /// already existed - the missing package(s) are added automatically and the
    /// build is retried (see <see cref="TryResolveMissingPackagesAndRebuildAsync"/>).
    /// </summary>
    /// <param name="sourceFilePath">Path to the original C# file.</param>
    /// <param name="testClassCode">The complete C# code of the test class.</param>
    /// <param name="testProjectName">Name of the generated/expected test project (and its .csproj).</param>
    /// <param name="testsRelativeSubPath">
    /// Path, relative to the solution directory, under which the test project lives
    /// (e.g. "tests/UnitTests"). Accepts '/' or '\' as separators.
    /// </param>
    /// <param name="testTemplate">The "dotnet new" template short name used to scaffold a new test project (e.g. "xunit", "nunit", "mstest").</param>
    /// <param name="targetFramework">Optional explicit target framework (e.g. "net10.0") passed to "dotnet new" via --framework. Null uses the template default.</param>
    /// <param name="generatedClassNamePrefix">Prefix used for the fallback file name when no class name could be extracted from <paramref name="testClassCode"/>.</param>
    public async Task<TestGenerationResult> SetupAndValidateTestAsync(
        string sourceFilePath,
        string testClassCode,
        string testProjectName = "UnitTestProject",
        string testsRelativeSubPath = "tests/UnitTests",
        string testTemplate = "xunit",
        string? targetFramework = null,
        string generatedClassNamePrefix = "GeneratedTest_",
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

        if (string.IsNullOrWhiteSpace(testProjectName))
        {
            return new TestGenerationResult(false, "The test project name must not be empty.");
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
            string testClassName = ExtractClassName(testClassCode, generatedClassNamePrefix);

            // 3. Determine the path for the test project.
            string testProjectDir = CombineUnderSolution(solutionDirectory, testsRelativeSubPath, testProjectName);
            string testProjectPath = Path.Combine(testProjectDir, $"{testProjectName}.csproj");

            // 4. Determine the project that contains the source file so that a
            //    ProjectReference can be added.
            string? sourceProjectPath = FindContainingProject(sourceFilePath, solutionDirectory);

            // 5. Create the test project if it does not exist.
            if (!File.Exists(testProjectPath))
            {
                var createResult = await CreateTestProjectAsync(
                    solutionDirectory, testProjectDir, testProjectName, testTemplate, targetFramework,
                    sourceProjectPath, cancellationToken);
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

            // 7a. If compilation failed, this may simply be because the test class
            // "using"s a namespace (e.g. NSubstitute, FluentAssertions, ...) whose
            // NuGet package is not yet referenced by the test project - regardless
            // of whether that project was just scaffolded above or already existed.
            // Try to detect and add the missing package(s) and rebuild.
            if (!buildResult.IsSuccess)
            {
                buildResult = await TryResolveMissingPackagesAndRebuildAsync(
                    testProjectPath, testProjectDir, testClassCode, buildResult, cancellationToken);
            }

            if (!buildResult.IsSuccess)
            {
                RestoreOrDeleteTestFile(testClassPath, isNewFile, previousContent);
                return buildResult;
            }

            string successMessage = $"Test class successfully created and validated in {testClassPath}.";
            if (!string.IsNullOrEmpty(buildResult.Message) &&
                !buildResult.Message.StartsWith("Compilation succeeded", StringComparison.Ordinal))
            {
                successMessage += " " + buildResult.Message;
            }

            return new TestGenerationResult(true, successMessage);
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
    /// Given a build that failed with <paramref name="failedBuildResult"/>, repeatedly:
    /// (1) extracts the namespaces the test class "using"s, (2) checks the compiler
    /// errors for CS0246/CS0234 ("type or namespace could not be found") diagnostics
    /// that reference one of those namespaces, (3) tries to add the corresponding NuGet
    /// package via "dotnet add package" (which is Central-Package-Management aware on
    /// modern SDKs, so it works the same whether the project uses CPM or not), and
    /// (4) rebuilds. Stops once the build succeeds, no further missing namespace can be
    /// matched to a package, or <see cref="MaxPackageResolutionIterations"/> is reached.
    /// </summary>
    private async Task<TestGenerationResult> TryResolveMissingPackagesAndRebuildAsync(
        string testProjectPath,
        string testProjectDir,
        string testClassCode,
        TestGenerationResult failedBuildResult,
        CancellationToken ct)
    {
        var usingNamespaces = ExtractUsingNamespaces(testClassCode);
        if (usingNamespaces.Count == 0)
        {
            return failedBuildResult;
        }

        var attemptedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolvedNamespaces = new List<string>();
        var addedPackages = new List<string>();
        var currentResult = failedBuildResult;

        for (int iteration = 0; iteration < MaxPackageResolutionIterations; iteration++)
        {
            var candidateNamespaces = ExtractMissingNamespaceCandidates(currentResult.CompilerErrors ?? Array.Empty<string>(), usingNamespaces)
                .Where(ns => !unresolvedNamespaces.Contains(ns, StringComparer.Ordinal))
                .ToList();

            if (candidateNamespaces.Count == 0)
            {
                break;
            }

            bool anyPackageAddedThisRound = false;

            foreach (var ns in candidateNamespaces)
            {
                string? addedPackageId = await ResolveAndAddPackageAsync(testProjectPath, testProjectDir, ns, attemptedPackageIds, ct);
                if (addedPackageId is not null)
                {
                    addedPackages.Add(addedPackageId);
                    anyPackageAddedThisRound = true;
                }
                else
                {
                    unresolvedNamespaces.Add(ns);
                }
            }

            if (!anyPackageAddedThisRound)
            {
                // Nothing new was added in this round, so re-running the build
                // would only reproduce the same errors - stop here.
                break;
            }

            currentResult = await ValidateProjectCompilesAsync(testProjectPath, ct);
            if (currentResult.IsSuccess)
            {
                break;
            }
        }

        var distinctAdded = addedPackages.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var distinctUnresolved = unresolvedNamespaces.Distinct(StringComparer.Ordinal).ToList();

        if (currentResult.IsSuccess)
        {
            string message = distinctAdded.Count > 0
                ? $"Compilation succeeded after automatically adding the missing NuGet package(s): {string.Join(", ", distinctAdded)}."
                : currentResult.Message;
            return new TestGenerationResult(true, message);
        }

        if (distinctAdded.Count == 0 && distinctUnresolved.Count == 0)
        {
            // Nothing package-related was detected; the compile error has another cause.
            return currentResult;
        }

        var notes = new List<string>();
        if (distinctAdded.Count > 0)
        {
            notes.Add($"Automatically added NuGet package(s): {string.Join(", ", distinctAdded)}.");
        }
        if (distinctUnresolved.Count > 0)
        {
            notes.Add($"Could not automatically resolve a NuGet package for the namespace(s): {string.Join(", ", distinctUnresolved)}. Add the required PackageReference manually.");
        }

        return new TestGenerationResult(
            isSuccess: false,
            message: currentResult.Message,
            compilerErrors: (currentResult.CompilerErrors ?? Array.Empty<string>()).Concat(notes).ToArray(),
            exceptionDetails: currentResult.ExceptionDetails);
    }

    /// <summary>
    /// Extracts the namespaces referenced by ordinary ("using X.Y.Z;") directives in
    /// <paramref name="code"/>. Alias directives ("using Foo = Bar.Baz;"), "using static ...;"
    /// and the "System" root namespace itself are deliberately excluded.
    /// </summary>
    private static List<string> ExtractUsingNamespaces(string code)
    {
        var codeWithoutLineComments = string.Join(
            "\n",
            code.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

        var result = Regex.Matches(
                codeWithoutLineComments,
                @"^\s*using\s+(?!static\s)([A-Za-z_][A-Za-z0-9_.]*)\s*;",
                RegexOptions.Multiline | RegexOptions.Compiled)
            .Select(m => m.Groups[1].Value)
            .Where(ns => !string.Equals(ns, "System", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();


        return result;
    }

    /// <summary>
    /// Cross-references CS0246 / CS0234 ("type or namespace could not be found")
    /// compiler diagnostics against <paramref name="usingNamespaces"/> and returns
    /// the subset of those namespaces that the errors point to - either because the
    /// full namespace or one of its dot-separated segments appears as the quoted
    /// identifier in an error message.
    /// </summary>
    private static List<string> ExtractMissingNamespaceCandidates(IReadOnlyList<string> compilerErrors, IReadOnlyList<string> usingNamespaces)
    {
        var missingIdentifiers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in compilerErrors)
        {
            if (!(line.Contains("CS0246", StringComparison.OrdinalIgnoreCase) ||
                  line.Contains("CS0234", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (Match m in Regex.Matches(line, "'([^']+)'"))
            {
                missingIdentifiers.Add(m.Groups[1].Value);
            }
        }

        if (missingIdentifiers.Count == 0)
        {
            return new List<string>();
        }

        return usingNamespaces
            .Where(ns => missingIdentifiers.Contains(ns) ||
                         ns.Split('.').Any(segment => missingIdentifiers.Contains(segment)))
            .ToList();
    }

    /// <summary>
    /// Tries each candidate NuGet package id for <paramref name="namespaceName"/> (see
    /// <see cref="GetPackageCandidates"/>) via "dotnet add package" followed by an explicit
    /// restore, until one succeeds. Package ids already attempted in this run (successfully
    /// or not) are tracked in <paramref name="attemptedPackageIds"/> and skipped. Returns the
    /// package id that was successfully added, or null if none of the candidates worked.
    /// </summary>
    private async Task<string?> ResolveAndAddPackageAsync(
        string testProjectPath,
        string testProjectDir,
        string namespaceName,
        HashSet<string> attemptedPackageIds,
        CancellationToken ct)
    {
        foreach (var candidate in GetPackageCandidates(namespaceName))
        {
            if (!attemptedPackageIds.Add(candidate))
            {
                continue;
            }

            var addResult = await RunDotNetCliAsync(
                new[] { "add", testProjectPath, "package", candidate, "--no-restore" },
                testProjectDir,
                ct);

            if (addResult.ExitCode != 0)
            {
                continue;
            }

            var restoreResult = await RunDotNetCliAsync(new[] { "restore", testProjectPath }, testProjectDir, ct);
            if (restoreResult.ExitCode == 0)
            {
                return candidate;
            }

            // The package id exists but could not actually be restored (e.g. no
            // version compatible with the target framework) - undo and move on.
            await RunDotNetCliAsync(new[] { "remove", testProjectPath, "package", candidate }, testProjectDir, ct);
        }

        return null;
    }

    /// <summary>
    /// Yields NuGet package id candidates for a namespace, in order of preference:
    /// first any explicit mapping in <see cref="KnownNamespaceToPackageMap"/> for the full
    /// namespace, then the namespace itself progressively shortened from the right
    /// (e.g. "NSubstitute.ExceptionExtensions" -> "NSubstitute.ExceptionExtensions" ->
    /// "NSubstitute"), consulting the map for each shortened form as well, since most
    /// package ids equal their root namespace.
    /// </summary>
    private IEnumerable<string> GetPackageCandidates(string namespaceName)
    {
        if (KnownNamespaceToPackageMap.TryGetValue(namespaceName, out var mapped))
        {
            yield return mapped;
        }

        var segments = namespaceName.Split('.');
        for (int len = segments.Length; len >= 1; len--)
        {
            var candidate = string.Join(".", segments.Take(len));
            yield return KnownNamespaceToPackageMap.TryGetValue(candidate, out var mappedPrefix)
                ? mappedPrefix
                : candidate;
        }
    }

    /// <summary>
    /// Combines the solution directory with a relative sub-path (accepting both
    /// '/' and '\' as separators, independent of the host OS) and the project name.
    /// </summary>
    private static string CombineUnderSolution(string solutionDirectory, string relativeSubPath, string projectName)
    {
        var segments = (relativeSubPath ?? string.Empty)
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

        var allSegments = new List<string> { solutionDirectory };
        allSegments.AddRange(segments);
        allSegments.Add(projectName);

        return Path.Combine(allSegments.ToArray());
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
    private static string ExtractClassName(string classCode, string generatedNamePrefix)
    {
        var codeWithoutLineComments = string.Join(
            "\n",
            classCode.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

        var match = Regex.Match(codeWithoutLineComments, @"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        string fallback = $"{generatedNamePrefix}{Guid.NewGuid():N}";
        return fallback.Length > 40 ? fallback.Substring(0, 40) : fallback;
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

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> looking for the nearest
    /// "Directory.Packages.props" file (NuGet Central Package Management).
    /// </summary>
    private static string? FindDirectoryPackagesProps(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Directory.Packages.props");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// "dotnet new" test templates (xunit/nunit/mstest) generate PackageReference
    /// items with an explicit Version attribute. If the solution uses NuGet
    /// Central Package Management (a "Directory.Packages.props" with
    /// ManagePackageVersionsCentrally=true above the project), that explicit
    /// Version causes a hard restore failure ("NU1008: ... cannot define a value
    /// for Version ... Projects using Central Package Management must define a
    /// Version value on a PackageVersion item").
    /// <para>
    /// This makes the freshly created project CPM-compliant by stripping the
    /// Version attribute from its PackageReference items and, for any package
    /// that has no corresponding entry yet, adding a PackageVersion item to
    /// Directory.Packages.props (using the version the template originally
    /// requested). Existing PackageVersion entries (and therefore other
    /// projects in the solution) are never modified.
    /// Does nothing if CPM is not in use for this project.
    /// </para>
    /// </summary>
    private static async Task FixCentralPackageManagementCompatibilityAsync(string csprojPath, CancellationToken ct)
    {
        string? propsPath = FindDirectoryPackagesProps(Path.GetDirectoryName(csprojPath)!);
        if (propsPath is null) return;

        var propsDoc = XDocument.Parse(await ReadAllTextAsyncCompat(propsPath, ct));
        if (propsDoc.Root is null) return;

        bool cpmEnabled = propsDoc.Root
            .Descendants("ManagePackageVersionsCentrally")
            .Any(e => string.Equals(e.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase));
        if (!cpmEnabled) return;

        var csprojDoc = XDocument.Parse(await ReadAllTextAsyncCompat(csprojPath, ct));
        if (csprojDoc.Root is null) return;

        var packageRefs = csprojDoc.Root.Descendants("PackageReference").ToList();
        if (packageRefs.Count == 0) return;

        var existingVersions = new HashSet<string>(
            propsDoc.Root.Descendants("PackageVersion")
                .Select(e => (string?)e.Attribute("Include"))
                .Where(v => !string.IsNullOrEmpty(v))!,
            StringComparer.OrdinalIgnoreCase);

        bool csprojChanged = false;
        bool propsChanged = false;
        XElement? targetItemGroup = null;

        foreach (var packageRef in packageRefs)
        {
            string? include = (string?)packageRef.Attribute("Include");
            var versionAttr = packageRef.Attribute("Version");
            string? version = versionAttr?.Value;

            if (string.IsNullOrEmpty(include)) continue;

            if (versionAttr is not null)
            {
                versionAttr.Remove();
                csprojChanged = true;
            }

            if (!string.IsNullOrEmpty(version) && !existingVersions.Contains(include!))
            {
                if (targetItemGroup is null)
                {
                    targetItemGroup = propsDoc.Root.Elements("ItemGroup")
                        .FirstOrDefault(g => g.Elements("PackageVersion").Any());

                    if (targetItemGroup is null)
                    {
                        targetItemGroup = new XElement("ItemGroup");
                        propsDoc.Root.Add(targetItemGroup);
                    }
                }

                targetItemGroup.Add(new XElement(
                    "PackageVersion",
                    new XAttribute("Include", include!),
                    new XAttribute("Version", version!)));

                existingVersions.Add(include!);
                propsChanged = true;
            }
        }

        if (csprojChanged)
        {
            csprojDoc.Save(csprojPath);
        }

        if (propsChanged)
        {
            propsDoc.Save(propsPath);
        }
    }

    private async Task<TestGenerationResult> CreateTestProjectAsync(
        string solutionDirectory,
        string directory,
        string projectName,
        string testTemplate,
        string? targetFramework,
        string? sourceProjectPath,
        CancellationToken ct)
    {
        Directory.CreateDirectory(directory);

        var newArgs = new List<string> { "new", testTemplate, "-n", projectName, "-o", "." };
        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            newArgs.Add("--framework");
            newArgs.Add(targetFramework!);
        }

        // NOTE: RunDotNetCliAsync automatically performs an explicit "dotnet restore"
        // right after a successful "dotnet new" call (see there for details), so the
        // NuGet packages of the freshly created project are guaranteed to be restored
        // once this call returns.
        var newResult = await RunDotNetCliAsync(newArgs, directory, ct);

        if (newResult.ExitCode != 0)
        {
            return new TestGenerationResult(
                false,
                "Failed to create the test project or restore its NuGet packages via the .NET CLI.",
                compilerErrors: newResult.Errors.Concat(newResult.Output).ToArray());
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

        // Delete the default generated sample test file if present.
        string sampleTestClassFile = Path.Combine(directory, DefaultSampleFileName);
        if (File.Exists(sampleTestClassFile)) File.Delete(sampleTestClassFile);

        return new TestGenerationResult(true, "Test project created successfully.");
    }

    private async Task<TestGenerationResult> ValidateProjectCompilesAsync(string projectPath, CancellationToken ct)
    {
        string projectDir = Path.GetDirectoryName(projectPath)!;
        string errorLogPath = Path.Combine(projectDir, $"build_errors_{Guid.NewGuid():N}.log");

        try
        {
            // Force a full rebuild so that stale artifacts cannot hide compile errors.
            // "-t:Rebuild" is stronger than "--no-incremental" and guarantees the
            // compiler actually runs against the current sources.
            //
            // In addition to the normal console output we ask MSBuild for a
            // dedicated, errors-only file log ("-flp:errorsonly;..."). Parsing
            // that file is far more reliable than scraping the console output:
            // verbosity settings, NuGet's implicit-restore noise, localized
            // summaries or interleaved stdout/stderr lines can otherwise cause
            // real "error CSxxxx" lines to be missed.
            var result = await RunDotNetCliAsync(
                new[]
                {
                    "build", projectPath, "-t:Rebuild", "--nologo", "-v:quiet",
                    $"-flp:errorsonly;logfile={errorLogPath};verbosity=normal"
                },
                projectDir,
                ct);

            string[] fileLoggerErrors = Array.Empty<string>();
            if (File.Exists(errorLogPath))
            {
                var logContent = await ReadAllTextAsyncCompat(errorLogPath, ct);
                fileLoggerErrors = logContent
                    .Split('\n')
                    .Select(l => l.TrimEnd('\r'))
                    .Where(l => l.Length > 0)
                    .ToArray();
            }

            if (result.ExitCode != 0)
            {
                var allLines = result.Errors.Concat(result.Output).ToArray();

                // The .NET CLI is forced to English (DOTNET_CLI_UI_LANGUAGE=en / VSLANG=1033)
                // inside RunDotNetCliAsync, so "error CSxxxx" is the expected form. The
                // regex fallback also catches other diagnostic sources (NuGet "NUxxxx",
                // MSBuild "MSBxxxx", etc.) and any residual localization.
                var compilerErrors = fileLoggerErrors.Length > 0
                    ? fileLoggerErrors
                    : allLines
                        .Where(line =>
                            line.Contains("error CS", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("Fehler CS", StringComparison.OrdinalIgnoreCase) ||
                            Regex.IsMatch(line, @"\b(error|Fehler)\s+[A-Za-z]+\d+", RegexOptions.IgnoreCase))
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
        finally
        {
            try
            {
                if (File.Exists(errorLogPath)) File.Delete(errorLogPath);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }


    private async Task<TestGenerationResult> YValidateProjectCompilesAsync(string projectPath, CancellationToken ct)
    {
        string projectDir = Path.GetDirectoryName(projectPath)!;

        var result = await RunDotNetCliAsync(
            new[] { "build", projectPath, "-t:Rebuild", "--nologo", "-v:minimal", "-p:UseSharedCompilation=false" },
            projectDir,
            ct);

        var allLines = (result.Errors ?? Array.Empty<string>())
            .Concat(result.Output ?? Array.Empty<string>())
            .ToArray();

        var compilerErrors = allLines
            .Where(line => line.Contains("error CS", StringComparison.OrdinalIgnoreCase) ||
                           line.Contains("Fehler CS", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Trim())
            .ToArray();

        if (result.ExitCode != 0 || compilerErrors.Length > 0)
        {
            // Kombiniert die Compiler-Fehler und alle CLI-Zeilen getrennt durch visuelle Marker
            var combinedOutput = new List<string>();

            if (compilerErrors.Length > 0)
            {
                combinedOutput.Add("=== GEFILTERTE COMPILER FEHLER ===");
                combinedOutput.AddRange(compilerErrors);
                combinedOutput.Add(""); // Leerzeile zur Trennung
            }

            combinedOutput.Add("=== VOLLSTÄNDIGER CLI OUTPUT (ALL LINES) ===");
            combinedOutput.AddRange(allLines.Length > 0 ? allLines : new[] { "Kein CLI Output vorhanden." });

            return new TestGenerationResult(
                isSuccess: false,
                message: "The test class could not be compiled (syntax, namespace, or reference error).",
                compilerErrors: combinedOutput.ToArray()
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
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
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

    private static bool ContainsCommand(IReadOnlyList<string> arguments, string command)
        => arguments.Any(a => string.Equals(a, command, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs a "dotnet" CLI command and captures its output.
    /// <para>
    /// Whenever the given arguments contain a "dotnet new ..." project-creation
    /// command and it succeeds, this method first makes the created project
    /// compatible with NuGet Central Package Management if the solution uses it
    /// (see <see cref="FixCentralPackageManagementCompatibilityAsync"/>), and then
    /// automatically issues a subsequent, explicit "dotnet restore" for the
    /// freshly created .csproj. "dotnet new" performs an implicit restore itself,
    /// but that step can silently fail or be skipped (custom templates,
    /// offline/authenticated feeds, "--no-restore", CPM conflicts, etc.), which
    /// would otherwise surface later as a confusing compile error instead of a
    /// clear restore error. Callers therefore never need to worry about restoring
    /// packages for a newly created test project themselves.
    /// </para>
    /// </summary>
    private async Task<(int ExitCode, string[] Output, string[] Errors)> RunDotNetCliAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var primaryResult = await ExecuteDotNetProcessAsync(arguments, workingDirectory, cancellationToken, timeout);

        if (primaryResult.ExitCode == 0 && ContainsCommand(arguments, "new"))
        {
            string? createdProject = Directory.EnumerateFiles(workingDirectory, "*.csproj").FirstOrDefault();
            if (createdProject is not null)
            {
                // If the solution uses NuGet Central Package Management, the template's
                // PackageReference "Version" attributes would otherwise make the restore
                // below fail with NU1008. Fix that up first.
                await FixCentralPackageManagementCompatibilityAsync(createdProject, cancellationToken);

                var restoreResult = await ExecuteDotNetProcessAsync(
                    new[] { "restore", createdProject }, workingDirectory, cancellationToken, timeout);

                var combinedOutput = primaryResult.Output.Concat(restoreResult.Output).ToArray();
                var combinedErrors = primaryResult.Errors.Concat(restoreResult.Errors).ToArray();

                return (restoreResult.ExitCode != 0 ? restoreResult.ExitCode : 0, combinedOutput, combinedErrors);
            }
        }

        return primaryResult;
    }

    private async Task<(int ExitCode, string[] Output, string[] Errors)> ExecuteDotNetProcessAsync(
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        TimeSpan? timeout)
    {
        var argumentsString = BuildArgumentString(arguments.ToList());

        var startInfo = new ProcessStartInfo
        {
            FileName = _dotnetExecutable,
            Arguments = argumentsString,
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

        using var timeoutCts = new CancellationTokenSource(timeout ?? _defaultProcessTimeout);
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
                $"The process '{_dotnetExecutable} {argumentsString}' was aborted after " +
                $"{(timeout ?? _defaultProcessTimeout).TotalSeconds}s (timeout).");
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