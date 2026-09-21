@echo off
echo ========================================================
echo 🤖 KI-Translator: Loesche blockierende Prozesse...
echo ========================================================

:: 1. Beendet alle laufenden MSBuild-Prozesse (Ersatz fuer Stop-Process)
:: /F erzwingt das Beenden, /IM gibt den Prozessnamen an
taskkill /F /IM MSBuild.exe 2>nul

:: 2. Loescht den dotnet Build-Server Cache
call dotnet build-server shutdown



echo.
echo === Fertig! Das Terminal bleibt offen. ===
pause
