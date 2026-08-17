@echo off
REM Double-click me. Runs check-oes-connect.ps1 from this same folder and saves
REM the result next to it as oes-diagnostic.txt, so it can be sent back by mail.
REM Put BOTH files in the folder that holds OES_Leak_Monitor.exe.
setlocal
cd /d "%~dp0"

if not exist "%~dp0check-oes-connect.ps1" (
    echo check-oes-connect.ps1 is not in this folder - copy both files together.
    pause
    exit /b 1
)

if not exist "%~dp0OES_Leak_Monitor.exe" (
    echo.
    echo WARNING: OES_Leak_Monitor.exe is not in this folder.
    echo The check must run in the app folder, or it inspects the wrong files.
    echo.
    pause
)

"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -Command "& '%~dp0check-oes-connect.ps1' *>&1 | Tee-Object -FilePath '%~dp0oes-diagnostic.txt'"

echo.
echo ------------------------------------------------------------------
echo Saved to: %~dp0oes-diagnostic.txt
echo.
pause
