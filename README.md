# NetAI.TestGenerator

**AI-powered unit test generation for .NET**

NetAI.TestGenerator analyzes C# source code with Roslyn and generates unit tests using a local Large Language Model (LLM). It integrates into existing test projects via NuGet packages or MSBuild tasks and helps increase test coverage with minimal manual effort.

> **Note:** This is the open-source community edition, released under the MIT License.  
> An Enterprise version with cloud LLM support, team collaboration, and CI/CD analytics is planned.

---

## Table of Contents

- Features
- Project Structure
- How It Works
- Supported Frameworks
- Installation
- Configuration
- Usage
  - As a Build Task
  - Programmatic API
- Examples
- Configuration Options
- API Overview
- Roadmap
- License
- Contributing
- Troubleshooting
- Known Limitations
- Contact

---

## Features

- **Roslyn-powered analysis** – Parses source files and extracts classes, methods, dependencies, and testability information.
- **AI-driven test generation** – Uses a local LLM to create meaningful unit tests from semantic context.
- **Multiple test frameworks** – Supports xUnit, NUnit, and MSTest.
- **Mocking framework support** – Integrates with Moq, NSubstitute, and FakeItEasy.
- **Optional assertion/test-data libraries** – FluentAssertions and AutoFixture.
- **Compile validation & auto-repair** – Validates generated code and attempts Roslyn-based or AI-driven repairs.
- **Smart skipping** – Detects existing tests, trivial methods, oversized methods, and untestable code such as `async void`.
- **Build task integration** – Runs automatically during build via MSBuild.
- **Two NuGet packages** – Clean separation between core logic and build tasks.
- **Configurable** – Fine-tune AI and test-generation behavior via `aisettings.json` or code.

---

## Project Structure

| Package | Target Framework | Description |
|---|---|---|
| `NetAI.TestGenerator.Core` | .NET Standard 2.0, .NET 10.0 | Core library with services, models, Roslyn analysis, AI generation, validation |
| `NetAI.TestGenerator.Tasks` | .NET Standard 2.0 | MSBuild tasks and build targets for automatic test generation |

---

## How It Works

1. **Source Analysis**  
   Reads a source file, parses it with Roslyn, and identifies the target class and its methods.

2. **Existing Test Detection**  
   Checks the existing test project for already covered methods and skips them.

3. **Semantic Analysis**  
   Uses Roslyn semantic models to analyze testability, dependencies, complexity, STA requirements, and to suggest a test strategy.

4. **Prompt Construction**  
   Builds a detailed prompt including project context, semantic analysis, binding skeleton, and relevant source snippets.

5. **AI Generation**  
   Sends the prompt to a local LLM and receives a complete test class or method.

6. **Validation & Repair**  
   Compiles the generated code. If errors occur, the system tries automatic Roslyn fixes and AI repair loops.

7. **Merging & Output**  
   Successfully compiled tests are merged into the existing test project and written to disk.

---

## Supported Frameworks

| Category | Supported |
|---|---|
| Test Framework | xUnit, NUnit, MSTest |
| Mocking | Moq, NSubstitute, FakeItEasy |
| Assertions | FluentAssertions (optional) |
| Test Data | AutoFixture (optional) |

---

## Installation

### NuGet Packages

```bash
dotnet add package NetAI.TestGenerator.Core
dotnet add package NetAI.TestGenerator.Tasks
```

Or via `.csproj`:

```xml
<ItemGroup>
  <PackageReference Include="NetAI.TestGenerator.Core" Version="1.0.0" />
  <PackageReference Include="NetAI.TestGenerator.Tasks" Version="1.0.0" />
</ItemGroup>
```

### Build Task

Import the MSBuild targets into your test project:

```xml
<Import Project="NetAI.TestGenerator.Tasks\build\NetAI.TestGenerator.Tasks.targets" />
```

Optional explicit target:

```xml
<Target Name="GenerateTests" BeforeTargets="Build">
  <NetAIGenerateTests
      SourceFile="$(MSBuildProjectDirectory)\MyClass.cs"
      TestProjectDirectory="$(MSBuildProjectDirectory)\Tests"
      SolutionPath="$(SolutionPath)" />
</Target>
```

> The exact task name and parameters may vary by package version. See the package documentation for the final API.

---

## Configuration

Configuration is provided via `aisettings.json` or programmatically through `AiSettings`.

Example `aisettings.json`:

```json
{
  "aiModel": "local-llm",
  "maxTokens": 1000,
  "temperature": 0.7,
  "topP": 1,
  "frequencyPenalty": 0,
  "presencePenalty": 0,
  "testGeneration": {
    "testFramework": "xunit",
    "mockingFramework": "moq",
    "useFluentAssertions": true,
    "useAutoFixture": false,
    "includeUnitTests": true,
    "includeIntegrationTests": false,
    "verbosePrompt": true,
    "maxMethodChars": 25000
  },
  "codeAnalysis": {
    "includeComments": true,
    "includeXmlDocumentation": true
  }
}
```

---

## Usage

### As a Build Task

Once configured, the generator runs automatically during build. It will:

- Analyze the specified source file(s)
- Generate tests for uncovered methods
- Write them into the existing test project directory
- Skip methods that already have tests or are not testable

### Programmatic API

```csharp
using NetAI.TestGenerator.Core;
using NetAI.TestGenerator.Core.Services;
using NetAI.TestGenerator.Core.Models;

var service = new TestGeneratorService
{
    Configuration = new AiSettings
    {
        AiModel = "local-llm",
        MaxTokens = 1000,
        Temperature = 0.7,
        TestGeneration = new TestGenerationSettings
        {
            TestFramework = "xunit",
            MockingFramework = "moq",
            UseFluentAssertions = true,
            UseAutoFixture = false,
            VerbosePrompt = true,
            MaxMethodChars = 25000
        }
    }
};

var result = await service.GenerateTestsAsync(
    fullyQualifiedTypeName: "MyNamespace.MyClass",
    methodName: "MyMethod",
    cancellationToken: CancellationToken.None);

string testFileContent = service.CreateNewTestClassFile(
    projectName: "MyProject.Tests",
    className: "MyClassTests",
    fullyQualifiedTypeName: "MyNamespace.MyClass",
    methodName: "MyMethod",
    frameworkUsing: "net8.0");

Console.WriteLine(testFileContent);
```

Optional compile validation:

```csharp
using NetAI.TestGenerator.Core.Services;

ICompilerService compiler = new CompilerService();
var compilationResult = await compiler.CompileProjectAsync(
    projectPath: @"C:\MyProject.Tests\MyProject.Tests.csproj",
    cancellationToken: CancellationToken.None);
```

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

### Skipped Test – untestable `async void`

```csharp
[Fact(Skip = "Method is async void. Refactor into a testable async Task.")]
public void OnButtonClick_IsNotDirectlyTestable()
{
}
```

---

## Configuration Options

| Option | Type | Default | Description |
|---|---|---|---|
| `aiModel` | string | `local-llm` | AI model identifier |
| `maxTokens` | int | `1000` | Maximum tokens for generation |
| `temperature` | double | `0.7` | Sampling temperature |
| `topP` | double | `1` | Nucleus sampling |
| `frequencyPenalty` | double | `0` | Frequency penalty |
| `presencePenalty` | double | `0` | Presence penalty |
| `testGeneration.testFramework` | string | `xunit` | `xunit`, `nunit`, `mstest` |
| `testGeneration.mockingFramework` | string | `moq` | `moq`, `nsubstitute`, `fakeiteasy` |
| `testGeneration.useFluentAssertions` | bool | `true` | Use FluentAssertions if available |
| `testGeneration.useAutoFixture` | bool | `false` | Use AutoFixture for test data |
| `testGeneration.includeUnitTests` | bool | `true` | Generate unit tests |
| `testGeneration.includeIntegrationTests` | bool | `false` | Generate integration tests |
| `testGeneration.verbosePrompt` | bool | `true` | Include detailed instructions in prompts |
| `testGeneration.maxMethodChars` | int | `25000` | Max method size to attempt generation |
| `codeAnalysis.includeComments` | bool | `true` | Include source comments in analysis |
| `codeAnalysis.includeXmlDocumentation` | bool | `true` | Include XML documentation in analysis |

---

## API Overview

### `TestGeneratorService`

Main entry point for generating tests.

```csharp
Task<TestGenerationResult> GenerateTestsAsync(
    string fullyQualifiedTypeName,
    string methodName,
    CancellationToken cancellationToken)

string CreateNewTestClassFile(
    string projectName,
    string className,
    string fullyQualifiedTypeName,
    string methodName,
    string frameworkUsing)
```

Property:

```csharp
AiSettings Configuration { get; set; }
```

### `ICompilerService`

Compiles a test project and returns compilation results.

```csharp
Task<CompilationResult> CompileProjectAsync(
    string projectPath,
    CancellationToken cancellationToken)
```

---

## Roadmap

- Publish stable NuGet packages
- Finalize MSBuild task documentation
- Support additional LLM providers
- Test coverage analysis integration
- Enterprise version with:
  - Cloud LLM support
  - Team dashboards
  - CI/CD pipeline analytics
  - Advanced refactoring suggestions

---

## License

This project is licensed under the **MIT License**.

See the [LICENSE](LICENSE) file for details.

---

## Contributing

Contributions are welcome! Please open an issue or submit a pull request.

1. Fork the repository
2. Create a feature branch
3. Commit your changes
4. Push to the branch
5. Open a pull request

Please ensure your code follows the existing style and includes appropriate tests.

---

## Troubleshooting

### Common Issues

1. **Missing AI configuration** – Ensure `aisettings.json` is present in the project root or `bin` directory.
2. **Compilation errors** – Verify target framework versions and package references.
3. **API access issues** – Confirm the local LLM endpoint or model credentials are correctly configured.

### Known Limitations

1. Best results require meaningful XML documentation comments.
2. Complex dependency graphs may need manual adjustments.
3. Performance depends on the local LLM and hardware.

---

## Contact

For support or questions, please open an issue on the GitHub repository.