<#
.SYNOPSIS
    Creates (or recreates) the development signing certificate for
    HololensSatelliteViewer_TemporaryKey.pfx.

.DESCRIPTION
    Run this script once from the repo root if the .pfx is missing or expired.
    The certificate is self-signed, carries no CA trust, and is used only for
    local sideloading onto a HoloLens in Developer Mode.

    The file is committed to git so a fresh clone works without running this
    script.  Only run it if you need to regenerate the cert.

.NOTES
    Password is intentionally simple ("temp") and matches
    <PackageCertificatePassword>temp</PackageCertificatePassword> in the .csproj.
    Change both here and in the .csproj if you want a different password.

.EXAMPLE
    # From the repo root (HololensSatelliteViewer\):
    powershell -ExecutionPolicy Bypass -File scripts\create_cert.ps1
#>

[CmdletBinding()]
param(
    [string]$OutputPath = "HololensSatelliteViewer_TemporaryKey.pfx",
    [string]$Password   = "temp"
)

$ErrorActionPreference = "Stop"

# Resolve path relative to the repo root (one level above this script)
$repoRoot = Split-Path $PSScriptRoot -Parent
$pfxPath  = Join-Path $repoRoot $OutputPath

Write-Host "Creating signing certificate..."

$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject "CN=HololensSatelliteViewer" `
    -KeyUsage DigitalSignature `
    -FriendlyName "HololensSatelliteViewer" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -TextExtension @(
        "2.5.29.37={text}1.3.6.1.5.5.7.3.3",   # Code signing EKU
        "2.5.29.19={text}"                        # Basic constraints: not a CA
    )

$secPwd = ConvertTo-SecureString -String $Password -Force -AsPlainText

Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $secPwd | Out-Null

Write-Host "Certificate written to: $pfxPath"
Write-Host "Thumbprint            : $($cert.Thumbprint)"
Write-Host "Expires               : $($cert.NotAfter)"
