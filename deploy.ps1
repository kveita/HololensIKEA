[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x86',
    [string]$ProjectPath,
    [string]$CertificatePath,
    [string]$CertificatePassword = 'temp',
    [string]$DeviceIp = '127.0.0.1'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $ProjectPath) {
    $ProjectPath = Join-Path $PSScriptRoot 'HololensIKEA.csproj'
}

if (-not $CertificatePath) {
    $CertificatePath = Join-Path $PSScriptRoot 'HololensIKEA_TemporaryKey.pfx'
}

function Get-MsBuildPath {
    $default = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'
    if (Test-Path $default) {
        return $default
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $path = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
        if ($path -and (Test-Path $path)) {
            return $path
        }

        $installPath = & $vswhere -latest -products * -property installationPath
        if ($installPath) {
            $candidate = Join-Path $installPath 'MSBuild\Current\Bin\MSBuild.exe'
            if (Test-Path $candidate) {
                return $candidate
            }
        }
    }

    throw 'MSBuild.exe was not found.'
}

function Get-WinAppDeployCmdPath {
    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $candidate = Get-ChildItem $sdkRoot -Recurse -Filter WinAppDeployCmd.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName |
        Select-Object -Last 1 -ExpandProperty FullName

    if ($candidate) {
        return $candidate
    }

    throw 'WinAppDeployCmd.exe was not found.'
}

function Get-LatestAppPackageFolder {
    $packageRoot = Join-Path $PSScriptRoot 'AppPackages'
    if (-not (Test-Path $packageRoot)) {
        throw "AppPackages folder not found: $packageRoot"
    }

    $folder = Get-ChildItem $packageRoot -Directory |
        Where-Object { $_.Name -like '*_x86_Test' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if (-not $folder) {
        throw "No x86 test package folder found under $packageRoot"
    }

    return $folder.FullName
}

function Uninstall-ExistingPackage {
    param(
        [string]$WadcPath,
        [string]$IpAddress,
        [string]$PackageNamePrefix
    )

    $installed = & $WadcPath list -ip $IpAddress 2>$null | Select-String $PackageNamePrefix
    foreach ($entry in $installed) {
        $package = $entry.Line.Trim()
        if ($package) {
            Write-Host "Uninstalling existing package: $package"
            & $WadcPath uninstall -package $package -ip $IpAddress | Out-Host
        }
    }
}

$msbuild = Get-MsBuildPath
$wadc = Get-WinAppDeployCmdPath
$packageFolder = Get-LatestAppPackageFolder

Write-Host "Building $ProjectPath ($Configuration|$Platform)..."
& $msbuild $ProjectPath `
    /t:Rebuild `
    "/p:Configuration=$Configuration" `
    "/p:Platform=$Platform" `
    "/p:AppxPackageSigningEnabled=true" `
    "/p:PackageCertificateKeyFile=$CertificatePath" `
    "/p:PackageCertificatePassword=$CertificatePassword" `
    /v:minimal

if ($LASTEXITCODE -ne 0) {
    throw "MSBuild failed with exit code $LASTEXITCODE"
}

$appx = Get-ChildItem $packageFolder -Filter *.appx |
    Where-Object { $_.Name -notlike '*Bundle*' } |
    Select-Object -First 1

if (-not $appx) {
    throw "No .appx file found in $packageFolder"
}

$dependencyFolder = Join-Path $packageFolder 'Dependencies\x86'
$dependencies = @(
    'Microsoft.NET.Native.Framework.1.3.appx',
    'Microsoft.NET.Native.Runtime.1.4.appx',
    'Microsoft.VCLibs.x86.14.00.appx'
) | ForEach-Object {
    $path = Join-Path $dependencyFolder $_
    if (Test-Path $path) { $path }
}

Write-Host "Checking for HoloLens at $DeviceIp..."
& $wadc devices | Out-Host

Uninstall-ExistingPackage -WadcPath $wadc -IpAddress $DeviceIp -PackageNamePrefix 'HololensIKEA_'

Write-Host "Installing $($appx.FullName)..."
$installArgs = @('install', '-f', $appx.FullName, '-ip', $DeviceIp)
foreach ($dependency in $dependencies) {
    $installArgs += @('-d', $dependency)
}

& $wadc @installArgs

if ($LASTEXITCODE -ne 0) {
    throw "Deploy failed with exit code $LASTEXITCODE"
}

Write-Host 'Done.'
