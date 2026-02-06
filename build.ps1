<#
.SYNOPSIS
    Build script for VibeModel Revit add-in.
    Automatically closes Revit, builds, and restarts.

.PARAMETER RevitVersion
    Target Revit version (2022, 2023, 2024, 2025). Default: 2023

.PARAMETER Configuration
    Build configuration (Debug, Release). Default: Release

.PARAMETER NoRestart
    Don't restart Revit after build.

.PARAMETER Debug
    Enable debug logging in Revit (sets VIBEMODEL_DEBUG=1)

.EXAMPLE
    .\build.ps1
    .\build.ps1 -RevitVersion 2024
    .\build.ps1 -Debug
    .\build.ps1 -NoRestart
#>

param(
    [ValidateSet("2022", "2023", "2024", "2025")]
    [string]$RevitVersion = "2023",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$NoRestart,

    [switch]$Debug
)

$ErrorActionPreference = "Stop"
$ProjectDir = "$PSScriptRoot\src\VibeModel"
$RevitExe = "C:\Program Files\Autodesk\Revit $RevitVersion\Revit.exe"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  VibeModel Build Script" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Target: Revit $RevitVersion ($Configuration)" -ForegroundColor Gray
Write-Host ""

# Step 1: Close Revit if running
$revitProcess = Get-Process -Name "Revit" -ErrorAction SilentlyContinue
if ($revitProcess) {
    Write-Host "[1/4] Closing Revit..." -ForegroundColor Yellow

    $revitProcess | ForEach-Object { $_.CloseMainWindow() | Out-Null }

    $timeout = 30
    $elapsed = 0
    while ((Get-Process -Name "Revit" -ErrorAction SilentlyContinue) -and $elapsed -lt $timeout) {
        Start-Sleep -Seconds 1
        $elapsed++
        Write-Host "    Waiting for Revit to close... ($elapsed s)" -ForegroundColor Gray
    }

    $revitProcess = Get-Process -Name "Revit" -ErrorAction SilentlyContinue
    if ($revitProcess) {
        Write-Host "    Force closing Revit..." -ForegroundColor Red
        $revitProcess | Stop-Process -Force
        Start-Sleep -Seconds 2
    }

    Write-Host "    Revit closed." -ForegroundColor Green
} else {
    Write-Host "[1/4] Revit not running." -ForegroundColor Gray
}

# Step 2: Build
Write-Host "[2/4] Building VibeModel..." -ForegroundColor Yellow

Push-Location $ProjectDir
try {
    $buildOutput = & dotnet build -c $Configuration -p:RevitVersion=$RevitVersion 2>&1
    $buildSuccess = $LASTEXITCODE -eq 0

    if ($buildSuccess) {
        Write-Host "    Build succeeded!" -ForegroundColor Green
    } else {
        Write-Host "    Build FAILED!" -ForegroundColor Red
        Write-Host $buildOutput
        Pop-Location
        exit 1
    }
} finally {
    Pop-Location
}

# Step 3: Verify deployment
Write-Host "[3/4] Verifying deployment..." -ForegroundColor Yellow
$addinFolder = "$env:APPDATA\Autodesk\Revit\Addins\$RevitVersion"

$dllPath = "$addinFolder\VibeModel.dll"
$addinPath = "$addinFolder\VibeModel.addin"

if ((Test-Path $dllPath) -and (Test-Path $addinPath)) {
    $dllInfo = Get-Item $dllPath
    Write-Host "    DLL deployed: $($dllInfo.LastWriteTime)" -ForegroundColor Green
} else {
    Write-Host "    WARNING: Files not found in Addins folder!" -ForegroundColor Red
    Write-Host "    Expected: $dllPath" -ForegroundColor Gray
}

# Step 4: Restart Revit
if (-not $NoRestart) {
    Write-Host "[4/4] Starting Revit $RevitVersion..." -ForegroundColor Yellow

    if (-not (Test-Path $RevitExe)) {
        Write-Host "    WARNING: Revit executable not found at:" -ForegroundColor Red
        Write-Host "    $RevitExe" -ForegroundColor Gray
        Write-Host "    Please start Revit manually." -ForegroundColor Gray
    } else {
        if ($Debug) {
            $env:VIBEMODEL_DEBUG = "1"
            Write-Host "    Debug logging ENABLED" -ForegroundColor Magenta
            Write-Host "    Logs: $env:LOCALAPPDATA\VibeModel\logs\" -ForegroundColor Gray
        }

        Start-Process $RevitExe
        Write-Host "    Revit started!" -ForegroundColor Green
    }
} else {
    Write-Host "[4/4] Skipping Revit restart (--NoRestart)" -ForegroundColor Gray
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Build complete!" -ForegroundColor Green
Write-Host "  HTTP server: http://localhost:18884" -ForegroundColor Gray
Write-Host "  Test: curl -s http://localhost:18884/health" -ForegroundColor Gray
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
