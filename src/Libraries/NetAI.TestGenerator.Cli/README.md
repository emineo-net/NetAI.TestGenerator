# NetAI.TestGenerator.Cli

CLI wrapper around `NetAI.TestGenerator.Core` for AI-assisted unit-test generation.

The tool is called `netai` and exposes three commands:

- `netai generate <source>` — generate unit tests for a source file, a directory, or a whole `.csproj`
- `netai analyse <sourceFile>` — print a strategy/verdict report per method, no LLM call
- `netai init [directory]` — write a default `aisettings.json`

---

## 1. Try it without installing (recommended during development)

You do **not** need to pack or install anything to run the CLI from this repository.
Use `dotnet run --project …` from the **repository root**:

```powershell
# from the repository root
dotnet run --project src/Libraries/NetAI.TestGenerator.Cli -- `
  generate ./src/UI/WpftranlationTestApp/Calculator.cs `
  --test-project ./tests/WpftranlationTestApp.Tests `
  --verbose --no-color
```

Everything after `--` is passed to `netai` verbatim. The build is incremental,
so subsequent runs are fast.

You can also drop the `--project` flag if you `cd` into the CLI project directory,
but then all paths you pass must be relative to that directory — which is why the
repo-root form above is preferred.

---

## 2. Install as a global `dotnet tool`

Only needed if you want to call `netai` from **any** directory without the
`dotnet run --project …` prefix.

### 2a. Pack the CLI once

```powershell
# from the repository root
dotnet pack src/Libraries/NetAI.TestGenerator.Cli/NetAI.TestGenerator.Cli.csproj `
  -c Release `
  -o ./nupkg
```

This creates `./nupkg/NetAI.TestGenerator.Cli.<version>.nupkg`.

### 2b. Install it globally from that local source

```powershell
dotnet tool install --global --add-source ./nupkg NetAI.TestGenerator.Cli
```

Verify:

```powershell
netai --help
```

### 2c. Update after code changes

Bump `<Version>` in the `.csproj` (or pass `/p:Version=…` to `dotnet pack`),
then re-pack and run:

```powershell
dotnet tool update --global --add-source ./nupkg NetAI.TestGenerator.Cli
```

### 2d. Uninstall

```powershell
dotnet tool uninstall --global NetAI.TestGenerator.Cli
```

> Use `--local` instead of `--global` if you only want `netai` available inside
> this repository (requires a one-time `dotnet new tool-manifest` in the repo root,
> and you invoke it via `dotnet netai …`).

---

## 3. Commands

### `netai generate <source>`

Generates unit tests for the first class in each source file and writes them
into the resolved test project. Accepts a **single file**, a **directory**, or
— via `--project` — a whole **`.csproj`** as input.

| Option                  | Purpose |
|-------------------------|---------|
| `<source>`              | Source file or directory. Optional if `--project` is given. |
| `--project <FILE>`      | Path to a `.csproj`. Loads the Roslyn compilation via MSBuild **once per run**, uses its syntax trees as the source list, and enables semantic analysis. |
| `--test-project <DIR>`  | Directory of the test project. Default: sibling `<Project>.Tests` or `tests/<Project>.Tests`, searched upward from each source file. |
| `--config <FILE>`       | Path to `aisettings.json`. Default: nearest file found upward from the first source file. |
| `--recursive`           | If `<source>` is a directory, walk subdirectories. `obj/` and `bin/` are always excluded. |
| `--filter <GLOB>`       | Only include file names matching this glob (e.g. `*.Services.cs`). Repeatable. |
| `--exclude <GLOB>`      | Exclude file names matching this glob. Repeatable. |
| `--max-files <N>`       | Abort if more than `N` source files would be processed. |
| `--dry-run`             | List what would be processed — no LLM call, no file writes. |
| `--prompt-only`         | Write AI prompts to `obj/netai/prompts/` and **skip the compile-validation step**. The LLM is still called — see note below. |
| `--framework <NAME>`    | Override `xunit` / `nunit` / `mstest`. |
| `--mocking <NAME>`      | Override `moq` / `nsubstitute` / `fakeiteasy` / `none`. |
| `-v`, `--verbose`       | Show all orchestrator logs. |
| `--no-color`            | Disable ANSI colors. |
| `--json`                | Machine-readable JSON on stdout, logs on stderr. |

> **`--prompt-only` caveat.** This flag does **not** turn off the LLM. It saves
> the fully built prompt to `obj/netai/prompts/<Class>.<Method>.prompt.md` and
> then hands off to the LLM as usual. What it *does* skip is the compile /
> AI-repair loop afterwards. Use `--dry-run` if you want a run that neither
> calls the LLM nor writes anything.

#### Examples

**1. Happy path — tests land in the sibling test project**

The CLI walks up from the source file, finds the nearest `aisettings.json`
(or falls back to built-in defaults), locates the sibling `MyApp.Tests` folder,
and generates tests there.

```bash
netai generate ./src/MyApp/Services/OrderService.cs
```

Output:

```
✓ Done. Test project: <repo>/tests/MyApp.Tests
```

Result: `tests/MyApp.Tests/OrderServiceTests.cs` containing one test method per
uncovered public method.

**2. Explicit test project + framework override**

Use this when your test project does not follow the `<Project>.Tests`
convention, or when you want a different test framework just for this run
(the settings file is not modified).

```bash
netai generate ./src/MyApp/Services/OrderService.cs \
  --test-project ./tests/IntegrationTests \
  --framework nunit \
  --mocking nsubstitute \
  --verbose
```

`--verbose` streams every orchestrator log line (semantic analysis verdict,
skeleton mode, compile errors, AI repair attempts) to the console. Without it,
only `[NetAI]`-prefixed messages are shown.

**3. Dry run vs. prompt-only vs. full run**

Three modes, each doing strictly more work than the previous:

```bash
# (a) Dry run — no LLM, no file writes. Shows the file table only.
netai generate --project ./src/MyApp/MyApp.csproj --dry-run

# (b) Prompt-only — saves prompts to obj/netai/prompts/, calls the LLM,
#     but skips the compile-validation / AI-repair loop. Use this to
#     inspect the prompts that would be sent, or to compare LLM output
#     across runs without failing on compile errors.
netai generate --project ./src/MyApp/MyApp.csproj --prompt-only --verbose

# (c) Full run — prompt + LLM + compile validation + AI repair.
netai generate --project ./src/MyApp/MyApp.csproj --json > result.json
```

Example `result.json`:

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

**4. Bulk — a whole project or directory**

When you pass `--project`, the CLI loads the Roslyn compilation via MSBuild
**once**, then uses its syntax trees as the source list and reuses the same
compilation for every file. This avoids re-loading MSBuild per file and
enables semantic analysis for all of them.

```bash
# Whole project
netai generate --project ./src/MyApp/MyApp.csproj

# Directory (non-recursive by default)
netai generate ./src/MyApp/ --recursive

# Narrow down with filters
netai generate ./src/MyApp/ --recursive --filter "*.Services.cs" --exclude "*Tests.cs"

# Dry run first — no LLM, no writes, just a table of what would be processed
netai generate --project ./src/MyApp/MyApp.csproj --dry-run
```

At the end you get a summary table:

```
┌────────────────────────┬─────────┬──────────────────────┐
│ File                   │ Status  │ Test project         │
├────────────────────────┼─────────┼──────────────────────┤
│ OrderService.cs        │ ok      │ tests/MyApp.Tests    │
│ InvoiceService.cs      │ ok      │ tests/MyApp.Tests    │
│ Program.cs             │ skipped │ <not found>          │
└────────────────────────┴─────────┴──────────────────────┘
```

Failures in one file do not stop the rest. The process exit code is non-zero
whenever at least one file failed or was skipped. `obj/`, `bin/`, `*.Designer.cs`,
`*.g.cs` and `AssemblyInfo.cs` are always excluded.

Solutions are deliberately **not** supported as bulk input — pass a single
`.csproj` to `--project` or a directory instead.

### `netai analyse <sourceFile>`

Prints strategy / verdict / blockers per method without calling the LLM.

| Option             | Purpose |
|--------------------|---------|
| `--config <FILE>`  | Path to `aisettings.json`. |
| `--json`           | Emit a JSON document instead of a table. |

### `netai init [directory]`

Writes a default `aisettings.json` into the given directory (defaults to cwd).

---

## 4. Configuration

`aisettings.json` mirrors `AiTestingConfig` in `NetAI.TestGenerator.Core`.
Minimal example:

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

The `aiConfiguration` section is not read by the CLI — the LLM connection is
owned by `NetAI.TestGenerator.Core` and configured there (or via environment
variables consumed by `LocalLlmClient`).

---

## 5. Exit codes

| Code | Meaning |
|------|---------|
| 0    | Success |
| 1    | Configuration error (missing file, bad settings, no test project found) |
| 2    | Test generation failed for at least one file |
| 3    | Analysis failed |
| 99   | Unhandled error |
| 130  | Cancelled by Ctrl+C |

---

## 6. CI example

```bash
netai generate --project ./src/MyApp/MyApp.csproj --json > result.json
```

In JSON mode all chatter goes to stderr; stdout is exactly one JSON document.

---

## 7. Ctrl+C

Spectre.Console.Cli 0.49.1 exposes no cancellation hook on `AsyncCommand`, and
`ProcessProjectAsync` does not accept a `CancellationToken`. The CLI installs a
`Console.CancelKeyPress` handler that stops the batch loop after the current
file and returns exit code 130. The currently running LLM/compile step still
completes before the process exits.