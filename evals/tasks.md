# Eval Tasks – NetAI.TestGenerator

Für jeden Lauf: gleicher Prompt, gleiche Aufgabe, gleiche Umgebung.
Ergebnis in scoring.md eintragen.

---

## Task 1 – Domain – Result-Validierung
Projekt: Core (beide TFMs)
Ziel: `TestSpecification` als record mit primary constructor.
Validierung: leeres `TargetType` oder leeres `TargetNamespace` -> Result.Fail.
Erfolgskriterien:
- Kein `throw` für Validierungsfehler.
- xUnit-Tests: 3 Cases (leer, whitespace, gültig).
- Core netstandard2.0: ja.

## Task 2 – Domain – Value Object
Projekt: Core
Ziel: `NamespaceName` record mit `Result<NamespaceName>`-Factory.
Regeln: muss mit Buchstabe beginnen, nur A-Za-z0-9._ erlaubt.
Erfolgskriterien:
- Tests decken gültig/ungültig/leer ab.
- Keine Regex-Compiled-Option, falls die auf netstandard2.0 Probleme macht.

## Task 3 – Application – Ports
Projekt: Core (beide TFMs)
Ziel: Ports `ILlmClient` und `ITestProjectWriter` definieren.
Signaturen async + CancellationToken.
Erfolgskriterien:
- Rückgabe `Task<Result<T>>`, kein `void`, kein `throw`.
- Keine Implementierung, keine DI-Registrierung.

## Task 4 – Application – Use Case
Projekt: Core
Ziel: `GenerateTestsForTypeQuery` + Handler mit primary constructor.
Ruft `ILlmClient` und `ITestProjectWriter`.
Erfolgskriterien:
- Rückgabe `Result<GeneratedTestProject>`.
- Tests mit Fake-Ports (kein Moq von Domain).
- CancellationToken durchgereicht.

## Task 5 – Application – Extension Method
Projekt: Core
Ziel: `string.ToSafeIdentifier()` als extension method.
Keine Utility-Klasse, keine statische Helper-Klasse.
Erfolgskriterien:
- Tests für Sonderzeichen, Umlaute, leeren String.
- Extension in `Application/Extensions/`.

## Task 6 – Application – Result-Migration
Projekt: Core
Ziel: Bestehenden Handler von Exceptions auf Result<T> umstellen.
Aufrufer anpassen.
Erfolgskriterien:
- Kein öffentlicher API-Bruch nach außen (Tasks-Facade unverändert).
- Tests grün, keine throw-basierten Tests mehr.
- Diff nur auf den betroffenen Handler + Tests.

## Task 7 – Infrastructure – LLM-Client
Projekt: Core
Ziel: `LlmClient` (HttpClient) timeout-robust, Result<T>-Rückgabe.
Kein neues Paket (kein Polly, kein Flurl).
Erfolgskriterien:
- Timeout/Cancellation -> Result.Fail, kein Throw nach außen.
- Tests mit HttpMessageHandler-Fake.
- Core netstandard2.0: ja (HttpClient ist da).

## Task 8 – Infrastructure – Datei-Writer
Projekt: Core
Ziel: `TestProjectWriter` schreibt atomar (temp + File.Replace/Move).
Erfolgskriterien:
- Keine Änderung der öffentlichen Signatur.
- Test: vorhandene Datei wird bei Fehler nicht beschädigt.
- netstandard2.0-kompatibel.

## Task 9 – Tasks – MSBuild Task
Projekt: Tasks
Ziel: `GenerateTestsTask : Microsoft.Build.Utilities.Task`.
Properties: `TargetAssembly`, `OutputDir`. Ruft Core-Facade.
Erfolgskriterien:
- netstandard2.0-kompatibel: ja (explizit im Output melden).
- Kein net10-API, kein `required`, kein DateOnly.
- `dotnet pack` läuft durch.

## Task 10 – Tasks – Public Facade
Projekt: Tasks
Ziel: `TestGeneratorFacade` (netstandard2.0) mit
`Task<Result<GeneratedTestProject>> GenerateAsync(...)`.
XML-Doc auf allen public Members.
Erfolgskriterien:
- Public Surface minimal.
- Keine Core-Typen leaken, die nicht Vertrag sind.
- `dotnet pack` läuft durch.

## Task 11 – Tasks – NuGet-Metadaten
Projekt: Tasks
Ziel: Vollständige Paket-Metadaten in `.csproj`:
PackageId, Version, Authors, Description, PackageLicenseExpression,
RepositoryUrl, PackageTags, PackageReadmeFile + README.md.
Erfolgskriterien:
- `dotnet pack -c Release -o ./artifacts` erzeugt valides .nupkg.
- README.md vorhanden und im Paket enthalten.

## Task 12 – Cross-Layer – Ende-zu-Ende
Projekte: Core + Tasks + Tests
Ziel: Command -> Handler -> Ports -> Infra -> Tasks-Facade.
Plus xUnit-Tests, die einen Fixture-Input in ein Testprojekt
mit xUnit-Testmethoden überführen.
Erfolgskriterien:
- Generiertes Testprojekt: net10.0, xUnit, deterministisch.
- Kein Live-LLM-Aufruf im generierten Test (Fake/Fixture).
- `dotnet test` auf der Solution grün.
- `dotnet pack` auf Tasks grün.
- Layering respektiert: Tasks enthält keine Logik.