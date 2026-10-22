@echo off
setlocal EnableExtensions
title Nasaq - Build installer
cd /d "%~dp0"

echo.
echo   Nasaq installer build
echo   --------------------
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo   The .NET 8 SDK was not found on this PC.
    echo.
    echo   Download it here:
    echo     https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    echo   Under Windows, click the x64 button next to ".NET 8.0 SDK",
    echo   run the downloaded file, click Install, then double-click
    echo   build.cmd again.
    echo.
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set "CODE=%ERRORLEVEL%"

echo.
if "%CODE%"=="0" (
    echo   Build finished successfully.
    echo   The installer is in the "dist" folder.
) else if "%CODE%"=="2" (
    echo   The app was published, but Inno Setup is not installed.
    echo   Install it from https://jrsoftware.org/isdl.php and run
    echo   build.cmd again.
) else (
    echo   Build failed with exit code %CODE%.
    echo   Read the messages above for the reason.
)
echo.
pause
exit /b %CODE%
