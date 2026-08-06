@echo off
setlocal enabledelayedexpansion

rem ===========================================================================
rem  Publish OES Leak Monitor as a standalone single-file self-contained .exe.
rem  The target PC needs NO .NET install.
rem
rem  Just double-click this file (or run it from a command prompt).
rem  Prerequisite: Aqst.OesApp.Core 0.1.3 / Aqst.OesApp.Wpf 0.1.7 (or newer)
rem  must already be in ..\LocalPackages - pack them from the
rem  DualOes_PlasmaMonitor repo first.
rem ===========================================================================

cd /d "%~dp0"

rem NOTE: never name a variable here OUTDIR, OUTPUTPATH, CONFIGURATION or any
rem other MSBuild property - environment variables become global MSBuild
rem properties, so "set OUTDIR=..." silently redirects the build output into
rem that folder and breaks single-file bundling.
set "PUBFOLDER=%~dp0src\OES_Leak_Monitor\bin\Publish\win-x64"

echo ===========================================================================
echo  OES Leak Monitor - standalone publish
echo ===========================================================================
echo.

rem Locate the .NET SDK.
set "DOTNET=dotnet"
where dotnet >nul 2>&1
if errorlevel 1 set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"

rem The publish folder is emptied by the EmptyPublishDirBeforePublish target in
rem OES_Leak_Monitor.csproj, so re-publishing over a previous result is safe.
rem Do NOT add an "rmdir /s /q" here as well: deleting the folder from cmd and
rem again from MSBuild leaves it in a pending-delete state, and single-file
rem bundling then fails with "Unable to access file during bundling" followed by
rem a missing OES_Leak_Monitor.runtimeconfig.json. The completeness check below
rem is what guards this script.

"%DOTNET%" publish src\OES_Leak_Monitor\OES_Leak_Monitor.csproj -p:PublishProfile=SelfContained-win-x64
if errorlevel 1 (
    echo.
    echo  *** PUBLISH FAILED - see the messages above. ***
    echo.
    echo  Most common cause: Aqst.OesApp.Core 0.1.3 / Aqst.OesApp.Wpf 0.1.7 are
    echo  missing from ..\LocalPackages. Pack them from the DualOes_PlasmaMonitor
    echo  repo, then run this script again.
    echo.
    pause
    exit /b 1
)

rem Verify the delivery folder is complete. A publish can "succeed" and still
rem produce a folder missing the native DLLs, which is invisible until the app
rem silently runs in test mode on the production PC.
set "MISSING="
for %%F in (
    OES_Leak_Monitor.exe
    wpfgfx_cor3.dll
    PresentationNative_cor3.dll
    D3DCompiler_47_cor3.dll
    PenImc_cor3.dll
    vcruntime140_cor3.dll
    UserApplication.dll
    SiUSBXp.dll
    libsodium.dll
    user-manual-zh-TW.html
    daily-inspection-plan-zh-TW.html
) do (
    if not exist "%PUBFOLDER%\%%F" set "MISSING=!MISSING! %%F"
)

if defined MISSING (
    echo.
    echo  *** INCOMPLETE PUBLISH - DO NOT SHIP THIS FOLDER ***
    echo.
    echo  Missing from %PUBFOLDER%:
    for %%M in (!MISSING!) do echo    %%M
    echo.
    echo  Delete the folder and run this script again. If only the .html docs are
    echo  missing, regenerate them first:
    echo    python3 tools\md2html.py docs\user-manual-zh-TW.md docs\user-manual-zh-TW.html "OES Leak Monitor manual"
    echo    python3 tools\md2html.py docs\daily-inspection-plan-zh-TW.md docs\daily-inspection-plan-zh-TW.html "OES Plasma Monitor inspection plan"
    echo.
    pause
    exit /b 1
)

echo.
echo  *** PUBLISH SUCCEEDED - all 11 files present ***
echo  Output folder: %PUBFOLDER%
echo.
echo  Ship the WHOLE win-x64 folder. The .exe bundles the managed code and
echo  the .NET 8 runtime, but the native DLLs next to it must travel along:
echo    OES_Leak_Monitor.exe       app + bundled .NET 8 runtime
echo    wpfgfx_cor3.dll etc.       WPF native runtime DLLs
echo    UserApplication.dll        native OES DLL
echo    SiUSBXp.dll                native USB DLL
echo    libsodium.dll              native OES SDK dependency
echo    user-manual-zh-TW.html     operator manual
echo    daily-inspection-plan-zh-TW.html   daily/weekly inspection plan
echo.

if exist "%PUBFOLDER%" start "" explorer "%PUBFOLDER%"

pause
endlocal
