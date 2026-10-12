# NetAI.TestGenerator.Tasks

[![NuGet](https://img.shields.io/nuget/v/NetAI.TestGenerator.Tasks.svg?label=NuGet)](https://www.nuget.org/packages/NetAI.TestGenerator.Tasks)
[![NuGet Downloads](https://img.shields.io/nuget/dt/NetAI.TestGenerator.Tasks.svg)](https://www.nuget.org/packages/NetAI.TestGenerator.Tasks)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

> **The MSBuild task.** Add one NuGet package, and every missing unit test in your project asks Roslyn for a skeleton — and the LLM for a body — before your code finishes compiling.

This README covers the **MSBuild task / NuGet package**. For the full project overview, see the [solution README](../../../README.md). If you'd rather generate tests on the command line, use the sibling [`NetAI.TestGenerator.Cli`](../NetAI.TestGenerator.Cli/README.md) package.

---

## Table of Contents

- [Why a Build Task?](#why-a-build-task)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [What the Task Does on Each Build](#what-the-task-does-on-each-build)
- [Build-Time Switches](#build-time-switches)
- [Prompt-Only Mode](#prompt-only-mode)
- [Build Integration](#build-integration)
- [Troubleshooting](#troubleshooting)
- [Configuration](#configuration)
- [License](#license)

---

## Why a Build Task?

Because test coverage shouldn't depend on anyone remembering to write tests.

A build task means:

- **No separate step.** `dotnet build` is the whole workflow.
- **No drift.** Tests are generated next to the code that needs them, on every build where the generator is active.
- **No CI glue.** Whatever runs your build runs the generator.
- **Nothing to forget.** A developer adds a public method, builds, and the test project already contains a candidate test.

You stay in control: the task is disabled for design-time builds, skips multi-targeting beyond the first TFM, and can be turned off entirely with a single property.

---

## Installation

```bash
dotnet add package NetAI.TestGenerator.Tasks
```

Or add the reference directly to your `.csproj`:

```xml
<ItemGroup>
  <PackageReference Include="NetAI.TestGenerator.Tasks" Version="1.0.*" PrivateAssets="all" />
</ItemGroup>
```

The package injects its `.targets` file automatically. On the **first build**, two files are copied into your project root if they don't already exist:

- **`aisettings.json`** — the configuration file *(commit this)*.
- **`aisettings-schema.json`** — a JSON Schema for editor IntelliSense *(commit this)*.

Both are added to the project as `None` items with `CopyToOutputDirectory=PreserveNewest`.

---

## Quick Start

1. **Add the package** to the project whose tests you want generated.
2. **Build once** — `aisettings.json` is scaffolded into your project root.
3. **Edit `aisettings.json`** to point at your LLM and pick your test / mocking framework.
4. **Build again.** Missing tests appear in the resolved test project.

Minimal `aisettings.json`:

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
    "useAutoFixture": false,
    "verbosePrompt": true
  },
  "aiConfiguration": {
    "baseUrl": "http://localhost:11434/",
    "model": "llama3.1",
    "temperature": 0.2,
    "timeoutMinutes": 10
  }
}
```

The task searches upward from `ProjectDir` for a `.sln` / `.slnx`, then places the test project under `tests/UnitTests/<ProjectName>.Tests/` by default.

---

## What the Task Does on Each Build

For every `.cs` file in the project (with `obj/`, `bin/`, `*.g.cs`, `*.Designer.cs`, `AssemblyInfo.cs`, and `_wpftmp` artifacts excluded):

1. **Analyze** the first class found with Roslyn.
2. **Skip already-covered methods** by checking the resolved test file.
3. **Build a binding skeleton** with `UnitTestSkeletonGenerator` (usings, namespace, class name, fields, mocks, test attribute, `AI AREA`).
4. **Ask the LLM** to fill only the `AI AREA` section.
5. **Compile-validate** the resulting test class against the real test project.
6. **Auto-repair** missing usings and missing NuGet packages, then optionally hand the model one focused repair prompt.
7. **Write** the merged test class to `tests/UnitTests/<ProjectName>.Tests/<ClassName>Tests.cs`.

Errors and warnings from the orchestrator surface as native MSBuild messages with the `[NetAI]` prefix — visible in your build log and the Visual Studio Error List.

---

## Build-Time Switches

The task is controlled entirely by MSBuild properties. All four are optional.

| Property | Default | Effect |
|---|---|---|
| `TestGeneratorEnabled` | `true` | Set to `false` to disable the generator for this build. |
| `TestGeneratorSemanticAnalysisEnabled` | `true` | Set to `false` to run only a syntax-level analysis report — no test generation. |
| `TestGeneratorPromptOnly` | `false` | Set to `true` to write prompts and drafts without compiling the test project. |
| `TestGeneratorFailOnError` | `false` | Set to `true` to fail the build when a source file fails to generate a test. |

### Per project

```xml
<PropertyGroup>
  <TestGeneratorEnabled>true</TestGeneratorEnabled>
  <TestGeneratorSemanticAnalysisEnabled>true</TestGeneratorSemanticAnalysisEnabled>
  <TestGeneratorPromptOnly>false</TestGeneratorPromptOnly>
  <TestGeneratorFailOnError>false</TestGeneratorFailOnError>
</PropertyGroup>
```

### Per invocation

```bash
dotnet build -p:TestGeneratorEnabled=false
dotnet build -p:TestGeneratorPromptOnly=true
dotnet build -p:TestGeneratorFailOnError=true
```

### `buildConfigurationFilter`

The `buildConfigurationFilter` setting in `aisettings.json` decides *when* the task is active:

| Value | Behavior |
|---|---|
| `all` | Runs on every build (default). |
| `Debug` | Runs only on Debug builds. |
| `Release` | Runs only on Release builds. |

When the filter doesn't match the current configuration, the task logs a single `Skipped.` line and exits cleanly.

---

## Prompt-Only Mode

Set `TestGeneratorPromptOnly=true` to run the generator in "draft" mode:

- The prompt is written to `obj/netai/prompts/<ClassName>.<MethodName>.prompt.md`.
- The LLM is still called.
- The generated draft is saved to the test project.
- **The compile-validation step is skipped.**

This is the ideal mode for:

- Debugging a prompt — see exactly what the model is being asked to do.
- Comparing model outputs across runs without failing on compile errors.
- Migrating a project: capture the drafts first, clean them up manually, then re-enable validation.

```bash
dotnet build -p:TestGeneratorPromptOnly=true
```

> **Note:** `PromptOnly` does **not** disable the LLM call. For a true "what would happen?" dry run with no LLM and no writes, use the CLI's `--dry-run` mode — see the [CLI README](../NetAI.TestGenerator.Cli/README.md#3-commands).

---

## Build Integration

The `.targets` file that ships with the package is designed to be a **good citizen** in any project:

- **Design-time builds are skipped.** No interference with IntelliSense or the VS designer.
- **WPF-safe.** Projects whose name ends in `_wpftmp` abort silently.
- **Multi-targeting aware.** Runs only for the first TFM in `TargetFrameworks`.
- **Task host isolation.** `TaskFactory="TaskHostFactory"` runs the task in a separate MSBuild process, so the task assembly is **never locked** by Visual Studio.
- **Explicit warning if missing.** If the task assembly can't be located, a build warning is emitted instead of silently doing nothing.

The task participates in the following build phases:

| Phase | When | What it does |
|---|---|---|
| Settings bootstrap | Before compile | Scaffolds `aisettings.json` / `aisettings-schema.json` if missing. |
| Semantic analysis | After `ResolveReferences`, before `CoreCompile` | Produces `obj/netai/testability-report.txt` when `TestGeneratorSemanticAnalysisEnabled=true`. |
| Test generation | Before compile | Runs the orchestrator over `@(Compile)` sources. |
| Publish fallback | Before `PrepareForPublish` | Runs test generation on publish when `_IsPublishing` isn't set by the build host. |

---

## Troubleshooting

### "Task assembly not found" warning

The `.targets` file couldn't locate the task DLL. Try:

```bash
dotnet restore --force
dotnet build
```

If you're using a local feed, ensure the `.nupkg` is fully installed and the timestamped debug version isn't shadowing the release version.

### "No tests were generated."

Check the following in order:

1. **Is the task active?** Look for `[NetAI] Mode condition met` in the build log. If it says `Skipped`, check `buildConfigurationFilter`.
2. **Is `aisettings.json` at the project root?** The task searches upward and stops at the first match.
3. **Is there a `.sln` / `.slnx` above the project directory?** The task needs it to place the test project.
4. **Is the LLM reachable?** A wrong `baseUrl` (or one without the trailing `/`) will produce an error in the build log.
5. **Are there already tests for those methods?** Only methods *without* an existing test are processed.

### "The build is slow."

That's the LLM, not the task. Options:

- Use a local model on a fast machine (Ollama with a 7B–14B model is plenty for a single test method).
- Set `TestGeneratorPromptOnly=true` while iterating on prompt templates.
- Set `TestGeneratorFailOnError=false` (default) so a single failure doesn't abort the build.
- Temporarily disable the task: `dotnet build -p:TestGeneratorEnabled=false`.

### "Tests are generated but don't compile."

Every generated test is compiled before it's written. If it doesn't compile after the automatic repair attempts, the failure is logged as a warning (`TestGeneratorFailOnError=false`) or an error (`TestGeneratorFailOnError=true`), and the generated draft is kept at its path in the test project for manual inspection.

Common causes:

- The method genuinely cannot be tested without refactoring (see the `[NetAI] Blockers for '<method>'` log line).
- The LLM invented a type or member name — the task will try one AI repair, but a bad model or a bad prompt can defeat it.
- A file lock in the test project — check for `MSB3027` in the build log; close the running app / VS.

---

## Configuration

All behavior is driven by `aisettings.json` at the project root. The **full reference** — including all `aiConfiguration` variants (local, OpenAI, Azure, custom proxy) and rules — lives in the [solution README](../../../README.md#configuration--aisettingsjson).

Task-specific settings:

| Option | Type | Default | Description |
|---|---|---|---|
| `buildConfigurationFilter` | string | `all` | `all`, `Debug`, or `Release` — controls when the task runs. |
| `maxMethodChars` | int | `25000` | Methods longer than this get a skip stub instead of a real test. |

---

## License

MIT — see the [LICENSE](../../../LICENSE) file.