# NetAI.TestGenerator

[![NuGet](https://img.shields.io/nuget/v/NetAI.TestGenerator.Tasks.svg?label=NuGet%20(Tasks))](https://www.nuget.org/packages/NetAI.TestGenerator.Tasks)
[![NuGet Downloads](https://img.shields.io/nuget/dt/NetAI.TestGenerator.Tasks.svg)](https://www.nuget.org/packages/NetAI.TestGenerator.Tasks)
[![.NET](https://img.shields.io/badge/.NET-netstandard2.0%20%7C%20net10.0-512BD4)](https://dotnet.microsoft.com/)
[![Roslyn](https://img.shields.io/badge/Roslyn-Microsoft.CodeAnalysis-blue)](https://github.com/dotnet/roslyn)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](#contributing)

> ### Stop writing the same test scaffolding over and over.
> **NetAI.TestGenerator** turns Roslyn semantic analysis into a *binding test skeleton*, then lets an LLM fill in only the interesting part — the test body — and validates the result by actually compiling it.
> Fewer hallucinations. Fewer tokens. More green checkmarks.

---

## Table of Contents

- [What Is It?](#what-is-it)
- [The Three Pillars](#the-three-pillars)
- [Meet LLMRunner](#meet-llmrunner)
- [Features](#features)
- [How It Works](#how-it-works)
- [Requirements](#requirements)
- [Pick Your Entry Point](#pick-your-entry-point)
  - [1. MSBuild Task (NuGet)](#1-msbuild-task-nuget)
  - [2. Command-Line Tool (`netai`)](#2-command-line-tool-netai)
  - [3. Programmatic API](#3-programmatic-api)
- [Configuration — `aisettings.json`](#configuration--aisettingsjson)
  - [Getting Started with Ollama + Qwen 2.5 14B](#getting-started-with-ollama--qwen-25-14b)
  - [AI Connection Variants](#ai-connection-variants)
  - [Rules & Pitfalls](#rules--pitfalls)
- [Supported Frameworks](#supported-frameworks)
- [Repository Layout](#repository-layout)
- [Roadmap](#roadmap)
- [License](#license)
- [Contributing](#contributing)
- [Acknowledgements](#acknowledgements)

---

## What Is It?

Writing unit tests is the chore every .NET developer agrees is important — and the one most often postponed. The usual outcomes:

- **Manual tests** → consistent quality, but nobody has time for the 40% coverage nobody wrote.
- **Template-driven generators** → produce tests that compile and assert nothing meaningful.
- **Naïve "let the LLM write everything"** → endless hallucinated types, invented members, wrong using directives, dead-on-arrival code.

**NetAI.TestGenerator** takes a middle path:

- **Roslyn does the facts.** It analyzes method signatures, dependencies, access modifiers, async patterns, WPF/STA requirements, and static blockers — and produces a *binding skeleton* that is guaranteed to be structurally correct.
- **The LLM fills only the interesting part.** The skeleton's usings, namespace, class name, fields, constructor, and test attributes are authoritative. The model writes the test body.
- **Compilation validates the result.** Every generated test is compiled against the real test project. Missing usings get fixed, missing NuGet packages get resolved, and if all else fails, the model gets one targeted repair attempt.
- **You drive it from where you already are.** An MSBuild task on every build, or a `netai` CLI command when you want it on demand.

The result: fewer invented APIs, fewer "green tests that assert nothing", and a real chance that the test you never had time to write actually shows up in the pull request.

---

## The Three Pillars

NetAI.TestGenerator is split into three projects, each with a clear job:

| Project | What it is | Audience |
|---|---|---|
| **`NetAI.TestGenerator.Core`** | The engine. Roslyn analysis, skeleton generation, prompt building, LLM client, test-project management, compile validation. | Library authors / advanced users. |
| **`NetAI.TestGenerator.Tasks`** | An MSBuild task shipped as a NuGet package. Runs the engine during the build. | Every .NET project that wants test generation on autopilot. |
| **`NetAI.TestGenerator.Cli`** | A `dotnet tool` named **`netai`**. Runs the same engine on demand. | CI pipelines, one-off jobs, prompt debugging. |

Both `Tasks` and `Cli` sit on top of `Core`. Same engine, same `aisettings.json`, same results. Pick whichever fits your workflow — or use both.

Detailed documentation lives next to each project:

- **Tasks** → [`src/Libraries/NetAI.TestGenerator.Tasks/README.md`](src/Libraries/NetAI.TestGenerator.Tasks/README.md)
- **CLI** → [`src/Libraries/NetAI.TestGenerator.Cli/README.md`](src/Libraries/NetAI.TestGenerator.Cli/README.md)

---

## Meet LLMRunner

> **A companion tool from the same authors.** You don't need it to use NetAI.TestGenerator — but it removes the last excuse not to try a local model.

**LLMRunner** is a small WPF application that turns "I'd like to try a local LLM" into "I'm chatting with one in two minutes":

- **Pick from popular models** — a curated list with direct **HuggingFace** download links, so you don't have to hunt for GGUF files and quantizations that actually work.
- **Chat with the model before wiring it up** — sanity-check the output quality on your own prompts, not on someone else's benchmark.
- **Serve it on `http://localhost:8080/` with one click** — exactly the address `aisettings.json` expects by default. No environment variables, no command-line flags, no guesswork.

That means the whole "get a local LLM running" step collapses into: open LLMRunner → pick a model → click *Serve*. From there, `dotnet build` will already find your model.

> 👉 Find **LLMRunner** in the same repository profile or the releases page.

If you prefer to run a model server yourself (Ollama, LM Studio, llama.cpp), that's fine too — NetAI.TestGenerator speaks to any OpenAI-compatible endpoint. The sections below cover both.

---

## Features

- **Roslyn-based semantic analysis** — Real symbols and operations, not regex. `IMethodSymbol`, `INamedTypeSymbol`, `IOperation`.
- **Binding test skeleton** — Usings, namespace, class name, fields, mocks, constructor, and test attribute are all prepared and authoritative.
- **LLM fills only the AI area** — The interesting part. The structure stays untouched.
- **Compile validation** — Generated tests are compiled against the real test project — including `InternalsVisibleTo` and NuGet resolution.
- **Automatic repair loop** — Missing usings are added by Roslyn. Missing NuGet packages are detected and installed. If all else fails, the LLM gets one targeted repair prompt.
- **Multi-framework** — xUnit, NUnit, MSTest, with Moq, NSubstitute, or FakeItEasy.
- **WPF / STA aware** — Detects WPF base types, picks the matching STA test attribute (e.g. `[StaFact]`) when available.
- **`async void` and untestable patterns** — Detected and handled with skip stubs plus concrete refactoring hints.
- **Central Package Management support** — Reads and writes `Directory.Packages.props`.
- **Safe by default** — Skips design-time builds, respects multi-targeting, can be disabled per build.
- **Two entry points, one engine** — NuGet build task and `netai` CLI share the same `Core`.
- **Companion local-LLM runner** — LLMRunner gets you from zero to `localhost:8080` without a terminal.

---

## How It Works

The same pipeline runs whether you trigger it from MSBuild or the CLI:

1. **Resource Discovery**
   Source files are collected from `@(Compile)`, a `.csproj`, a directory scan, or an already-loaded Roslyn compilation.

2. **Compilation Setup**
   An in-memory Roslyn compilation is built from source files plus `@(ReferencePath)`. In the CLI, MSBuild can also load a real `.sln` / `.csproj` for the most accurate semantic context.

3. **Semantic Analysis**
   `RoslynDllTestabilityAnalyzer` inspects each method:
   - access modifiers
   - `async` / `async void`
   - static dependencies and concrete vs. abstract dependencies
   - mockability
   - WPF/STA relevance
   - test strategy: `Direct`, `Reflection`, `RefactorFirst`, `Skip`

4. **Skeleton Generation**
   `UnitTestSkeletonGenerator` emits a *binding skeleton*: correct usings, namespace, class name, mock fields, constructor, test attribute — plus a clearly marked `AI AREA`.

5. **Prompt Construction**
   The prompt contains the skeleton, the semantic analysis, the project context, and a class skeleton (fields, ctor, method under test, and called helpers). The model is told: preserve the structure, fill only the AI area.

6. **LLM Call**
   A single call per method via `LocalLlmClient` (OpenAI-compatible). Responses are parsed for the test class or method.

7. **Compile Validation**
   `TestProjectManager` writes the test class, references the source project, resolves missing NuGet packages, and runs `dotnet build -t:Compile`.

8. **Automatic Repair** *(up to one AI attempt)*
   - Missing usings → added by Roslyn, no LLM round-trip.
   - Missing NuGet packages → detected and installed.
   - Semantic errors (invented types, wrong member names) → the model gets a focused repair prompt with concrete hints.
   - Environment issues (locked assemblies) → detected and distinguished from real code errors.

9. **Write-Back**
   The final, compiled test methods are merged into a single test class file per source file, formatted with Roslyn.

Progress, warnings, and errors surface as native MSBuild messages (task) or as Spectre.Console output (CLI).

---

## Requirements

- .NET SDK **10.0 or later** for the host project. The task itself runs on `netstandard2.0` inside MSBuild's runtime.
- For the CLI: .NET **10.0 SDK** or later runtime.
- An **OpenAI-compatible endpoint**:
  - **Recommended for local use**: **LLMRunner** — pick a model, click *Serve*, done.
  - **Also supported**: [llama.cpp server](https://github.com/ggerganov/llama.cpp), [LM Studio](https://lmstudio.ai/), [Ollama](https://ollama.com/), …
  - **Hosted**: OpenAI, Azure OpenAI, or any gateway exposing `/v1/chat/completions`.
- A solution or project with a `.sln` / `.slnx` file discoverable upward from the project directory.

---

## Pick Your Entry Point

### 1. MSBuild Task (NuGet)

The zero-touch option: test generation happens automatically during every build.

```bash
dotnet add package NetAI.TestGenerator.Tasks
```

Then build once, edit `aisettings.json`, and build again. Missing tests appear in the test project before your code finishes compiling.

Full documentation, MSBuild properties, and troubleshooting:
👉 **[`src/Libraries/NetAI.TestGenerator.Tasks/README.md`](src/Libraries/NetAI.TestGenerator.Tasks/README.md)**

### 2. Command-Line Tool (`netai`)

For CI pipelines, ad-hoc runs, and prompt debugging:

```bash
dotnet tool install --global NetAI.TestGenerator.Cli

netai init     ./src/MyApp                          # scaffold aisettings.json
netai analyse  ./src/MyApp/Services/OrderService.cs # what does the analyzer see?
netai generate ./src/MyApp/Services/OrderService.cs # generate tests now
```

Full command reference, options, exit codes, and CI examples:
👉 **[`src/Libraries/NetAI.TestGenerator.Cli/README.md`](src/Libraries/NetAI.TestGenerator.Cli/README.md)**

### 3. Programmatic API

Drive the engine from your own code — perfect for custom tooling:

```csharp
using NetAI.TestGenerator.Core;
using NetAI.TestGenerator.Core.Config;

var config = AiSettingsLoader.Load(projectDir);

// The class name is historical – this orchestrator generates tests, not translations.
var orchestrator = new TetsGeneratorOrchestrator(config);

var result = await orchestrator.ProcessProjectAsync(
    sourceFilePath: @"C:\src\MyApp\Services\OrderService.cs",
    testProjectDirectory: @"C:\src\MyApp\tests\UnitTests\MyApp.Tests",
    logInfo: Console.WriteLine,
    solutionPath: @"C:\src\MyApp\MyApp.sln",
    promptOnly: false);

Console.WriteLine(result); // "ok" on success
```

> **Note:** `TetsGeneratorOrchestrator` (yes, the typo is real, and it is shipped) is the current entry point. The name may be corrected in a future release — a breaking change we'll flag clearly.

---

## Configuration — `aisettings.json`

Whether you use the task or the CLI, everything is driven by a single `aisettings.json` at the project root. It mirrors the `AiTestingConfig` record in `Core`.

### Minimal example

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

### Getting Started with Ollama + Qwen 2.5 14B

**Qwen 2.5 14B** is a strong, compact, open-weights model that fits comfortably on a 16 GB machine, follows structured prompt formats (like the binding skeleton) reliably, and produces high-quality C# test code. It's an excellent default for NetAI.TestGenerator if you don't already have a preferred model.

**Step 1 — Install Ollama** from [ollama.com/download](https://ollama.com/download).

**Step 2 — Pull the model and start the server:**

```bash
ollama pull qwen2.5:14b
ollama serve
```

Ollama listens on `http://localhost:11434/` and exposes an OpenAI-compatible endpoint at `/v1/chat/completions` — exactly the shape NetAI.TestGenerator expects.

**Step 3 — Point `aisettings.json` at it:**

```json
"aiConfiguration": {
  "baseUrl": "http://localhost:11434/",
  "model": "qwen2.5:14b",
  "temperature": 0.2,
  "timeoutMinutes": 10,
  "systemPrompt": "You are an expert .NET developer. Write clean, maintainable code following Clean Code principles. Always use the Arrange-Act-Assert (AAA) pattern.",
  "apiKey": null,
  "apiKeyEnvVar": null
}
```

That's it. Build (or run `netai generate`) and the model is picked up.

> **Tips:**
> - The trailing slash on `baseUrl` is required.
> - `timeoutMinutes` of `10` is a good starting point for a 14B model on CPU; on a modern GPU, `5` is usually enough.
> - If you'd rather not touch a terminal at all, **LLMRunner** does all of this — download, chat, and serve on `localhost:8080` — in a single window. See [Meet LLMRunner](#meet-llmrunner).

### AI Connection Variants

The same `aiConfiguration` block works for every endpoint — only the values change.

**Required fields**

| Field         | Type     | Description                                      |
|---------------|----------|--------------------------------------------------|
| `model`       | `string` | Model name, e.g. `gpt-4o`, `qwen2.5:14b`.        |
| `temperature` | `number` | Creativity (0.0 = deterministic, 2.0 = maximum). |

**Optional fields** — set to `null` or `0` if unused

| Field            | Type     | Default (when empty)     | Description                                                              |
|------------------|----------|--------------------------|--------------------------------------------------------------------------|
| `baseUrl`        | `string` | `http://localhost:8080/` | Endpoint URL — **must end with `/`**.                                    |
| `timeoutMinutes` | `integer`| `5`                      | HTTP timeout in minutes.                                                 |
| `systemPrompt`   | `string` | *(none)*                 | System role and rules for the model.                                     |
| `apiKey`         | `string` | *(none)*                 | API key directly in the file — **mutually exclusive with `apiKeyEnvVar`**. |
| `apiKeyEnvVar`   | `string` | *(none)*                 | Name of the env var holding the key — **mutually exclusive with `apiKey`**. |

#### Variant 1 — LLMRunner, no configuration required

If you use the companion **LLMRunner** app, it serves your chosen local model on `http://localhost:8080/` — the default. The minimal `aisettings.json` above works out of the box.

#### Variant 2 — Ollama (Qwen 2.5 14B)

See [Getting Started with Ollama + Qwen 2.5 14B](#getting-started-with-ollama--qwen-25-14b).

#### Variant 3 — Ollama with a different model

```json
"aiConfiguration": {
  "baseUrl": "http://localhost:11434/",
  "model": "llama3.1:8b",
  "temperature": 0.2,
  "timeoutMinutes": 10,
  "systemPrompt": "",
  "apiKey": null,
  "apiKeyEnvVar": null
}
```

#### Variant 4 — LM Studio

```json
"aiConfiguration": {
  "baseUrl": "http://localhost:1234/",
  "model": "qwen2.5-coder-14b-instruct",
  "temperature": 0.2,
  "timeoutMinutes": 10,
  "systemPrompt": "",
  "apiKey": null,
  "apiKeyEnvVar": null
}
```

#### Variant 5 — OpenAI *(hosted, via environment variable — recommended)*

```json
"aiConfiguration": {
  "baseUrl": "https://api.openai.com/",
  "model": "gpt-4o",
  "temperature": 0.2,
  "timeoutMinutes": 5,
  "systemPrompt": "You are an expert .NET developer. Write clean, maintainable code following Clean Code principles.",
  "apiKey": null,
  "apiKeyEnvVar": "OPENAI_API_KEY"
}
```

Set the variable:

**Windows (PowerShell, persistent):**
```powershell
setx OPENAI_API_KEY "sk-…"
```

**Linux / macOS:**
```bash
export OPENAI_API_KEY="sk-…"
```

**GitHub Actions:**
```yaml
env:
  OPENAI_API_KEY: ${{ secrets.OPENAI_API_KEY }}
```

#### Variant 6 — Azure OpenAI

```json
"aiConfiguration": {
  "baseUrl": "https://<your-resource>.openai.azure.com/",
  "model": "gpt-4o",
  "temperature": 0.2,
  "timeoutMinutes": 5,
  "systemPrompt": "",
  "apiKey": null,
  "apiKeyEnvVar": "AZURE_OPENAI_KEY"
}
```

#### Variant 7 — Custom proxy / corporate endpoint

```json
"aiConfiguration": {
  "baseUrl": "https://llm.company.local/",
  "model": "internal-model",
  "temperature": 0.2,
  "timeoutMinutes": 15,
  "systemPrompt": "",
  "apiKey": null,
  "apiKeyEnvVar": "INTERNAL_LLM_TOKEN"
}
```

### Rules & Pitfalls

- **`apiKey` and `apiKeyEnvVar` are mutually exclusive.** Setting both to non-empty values produces a validation error. Both empty means *no authentication*.
- **`baseUrl`, if given, must end with `/`.** `http://localhost:11434` is invalid; `http://localhost:11434/` is correct.
- **Env-var names are case-sensitive** on Linux and macOS. `OPENAI_API_KEY` ≠ `openai_api_key`.
- **Never commit secrets.** Prefer the `apiKeyEnvVar` variants for cloud endpoints.
- **`buildConfigurationFilter` is ignored by the CLI** — a CLI invocation is always explicit. Filtering only applies to the MSBuild task.

---

## Supported Frameworks

| Area | Support |
|---|---|
| Test frameworks | xUnit, NUnit, MSTest |
| Mocking | Moq, NSubstitute, FakeItEasy |
| Assertions | FluentAssertions or framework-provided asserts |
| Test data | AutoFixture *(optional)* |
| UI / STA | WPF detection, `Xunit.StaFact`, NUnit `[Apartment(ApartmentState.STA)]`, MSTest `[STATestMethod]` |
| Project formats | SDK-style, Central Package Management (`Directory.Packages.props`) |
| Target frameworks | `netstandard2.0` (task assembly), `net10.0` (Core) |

---

## Repository Layout

```
NetAI.TestGenerator.Core/
  Analysis/                       RoslynDllTestabilityAnalyzer, UnitTestSkeletonGenerator,
                                  TestFrameworkProfile, TestProjectInfo, TrivialTestGenerator
  Config/                         AiTestingConfig, LlmConnectionSettings, AiSettingsLoader
  Models/                         MethodFact, TypeFact, TestabilityReport, ParameterFact, …
  Services/                       TestProjectManager, TestCodeProcessor, TestCodeBeautifier,
                                  TestGeneratorService, AiPromptBuilder(Simple), BuildLogger
  LocalLlmClient.cs               OpenAI-compatible HTTP client
  TetsGeneratorOrchestrator.cs    End-to-end pipeline (historical name)
  PromptTemplates.cs              Scriban-powered prompt text

NetAI.TestGenerator.Tasks/
  TestGeneratorTasks.cs           MSBuild task entry point
  build/NetAI.TestGenerator.Tasks.targets
  aisettings.json                 Default config shipped with the package
  aisettings-schema.json          JSON Schema shipped with the package

NetAI.TestGenerator.Cli/
  Program.cs                      Command registration
  Commands/                       GenerateCommand, AnalyseCommand, InitCommand
  Configuration/                  SettingsLoader (discovery + defaults)
  Infrastructure/                 CompilationLoader, SourceCollector, ProjectLocator, FrameworkParser
  Logging/                        SpectreLog
  ExitCodes.cs                    Well-known exit codes
```

---

## Roadmap

- ✅ Roslyn semantic analysis + binding skeleton
- ✅ MSBuild task and NuGet package
- ✅ Command-line tool (`netai`)
- ✅ xUnit / NUnit / MSTest + Moq / NSubstitute / FakeItEasy
- ✅ WPF/STA support
- ✅ Companion app **LLMRunner** for local-model setup
- **Fix the historical `TetsGeneratorOrchestrator` typo** in a future major version
- **Test-quality scoring** — flag generated tests that compile but don't assert
- **Coverage-aware prioritization** — focus on uncovered public methods first
- **Playwright / bUnit adapters** for UI-layer test generation

---

## License

This project is licensed under the **MIT License**.

See the [LICENSE](LICENSE) file for details.

You are free to use, modify, and distribute it — including in commercial and closed-source products — as long as the license and copyright notice are preserved.

---

## Contributing

Contributions are welcome! For larger changes, please open an issue first so we can align on the direction. For smaller fixes or improvements, feel free to open a pull request directly.

1. Fork the repository
2. Create a feature branch
3. Commit your changes
4. Push to the branch
5. Open a pull request

Please ensure your code follows the existing style and includes appropriate tests where applicable. For bug reports, please include a minimal reproduction — a tiny host project plus the relevant `aisettings.json`.

---

## Acknowledgements

- Built on [Roslyn](https://github.com/dotnet/roslyn) for semantic analysis and code generation.
- Uses [Scriban](https://github.com/scriban/scriban) for prompt templating.
- Uses [Newtonsoft.Json](https://www.newtonsoft.com/json) for configuration parsing and LLM payloads.
- Uses [Spectre.Console](https://spectreconsole.net/) for the CLI experience.
- Works with any OpenAI-compatible LLM endpoint — local or hosted.
- Inspired by the daily reality that nobody has time to write the tests they know they should.