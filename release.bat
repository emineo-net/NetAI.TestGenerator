@echo off
setlocal

if "%~1"=="" (
    echo.
    echo Verwendung: release.bat ^<version^>
    echo Beispiel:   release.bat 1.0.1
    echo.
    pause
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0release.ps1" -Version %~1

endlocal
pause