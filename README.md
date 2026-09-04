# HololensIKEA

> **Unofficial educational hobby project.** This is not an IKEA product, is not affiliated with, sponsored by, or endorsed by IKEA, and uses IKEA names only to identify publicly available product pages. It is intended for learning HoloLens, Direct3D, UWP, and glTF workflows. Do not use it as a substitute for IKEA product information, measurements, availability, or official applications.

A native Direct3D 11 / UWP experiment for **Microsoft HoloLens 1**. It loads public IKEA product pages from a curated bookmark list and downloads each available 3D model at runtime for display as a movable, rotatable hologram.

The project is inspired by [IKEA 3D Model Download Button](https://github.com/apinanaivot/IKEA-3D-Model-Download-Button).

## Application workflow

At launch, the app displays a movable **IKEA bookmarks** panel. Selecting a bookmark queues that IKEA product and keeps the panel available for selecting additional products. Each selected product is placed in front of the user after its model has been downloaded and decoded. Multiple products are supported: selections and voice requests are queued, and completed models remain independently visible.

Models are manipulated with gaze and air tap. The original edge handles provide direct move and rotate interaction. When a model is active, a separate command bar is displayed below it with independent **Move**, **Rotate**, and **Delete** targets. The command bar is deliberately separated from the top rotation handle so deletion does not conflict with rotation. Delete uses a confirmation dialog before removing the active model.

Voice commands include `Bookmarks` to show the list, `Clear` or `Remove all` to clear the scene, and the first word of a bookmark name to add that product. For example, saying `BILLY` three times queues three BILLY Bookcases. The recognized name must match exactly; `BILLY Bookcase` is displayed in the list but `BILLY` is the voice trigger.

## Runtime-only IKEA model downloads

IKEA 3D models **must not be bundled with this application**. The repository and the AppX package contain no IKEA `.glb` model files. The bookmark list stores only product-page URLs:

```json
{
  "name": "BILLY Bookcase",
  "url": "https://www.ikea.com/us/en/p/billy-bookcase-brown-walnut-effect-50508652/"
}
```

When a bookmark is selected, the HoloLens downloads and processes the model during the app session:

1. The app extracts the eight-digit IKEA article number from the bookmark URL.
2. `ModelService3D` resolves IKEA's Rotera static model URL, for example `50508652-mini.glb`.
3. The app downloads the GLB over HTTPS directly from IKEA.
4. The GLB v2 payload is parsed on the device. IKEA's current models use `KHR_draco_mesh_compression`; the packaged x86 Draco decoder expands the compressed primitive in memory.
5. The resulting mesh is normalized, uploaded to Direct3D, and placed in the scene.

The product page and Rotera API are fallback discovery paths when the deterministic static URL is not available. The app does not download models during the repository update workflow, and it does not commit or package downloaded models. A temporary device-local cache may be added later, but any such cache must remain outside the repository and AppX package.

Only products whose current public IKEA pages expose a usable downloadable model are included. IKEA may change its markup, hosting, catalog, availability, or terms at any time; a model may therefore disappear from a future bookmark update.

## Bookmark refresh workflow

`.github/workflows/update-bookmarks.yml` runs on its configured schedule or by manual dispatch. It uses [Katana](https://github.com/projectdiscovery/katana) to discover IKEA product pages and validates model availability through IKEA's Rotera service.

The workflow is intentionally divided into two phases:

1. Validate existing page bookmarks first, preserving valid entries and skipping them during discovery.
2. Crawl for new IKEA product pages, deduplicate article numbers, and probe only new candidates with bounded concurrency.

The workflow writes page-only entries to `bookmarks.json`. It must never download, decode, commit, or package IKEA `.glb` files. CI validation fails if a bookmark contains legacy `glbUrl` or `sourceGlbUrl` fields or if a `.glb` file appears under `Models/`.

## Repository structure

| Area | Purpose |
| --- | --- |
| `BasicHologramMain.cs` | HoloLens lifecycle, input, bookmark queue, model placement, command bar, manipulation, and rendering loop. |
| `Models/Bookmark.cs` | Page-only IKEA bookmark metadata. |
| `Models/ProductInstance.cs` | State for saved model instances, including decoded runtime mesh data. |
| `Services/ProductServices.cs` | IKEA product-page loading and renderable product metadata creation. |
| `Services/ModelService3D.cs` | Runtime IKEA URL resolution, HTTPS GLB download, binary GLB parsing, Draco primitive dispatch, and mesh normalization. |
| `Services/DracoDecoder.cs` | HoloLens x86 binding for decoding `KHR_draco_mesh_compression` primitives in memory. |
| `Services/BookmarkVoiceCommandResolver.cs` | Exact first-word bookmark aliases used by voice commands. |
| `Content/GltfMeshRenderer.cs` | Direct3D 11 mesh upload and holographic rendering. |
| `Content/ProductManipulationHandles.cs` | Gaze-sensitive move/rotate handles and the separate Move/Rotate/Delete command bar. |
| `Native/x86/draco_tiny_dec.dll` | MIT-licensed native Draco decoder required by the HoloLens x86 runtime; it contains no IKEA model data. |
| `Content/BookmarksDialog.cs` | Movable in-app IKEA bookmarks panel. |
| `HololensIKEA.csproj` | UWP project configured for HoloLens 1 and x86 packaging. |
| `.github/workflows/update-bookmarks.yml` | Scheduled and manual page-only bookmark discovery and validation. |
| `.github/workflows/sideload-release.yml` | Manual or tag-triggered signed x86 sideload release workflow. |
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

## GitHub Actions and sideload releases

The repository includes five project workflows:

- `dotnet.yml` performs a compile-oriented validation.
- `dotnet-desktop.yml` creates a signed x86 sideload artifact.
- `update-bookmarks.yml` refreshes page-only IKEA bookmarks without storing models.
- `sideload-release.yml` creates a GitHub Release containing sideloadable x86 assets.
- `store-submission.yml` performs Store-compatible version validation, packaging, optional Windows App Certification Kit validation, and optional Partner Center submission.

The sideload release workflow supports both:

- Manual dispatch from the workflow's **Run workflow** control, optionally with a tag such as `v1.0.1.0`.
- Push of a tag matching `v*.*.*.0`.

A manual run without a tag selects the next available build number based on `Package.appxmanifest`. The release includes the signed `.appx`, dependency/install bundle zip, and public `.cer` certificate. It does not include IKEA model files.

Store submission requires the repository secrets documented in the workflow, including the Store app ID and Azure/Partner Center credentials.

The app manifest uses the HoloLens 1-compatible `Windows.Universal` target family, requests `internetClient` for IKEA page and model downloads, and packages the x86 architecture for the device. The package identity and publisher must remain aligned with the Microsoft Store product configured in Partner Center before the first submission.

## Implementation notes

The GLB parser handles the standard GLB v2 header, JSON chunk, binary chunk, position and normal accessors, unsigned byte/short/int triangle indices, and IKEA's `KHR_draco_mesh_compression` primitives. Draco geometry is decoded in memory on the HoloLens; the downloaded GLB is never written into the repository or AppX as an IKEA asset. Models are centered around their bounding-box center. Implausibly large coordinate ranges are treated as millimetres and converted to metres; normal IKEA GLB exports are otherwise retained in metres. The renderer uses the existing lightweight HoloLens shader path and bakes simple directional lighting from vertex normals, avoiding a Unity or runtime-engine dependency.

The app intentionally keeps the default product dimensions at 1 metre while a bookmark model is loading, but it does not treat that placeholder as the downloaded model. If runtime download or parsing fails, diagnostics are written to the debug output and the fallback behavior is used rather than claiming that a GLB was bundled.

Network access can fail if IKEA blocks the device user agent, changes its model hosting, removes a product's 3D model, or requires a model format not supported by the current HoloLens parser. Users should verify IKEA product information, measurements, availability, and usage rights independently.

## References

1. [IKEA 3D Model Download Button repository](https://github.com/apinanaivot/IKEA-3D-Model-Download-Button)
2. [ProjectDiscovery Katana](https://github.com/projectdiscovery/katana)
3. [HololensSatelliteViewer repository](https://github.com/turbolego/HololensSatelliteViewer)
