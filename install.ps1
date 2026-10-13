<#
.SYNOPSIS
    Installs VibeModel add-in for Autodesk Revit.

.DESCRIPTION
    Copies VibeModel.dll, VibeModel.addin, Markdig.dll and the VibeModel.PdfText helper
    to every detected Revit add-in folder, unblocks them, and optionally saves your
    Anthropic API key. Double-click install.cmd, or run this script with PowerShell.

    The window stays open at the end (and after an error) so the result can be read.

.PARAMETER SkipApiKey
    Skip the API key prompt.

.PARAMETER NoPause
    Do not wait for Enter at the end (for scripted use).

.EXAMPLE
    .\install.ps1
    .\install.ps1 -SkipApiKey -NoPause
#>

param(
    [switch]$SkipApiKey,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'

function Finish([int]$code) {
    if (-not $NoPause) {
        Write-Host ""
        Read-Host "  Press Enter to close this window" | Out-Null
    }
    exit $code
}

function Fail([string[]]$lines) {
    Write-Host ""
    foreach ($line in $lines) { Write-Host "  $line" -ForegroundColor Red }
    Finish 1
}

try {

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
    if (Test-Path (Join-Path $scriptDir "src\VibeModel\VibeModel.csproj")) {
        # GitHub's automatic "Source code (zip)" — no compiled add-in in it.
        Fail @(
            "This folder is the SOURCE CODE of VibeModel, not the add-in.",
            "The add-in (VibeModel.dll) is not in it.",
            "",
            "Download the file named  VibeModel-v<version>-Revit<year>.zip  under 'Assets' at",
            "https://github.com/Quincy987/VibeModel/releases/latest",
            "(not 'Source code (zip)'), unzip it and run install.cmd from there.",
            "",
            "Or build it yourself: run build.ps1 in this folder."
        )
    }
    Fail @(
        "Cannot find VibeModel.dll next to this script.",
        "Unzip the whole release zip first, then run install.cmd from the unzipped folder."
    )
}

Write-Host "  Found $($resolvedFiles.Count)/$($sourceFiles.Count) files:" -ForegroundColor Gray
foreach ($entry in $resolvedFiles.GetEnumerator()) {
    Write-Host "    $($entry.Key) -> $($entry.Value)" -ForegroundColor Gray
}

# --- Revit must be closed: it keeps VibeModel.dll locked, so the copy would fail ---

$revitProcs = @(Get-Process -Name "Revit" -ErrorAction SilentlyContinue)
if ($revitProcs.Count -gt 0) {
    Write-Host ""
    Write-Host "  Revit is running ($($revitProcs.Count) window(s)). Close Revit first - it keeps the add-in files locked." -ForegroundColor Yellow
    Read-Host "  Close Revit, then press Enter here to continue" | Out-Null
    $revitProcs = @(Get-Process -Name "Revit" -ErrorAction SilentlyContinue)
    if ($revitProcs.Count -gt 0) {
        Fail @("Revit is still running. Close it and run the installer again.")
    }
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
        Fail @("Invalid input. Exiting.")
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
            # Remove Windows "downloaded from internet" block that prevents .NET from loading the DLL
            Unblock-File -Path (Join-Path $targetDir $file) -ErrorAction SilentlyContinue
            Write-Host "    Copied $file" -ForegroundColor Gray
        } else {
            Write-Host "    WARNING: $file not found, skipping" -ForegroundColor Yellow
        }
    }

    # Optional: PDF text helper for the office-standards document index (own subfolder so its
    # dependencies never load into Revit). Without it only .txt sources can be indexed.
    $pdfTextSource = @(
        (Join-Path $scriptDir "VibeModel.PdfText"),
        (Join-Path $scriptDir "src\VibeModel.PdfText\bin\Release")
    ) | Where-Object { Test-Path (Join-Path $_ "VibeModel.PdfText.exe") } | Select-Object -First 1
    if ($pdfTextSource) {
        $pdfTextTarget = Join-Path $targetDir "VibeModel.PdfText"
        New-Item -ItemType Directory -Path $pdfTextTarget -Force | Out-Null
        Get-ChildItem $pdfTextSource -File | Where-Object { $_.Extension -in ".exe", ".dll", ".config" } | ForEach-Object {
            Copy-Item $_.FullName $pdfTextTarget -Force
            Unblock-File -Path (Join-Path $pdfTextTarget $_.Name) -ErrorAction SilentlyContinue
        }
        Write-Host "    Copied VibeModel.PdfText helper" -ForegroundColor Gray
    }
}

# --- Verify: every installed file must be there and unblocked ---

$problems = @()
foreach ($version in $revitVersions) {
    $targetDir = Join-Path $addinsBase $version
    foreach ($file in $sourceFiles) {
        $installed = Join-Path $targetDir $file
        if (-not (Test-Path $installed)) {
            $problems += "$installed is missing"
        } elseif (Get-Item $installed -Stream Zone.Identifier -ErrorAction SilentlyContinue) {
            $problems += "$installed is still blocked by Windows (right-click > Properties > Unblock)"
        }
    }
}
if ($problems.Count -gt 0) {
    Fail (@("Installed, but with problems:") + $problems)
}

Write-Host ""
Write-Host "  Files installed and unblocked successfully!" -ForegroundColor Green

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

    $askKey = $true
    if ($existingKey) {
        $masked = $existingKey.Substring(0, [Math]::Min(10, $existingKey.Length)) + "..."
        Write-Host "  API key already configured: $masked" -ForegroundColor Green
        $changeKey = Read-Host "  Change it? (y/N)"
        if ($changeKey -ne 'y' -and $changeKey -ne 'Y') { $askKey = $false }
    }

    if ($askKey) {
        Write-Host "  Using Claude Code (logged in with 'claude')? Just press Enter." -ForegroundColor White
        Write-Host "  Otherwise enter your Anthropic API key (from console.anthropic.com):" -ForegroundColor White
        $apiKey = Read-Host "  API Key (Enter to skip)"

        if ($apiKey) {
            if (-not (Test-Path $settingsDir)) {
                New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null
            }

            # Keep whatever else is in settings.json; only the key and backend change. The model
            # is left to the add-in's default.
            $settingsObj = @{}
            if (Test-Path $settingsPath) {
                try {
                    (Get-Content $settingsPath -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $settingsObj[$_.Name] = $_.Value }
                } catch { }
            }
            $settingsObj["AnthropicApiKey"] = $apiKey
            $settingsObj["PreferredBackend"] = "direct"
            $settingsObj | ConvertTo-Json | Set-Content $settingsPath -Encoding UTF8

            Write-Host "  API key saved!" -ForegroundColor Green
        } else {
            Write-Host "  Skipped. Claude Code is detected automatically; an API key can be set later via Settings in the chat pane." -ForegroundColor Gray
        }
    }
}

Write-Host ""
Write-Host "  Done! Start Revit - the VibeModel tab is in the ribbon." -ForegroundColor Cyan
Finish 0

} catch {
    Fail @("Installation failed: $($_.Exception.Message)", "", "$($_.ScriptStackTrace)")
}
