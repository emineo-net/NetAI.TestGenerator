using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NetAI.TestGenerator.Core.Models.Enums;

namespace DotNet10TestGenerator;

/// <summary>Creates test projects, manages source references, and validates generated tests with the .NET CLI.</summary>
public class TestProjectManager
{
    private const string DefaultSampleFileName = "UnitTest1.cs";
    private const string DefaultWindowsFramework = "net10.0-windows";
    private const int MaxPackageResolutionIterations = 5;
    private const string StaFactPackageId = "Xunit.StaFact";

    private static readonly Regex InternalsVisibleToMemberRegex = new(
        @"'(?<type>[A-Za-z_][A-Za-z0-9_]*)'\s+does not contain a definition for\s+'(?<member>[A-Za-z_][A-Za-z0-9_]*)'",
        RegexOptions.Compiled);
    private static readonly Regex SemanticCs1503Regex = new(
        @"CS1503.*?Argument\s+(?<n>\d+).*?cannot convert from\s+'(?<from>[^']+)'\s+to\s+'(?<to>[^']+)'",
        RegexOptions.Compiled);
    private static readonly Regex SemanticCs1061Regex = new(
        @"'(?<type>[A-Za-z_][A-Za-z0-9_]*)'\s+does not contain a definition for\s+'(?<member>[A-Za-z_][A-Za-z0-9_]*)'",
        RegexOptions.Compiled);
    private static readonly Regex SemanticCs1501Regex = new(
        @"CS1501.*?no overload for method\s+'(?<m>[^']+)'\s+takes\s+'(?<n>\d+)'\s+arguments",
        RegexOptions.Compiled);
    private static readonly Regex SemanticCs1729Regex = new(
        @"CS1729.*?'(?<t>[^']+)'\s+does not contain a constructor that takes\s+'(?<n>\d+)'",
        RegexOptions.Compiled);
    private static readonly Regex BuildDiagnosticRegex = new(
        @"^(?<file>.*?)\((?<line>\d+),\d+\):\s*error\s+(?<id>[A-Za-z]+\d+):\s*(?<msg>.*?)(\s+\[[^\]]+\])?\s*$",
        RegexOptions.Compiled);

    private static readonly HttpClient NuGetHttp = new()
    {
        BaseAddress = new Uri("https://api.nuget.org/v3-flatcontainer/"),
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly TimeSpan _defaultProcessTimeout;
    private readonly string _dotnetExecutable;
    private readonly TestFramework _testFramework;
    private readonly MockFramework _mockFramework;
    private readonly IReadOnlyDictionary<string, string> _wellKnownTypeToNamespace;

    private readonly ConcurrentDictionary<string, Task<Dictionary<string, List<TypeLocation>>>> _scanCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a manager for test project setup and compilation.</summary>
    /// <param name="defaultProcessTimeout">Default timeout for .NET CLI processes.</param>
    /// <param name="dotnetExecutable">Path or command name of the .NET CLI executable.</param>
    /// <param name="testFramework">
    /// Selected test framework. Controls which well-known test types (e.g. <c>Assert</c>,
    /// <c>[Test]</c>) are resolved to which namespace. Prevents adding <c>using Xunit;</c> to
    /// an NUnit/MSTest project.
    /// </param>
    /// <param name="mockFramework">
    /// Selected mocking framework. Only types of this framework are auto-resolved; a stray
    /// <c>Mock&lt;T&gt;</c> in an NSubstitute project will NOT silently pull in the Moq package.
    /// </param>
    public TestProjectManager(
        TimeSpan? defaultProcessTimeout = null,
        string dotnetExecutable = "dotnet",
        TestFramework testFramework = TestFramework.xUnit,
        MockFramework mockFramework = MockFramework.Unknown)
    {
        _defaultProcessTimeout = defaultProcessTimeout ?? TimeSpan.FromMinutes(5);
        _dotnetExecutable = string.IsNullOrWhiteSpace(dotnetExecutable) ? "dotnet" : dotnetExecutable;
        _testFramework = testFramework;
        _mockFramework = mockFramework;
        _wellKnownTypeToNamespace = BuildWellKnownTypeToNamespace(testFramework, mockFramework);
    }

    /// <summary>Gets namespace-to-NuGet-package mappings used to resolve missing test dependencies.</summary>
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
        ["Microsoft.Extensions.DependencyInjection"] = "Microsoft.Extensions.DependencyInjection",
        ["Microsoft.Extensions.DependencyInjection.Abstractions"] = "Microsoft.Extensions.DependencyInjection.Abstractions",
        ["Xunit.StaFact"] = "Xunit.StaFact",
    };

    /// <summary>Creates or updates a test project, writes the supplied class, and builds the project.</summary>
    /// <param name="sourceFilePath">Source file whose project should be referenced by the test project.</param>
    /// <param name="testClassCode">Complete test class source code to compile.</param>
    /// <param name="testProjectName">Name of the generated project when no directory override is supplied.</param>
    /// <param name="testsRelativeSubPath">Test project parent directory relative to the solution root.</param>
    /// <param name="testTemplate">Template passed to <c>dotnet new</c> when creating the project.</param>
    /// <param name="targetFramework">Optional target framework for the generated project.</param>
    /// <param name="generatedClassNamePrefix">Prefix used when a class name cannot be extracted from the code.</param>
    /// <param name="cancellationToken">Token used to cancel project creation and validation.</param>
    /// <param name="testProjectDirectoryOverride">Optional absolute directory that overrides the default solution-relative location.</param>
    /// <returns>Generation status, build diagnostics, and the generated test source.</returns>
    /// <remarks>The source project is discovered from <paramref name="sourceFilePath"/> and referenced automatically.</remarks>
    public async Task<TestGenerationResult> SetupAndValidateTestAsync(
        string sourceFilePath,
        string testClassCode,
        string testProjectName = "UnitTestProject",
        string testsRelativeSubPath = "tests/UnitTests",
        string testTemplate = "xunit",
        string? targetFramework = null,
        string generatedClassNamePrefix = "GeneratedTest_",
        CancellationToken cancellationToken = default,
        string? testProjectDirectoryOverride = null)
    {
        if (!File.Exists(sourceFilePath))
            return new TestGenerationResult(false, $"The source file was not found: {sourceFilePath}");
        if (string.IsNullOrWhiteSpace(testClassCode))
            return new TestGenerationResult(false, "The supplied test class code is empty.");
        string resolvedTestProjectName = string.IsNullOrWhiteSpace(testProjectDirectoryOverride)
            ? testProjectName
            : Path.GetFileName(Path.GetFullPath(testProjectDirectoryOverride).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(resolvedTestProjectName))
            return new TestGenerationResult(false, "The test project name must not be empty.");

        try
        {
            string? solutionDirectory = FindSolutionDirectory(sourceFilePath);
            if (solutionDirectory is null)
                return new TestGenerationResult(false,
                    $"No .sln or .slnx file was found above '{sourceFilePath}'.");

            string testClassName = ExtractClassName(testClassCode, generatedClassNamePrefix);
            string testProjectDir = string.IsNullOrWhiteSpace(testProjectDirectoryOverride)
                ? CombineUnderSolution(solutionDirectory, testsRelativeSubPath, resolvedTestProjectName)
                : Path.GetFullPath(testProjectDirectoryOverride);
            string testProjectPath = Path.Combine(testProjectDir, $"{resolvedTestProjectName}.csproj");

            string? sourceProjectPath = FindContainingProject(sourceFilePath, solutionDirectory);

            if (!File.Exists(testProjectPath))
            {
                var createResult = await CreateTestProjectAsync(
                    solutionDirectory, testProjectDir, resolvedTestProjectName, testTemplate, targetFramework,
                    sourceProjectPath, cancellationToken);
                if (!createResult.IsSuccess) return createResult;
            }
            else if (sourceProjectPath is not null)
            {
                await EnsureWindowsSettingsAsync(testProjectPath, sourceProjectPath, targetFramework, cancellationToken);

                if (!await ProjectHasReferenceAsync(testProjectPath, sourceProjectPath, cancellationToken))
                {
                    await RunDotNetCliAsync(
                        new[] { "add", testProjectPath, "reference", sourceProjectPath },
                        testProjectDir, cancellationToken);
                }
            }

            sourceProjectPath ??= FindContainingProject(sourceFilePath, solutionDirectory);
            if (sourceProjectPath is not null && File.Exists(sourceProjectPath))
            {
                if (!await ProjectHasReferenceAsync(testProjectPath, sourceProjectPath, cancellationToken))
                {
                    await EnsureWindowsSettingsAsync(testProjectPath, sourceProjectPath, targetFramework, cancellationToken);
                    await RunDotNetCliAsync(
                        new[] { "add", testProjectPath, "reference", sourceProjectPath },
                        testProjectDir, cancellationToken);
                    await RunDotNetCliAsync(
                        new[] { "restore", testProjectPath },
                        testProjectDir, cancellationToken);
                }
            }

            string testClassPath = Path.Combine(testProjectDir, $"{SafeFileName(testClassName)}.cs");
            await WriteAllTextAsyncCompat(testClassPath, testClassCode, Encoding.UTF8, cancellationToken);

            string currentTestClassCode = testClassCode;

            var buildResult = await ValidateProjectCompilesAsync(testProjectPath, cancellationToken);

            if (!buildResult.IsSuccess && buildResult.IsEnvironmentIssue)
            {
                return CreateEnvironmentFailureResult(
                    buildResult, testClassPath, currentTestClassCode);
            }

            if (!buildResult.IsSuccess)
            {
                bool cpmFixed = await TryFixFloatingVersionsAsync(
                    testProjectPath, buildResult.CompilerErrors ?? Array.Empty<string>(), cancellationToken);
                if (cpmFixed)
                    buildResult = await ValidateProjectCompilesAsync(testProjectPath, cancellationToken);
            }

            if (!buildResult.IsSuccess)
            {
                string? fixedCode = await TryAddMissingUsingsAsync(
                    testClassPath, testProjectPath, sourceProjectPath, solutionDirectory,
                    buildResult.CompilerErrors ?? Array.Empty<string>(), cancellationToken);

                if (fixedCode is not null)
                {
                    currentTestClassCode = fixedCode;
                    buildResult = await ValidateProjectCompilesAsync(testProjectPath, cancellationToken);
                }
            }

            if (!buildResult.IsSuccess)
            {
                buildResult = await TryResolveMissingPackagesAndRebuildAsync(
                    testProjectPath, testProjectDir, currentTestClassCode, buildResult, cancellationToken);
            }

            if (!buildResult.IsSuccess)
            {
                bool ivtAdded = await TryEnableInternalsVisibleToAsync(
                    testProjectPath, sourceProjectPath,
                    buildResult.CompilerErrors ?? Array.Empty<string>(), cancellationToken);
                if (ivtAdded)
                    buildResult = await ValidateProjectCompilesAsync(testProjectPath, cancellationToken);
            }

            if (buildResult.IsSuccess)
            {
                string successMessage = $"Test class successfully created and validated in {testClassPath}.";
                if (!string.IsNullOrEmpty(buildResult.Message) &&
                    !buildResult.Message.StartsWith("Compilation succeeded", StringComparison.Ordinal))
                {
                    successMessage += " " + buildResult.Message;
                }

                return new TestGenerationResult(
                    isSuccess: true,
                    message: successMessage,
                    testClassPath: testClassPath,
                    testClassCode: currentTestClassCode);
            }

            var finalErrors = buildResult.CompilerErrors ?? Array.Empty<string>();

            bool requiresRegeneration = HasSemanticErrors(finalErrors);

            var unresolvableTypes = await FindUnresolvableTypeNamesAsync(
                solutionDirectory, finalErrors, cancellationToken);

            if (unresolvableTypes.Count > 0)
                requiresRegeneration = true;

            string failureMessage = buildResult.Message +
                $" The generated test class was kept at '{testClassPath}'.";
            if (requiresRegeneration)
            {
                failureMessage +=
                    " The test code must be regenerated: it references members or types that do not " +
                    "exist in the target project.";
            }

            var enrichedErrors = finalErrors.ToList();

            if (requiresRegeneration)
            {
                enrichedErrors.Add("---");
                enrichedErrors.Add("These errors require regeneration of the test code. " +
                                   "The infrastructure cannot fix them.");

                if (unresolvableTypes.Count > 0)
                {
                    enrichedErrors.Add("The following type/namespace names do not exist anywhere in " +
                                       "the solution. They appear to be hallucinated – do NOT reference them:");
                    foreach (var t in unresolvableTypes)
                        enrichedErrors.Add($"• '{t}'");
                }

                var semantic = ExtractSemanticErrorSummary(finalErrors);
                if (semantic.Count > 0)
                {
                    enrichedErrors.Add("Additional hints:");
                    enrichedErrors.AddRange(semantic);
                }
            }

            return new TestGenerationResult(
                isSuccess: false,
                message: failureMessage,
                compilerErrors: enrichedErrors.ToArray(),
                exceptionDetails: buildResult.ExceptionDetails,
                requiresRegeneration: requiresRegeneration,
                isEnvironmentIssue: buildResult.IsEnvironmentIssue,
                testClassPath: testClassPath,
                testClassCode: currentTestClassCode);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new TestGenerationResult(
                false,
                $"An unexpected error occurred: {ex.Message}",
                exceptionDetails: FormatExceptionDetails(ex));
        }
    }

    private static TestGenerationResult CreateEnvironmentFailureResult(
        TestGenerationResult buildResult, string testClassPath, string currentTestClassCode)
    {
        var envErrors = (buildResult.CompilerErrors ?? Array.Empty<string>())
            .Concat(new[]
            {
                "---",
                "This is an environment issue, NOT a problem with the generated test code.",
                "The test class was written successfully and looks correct.",
                "Even the compile-only invocation " +
                "('dotnet build -t:Compile -p:BuildProjectReferences=false') was blocked.",
                "Close Visual Studio and any running instances of the tested application, then " +
                "call this method again with the SAME testClassCode. Do NOT regenerate the test code."
            })
            .ToArray();

        return new TestGenerationResult(
            isSuccess: false,
            message:
                $"Build failed due to a file lock or environment issue. The test class is fine " +
                $"and was kept at '{testClassPath}'. This is NOT a test code problem – do not " +
                $"regenerate it. Close Visual Studio and any running instances of the tested " +
                $"application, then call this method again with the same parameters.",
            compilerErrors: envErrors,
            exceptionDetails: buildResult.ExceptionDetails,
            requiresRegeneration: false,
            isEnvironmentIssue: true,
            testClassPath: testClassPath,
            testClassCode: currentTestClassCode);
    }

    private async Task<List<string>> FindUnresolvableTypeNamesAsync(
        string solutionDirectory,
        IReadOnlyList<string> compilerErrors,
        CancellationToken ct)
    {
        var candidates = ExtractMissingIdentifiers(compilerErrors);
        if (candidates.Count == 0) return new List<string>();

        foreach (var line in compilerErrors)
        {
            if (line.IndexOf("CS0234", StringComparison.OrdinalIgnoreCase) < 0) continue;
            foreach (Match m in Regex.Matches(line, "'([^']+)'"))
            {
                string name = m.Groups[1].Value;
                if (name.Length > 0 && !candidates.Contains(name, StringComparer.Ordinal))
                    candidates.Add(name);
            }
        }

        var sourceTypes = await GetOrScanAsync(solutionDirectory, ct);
        var unresolvable = new List<string>();

        foreach (var id in candidates)
        {
            if (sourceTypes.ContainsKey(id)) continue;
            if (_wellKnownTypeToNamespace.ContainsKey(id)) continue;
            unresolvable.Add(id);
        }

        return unresolvable.Distinct(StringComparer.Ordinal).ToList();
    }

    private static bool IsEnvironmentErrorRaw(IReadOnlyList<string> allLines)
    {
        foreach (var line in allLines)
        {
            bool isWarningLine =
                line.IndexOf("warning MSB", StringComparison.OrdinalIgnoreCase) >= 0 &&
                line.IndexOf("error MSB", StringComparison.OrdinalIgnoreCase) < 0;
            if (isWarningLine) continue;

            if (line.IndexOf("MSB3027", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (line.IndexOf("MSB3028", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (line.IndexOf("MSB3023", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (line.IndexOf("MSB3021", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (line.IndexOf("Exceeded retry count", StringComparison.OrdinalIgnoreCase) >= 0 &&
                line.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (line.IndexOf("The process cannot access the file", StringComparison.OrdinalIgnoreCase) >= 0 &&
                line.IndexOf(": error ", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    private async Task<bool> TryEnableInternalsVisibleToAsync(
        string testProjectPath, string? sourceProjectPath,
        IReadOnlyList<string> compilerErrors, CancellationToken ct)
    {
        if (sourceProjectPath is null || !File.Exists(sourceProjectPath)) return false;

        var candidates = new List<(string Type, string Member)>();
        foreach (var line in compilerErrors)
        {
            if (line.IndexOf("CS1061", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var m = InternalsVisibleToMemberRegex.Match(line);
            if (m.Success) candidates.Add((m.Groups["type"].Value, m.Groups["member"].Value));
        }
        if (candidates.Count == 0) return false;

        string sourceProjectDir = Path.GetDirectoryName(sourceProjectPath)!;
        bool anyInternalFound = false;
        foreach (var (_, member) in candidates)
        {
            var memberRegex = new Regex(
                @"\binternal\b[^\r\n;={]*\b" + Regex.Escape(member) + @"\b",
                RegexOptions.Compiled);
            foreach (var file in Directory.EnumerateFiles(sourceProjectDir, "*.cs", SearchOption.AllDirectories))
            {
                if (IsInBuildOutput(file)) continue;
                string text;
                try { text = await ReadAllTextAsyncCompat(file, ct); } catch { continue; }
                if (memberRegex.IsMatch(text))
                {
                    anyInternalFound = true;
                    break;
                }
            }
            if (anyInternalFound) break;
        }
        if (!anyInternalFound) return false;

        string testAssemblyName = await GetProjectAssemblyNameAsync(testProjectPath, ct)
                                  ?? Path.GetFileNameWithoutExtension(testProjectPath);

        var doc = XDocument.Parse(await ReadAllTextAsyncCompat(sourceProjectPath, ct));
        if (doc.Root is null) return false;

        if (doc.Root.Descendants("InternalsVisibleTo")
            .Any(e => string.Equals(e.Value.Trim(), testAssemblyName, StringComparison.OrdinalIgnoreCase)))
            return false;

        var itemGroup = doc.Root.Elements("ItemGroup")
            .FirstOrDefault(g => g.Elements("InternalsVisibleTo").Any());
        if (itemGroup is null) { itemGroup = new XElement("ItemGroup"); doc.Root.Add(itemGroup); }
        itemGroup.Add(new XElement("InternalsVisibleTo", new XAttribute("Include", testAssemblyName)));
        doc.Save(sourceProjectPath);

        await RunDotNetCliAsync(new[] { "restore", testProjectPath },
            Path.GetDirectoryName(testProjectPath)!, ct);
        return true;
    }

    private static async Task<string?> GetProjectAssemblyNameAsync(string projectPath, CancellationToken ct)
    {
        try
        {
            var doc = XDocument.Parse(await ReadAllTextAsyncCompat(projectPath, ct));
            var name = doc.Descendants("AssemblyName").FirstOrDefault()?.Value?.Trim();
            return !string.IsNullOrEmpty(name) ? name : Path.GetFileNameWithoutExtension(projectPath);
        }
        catch { return null; }
    }

    private static bool IsInBuildOutput(string path)
    {
        string sep = Path.DirectorySeparatorChar.ToString();
        string alt = Path.AltDirectorySeparatorChar.ToString();
        return path.Contains(sep + "obj" + sep) || path.Contains(sep + "bin" + sep)
            || path.Contains(alt + "obj" + alt) || path.Contains(alt + "bin" + alt);
    }

    private static bool HasSemanticErrors(IReadOnlyList<string> compilerErrors)
    {
        foreach (var line in compilerErrors)
        {
            if (line.IndexOf("CS1503", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (line.IndexOf("CS1501", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (line.IndexOf("CS1729", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (line.IndexOf("CS1061", StringComparison.OrdinalIgnoreCase) >= 0 &&
                line.IndexOf("does not contain a definition", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (line.IndexOf("CS0117", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (line.IndexOf("CS0029", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    private static List<string> ExtractSemanticErrorSummary(IReadOnlyList<string> compilerErrors)
    {
        var notes = new List<string>();
        foreach (var line in compilerErrors)
        {
            var m = SemanticCs1503Regex.Match(line);
            if (m.Success)
            {
                notes.Add($"• Argument #{m.Groups["n"].Value} has wrong type: expected " +
                          $"'{m.Groups["to"].Value}', got '{m.Groups["from"].Value}'.");
                continue;
            }
            m = SemanticCs1061Regex.Match(line);
            if (m.Success)
            {
                notes.Add($"• '{m.Groups["type"].Value}' has no member '{m.Groups["member"].Value}'.");
                continue;
            }
            m = SemanticCs1501Regex.Match(line);
            if (m.Success)
            {
                notes.Add($"• Method '{m.Groups["m"].Value}' does not accept {m.Groups["n"].Value} arguments.");
                continue;
            }
            m = SemanticCs1729Regex.Match(line);
            if (m.Success)
            {
                notes.Add($"• Type '{m.Groups["t"].Value}' has no constructor with {m.Groups["n"].Value} parameters.");
            }
        }
        return notes;
    }

    private async Task<bool> TryFixFloatingVersionsAsync(
        string testProjectPath, IReadOnlyList<string> compilerErrors, CancellationToken ct)
    {
        var floatingPackageIds = ExtractFloatingVersionPackageIds(compilerErrors);
        if (floatingPackageIds.Count == 0) return false;

        string? propsPath = FindDirectoryPackagesProps(Path.GetDirectoryName(testProjectPath)!);
        if (propsPath is null) return false;

        var doc = XDocument.Parse(await ReadAllTextAsyncCompat(propsPath, ct));
        if (doc.Root is null) return false;

        bool changed = false;
        foreach (var packageId in floatingPackageIds)
        {
            var elements = doc.Root.Descendants("PackageVersion")
                .Where(e => string.Equals((string?)e.Attribute("Include"), packageId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (elements.Count == 0) continue;

            string? concrete = await ResolvePackageVersionAsync(packageId, false, ct);
            if (concrete is null) continue;

            bool anyFloating = elements.Any(e =>
                LooksLikeFloatingVersion((string?)e.Attribute("Version") ?? string.Empty));
            if (!anyFloating) continue;

            elements[0].SetAttributeValue("Version", concrete);
            for (int i = 1; i < elements.Count; i++) elements[i].Remove();
            changed = true;
        }

        if (changed)
        {
            doc.Save(propsPath);
            await RunDotNetCliAsync(new[] { "restore", testProjectPath },
                Path.GetDirectoryName(testProjectPath)!, ct);
        }
        return changed;
    }

    private static bool LooksLikeFloatingVersion(string version)
    {
        version = version.Trim();
        if (version.Length == 0) return false;
        if (version == "*" || version.Contains('*')) return true;
        if (version.StartsWith("^") || version.StartsWith("~") ||
            version.StartsWith(">") || version.StartsWith("<")) return true;
        return false;
    }

    private static List<string> ExtractFloatingVersionPackageIds(IReadOnlyList<string> compilerErrors)
    {
        var result = new List<string>();
        foreach (var line in compilerErrors)
        {
            if (line.IndexOf("NU1011", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var m = Regex.Match(line,
                @"floating version:\s*([A-Za-z0-9_.\- ,]+?)(?:\.\s|\s*\[|$)",
                RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            foreach (var part in m.Groups[1].Value.Split(','))
            {
                string id = part.Trim().TrimEnd('.');
                if (id.Length > 0 && !result.Contains(id, StringComparer.OrdinalIgnoreCase))
                    result.Add(id);
            }
        }
        return result;
    }

    private static async Task<string?> ResolvePackageVersionAsync(
        string packageId, bool allowPrerelease, CancellationToken ct)
    {
        try
        {
            string id = packageId.ToLowerInvariant();
            string url = $"{id}/index.json";
            using var resp = await NuGetHttp.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("versions", out var versions)) return null;

            string? latest = null;
            foreach (var v in versions.EnumerateArray())
            {
                string s = v.GetString() ?? string.Empty;
                if (string.IsNullOrEmpty(s)) continue;
                if (!allowPrerelease && s.Contains('-')) continue;
                if (IsNewerVersion(s, latest)) latest = s;
            }
            return latest;
        }
        catch { return null; }
    }

    private static bool IsNewerVersion(string candidate, string? current)
    {
        if (current is null) return true;
        static string StripSuffix(string s)
        {
            int i = s.IndexOf('-');
            return i >= 0 ? s.Substring(0, i) : s;
        }
        try
        {
            var a = Version.Parse(Normalize(StripSuffix(candidate)));
            var b = Version.Parse(Normalize(StripSuffix(current)));
            return a > b;
        }
        catch { return string.CompareOrdinal(candidate, current) > 0; }

        static string Normalize(string s)
        {
            int parts = s.Count(c => c == '.') + 1;
            return parts switch { 1 => s + ".0.0", 2 => s + ".0", _ => s };
        }
    }

    private async Task<string?> ResolveAndAddPackageAsync(
        string testProjectPath, string testProjectDir, string namespaceName,
        HashSet<string> attemptedPackageIds, CancellationToken ct)
    {
        foreach (var candidate in GetPackageCandidates(namespaceName))
        {
            if (!attemptedPackageIds.Add(candidate)) continue;
            if (await ProjectHasPackageReferenceAsync(testProjectPath, candidate, ct)) return candidate;

            string? propsPath = FindDirectoryPackagesProps(testProjectDir);
            bool cpm = await IsCentralPackageManagementEnabledAsync(propsPath, ct);

            string? version = await ResolvePackageVersionAsync(candidate, allowPrerelease: false, ct);
            if (version is null) continue;

            if (cpm)
            {
                await EnsurePackageVersionEntryAsync(propsPath!, candidate, version, ct);
                if (!await EnsureCsprojPackageReferenceAsync(testProjectPath, candidate, ct)) continue;
            }
            else
            {
                var addResult = await RunDotNetCliAsync(
                    new[] { "add", testProjectPath, "package", candidate, "--version", version, "--no-restore" },
                    testProjectDir, ct);
                if (addResult.ExitCode != 0) continue;
            }

            var restoreResult = await RunDotNetCliAsync(
                new[] { "restore", testProjectPath }, testProjectDir, ct);
            if (restoreResult.ExitCode == 0) return candidate;

            await RemovePackageReferenceAsync(testProjectPath, candidate, ct);
        }
        return null;
    }

    private IEnumerable<string> GetPackageCandidates(string namespaceName)
    {
        if (KnownNamespaceToPackageMap.TryGetValue(namespaceName, out var mapped)) yield return mapped;
        var segments = namespaceName.Split('.');
        for (int len = segments.Length; len >= 1; len--)
        {
            var candidate = string.Join(".", segments.Take(len));
            yield return KnownNamespaceToPackageMap.TryGetValue(candidate, out var mappedPrefix)
                ? mappedPrefix : candidate;
        }
    }

    private static async Task<bool> IsCentralPackageManagementEnabledAsync(string? propsPath, CancellationToken ct)
    {
        if (propsPath is null) return false;
        try
        {
            var doc = XDocument.Parse(await ReadAllTextAsyncCompat(propsPath, ct));
            return doc.Root?.Descendants("ManagePackageVersionsCentrally")
                .Any(e => string.Equals(e.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch { return false; }
    }

    private static async Task EnsurePackageVersionEntryAsync(
        string propsPath, string packageId, string version, CancellationToken ct)
    {
        var doc = XDocument.Parse(await ReadAllTextAsyncCompat(propsPath, ct));
        if (doc.Root is null) return;

        var existing = doc.Root.Descendants("PackageVersion")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("Include"), packageId, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            string? cur = (string?)existing.Attribute("Version");
            if (LooksLikeFloatingVersion(cur ?? string.Empty) ||
                !string.Equals(cur, version, StringComparison.OrdinalIgnoreCase))
            {
                existing.SetAttributeValue("Version", version);
                doc.Save(propsPath);
            }
            return;
        }

        var itemGroup = doc.Root.Elements("ItemGroup").FirstOrDefault(g => g.Elements("PackageVersion").Any());
        if (itemGroup is null) { itemGroup = new XElement("ItemGroup"); doc.Root.Add(itemGroup); }
        itemGroup.Add(new XElement("PackageVersion",
            new XAttribute("Include", packageId), new XAttribute("Version", version)));
        doc.Save(propsPath);
    }

    private static async Task<bool> EnsureCsprojPackageReferenceAsync(
        string csprojPath, string packageId, CancellationToken ct)
    {
        var doc = XDocument.Parse(await ReadAllTextAsyncCompat(csprojPath, ct));
        if (doc.Root is null) return false;

        var existing = doc.Root.Descendants("PackageReference")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("Include"), packageId, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Attribute("Version")?.Remove();
            doc.Save(csprojPath);
            return true;
        }

        var itemGroup = doc.Root.Elements("ItemGroup").FirstOrDefault(g => g.Elements("PackageReference").Any());
        if (itemGroup is null) { itemGroup = new XElement("ItemGroup"); doc.Root.Add(itemGroup); }
        itemGroup.Add(new XElement("PackageReference", new XAttribute("Include", packageId)));
        doc.Save(csprojPath);
        return true;
    }

    private static async Task RemovePackageReferenceAsync(string csprojPath, string packageId, CancellationToken ct)
    {
        try
        {
            var doc = XDocument.Parse(await ReadAllTextAsyncCompat(csprojPath, ct));
            var existing = doc.Root?.Descendants("PackageReference")
                .FirstOrDefault(e => string.Equals((string?)e.Attribute("Include"), packageId, StringComparison.OrdinalIgnoreCase));
            if (existing is null) return;
            existing.Remove();
            doc.Save(csprojPath);
        }
        catch { }
    }

    private Task<Dictionary<string, List<TypeLocation>>> GetOrScanAsync(string solutionDirectory, CancellationToken ct)
        => _scanCache.GetOrAdd(solutionDirectory, dir => Task.Run(() => ScanTypeNamespaces(dir), ct));

    private async Task<string?> TryAddMissingUsingsAsync(
        string testClassPath, string testProjectPath, string? sourceProjectPath,
        string solutionDirectory, IReadOnlyList<string> compilerErrors, CancellationToken ct)
    {
        var missing = ExtractMissingIdentifiers(compilerErrors);
        if (missing.Count == 0) return null;

        var sourceTypes = await GetOrScanAsync(solutionDirectory, ct);
        string code = await ReadAllTextAsyncCompat(testClassPath, ct);
        var toAdd = new List<string>();
        var projectsToReference = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string testProjectFullPath = Path.GetFullPath(testProjectPath);

        foreach (var identifier in missing)
        {
            string? ns = null;
            string? owningProject = null;

            if (sourceTypes.TryGetValue(identifier, out var locations) && locations.Count > 0)
            {
                var distinctNamespaces = locations.Select(l => l.Namespace).Distinct(StringComparer.Ordinal).ToList();
                if (distinctNamespaces.Count == 1)
                {
                    ns = distinctNamespaces[0];
                    var distinctProjects = locations.Select(l => l.ProjectPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (distinctProjects.Count == 1) owningProject = distinctProjects[0];
                }
            }
            else if (_wellKnownTypeToNamespace.TryGetValue(identifier, out var wellKnown))
            {
                ns = wellKnown;
            }

            if (ns is not null && !toAdd.Contains(ns, StringComparer.Ordinal) &&
                !Regex.IsMatch(code, @"^\s*using\s+" + Regex.Escape(ns) + @"\s*;", RegexOptions.Multiline))
                toAdd.Add(ns);

            if (owningProject is not null &&
                !string.Equals(Path.GetFullPath(owningProject), testProjectFullPath, StringComparison.OrdinalIgnoreCase))
                projectsToReference.Add(owningProject);
        }

        string testProjectDir = Path.GetDirectoryName(testProjectPath)!;
        foreach (var refProject in projectsToReference)
        {
            if (!File.Exists(refProject)) continue;
            if (await ProjectHasReferenceAsync(testProjectPath, refProject, ct)) continue;
            await RunDotNetCliAsync(new[] { "add", testProjectPath, "reference", refProject },
                testProjectDir, ct);
        }

        if (toAdd.Count == 0) return projectsToReference.Count > 0 ? code : null;

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
                line.IndexOf("CS0103", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("CS1929", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("CS0234", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("CS1061", StringComparison.OrdinalIgnoreCase) < 0) continue;

            if (line.IndexOf("CS1061", StringComparison.OrdinalIgnoreCase) >= 0 &&
                line.IndexOf("does not contain a definition", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            foreach (Match m in Regex.Matches(line, "'([^']+)'"))
            {
                string name = Regex.Replace(m.Groups[1].Value, "<.*>$", string.Empty);
                if (name.Length > 0 && name.IndexOf('.') < 0 && !result.Contains(name, StringComparer.Ordinal))
                    result.Add(name);
            }
        }
        return result;
    }

    private static Dictionary<string, List<TypeLocation>> ScanTypeNamespaces(string solutionDirectory)
    {
        var map = new Dictionary<string, List<TypeLocation>>(StringComparer.Ordinal);
        var namespaceRegex = new Regex(@"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.Multiline);
        var typeRegex = new Regex(
            @"^\s*(?:(?:public|internal|sealed|static|abstract|partial|readonly|unsafe)\s+)*" +
            @"(?:record\s+(?:class|struct)|class|struct|interface|enum|record)\s+([A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Multiline);

        foreach (var file in Directory.EnumerateFiles(solutionDirectory, "*.cs", SearchOption.AllDirectories))
        {
            if (IsInBuildOutput(file)) continue;
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            var nsMatch = namespaceRegex.Match(text);
            if (!nsMatch.Success) continue;
            string ns = nsMatch.Groups[1].Value;
            string? owningProject = FindContainingProject(file, solutionDirectory);
            if (owningProject is null) continue;

            foreach (Match typeMatch in typeRegex.Matches(text))
            {
                string typeName = typeMatch.Groups[1].Value;
                if (!map.TryGetValue(typeName, out var list)) { list = new List<TypeLocation>(); map[typeName] = list; }
                if (!list.Any(l => string.Equals(l.Namespace, ns, StringComparison.Ordinal) &&
                                   string.Equals(l.ProjectPath, owningProject, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new TypeLocation(ns, owningProject));
            }
        }
        return map;
    }

    private async Task<TestGenerationResult> TryResolveMissingPackagesAndRebuildAsync(
        string testProjectPath, string testProjectDir, string testClassCode,
        TestGenerationResult failedBuildResult, CancellationToken cancellationToken)
    {
        var usingNamespaces = ExtractUsingNamespaces(testClassCode);
        if (usingNamespaces.Count == 0 && (failedBuildResult.CompilerErrors?.Length ?? 0) == 0)
            return failedBuildResult;

        var attemptedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolvedNamespaces = new List<string>();
        var addedPackages = new List<string>();
        var currentResult = failedBuildResult;

        for (int iteration = 0; iteration < MaxPackageResolutionIterations; iteration++)
        {
            var candidateNamespaces = ExtractMissingNamespaceCandidates(
                    currentResult.CompilerErrors ?? Array.Empty<string>(), usingNamespaces)
                .Where(ns => !unresolvedNamespaces.Contains(ns, StringComparer.Ordinal))
                .ToList();

            foreach (var nu in ExtractNuGetPackageCandidates(currentResult.CompilerErrors ?? Array.Empty<string>()))
                if (!candidateNamespaces.Contains(nu, StringComparer.Ordinal)) candidateNamespaces.Add(nu);

            if (candidateNamespaces.Count == 0) break;

            bool anyAdded = false;
            foreach (var ns in candidateNamespaces)
            {
                string? added = await ResolveAndAddPackageAsync(
                    testProjectPath, testProjectDir, ns, attemptedPackageIds, cancellationToken);
                if (added is not null) { addedPackages.Add(added); anyAdded = true; }
                else unresolvedNamespaces.Add(ns);
            }
            if (!anyAdded) break;

            currentResult = await ValidateProjectCompilesAsync(testProjectPath, cancellationToken);
            if (currentResult.IsSuccess) break;
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

        if (distinctAdded.Count == 0 && distinctUnresolved.Count == 0) return currentResult;

        var notes = new List<string>();
        if (distinctAdded.Count > 0) notes.Add($"Automatically added NuGet package(s): {string.Join(", ", distinctAdded)}.");
        if (distinctUnresolved.Count > 0) notes.Add($"Could not automatically resolve a NuGet package for the namespace(s): {string.Join(", ", distinctUnresolved)}.");

        return new TestGenerationResult(
            false, currentResult.Message,
            compilerErrors: (currentResult.CompilerErrors ?? Array.Empty<string>()).Concat(notes).ToArray(),
            exceptionDetails: currentResult.ExceptionDetails,
            isEnvironmentIssue: currentResult.IsEnvironmentIssue);
    }

    private static List<string> ExtractNuGetPackageCandidates(IReadOnlyList<string> compilerErrors)
    {
        var result = new List<string>();
        foreach (var line in compilerErrors)
        {
            if (line.IndexOf("NU1101", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("NU1201", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("NU1202", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("NU1011", StringComparison.OrdinalIgnoreCase) < 0) continue;

            var m = Regex.Match(line,
                @"(?:package|find package|floating version:)\s+([A-Za-z_][A-Za-z0-9_.\-]+)",
                RegexOptions.IgnoreCase);
            if (m.Success && !result.Contains(m.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
                result.Add(m.Groups[1].Value);
        }
        return result;
    }

    private static List<string> ExtractUsingNamespaces(string code)
    {
        var codeWithoutLineComments = string.Join("\n",
            code.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        var matches = Regex.Matches(codeWithoutLineComments,
            @"^\s*using\s+(?!static\s)([A-Za-z_][A-Za-z0-9_.]*)\s*;",
            RegexOptions.Multiline | RegexOptions.Compiled);

        var namespaces = new List<string>();
        foreach (Match match in matches)
        {
            string ns = match.Groups[1].Value;
            if (!string.Equals(ns, "System", StringComparison.Ordinal) &&
                !namespaces.Contains(ns, StringComparer.Ordinal))
                namespaces.Add(ns);
        }
        return namespaces;
    }

    private List<string> ExtractMissingNamespaceCandidates(
        IReadOnlyList<string> compilerErrors, IReadOnlyList<string> usingNamespaces)
    {
        var missingIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in compilerErrors)
        {
            if (line.IndexOf("CS0246", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("CS0234", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("CS1929", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("CS1061", StringComparison.OrdinalIgnoreCase) < 0) continue;

            if (line.IndexOf("CS1061", StringComparison.OrdinalIgnoreCase) >= 0 &&
                line.IndexOf("does not contain a definition", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            foreach (Match m in Regex.Matches(line, "'([^']+)'"))
                missingIdentifiers.Add(Regex.Replace(m.Groups[1].Value, "<.*>$", string.Empty));
        }

        if (missingIdentifiers.Count == 0) return new List<string>();

        var candidates = usingNamespaces
            .Where(ns => missingIdentifiers.Contains(ns) ||
                         ns.Split('.').Any(segment => missingIdentifiers.Contains(segment)))
            .ToList();

        foreach (var id in missingIdentifiers)
            if (_wellKnownTypeToNamespace.TryGetValue(id, out var ns) &&
                !string.Equals(ns, "System", StringComparison.Ordinal) &&
                !candidates.Contains(ns, StringComparer.Ordinal))
                candidates.Add(ns);

        return candidates;
    }

    /// <summary>
    /// Builds the well-known-type-to-namespace map for the selected frameworks. BCL, WPF and
    /// DI types are always present. Test-framework types (Assert, [Fact], [Test], [TestClass] …)
    /// are added only for the configured test framework. Mock types are added only for the
    /// configured mocking framework.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildWellKnownTypeToNamespace(
        TestFramework testFramework, MockFramework mockFramework)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // --- Framework-agnostisch: BCL / DI ---
            ["IServiceProvider"] = "System",
            ["Task"] = "System.Threading.Tasks",
            ["ServiceProvider"] = "Microsoft.Extensions.DependencyInjection",
            ["ServiceCollection"] = "Microsoft.Extensions.DependencyInjection",
            ["IServiceCollection"] = "Microsoft.Extensions.DependencyInjection",
            ["GetRequiredService"] = "Microsoft.Extensions.DependencyInjection",
            ["GetService"] = "Microsoft.Extensions.DependencyInjection",
            ["AddSingleton"] = "Microsoft.Extensions.DependencyInjection",
            ["AddScoped"] = "Microsoft.Extensions.DependencyInjection",
            ["AddTransient"] = "Microsoft.Extensions.DependencyInjection",

            // --- Framework-agnostisch: WPF ---
            ["Application"] = "System.Windows",
            ["Window"] = "System.Windows",
            ["RoutedEventArgs"] = "System.Windows",
            ["RoutedEvent"] = "System.Windows",
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

        // Test-Framework-spezifische Typen — nur fuer das ausgewaehlte Framework.
        switch (testFramework)
        {
            case TestFramework.xUnit:
                map["Assert"] = "Xunit";
                map["Fact"] = "Xunit";
                map["Theory"] = "Xunit";
                map["InlineData"] = "Xunit";
                map["WpfFact"] = "Xunit";
                map["StaFact"] = "Xunit";
                map["UIFact"] = "Xunit";
                break;

            case TestFramework.NUnit:
                map["Assert"] = "NUnit.Framework";
                map["Test"] = "NUnit.Framework";
                map["TestFixture"] = "NUnit.Framework";
                map["SetUp"] = "NUnit.Framework";
                map["TearDown"] = "NUnit.Framework";
                map["OneTimeSetUp"] = "NUnit.Framework";
                map["OneTimeTearDown"] = "NUnit.Framework";
                map["TestCase"] = "NUnit.Framework";
                map["TestCaseSource"] = "NUnit.Framework";
                map["Ignore"] = "NUnit.Framework";
                break;

            case TestFramework.MSTest:
                map["Assert"] = "Microsoft.VisualStudio.TestTools.UnitTesting";
                map["TestClass"] = "Microsoft.VisualStudio.TestTools.UnitTesting";
                map["TestMethod"] = "Microsoft.VisualStudio.TestTools.UnitTesting";
                map["TestInitialize"] = "Microsoft.VisualStudio.TestTools.UnitTesting";
                map["TestCleanup"] = "Microsoft.VisualStudio.TestTools.UnitTesting";
                map["ClassInitialize"] = "Microsoft.VisualStudio.TestTools.UnitTesting";
                map["ClassCleanup"] = "Microsoft.VisualStudio.TestTools.UnitTesting";
                map["DataTestMethod"] = "Microsoft.VisualStudio.TestTools.UnitTesting";
                map["DataRow"] = "Microsoft.VisualStudio.TestTools.UnitTesting";
                map["Ignore"] = "Microsoft.VisualStudio.TestTools.UnitTesting";
                break;
        }

        // Mock-Framework-spezifische Typen — nur fuer das ausgewaehlte Framework.
        switch (mockFramework)
        {
            case MockFramework.Moq:
                map["Mock"] = "Moq";
                map["It"] = "Moq";
                map["Times"] = "Moq";
                map["MockBehavior"] = "Moq";
                map["MockException"] = "Moq";
                break;

            case MockFramework.NSubstitute:
                map["Substitute"] = "NSubstitute";
                map["Arg"] = "NSubstitute";
                map["Received"] = "NSubstitute";
                break;

            case MockFramework.FakeItEasy:
                map["A"] = "FakeItEasy";
                map["Fake"] = "FakeItEasy";
                break;
        }

        return map;
    }

    private sealed record TypeLocation(string Namespace, string ProjectPath);
    private enum XUnitFlavor { Unknown, V2, V3 }

    private static async Task<bool> ProjectHasPackageReferenceAsync(
        string projectPath, string packageId, CancellationToken ct)
    {
        var xml = await ReadAllTextAsyncCompat(projectPath, ct);
        var doc = XDocument.Parse(xml);
        return doc.Descendants("PackageReference")
            .Any(e => string.Equals((string?)e.Attribute("Include"), packageId, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<XUnitFlavor> DetectXUnitFlavorAsync(string testProjectPath, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var csprojDoc = XDocument.Parse(await ReadAllTextAsyncCompat(testProjectPath, ct));
            foreach (var pr in csprojDoc.Descendants("PackageReference"))
            {
                var id = (string?)pr.Attribute("Include");
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id!);
            }
            string? propsPath = FindDirectoryPackagesProps(Path.GetDirectoryName(testProjectPath)!);
            if (propsPath is not null)
            {
                var propsDoc = XDocument.Parse(await ReadAllTextAsyncCompat(propsPath, ct));
                foreach (var pv in propsDoc.Descendants("PackageVersion"))
                {
                    var id = (string?)pv.Attribute("Include");
                    if (!string.IsNullOrWhiteSpace(id)) ids.Add(id!);
                }
            }
        }
        catch { return XUnitFlavor.Unknown; }

        if (ids.Any(id => id.Equals("xunit.v3", StringComparison.OrdinalIgnoreCase) ||
                          id.StartsWith("xunit.v3.", StringComparison.OrdinalIgnoreCase)))
            return XUnitFlavor.V3;
        if (ids.Any(id => id.Equals("xunit", StringComparison.OrdinalIgnoreCase) ||
                          (id.StartsWith("xunit.", StringComparison.OrdinalIgnoreCase) &&
                           !id.StartsWith("xunit.v3", StringComparison.OrdinalIgnoreCase))))
            return XUnitFlavor.V2;
        return XUnitFlavor.Unknown;
    }

    private static string GetCompatibleStaFactVersion(XUnitFlavor flavor) => flavor switch
    {
        XUnitFlavor.V2 => "1.1.11",
        XUnitFlavor.V3 => "3.0.0",
        _ => "1.1.11",
    };

    private async Task<bool> EnsurePackageReferenceWithVersionAsync(
        string testProjectPath, string packageId, string version, CancellationToken ct)
    {
        string testProjectDir = Path.GetDirectoryName(testProjectPath)!;
        string? propsPath = FindDirectoryPackagesProps(testProjectDir);
        bool cpmEnabled = await IsCentralPackageManagementEnabledAsync(propsPath, ct);

        if (!cpmEnabled)
        {
            var csprojDoc = XDocument.Parse(await ReadAllTextAsyncCompat(testProjectPath, ct));
            var existing = csprojDoc.Descendants("PackageReference")
                .FirstOrDefault(e => string.Equals((string?)e.Attribute("Include"), packageId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                string? currentVersion = (string?)existing.Attribute("Version");
                if (string.Equals(currentVersion, version, StringComparison.OrdinalIgnoreCase)) return false;
                existing.SetAttributeValue("Version", version);
                csprojDoc.Save(testProjectPath);
                return true;
            }
            var addResult = await RunDotNetCliAsync(
                new[] { "add", testProjectPath, "package", packageId, "--version", version },
                testProjectDir, ct);
            return addResult.ExitCode == 0;
        }

        bool changed = false;
        var propsDoc = XDocument.Parse(await ReadAllTextAsyncCompat(propsPath!, ct));
        var versionElement = propsDoc.Descendants("PackageVersion")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("Include"), packageId, StringComparison.OrdinalIgnoreCase));
        if (versionElement is null)
        {
            var itemGroup = propsDoc.Root!.Elements("ItemGroup").FirstOrDefault(g => g.Elements("PackageVersion").Any());
            if (itemGroup is null) { itemGroup = new XElement("ItemGroup"); propsDoc.Root.Add(itemGroup); }
            itemGroup.Add(new XElement("PackageVersion",
                new XAttribute("Include", packageId), new XAttribute("Version", version)));
            changed = true;
        }
        else
        {
            string? cur = (string?)versionElement.Attribute("Version");
            if (!string.Equals(cur, version, StringComparison.OrdinalIgnoreCase) ||
                LooksLikeFloatingVersion(cur ?? string.Empty))
            {
                versionElement.SetAttributeValue("Version", version);
                changed = true;
            }
        }
        if (changed) propsDoc.Save(propsPath!);

        var csprojDocCpm = XDocument.Parse(await ReadAllTextAsyncCompat(testProjectPath, ct));
        var existingRef = csprojDocCpm.Descendants("PackageReference")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("Include"), packageId, StringComparison.OrdinalIgnoreCase));
        if (existingRef is null)
        {
            var itemGroup = csprojDocCpm.Root!.Elements("ItemGroup").FirstOrDefault(g => g.Elements("PackageReference").Any());
            if (itemGroup is null) { itemGroup = new XElement("ItemGroup"); csprojDocCpm.Root.Add(itemGroup); }
            itemGroup.Add(new XElement("PackageReference", new XAttribute("Include", packageId)));
            changed = true;
            csprojDocCpm.Save(testProjectPath);
        }
        return changed;
    }

    private async Task EnsureWindowsSettingsAsync(
        string testProjectPath, string? sourceProjectPath, string? preferredFramework, CancellationToken ct)
    {
        try
        {
            if (sourceProjectPath is null || !File.Exists(sourceProjectPath)) return;
            var sourceDoc = XDocument.Parse(await ReadAllTextAsyncCompat(sourceProjectPath, ct));
            bool useWpf = HasTrueProperty(sourceDoc, "UseWPF");
            bool useWinForms = HasTrueProperty(sourceDoc, "UseWindowsForms");

            string? windowsTfm = sourceDoc.Descendants()
                .Where(e => e.Name.LocalName == "TargetFramework" || e.Name.LocalName == "TargetFrameworks")
                .SelectMany(e => e.Value.Split(';')).Select(t => t.Trim())
                .FirstOrDefault(t => t.IndexOf("-windows", StringComparison.OrdinalIgnoreCase) >= 0);

            if (!useWpf && !useWinForms && windowsTfm is null) return;
            if (windowsTfm is null)
                windowsTfm = preferredFramework is not null &&
                             preferredFramework.IndexOf("-windows", StringComparison.OrdinalIgnoreCase) >= 0
                    ? preferredFramework : DefaultWindowsFramework;

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
            foreach (var multi in root.Descendants("TargetFrameworks").ToList()) { multi.Remove(); changed = true; }
            changed |= SetProperty(root, propertyGroup, "TargetFramework", windowsTfm);
            if (useWpf) changed |= SetProperty(root, propertyGroup, "UseWPF", "true");
            if (useWinForms) changed |= SetProperty(root, propertyGroup, "UseWindowsForms", "true");
            if (changed) testDoc.Save(testProjectPath);

            var flavor = await DetectXUnitFlavorAsync(testProjectPath, ct);
            bool packageChanged = false;
            if (useWpf && flavor != XUnitFlavor.Unknown)
            {
                string staFactVersion = GetCompatibleStaFactVersion(flavor);
                packageChanged = await EnsurePackageReferenceWithVersionAsync(
                    testProjectPath, StaFactPackageId, staFactVersion, ct);
            }
            if (flavor == XUnitFlavor.V3)
            {
                var outputDoc = XDocument.Parse(await ReadAllTextAsyncCompat(testProjectPath, ct));
                var outputRoot = outputDoc.Root;
                if (outputRoot is not null)
                {
                    var outputPg = outputRoot.Elements("PropertyGroup").FirstOrDefault();
                    if (outputPg is not null && SetProperty(outputRoot, outputPg, "OutputType", "Exe"))
                    {
                        outputDoc.Save(testProjectPath);
                        changed = true;
                    }
                }
            }
            if (changed || packageChanged)
            {
                string dir = Path.GetDirectoryName(testProjectPath)!;
                await RunDotNetCliAsync(new[] { "restore", testProjectPath }, dir, ct);
            }
        }
        catch { }
    }

    private static bool HasTrueProperty(XDocument doc, string propertyName)
        => doc.Descendants().Where(e => e.Name.LocalName == propertyName)
            .Any(e => string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));

    private static bool SetProperty(XElement root, XElement defaultGroup, string name, string value)
    {
        var existing = root.Descendants(name).FirstOrDefault();
        if (existing is null) { defaultGroup.Add(new XElement(name, value)); return true; }
        if (string.Equals(existing.Value.Trim(), value, StringComparison.OrdinalIgnoreCase)) return false;
        existing.Value = value;
        return true;
    }

    private async Task<TestGenerationResult> CreateTestProjectAsync(
        string solutionDirectory, string directory, string projectName, string testTemplate,
        string? targetFramework, string? sourceProjectPath, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var newArgs = new List<string> { "new", testTemplate, "-n", projectName, "-o", "." };
        if (!string.IsNullOrWhiteSpace(targetFramework) &&
            targetFramework!.IndexOf("-windows", StringComparison.OrdinalIgnoreCase) < 0)
        {
            newArgs.Add("--framework");
            newArgs.Add(targetFramework);
        }
        var newResult = await RunDotNetCliAsync(newArgs, directory, ct);
        if (newResult.ExitCode != 0)
        {
            var combined = newResult.Errors.Concat(newResult.Output).ToArray();
            return new TestGenerationResult(false,
                "Failed to create the test project or restore its NuGet packages via the .NET CLI.",
                compilerErrors: combined,
                isEnvironmentIssue: IsEnvironmentErrorRaw(combined));
        }

        string projectPath = Path.Combine(directory, $"{projectName}.csproj");
        var slnFiles = Directory.GetFiles(solutionDirectory, "*.sln")
            .Concat(Directory.GetFiles(solutionDirectory, "*.slnx")).ToArray();
        if (slnFiles.Length > 0)
            await RunDotNetCliAsync(new[] { "sln", slnFiles[0], "add", projectPath }, solutionDirectory, ct);

        if (sourceProjectPath is not null)
        {
            await EnsureWindowsSettingsAsync(projectPath, sourceProjectPath, targetFramework, ct);
            var refResult = await RunDotNetCliAsync(
                new[] { "add", projectPath, "reference", sourceProjectPath }, directory, ct);
            if (refResult.ExitCode != 0)
            {
                var combined = refResult.Errors.Concat(refResult.Output).ToArray();
                return new TestGenerationResult(false,
                    "The test project was created, but the reference to the source project could not be set.",
                    compilerErrors: combined,
                    isEnvironmentIssue: IsEnvironmentErrorRaw(combined));
            }
        }

        string sampleTestClassFile = Path.Combine(directory, DefaultSampleFileName);
        if (File.Exists(sampleTestClassFile)) File.Delete(sampleTestClassFile);
        return new TestGenerationResult(true, "Test project created successfully.");
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
            if (dir.EnumerateFiles("*.sln").Any() || dir.EnumerateFiles("*.slnx").Any())
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static string ExtractClassName(string classCode, string generatedNamePrefix)
    {
        var codeWithoutLineComments = string.Join("\n",
            classCode.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        var match = Regex.Match(codeWithoutLineComments, @"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
        if (match.Success) return match.Groups[1].Value;
        string fallback = $"{generatedNamePrefix}{Guid.NewGuid():N}";
        return fallback.Length > 40 ? fallback.Substring(0, 40) : fallback;
    }

    private static string SafeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private static string? FindContainingProject(string filePath, string solutionDirectory)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        while (dir is not null && dir.FullName.StartsWith(solutionDirectory, StringComparison.OrdinalIgnoreCase))
        {
            var csproj = dir.GetFiles("*.csproj").FirstOrDefault();
            if (csproj is not null) return csproj.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static async Task<bool> ProjectHasReferenceAsync(
        string testProjectPath, string sourceProjectPath, CancellationToken ct)
    {
        var xml = await ReadAllTextAsyncCompat(testProjectPath, ct);
        var doc = XDocument.Parse(xml);
        var testProjectDir = Path.GetDirectoryName(testProjectPath)!;
        var refs = doc.Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include")).Where(v => v is not null)
            .Select(v => Path.GetFullPath(Path.Combine(testProjectDir, v!)));
        return refs.Any(r => string.Equals(r, Path.GetFullPath(sourceProjectPath), StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindDirectoryPackagesProps(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Directory.Packages.props");
            if (File.Exists(candidate)) return candidate;
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
        bool cpmEnabled = propsDoc.Root.Descendants("ManagePackageVersionsCentrally")
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

        bool csprojChanged = false, propsChanged = false;
        XElement? targetItemGroup = null;

        foreach (var packageRef in packageRefs)
        {
            string? include = (string?)packageRef.Attribute("Include");
            var versionAttr = packageRef.Attribute("Version");
            string? version = versionAttr?.Value;
            if (string.IsNullOrEmpty(include)) continue;
            if (versionAttr is not null) { versionAttr.Remove(); csprojChanged = true; }

            if (!string.IsNullOrEmpty(version) && !LooksLikeFloatingVersion(version!) &&
                !existingVersions.Contains(include!))
            {
                if (targetItemGroup is null)
                {
                    targetItemGroup = propsDoc.Root.Elements("ItemGroup")
                        .FirstOrDefault(g => g.Elements("PackageVersion").Any());
                    if (targetItemGroup is null) { targetItemGroup = new XElement("ItemGroup"); propsDoc.Root.Add(targetItemGroup); }
                }
                targetItemGroup.Add(new XElement("PackageVersion",
                    new XAttribute("Include", include!), new XAttribute("Version", version!)));
                existingVersions.Add(include!);
                propsChanged = true;
            }
        }
        if (csprojChanged) csprojDoc.Save(csprojPath);
        if (propsChanged) propsDoc.Save(propsPath);
    }

    private async Task<TestGenerationResult> ValidateProjectCompilesAsync(
        string testProjectPath,
        CancellationToken cancellationToken)
    {
        try
        {
            string projectDir = Path.GetDirectoryName(testProjectPath)!;

            var buildArgs = new List<string>
            {
                "build", testProjectPath,
                "--nologo",
                "-v", "q",
                "-p:GenerateFullPaths=true",
                "-t:Compile",
                "-p:BuildProjectReferences=false"
            };

            var result = await RunDotNetCliAsync(buildArgs, projectDir, cancellationToken);

            if (result.ExitCode != 0 &&
                result.Output.Concat(result.Errors).Any(line =>
                    line.IndexOf("CS0006", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                int referencesPropertyIndex = buildArgs.IndexOf("-p:BuildProjectReferences=false");
                if (referencesPropertyIndex >= 0)
                {
                    buildArgs[referencesPropertyIndex] = "-p:BuildProjectReferences=true";
                    buildArgs.Remove("-t:Compile");
                    buildArgs.Add("-p:TestGeneratorEnabled=false");
                    var referenceBuildResult = await RunDotNetCliAsync(buildArgs, projectDir, cancellationToken);
                    result = (
                        referenceBuildResult.ExitCode,
                        result.Output.Concat(referenceBuildResult.Output).ToArray(),
                        result.Errors.Concat(referenceBuildResult.Errors).ToArray());
                }
            }

            if (result.ExitCode == 0)
            {
                return new TestGenerationResult(true, "Compilation succeeded.");
            }

            var allLines = result.Output.Concat(result.Errors).ToArray();
            bool isEnvironmentIssue = IsEnvironmentErrorRaw(allLines);

            var errors = allLines.Select(l => BuildDiagnosticRegex.Match(l))
                .Where(m => m.Success)
                .Select(m => $"error {m.Groups["id"].Value}: {m.Groups["msg"].Value} " +
                             $"({Path.GetFileName(m.Groups["file"].Value)}, line {m.Groups["line"].Value})")
                .Distinct(StringComparer.Ordinal).ToList();

            if (errors.Count == 0)
                errors = allLines.Where(l => l.IndexOf(" error ", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Distinct(StringComparer.Ordinal).ToList();

            if (errors.Count == 0)
                errors = allLines.Where(l => !string.IsNullOrWhiteSpace(l)).Take(30).ToList();

            if (isEnvironmentIssue)
            {
                foreach (var line in allLines)
                {
                    bool isWarningLine =
                        line.IndexOf("warning MSB", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        line.IndexOf("error MSB", StringComparison.OrdinalIgnoreCase) < 0;
                    if (isWarningLine) continue;

                    bool isLockError =
                        line.IndexOf("MSB3027", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        line.IndexOf("MSB3028", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        line.IndexOf("MSB3023", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        line.IndexOf("MSB3021", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (line.IndexOf("Exceeded retry count", StringComparison.OrdinalIgnoreCase) >= 0 &&
                         line.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0);

                    if (isLockError && !errors.Contains(line, StringComparer.Ordinal))
                        errors.Add(line);
                }
            }

            return new TestGenerationResult(
                false, "Compilation failed.",
                compilerErrors: errors.ToArray(),
                isEnvironmentIssue: isEnvironmentIssue);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new TestGenerationResult(false,
                $"Build validation failed: {ex.Message}",
                exceptionDetails: FormatExceptionDetails(ex));
        }
    }

    private static Task<string> ReadAllTextAsyncCompat(string path, CancellationToken cancellationToken)
        => Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); return File.ReadAllText(path, Encoding.UTF8); }, cancellationToken);

    private static Task WriteAllTextAsyncCompat(
        string path, string contents, Encoding encoding, CancellationToken cancellationToken)
        => Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); File.WriteAllText(path, contents, encoding); }, cancellationToken);

    private static Task WaitForExitAsyncCompat(Process process, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnExited(object? sender, EventArgs e) => tcs.TrySetResult(true);
        process.Exited += OnExited;
        try
        {
            if (process.HasExited) { process.Exited -= OnExited; return Task.CompletedTask; }
            var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return WaitAndCleanupAsync(tcs.Task, registration, process, OnExited);
        }
        catch { process.Exited -= OnExited; throw; }
    }

    private static async Task WaitAndCleanupAsync(
        Task waitTask, CancellationTokenRegistration registration, Process process, EventHandler handler)
    {
        try { await waitTask.ConfigureAwait(false); }
        finally { registration.Dispose(); process.Exited -= handler; }
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
        IReadOnlyList<string> arguments, string workingDirectory,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var primaryResult = await ExecuteDotNetProcessAsync(
            arguments, workingDirectory, cancellationToken, timeout);

        if (primaryResult.ExitCode == 0 && ContainsCommand(arguments, "new"))
        {
            string? createdProject = Directory.EnumerateFiles(workingDirectory, "*.csproj").FirstOrDefault();
            if (createdProject is not null)
            {
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
        IEnumerable<string> arguments, string workingDirectory,
        CancellationToken cancellationToken, TimeSpan? timeout)
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
        catch (OperationCanceledException) { TryKill(process); throw; }

        return (process.ExitCode, outputList.ToArray(), errorList.ToArray());
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(); } catch { }
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
            var relevantFrames = ex.StackTrace.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0 && !l.Contains("System.Runtime.CompilerServices"))
                .ToArray();
            if (relevantFrames.Length > 0) { lines.Add("Stack trace:"); lines.AddRange(relevantFrames); }
        }
        return lines.ToArray();
    }
}