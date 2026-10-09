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
echo ========================================================
echo 📁 Wechsle in das WPF-Projektverzeichnis...
echo ========================================================
cd /d "C:\Users\steph\source\repos\WebObserver2\WpfExplorer"

echo.
echo ========================================================
echo 🛠️ Starte sauberen WPF-Build...
echo ========================================================
:: call sorgt dafuer, dass die Batch-Datei nach dem Build nicht einfach schliesst
call dotnet build --no-dependencies

echo.
echo === Fertig! Das Terminal bleibt offen. ===
pause
