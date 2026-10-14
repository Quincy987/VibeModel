<#
.SYNOPSIS
    Installs the VibeModel add-in for Autodesk Revit - for the current Windows user, no
    administrator rights needed.

.DESCRIPTION
    Copies VibeModel.dll, VibeModel.addin, Markdig.dll and the VibeModel.PdfText helper to the
    add-in folder of every Revit version found under %APPDATA%\Autodesk\Revit\Addins, removes
    the "downloaded from the internet" block, and checks the result. Connecting to Claude is not
    done here: Revit's chat pane shows a "Connect to Claude" card the first time.

    Double-click install.cmd, or run this script with PowerShell. The window stays open at the
    end (and after an error) so the result can be read. Every line is also written to
    %LOCALAPPDATA%\VibeModel\logs\install.log. Running it again is safe: unchanged files are
    reported as up to date.

.PARAMETER NoPause
    Do not wait for Enter at the end (scripted use).

.PARAMETER Quiet
    For IT deployment: no questions, no pause, a running Revit is an error (exit 2) instead of
    a prompt. Output still goes to install.log.

.PARAMETER InstallClaudeCode
    Also install Claude Code with Anthropic's official per-user installer
    (irm https://claude.ai/install.ps1 | iex) and check `claude --version`. Optional: the chat
    works with an API key alone, and the chat pane can install Claude Code too.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -Quiet
    .\install.ps1 -InstallClaudeCode
#>

param(
    [switch]$NoPause,
    [switch]$Quiet,
    [switch]$InstallClaudeCode
)

$ErrorActionPreference = 'Stop'
if ($Quiet) { $NoPause = $true }

$logDir = Join-Path $env:LOCALAPPDATA "VibeModel\logs"
$logFile = Join-Path $logDir "install.log"
$script:installerVersion = "?"

function Log([string]$text) {
    try {
        if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
        Add-Content -Path $logFile -Value ("{0} [installer {1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $script:installerVersion, $text) -Encoding UTF8
    } catch { }
}

# Every user-facing line goes to the screen and to install.log.
function Say([string]$text, [string]$color = "Gray") {
    Write-Host "  $text" -ForegroundColor $color
    Log $text
}

function Finish([int]$code) {
    Log ("exit " + $code)
    if (-not $NoPause) {
        Write-Host ""
        Read-Host "  Press Enter to close this window" | Out-Null
    }
    exit $code
}

function Fail([string[]]$lines, [int]$code = 1) {
    Write-Host ""
    foreach ($line in $lines) { Say $line "Red" }
    Finish $code
}

try {

Write-Host ""
Write-Host "  VibeModel Installer" -ForegroundColor Cyan
Write-Host "  ===================" -ForegroundColor Cyan
Write-Host ""

# --- Locate source files ---

$scriptDir = $PSScriptRoot
$sourceFiles = @("VibeModel.dll", "VibeModel.addin", "Markdig.dll")

# Search directories - the release zip (next to this script), else build output dirs
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
        # GitHub's automatic "Source code (zip)" - no compiled add-in in it.
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

$script:installerVersion = (Get-Item $resolvedFiles["VibeModel.dll"]).VersionInfo.FileVersion
Log ("start in " + $scriptDir + " as " + $env:USERNAME + (@("", " (quiet)")[[int][bool]$Quiet]))
Say ("VibeModel " + $script:installerVersion) "White"

# --- Revit must be closed: it keeps VibeModel.dll locked, so the copy would fail ---

$revitProcs = @(Get-Process -Name "Revit" -ErrorAction SilentlyContinue)
if ($revitProcs.Count -gt 0) {
    if ($Quiet) { Fail @("Revit is running - close it and run the installer again.") 2 }
    Write-Host ""
    Say "Revit is running ($($revitProcs.Count) window(s)). Close Revit first - it keeps the add-in files locked." "Yellow"
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
    Say "No Revit add-in folder found under $addinsBase." "Yellow"
    if ($Quiet) { Fail @("No Revit version detected - start Revit once, or create $addinsBase\<year>.") 3 }
    Write-Host ""
    $manualYear = Read-Host "  Enter your Revit version year (e.g. 2023)"
    if ($manualYear -match '^\d{4}$') {
        $revitVersions = @($manualYear)
    } else {
        Fail @("Invalid input. Exiting.")
    }
}

Say "Revit versions: $($revitVersions -join ', ')" "Green"
Write-Host ""

# --- Copy files (unchanged files are left alone) ---

function Copy-IfChanged([string]$source, [string]$targetDir) {
    $target = Join-Path $targetDir (Split-Path $source -Leaf)
    if ((Test-Path $target) -and ((Get-FileHash $source).Hash -eq (Get-FileHash $target).Hash)) {
        Unblock-File -Path $target -ErrorAction SilentlyContinue
        return $false
    }
    Copy-Item $source $targetDir -Force
    # Remove Windows' "downloaded from the internet" block that stops .NET loading the DLL
    Unblock-File -Path $target -ErrorAction SilentlyContinue
    return $true
}

foreach ($version in $revitVersions) {
    $targetDir = Join-Path $addinsBase $version

    if (-not (Test-Path $targetDir)) {
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    }

    Say "Revit ${version}: $targetDir" "White"

    foreach ($file in $sourceFiles) {
        if ($resolvedFiles.ContainsKey($file)) {
            if (Copy-IfChanged $resolvedFiles[$file] $targetDir) {
                Say "  installed $file"
            } else {
                Say "  $file already up to date (v$($script:installerVersion))"
            }
        } else {
            Say "  WARNING: $file not found, skipped" "Yellow"
        }
    }

    # PDF text helper for the office-standards document index (own subfolder so its
    # dependencies never load into Revit). Without it only .txt sources can be indexed.
    $pdfTextSource = @(
        (Join-Path $scriptDir "VibeModel.PdfText"),
        (Join-Path $scriptDir "src\VibeModel.PdfText\bin\Release")
    ) | Where-Object { Test-Path (Join-Path $_ "VibeModel.PdfText.exe") } | Select-Object -First 1
    if ($pdfTextSource) {
        $pdfTextTarget = Join-Path $targetDir "VibeModel.PdfText"
        New-Item -ItemType Directory -Path $pdfTextTarget -Force | Out-Null
        $changed = 0
        Get-ChildItem $pdfTextSource -File | Where-Object { $_.Extension -in ".exe", ".dll", ".config" } | ForEach-Object {
            if (Copy-IfChanged $_.FullName $pdfTextTarget) { $changed++ }
        }
        if ($changed -gt 0) { Say "  installed the VibeModel.PdfText helper ($changed files)" }
        else { Say "  VibeModel.PdfText helper already up to date" }
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
Say "Files installed and unblocked." "Green"

# --- Signature: what Revit will see when it loads the add-in ---

$dllForSignature = Join-Path (Join-Path $addinsBase $revitVersions[0]) "VibeModel.dll"
$sig = Get-AuthenticodeSignature -FilePath $dllForSignature
# What Revit 2023 does (measured): trusted signature -> loads silently; unsigned -> asks once
# ("Always Load" remembers it); signed but untrusted -> "Invalid Signature" on every start.
switch ($sig.Status) {
    "Valid" { Say ("Signed by " + $sig.SignerCertificate.Subject + " (trusted) - Revit loads it without asking.") "Green" }
    "NotSigned" { Say "The add-in is not signed: the first time, Revit asks whether to load it - choose 'Always Load'." "Yellow" }
    default {
        $subject = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { "?" }
        Say ("Signed by " + $subject + ", but this computer does not trust that certificate (" + $sig.Status + ").") "Yellow"
        Say "Revit will warn 'Invalid Signature' on every start. Ask your administrator for the official build." "Yellow"
    }
}

# --- Optional: Claude Code (per user, no administrator rights) ---

if ($InstallClaudeCode) {
    Write-Host ""
    Say "Installing Claude Code (Anthropic's per-user installer)..." "White"
    try {
        Invoke-Expression (Invoke-RestMethod -Uri "https://claude.ai/install.ps1" -UseBasicParsing)
    } catch {
        Say ("Claude Code installer failed: " + $_.Exception.Message) "Red"
    }
    $claude = Join-Path $env:USERPROFILE ".local\bin\claude.exe"
    if (Test-Path $claude) {
        $claudeVersion = & $claude --version 2>$null
        Say ("Claude Code ready: " + $claudeVersion + " (" + $claude + ")") "Green"
        Say "Log in once: the chat pane's 'Log in' button, or run 'claude' in a terminal." "Gray"
    } else {
        Say "Claude Code was not found after the installer ran - see the messages above." "Red"
    }
}

Write-Host ""
Say "Done! Start Revit -> tab VibeModel -> Chat. The first time, the chat asks how to connect to Claude." "Cyan"
Finish 0

} catch {
    Fail @("Installation failed: $($_.Exception.Message)", "", "$($_.ScriptStackTrace)")
}
