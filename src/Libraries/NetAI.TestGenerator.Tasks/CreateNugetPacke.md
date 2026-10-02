cd C:\Users\steph\source\repos\NetAI.TestGenerator\src\Libraries\NetAI.TestGenerator.Tasks

// später
dotnet pack -c Release
// lokal das, erzeugt NetAI.TestGenerator.Tasks.1.0.0-dev.20261002.1553.nupkg
dotnet pack -c Debug

dotnet nuget locals all --clear


