@echo off
setlocal EnableExtensions
title Nasaq - Tag and publish a GitHub release
cd /d "%~dp0.."

where git >nul 2>nul
if errorlevel 1 (
    echo   Git was not found on this PC.
    echo   Install Git for Windows from https://git-scm.com/download/win
    echo   or use GitHub Desktop, then try again.
    echo.
    pause
    exit /b 1
)

git rev-parse --is-inside-work-tree >nul 2>nul
if errorlevel 1 (
    echo   This folder is not a Git repository yet.
    echo.
    echo   Follow "Put the project on GitHub" in build\BUILD-GUIDE.md first,
    echo   then double-click this file again.
    echo.
    pause
    exit /b 1
)

for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "([xml](Get-Content -LiteralPath 'Nasaq.csproj')).Project.PropertyGroup.Version | Select-Object -First 1"`) do set "VERSION=%%v"

if "%VERSION%"=="" (
    echo   Could not read the version from Nasaq.csproj.
    echo.
    pause
    exit /b 1
)

set "TAG=v%VERSION%"

git status --porcelain | findstr /r "." >nul
if not errorlevel 1 (
    echo   You have uncommitted changes. Commit and push them before releasing.
    echo.
    git status --short
    echo.
    pause
    exit /b 1
)

echo.
echo   Version : %VERSION%
echo   Tag     : %TAG%
echo.
echo   This will:
echo     1. Push the current branch to GitHub.
echo     2. Create tag %TAG% on the current commit.
echo     3. Push the tag, which starts the "Build and Release" workflow.
echo.
echo   GitHub Actions will build the installer and attach it to the release.
echo.
pause

git push
if errorlevel 1 (
    echo.
    echo   Could not push the current branch.
    echo   Make sure this repository has an "origin" remote and an upstream branch.
    echo.
    pause
    exit /b 1
)

git tag "%TAG%"
if errorlevel 1 (
    echo.
    echo   Could not create tag %TAG%. It may already exist.
    echo   To release a new build, bump ^<Version^> in Nasaq.csproj first.
    echo.
    pause
    exit /b 1
)

git push origin "%TAG%"
if errorlevel 1 (
    echo.
    echo   Could not push tag %TAG%.
    echo   Check that "origin" points to your GitHub repository.
    echo.
    pause
    exit /b 1
)

echo.
echo   Tag %TAG% pushed.
echo   Open your repository on GitHub, click the "Actions" tab, and watch
echo   the "Build and Release" run. The installer will appear under
echo   "Releases" when it finishes.
echo.
pause
