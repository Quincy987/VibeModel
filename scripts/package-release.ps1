<#
.SYNOPSIS
    Builds the add-in in Release and packs it as the zip that goes on a GitHub release.

.DESCRIPTION
    Output: release\VibeModel-v<version>-Revit<year>.zip (the release folder is gitignored).
    The zip unpacks to a flat folder that install.cmd / install.ps1 install from:
      VibeModel.dll, Markdig.dll, VibeModel.addin, VibeModel.PdfText\, install.cmd, install.ps1,
      README.txt (three install steps), CHANGELOG.md, LICENSE.
    GitHub's own "Source code (zip)" has no DLL in it - this zip is what users download.

.PARAMETER RevitVersion
    Revit year to build against (the RevitAPI of that year must be installed). Default 2023.

.PARAMETER NoBuild
    Pack whatever is in src\VibeModel\bin\Release\Revit<year> without building first.

.EXAMPLE
    .\scripts\package-release.ps1
    .\scripts\package-release.ps1 -RevitVersion 2024
#>

param(
    [string]$RevitVersion = "2023",
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$csproj = Join-Path $root "src\VibeModel\VibeModel.csproj"
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $csproj" }

if (-not $NoBuild) {
    Write-Host "Building VibeModel $version for Revit $RevitVersion (Release)..." -ForegroundColor Cyan
    # Plain dotnet build never deploys into the live Addins folder (Deploy is opt-in).
    dotnet build $csproj -c Release -p:RevitVersion=$RevitVersion -m:2 -nologo -v:m
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
}

$binDir = Join-Path $root "src\VibeModel\bin\Release\Revit$RevitVersion"
$pdfDir = Join-Path $root "src\VibeModel.PdfText\bin\Release"
foreach ($required in @("VibeModel.dll", "Markdig.dll")) {
    if (-not (Test-Path (Join-Path $binDir $required))) { throw "Missing $required in $binDir - build first" }
}
if (-not (Test-Path (Join-Path $pdfDir "VibeModel.PdfText.exe"))) { throw "Missing VibeModel.PdfText.exe in $pdfDir" }

$releaseDir = Join-Path $root "release"
$stageName = "VibeModel-v$version-Revit$RevitVersion"
$stage = Join-Path $releaseDir $stageName
$zip = Join-Path $releaseDir "$stageName.zip"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage "VibeModel.PdfText") -Force | Out-Null

Copy-Item (Join-Path $binDir "VibeModel.dll") $stage
Copy-Item (Join-Path $binDir "Markdig.dll") $stage
Copy-Item (Join-Path $root "VibeModel.addin") $stage
Copy-Item (Join-Path $root "install.ps1") $stage
Copy-Item (Join-Path $root "install.cmd") $stage
Copy-Item (Join-Path $root "CHANGELOG.md") $stage
if (Test-Path (Join-Path $root "LICENSE")) { Copy-Item (Join-Path $root "LICENSE") $stage }
Get-ChildItem $pdfDir -File | Where-Object { $_.Extension -in ".exe", ".dll", ".config" } |
    Copy-Item -Destination (Join-Path $stage "VibeModel.PdfText")

@"
VibeModel v$version - AI chat inside Autodesk Revit $RevitVersion
=================================================

Install (3 steps):
  1. Close Revit.
  2. Double-click install.cmd. It copies the add-in to %APPDATA%\Autodesk\Revit\Addins\<year>\ and unblocks the files.
     (Press Enter at the API-key question when you use Claude Code.)
  3. Start Revit. The VibeModel tab is in the ribbon; click Chat.

Connect to Claude:
  - Claude Code (recommended): install Node.js, run  npm install -g @anthropic-ai/claude-code , run  claude  once and log in.
    VibeModel detects it automatically.
  - Or an Anthropic API key: Settings in the chat pane.

No VibeModel tab? Every file must be in the Addins folder of YOUR Revit year and the DLLs must be unblocked;
install.cmd checks both and tells you. Run it again after closing Revit.

Docs, source and newer versions: https://github.com/Quincy987/VibeModel
"@ | Set-Content (Join-Path $stage "README.txt") -Encoding UTF8

Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Packed $zip ($size MB)" -ForegroundColor Green
Write-Host "Contents:" -ForegroundColor Gray
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::OpenRead($zip).Entries | ForEach-Object { Write-Host "  $($_.FullName)" -ForegroundColor Gray }
