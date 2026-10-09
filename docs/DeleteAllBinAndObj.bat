@echo off
echo Loesche alle bin und obj Ordner...

for /d /r %%p in (bin) do (
    if exist "%%p" (
        echo Loesche: "%%p"
        rd /s /q "%%p"
    )
)

for /d /r %%p in (obj) do (
    if exist "%%p" (
        echo Loesche: "%%p"
        rd /s /q "%%p"
    )
)

echo Bereinigung abgeschlossen!
pause
