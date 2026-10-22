<#
.SYNOPSIS
    Builds Nasaq as a self-contained Windows x64 application and wraps it in a
    single .exe installer using Inno Setup 6.

.DESCRIPTION
    Run this through build.cmd (double-click) for a one-click build.

    The script:
      1. Publishes a self-contained copy of Nasaq (no .NET runtime needed on
         the target PC).
      2. Optionally downloads FFmpeg and places it in the published "tools"
         folder so audio/video conversion works out of the box.
      3. Runs Nasaq's built-in self-test against the published files.
      4. Compiles dist\Nasaq-Setup-<version>.exe with Inno Setup.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER Runtime
    Target runtime identifier. Default: win-x64.

.PARAMETER NoFfmpeg
    Do not download or bundle FFmpeg. The installer will be much smaller, but
    audio/video conversion will need FFmpeg installed separately.

.PARAMETER SkipSelfTest
    Skip the built-in conversion self-test.

.PARAMETER SkipInstaller
    Publish only; do not run Inno Setup.
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",

    [switch]$NoFfmpeg,
    [switch]$SkipSelfTest,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# Show the self-test's "·" separator correctly in the console.
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$BuildDir     = $PSScriptRoot
$ProjectRoot  = Split-Path -Parent $BuildDir
$ProjectFile  = Join-Path $ProjectRoot "Nasaq.csproj"
$PublishDir   = Join-Path $BuildDir "publish"
$CacheDir     = Join-Path $BuildDir "cache"
$DistDir      = Join-Path $ProjectRoot "dist"
$InnoScript   = Join-Path $BuildDir "installer.iss"
$SelfTestDir  = Join-Path $BuildDir "selftest"
$FfmpegUrl    = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
$InnoUrl      = "https://jrsoftware.org/isdl.php"
$ExeName      = "Nasaq.exe"

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Write-Ok([string]$Text) {
    Write-Host "    $Text" -ForegroundColor Green
}

function Write-Note([string]$Text) {
    Write-Host "    $Text" -ForegroundColor DarkGray
}

function Stop-Build([string]$Text) {
    Write-Host ""
    Write-Host "ERROR: $Text" -ForegroundColor Red
    Write-Host ""
    exit 1
}

function Find-Iscc {
    $candidates = @()

    $pf86 = ${env:ProgramFiles(x86)}
    if ($pf86) { $candidates += (Join-Path $pf86 "Inno Setup 6\ISCC.exe") }

    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe") }
    if ($env:LOCALAPPDATA) {
        $candidates += (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
        $candidates += (Join-Path $env:LOCALAPPDATA "Inno Setup 6\ISCC.exe")
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }

    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $uninstallRoots = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
    )
    foreach ($root in $uninstallRoots) {
        try {
            $keys = Get-ChildItem -Path $root -ErrorAction Stop |
                    Where-Object { $_.PSChildName -like "Inno Setup*" }
            foreach ($key in $keys) {
                $location = (Get-ItemProperty -Path $key.PSPath -ErrorAction SilentlyContinue).InstallLocation
                if ($location) {
                    $exe = Join-Path $location "ISCC.exe"
                    if (Test-Path -LiteralPath $exe) { return $exe }
                }
            }
        } catch { }
    }

    return $null
}

function Install-BundledFfmpeg {
    param([string]$Destination)

    $toolsDir = Join-Path $Destination "tools"
    New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null
    New-Item -ItemType Directory -Path $CacheDir -Force | Out-Null

    $zip        = Join-Path $CacheDir "ffmpeg-release-essentials.zip"
    $extractDir = Join-Path $CacheDir "ffmpeg-extract"

    if ((Test-Path -LiteralPath $zip) -and ((Get-Item -LiteralPath $zip).Length -lt 50MB)) {
        Write-Note "Discarding an incomplete FFmpeg download."
        Remove-Item -LiteralPath $zip -Force
    }

    if (-not (Test-Path -LiteralPath $zip)) {
        Write-Note "Downloading FFmpeg (about 115 MB, cached for next time)..."
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        try {
            Invoke-WebRequest -Uri $FfmpegUrl -OutFile $zip -UseBasicParsing
        } catch {
            if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue }
            throw "Could not download FFmpeg: $($_.Exception.Message)"
        }
    } else {
        Write-Note "Using the cached FFmpeg archive."
    }

    if (Test-Path -LiteralPath $extractDir) {
        Remove-Item -LiteralPath $extractDir -Recurse -Force
    }

    Write-Note "Extracting FFmpeg..."
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    try {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $extractDir)
    } catch {
        Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $extractDir -Recurse -Force -ErrorAction SilentlyContinue
        throw "The FFmpeg archive was damaged and has been removed. Run the build again to re-download it."
    }

    $ffmpeg  = Get-ChildItem -Path $extractDir -Filter "ffmpeg.exe"  -Recurse -File | Select-Object -First 1
    $ffprobe = Get-ChildItem -Path $extractDir -Filter "ffprobe.exe" -Recurse -File | Select-Object -First 1
    if (-not $ffmpeg) { throw "ffmpeg.exe was not found inside the downloaded archive." }

    Copy-Item -LiteralPath $ffmpeg.FullName -Destination (Join-Path $toolsDir "ffmpeg.exe") -Force
    if ($ffprobe) {
        Copy-Item -LiteralPath $ffprobe.FullName -Destination (Join-Path $toolsDir "ffprobe.exe") -Force
    }

    $license = Get-ChildItem -Path $extractDir -Filter "LICENSE*" -Recurse -File | Select-Object -First 1
    if ($license) {
        Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $toolsDir "FFmpeg-LICENSE.txt") -Force
    }

    $notice = Join-Path $BuildDir "FFmpeg-NOTICE.txt"
    if (Test-Path -LiteralPath $notice) {
        Copy-Item -LiteralPath $notice -Destination (Join-Path $toolsDir "FFmpeg-NOTICE.txt") -Force
    }

    $bundled = Join-Path $toolsDir "ffmpeg.exe"
    # Capture the output first. Piping directly into Select-Object -First 1 can
    # close the pipe early and make ffmpeg exit with a non-zero code.
    $versionOutput = @(& $bundled -version 2>&1)
    $ffmpegExit = $LASTEXITCODE
    if ($ffmpegExit -ne 0 -or $versionOutput.Count -eq 0) {
        throw "The bundled ffmpeg.exe did not start correctly (exit code $ffmpegExit)."
    }

    Write-Ok ("FFmpeg bundled: " + $versionOutput[0])
    Remove-Item -LiteralPath $extractDir -Recurse -Force -ErrorAction SilentlyContinue
}

# ── Pre-flight ────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "  Nasaq installer build" -ForegroundColor White
Write-Host "  --------------------" -ForegroundColor DarkGray

if (-not (Test-Path -LiteralPath $ProjectFile)) {
    Stop-Build "Nasaq.csproj was not found at $ProjectFile"
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Stop-Build @"
The .NET 8 SDK is required to build Nasaq.

Download it here:
  https://dotnet.microsoft.com/download/dotnet/8.0

Click 'SDK x64' under Windows, run the downloaded file, and click Next
through the installer. Then double-click build.cmd again.
"@
}

$sdkLines = & dotnet --list-sdks
$hasSdk = $false
foreach ($line in $sdkLines) {
    if ($line -match '^(?<major>\d+)\.') {
        if ([int]$Matches['major'] -ge 8) { $hasSdk = $true }
    }
}
if (-not $hasSdk) {
    Stop-Build "The .NET 8 SDK (or newer) is required. Download it from https://dotnet.microsoft.com/download/dotnet/8.0"
}
Write-Ok ("Using .NET SDK: " + (($sdkLines | Select-Object -Last 1).Trim()))

$iscc = $null
if (-not $SkipInstaller) {
    $iscc = Find-Iscc
    if (-not $iscc) {
        Write-Host ""
        Write-Host "  Inno Setup 6 was not found on this PC." -ForegroundColor Yellow
        Write-Host "  The app will still be published, but the installer cannot be built yet." -ForegroundColor Yellow
        Write-Host "  Download it here: $InnoUrl" -ForegroundColor Yellow
        Write-Host ""
        try { Start-Process $InnoUrl } catch { }
    }
}

# ── Publish ───────────────────────────────────────────────────────────────────

Write-Step "Publishing Nasaq ($Configuration, $Runtime, self-contained)"

if (Test-Path -LiteralPath $PublishDir) {
    Remove-Item -LiteralPath $PublishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null

$publishArgs = @(
    "publish", $ProjectFile,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=false",
    "-p:DebugType=none",
    "-p:DebugSymbols=false",
    "-o", $PublishDir
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    Stop-Build "dotnet publish failed (exit code $LASTEXITCODE)."
}

$publishedExe = Join-Path $PublishDir $ExeName
if (-not (Test-Path -LiteralPath $publishedExe)) {
    Stop-Build "The publish step finished but $ExeName was not created."
}
Write-Ok "Published to $PublishDir"

# ── Optional FFmpeg ──────────────────────────────────────────────────────────

if (-not $NoFfmpeg) {
    Write-Step "Bundling FFmpeg"
    try {
        Install-BundledFfmpeg -Destination $PublishDir
    } catch {
        Write-Host ""
        Write-Warning $_.Exception.Message
        Write-Host "  Continuing without bundled FFmpeg." -ForegroundColor Yellow
        Write-Host "  Audio/video conversion will need FFmpeg installed separately." -ForegroundColor Yellow
    }
} else {
    Write-Step "Skipping FFmpeg (-NoFfmpeg)"
    Write-Note "The installer will be smaller, but audio/video conversion will need FFmpeg."
}

# ── Self-test ────────────────────────────────────────────────────────────────

if (-not $SkipSelfTest) {
    Write-Step "Running the built-in self-test"

    if (Test-Path -LiteralPath $SelfTestDir) {
        Remove-Item -LiteralPath $SelfTestDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $SelfTestDir -Force | Out-Null

    $process = Start-Process -FilePath $publishedExe `
        -ArgumentList @("--selftest", "`"$SelfTestDir`"") -PassThru

    if (-not $process.WaitForExit(300000)) {
        try { $process.Kill() } catch { }
        Stop-Build "The self-test timed out after five minutes."
    }

    $report = Join-Path $SelfTestDir "selftest-report.txt"
    if (Test-Path -LiteralPath $report) {
        $summary = Select-String -Path $report -Pattern "^SUMMARY" | Select-Object -First 1
        if ($summary) { Write-Note $summary.Line.Trim() }
    }

    if ($process.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $report) {
            Write-Host ""
            Get-Content -LiteralPath $report | ForEach-Object { Write-Host "    $_" }
        }
        Stop-Build "The self-test reported a failure. Review $report"
    }
    Write-Ok "Self-test passed."
} else {
    Write-Step "Skipping the self-test (-SkipSelfTest)"
}

# ── Installer ────────────────────────────────────────────────────────────────

if ($SkipInstaller) {
    Write-Step "Skipping the installer (-SkipInstaller)"
    Write-Ok "Application folder: $PublishDir"
    exit 0
}

if (-not $iscc) {
    Write-Host ""
    Write-Host "The app is ready in:" -ForegroundColor Yellow
    Write-Host "  $PublishDir" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Install Inno Setup 6 from $InnoUrl and double-click build.cmd again" -ForegroundColor Yellow
    Write-Host "to produce the installer." -ForegroundColor Yellow
    Write-Host ""
    exit 2
}

Write-Step "Compiling the installer with Inno Setup"

if (-not (Test-Path -LiteralPath $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
}

$version = (Get-Item -LiteralPath $publishedExe).VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($version)) { $version = "1.0.0" }

$innoArgs = @(
    "/Qp",
    "/DMyAppVersion=$version",
    "/O$DistDir",
    $InnoScript
)

& $iscc @innoArgs
if ($LASTEXITCODE -ne 0) {
    Stop-Build "Inno Setup failed (exit code $LASTEXITCODE)."
}

$setup = Get-ChildItem -Path $DistDir -Filter "Nasaq-Setup-*.exe" -File |
         Sort-Object LastWriteTime -Descending |
         Select-Object -First 1
if (-not $setup) {
    Stop-Build "Inno Setup finished but no installer was found in $DistDir."
}

$sizeMb = [math]::Round($setup.Length / 1MB, 1)

Write-Host ""
Write-Host "  Build complete" -ForegroundColor Green
Write-Host "  --------------" -ForegroundColor DarkGray
Write-Host "  Installer : $($setup.FullName)" -ForegroundColor White
Write-Host "  Size      : $sizeMb MB" -ForegroundColor White
Write-Host "  App folder: $PublishDir" -ForegroundColor DarkGray
Write-Host ""
