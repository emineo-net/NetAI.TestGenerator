# NetAI.TestGenerator.Cli

> **`netai`** — generate and analyze .NET unit tests from the command line.
> Same engine as the MSBuild task. Same `aisettings.json`. No IDE required.

This README covers the **CLI tool**. For the full project overview, see the [solution README](../../../README.md). If you'd rather have test generation happen automatically during `dotnet build`, use the sibling [`NetAI.TestGenerator.Tasks`](../NetAI.TestGenerator.Tasks/README.md) package.

---

## Table of Contents

- [Why a CLI?](#why-a-cli)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Commands](#commands)
  - [`generate`](#generate)
  - [`analyse`](#analyse)
  - [`init`](#init)
- [Options](#options)
- [Exit Codes](#exit-codes)
- [JSON Output (for CI)](#json-output-for-ci)
- [Examples](#examples)
- [Configuration](#configuration)
- [License](#license)

---

## Why a CLI?

The build task is great when you want coverage on autopilot. Sometimes, though, you want it *on demand*:

- **You're in CI** and want a dedicated test-generation stage that fails the pipeline on missing tests.
- **You're debugging a prompt** and want to see what the LLM actually receives — without rebuilding the whole solution.
- **You're migrating** an existing project and want to generate tests for one file at a time, carefully.
- **You're in a repo** where you don't want a build task poking at test files on every compile.

The CLI is exactly that: the same engine, exposed as a `dotnet tool` named `netai`.

---

## Installation

```bash
dotnet tool install --global NetAI.TestGenerator.Cli
```

Once installed, the tool is available as `netai`:

```bash
netai --help
```

**Local install (recommended for CI):**

```bash
dotnet tool install --local NetAI.TestGenerator.Cli
dotnet tool restore          # in CI, before running the tool
dotnet netai generate ./src/MyApp/Services/OrderService.cs
```

Or add a manifest:

```bash
dotnet new tool-manifest
dotnet tool install NetAI.TestGenerator.Cli
```

### Running from source (for development)

You do **not** need to pack or install anything to run the CLI from this repository. From the **repository root**:

```bash
dotnet run --project src/Libraries/NetAI.TestGenerator.Cli -- \
  generate ./src/UI/WpftranlationTestApp/Calculator.cs \
  --test-project ./tests/WpftranlationTestApp.Tests \
  --verbose --no-color
```

Everything after `--` is passed to `netai` verbatim.

---

## Quick Start

```bash
# 1. Scaffold an aisettings.json next to your .csproj / .sln
netai init ./src/MyApp

# 2. Point it at a local model. The easiest way:
#    - open LLMRunner (companion app, same authors)
#    - pick a model, click "Serve on localhost:8081"
#    - the default aisettings.json already expects exactly that address.
#
#    Prefer a terminal? Use Ollama with Qwen 2.5 14B:
#      ollama pull qwen2.5:14b
#      ollama serve
#    ... then set baseUrl to http://localhost:11434/ and model to qwen2.5:14b.

# 3. Find out what the analyzer sees for a file
netai analyse ./src/MyApp/Services/OrderService.cs

# 4. Generate tests
netai generate ./src/MyApp/Services/OrderService.cs
```

That's it. Tests will be generated in the resolved test project — the same way the build task would do it.

> **Just want to try it with a local model?** Grab **LLMRunner** from the same repository profile or the releases page. It downloads, chats, and serves a model on `http://localhost:8081/` — one window, no terminal.

---

## Commands

### `generate`

Generate unit tests for a source file, a directory, or a whole `.csproj`.

```
netai generate [source] [options]
```

| Argument       | Required | Description                                                                    |
|----------------|----------|--------------------------------------------------------------------------------|
| `[source]`     | no*      | Source file or directory. Optional if `--project` is given.                    |

*At least one of `[source]` or `--project` must be provided.

The command:

1. Resolves source files — from `--project` (via MSBuild compilation), from the given directory, or a single file.
2. Loads `aisettings.json` from the first source file's directory, or upward.
3. For each source file:
   - Analyzes the first class with Roslyn.
   - Builds the binding skeleton and calls the LLM.
   - Validates by compiling the resulting test project.
   - Repairs and retries.
4. Writes merged test classes to the resolved test-project directory.

**Resolution order** for the test project directory:

1. `--test-project <DIR>` if given.
2. Sibling `<Project>.Tests/` searched upward from each source file.
3. `tests/<Project>.Tests/` (or `test/`, `Tests/`, `Test/`) searched upward.

**Examples:**

```bash
# Single file — the sibling test project is discovered automatically
netai generate ./src/MyApp/Services/OrderService.cs

# A whole project via its .csproj (loads the Roslyn compilation once, then reuses it)
netai generate --project ./src/MyApp/MyApp.csproj

# A directory, recursive, filtered
netai generate ./src/MyApp/ --recursive --filter "*.Services.cs" --exclude "*Tests.cs"

# Explicit test project + framework overrides for this run only
netai generate ./src/MyApp/Services/OrderService.cs \
  --test-project ./tests/IntegrationTests \
  --framework nunit \
  --mocking nsubstitute \
  --verbose

# Machine-readable result on stdout, logs on stderr
netai generate --project ./src/MyApp/MyApp.csproj --json > result.json
```

---

### `analyse`

Print what the analyzer sees for a C# file — no LLM call, no file writes.

```
netai analyse <sourceFile> [options]
```

| Argument       | Required | Description                                |
|----------------|----------|--------------------------------------------|
| `<sourceFile>` | yes      | Path to the C# source file to analyse.     |

The output is a table with one row per method:

```
┌──────────────────────────────┬──────────────┬──────────┬─────┬───────────────────────────────┐
│ Method                       │ Strategy     │ Testable │ STA │ Verdict                       │
├──────────────────────────────┼──────────────┼──────────┼─────┼───────────────────────────────┤
│ ProcessOrderAsync            │ Direct       │ yes      │ -   │ Directly testable.            │
│ OnLoaded                     │ RefactorFirst│ no       │ yes │ NOT directly testable: …      │
│ InternalHelper               │ Reflection   │ no       │ -   │ NOT directly testable: …      │
└──────────────────────────────┴──────────────┴──────────┴─────┴───────────────────────────────┘
```

**Examples:**

```bash
netai analyse ./src/MyApp/Services/OrderService.cs
netai analyse ./src/MyApp/Services/OrderService.cs --json
```

---

### `init`

Write a default `aisettings.json` into the given directory.

```
netai init [directory]
```

| Argument      | Required | Default                    | Description                                |
|---------------|----------|----------------------------|--------------------------------------------|
| `[directory]` | no       | current working directory  | Where to create `aisettings.json`.         |

Behavior:

- If `aisettings.json` already exists, it prints a yellow notice and exits `0` — **it never overwrites an existing config**.
- Otherwise it writes a fully-populated default file and prints the path.

**Example:**

```bash
netai init
netai init ./src/MyApp
```

---

## Options

### `generate` options

| Option | Description |
|---|---|
| `[source]` | Source file or directory. Optional if `--project` is given. |
| `--project <FILE>` | Path to a `.csproj`. Loads the Roslyn compilation via MSBuild **once per run**, uses its syntax trees as the source list, and enables semantic analysis. |
| `--test-project <DIR>` | Directory of the test project. Default: sibling `<Project>.Tests` or `tests/<Project>.Tests`, searched upward from each source file. |
| `--config <FILE>` | Path to `aisettings.json`. Default: nearest file found **upward** from the first source file. |
| `--recursive` | If `[source]` is a directory, walk subdirectories. `obj/` and `bin/` are always excluded. |
| `--filter <GLOB>` | Only include file names matching this glob (e.g. `*.Services.cs`). Repeatable. |
| `--exclude <GLOB>` | Exclude file names matching this glob. Repeatable. |
| `--max-files <N>` | Abort if more than `N` source files would be processed. |
| `--dry-run` | List what would be processed — **no LLM call, no file writes**. |
| `--prompt-only` | Write prompts to `obj/netai/prompts/` and **skip the compile-validation step**. The LLM is still called. |
| `--framework <NAME>` | Override `xunit` / `nunit` / `mstest` for this run only. |
| `--mocking <NAME>` | Override `moq` / `nsubstitute` / `fakeiteasy` / `none` for this run only. |
| `-v`, `--verbose` | Show all orchestrator log lines. |
| `--no-color` | Disable ANSI colors (useful when piping to a file). |
| `--json` | Machine-readable JSON on **stdout**, logs on **stderr**. Ideal for CI. |

### `analyse` options

| Option | Description |
|---|---|
| `--config <FILE>` | Path to `aisettings.json`. |
| `--json` | Emit a JSON document instead of a table. |

### Global

| Option | Description |
|---|---|
| `-h`, `--help` | Show help. |
| `--version` | Show the tool version. |

> **`--dry-run` vs `--prompt-only`.** `--dry-run` does **nothing** — no LLM, no writes, just a table of what *would* be processed. `--prompt-only` writes the fully-built prompt to `obj/netai/prompts/<Class>.<Method>.prompt.md`, calls the LLM, saves the draft, and skips the compile-validation step. Use `--dry-run` for a safe reconnaissance pass; use `--prompt-only` when you want to inspect or iterate on prompts.

---

## Exit Codes

The CLI uses well-defined exit codes so your pipeline can react intelligently:

| Code | Name                | Meaning                                                            |
|-----:|---------------------|--------------------------------------------------------------------|
| `0`  | `Success`           | All requested work completed successfully.                         |
| `1`  | `ConfigurationError`| Missing file, bad settings, no test project could be resolved.     |
| `2`  | `GenerationFailed`  | At least one file failed to generate a compilable test.            |
| `3`  | `AnalysisFailed`    | The `analyse` command could not build a report.                    |
| `99` | `UnhandledError`    | Unexpected exception. Check stderr for details.                    |
| `130`| `Cancelled`         | Terminated by Ctrl+C.                                              |

**Example CI snippet:**

```bash
netai generate --project ./src/MyApp/MyApp.csproj --json
case $? in
  0)   echo "✅ All tests generated successfully." ;;
  1)   echo "❌ Configuration problem." ; exit 1 ;;
  2)   echo "❌ At least one file failed." ; exit 1 ;;
  *)   echo "💥 Unexpected error." ; exit 1 ;;
esac
```

---

## JSON Output (for CI)

With `--json`, the tool writes a single JSON object to **stdout** and sends every diagnostic line to **stderr**. This means you can pipe stdout straight into `jq`, your build system, or a dashboard without parsing human-readable text.

**On success:**

```json
{
  "success": true,
  "total": 1,
  "ok": 1,
  "failed": 0,
  "skipped": 0,
  "cancelled": 0,
  "files": [
    {
      "file": "OrderService.cs",
      "status": "ok",
      "testProjectDirectory": "<repo>/tests/MyApp.Tests",
      "message": null
    }
  ]
}
```

**On partial failure:**

```json
{
  "success": false,
  "total": 3,
  "ok": 2,
  "failed": 1,
  "skipped": 0,
  "cancelled": 0,
  "files": [
    { "file": "OrderService.cs",    "status": "ok",     "testProjectDirectory": "<repo>/tests/MyApp.Tests", "message": null },
    { "file": "InvoiceService.cs",  "status": "ok",     "testProjectDirectory": "<repo>/tests/MyApp.Tests", "message": null },
    { "file": "Program.cs",         "status": "failed", "testProjectDirectory": "<repo>/tests/MyApp.Tests", "message": "No class found in source file." }
  ]
}
```

Verbose logs (`-v`) go to stderr, so they never pollute the JSON.

**Example — fail a GitHub Actions step on any generation error:**

```yaml
- name: Generate tests
  run: |
    dotnet tool restore
    dotnet netai generate --project ./src/MyApp/MyApp.csproj --json > result.json
    jq -e '.success == true' result.json
```

---

## Examples

### Analyse first — see what the analyzer sees

```bash
netai analyse ./src/MyApp/Services/OrderService.cs --json > analysis.json
```

### Dry-run a project — no LLM, no writes

```bash
netai generate --project ./src/MyApp/MyApp.csproj --dry-run
```

The output is a table of what *would* be processed:

```
┌────────────────────┬──────────────────┬─────────┬──────────────────────┐
│ File               │ Class            │ Methods │ Test project         │
├────────────────────┼──────────────────┼─────────┼──────────────────────┤
│ OrderService.cs    │ OrderService     │       7 │ tests/MyApp.Tests    │
│ InvoiceService.cs  │ InvoiceService   │       4 │ tests/MyApp.Tests    │
│ Program.cs         │ <none>           │       2 │ tests/MyApp.Tests    │
└────────────────────┴──────────────────┴─────────┴──────────────────────┘
```

### Prompt-only — save prompts and drafts, skip compile validation

```bash
netai generate --project ./src/MyApp/MyApp.csproj --prompt-only --verbose
```

Prompts land in `obj/netai/prompts/<Class>.<Method>.prompt.md`. Drafts land in the test project.

### Bulk — a whole directory, recursive, filtered

```bash
netai generate ./src/MyApp/ \
  --recursive \
  --filter "*.Services.cs" \
  --exclude "*Tests.cs" \
  --max-files 25
```

### Iterate on a single file with verbose logs

```bash
netai generate ./src/MyApp/Services/OrderService.cs -v --no-color
```

### GitHub Actions — generate tests and fail the build on errors

```yaml
- name: Setup .NET
  uses: actions/setup-dotnet@v4
  with:
    dotnet-version: '10.0.x'

- name: Install netai
  run: dotnet tool install --global NetAI.TestGenerator.Cli

- name: Generate tests
  env:
    OPENAI_API_KEY: ${{ secrets.OPENAI_API_KEY }}
  run: |
    netai generate --project ./src/MyApp/MyApp.csproj --json > result.json
    jq -e '.success == true' result.json
```

---

## Configuration

The CLI reads the same `aisettings.json` as the build task — there is only one config format. See the [solution README](../../../README.md#configuration--aisettingsjson) for:

- The full configuration reference.
- All AI connection variants (local, OpenAI, Azure, custom proxy).
- A ready-to-use **Ollama + Qwen 2.5 14B** example.
- Rules & pitfalls (trailing slash on `baseUrl`, mutually-exclusive key fields, …).

**Note:** `buildConfigurationFilter` is **ignored by the CLI** — a CLI invocation is always explicit. Mode-based gating only applies to the MSBuild task.

---

## Ctrl+C

Spectre.Console.Cli 0.49.1 exposes no cancellation hook on `AsyncCommand`, and `ProcessProjectAsync` does not accept a `CancellationToken`. The CLI installs a `Console.CancelKeyPress` handler that stops the batch loop after the current file and returns exit code `130`. The currently running LLM / compile step still completes before the process exits.

---

## License

MIT — see the [LICENSE](../../../LICENSE) file.