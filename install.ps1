<#
.SYNOPSIS
    Installs the VibeModel add-in for Autodesk Revit - for the current Windows user, no
    administrator rights needed.

.DESCRIPTION
    Two parts, for the Revit version this build is for (revit-year.txt in the release zip):
    - the LOADER (VibeModel.Loader.dll + VibeModel.addin) goes to
      %APPDATA%\Autodesk\Revit\Addins\<year>. That is the file Revit asks about ("Always Load")
      and it does not change between versions;
    - the ADD-IN itself (VibeModel.dll, Markdig.dll, the VibeModel.PdfText helper) goes to its own
      version folder %LOCALAPPDATA%\VibeModel\versions\Revit<year>\<version>\, and
      versions\Revit<year>\current.txt names the version the loader starts.
    In-app updates (plan 19) add version folders the same way. A VibeModel.dll left in the add-ins
    folder by an older install is removed (it would be started instead of any update).
    Removes the "downloaded from the internet" mark and checks the result. Other Revit versions
    are named and left alone: a build only loads in the Revit it was compiled against.
    Connecting to Claude is not done here: Revit's chat pane shows a "Connect to Claude" card the
    first time.

    Revit has to be closed only when the loader or the .addin change (or old files must go); a new
    version of the add-in alone installs while Revit runs and is active after Revit restarts.

    Double-click install.cmd, or run this script with PowerShell. The window stays open at the
    end (and after an error) so the result can be read. Every line is also written to
    %LOCALAPPDATA%\VibeModel\logs\install.log. Running it again is safe: unchanged files are
    reported as up to date.

.PARAMETER NoPause
    Do not wait for Enter at the end (scripted use).

.PARAMETER Quiet
    For IT deployment: no questions, no pause, a running Revit that has to be closed is an error
    (exit 2) instead of a prompt, and so is a missing Revit for this build (exit 3). Output still
    goes to install.log.

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
# The loader + manifest go to Revit's add-ins folder; the add-in to its version folder.
$loaderFiles = @("VibeModel.Loader.dll", "VibeModel.addin")
$payloadFiles = @("VibeModel.dll", "Markdig.dll")
$sourceFiles = $loaderFiles + $payloadFiles

# Search directories - the release zip (next to this script), else build output dirs
$searchDirs = @($scriptDir)
foreach ($year in @("2023", "2024", "2022")) {
    $searchDirs += (Join-Path $scriptDir "bin\Release\Revit$year")
    $searchDirs += (Join-Path $scriptDir "src\VibeModel\bin\Release\Revit$year")
    $searchDirs += (Join-Path $scriptDir "src\VibeModel.Loader\bin\Release\Revit$year")
}
# Only Revit 2022-2024 can load this add-in (.NET Framework 4.8); Revit 2025+ runs on .NET 8.

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
foreach ($file in $loaderFiles) {
    if (-not $resolvedFiles.ContainsKey($file)) {
        Fail @("Cannot find $file next to this script.",
               "Unzip the whole release zip first, then run install.cmd from the unzipped folder.")
    }
}

$script:installerVersion = (Get-Item $resolvedFiles["VibeModel.dll"]).VersionInfo.FileVersion
# The version folder's name: "1.2.13" (a 4th part only when it is not 0) - as the in-app update names it.
$parsed = [version]$script:installerVersion
$versionName = if ($parsed.Revision -gt 0) { $parsed.ToString() } else { "{0}.{1}.{2}" -f $parsed.Major, $parsed.Minor, [Math]::Max($parsed.Build, 0) }
Log ("start in " + $scriptDir + " as " + $env:USERNAME + (@("", " (quiet)")[[int][bool]$Quiet]))
Say ("VibeModel " + $versionName) "White"

# --- Which Revit: the one this build is for ---

$addinsBase = Join-Path $env:APPDATA "Autodesk\Revit\Addins"
$detected = @()
if (Test-Path $addinsBase) {
    $detected = @(Get-ChildItem $addinsBase -Directory | Where-Object { $_.Name -match '^202[2-4]$' } | ForEach-Object { $_.Name })
}

# The release zip names its Revit year in revit-year.txt; a build folder in its path (bin\Release\Revit2023).
$buildYear = $null
$yearFile = Join-Path $scriptDir "revit-year.txt"
if (Test-Path $yearFile) { $buildYear = (Get-Content $yearFile -Raw).Trim() }
elseif ($resolvedFiles["VibeModel.dll"] -match 'Revit(20\d\d)') { $buildYear = $Matches[1] }

if ($buildYear) {
    $revitExe = Join-Path $env:ProgramFiles "Autodesk\Revit $buildYear\Revit.exe"
    if (-not ($detected -contains $buildYear) -and -not (Test-Path $revitExe)) {
        Fail @("This build is for Revit $buildYear, which is not installed for this user",
               "(no $addinsBase\$buildYear and no $revitExe).",
               "Download the zip for your Revit version.") 3
    }
    $revitVersions = @($buildYear)
    $others = @($detected | Where-Object { $_ -ne $buildYear })
    if ($others.Count -gt 0) {
        Say ("Also found Revit " + ($others -join ', ') + " - not touched: this build is for Revit $buildYear only.") "Yellow"
    }
} else {
    # A build folder without a year: the old behaviour, every detected version.
    $revitVersions = $detected
    if ($revitVersions.Count -eq 0) {
        Say "No Revit add-in folder found under $addinsBase." "Yellow"
        if ($Quiet) { Fail @("No Revit version detected - start Revit once, or create $addinsBase\<year>.") 3 }
        Write-Host ""
        $manualYear = Read-Host "  Enter your Revit version year (2022, 2023 or 2024)"
        if ($manualYear -match '^202[2-4]$') {
            $revitVersions = @($manualYear)
        } else {
            Fail @("Invalid input - this add-in loads only in Revit 2022, 2023 or 2024. Exiting.")
        }
    }
}

function VersionsRoot([string]$year) { Join-Path $env:LOCALAPPDATA "VibeModel\versions\Revit$year" }

function SameFile([string]$a, [string]$b) {
    (Test-Path $b) -and ((Get-FileHash $a).Hash -eq (Get-FileHash $b).Hash)
}

# Files from installs before the loader, in the add-ins folder: VibeModel.dll there would be
# started instead of the installed version (the loader's rule for a developer build).
$legacyNames = @("VibeModel.dll", "Markdig.dll", "VibeModel.PdfText")

# --- Revit must be closed only when something it holds open changes ---
# Revit keeps the loader (and, from older installs, VibeModel.dll in the add-ins folder) locked.

$needsClosedRevit = @()
foreach ($version in $revitVersions) {
    $targetDir = Join-Path $addinsBase $version
    foreach ($file in $loaderFiles) {
        if (-not (SameFile $resolvedFiles[$file] (Join-Path $targetDir $file))) { $needsClosedRevit += "$file (Revit $version)" }
    }
    foreach ($name in $legacyNames) {
        if (Test-Path (Join-Path $targetDir $name)) { $needsClosedRevit += "old $name in the add-ins folder (Revit $version)" }
    }
    # The same version again with different files (a rebuilt test version): its folder may be in use.
    $installedDll = Join-Path (Join-Path (VersionsRoot $version) $versionName) "VibeModel.dll"
    if ((Test-Path $installedDll) -and -not (SameFile $resolvedFiles["VibeModel.dll"] $installedDll)) {
        $needsClosedRevit += "VibeModel $versionName itself (rebuilt, Revit $version)"
    }
}

$revitProcs = @(Get-Process -Name "Revit" -ErrorAction SilentlyContinue)
$revitWasRunning = $revitProcs.Count -gt 0
if ($revitWasRunning -and $needsClosedRevit.Count -gt 0) {
    Log ("Revit must be closed for: " + ($needsClosedRevit -join "; "))
    if ($Quiet) { Fail @("Revit is running - close it and run the installer again.") 2 }
    Write-Host ""
    Say "Revit is running ($($revitProcs.Count) window(s)). Close Revit first - it keeps the add-in files locked." "Yellow"
    Read-Host "  Close Revit, then press Enter here to continue" | Out-Null
    $revitProcs = @(Get-Process -Name "Revit" -ErrorAction SilentlyContinue)
    if ($revitProcs.Count -gt 0) {
        Fail @("Revit is still running. Close it and run the installer again.")
    }
    $revitWasRunning = $false
}

Say "Installing for Revit $($revitVersions -join ', ')" "Green"
Write-Host ""

# --- Copy files (unchanged files are left alone) ---

function Copy-IfChanged([string]$source, [string]$targetDir) {
    $target = Join-Path $targetDir (Split-Path $source -Leaf)
    if (SameFile $source $target) {
        Unblock-File -Path $target -ErrorAction SilentlyContinue
        return $false
    }
    Copy-Item $source $targetDir -Force
    # Remove Windows' "downloaded from the internet" block that stops .NET loading the DLL
    Unblock-File -Path $target -ErrorAction SilentlyContinue
    return $true
}

# PDF text helper for the office-standards document index (own subfolder so its dependencies
# never load into Revit). Without it only .txt sources can be indexed.
$pdfTextSource = @(
    (Join-Path $scriptDir "VibeModel.PdfText"),
    (Join-Path $scriptDir "src\VibeModel.PdfText\bin\Release")
) | Where-Object { Test-Path (Join-Path $_ "VibeModel.PdfText.exe") } | Select-Object -First 1

foreach ($version in $revitVersions) {
    $targetDir = Join-Path $addinsBase $version
    $versionsRoot = VersionsRoot $version
    $payloadDir = Join-Path $versionsRoot $versionName
    foreach ($dir in @($targetDir, $payloadDir)) {
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    }

    Say "Revit ${version}: loader in $targetDir" "White"
    foreach ($file in $loaderFiles) {
        if (Copy-IfChanged $resolvedFiles[$file] $targetDir) { Say "  installed $file" }
        else { Say "  $file already up to date" }
    }

    foreach ($name in $legacyNames) {
        $old = Join-Path $targetDir $name
        if (Test-Path $old) {
            Remove-Item $old -Recurse -Force
            Say "  removed the old $name from the add-ins folder (the loader now starts the installed version)"
        }
    }

    Say "Revit ${version}: VibeModel $versionName in $payloadDir" "White"
    foreach ($file in $payloadFiles) {
        if ($resolvedFiles.ContainsKey($file)) {
            if (Copy-IfChanged $resolvedFiles[$file] $payloadDir) { Say "  installed $file" }
            else { Say "  $file already up to date (v$versionName)" }
        } else {
            Say "  WARNING: $file not found, skipped" "Yellow"
        }
    }
    if ($pdfTextSource) {
        $pdfTextTarget = Join-Path $payloadDir "VibeModel.PdfText"
        New-Item -ItemType Directory -Path $pdfTextTarget -Force | Out-Null
        $changed = 0
        Get-ChildItem $pdfTextSource -File | Where-Object { $_.Extension -in ".exe", ".dll", ".config" } | ForEach-Object {
            if (Copy-IfChanged $_.FullName $pdfTextTarget) { $changed++ }
        }
        if ($changed -gt 0) { Say "  installed the VibeModel.PdfText helper ($changed files)" }
        else { Say "  VibeModel.PdfText helper already up to date" }
    }

    # The version the loader starts next time (ASCII, one line - what the in-app update writes too).
    Set-Content -Path (Join-Path $versionsRoot "current.txt") -Value $versionName -Encoding ASCII
    Say "  current.txt -> $versionName"
}

# --- Verify: every installed file must be there and unblocked ---

$problems = @()
foreach ($version in $revitVersions) {
    $checks = @()
    foreach ($file in $loaderFiles) { $checks += (Join-Path (Join-Path $addinsBase $version) $file) }
    foreach ($file in $payloadFiles) { $checks += (Join-Path (Join-Path (VersionsRoot $version) $versionName) $file) }
    foreach ($installed in $checks) {
        if (-not (Test-Path $installed)) {
            $problems += "$installed is missing"
        } elseif (Get-Item $installed -Stream Zone.Identifier -ErrorAction SilentlyContinue) {
            $problems += "$installed is still blocked by Windows (right-click > Properties > Unblock)"
        }
    }
    if (Test-Path (Join-Path (Join-Path $addinsBase $version) "VibeModel.dll")) {
        $problems += "an old VibeModel.dll is still in $(Join-Path $addinsBase $version) - it would be started instead"
    }
}
if ($problems.Count -gt 0) {
    Fail (@("Installed, but with problems:") + $problems)
}

Write-Host ""
Say "Files installed and unblocked." "Green"

# --- Signature: what Revit will see when it loads the add-in (the loader) ---

$dllForSignature = Join-Path (Join-Path $addinsBase $revitVersions[0]) "VibeModel.Loader.dll"
$sig = Get-AuthenticodeSignature -FilePath $dllForSignature
# What Revit 2023 does (measured): trusted signature -> loads silently; unsigned -> asks once
# ("Always Load" remembers it); signed but untrusted -> "Invalid Signature" on every start.
switch ($sig.Status) {
    "Valid" { Say ("Signed by " + $sig.SignerCertificate.Subject + " (trusted) - Revit loads it without asking.") "Green" }
    "NotSigned" { Say "The loader is not signed: Revit asks once whether to load it - choose 'Always Load'. In-app updates do not change the loader, so it does not ask again after them." "Yellow" }
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
    # In its own PowerShell: an `exit` (or a changed error preference) in Anthropic's script must
    # not end or alter this installer.
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "irm https://claude.ai/install.ps1 | iex"
    if ($LASTEXITCODE -ne 0) { Say ("Claude Code installer exited with code " + $LASTEXITCODE) "Red" }
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
if ($revitWasRunning) {
    Say "Done! Revit is still running: VibeModel $versionName is active after you restart Revit." "Cyan"
} else {
    Say "Done! Start Revit -> tab VibeModel -> Chat. The first time, the chat asks how to connect to Claude." "Cyan"
}
Finish 0

} catch {
    Fail @("Installation failed: $($_.Exception.Message)", "", "$($_.ScriptStackTrace)")
}
