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
    private const string StaFactPackageId = "Xunit.StaFact";

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

        // Dependency Injection (GetRequiredService<T>() ist eine Extension-Methode)
        ["Microsoft.Extensions.DependencyInjection"] = "Microsoft.Extensions.DependencyInjection",
        ["Microsoft.Extensions.DependencyInjection.Abstractions"] = "Microsoft.Extensions.DependencyInjection.Abstractions",

        // WPF-Unit-Tests benötigen STA-Threads
        ["Xunit.StaFact"] = "Xunit.StaFact",
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
                // TFM / UseWPF müssen stimmen, BEVOR die Referenz hinzugefügt wird (NU1201).
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

            // KI-generierter Code vergisst oft 'using'-Direktiven und Projekt-Referenzen.
            // Erst eindeutig auflösbare using's ergänzen und neu bauen.

            if (!buildResult.IsSuccess)
            {
                string? fixedCode = await TryAddMissingUsingsAsync(
                    testClassPath,
                    testProjectPath,
                    sourceProjectPath,
                    solutionDirectory,
                    buildResult.CompilerErrors ?? Array.Empty<string>(),
                    cancellationToken);

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
            // xUnit + STA (WPF)
            ["WpfFact"] = "Xunit",
            ["StaFact"] = "Xunit",
            ["UIFact"] = "Xunit",
            // Moq
            ["Mock"] = "Moq",
            ["It"] = "Moq",
            ["Times"] = "Moq",
            ["MockBehavior"] = "Moq",
            ["MockException"] = "Moq",
            // BCL
            ["IServiceProvider"] = "System",
            ["Task"] = "System.Threading.Tasks",
            // Dependency Injection
            ["ServiceProvider"] = "Microsoft.Extensions.DependencyInjection",
            ["ServiceCollection"] = "Microsoft.Extensions.DependencyInjection",
            ["IServiceCollection"] = "Microsoft.Extensions.DependencyInjection",
            ["GetRequiredService"] = "Microsoft.Extensions.DependencyInjection",
            ["GetService"] = "Microsoft.Extensions.DependencyInjection",
            ["AddSingleton"] = "Microsoft.Extensions.DependencyInjection",
            ["AddScoped"] = "Microsoft.Extensions.DependencyInjection",
            ["AddTransient"] = "Microsoft.Extensions.DependencyInjection",
            // WPF
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

    /// <summary>
    ///     Fundort eines Typs: Namensraum + enthaltendes Projekt. Wird für die
    ///     automatische Projekt-Referenz-Auflösung verwendet.
    /// </summary>
    private sealed record TypeLocation(string Namespace, string ProjectPath);

    /// <summary>
    ///     Erkannte xUnit-Hauptversion eines Testprojekts.
    /// </summary>
    private enum XUnitFlavor
    {
        Unknown,
        V2,
        V3,
    }

    /// <summary>
    ///     Prüft, ob im Testprojekt bereits eine PackageReference mit gegebener ID existiert.
    /// </summary>
    private static async Task<bool> ProjectHasPackageReferenceAsync(
        string projectPath, string packageId, CancellationToken ct)
    {
        var xml = await ReadAllTextAsyncCompat(projectPath, ct);
        var doc = XDocument.Parse(xml);
        return doc.Descendants("PackageReference")
            .Any(e => string.Equals(
                (string?)e.Attribute("Include"),
                packageId,
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Liest alle referenzierten Paket-IDs aus dem Testprojekt UND aus
    ///     Directory.Packages.props (Central Package Management) und leitet daraus
    ///     die xUnit-Hauptversion ab.
    /// </summary>
    private static async Task<XUnitFlavor> DetectXUnitFlavorAsync(
        string testProjectPath, CancellationToken ct)
    {
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var csprojDoc = XDocument.Parse(await ReadAllTextAsyncCompat(testProjectPath, ct));
            foreach (var pr in csprojDoc.Descendants("PackageReference"))
            {
                var id = (string?)pr.Attribute("Include");
                if (!string.IsNullOrWhiteSpace(id)) packageIds.Add(id!);
            }

            string? propsPath = FindDirectoryPackagesProps(Path.GetDirectoryName(testProjectPath)!);
            if (propsPath is not null)
            {
                var propsDoc = XDocument.Parse(await ReadAllTextAsyncCompat(propsPath, ct));
                foreach (var pv in propsDoc.Descendants("PackageVersion"))
                {
                    var id = (string?)pv.Attribute("Include");
                    if (!string.IsNullOrWhiteSpace(id)) packageIds.Add(id!);
                }
            }
        }
        catch
        {
            // Defensiv: bei unerwarteten XML-Problemen lieber nichts annehmen
            // als eine Ausnahme bis in den Aufrufer durchzureichen.
            return XUnitFlavor.Unknown;
        }

        // xUnit v3 erkennt man an "xunit.v3" bzw. "xunit.v3.*"
        if (packageIds.Any(id => id.Equals("xunit.v3", StringComparison.OrdinalIgnoreCase) ||
                                  id.StartsWith("xunit.v3.", StringComparison.OrdinalIgnoreCase)))
        {
            return XUnitFlavor.V3;
        }

        // xUnit v2 erkennt man an "xunit" oder "xunit.*" (außer den v3-Varianten)
        if (packageIds.Any(id => id.Equals("xunit", StringComparison.OrdinalIgnoreCase) ||
                                  (id.StartsWith("xunit.", StringComparison.OrdinalIgnoreCase) &&
                                   !id.StartsWith("xunit.v3", StringComparison.OrdinalIgnoreCase))))
        {
            return XUnitFlavor.V2;
        }

        return XUnitFlavor.Unknown;
    }

    /// <summary>
    ///     Liefert die zur erkannten xUnit-Hauptversion passende Xunit.StaFact-Version.
    ///     Verhindert CS0433 ("type exists in both xunit.core and xunit.v3.core").
    /// </summary>
    private static string GetCompatibleStaFactVersion(XUnitFlavor flavor) => flavor switch
    {
        XUnitFlavor.V2 => "1.1.11",
        XUnitFlavor.V3 => "3.0.0",
        _ => "1.1.11",
    };

    /// <summary>
    ///     Stellt sicher, dass das Testprojekt eine PackageReference mit gegebener ID und
    ///     Version hat. Berücksichtigt Central Package Management: die Version wird in
    ///     Directory.Packages.props gepflegt, die Referenz im .csproj. Eine bereits
    ///     vorhandene, aber falsche Version wird korrigiert.
    /// </summary>
    private async Task<bool> EnsurePackageReferenceWithVersionAsync(
        string testProjectPath,
        string packageId,
        string version,
        CancellationToken ct)
    {
        string testProjectDir = Path.GetDirectoryName(testProjectPath)!;
        string? propsPath = FindDirectoryPackagesProps(testProjectDir);

        bool cpmEnabled = false;
        if (propsPath is not null)
        {
            try
            {
                var propsDocCheck = XDocument.Parse(await ReadAllTextAsyncCompat(propsPath, ct));
                cpmEnabled = propsDocCheck.Root?
                    .Descendants("ManagePackageVersionsCentrally")
                    .Any(e => string.Equals(e.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase)) == true;
            }
            catch
            {
                cpmEnabled = false;
            }
        }

        // ---------- Ohne CPM: direkte Version in der PackageReference ----------
        if (!cpmEnabled)
        {
            var csprojDoc = XDocument.Parse(await ReadAllTextAsyncCompat(testProjectPath, ct));
            var existing = csprojDoc.Descendants("PackageReference")
                .FirstOrDefault(e => string.Equals(
                    (string?)e.Attribute("Include"), packageId, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                string? currentVersion = (string?)existing.Attribute("Version");
                if (string.Equals(currentVersion, version, StringComparison.OrdinalIgnoreCase))
                    return false;

                existing.SetAttributeValue("Version", version);
                csprojDoc.Save(testProjectPath);
                return true;
            }

            var addResult = await RunDotNetCliAsync(
                new[] { "add", testProjectPath, "package", packageId, "--version", version },
                testProjectDir, ct);
            return addResult.ExitCode == 0;
        }

        // ---------- Mit CPM: Version in props, Referenz in csproj ----------
        bool changed = false;

        var propsDoc = XDocument.Parse(await ReadAllTextAsyncCompat(propsPath!, ct));
        var versionElement = propsDoc.Descendants("PackageVersion")
            .FirstOrDefault(e => string.Equals(
                (string?)e.Attribute("Include"), packageId, StringComparison.OrdinalIgnoreCase));

        if (versionElement is null)
        {
            var itemGroup = propsDoc.Root!.Elements("ItemGroup")
                .FirstOrDefault(g => g.Elements("PackageVersion").Any());

            if (itemGroup is null)
            {
                itemGroup = new XElement("ItemGroup");
                propsDoc.Root.Add(itemGroup);
            }

            itemGroup.Add(new XElement("PackageVersion",
                new XAttribute("Include", packageId),
                new XAttribute("Version", version)));
            changed = true;
        }
        else
        {
            string? currentVersion = (string?)versionElement.Attribute("Version");
            if (!string.Equals(currentVersion, version, StringComparison.OrdinalIgnoreCase))
            {
                versionElement.SetAttributeValue("Version", version);
                changed = true;
            }
        }

        if (changed)
        {
            propsDoc.Save(propsPath!);
        }

        var csprojDocCpm = XDocument.Parse(await ReadAllTextAsyncCompat(testProjectPath, ct));
        var existingRef = csprojDocCpm.Descendants("PackageReference")
            .FirstOrDefault(e => string.Equals(
                (string?)e.Attribute("Include"), packageId, StringComparison.OrdinalIgnoreCase));

        if (existingRef is null)
        {
            var itemGroup = csprojDocCpm.Root!.Elements("ItemGroup")
                .FirstOrDefault(g => g.Elements("PackageReference").Any());

            if (itemGroup is null)
            {
                itemGroup = new XElement("ItemGroup");
                csprojDocCpm.Root.Add(itemGroup);
            }

            itemGroup.Add(new XElement("PackageReference",
                new XAttribute("Include", packageId)));
            changed = true;

            csprojDocCpm.Save(testProjectPath);
        }

        return changed;
    }

    /// <summary>
    ///     Löst CS0246 / CS0103 / CS1061 / CS1929-Fehler auf, die durch fehlende 'using'-Direktiven
    ///     und/oder fehlende Projekt-Referenzen entstehen. Typen werden solution-weit gesucht;
    ///     Framework-Typen kommen aus der statischen <see cref="WellKnownTypeToNamespace"/>-Tabelle.
    ///     Fehlt eine Projekt-Referenz auf das Projekt, in dem der Typ deklariert ist, wird sie
    ///     automatisch hinzugefügt.
    ///     Liefert den neuen Dateiinhalt, den unveränderten Inhalt (falls nur Projekt-Referenzen
    ///     ergänzt wurden) oder null (falls nichts geändert wurde).
    /// </summary>
    private async Task<string?> TryAddMissingUsingsAsync(
        string testClassPath,
        string testProjectPath,
        string? sourceProjectPath,
        string solutionDirectory,
        IReadOnlyList<string> compilerErrors,
        CancellationToken ct)
    {
        var missing = ExtractMissingIdentifiers(compilerErrors);
        if (missing.Count == 0) return null;

        var sourceTypes = await Task.Run(() => ScanTypeNamespaces(solutionDirectory), ct);

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
                // Mehrere Namespaces → mehrdeutig → lieber nichts tun
                var distinctNamespaces = locations
                    .Select(l => l.Namespace)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (distinctNamespaces.Count == 1)
                {
                    ns = distinctNamespaces[0];

                    // Nur referenzieren, wenn genau ein Projekt den Typ deklariert.
                    var distinctProjects = locations
                        .Select(l => l.ProjectPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (distinctProjects.Count == 1)
                    {
                        owningProject = distinctProjects[0];
                    }
                }
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

            if (owningProject is not null &&
                !string.Equals(Path.GetFullPath(owningProject), testProjectFullPath, StringComparison.OrdinalIgnoreCase))
            {
                projectsToReference.Add(owningProject);
            }
        }

        // Fehlende Projekt-Referenzen nachziehen (z. B. WPF-App mit MainWindow).
        string testProjectDir = Path.GetDirectoryName(testProjectPath)!;
        foreach (var refProject in projectsToReference)
        {
            if (!File.Exists(refProject)) continue;
            if (await ProjectHasReferenceAsync(testProjectPath, refProject, ct)) continue;

            await RunDotNetCliAsync(
                new[] { "add", testProjectPath, "reference", refProject },
                testProjectDir,
                ct);
        }

        // Wenn keine usings ergänzt wurden, aber Projekt-Referenzen geändert wurden,
        // den unveränderten Code zurückgeben, damit der Aufrufer neu baut.
        if (toAdd.Count == 0)
        {
            return projectsToReference.Count > 0 ? code : null;
        }

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
            // CS0246 = Typ/Namensraum nicht gefunden
            // CS0103 = Name existiert nicht im aktuellen Kontext
            // CS1061 = Typ hat kein Member 'X' (typisch für fehlende Extension-Methoden)
            // CS1929 = Extension-Methode 'X' kann nicht angewendet werden
            if (line.IndexOf("CS0246", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("CS0103", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("CS1061", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("CS1929", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            foreach (Match m in Regex.Matches(line, "'([^']+)'"))
            {
                // 'Mock<T>' oder 'Mock<>' → 'Mock'
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

    /// <summary>
    ///     Scannt alle .cs-Dateien der Solution (ohne bin/obj) und liefert für jeden Typnamen
    ///     die Liste der (Namespace, enthaltendes Projekt)-Paare.
    /// </summary>
    private static Dictionary<string, List<TypeLocation>> ScanTypeNamespaces(string solutionDirectory)
    {
        var map = new Dictionary<string, List<TypeLocation>>(StringComparer.Ordinal);
        string sep = Path.DirectorySeparatorChar.ToString();

        var namespaceRegex = new Regex(
            @"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)",
            RegexOptions.Multiline);

        var typeRegex = new Regex(
            @"^\s*(?:(?:public|internal|sealed|static|abstract|partial|readonly|unsafe)\s+)*" +
            @"(?:record\s+(?:class|struct)|class|struct|interface|enum|record)\s+([A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Multiline);

        foreach (var file in Directory.EnumerateFiles(solutionDirectory, "*.cs", SearchOption.AllDirectories))
        {
            string relative = file.Substring(solutionDirectory.Length);
            if (relative.Contains(sep + "obj" + sep) ||
                relative.Contains(sep + "bin" + sep))
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

            string? owningProject = FindContainingProject(file, solutionDirectory);
            if (owningProject is null) continue;

            foreach (Match typeMatch in typeRegex.Matches(text))
            {
                string typeName = typeMatch.Groups[1].Value;

                if (!map.TryGetValue(typeName, out var list))
                {
                    list = new List<TypeLocation>();
                    map[typeName] = list;
                }

                if (!list.Any(l =>
                        string.Equals(l.Namespace, ns, StringComparison.Ordinal) &&
                        string.Equals(l.ProjectPath, owningProject, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(new TypeLocation(ns, owningProject));
                }
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

    private static List<string> ExtractMissingNamespaceCandidates(
        IReadOnlyList<string> compilerErrors,
        IReadOnlyList<string> usingNamespaces)
    {
        var missingIdentifiers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in compilerErrors)
        {
            if (line.IndexOf("CS0246", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("CS0234", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("CS1061", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("CS1929", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            foreach (Match m in Regex.Matches(line, "'([^']+)'"))
            {
                missingIdentifiers.Add(Regex.Replace(m.Groups[1].Value, "<.*>$", string.Empty));
            }
        }

        if (missingIdentifiers.Count == 0)
        {
            return new List<string>();
        }

        var candidates = usingNamespaces
            .Where(ns => missingIdentifiers.Contains(ns) ||
                         ns.Split('.').Any(segment => missingIdentifiers.Contains(segment)))
            .ToList();

        foreach (var id in missingIdentifiers)
        {
            if (WellKnownTypeToNamespace.TryGetValue(id, out var ns) &&
                !string.Equals(ns, "System", StringComparison.Ordinal) &&
                !candidates.Contains(ns, StringComparer.Ordinal))
            {
                candidates.Add(ns);
            }
        }

        return candidates;
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
    ///     Wenn das Quellprojekt WPF / WinForms verwendet (net10.0-windows + UseWPF), muss das
    ///     Testprojekt dieselbe Windows-TFM und dieselben UseWPF/UseWindowsForms-Flags setzen.
    ///     Zusätzlich wird für WPF-Tests das Paket 'Xunit.StaFact' in der zur xUnit-Hauptversion
    ///     passenden Version referenziert (verhindert CS0433). Bei xUnit v3 wird OutputType=Exe
    ///     erzwungen. Diese Methode darf NIE eine Ausnahme werfen, damit der Auto-Fix-Flow
    ///     (TryAddMissingUsingsAsync) auf jeden Fall noch laufen kann.
    /// </summary>
    private async Task EnsureWindowsSettingsAsync(
        string testProjectPath,
        string? sourceProjectPath,
        string? preferredFramework,
        CancellationToken ct)
    {
        try
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

            // Multi-targeting würde doppelte Fehler produzieren; eine einzelne TFM verwenden.
            foreach (var multi in root.Descendants("TargetFrameworks").ToList())
            {
                multi.Remove();
                changed = true;
            }

            changed |= SetProperty(root, propertyGroup, "TargetFramework", windowsTfm);
            if (useWpf) changed |= SetProperty(root, propertyGroup, "UseWPF", "true");
            if (useWinForms) changed |= SetProperty(root, propertyGroup, "UseWindowsForms", "true");

            if (changed)
            {
                testDoc.Save(testProjectPath);
            }

            // xUnit-Hauptversion ermitteln (aus csproj + Directory.Packages.props).
            var flavor = await DetectXUnitFlavorAsync(testProjectPath, ct);

            bool packageChanged = false;

            // WPF-Tests brauchen STA-Threads → [WpfFact]/[UIFact].
            // Xunit.StaFact muss versionsgekoppelt zur xUnit-Hauptversion sein,
            // sonst entsteht CS0433 (FactAttribute in xunit.core UND xunit.v3.core).
            if (useWpf && flavor != XUnitFlavor.Unknown)
            {
                string staFactVersion = GetCompatibleStaFactVersion(flavor);
                packageChanged = await EnsurePackageReferenceWithVersionAsync(
                    testProjectPath, StaFactPackageId, staFactVersion, ct);
            }

            // xUnit v3 erfordert OutputType=Exe.
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
        catch
        {
            // Absichtlich geschluckt: Setup-Probleme hier dürfen den Auto-Fix-Flow
            // (TryAddMissingUsingsAsync) nicht verhindern.
        }
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

        // "-windows" TFMs sind für die --framework-Option des Templates nicht zulässig;
        // EnsureWindowsSettingsAsync setzt sie danach direkt in der csproj.
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
            // Muss vor "add reference" passieren (TFM-Kompatibilität).
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
    ///     Validiert das Testprojekt durch einen echten "dotnet build".
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