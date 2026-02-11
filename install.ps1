<#
.SYNOPSIS
    Installs VibeModel add-in for Autodesk Revit.

.DESCRIPTION
    Copies VibeModel.dll, VibeModel.addin, and Markdig.dll to detected Revit
    add-in folders and optionally saves your Anthropic API key.

.PARAMETER SkipApiKey
    Skip the API key prompt.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -SkipApiKey
#>

param(
    [switch]$SkipApiKey
)

$ErrorActionPreference = 'Stop'

Write-Host ""
Write-Host "  VibeModel Installer" -ForegroundColor Cyan
Write-Host "  ===================" -ForegroundColor Cyan
Write-Host ""

# --- Locate source files ---

$scriptDir = $PSScriptRoot
$sourceFiles = @("VibeModel.dll", "VibeModel.addin", "Markdig.dll")

# Search directories — build output dirs + script dir + repo root
$searchDirs = @(
    $scriptDir,
    (Join-Path $scriptDir "bin\Release\Revit2023"),
    (Join-Path $scriptDir "bin\Release\Revit2024"),
    (Join-Path $scriptDir "bin\Release\Revit2025"),
    (Join-Path $scriptDir "src\VibeModel\bin\Release\Revit2023"),
    (Join-Path $scriptDir "src\VibeModel\bin\Release\Revit2024"),
    (Join-Path $scriptDir "src\VibeModel\bin\Release\Revit2025")
)

# Resolve each file independently (DLLs may be in build output, .addin in repo root)
$resolvedFiles = @{}
foreach ($file in $sourceFiles) {
    foreach ($dir in $searchDirs) {
        $candidate = Join-Path $dir $file
        if (Test-Path $candidate) {
            $resolvedFiles[$file] = $candidate
            break
        }
    }
}

if (-not $resolvedFiles.ContainsKey("VibeModel.dll")) {
    Write-Host "  ERROR: Cannot find VibeModel.dll" -ForegroundColor Red
    Write-Host "  Run this script from the release/build output directory," -ForegroundColor Red
    Write-Host "  or run 'build.ps1' first." -ForegroundColor Red
    Write-Host ""
    exit 1
}

Write-Host "  Found $($resolvedFiles.Count)/$($sourceFiles.Count) files:" -ForegroundColor Gray
foreach ($entry in $resolvedFiles.GetEnumerator()) {
    Write-Host "    $($entry.Key) -> $($entry.Value)" -ForegroundColor Gray
}

# --- Detect Revit versions ---

$addinsBase = Join-Path $env:APPDATA "Autodesk\Revit\Addins"
$revitVersions = @()

if (Test-Path $addinsBase) {
    Get-ChildItem $addinsBase -Directory | ForEach-Object {
        if ($_.Name -match '^\d{4}$') {
            $year = [int]$_.Name
            if ($year -ge 2022 -and $year -le 2030) {
                $revitVersions += $_.Name
            }
        }
    }
}

if ($revitVersions.Count -eq 0) {
    Write-Host "  No Revit installations detected." -ForegroundColor Yellow
    Write-Host "  Searching for: $addinsBase\{2022-2025}\" -ForegroundColor Gray
    Write-Host ""

    $manualYear = Read-Host "  Enter your Revit version year (e.g. 2023)"
    if ($manualYear -match '^\d{4}$') {
        $revitVersions = @($manualYear)
    } else {
        Write-Host "  Invalid input. Exiting." -ForegroundColor Red
        exit 1
    }
}

Write-Host "  Detected Revit versions: $($revitVersions -join ', ')" -ForegroundColor Green
Write-Host ""

# --- Copy files ---

foreach ($version in $revitVersions) {
    $targetDir = Join-Path $addinsBase $version

    if (-not (Test-Path $targetDir)) {
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    }

    Write-Host "  Installing to Revit $version..." -ForegroundColor White

    foreach ($file in $sourceFiles) {
        if ($resolvedFiles.ContainsKey($file)) {
            Copy-Item $resolvedFiles[$file] $targetDir -Force
            Write-Host "    Copied $file" -ForegroundColor Gray
        } else {
            Write-Host "    WARNING: $file not found, skipping" -ForegroundColor Yellow
        }
    }
}

Write-Host ""
Write-Host "  Files installed successfully!" -ForegroundColor Green

# --- API key ---

if (-not $SkipApiKey) {
    Write-Host ""

    $settingsDir = Join-Path $env:LOCALAPPDATA "VibeModel"
    $settingsPath = Join-Path $settingsDir "settings.json"

    # Check for existing key
    $existingKey = $null
    if (Test-Path $settingsPath) {
        try {
            $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
            if ($settings.AnthropicApiKey) {
                $existingKey = $settings.AnthropicApiKey
            }
        } catch { }
    }

    if ($existingKey) {
        $masked = $existingKey.Substring(0, [Math]::Min(10, $existingKey.Length)) + "..."
        Write-Host "  API key already configured: $masked" -ForegroundColor Green
        $changeKey = Read-Host "  Change it? (y/N)"
        if ($changeKey -ne 'y' -and $changeKey -ne 'Y') {
            Write-Host ""
            Write-Host "  Done! Open Revit to start using VibeModel." -ForegroundColor Cyan
            Write-Host ""
            exit 0
        }
    }

    Write-Host "  Enter your Anthropic API key (from console.anthropic.com):" -ForegroundColor White
    Write-Host "  (Press Enter to skip)" -ForegroundColor Gray
    $apiKey = Read-Host "  API Key"

    if ($apiKey) {
        if (-not (Test-Path $settingsDir)) {
            New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null
        }

        $settingsObj = @{ AnthropicApiKey = $apiKey; PreferredBackend = "direct"; Model = "claude-sonnet-4-5-20250929" }
        $settingsObj | ConvertTo-Json | Set-Content $settingsPath -Encoding UTF8

        Write-Host "  API key saved!" -ForegroundColor Green
    } else {
        Write-Host "  Skipped. You can configure it later via Settings in the chat pane." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "  Done! Open Revit to start using VibeModel." -ForegroundColor Cyan
Write-Host ""
