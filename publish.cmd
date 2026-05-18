@echo off
setlocal

rem ===========================================================================
rem  Publish OES Leak Monitor as a standalone single-file self-contained .exe.
rem  The target PC needs NO .NET install.
rem
rem  Just double-click this file (or run it from a command prompt).
rem  Prerequisite: Aqst.OesApp.Wpf 0.1.2 (or newer) must already be in
rem  ..\LocalPackages - pack it from the DualOes_PlasmaMonitor repo first.
rem ===========================================================================

cd /d "%~dp0"

echo ===========================================================================
echo  OES Leak Monitor - standalone publish
echo ===========================================================================
echo.

rem Locate the .NET SDK.
set "DOTNET=dotnet"
where dotnet >nul 2>&1
if errorlevel 1 set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"

"%DOTNET%" publish src\OES_Leak_Monitor\OES_Leak_Monitor.csproj -p:PublishProfile=SelfContained-win-x64
if errorlevel 1 (
    echo.
    echo  *** PUBLISH FAILED - see the messages above. ***
    echo.
    echo  Most common cause: Aqst.OesApp.Wpf 0.1.2 is missing from
    echo  ..\LocalPackages. Pack it from the DualOes_PlasmaMonitor repo, then
    echo  run this script again.
    echo.
    pause
    exit /b 1
)

set "OUTDIR=%~dp0src\OES_Leak_Monitor\bin\Publish\win-x64"
echo.
echo  *** PUBLISH SUCCEEDED ***
echo  Output folder: %OUTDIR%
echo.
echo  Ship the WHOLE win-x64 folder. The .exe bundles the managed code and
echo  the .NET 8 runtime, but the native DLLs next to it must travel along:
echo    OES_Leak_Monitor.exe       app + bundled .NET 8 runtime
echo    wpfgfx_cor3.dll etc.       WPF native runtime DLLs
echo    UserApplication.dll        native OES DLL
echo    SiUSBXp.dll                native USB DLL
echo.

if exist "%OUTDIR%" start "" explorer "%OUTDIR%"

pause
endlocal
