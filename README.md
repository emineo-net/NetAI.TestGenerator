# NetAI.TestGenerator

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Roslyn](https://img.shields.io/badge/Roslyn-Microsoft.CodeAnalysis-blue)](https://github.com/dotnet/roslyn)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![NuGet](https://img.shields.io/nuget/v/NetAI.TestGenerator.Tasks.svg)](https://www.nuget.org/packages/NetAI.TestGenerator.Tasks)

> **AI-powered unit test generation for .NET** – with Roslyn semantic analysis, compile validation, and an LLM that only fills the prepared test skeleton.

---

## Table of Contents

- [Overview](#overview)
- [Why NetAI.TestGenerator?](#why-netaitestgenerator)
- [Architecture](#architecture)
- [Installation](#installation)
- [Configuration](#configuration)
- [Usage](#usage)
- [Supported Frameworks](#supported-frameworks)
- [Technical Highlights](#technical-highlights)
- [Examples](#examples)
- [API Overview](#api-overview)
- [Roadmap](#roadmap)
- [Troubleshooting](#troubleshooting)
- [Known Limitations](#known-limitations)
- [License](#license)
- [Contributing](#contributing)
- [Contact](#contact)

---

## Overview

**NetAI.TestGenerator** automates the creation of unit tests in .NET projects.  
The MSBuild task is injected into a host project as a NuGet package and analyzes the source code during the build. It then detects missing tests, generates a semantically correct test skeleton, and completes it using AI.

The central approach: **The AI does not have to invent the entire test.**  
Roslyn analyzes types, dependencies, access modifiers, async patterns, WPF/STA requirements, and testability. This produces a binding skeleton. The LLM only fills in the actual test body – which reduces hallucinations, saves tokens, and significantly improves compilability.

The project consists of two libraries:

| Project | Responsibility |
|---|---|
| **NetAI.TestGenerator.Core** | Roslyn analysis, prompt generation, LLM communication, test project management, compile validation, and repair logic. |
| **NetAI.TestGenerator.Tasks** | MSBuild task and NuGet package that is injected into the host project and starts generation during the build process. |

---

## Why NetAI.TestGenerator?

- **Semantic analysis instead of guessing from text:** Roslyn provides precise information about methods, parameters, return types, dependencies, and blockers.
- **AI only for the test body:** The binding skeleton remains authoritative. Usings, namespace, class name, fields, constructor, and test attributes are not changed by the model.
- **Compile validation included:** Generated tests are compiled against the real test project. Missing usings, NuGet packages, or incorrect types are handled automatically.
- **Multiple frameworks:** xUnit, NUnit, MSTest, as well as Moq, NSubstitute, and FakeItEasy.
- **WPF/STA support:** WPF types are detected; STA attributes and matching test attributes are taken into account.
- **MSBuild integration:** Runs automatically during the build – no separate CLI call required.
- **Configurable:** Framework, mocking, code style, AI endpoint, and generation behavior can be controlled via `aisettings.json`.

---

## Architecture

```text
Host Project
   │
   ├─ NetAI.TestGenerator.Tasks (MSBuild task, NuGet)
   │     └─ calls Core
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

**Flow:**

1. The MSBuild task is included via `NetAI.TestGenerator.Tasks.targets`.
2. `NetAI.TestGenerator.Core` loads the source code files and references.
3. Roslyn creates a compilation – either via MSBuild workspace, solution/project, or in-memory.
4. The `RoslynDllTestabilityAnalyzer` analyzes each method:
   - access modifiers
   - async/async void
   - static dependencies
   - concrete vs. abstract dependencies
   - mockability
   - WPF/STA relevance
   - test strategy: `Direct`, `Reflection`, `RefactorFirst`, `Skip`
5. The `UnitTestSkeletonGenerator` creates a binding test skeleton.
6. The LLM receives the skeleton and fills only the marked `AI AREA` section.
7. The `TestProjectManager` writes the test class, references the source project, resolves NuGet packages, and compiles.
8. If errors occur, automatic Roslyn fixes and optionally an AI repair run follow.

---

## Installation

### Prerequisites

- .NET SDK (tested with .NET 10; the task assembly is `netstandard2.0`-compatible)
- A solution/project directory with `.sln` or `.slnx`
- Optional: a running OpenAI-compatible LLM endpoint (local or hosted)

### Install the NuGet package

```bash
dotnet add package NetAI.TestGenerator.Tasks
```

Or directly in the `.csproj`:

```xml
<PackageReference Include="NetAI.TestGenerator.Tasks" Version="1.0.0" PrivateAssets="all" />
```

The package brings the targets with it and automatically copies `aisettings.json` and `aisettings-schema.json` into the project directory on the first build, if they do not already exist.

---

## Configuration

Create an `aisettings.json` in the host project:

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

### Important Options

| Property | Description |
|---|---|
| `buildConfigurationFilter` | `all`, `Debug`, or `Release` – controls when the task runs. |
| `environment.targetDotNetVersion` | Target framework for the test project. |
| `environment.testProjectName` | Name of the generated test project. |
| `frameworks.testFramework` | `xunit`, `nunit`, or `mstest`. |
| `frameworks.mockingFramework` | `moq`, `nsubstitute`, or `fakeiteasy`. |
| `frameworks.useFluentAssertions` | Enables FluentAssertions in generated tests. |
| `frameworks.useAutoFixture` | Enables AutoFixture for test data. |
| `frameworks.verbosePrompt` | Extended prompt explanations for the model. |
| `codeStyle.useFileScopedNamespace` | File-scoped namespaces in generated code. |
| `codeStyle.useAsyncSuffix` | Async methods with `Async` suffix. |
| `generationBehavior.testStrategy` | `unit`, `integration`, or `both`. |
| `generationBehavior.maxTestsPerClass` | Maximum number of tests per test class. |
| `aiConfiguration.baseUrl` | OpenAI-compatible endpoint. Must end with `/`. |
| `aiConfiguration.model` | Model name, e.g. `gpt-4o` or a local model. |
| `aiConfiguration.apiKey` / `apiKeyEnvVar` | API key directly or via environment variable. |

---

## Usage

### MSBuild Integration

Once the package is referenced and `aisettings.json` is present, the task runs automatically during the build.

Optionally, you can control the behavior via MSBuild properties:

```xml
<PropertyGroup>
  <TestGeneratorEnabled>true</TestGeneratorEnabled>
  <TestGeneratorSemanticAnalysisEnabled>true</TestGeneratorSemanticAnalysisEnabled>
  <TestGeneratorPromptOnly>false</TestGeneratorPromptOnly>
  <TestGeneratorFailOnError>false</TestGeneratorFailOnError>
</PropertyGroup>
```

- `TestGeneratorEnabled=false` disables the task.
- `TestGeneratorSemanticAnalysisEnabled=false` skips semantic analysis.
- `TestGeneratorPromptOnly=true` writes only prompts and drafts, without compiling.
- `TestGeneratorFailOnError=true` makes the build fail on errors.

### Prompt-Only Mode

```bash
dotnet build -p:TestGeneratorPromptOnly=true
```

In this mode, the generated prompts and test drafts are saved but not compiled against the test project. Ideal for debugging or pure prompt experiments.

### Programmatic Usage

```csharp
using NetAI.TestGenerator.Core;
using NetAI.TestGenerator.Core.Config;

var config = AiSettingsLoader.Load(projectDir);

// The class name is historical – the orchestrator generates tests.
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

## Supported Frameworks

| Area | Support |
|---|---|
| Test frameworks | xUnit, NUnit, MSTest |
| Mocking | Moq, NSubstitute, FakeItEasy |
| Assertions | FluentAssertions or framework asserts |
| Test data | AutoFixture optional |
| UI/STA | WPF detection, `Xunit.StaFact`, NUnit `[Apartment(ApartmentState.STA)]`, MSTest `[STATestMethod]` |
| Project formats | SDK-style, Central Package Management, `Directory.Packages.props` |
| Target frameworks | `netstandard2.0` (task), `net10.0` (Core) |

---

## Technical Highlights

### 1. Semantic Analysis with Roslyn

The `RoslynDllTestabilityAnalyzer` does not work on a text basis, but on real Roslyn symbols and operations:

- `IMethodSymbol`, `INamedTypeSymbol`, `IOperation`
- Detection of interfaces, abstract classes, static APIs, sealed classes
- Analysis of instance fields and their concrete types
- Classification of dependencies (`Interface`, `AbstractClass`, `ConcreteClass`, `StaticClass`, …)
- Mockability and recommended abstractions
- Construction of a call graph
- Detection of `async void`, `CancellationToken`, `Task`/`Task<T>`
- WPF/STA detection via base classes, interfaces, and attributes

This produces a `TestabilityReport` with a plain-text verdict, blockers, recommendations, and a concrete test strategy.

### 2. AI Fills Only the Skeleton

The `UnitTestSkeletonGenerator` creates a **binding skeleton**:

- correct usings
- namespace and class name
- mock fields and initialization
- constructor of the SUT
- matching test attribute
- optionally a skip attribute or refactoring hints
- clearly marked `AI AREA` section

The LLM receives this skeleton as an authoritative structure. It may only fill in the test body between the markers. The structure itself – including mock setup, fields, and attributes – remains unchanged. This drastically reduces typical AI errors such as invented types, wrong namespaces, or unsuitable mocking syntax.

### 3. Compile Validation and Automatic Repair

The `TestProjectManager` handles real validation:

- Creates or updates the test project.
- References the source project.
- Resolves missing NuGet packages.
- Supports Central Package Management.
- Adds `InternalsVisibleTo` if needed.
- Compiles with `dotnet build -t:Compile`.
- Detects environment issues such as file locks and distinguishes them from real test errors.

If compiler errors occur, several stages are applied:

1. **Roslyn fixes:** missing usings are added automatically.
2. **NuGet resolution:** missing packages are detected and added.
3. **AI repair:** if an error remains, the model receives the current code and the compiler errors and returns a corrected version.

### 4. WPF and STA Support

WPF types such as `Window`, `UserControl`, `DependencyObject`, or `DispatcherObject` are detected. If an STA requirement is present, the generator selects the matching test attribute – depending on the test framework and available packages. Otherwise, a skip attribute with an explanatory comment is generated.

### 5. Optional: .resx Translation

The `TranslationPromptBuilder` additionally supports AI-powered localization of `.resx` files. It detects the domain of the application and generates structured batch prompts for translations. The translation feature is optional and can be used independently of test generation.

---

## Examples

### Generated Test – xUnit + Moq

```csharp
using Moq;
using Xunit;

namespace MyProject.Tests
{
    public class OrderServiceTests
    {
        [Fact]
        public async Task ProcessOrder_EmptyCart_ThrowsInvalidOperationException()
        {
            var mockRepo = new Mock<IOrderRepository>();
            var service = new OrderService(mockRepo.Object);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ProcessOrderAsync(new Cart()));
        }
    }
}
```

### Skipped Test – Untestable `async void`

```csharp
[Fact(Skip = "Method is async void. Refactor into a testable async Task.")]
public void OnButtonClick_IsNotDirectlyTestable()
{
}
```

---

## API Overview

### `AiSettingsLoader`

Loads the `aisettings.json` configuration for a project directory.

```csharp
AiSettings Load(string projectDir)
```

### `ResxTranslationOrchestrator`

Main orchestrator for test generation.  
The class name is historical; it generates tests, not translations.

```csharp
ResxTranslationOrchestrator(AiSettings config)

Task<string> ProcessProjectAsync(
    string sourceFilePath,
    string testProjectDirectory,
    Action<string> logInfo,
    string solutionPath,
    bool promptOnly)
```

### `TestProjectManager`

Creates or updates the test project, resolves packages, references the source project, and compiles the generated tests.

### `RoslynDllTestabilityAnalyzer`

Performs semantic analysis and produces a `TestabilityReport` with test strategy, blockers, and recommendations.

### `UnitTestSkeletonGenerator`

Generates the binding test skeleton that the LLM must fill.

### `LocalLlmClient`

Handles communication with an OpenAI-compatible LLM endpoint.

---

## Roadmap

- **Enterprise version planned:** An Enterprise solution is planned for companies, using this repository as its foundation.
- Central configuration and policy enforcement
- Audit logs and reporting
- CI/CD gates and quality gates
- Advanced model and prompt management
- Support and maintenance offerings
- Integration into larger build and release pipelines

---

## Troubleshooting

### Common Issues

1. **Missing AI configuration**  
   Ensure `aisettings.json` is present in the project root or the expected build directory.

2. **Compilation errors**  
   Verify target framework versions and package references. Check whether the generated test project references the source project correctly.

3. **API access issues**  
   Confirm that the local LLM endpoint or model credentials are correctly configured. The `baseUrl` must end with `/`.

4. **File locks during build**  
   The generator distinguishes environment issues such as locked files from real test errors. Close any process that may be holding the test assembly.

5. **WPF/STA tests are skipped**  
   Ensure the required STA test packages are available for your test framework, e.g. `Xunit.StaFact` for xUnit.

6. **NuGet packages are missing**  
   The generator attempts to resolve missing packages automatically. With Central Package Management, ensure `Directory.Packages.props` is present and valid.

---

## Known Limitations

1. Best results require meaningful XML documentation comments and clear method names.
2. Complex dependency graphs may need manual adjustments.
3. Performance depends on the local LLM and available hardware.
4. `async void` methods are detected but cannot be tested directly; the generator emits a skip attribute and a refactoring hint.
5. WPF/STA support depends on the selected test framework and the availability of matching STA packages.
6. The AI repair loop improves compilability but does not guarantee perfect semantic correctness in every complex scenario.

---

## License

This project is licensed under the **MIT License**.  
You may use, modify, and distribute it freely – including commercially. See the `LICENSE` file for details.

---

## Contributing

Contributions are welcome!  
For larger changes, please open an issue first so we can align on the direction. For smaller fixes or improvements, you can open a pull request directly.

1. Fork the repository
2. Create a feature branch
3. Commit your changes
4. Push to the branch
5. Open a pull request

Please ensure your code follows the existing style and includes appropriate tests where applicable.

---

## Contact

Questions, ideas, or feedback?  
Open an issue in the repository or contact the team via the provided contact channels.