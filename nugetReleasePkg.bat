@echo off
cd /d "C:\Users\steph\source\repos\NetAI.TestGenerator\src\Libraries\NetAI.TestGenerator.Tasks"

echo Release-Build...
dotnet build -c Release

echo Release-Pack...
dotnet pack -c Release --no-build

echo.
echo Fertig! Paket liegt in: bin\Release\
dir /b bin\Release\*.nupkg
pause