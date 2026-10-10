@echo off
setlocal enabledelayedexpansion

:: ============================================================
:: publishNuget.bat - Veroeffentlicht das Paket auf nuget.org
:: Autor: Stephan Emmermann / emineo-net
:: ============================================================

:: --- KONFIGURATION -----------------------------------------
set "PROJECT_DIR=C:\Users\steph\source\repos\NetAI.TestGenerator\src\Libraries\NetAI.TestGenerator.Tasks"
set "NUGET_SOURCE=https://api.nuget.org/v3/index.json"

:: API-Key HIER eintragen (oder als Umgebungsvariable NUGET_API_KEY setzen)
if "%NUGET_API_KEY%"=="" set "NUGET_API_KEY=HIER_DEINEN_API_KEY_EINTRAGEN"
:: -----------------------------------------------------------

echo ============================================================
echo   NuGet Release-Publish fuer NetAI.TestGenerator.Tasks
echo ============================================================
echo.

if /i "%NUGET_API_KEY%"=="HIER_DEINEN_API_KEY_EINTRAGEN" (
    echo [FEHLER] Bitte API-Key in dieser .bat eintragen oder NUGET_API_KEY setzen.
    pause
    exit /b 1
)

echo Wechsle in das Projektverzeichnis...
cd /d "%PROJECT_DIR%" || (echo [FEHLER] Projektverzeichnis nicht gefunden. & pause & exit /b 1)

echo.
echo Stoppe blockierende MSBuild-Prozesse...
dotnet build-server shutdown >nul

echo Loesche alte bin- und obj-Ordner...
for /d /r %%x in (bin obj) do if exist "%%x" rmdir /s /q "%%x"

echo.
echo Kompiliere im RELEASE-Modus...
dotnet build -c Release
if errorlevel 1 (echo [FEHLER] Build fehlgeschlagen. & pause & exit /b 1)

echo.
echo Erstelle RELEASE-NuGet-Paket...
dotnet pack -c Release --no-build
if errorlevel 1 (echo [FEHLER] Pack fehlgeschlagen. & pause & exit /b 1)

:: --- .nupkg ermitteln ---
set "NUPKG="
for %%f in ("bin\Release\*.nupkg") do (
    echo %%f | findstr /i /c:".symbols.nupkg" >nul
    if errorlevel 1 set "NUPKG=%%f"
)

if "%NUPKG%"=="" (
    echo [FEHLER] Keine .nupkg-Datei in bin\Release gefunden.
    pause
    exit /b 1
)

echo.
echo ============================================================
echo   Paket gefunden: %NUPKG%
echo   Ziel:           %NUGET_SOURCE%
echo ============================================================
echo.
set /p CONFIRM="Jetzt veroeffentlichen? (j/n): "
if /i not "%CONFIRM%"=="j" (
    echo Abbruch durch Benutzer.
    pause
    exit /b 0
)

echo.
echo Push nach nuget.org...
dotnet nuget push "%NUPKG%" --api-key "%NUGET_API_KEY%" --source "%NUGET_SOURCE%" --skip-duplicate
if errorlevel 1 (
    echo.
    echo [FEHLER] Push fehlgeschlagen. Mögliche Ursachen:
    echo   - Version bereits vorhanden
    echo   - API-Key ungueltig oder abgelaufen
    echo   - PackageId bereits von jemand anderem belegt
    pause
    exit /b 1
)

echo.
echo ============================================================
echo   ERFOLGREICH veroeffentlicht!
echo   Pruefe: https://www.nuget.org/packages/NetAI.TestGenerator.Tasks
echo ============================================================
echo.
echo Hinweis: Es dauert 1-5 Minuten, bis das Paket indexiert und
echo          in der Suche sichtbar ist.
echo.
pause
endlocal