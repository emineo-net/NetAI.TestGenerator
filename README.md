# NetAI.TestGenerator

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Roslyn](https://img.shields.io/badge/Roslyn-Microsoft.CodeAnalysis-blue)](https://github.com/dotnet/roslyn)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![NuGet](https://img.shields.io/nuget/v/NetAI.TestGenerator.Tasks.svg)](https://www.nuget.org/packages/NetAI.TestGenerator.Tasks)

> **AI-gestützte Unit-Test-Generierung für .NET** – mit Roslyn-Semantikanalyse, Compile-Validierung und einem LLM, das lediglich das vorbereitete Test-Skeleton ausfüllt.

---

## Überblick

**NetAI.TestGenerator** automatisiert die Erstellung von Unit-Tests in .NET-Projekten.  
Der MSBuild-Task wird als NuGet-Paket in ein Host-Projekt injiziert und analysiert den Quellcode während des Builds. Anschließend werden fehlende Tests erkannt, ein semantisch korrektes Test-Skeleton erzeugt und per KI vervollständigt.

Der zentrale Ansatz: **Die KI muss nicht den gesamten Test erfinden.**  
Roslyn analysiert Typen, Abhängigkeiten, Zugriffsmodifizierer, Async-Muster, WPF/STA-Anforderungen und Testbarkeit. Daraus entsteht ein verbindliches Skeleton. Das LLM füllt nur noch den eigentlichen Testkörper aus – das reduziert Halluzinationen, spart Tokens und erhöht die Kompilierbarkeit erheblich.

Das Projekt besteht aus zwei Bibliotheken:

| Projekt | Aufgabe |
|---|---|
| **NetAI.TestGenerator.Core** | Roslyn-Analyse, Prompt-Erzeugung, LLM-Kommunikation, Testprojekt-Verwaltung, Compile-Validierung und Reparaturlogik. |
| **NetAI.TestGenerator.Tasks** | MSBuild-Task und NuGet-Paket, das in das Host-Projekt injiziert wird und die Generierung im Build-Prozess startet. |

---

## Warum NetAI.TestGenerator?

- **Semantische Analyse statt Textraten:** Roslyn liefert präzise Informationen über Methoden, Parameter, Rückgabetypen, Abhängigkeiten und Blocker.
- **KI nur für den Testkörper:** Das Binding-Skeleton bleibt verbindlich. Usings, Namespace, Klassenname, Felder, Konstruktor und Test-Attribute werden nicht vom Modell verändert.
- **Compile-Validierung inklusive:** Generierte Tests werden gegen das echte Testprojekt kompiliert. Fehlende Usings, NuGet-Pakete oder falsche Typen werden automatisch behandelt.
- **Mehrere Frameworks:** xUnit, NUnit, MSTest sowie Moq, NSubstitute und FakeItEasy.
- **WPF/STA-Unterstützung:** WPF-Typen werden erkannt; STA-Attribute und passende Testattribute werden berücksichtigt.
- **MSBuild-Integration:** Läuft automatisch während des Builds – ohne separaten CLI-Aufruf.
- **Konfigurierbar:** Über `aisettings.json` lassen sich Framework, Mocking, Code-Stil, KI-Endpunkt und Generierungsverhalten steuern.

---

## Architektur

```text
Host-Projekt
   │
   ├─ NetAI.TestGenerator.Tasks (MSBuild-Task, NuGet)
   │     └─ ruft Core auf
   │
   └─ NetAI.TestGenerator.Core
         ├─ RoslynDllTestabilityAnalyzer
         ├─ UnitTestSkeletonGenerator
         ├─ TestProjectManager
         ├─ TestProjectManagerCompilerService
         ├─ TestCodeProcessor / TestCodeBeautifier
         ├─ LocalLlmClient
         └─ AiPromptBuilder / PromptTemplates
```

**Ablauf:**

1. Der MSBuild-Task wird über `NetAI.TestGenerator.Tasks.targets` eingebunden.
2. `NetAI.TestGenerator.Core` lädt die Quellcodedateien und Referenzen.
3. Roslyn erstellt eine Compilation – wahlweise über MSBuild-Workspace, Solution/Project oder In-Memory.
4. Der `RoslynDllTestabilityAnalyzer` analysiert jede Methode:
   - Zugriffsmodifizierer
   - Async/Async-Void
   - statische Abhängigkeiten
   - konkrete vs. abstrakte Abhängigkeiten
   - Mockbarkeit
   - WPF/STA-Relevanz
   - Teststrategie: `Direct`, `Reflection`, `RefactorFirst`, `Skip`
5. Der `UnitTestSkeletonGenerator` erzeugt ein verbindliches Test-Skeleton.
6. Das LLM erhält das Skeleton und füllt nur den markierten `AI AREA`-Bereich.
7. Der `TestProjectManager` schreibt die Testklasse, referenziert das Quellprojekt, löst NuGet-Pakete und kompiliert.
8. Bei Fehlern folgen automatische Roslyn-Fixes und optional ein KI-Reparaturlauf.

---

## Installation

### Voraussetzungen

- .NET SDK (getestet mit .NET 10; die Task-Assembly ist `netstandard2.0`-kompatibel)
- Ein Solution-/Projektverzeichnis mit `.sln` oder `.slnx`
- Optional: laufender OpenAI-kompatibler LLM-Endpunkt (lokal oder gehostet)

### NuGet-Paket installieren

```bash
dotnet add package NetAI.TestGenerator.Tasks
```

Oder direkt in der `.csproj`:

```xml
<PackageReference Include="NetAI.TestGenerator.Tasks" Version="1.0.0" PrivateAssets="all" />
```

Das Paket bringt die Targets mit und kopiert beim ersten Build automatisch `aisettings.json` sowie `aisettings-schema.json` ins Projektverzeichnis, sofern sie noch nicht existieren.

---

## Konfiguration

Lege im Host-Projekt eine `aisettings.json` an:

```json
{
  "$schema": "aisettings-schema.json",
  "version": "1.1",
  "buildConfigurationFilter": "all",
  "environment": {
    "targetDotNetVersion": "net10.0",
    "testProjectName": "{ProjectName}.Tests"
  },
  "frameworks": {
    "testFramework": "xunit",
    "mockingFramework": "moq",
    "useFluentAssertions": true,
    "useAutoFixture": true,
    "verbosePrompt": true
  },
  "codeStyle": {
    "useFileScopedNamespace": true,
    "useAsyncSuffix": true,
    "maxLineLength": 120
  },
  "generationBehavior": {
    "testStrategy": "Both",
    "splitTestsByMethod": false,
    "maxTestsPerClass": 15
  },
  "aiConfiguration": {
    "baseUrl": "http://localhost:8080/",
    "model": "gpt-4o",
    "temperature": 0.2,
    "timeoutMinutes": 5,
    "systemPrompt": "You are an expert .NET developer. Write clean, maintainable code following Clean Code principles. Always use the Arrange-Act-Assert (AAA) pattern.",
    "apiKey": null,
    "apiKeyEnvVar": null
  }
}
```

### Wichtige Optionen

| Eigenschaft | Beschreibung |
|---|---|
| `buildConfigurationFilter` | `all`, `Debug` oder `Release` – steuert, wann der Task läuft. |
| `environment.targetDotNetVersion` | Ziel-Framework für das Testprojekt. |
| `environment.testProjectName` | Name des generierten Testprojekts. |
| `frameworks.testFramework` | `xunit`, `nunit` oder `mstest`. |
| `frameworks.mockingFramework` | `moq`, `nsubstitute` oder `fakeiteasy`. |
| `frameworks.useFluentAssertions` | Aktiviert FluentAssertions in generierten Tests. |
| `frameworks.useAutoFixture` | Aktiviert AutoFixture für Testdaten. |
| `frameworks.verbosePrompt` | Erweiterte Prompt-Erklärungen für das Modell. |
| `codeStyle.useFileScopedNamespace` | File-scoped Namespaces im generierten Code. |
| `codeStyle.useAsyncSuffix` | Async-Methoden mit `Async`-Suffix. |
| `generationBehavior.testStrategy` | `unit`, `integration` oder `both`. |
| `generationBehavior.maxTestsPerClass` | Maximale Anzahl Tests pro Testklasse. |
| `aiConfiguration.baseUrl` | OpenAI-kompatibler Endpunkt. Muss mit `/` enden. |
| `aiConfiguration.model` | Modellname, z. B. `gpt-4o` oder ein lokales Modell. |
| `aiConfiguration.apiKey` / `apiKeyEnvVar` | API-Key direkt oder über Umgebungsvariable. |

---

## Verwendung

### MSBuild-Integration

Sobald das Paket referenziert und `aisettings.json` vorhanden ist, läuft der Task automatisch während des Builds.

Optional kannst du das Verhalten über MSBuild-Properties steuern:

```xml
<PropertyGroup>
  <TestGeneratorEnabled>true</TestGeneratorEnabled>
  <TestGeneratorSemanticAnalysisEnabled>true</TestGeneratorSemanticAnalysisEnabled>
  <TestGeneratorPromptOnly>false</TestGeneratorPromptOnly>
  <TestGeneratorFailOnError>false</TestGeneratorFailOnError>
</PropertyGroup>
```

- `TestGeneratorEnabled=false` deaktiviert den Task.
- `TestGeneratorSemanticAnalysisEnabled=false` überspringt die semantische Analyse.
- `TestGeneratorPromptOnly=true` schreibt nur Prompts und Entwürfe, ohne zu kompilieren.
- `TestGeneratorFailOnError=true` lässt den Build bei Fehlern fehlschlagen.

### Prompt-Only-Modus

```bash
dotnet build -p:TestGeneratorPromptOnly=true
```

In diesem Modus werden die generierten Prompts und Testentwürfe gespeichert, aber nicht gegen das Testprojekt kompiliert. Ideal zum Debuggen oder für reine Prompt-Experimente.

### Programmatische Nutzung

```csharp
using NetAI.TestGenerator.Core;
using NetAI.TestGenerator.Core.Config;

var config = AiSettingsLoader.Load(projectDir);

// Der Klassenname ist historisch bedingt – der Orchestrator erzeugt Tests.
var orchestrator = new ResxTranslationOrchestrator(config);

var result = await orchestrator.ProcessProjectAsync(
    sourceFilePath: @"C:\src\MyApp\Services\OrderService.cs",
    testProjectDirectory: @"C:\src\MyApp\tests\UnitTests\MyApp.Tests",
    logInfo: Console.WriteLine,
    solutionPath: @"C:\src\MyApp\MyApp.sln",
    promptOnly: false);

Console.WriteLine(result);
```

---

## Unterstützte Frameworks

| Bereich | Unterstützung |
|---|---|
| Test-Frameworks | xUnit, NUnit, MSTest |
| Mocking | Moq, NSubstitute, FakeItEasy |
| Assertions | FluentAssertions oder Framework-Asserts |
| Testdaten | AutoFixture optional |
| UI/STA | WPF-Erkennung, `Xunit.StaFact`, NUnit `[Apartment(ApartmentState.STA)]`, MSTest `[STATestMethod]` |
| Projektformate | SDK-Style, Central Package Management, `Directory.Packages.props` |
| Ziel-Frameworks | `netstandard2.0` (Task), `net10.0` (Core) |

---

## Technische Highlights

### 1. Semantische Analyse mit Roslyn

Der `RoslynDllTestabilityAnalyzer` arbeitet nicht auf Textbasis, sondern auf echten Roslyn-Symbolen und -Operationen:

- `IMethodSymbol`, `INamedTypeSymbol`, `IOperation`
- Erkennung von Interfaces, abstrakten Klassen, statischen APIs, Sealed Classes
- Analyse von Instanzfeldern und deren konkreten Typen
- Klassifizierung von Abhängigkeiten (`Interface`, `AbstractClass`, `ConcreteClass`, `StaticClass`, …)
- Mockbarkeit und empfohlene Abstraktionen
- Aufbau eines Call-Graphs
- Erkennung von `async void`, `CancellationToken`, `Task`/`Task<T>`
- WPF/STA-Erkennung über Basisklassen, Interfaces und Attribute

Daraus entsteht ein `TestabilityReport` mit Klartext-Verdict, Blockern, Empfehlungen und einer konkreten Teststrategie.

### 2. KI füllt nur das Skeleton

Der `UnitTestSkeletonGenerator` erzeugt ein **Binding-Skeleton**:

- korrekte Usings
- Namespace und Klassenname
- Mock-Felder und Initialisierung
- Konstruktor des SUT
- passendes Testattribut
- ggf. Skip-Attribut oder Refactoring-Hinweise
- klar markierter `AI AREA`-Bereich

Das LLM erhält dieses Skeleton als verbindliche Struktur. Es darf nur den Testkörper zwischen den Markern ausfüllen. Die Struktur selbst – inklusive Mock-Setup, Felder und Attribute – bleibt unverändert. Das reduziert typische KI-Fehler wie erfundene Typen, falsche Namespaces oder unpassende Mocking-Syntax drastisch.

### 3. Compile-Validierung und automatische Reparatur

Der `TestProjectManager` übernimmt die reale Validierung:

- Erstellt oder aktualisiert das Testprojekt.
- Referenziert das Quellprojekt.
- Löst fehlende NuGet-Pakete auf.
- Unterstützt Central Package Management.
- Fügt bei Bedarf `InternalsVisibleTo` hinzu.
- Kompiliert mit `dotnet build -t:Compile`.
- Erkennt Umgebungsprobleme wie Dateisperren und unterscheidet sie von echten Testfehlern.

Bei Compilerfehlern greifen mehrere Stufen:

1. **Roslyn-Fixes:** fehlende Usings werden automatisch ergänzt.
2. **NuGet-Auflösung:** fehlende Pakete werden erkannt und hinzugefügt.
3. **KI-Reparatur:** bleibt ein Fehler, erhält das Modell den aktuellen Code und die Compilerfehler und liefert eine korrigierte Version.

### 4. WPF- und STA-Unterstützung

WPF-Typen wie `Window`, `UserControl`, `DependencyObject` oder `DispatcherObject` werden erkannt. Ist eine STA-Anforderung gegeben, wählt der Generator das passende Testattribut – abhängig vom Test-Framework und den verfügbaren Paketen. Andernfalls wird ein Skip-Attribut mit erklärendem Kommentar erzeugt.

### 5. Optional: .resx-Übersetzung

Der `TranslationPromptBuilder` unterstützt zusätzlich die KI-gestützte Lokalisierung von `.resx`-Dateien. Er erkennt die Domäne der Anwendung und erzeugt strukturierte Batch-Prompts für Übersetzungen. Die Übersetzungsfunktion ist optional und unabhängig von der Testgenerierung nutzbar.

---

## Roadmap

- **Enterprise-Version geplant:** Für Unternehmen ist später eine Enterprise-Lösung vorgesehen, die dieses Repository als Grundlage verwenden wird.
- Zentrale Konfiguration und Policy-Enforcement
- Audit-Logs und Reporting
- CI/CD-Gates und Quality-Gates
- Erweiterte Modell- und Prompt-Verwaltung
- Support- und Wartungsangebote
- Integration in größere Build- und Release-Pipelines

---

## Lizenz

Dieses Projekt steht unter der **MIT-Lizenz**.  
Du darfst es frei verwenden, ändern und weitergeben – auch kommerziell. Weitere Details findest du in der `LICENSE`-Datei.

---

## Beitragen

Beiträge sind willkommen!  
Bitte erstelle für größere Änderungen zuerst ein Issue, damit wir die Richtung abstimmen können. Für kleinere Fixes oder Verbesserungen kannst du direkt einen Pull Request öffnen.

---

## Kontakt

Fragen, Ideen oder Feedback?  
Eröffne ein Issue im Repository oder kontaktiere das Team über die angegebenen Kontaktkanäle.