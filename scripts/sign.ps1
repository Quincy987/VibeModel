<#
.SYNOPSIS
    Authenticode-signs the files VibeModel ships: the add-in DLL, the PDF helper and the
    install / packaging scripts.

.DESCRIPTION
    Uses Set-AuthenticodeSignature (no Windows SDK / signtool needed) with an RFC 3161 timestamp,
    so signatures stay valid after the certificate expires.

    Certificate, first match wins:
      -Thumbprint  a code-signing certificate in Cert:\CurrentUser\My or Cert:\LocalMachine\My
      -PfxPath     a .pfx file (password asked, or -PfxPassword)
      neither      a SELF-SIGNED "VibeModel Dev (not for distribution)" certificate, created once
                   in Cert:\CurrentUser\My. Fine for testing on this machine; on any other computer
                   it is "not trusted" and Revit still asks whether to load the add-in. Real
                   distribution needs a certificate from a CA (ask IT).

.PARAMETER NoTimestamp
    Sign without an RFC 3161 timestamp. Without this switch an unreachable timestamp server is an
    ERROR (an untimestamped signature dies with the certificate; silently shipping one is worse
    than failing the build). Only for a local test build.

.PARAMETER Files
    The files to sign. Default: the add-in, the PDF helper and the scripts of a build/release
    folder given by -Root.

.EXAMPLE
    .\scripts\sign.ps1 -Files release\stage\VibeModel.dll
    .\scripts\sign.ps1 -Root release\VibeModel-v1.2.9-Revit2023 -Thumbprint 0123ABCD...
#>

param(
    [string[]]$Files,
    [string]$Root,
    [string]$Thumbprint,
    [string]$PfxPath,
    [SecureString]$PfxPassword,
    [string]$TimestampServer = "http://timestamp.digicert.com",
    [switch]$NoTimestamp
)

$ErrorActionPreference = 'Stop'
$DevSubject = "CN=VibeModel Dev (not for distribution)"

function Get-SigningCertificate {
    if ($Thumbprint) {
        $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert |
            Where-Object { $_.Thumbprint -eq $Thumbprint.Replace(" ", "").ToUpperInvariant() } | Select-Object -First 1
        if (-not $cert) { throw "No code-signing certificate with thumbprint $Thumbprint in CurrentUser\My or LocalMachine\My" }
        return @{ Cert = $cert; SelfSigned = $false }
    }
    if ($PfxPath) {
        if (-not $PfxPassword) { $PfxPassword = Read-Host "Password for $PfxPath" -AsSecureString }
        $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($PfxPath, $PfxPassword)
        return @{ Cert = $cert; SelfSigned = $false }
    }
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { $_.Subject -eq $DevSubject -and $_.NotAfter -gt (Get-Date).AddDays(7) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $DevSubject `
            -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature -KeyExportPolicy NonExportable `
            -NotAfter (Get-Date).AddYears(3)
        Write-Host "Created self-signed certificate $($cert.Thumbprint) ($DevSubject)" -ForegroundColor Yellow
    }
    return @{ Cert = $cert; SelfSigned = $true }
}

if (-not $Files) {
    if (-not $Root) { throw "Give -Files or -Root" }
    $Files = @(
        (Join-Path $Root "VibeModel.dll"),
        (Join-Path $Root "VibeModel.PdfText\VibeModel.PdfText.exe"),
        (Join-Path $Root "install.ps1")
    ) | Where-Object { Test-Path $_ }
}

$signing = Get-SigningCertificate
if ($signing.SelfSigned) {
    Write-Host ""
    Write-Host "  WARNING: signing with a SELF-SIGNED test certificate ($DevSubject)." -ForegroundColor Yellow
    Write-Host "  Other computers will report it as NOT TRUSTED. Use -Thumbprint or -PfxPath with a" -ForegroundColor Yellow
    Write-Host "  certificate from a certificate authority for anything you hand out." -ForegroundColor Yellow
    Write-Host ""
}

$failed = 0
foreach ($file in $Files) {
    if ($NoTimestamp) {
        $result = Set-AuthenticodeSignature -FilePath $file -Certificate $signing.Cert -HashAlgorithm SHA256
    } else {
        try {
            $result = Set-AuthenticodeSignature -FilePath $file -Certificate $signing.Cert -TimestampServer $TimestampServer -HashAlgorithm SHA256
        } catch {
            # A timestamp server that cannot be reached (offline, proxy) is a hard error: a signature
            # without a timestamp expires with the certificate. Pass -NoTimestamp to accept that knowingly.
            throw "Timestamping $(Split-Path $file -Leaf) at $TimestampServer failed: $($_.Exception.Message). Fix the connection (proxy?) or pass -NoTimestamp for a local test build."
        }
    }
    $check = Get-AuthenticodeSignature -FilePath $file
    $ok = $check.SignerCertificate -and $check.SignerCertificate.Thumbprint -eq $signing.Cert.Thumbprint
    # Set-AuthenticodeSignature can also return without throwing yet without a timestamp.
    if ($ok -and -not $NoTimestamp -and -not $check.TimeStamperCertificate) {
        throw "$(Split-Path $file -Leaf) was signed WITHOUT a timestamp (server $TimestampServer). Fix the connection or pass -NoTimestamp."
    }
    if (-not $ok) { $failed++ }
    Write-Host ("  {0,-28} {1}  ({2})" -f (Split-Path $file -Leaf), $(if ($ok) { "signed" } else { "NOT signed" }), $check.Status)
}

[pscustomobject]@{
    Signer     = $signing.Cert.Subject
    Thumbprint = $signing.Cert.Thumbprint
    SelfSigned = $signing.SelfSigned
    Failed     = $failed
}
if ($failed -gt 0) { exit 1 }
