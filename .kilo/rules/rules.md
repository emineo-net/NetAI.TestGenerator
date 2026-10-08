# NetAI.TestGenerator – Project Rules

## Solution
- NetAI.TestGenerator.slnx
- Projects & TFMs:
  - NetAI.TestGenerator.Core        -> <TargetFrameworks>netstandard2.0;net10.0</TargetFrameworks>
  - NetAI.TestGenerator.Tasks       -> netstandard2.0 (NuGet package)
  - NetAI.TestGenerator.Core.Tests  -> net10.0 (xUnit)
  - NetAI.TestGenerator.Tasks.Tests -> net10.0 (xUnit)
- Consumer: external WPF app references the NetAI.TestGenerator.Tasks
  NuGet package and calls its public facade to generate a target
  test project containing xUnit test methods.

## Commands
- Build: dotnet build NetAI.TestGenerator.slnx -c Debug --nologo
- Test:  dotnet test  NetAI.TestGenerator.slnx --nologo
- Pack:  dotnet pack  NetAI.TestGenerator.Tasks -c Release -o ./artifacts

## Architecture
- Core: Clean Architecture (Domain -> Application -> Infrastructure
  as folders or sub-projects).
- Tasks: thin adapter over Core. MSBuild Task classes + public facade.
  No business logic in Tasks. Public surface = NuGet contract for the
  WPF app.

## Multi-Target Core
- Core: <TargetFrameworks>netstandard2.0;net10.0</TargetFrameworks>
- Jede Zeile Core muss auf beiden TFMs kompilieren.
- Verboten in Core ohne #if: required, DateOnly, TimeOnly, Half,
  Index/Range, STJ-Source-Gen, net7+-only APIs.
- #if NETSTANDARD2_0 / NET10_0_OR_GREATER nur mit Kommentar und
  nur wenn kein gemeinsamer Code möglich ist.

## Tasks
- netstandard2.0 only.
- MSBuild Task = dünner Adapter. Keine Logik.
- Public Facade = NuGet-Contract für die WPF-App. XML-Doc Pflicht.
- Akzeptanztest: `dotnet pack` muss durchlaufen und valides .nupkg liefern.
- Pre-approved packages (nur diese):
  Microsoft.Build.Utilities.Core, Microsoft.Build.Framework.

## Hard constraints
- No EF migrations, no schema changes.
- No AutoMapper.
- No MVVM, no ViewModels, no binding frameworks. Code-behind only.
- No new NuGet packages without explicit approval.
- Generated test project must be:
  net10.0, xUnit, deterministic, no live LLM call inside generated tests.
- Do not change public APIs across project boundaries unless asked.

## Preferred patterns
- Primary constructors where the TFM allows them.
- Result<T> for fallible flows. No exceptions for control flow.
- Extension methods for reusable helpers.
- Records for DTOs.
- CancellationToken propagated through every async API.
- Test method naming: MethodName_Scenario_ExpectedResult.

## File naming
- One public type per file, filename = type name.
- Namespaces mirror folders.