# HololensIKEA

> **Unofficial educational hobby project.** This is not an IKEA product, is not affiliated with, sponsored by, or endorsed by IKEA, and uses IKEA names only to identify publicly available product pages. It is intended for learning HoloLens, Direct3D, UWP, and glTF workflows. Do not use it as a substitute for IKEA product information, measurements, availability, or official applications.

A native Direct3D 11 / UWP experiment for **Microsoft HoloLens 1**. It loads a curated set of public IKEA product 3D models and presents each as a movable, rotatable hologram.

The project is inspired by [IKEA 3D Model Download Button](https://github.com/apinanaivot/IKEA-3D-Model-Download-Button). A weekly GitHub Actions job uses [Katana](https://github.com/projectdiscovery/katana) in headless-browser mode to visit public product pages and record the actual `.glb` request made by IKEA's client-side model viewer. IKEA serves these models with Draco compression, so the workflow decodes each model into a standard GLB in `Models/`; the application downloads those decoded meshes locally.

## Application workflow

At launch, the app displays a movable bookmark list. Select a product to add its model without closing the list. The model is placed in front of the user and can be moved or rotated using gaze and air tap. Multiple models are supported: selections and voice requests are queued, and each completed model remains independently visible.

Voice commands include `Bookmarks` to show the list, `Clear` or `Remove all` to clear the scene, and the first word of a bookmark name to add that product. For example, saying `BILLY` three times queues three BILLY Bookcases. The recognized name must match exactly; `BILLY Bookcase` is displayed in the list but `BILLY` is the voice trigger.

Only products whose current public pages expose a downloadable GLB are included. IKEA may change its markup, hosting, catalog, availability, or terms at any time; a model may therefore disappear from a future bookmark update.

## Repository structure

| Area | Purpose |
| --- | --- |
| `BasicHologramMain.cs` | HoloLens lifecycle, input, model placement, manipulation, and rendering loop. |
| `Services/ProductServices.cs` | IKEA product-page loading and renderable product metadata creation. |
| `Services/ModelService3D.cs` | GLB download, binary GLB parsing, and mesh normalization. |
| `Services/BookmarkVoiceCommandResolver.cs` | Exact first-word bookmark aliases used by voice commands. |
| `Content/GltfMeshRenderer.cs` | Direct3D 11 mesh upload and holographic rendering. |
| `Content/ProductManipulationHandles.cs` | Gaze/air-tap move and rotate affordances. |
| `HololensIKEA.csproj` | UWP project configured for HoloLens 1 and x86 packaging. |
| `.github/workflows/update-bookmarks.yml` | Runs every Sunday and resolves Katana's latest published release to refresh verified bookmark GLB URLs. |
| `deploy.ps1` | USB deployment helper using `WinAppDeployCmd.exe`. |

## Requirements

Building requires Windows 10 or 11, Visual Studio 2022 with the UWP workload, a Windows 10 SDK containing the HoloLens-compatible UWP targets, and an x86-capable HoloLens 1 device with Developer Mode enabled. The cross-platform `dotnet` CLI cannot build this UWP project because the Windows XAML and UWP MSBuild targets are Windows-only.

## Local build

Run the following from a Visual Studio Developer PowerShell. The x86 platform is required for HoloLens 1.

```powershell
$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"

& $msbuild .\HololensIKEA.csproj `
    /p:Configuration=Debug `
    /p:Platform=x86 `
    /p:AppxPackageSigningEnabled=false `
    /p:GenerateAppxPackageOnBuild=false `
    /v:minimal
```

To create a signed sideload package locally, generate or replace the development certificate with `scripts\create_cert.ps1`, then run:

```powershell
& $msbuild .\HololensIKEA.csproj `
    /p:Configuration=Release `
    /p:Platform=x86 `
    /p:AppxPackageSigningEnabled=true `
    /p:PackageCertificateKeyFile=HololensIKEA_TemporaryKey.pfx `
    /p:PackageCertificatePassword=temp `
    /v:minimal
```

The package is emitted below `AppPackages\HololensIKEA_1.0.0.0_x86_Test\`.

## USB deployment

Enable **Developer Mode** and **Device Portal** on the HoloLens, connect it using a Micro-USB cable, and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy.ps1
```

The helper discovers the HoloLens through the Remote NDIS USB connection and installs the x86 package with `WinAppDeployCmd.exe`. For first-time pairing, supply the six-digit PIN when prompted by the deployment helper or install manually with the Windows 10 SDK deployment command.

## Microsoft Store packaging

The repository includes four Windows GitHub Actions workflows. `dotnet.yml` performs a compile check, `dotnet-desktop.yml` creates a signed x86 sideload `.appx` artifact, `sideload-release.yml` creates GitHub Releases with sideloadable `.appx` assets on version tags, and `store-submission.yml` performs Store-compatible version validation, packaging, optional Windows App Certification Kit validation, and optional Partner Center submission. Store submission requires the repository secrets documented in the workflow, including the Store app ID and Azure/Partner Center credentials.

To create a sideload release, push a tag matching `v*.*.*` or `v*.*.*.*` where the numeric part matches `<Identity Version>` in `Package.appxmanifest`. The release includes the signed `.appx`, a dependency/install bundle zip, and the public `.cer` certificate.

The app manifest uses the HoloLens 1-compatible `Windows.Universal` target family, requests `internetClient` for IKEA page and model downloads, and packages the x86 architecture for the device. The package identity and publisher must remain aligned with the Microsoft Store product configured in Partner Center before the first submission.

## Important implementation notes

The GLB parser handles the standard GLB v2 header, JSON chunk, binary chunk, position and normal accessors, and unsigned byte/short/int triangle indices. Models are centered around their bounding-box center. Implausibly large coordinate ranges are treated as millimetres and converted to metres; normal IKEA GLB exports are otherwise retained in metres. The renderer uses the existing lightweight HoloLens shader path and bakes simple directional lighting from vertex normals, avoiding a Unity or runtime-engine dependency.

The application fetches only a recorded public GLB URL. It does not upload user data or require an application server. Network access can fail if IKEA blocks the device user agent, changes its markup or model hosting, or removes the product's 3D model.

## References

1. [IKEA 3D Model Download Button repository](https://github.com/apinanaivot/IKEA-3D-Model-Download-Button)
2. [EFObasenHololens repository](https://github.com/turbolego/EFObasenHololens)
3. [HololensSatelliteViewer repository](https://github.com/turbolego/HololensSatelliteViewer)
