# NetAI.TestGenerator.Cli

CLI wrapper around `NetAI.TestGenerator.Core` for AI-assisted unit-test generation.

## Build & install as a local dotnet tool

```bash
dotnet pack src/Libraries/NetAI.TestGenerator.Cli/NetAI.TestGenerator.Cli.csproj -c Release -o ./nupkg
dotnet tool install --global --add-source ./nupkg NetAI.TestGenerator.Cli
# or update
dotnet tool update  --global --add-source ./nupkg NetAI.TestGenerator.Cli
```

After installing, the command is `netai`.

## Commands

### `netai generate <sourceFile>`

Generates unit tests for the first class in the given source file and writes them into the resolved test project.

| Option                  | Purpose |
|-------------------------|---------|
| `--test-project <DIR>`  | Directory of the test project. Default: sibling `<Project>.Tests` or `tests/<Project>.Tests`. |
| `--config <FILE>`       | Path to `aisettings.json`. Default: nearest file found upward from the source file. |
| `--solution <FILE>`     | `.sln` for MSBuild-based semantic analysis. |
| `--project <FILE>`      | `.csproj` for MSBuild-based semantic analysis. |
| `--prompt-only`         | Save prompts under `obj/netai/prompts` and skip the LLM. |
| `--framework <NAME>`    | Override `xunit` / `nunit` / `mstest`. |
| `--mocking <NAME>`      | Override `moq` / `nsubstitute` / `fakeiteasy` / `none`. |
| `-v`, `--verbose`       | Show all orchestrator logs. |
| `--no-color`            | Disable ANSI colors. |
| `--json`                | Machine-readable JSON on stdout, logs on stderr. |

#### Examples

**1. Happy path – tests land in the sibling test project**

The CLI walks up from the source file, finds the nearest `aisettings.json` (or falls back to built-in defaults), locates the sibling `MyApp.Tests` folder, and generates tests there.

```bash
netai generate ./src/MyApp/Services/OrderService.cs
```

Output:

```
✓ Done. Test project: C:\repos\MyApp\tests\MyApp.Tests
```

Result: `tests/MyApp.Tests/OrderServiceTests.cs` containing one test method per uncovered public method.

**2. Explicit test project + framework override**

Use this when your test project doesn't follow the `<Project>.Tests` convention, or when you want a different test framework just for this run (the settings file is not modified).

```bash
netai generate ./src/MyApp/Services/OrderService.cs \
  --test-project ./tests/IntegrationTests \
  --framework nunit \
  --mocking nsubstitute \
  --verbose
```

`--verbose` streams every orchestrator log line (semantic analysis verdict, skeleton mode, compile errors, AI repair attempts) to the console. Without it, only `[NetAI]`-prefixed messages are shown.

**3. CI / prompt-only dry run**

Two common variations for pipelines and prompt debugging:

```bash
# Only write the AI prompts to disk – do not call the LLM, do not compile.
# Useful for reviewing what would be sent, or for caching prompts in CI.
netai generate ./src/MyApp/Services/OrderService.cs --prompt-only

# Full generation but machine-readable output. Stdout is exactly one JSON
# document; all log chatter goes to stderr.
netai generate ./src/MyApp/Services/OrderService.cs --json > result.json
```

Example `result.json`:

```json
{
  "success": true,
  "sourceFile": "OrderService.cs",
  "testProjectDirectory": "C:\\repos\\MyApp\\tests\\MyApp.Tests",
  "result": "ok"
}
```

### `netai analyse <sourceFile>`

Prints strategy / verdict / blockers per method without calling the LLM.

| Option             | Purpose |
|--------------------|---------|
| `--config <FILE>`  | Path to `aisettings.json`. |
| `--json`           | Emit a JSON document instead of a table. |

### `netai init [directory]`

Writes a default `aisettings.json` into the given directory (defaults to cwd).

## Configuration

`aisettings.json` mirrors `AiTestingConfig` in `NetAI.TestGenerator.Core`. Minimal example:

```json
{
  "version": "1.1",
  "environment": { "targetDotNetVersion": "net9.0", "testProjectName": "{ProjectName}.Tests" },
  "frameworks": {
    "testFramework": "xunit",
    "mockingFramework": "moq",
    "useFluentAssertions": true,
    "useAutoFixture": false,
    "verbosePrompt": true
  },
  "codeStyle": { "useFileScopedNamespace": true, "useAsyncSuffix": true, "maxLineLength": 120 },
  "generationBehavior": { "testStrategy": "Both", "splitTestsByMethod": false, "maxTestsPerClass": 15 }
}
```

The `aiConfiguration` section is not read by the CLI – it belongs to the NuGet/build-task flow.

## Exit codes

| Code | Meaning |
|------|---------|
| 0    | Success |
| 1    | Configuration error (missing file, bad settings, no test project found) |
| 2    | Test generation failed |
| 3    | Analysis failed |
| 99   | Unhandled error |
| 130  | Cancelled by Ctrl+C |

## CI example

```bash
netai generate ./src/OrderService.cs --json > result.json
```

In JSON mode all chatter goes to stderr; stdout is exactly one JSON document.

## Ctrl+C

Spectre 0.49.1 exposes no cancellation hook on `AsyncCommand`, and `ProcessProjectAsync` does not accept a `CancellationToken`. The CLI installs a `Console.CancelKeyPress` handler that logs cancellation and returns exit code 130, but the underlying LLM/compile loop runs to completion before the process exits.