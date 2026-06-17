# SnapStak Mobile - CON10X Migration Guide

## What Changed

The remote CON10X server dependency has been removed. All processing now happens on-device.

### Removed
- `EngineService` (fetched `content.js` from remote server)
- `AppConfig.ServerUrl`, `AppConfig.ApiContex`, `AppConfig.ApiEngine` (remote endpoints)
- `DeconstructPage.SendToBackendAsync` / `SendComponentAsync` / `BuildPayload` (HTTP POSTs to server)
- `TransformPage` SSE listener + `ListenToSessionSseAsync` + `SendProcessSessionAsync` (server pipeline)

### Added
- `Services/LocalEngineService.cs` - reads `content.mobile.js` from MAUI embedded asset
- `Services/MobilePluginSettingsService.cs` - plugin toggles + OpenRouter key via MAUI Preferences
- `Storage/FilePillarStorage.cs` - IPillarStorage via FileSystem.AppDataDirectory
- `Pipeline/MobileConteXPipelineService.cs` - local DOM→SVG→plugins orchestrator
- `Engine/Plugins/SnapStakSvg/SnapStakSvgTranslatorPlugin.cs` - native SVG export plugin
- `Views/SettingsPage.xaml` + `.xaml.cs` - full settings UI (API key, subscription, plugins, danger zone)

### Modified
- `App.xaml.cs` - `App.Engine` now returns `LocalEngineService` via DI
- `AppConfig.cs` - stripped to subscription endpoint only
- `AppShell.xaml` - Settings tab added
- `MauiProgram.cs` - all new services registered
- `Views/DeconstructPage.xaml.cs` - calls local pipeline instead of HTTP POST
- `Views/TransformPage.xaml.cs` - share sheet export instead of SSE + zip download

---

## Integration Steps

### 1. Add `content.mobile.js` as a MAUI raw asset

In your project, create the folder:
```
Resources/Raw/engine/
```

Copy the uploaded `content.mobile.js` into that folder and rename it:
```
Resources/Raw/engine/content.mobile.js
```

In your `.csproj`, ensure it is included as a `MauiAsset`:
```xml
<MauiAsset Include="Resources\Raw\engine\content.mobile.js" />
```

### 2. Link Desktop engine files

The CON10X engine (StructureService, SvgSerializer, all plugins) lives in `SnapStak.Wasm.Client`.
Use the `<Compile Include="..." Link="...">` entries in `SnapStakMobile.csproj` to link them.

If the projects are in separate solutions, copy the files instead. No changes to those files
are needed - they compile unchanged in the MAUI context.

### 3. Install NuGet packages (via VS NuGet Package Manager UI only)

- `Newtonsoft.Json` 13.x (already installed)
- `SauceControl.Blake2Fast` 2.x (required by `PenpotTranslatorPlugin`)
  - Right-click SnapStakMobile project → Manage NuGet Packages → Browse → `SauceControl.Blake2Fast`

### 4. Replace modified files

Copy these files from this output into the existing project, replacing the originals:

| This output file | Replace in project |
|---|---|
| `AppConfig.cs` | `AppConfig.cs` |
| `App.xaml.cs` | `App.xaml.cs` |
| `AppShell.xaml` | `AppShell.xaml` |
| `MauiProgram.cs` | `MauiProgram.cs` |
| `Views/DeconstructPage.xaml.cs` | `Views/DeconstructPage.xaml.cs` |
| `Views/TransformPage.xaml.cs` | `Views/TransformPage.xaml.cs` |

### 5. Add new files

Copy these new files into the project:

| This output file | Destination in project |
|---|---|
| `Services/LocalEngineService.cs` | `Services/LocalEngineService.cs` |
| `Services/MobilePluginSettingsService.cs` | `Services/MobilePluginSettingsService.cs` |
| `Storage/FilePillarStorage.cs` | `Storage/FilePillarStorage.cs` |
| `Pipeline/MobileConteXPipelineService.cs` | `Pipeline/MobileConteXPipelineService.cs` |
| `Engine/Plugins/SnapStakSvg/SnapStakSvgTranslatorPlugin.cs` | `Engine/Plugins/SnapStakSvg/SnapStakSvgTranslatorPlugin.cs` |
| `Views/SettingsPage.xaml` | `Views/SettingsPage.xaml` |
| `Views/SettingsPage.xaml.cs` | `Views/SettingsPage.xaml.cs` |

### 6. `LocalAssetWebViewHandler.cs` - no changes needed

The existing `Platforms/Android/LocalAssetWebViewHandler.cs` works unchanged because:
- `App.Engine` still exposes `.Read()` and `.IsLoaded`
- `App.Engine` now returns `LocalEngineService` instead of `EngineService`
- Both implement the same method signatures

---

## Architecture Summary

```
AppScannerService           [unchanged - APK WebView detection]
    ↓
AssetExtractorService       [unchanged - APK asset extraction]
    ↓
DeconstructPage             [modified]
    WebView interception     [unchanged - LocalAssetWebViewHandler]
    content.mobile.js        [LocalEngineService - from embedded asset]
    DOM extraction           [unchanged - polling window.__snapstak_result__]
    ↓ extracted JSON
MobileConteXPipelineService [NEW - local, replaces HTTP POST to server]
    ParseExtractionJson      [builds TransformRequest from extractMobile() output]
    StructureService         [ported from Desktop - BuildSVGTree]
    SvgSerializer            [ported from Desktop - SerializeTreeSVG]
    FilePillarStorage        [NEW - writes to FileSystem.AppDataDirectory]
    ↓
TranslatorPluginHost        [ported from Desktop - reflection discovery]
    PenpotTranslatorPlugin   → .penpot
    FigmaTranslatorPlugin    → .figma.svg
    SnapStakSvgPlugin        → .snapstak.svg [NEW - mobile-only]
    CanvaTranslatorPlugin    → .canva.pdf
    ↓
TransformPage               [modified]
    Framework cards          [unchanged - same UI]
    Plugin export buttons    [NEW - Android share sheet per output file]
    Code generation          [unchanged - still uses OpenRouter API key]
```

---

## Plugin Settings

Plugins are toggled in **Settings → Design Tool Plugins**.

Default state:
- Penpot ✓ enabled
- Figma ✓ enabled
- SnapStak SVG ✓ enabled
- Canva ✗ disabled (requires Canva Connect API credentials)

Enabled plugins run automatically on every deconstruct. Their output files
appear as share buttons in TransformPage after extraction completes.

---

## Data Storage

All component data is stored at:
```
FileSystem.AppDataDirectory/snapstak/{userUuid}/{componentId}/
```

Files written per component:
- `{componentId}.svg` - Structure pillar (master SVG)
- `{componentId}_css.json` - Behaviour source data
- `{componentId}_influence.json` - Influence pillar
- `{componentId}_objective.json` - Objective pillar
- `{componentId}.penpot` - Penpot export (if enabled)
- `{componentId}.figma.svg` - Figma export (if enabled)
- `{componentId}.snapstak.svg` - SnapStak SVG export (if enabled)
- `{componentId}.canva.pdf` - Canva export (if enabled)

Clear via **Settings → Danger Zone → Clear all data**.
