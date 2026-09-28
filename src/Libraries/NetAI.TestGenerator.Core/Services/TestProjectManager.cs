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

public class TestProjectManager
{
    private const string DefaultSampleFileName = "UnitTest1.cs";
    private const string DefaultWindowsFramework = "net10.0-windows";
    private const int MaxPackageResolutionIterations = 5;

    private readonly TimeSpan _defaultProcessTimeout;
    private readonly string _dotnetExecutable;

    public TestProjectManager(TimeSpan? defaultProcessTimeout = null, string dotnetExecutable = "dotnet")
    {
        _defaultProcessTimeout = defaultProcessTimeout ?? TimeSpan.FromMinutes(5);
        _dotnetExecutable = string.IsNullOrWhiteSpace(dotnetExecutable) ? "dotnet" : dotnetExecutable;
    }

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
            string? solutionDirectory = FindSolutionDirectory(sourceFilePath);
            if (solutionDirectory is null)
            {
                return new TestGenerationResult(
                    false,
                    $"No .sln or .slnx file was found above '{sourceFilePath}'.");
            }

            string testClassName = ExtractClassName(testClassCode, generatedClassNamePrefix);

            string testProjectDir = CombineUnderSolution(solutionDirectory, testsRelativeSubPath, testProjectName);
            string testProjectPath = Path.Combine(testProjectDir, $"{testProjectName}.csproj");

            string? sourceProjectPath = FindContainingProject(sourceFilePath, solutionDirectory);

            if (!File.Exists(testProjectPath))
            {
                var createResult = await CreateTestProjectAsync(
                    solutionDirectory, testProjectDir, testProjectName, testTemplate, targetFramework,
                    sourceProjectPath, cancellationToken);
                if (!createResult.IsSuccess) return createResult;
            }
            else if (sourceProjectPath is not null)
            {
                // TFM / UseWPF must match BEFORE the reference is added, otherwise NU1201 occurs.
                await EnsureWindowsSettingsAsync(testProjectPath, sourceProjectPath, targetFramework, cancellationToken);

                if (!await ProjectHasReferenceAsync(testProjectPath, sourceProjectPath, cancellationToken))
                {
                    await RunDotNetCliAsync(
                        new[] { "add", testProjectPath, "reference", sourceProjectPath },
                        testProjectDir,
                        cancellationToken);
                }
            }

            string testClassPath = Path.Combine(testProjectDir, $"{SafeFileName(testClassName)}.cs");
            bool isNewFile = !File.Exists(testClassPath);
            string? previousContent = isNewFile ? null : await ReadAllTextAsyncCompat(testClassPath, cancellationToken);
            await WriteAllTextAsyncCompat(testClassPath, testClassCode, Encoding.UTF8, cancellationToken);

            var buildResult = await ValidateProjectCompilesAsync(testProjectPath, cancellationToken);


            if (buildResult.CompilerErrors.Length > 0)
            {
                var errorslll = string.Join("\n", buildResult.CompilerErrors.ToList());
            }

            // AI-generated code often forgets 'using' directives (app namespace, System.Windows.Controls, Moq ...).
            // Add the ones we can resolve unambiguously and rebuild. This also lets the NuGet
            // resolver below see e.g. 'using Moq;'.
            if (!buildResult.IsSuccess)
            {
                string? fixedCode = await TryAddMissingUsingsAsync(
                    testClassPath, sourceProjectPath, buildResult.CompilerErrors ?? Array.Empty<string>(), cancellationToken);

                if (fixedCode is not null)
                {
                    testClassCode = fixedCode;
                    buildResult = await ValidateProjectCompilesAsync(testProjectPath, cancellationToken);
                }
            }

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

    private static readonly IReadOnlyDictionary<string, string> WellKnownTypeToNamespace =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // xUnit
            ["Assert"] = "Xunit",
            ["Fact"] = "Xunit",
            ["Theory"] = "Xunit",
            ["InlineData"] = "Xunit",
            // Moq
            ["Mock"] = "Moq",
            ["It"] = "Moq",
            ["Times"] = "Moq",
            ["MockBehavior"] = "Moq",
            // BCL
            ["IServiceProvider"] = "System",
            ["Task"] = "System.Threading.Tasks",
            // WPF
            ["Application"] = "System.Windows",
            ["Window"] = "System.Windows",
            ["RoutedEventArgs"] = "System.Windows",
            ["Visibility"] = "System.Windows",
            ["Thickness"] = "System.Windows",
            ["FrameworkElement"] = "System.Windows",
            ["DependencyObject"] = "System.Windows",
            ["Control"] = "System.Windows.Controls",
            ["ContentControl"] = "System.Windows.Controls",
            ["UserControl"] = "System.Windows.Controls",
            ["Button"] = "System.Windows.Controls",
            ["TextBox"] = "System.Windows.Controls",
            ["TextBlock"] = "System.Windows.Controls",
            ["Label"] = "System.Windows.Controls",
            ["CheckBox"] = "System.Windows.Controls",
            ["ComboBox"] = "System.Windows.Controls",
            ["ListBox"] = "System.Windows.Controls",
            ["Grid"] = "System.Windows.Controls",
            ["StackPanel"] = "System.Windows.Controls",
            ["Panel"] = "System.Windows.Controls",
            ["Dispatcher"] = "System.Windows.Threading",
        };

    /// <summary>
    /// Resolves CS0246 / CS0103 errors caused by missing 'using' directives: types declared in the
    /// source project are mapped via their namespace, common framework types via a static table.
    /// Only unambiguous matches are added. Returns the new file content, or null if nothing changed.
    /// </summary>
    private async Task<string?> TryAddMissingUsingsAsync(
        string testClassPath,
        string? sourceProjectPath,
        IReadOnlyList<string> compilerErrors,
        CancellationToken ct)
    {
        var missing = ExtractMissingIdentifiers(compilerErrors);
        if (missing.Count == 0) return null;

        var sourceTypes = sourceProjectPath is null
            ? new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            : await Task.Run(() => ScanTypeNamespaces(sourceProjectPath), ct);

        string code = await ReadAllTextAsyncCompat(testClassPath, ct);
        var toAdd = new List<string>();

        foreach (var identifier in missing)
        {
            string? ns = null;

            if (sourceTypes.TryGetValue(identifier, out var namespaces))
            {
                if (namespaces.Count == 1) ns = namespaces.First();
            }
            else if (WellKnownTypeToNamespace.TryGetValue(identifier, out var wellKnown))
            {
                ns = wellKnown;
            }

            if (ns is not null &&
                !toAdd.Contains(ns, StringComparer.Ordinal) &&
                !Regex.IsMatch(code, @"^\s*using\s+" + Regex.Escape(ns) + @"\s*;", RegexOptions.Multiline))
            {
                toAdd.Add(ns);
            }
        }

        if (toAdd.Count == 0) return null;

        string header = string.Concat(toAdd.Select(n => $"using {n};{Environment.NewLine}"));
        string newCode = header + code;

        await WriteAllTextAsyncCompat(testClassPath, newCode, Encoding.UTF8, ct);
        return newCode;
    }

    private static List<string> ExtractMissingIdentifiers(IReadOnlyList<string> compilerErrors)
    {
        var result = new List<string>();

        foreach (var line in compilerErrors)
        {
            if (line.IndexOf("CS0246", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("CS0103", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            foreach (Match m in Regex.Matches(line, "'([^']+)'"))
            {
                // 'Mock<>' -> 'Mock'
                string name = Regex.Replace(m.Groups[1].Value, "<.*>$", string.Empty);
                if (name.Length > 0 &&
                    name.IndexOf('.') < 0 &&
                    !result.Contains(name, StringComparer.Ordinal))
                {
                    result.Add(name);
                }
            }
        }

        return result;
    }

    private static Dictionary<string, HashSet<string>> ScanTypeNamespaces(string sourceProjectPath)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        string projectDir = Path.GetDirectoryName(sourceProjectPath)!;
        string sep = Path.DirectorySeparatorChar.ToString();

        var namespaceRegex = new Regex(@"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.Multiline);
        var typeRegex = new Regex(
            @"^\s*(?:(?:public|internal|sealed|static|abstract|partial|readonly|unsafe)\s+)*" +
            @"(?:record\s+(?:class|struct)|class|struct|interface|enum|record)\s+([A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Multiline);

        foreach (var file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
        {
            string relative = file.Substring(projectDir.Length);
            if (relative.Contains(sep + "obj" + sep) || relative.Contains(sep + "bin" + sep))
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            var nsMatch = namespaceRegex.Match(text);
            if (!nsMatch.Success) continue;

            string ns = nsMatch.Groups[1].Value;
            foreach (Match typeMatch in typeRegex.Matches(text))
            {
                string typeName = typeMatch.Groups[1].Value;
                if (!map.TryGetValue(typeName, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    map[typeName] = set;
                }
                set.Add(ns);
            }
        }

        return map;
    }

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

    private static List<string> ExtractUsingNamespaces(string code)
    {
        var codeWithoutLineComments = string.Join(
            "\n",
            code.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

        var matches = Regex.Matches(
            codeWithoutLineComments,
            @"^\s*using\s+(?!static\s)([A-Za-z_][A-Za-z0-9_.]*)\s*;",
            RegexOptions.Multiline | RegexOptions.Compiled);

        var namespaces = new List<string>();
        foreach (Match match in matches)
        {
            string ns = match.Groups[1].Value;
            if (!string.Equals(ns, "System", StringComparison.Ordinal) &&
                !namespaces.Contains(ns, StringComparer.Ordinal))
            {
                namespaces.Add(ns);
            }
        }

        return namespaces;
    }

    private static List<string> ExtractMissingNamespaceCandidates(IReadOnlyList<string> compilerErrors, IReadOnlyList<string> usingNamespaces)
    {
        var missingIdentifiers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in compilerErrors)
        {
            if (line.IndexOf("CS0246", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("CS0234", StringComparison.OrdinalIgnoreCase) < 0)
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

            await RunDotNetCliAsync(new[] { "remove", testProjectPath, "package", candidate }, testProjectDir, ct);
        }

        return null;
    }

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

    private static string CombineUnderSolution(string solutionDirectory, string relativeSubPath, string projectName)
    {
        var segments = (relativeSubPath ?? string.Empty)
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

        var allSegments = new List<string> { solutionDirectory };
        allSegments.AddRange(segments);
        allSegments.Add(projectName);

        return Path.Combine(allSegments.ToArray());
    }

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

    /// <summary>
    /// If the source project is a WPF / WinForms project (e.g. net10.0-windows + UseWPF),
    /// the test project must use the same Windows target framework and the same UseWPF /
    /// UseWindowsForms flags. Otherwise referencing the project fails (NU1201) and types like
    /// MainWindow or RoutedEventArgs are not available.
    /// </summary>
    private async Task EnsureWindowsSettingsAsync(
        string testProjectPath,
        string? sourceProjectPath,
        string? preferredFramework,
        CancellationToken ct)
    {
        if (sourceProjectPath is null || !File.Exists(sourceProjectPath)) return;

        var sourceDoc = XDocument.Parse(await ReadAllTextAsyncCompat(sourceProjectPath, ct));

        bool useWpf = HasTrueProperty(sourceDoc, "UseWPF");
        bool useWinForms = HasTrueProperty(sourceDoc, "UseWindowsForms");

        string? windowsTfm = sourceDoc.Descendants()
            .Where(e => e.Name.LocalName == "TargetFramework" || e.Name.LocalName == "TargetFrameworks")
            .SelectMany(e => e.Value.Split(';'))
            .Select(t => t.Trim())
            .FirstOrDefault(t => t.IndexOf("-windows", StringComparison.OrdinalIgnoreCase) >= 0);

        if (!useWpf && !useWinForms && windowsTfm is null) return;

        if (windowsTfm is null)
        {
            windowsTfm = preferredFramework is not null &&
                         preferredFramework.IndexOf("-windows", StringComparison.OrdinalIgnoreCase) >= 0
                ? preferredFramework
                : DefaultWindowsFramework;
        }

        var testDoc = XDocument.Parse(await ReadAllTextAsyncCompat(testProjectPath, ct));
        var root = testDoc.Root;
        if (root is null) return;

        bool changed = false;

        var propertyGroup = root.Elements("PropertyGroup").FirstOrDefault();
        if (propertyGroup is null)
        {
            propertyGroup = new XElement("PropertyGroup");
            root.AddFirst(propertyGroup);
            changed = true;
        }

        // Multi-targeting would produce duplicate errors; use a single TFM.
        foreach (var multi in root.Descendants("TargetFrameworks").ToList())
        {
            multi.Remove();
            changed = true;
        }

        changed |= SetProperty(root, propertyGroup, "TargetFramework", windowsTfm);
        if (useWpf) changed |= SetProperty(root, propertyGroup, "UseWPF", "true");
        if (useWinForms) changed |= SetProperty(root, propertyGroup, "UseWindowsForms", "true");

        if (!changed) return;

        testDoc.Save(testProjectPath);

        string dir = Path.GetDirectoryName(testProjectPath)!;
        await RunDotNetCliAsync(new[] { "restore", testProjectPath }, dir, ct);
    }

    private static bool HasTrueProperty(XDocument doc, string propertyName)
    {
        return doc.Descendants()
            .Where(e => e.Name.LocalName == propertyName)
            .Any(e => string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
    }

    private static bool SetProperty(XElement root, XElement defaultGroup, string name, string value)
    {
        var existing = root.Descendants(name).FirstOrDefault();
        if (existing is null)
        {
            defaultGroup.Add(new XElement(name, value));
            return true;
        }

        if (string.Equals(existing.Value.Trim(), value, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        existing.Value = value;
        return true;
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

        // "-windows" TFMs are not accepted by the template's --framework option;
        // EnsureWindowsSettingsAsync sets them directly in the csproj afterwards.
        if (!string.IsNullOrWhiteSpace(targetFramework) &&
            targetFramework!.IndexOf("-windows", StringComparison.OrdinalIgnoreCase) < 0)
        {
            newArgs.Add("--framework");
            newArgs.Add(targetFramework);
        }

        var newResult = await RunDotNetCliAsync(newArgs, directory, ct);

        if (newResult.ExitCode != 0)
        {
            return new TestGenerationResult(
                false,
                "Failed to create the test project or restore its NuGet packages via the .NET CLI.",
                compilerErrors: newResult.Errors.Concat(newResult.Output).ToArray());
        }

        string projectPath = Path.Combine(directory, $"{projectName}.csproj");

        var slnFiles = Directory.GetFiles(solutionDirectory, "*.sln")
            .Concat(Directory.GetFiles(solutionDirectory, "*.slnx"))
            .ToArray();

        if (slnFiles.Length > 0)
        {
            await RunDotNetCliAsync(new[] { "sln", slnFiles[0], "add", projectPath }, solutionDirectory, ct);
        }

        if (sourceProjectPath is not null)
        {
            // Must happen before "add reference" (TFM compatibility).
            await EnsureWindowsSettingsAsync(projectPath, sourceProjectPath, targetFramework, ct);

            var refResult = await RunDotNetCliAsync(new[] { "add", projectPath, "reference", sourceProjectPath }, directory, ct);

            if (refResult.ExitCode != 0)
            {
                return new TestGenerationResult(
                    false,
                    "The test project was created, but the reference to the source project could not be set.",
                    compilerErrors: refResult.Errors.Concat(refResult.Output).ToArray());
            }
        }

        string sampleTestClassFile = Path.Combine(directory, DefaultSampleFileName);
        if (File.Exists(sampleTestClassFile)) File.Delete(sampleTestClassFile);

        return new TestGenerationResult(true, "Test project created successfully.");
    }

    /// <summary>
    /// Validates the test project by running a real "dotnet build". This guarantees that all
    /// package, project and WPF references are resolved exactly as they will be at test time.
    /// </summary>
    private async Task<TestGenerationResult> ValidateProjectCompilesAsync(
        string testProjectPath,
        CancellationToken cancellationToken)
    {
        try
        {
            string projectDir = Path.GetDirectoryName(testProjectPath)!;

            var result = await RunDotNetCliAsync(
                new[]
                {
                    "build", testProjectPath,
                    "--nologo",
                    "-v", "q",
                    "-p:GenerateFullPaths=true"
                },
                projectDir,
                cancellationToken);

            if (result.ExitCode == 0)
            {
                return new TestGenerationResult(true, "Compilation succeeded.");
            }

            var allLines = result.Output.Concat(result.Errors).ToArray();

            // Format: C:\...\File.cs(6,6): error CS0246: The type or namespace name 'Xunit' ... [proj.csproj]
            var errorRegex = new Regex(
                @"^(?<file>.*?)\((?<line>\d+),\d+\):\s*error\s+(?<id>[A-Za-z]+\d+):\s*(?<msg>.*?)(\s+\[[^\]]+\])?\s*$",
                RegexOptions.Compiled);

            var errors = allLines
                .Select(l => errorRegex.Match(l))
                .Where(m => m.Success)
                .Select(m => $"error {m.Groups["id"].Value}: {m.Groups["msg"].Value} " +
                             $"({Path.GetFileName(m.Groups["file"].Value)}, line {m.Groups["line"].Value})")
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (errors.Length == 0)
            {
                // e.g. restore errors (NU1xxx) without file/line information
                errors = allLines
                    .Where(l => l.IndexOf(" error ", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }

            if (errors.Length == 0)
            {
                errors = allLines.Where(l => !string.IsNullOrWhiteSpace(l)).Take(30).ToArray();
            }

            return new TestGenerationResult(false, "Compilation failed.", compilerErrors: errors);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TestGenerationResult(
                false,
                $"Build validation failed: {ex.Message}",
                exceptionDetails: FormatExceptionDetails(ex));
        }
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
                process.Exited -= OnExited;
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
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // English output so the error parsing (regex, 'Namespace' detection) works reliably.
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        startInfo.Environment["VSLANG"] = "1033";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

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