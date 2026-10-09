@echo off
echo Wechsle in das Projektverzeichnis...
cd /d "C:\Users\steph\source\repos\NetAI.TestGenerator\src\Libraries\NetAI.TestGenerator.Tasks"

echo Stoppe blockierende MSBuild-Prozesse (loest Access Denied)...
dotnet build-server shutdown

echo Loesche alte bin- und obj-Ordner...
for /d /r %%x in (bin obj) do if exist "%%x" rmdir /s /q "%%x"

echo Bereinige NuGet-Cache...
dotnet nuget locals all --clear

echo Kompiliere das Projekt frisch...
dotnet build -c Debug

echo Erstelle neues NuGet-Paket...
dotnet pack -c Debug --no-build

echo Fertig!
pause
