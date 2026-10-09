## NetAI.TestGenerator

**AI-powered unit test generation for .NET**

NetAI.TestGenerator is a Roslyn-based tool that analyzes your C# source code and automatically generates unit tests using a local Large Language Model (LLM). It integrates seamlessly into your build process as a NuGet package or build task, helping you achieve higher test coverage with minimal manual effort.

> **Note:** This is the open-source community edition, released under the MIT License.
> 
> An Enterprise version with advanced features (e.g., cloud LLM support, team collaboration, CI/CD analytics) is planned for the future.

___

## Table of Contents

-   Features
    
-   How It Works
    
-   Supported Frameworks
    
-   Installation
    
-   Configuration
    
-   Usage
    
    -   As a Build Task
        
    -   Programmatic API
        
-   Examples
    
-   Configuration Options
    
-   Roadmap
    
-   License
    
-   Contributing
    

___

## Features

-   **Roslyn-powered analysis** – Parses source files, extracts classes, methods, dependencies, and testability information.
    
-   **AI-driven test generation** – Uses a local LLM to create meaningful unit tests based on semantic context.
    
-   **Multiple test frameworks** – Supports xUnit, NUnit, and MSTest.
    
-   **Mocking framework support** – Integrates with Moq, NSubstitute, and FakeItEasy.
    
-   **Compile validation & auto-repair** – Validates generated code and automatically fixes compilation errors (including Roslyn-based fixes and AI repair loops).
    
-   **Smart skipping** – Detects existing tests, trivial methods, oversized methods, and untestable code (e.g., `async void`).
    
-   **Build task integration** – Can run automatically during your build via MSBuild.
    
-   **NuGet package** – Easy to add to any .NET project.
    
-   **Configurable** – Fine-tune behavior via `AiTestingConfig`.
    

___

## How It Works

1.  **Source Analysis**
    
    The orchestrator reads a source file, parses it with Roslyn, and identifies the target class and its methods.
    
2.  **Existing Test Detection**
    
    It checks the test project for already covered methods and skips them.
    
3.  **Semantic Analysis**
    
    Using Roslyn semantic models, it analyzes method testability, dependencies, complexity, STA requirements, and suggests a test strategy (Direct, Skip, RefactorFirst, Reflection).
    
4.  **Prompt Construction**
    
    A detailed prompt is built, including:
    
    -   Project context (frameworks, packages, global usings)
        
    -   Semantic analysis (dependencies, call graph, recommendations)
        
    -   A binding skeleton (if available) to enforce structure
        
    -   Relevant source code snippets
        
5.  **AI Generation**
    
    The prompt is sent to a local LLM (via `LocalLlmClient`). The AI returns a complete test class or method.
    
6.  **Validation & Repair**
    
    The generated code is compiled. If errors occur, the system attempts:
    
    -   Automatic Roslyn fixes (missing usings, etc.)
        
    -   AI-driven repair loops (up to a configurable number of attempts)
        
7.  **Merging & Output**
    
    Successfully compiled tests are merged into a single test class file and written to the test project.
    

___

## Supported Frameworks

|Category|Supported Frameworks|
|---|---|
|Test Framework|xUnit, NUnit, MSTest|
|Mocking|Moq, NSubstitute, FakeItEasy|
|Assertions|FluentAssertions (optional)|
|Test Data|AutoFixture (optional)|

___

## Installation

### NuGet Package

```
dotnet add package NetAI.TestGenerator
```

> Replace `NetAI.TestGenerator` with the actual package ID once published.

### Build Task

Add the package reference to your test project (or a dedicated build project) and configure it to run during build. Example `.csproj` snippet:

```
<ItemGroup>
  <PackageReference Include="NetAI.TestGenerator" Version="1.0.0" />
</ItemGroup>

<Target Name="GenerateTests" BeforeTargets="Build">
  <NetAIGenerateTests SourceFile="$(MSBuildProjectDirectory)\MyClass.cs"
                      TestProjectDirectory="$(MSBuildProjectDirectory)\Tests"
                      SolutionPath="$(SolutionPath)" />
</Target>
```

> The exact MSBuild task name and parameters will be documented once the build task is finalized.

___

## Configuration

Configuration is provided via `AiTestingConfig`. You can set it programmatically or through a configuration file (e.g., `aitesting.json`).

Example `aitesting.json`:

```
{
  "Frameworks": {
    "TestFramework": "xunit",
    "MockingFramework": "moq",
    "UseFluentAssertions": true,
    "UseAutoFixture": false,
    "VerbosePrompt": true
  },
  "MaxMethodChars": 25000
}
```

___

## Usage

### As a Build Task

Once configured, the generator runs automatically during your build. It will:

-   Analyze the specified source file(s)
    
-   Generate tests for uncovered methods
    
-   Write them to the test project directory
    
-   Skip methods that already have tests or are not testable
    

### Programmatic API

You can also use the orchestrator directly in your own code:

```
using NetAI.TestGenerator.Core;

var orchestrator = new ResxTranslationOrchestrator(new AiTestingConfig
{
    Frameworks = new FrameworkConfig
    {
        TestFramework = "xunit",
        MockingFramework = "moq",
        UseFluentAssertions = true
    },
    MaxMethodChars = 30000
});

string result = await orchestrator.ProcessProjectAsync(
    sourceFilePath: @"C:\MyProject\MyClass.cs",
    testProjectDirectory: @"C:\MyProject.Tests",
    logInfo: msg => Console.WriteLine(msg),
    solutionPath: @"C:\MyProject.sln"
);

Console.WriteLine(result); 
```

> **Note:** `ResxTranslationOrchestrator` is the current entry point. The name may change in future releases to better reflect its purpose.

___

## Examples

### Generated Test (xUnit + Moq)

```
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

            
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ProcessOrderAsync(new Cart()));
        }
    }
}
```

### Skipped Test (untestable `async void`)

```
[Fact(Skip = "Method is async void. Refactor into a testable async Task.")]
public void OnButtonClick_IsNotDirectlyTestable()
{
    
    
    
    
}
```

___

## Configuration Options

|Option|Type|Default|Description|
|---|---|---|---|
|`Frameworks.TestFramework`|string|`xunit`|Test framework: `xunit`, `nunit`, `mstest`|
|`Frameworks.MockingFramework`|string|`moq`|Mocking framework: `moq`, `nsubstitute`, `fakeiteasy`|
|`Frameworks.UseFluentAssertions`|bool|`true`|Use FluentAssertions if available|
|`Frameworks.UseAutoFixture`|bool|`false`|Use AutoFixture for test data|
|`Frameworks.VerbosePrompt`|bool|`true`|Include detailed instructions in prompts|
|`MaxMethodChars`|int|`25000`|Maximum method size (in characters) to attempt test generation|

___

## Roadmap

-   □
    
    Publish stable NuGet package
    
-   □
    
    Add MSBuild task documentation
    
-   □
    
    Support for more LLM providers (OpenAI, Azure OpenAI)
    
-   □
    
    Test coverage analysis integration
    
-   □
    
    Enterprise version with:
    
    -   Cloud LLM support
        
    -   Team dashboards
        
    -   CI/CD pipeline analytics
        
    -   Advanced refactoring suggestions
        

___

## License

This project is licensed under the **MIT License**.

See the [LICENSE](https://license/) file for details.

___

## Contributing

Contributions are welcome! Please open an issue or submit a pull request.

1.  Fork the repository
    
2.  Create a feature branch
    
3.  Commit your changes
    
4.  Push to the branch
    
5.  Open a pull request
    

Please ensure your code follows the existing style and includes appropriate tests.

___

## Acknowledgments

-   Built with [Roslyn](https://github.com/dotnet/roslyn)
    
-   Uses local LLMs for privacy-friendly AI generation
    
-   Inspired by the need for faster, smarter unit testing in .NET
    

