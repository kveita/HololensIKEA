# HoloLensHello

[![UWP Build](https://github.com/turbolego/HoloLensHello/actions/workflows/dotnet.yml/badge.svg)](https://github.com/turbolego/HoloLensHello/actions/workflows/dotnet.yml)
[![UWP Package](https://github.com/turbolego/HoloLensHello/actions/workflows/dotnet-desktop.yml/badge.svg)](https://github.com/turbolego/HoloLensHello/actions/workflows/dotnet-desktop.yml)
![Platform x86](https://img.shields.io/badge/platform-x86-blue)
![SDK 10.0.19041](https://img.shields.io/badge/Windows%20SDK-10.0.19041-blue)
![HoloLens 1](https://img.shields.io/badge/HoloLens-1st%20gen-blueviolet)

A **Hello World** UWP app for **Microsoft HoloLens 1** built with plain
MSBuild � no Visual Studio IDE required at run time.  The app renders a
software-rasterised 3D rotating cube with per-face Lambert lighting,
depth-sorted with the painter's algorithm and animated at ~60 fps via
`DispatcherTimer`.

Because black pixels are transparent on HoloLens's see-through display, the
cube appears to **float in the real world**.

---

## Contents

- [What You Will See](#what-you-will-see)
- [Project Structure](#project-structure)
- [Prerequisites](#prerequisites)
- [Step 1 � Install Visual Studio 2022](#step-1--install-visual-studio-2022)
- [Step 2 � Clone the Repository](#step-2--clone-the-repository)
- [Step 3 � Build](#step-3--build)
- [Step 4 � Set Up HoloLens for Development](#step-4--set-up-hololens-for-development)
- [Step 5 � Deploy via USB](#step-5--deploy-via-usb)
- [Updating and Redeploying](#updating-and-redeploying)
- [Troubleshooting](#troubleshooting)
- [How It Works](#how-it-works)
- [CI/CD](#cicd)
- [Reference: Key Paths](#reference-key-paths)

---

## What You Will See

When the app launches on HoloLens 1 a colour-shaded rotating cube appears
floating in space.  Each of the six faces has a distinct hue
(red � green � blue � yellow � magenta � cyan) brightened or darkened by a
single directional light.  The cube rotates around its Y-axis with a gentle
sinusoidal X tilt.

---

## Project Structure

```
HoloLensHello/
??? .github/
?   ??? workflows/
?       ??? dotnet.yml            # CI compile check (Debug + Release, no cert)
?       ??? dotnet-desktop.yml    # Release .appx build + artifact upload
??? Assets/                       # PNG logos/splash at the exact required sizes
??? Properties/
?   ??? AssemblyInfo.cs
??? scripts/
?   ??? create_cert.ps1           # Regenerate HoloLensHello_TemporaryKey.pfx
?   ??? make_assets.py            # Generate placeholder PNGs from scratch
?   ??? regenerate_refs.py        # Rebuild csproj HintPaths for a non-default VS path
??? App.xaml / App.xaml.cs
??? Directory.Build.targets       # Diagnostic: logs refs seen by XAML compiler
??? HoloLensHello.csproj          # UWP project � .NETCore 5.0, x86
??? HoloLensHello_TemporaryKey.pfx # Dev signing cert (self-signed, committed)
??? MainPage.xaml                 # Black canvas + bottom label
??? MainPage.xaml.cs              # Software 3D renderer (~160 lines)
??? Package.appxmanifest          # Identity, capabilities, logos
```

---

## Prerequisites

| Requirement | Version / Notes |
|---|---|
| Windows | 10 or 11 (64-bit) |
| Visual Studio 2022 | Community (free) or higher � **install the UWP workload** |
| Windows 10 SDK | **10.0.19041.0** with WinMD contracts (included in UWP workload) |
| Python 3 | 3.6+ � only needed if HintPaths need regenerating |
| HoloLens 1 | Developer Mode enabled (see Step 4) |
| Cable | Micro-USB to USB-A |

---

## Step 1 � Install Visual Studio 2022

### Option A: VS Installer GUI (recommended)

1. Download **Visual Studio 2022 Community** (free) from
   <https://visualstudio.microsoft.com/vs/community/>.
2. In the **Workloads** tab select **Universal Windows Platform development**.
3. In the right-hand detail panel confirm **Windows 10 SDK (10.0.19041.0)**
   is ticked.
4. Click **Install** (~10�20 min).

### Option B: Fully unattended command line

Run in an **elevated** PowerShell prompt:

```powershell
# Download bootstrapper
Invoke-WebRequest -Uri "https://aka.ms/vs/17/release/vs_community.exe" `
                  -OutFile "$env:TEMP\vs_community.exe"

# Install VS with the UWP workload + SDK 19041
& "$env:TEMP\vs_community.exe" install --quiet --norestart `
    --add Microsoft.VisualStudio.Workload.Universal `
    --add Microsoft.VisualStudio.Component.Windows10SDK.19041 `
    --includeRecommended

# Wait for the background install to complete
while (Get-Process vs_community -ErrorAction SilentlyContinue) { Start-Sleep 5 }
Write-Host "Done."
```

### Option C: Add missing components to an existing VS 2022

If VS is already installed but `WMC1006` errors appear at build time, the
WinMD contracts or XAML targets are missing.  Add them:

```powershell
$vswhere   = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath    = & $vswhere -latest -products * -property installationPath
$installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vs_installer.exe"

& $installer modify `
    --installPath "$vsPath" `
    --add Microsoft.VisualStudio.Component.Windows10SDK.19041 `
    --add Microsoft.VisualStudio.ComponentGroup.UWP.MSBuild `
    --quiet --norestart --force

# 0 = success; 3010 = success + restart deferred (fine)
if ($LASTEXITCODE -notin @(0, 3010)) { throw "Installer exited $LASTEXITCODE" }
```

### Verify the installation

Every line should print `True`:

```powershell
# XAML compiler MSBuild targets
Test-Path "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Microsoft\WindowsXaml\v17.0\8.2\Microsoft.Windows.UI.Xaml.CSharp.targets"

# Windows 10 SDK WinMD contracts (folder must be non-empty)
(Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\References\10.0.19041.0" -Force |
    Measure-Object).Count -ge 80   # expect 88+

# UWP managed reference assemblies
Test-Path "C:\Program Files (x86)\Microsoft SDKs\UWPNuGetPackages\system.runtime\4.1.0\ref\netcore50\System.Runtime.dll"

# Framework lock file used by scripts/regenerate_refs.py
Test-Path "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETCore\v5.0\project.lock.json"
```

---

## Step 2 � Clone the Repository

```powershell
git clone https://github.com/turbolego/HoloLensHello.git
cd HoloLensHello
```

The repo already contains:

- **All source code** and project files.
- **All required asset PNGs** at the correct dimensions.
- **`HoloLensHello_TemporaryKey.pfx`** � the development signing certificate.
  It is self-signed, has no CA trust, and is used only for local sideloading.
  Password: `temp` (matches `<PackageCertificatePassword>` in the `.csproj`).

> **Non-default VS path?**
> The `.csproj` contains 103 `<Reference>` HintPaths pointing into
> `C:\Program Files (x86)\Microsoft SDKs\UWPNuGetPackages\`.
> If VS is on a different drive or path, regenerate them:
>
> ```powershell
> python scripts\regenerate_refs.py
> ```

---

## Step 3 � Build

Use the MSBuild that ships with VS 2022.  The cross-platform `dotnet build`
CLI cannot resolve the Windows XAML targets.

```powershell
$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
```

### Debug build � compile check only (no `.appx`)

```powershell
& $msbuild HoloLensHello.csproj `
    /p:Configuration=Debug `
    /p:Platform=x86 `
    /p:AppxPackageSigningEnabled=false `
    /p:GenerateAppxPackageOnBuild=false `
    /v:minimal
```

### Release build � signed `.appx` ready to deploy

```powershell
& $msbuild HoloLensHello.csproj `
    /p:Configuration=Release `
    /p:Platform=x86 `
    /v:minimal
```

A successful build ends with:

```
HoloLensHello -> ...\bin\x86\Release\HoloLensHello.exe
HoloLensHello -> ...\AppPackages\HoloLensHello_1.0.1.0_x86_Test\HoloLensHello_1.0.1.0_x86.appx
```

The `AppPackages\` folder contains everything needed to deploy:

```
AppPackages\HoloLensHello_1.0.1.0_x86_Test\
??? HoloLensHello_1.0.1.0_x86.appx          ? main package
??? HoloLensHello_1.0.1.0_x86.cer           ? public cert for device trust
??? Dependencies\x86\
    ??? Microsoft.NET.CoreRuntime.1.1.appx
    ??? Microsoft.VCLibs.x86.14.00.appx
```

---

## Step 4 � Set Up HoloLens for Development

### Enable Developer Mode

1. Put on the HoloLens and open the **Start menu** (bloom gesture).
2. Go to **Settings ? Update & Security ? For developers**.
3. Toggle **Use developer features** ? **On**.
4. Toggle **Enable Device Portal** ? **On**.

### Connect via USB

Connect the HoloLens to the PC with a **Micro-USB to USB-A** cable.
Windows automatically installs a **Remote NDIS (RNDIS)** driver that tunnels
TCP/IP over USB; the device becomes reachable at `127.0.0.1`.

> **RNDIS not installing?**
> Open Device Manager.  Under **Network adapters** you should see
> *Remote NDIS based Internet Sharing Device*.
> If it shows an error: right-click ? **Update driver ? Search automatically**.

---

## Step 5 � Deploy via USB

`WinAppDeployCmd.exe` ships with every Windows 10 SDK installation.

```powershell
# Locate the tool (path includes the installed SDK version)
$wadc = (
    Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" `
        -Recurse -Filter "WinAppDeployCmd.exe" |
    Sort-Object FullName |
    Select-Object -Last 1
).FullName
```

### Discover the connected HoloLens

```powershell
& $wadc devices
```

Expected output:

```
Discovering devices...
IP Address    GUID                                  Model/Name
127.0.0.1     33cc9230-c8d0-45bd-119f-54ea5fbd3d97  Microsoft HoloLens
Done.
```

If nothing appears: unplug, replug, and retry.

> **First-time pairing:** The HoloLens shows a 6-digit PIN.
> Pass it with `-pin <code>` on the `install` command below.

### Install the app

```powershell
$pkg = "AppPackages\HololensIKEA_1.0.0.0_x86_Test"

& $wadc install `
    -f  "$pkg\HololensIKEA_1.0.0.0_x86.appx" `
    -ip 127.0.0.1 `
    -d  "$pkg\Dependencies\x86\Microsoft.NET.Native.Framework.1.3.appx" `
    -d  "$pkg\Dependencies\x86\Microsoft.NET.Native.Runtime.1.4.appx" `
    -d  "$pkg\Dependencies\x86\Microsoft.VCLibs.x86.14.00.appx"
```

Expected output:

```
Opening connection to device at '127.0.0.1'.
Installing app...
Disconnecting.
Done.
```

### Confirm and launch

```powershell
& $wadc list -ip 127.0.0.1 | Select-String "HololensIKEA"
```

Open the HoloLens **Start menu**, find the **HololensIKEA Hololens** tile, and air-tap.

---

## Updating and Redeploying

1. Edit code.
2. Bump `Version` in `Package.appxmanifest`:
   ```xml
    <Identity Name="HololensIKEA" Publisher="CN=HololensIKEA" Version="1.0.0.1" />
   ```
3. Run the deploy script again. It rebuilds, signs, uninstalls the old package if needed, and redeploys:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\deploy.ps1
   ```

---

## Troubleshooting

### `WMC1006: Cannot resolve Assembly or Windows Metadata file '*.winmd'`

The Windows 10 SDK is present but the **WinMD contracts** component is missing
(the `References\10.0.19041.0\` folder exists but is empty).

```powershell
$vswhere   = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath    = & $vswhere -latest -products * -property installationPath
$installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vs_installer.exe"

& $installer modify `
    --installPath "$vsPath" `
    --add Microsoft.VisualStudio.Component.Windows10SDK.19041 `
    --add Microsoft.VisualStudio.ComponentGroup.UWP.MSBuild `
    --quiet --norestart --force

if ($LASTEXITCODE -notin @(0, 3010)) { throw "Installer exited $LASTEXITCODE" }

# Verify: should print 88 or more
(Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\References\10.0.19041.0" -Force |
    Measure-Object).Count
```

---

### `error CS0234` / reference resolution failures

The 103 `<Reference>` HintPaths in the `.csproj` assume VS is at the default
`C:\Program Files\` path.  Regenerate them for your machine:

```powershell
python scripts\regenerate_refs.py
```

The script reads the canonical reference list from the `.NETCore v5.0`
framework lock file and rewrites only the `<Reference>` block.

---

### `Cannot resolve Assembly or Windows Metadata file 'System.Runtime.dll'`

The XAML compiler needs the **UWP-specific** `netcore50` variant of
`System.Runtime.dll`, not the .NET Framework 4.x facade.

```powershell
Select-String "System.Runtime" HoloLensHello.csproj |
    Select-String "HintPath" | Select-Object -First 1
# Must contain: ...\system.runtime\4.1.0\ref\netcore50\System.Runtime.dll
```

If the path is wrong, re-run `scripts\regenerate_refs.py`.

---

### `APPX0702: Payload file does not have valid dimensions`

The logo PNGs are missing or the wrong size.  Regenerate them:

```powershell
python scripts\make_assets.py
```

Required sizes:

| File | Pixels |
|---|---|
| `Square150x150Logo.scale-200.png` | 300 � 300 |
| `Wide310x150Logo.scale-200.png` | 620 � 300 |
| `Square44x44Logo.scale-200.png` | 88 � 88 |
| `Square44x44Logo.targetsize-24_altform-unplated.png` | 24 � 24 |
| `StoreLogo.png` | 50 � 50 |
| `SplashScreen.scale-200.png` | 1240 � 600 |
| `LockScreenLogo.scale-200.png` | 96 � 96 |

---

### `APPX0104 / APPX0107: Certificate file not found`

The `.pfx` is committed to git and should be present after cloning.
If it is missing (e.g. deleted accidentally), regenerate it:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\create_cert.ps1
```

---

### `WinAppDeployCmd devices` shows nothing

- Confirm Developer Mode and Device Portal are enabled (Step 4).
- Unplug and replug the Micro-USB cable.
- In Device Manager confirm *Remote NDIS based Internet Sharing Device*
  appears under Network adapters without an error flag.
- Try a direct USB port on the PC � some hubs block the RNDIS handshake.

---

### `WinAppDeployCmd install` asks for a PIN

First-time pairing only.  On the HoloLens go to
**Settings ? For developers ? Pair** to display the 6-digit code:

```powershell
& $wadc install -f "$pkg\*.appx" -ip 127.0.0.1 -pin 123456 `
    -d "$pkg\Dependencies\x86\Microsoft.VCLibs.x86.14.00.appx" `
    -d "$pkg\Dependencies\x86\Microsoft.NET.CoreRuntime.1.1.appx"
```

---

## How It Works

### Why explicit `<Reference>` HintPaths instead of NuGet restore?

This project targets `TargetFrameworkIdentifier=.NETCore` /
`TargetFrameworkVersion=v5.0` � the UAP model from the VS2015�2017 era.
Managed references normally arrive via `Microsoft.NETCore.UniversalWindowsPlatform`
NuGet packages, resolved by a `project.json` ? `project.assets.json` restore.

On a machine where those packages are already installed at
`C:\Program Files (x86)\Microsoft SDKs\UWPNuGetPackages\` (placed there by the
VS UWP workload) but `nuget restore` has never been run, the
`ResolveNuGetPackageAssets` MSBuild target is skipped and the reference path
stays empty.  Rather than adding a restore step, the `.csproj` bypasses package
resolution by pointing each of the 103 assemblies directly at its known path.

The canonical list comes from the `.NETCore,Version=v5.0` target inside:

```
C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETCore\v5.0\project.lock.json
```

`scripts/regenerate_refs.py` re-derives that mapping if paths ever change.

### Why `System.Runtime.dll` must be the `netcore50` variant

The XAML compiler (`MarkupCompilePass1`) builds a *type universe* by loading
every assembly in `@(ReferencePath)` and walking all their dependencies.

- The `.NET Framework 4.x facade` (`v4.8\Facades\System.Runtime.dll`, 29 KB)
  contains only type-forwarders.  The XAML compiler follows those forwarders
  into `mscorlib` / `System.Core`, which are not UWP types, causing a
  `NullReferenceException` inside the compiler.
- The `netcore50` variant (`UWPNuGetPackages\system.runtime\4.1.0\ref\netcore50\
  System.Runtime.dll`, 124 KB) contains real UWP type definitions the compiler
  can reason about without cascading into framework DLLs.

### 3D renderer (`MainPage.xaml.cs`)

~160 lines of pure C# on the UI thread.  No DirectX.  No engine.

| Step | Detail |
|---|---|
| Geometry | Unit cube: 8 vertices at �1 in XYZ, 6 quads wound CCW |
| Transform | Rotate-Y then Rotate-X; 4 `Math.Cos/Sin` values reused for both vertices and normals |
| Back-face cull | Rotated normal `z > 0` ? `Visibility.Collapsed` |
| Lighting | Lambert: `0.25�ambient + 0.75�max(0, ?dot(rotNormal, lightDir))` |
| Depth sort | Face-centroid Z, descending `Array.Sort`, `Canvas.SetZIndex` per rank |
| Projection | `screen_x = cx + vx / (vz + 4.5) � scale` |
| Allocation | 6 `Polygon` + `SolidColorBrush` created once; only `PointCollection` (4 pts) is new per frame |
| Frame rate | `DispatcherTimer` at 16 ms (~60 fps target) |

**Why black = transparent on HoloLens:**
HoloLens 1 is additive � it adds light to the view.  A pixel that emits no
light is invisible, so `Background="Black"` means no window frame; only the
lit cube faces are visible in the real world.

### Key project properties

| Property | Value | Reason |
|---|---|---|
| `TargetPlatformIdentifier` | `UAP` | UWP app container |
| `TargetPlatformVersion` | `10.0.19041.0` | Latest SDK with HoloLens-compatible WinMD contracts |
| `TargetPlatformMinVersion` | `10.0.10240.0` | HoloLens 1 ships RS4 (17763); minimum set to RTM |
| `PlatformTarget` | `x86` | HoloLens 1 has an Intel Atom x86 SoC |
| `LangVersion` | `7.3` | Enforced by XAML 8.2 build targets |
| `UseDotNetNativeToolchain` | `false` | .NET Native AOT not needed for sideloaded dev builds |

---

## CI/CD

Two GitHub Actions workflows run on pinned `windows-2022` runners.

### `dotnet.yml` � compile check (PR gate)

Triggers on every push and PR to `master`.  Builds Debug **and** Release with
signing and packaging disabled � no certificate is required, and no `.appx`
is produced.  Its sole purpose is to catch build regressions.

```
push / PR  ?  Checkout
           ?  Setup MSBuild
           ?  Install Windows10SDK.19041 + UWP.MSBuild via vs_installer
           ?  msbuild /p:Configuration=Debug   /p:AppxPackageSigningEnabled=false ...
           ?  msbuild /p:Configuration=Release /p:AppxPackageSigningEnabled=false ...
```

### `dotnet-desktop.yml` � signed `.appx` artifact

Triggers on push to `master` only.  Generates a **fresh** self-signed cert on
the runner (no stored secrets), builds a full signed Release `.appx`, uploads
it as a named artifact, then removes the cert.

```
push to master  ?  Checkout
                ?  Setup MSBuild
                ?  Install Windows10SDK.19041 + UWP.MSBuild via vs_installer
                ?  New-SelfSignedCertificate + Export-PfxCertificate  (password: "ci")
                ?  msbuild /p:Configuration=Release /p:GenerateAppxPackageOnBuild=true
                ?  Remove-Item cert
                ?  Upload HoloLensHello-<sha>-Release-x86 artifact
```

**Why `vs_installer.exe modify` in CI:**
The `windows-2022` runner ships with VS2022 Enterprise, but without the UWP
workload.  The `References\10.0.19041.0\` folder exists but is empty.  Adding
`Windows10SDK.19041` + `UWP.MSBuild` populates it (~300 MB, idempotent).

**Deploying the CI artifact to HoloLens:**

1. **Actions ? UWP Package ? latest run ? Artifacts**
2. Download and extract `HoloLensHello-<sha>-Release-x86.zip`.
3. Connect HoloLens via USB, then:

```powershell
$wadc = (Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" `
    -Recurse -Filter "WinAppDeployCmd.exe" | Sort-Object FullName |
    Select-Object -Last 1).FullName

$pkg = "HoloLensHello_1.0.1.0_x86_Test"
& $wadc install -f "$pkg\HoloLensHello_1.0.1.0_x86.appx" -ip 127.0.0.1 `
    -d "$pkg\Dependencies\x86\Microsoft.VCLibs.x86.14.00.appx" `
    -d "$pkg\Dependencies\x86\Microsoft.NET.CoreRuntime.1.1.appx"
```

---

## Reference: Key Paths

| Item | Default path |
|---|---|
| MSBuild | `C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe` |
| UWP XAML targets | `...\MSBuild\Microsoft\WindowsXaml\v17.0\8.2\Microsoft.Windows.UI.Xaml.CSharp.targets` |
| Windows 10 SDK root | `C:\Program Files (x86)\Windows Kits\10\` |
| SDK WinMD contracts | `...\Windows Kits\10\References\10.0.19041.0\` |
| UWP NuGet packages | `C:\Program Files (x86)\Microsoft SDKs\UWPNuGetPackages\` |
| Framework lock file | `C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETCore\v5.0\project.lock.json` |
| WinAppDeployCmd | `C:\Program Files (x86)\Windows Kits\10\bin\<sdk-version>\x64\WinAppDeployCmd.exe` |
| VS installer | `C:\Program Files (x86)\Microsoft Visual Studio\Installer\vs_installer.exe` |
| vswhere | `C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe` |
