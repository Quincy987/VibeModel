<#
.SYNOPSIS
    Builds the add-in in Release and packs it as the zip that goes on a GitHub release.

.DESCRIPTION
    Output in release\ (gitignored); all three go on the GitHub release:
      VibeModel-v<version>-Revit<year>.zip         the add-in
      VibeModel-v<version>-Revit<year>.zip.sha256  its checksum ("<hex>  <zip name>")
      latest-Revit<year>.json                      the in-app update manifest (plan 19): version,
                                                    zip, sha256, size, published, notes, minLoader
    The zip unpacks to a flat folder that install.cmd / install.ps1 install from - and that the
    in-app update unpacks into %LOCALAPPDATA%\VibeModel\versions\<version>\:
      VibeModel.Loader.dll, VibeModel.addin, VibeModel.dll, Markdig.dll, VibeModel.PdfText\,
      install.cmd, install.ps1, README.txt (three install steps), CHANGELOG.md, LICENSE.
    GitHub's own "Source code (zip)" has no DLL in it - this zip is what users download.

.PARAMETER RevitVersion
    Revit year to build against (the RevitAPI of that year must be installed). Default 2023.

.PARAMETER NoBuild
    Pack whatever is in src\VibeModel\bin\Release\Revit<year> without building first.

.PARAMETER Sign
    Authenticode-sign VibeModel.dll, the PDF helper and install.ps1 before zipping (scripts\sign.ps1)
    with the certificate given by -Thumbprint or -PfxPath.

.PARAMETER AllowSelfSigned
    Testing only: -Sign without a certificate uses a self-signed one. Never hand such a zip out:
    measured in Revit 2023, a self-signed (untrusted) signature makes Revit show "Security - Invalid
    Signature ... may have been tampered with ... we recommend that you do not load it" with only
    Load / Do Not Load on EVERY start - worse than an unsigned add-in, which asks once
    ("Always Load").

.EXAMPLE
    .\scripts\package-release.ps1
    .\scripts\package-release.ps1 -RevitVersion 2024
    .\scripts\package-release.ps1 -Sign -Thumbprint 0123ABCD...
#>

param(
    [string]$RevitVersion = "2023",
    [switch]$NoBuild,
    [switch]$Sign,
    [string]$Thumbprint,
    [string]$PfxPath,
    [switch]$AllowSelfSigned
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

if ($Sign -and -not $Thumbprint -and -not $PfxPath -and -not $AllowSelfSigned) {
    throw "-Sign needs -Thumbprint or -PfxPath (a certificate this office trusts). A self-signed signature makes Revit warn 'Invalid Signature' on every start - for a test build add -AllowSelfSigned."
}

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
$loaderDir = Join-Path $root "src\VibeModel.Loader\bin\Release\Revit$RevitVersion"
$pdfDir = Join-Path $root "src\VibeModel.PdfText\bin\Release"
foreach ($required in @("VibeModel.dll", "Markdig.dll")) {
    if (-not (Test-Path (Join-Path $binDir $required))) { throw "Missing $required in $binDir - build first" }
}
if (-not (Test-Path (Join-Path $pdfDir "VibeModel.PdfText.exe"))) { throw "Missing VibeModel.PdfText.exe in $pdfDir" }
if (-not (Test-Path (Join-Path $loaderDir "VibeModel.Loader.dll"))) { throw "Missing VibeModel.Loader.dll in $loaderDir - build first" }
# The loader has its own version (1.0.0); the manifest names the oldest loader this build runs under.
$loaderVersion = (Get-Item (Join-Path $loaderDir "VibeModel.Loader.dll")).VersionInfo.FileVersion
$minLoader = "1.0.0"

$releaseDir = Join-Path $root "release"
$stageName = "VibeModel-v$version-Revit$RevitVersion"
$stage = Join-Path $releaseDir $stageName
$zip = Join-Path $releaseDir "$stageName.zip"

$shaFile = "$zip.sha256"
$manifestFile = Join-Path $releaseDir "latest-Revit$RevitVersion.json"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
foreach ($old in @($zip, $shaFile, $manifestFile)) { if (Test-Path $old) { Remove-Item $old -Force } }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage "VibeModel.PdfText") -Force | Out-Null

Copy-Item (Join-Path $loaderDir "VibeModel.Loader.dll") $stage
Copy-Item (Join-Path $binDir "VibeModel.dll") $stage
Copy-Item (Join-Path $binDir "Markdig.dll") $stage
Copy-Item (Join-Path $root "VibeModel.addin") $stage
Copy-Item (Join-Path $root "install.ps1") $stage
Copy-Item (Join-Path $root "install.cmd") $stage
Copy-Item (Join-Path $root "CHANGELOG.md") $stage
# The installer installs only into this Revit year (a build loads only in the Revit it targets).
Set-Content (Join-Path $stage "revit-year.txt") $RevitVersion -Encoding ASCII
if (Test-Path (Join-Path $root "LICENSE")) { Copy-Item (Join-Path $root "LICENSE") $stage }
Get-ChildItem $pdfDir -File | Where-Object { $_.Extension -in ".exe", ".dll", ".config" } |
    Copy-Item -Destination (Join-Path $stage "VibeModel.PdfText")

$signedBy = "not signed"
if ($Sign) {
    $signArgs = @{ Root = $stage }
    if ($Thumbprint) { $signArgs.Thumbprint = $Thumbprint }
    if ($PfxPath) { $signArgs.PfxPath = $PfxPath }
    $signResult = & (Join-Path $PSScriptRoot "sign.ps1") @signArgs | Select-Object -Last 1
    if ($signResult.Failed -gt 0) { throw "Signing failed for $($signResult.Failed) file(s)" }
    $signedBy = $signResult.Signer + $(if ($signResult.SelfSigned) { " (SELF-SIGNED TEST BUILD - Revit warns 'Invalid Signature'; do not distribute)" } else { "" })
}

@"
VibeModel v$version - AI chat inside Autodesk Revit $RevitVersion
=================================================

Install (no administrator rights needed):
  1. Close Revit.
  2. Double-click install.cmd. It copies the add-in for your Windows account, unblocks the files
     and checks the result. Nothing to fill in.
  3. Start Revit -> tab VibeModel -> Chat. The first time, the chat shows "Connect to Claude":
     paste the API key you got from your administrator, or install / log in to Claude Code
     with one click. No terminal, no Node.js.

Updates: when a newer version is published the chat pane says "VibeModel <version> beschikbaar"
- click Bijwerken (Update); it is active after you restart Revit. No installer, no new security
question. Settings -> Updates checks now or goes back to the previous version.

Something not working? Click Diagnose in the chat pane, copy the report and send it to your
administrator. The installer writes %LOCALAPPDATA%\VibeModel\logs\install.log.

For IT: install.cmd -Quiet (no questions), managed.json for a shared key / model / proxy-friendly
gateway, details in docs/deployment.md on GitHub.

Signature: $signedBy
Docs, source and newer versions: https://github.com/Quincy987/VibeModel
"@ | Set-Content (Join-Path $stage "README.txt") -Encoding UTF8

Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force

# Checksum + the in-app update manifest (plan 19). The add-in refuses a download whose size or
# sha256 differs from the manifest, and the zip name must be exactly this one.
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$zipName = Split-Path $zip -Leaf
Set-Content $shaFile "$sha  $zipName" -Encoding ASCII
$manifest = [ordered]@{
    version   = $version
    revitYear = $RevitVersion
    zip       = $zipName
    sha256    = $sha
    size      = (Get-Item $zip).Length
    published = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd")
    notes     = "https://github.com/Quincy987/VibeModel/releases/tag/v$version"
    minLoader = $minLoader
}
# ASCII, no BOM: the add-in reads it as JSON.
[System.IO.File]::WriteAllText($manifestFile, ($manifest | ConvertTo-Json -Compress), [System.Text.Encoding]::ASCII)

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Packed $zip ($size MB)" -ForegroundColor Green
Write-Host "Checksum: $shaFile" -ForegroundColor Green
Write-Host "Update manifest: $manifestFile (loader $loaderVersion, minLoader $minLoader)" -ForegroundColor Green
Write-Host "Contents:" -ForegroundColor Gray
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::OpenRead($zip).Entries | ForEach-Object { Write-Host "  $($_.FullName)" -ForegroundColor Gray }
