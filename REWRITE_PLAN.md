# Plan: Rewrite HoloLens App to Display 3D Models from JSON

## 1. Requirements & Analysis
- Parse product JSON data to extract relevant 3D model parameters (dimensions, labels, etc.).
- Generate simple 3D geometry (e.g., cubes) based on these parameters.
- Display the generated 3D models in the HoloLens app, with labels and correct positioning.
- Support for multiple products and dynamic loading.
- Use or adapt code samples from `Windows-universal-samples-main` for 3D rendering and UI.

## 2. Data Handling
- Define C# classes to deserialize the JSON structure (using e.g., `System.Text.Json` or `Newtonsoft.Json`).
- Implement a loader to read JSON files at runtime (from local storage or remote source).
- Map JSON fields (e.g., "bredde på produktet", "høyde på produktet", "lengde på produktet") to 3D model parameters.

## 3. 3D Model Generation
- For each product, create a 3D model (e.g., a box/cube) using the extracted dimensions.
- Optionally, support more complex geometry if the JSON provides more details.
- Add text labels in 3D space for product names and dimensions.

## 4. Rendering Pipeline
- Integrate with the HoloLens rendering pipeline (Direct3D, SharpDX, or Unity if migrating).
- Use or adapt rendering code from the `Content/` and `Common/` folders, or from Microsoft samples.
- Position models in the scene with appropriate spacing and orientation.

## 5. User Interaction
- Allow user to select/view different products.
- Support basic interactions: rotate, zoom, select, show/hide labels.

## 6. UI & Experience
- Display product metadata (name, description, etc.) alongside or overlaid on the 3D model.
- Ensure the UI is usable in mixed reality (font size, placement, etc.).

## 7. Testing & Iteration
- Test with multiple JSON files and product types.
- Validate rendering accuracy and usability on HoloLens hardware.

## 8. Documentation
- Document the JSON format, model mapping, and how to add new products.
- Provide usage instructions for the new app.

## 9. Fetching Product Data from Efobasen.no

- **API Endpoint**:  
	Use the endpoint `https://efobasen.no/API/VisProdukt/HentProduktinfo` with a POST request and payload `{"Elnummer":<product_number>}` to fetch product JSON.
- **Example**:  
	For product 4902149, use:
	```
	curl 'https://efobasen.no/API/VisProdukt/HentProduktinfo' \
		-H 'content-type: application/json' \
		--data-raw '{"Elnummer":4902149}'
	```
- **Authentication**:  
	If required, ensure headers (like `ngversionstamp`) are set as in the browser.

## 10. Parsing and Mapping JSON to 3D Model

- **Extract Dimensions**:  
	- For the indoor unit:  
		- Bredde: `Skjema.Skjema.Grupper[].Felter[]` where `Navn` = "bredde innendørs enhet" or similar (see ETIM/Grupper).
		- Høyde: "høyde innendørs enhet"
		- Dybde: "dybde innendørsenhet"
	- For the outdoor unit:  
		- Bredde: "bredde utendørs enhet"
		- Høyde: "høyde utendørs enhet"
		- Dybde: "dybde utendørs enhet"
- **Fallback**:  
	If ETIM fields are missing, use `Vareinformasjon` or `Pakningsalternativer` groups.

- **Map to Model**:  
	- Use these values to create cubes in your 3D scene, as in your OpenSCAD example.
	- Add labels using product name and dimensions.

## 11. Integration Steps

- **Step 1**: Implement a C# class to fetch product JSON from the API (using `HttpClient`).
- **Step 2**: Deserialize the JSON into C# classes.
- **Step 3**: Write a mapping function to extract the correct fields for 3D model generation.
- **Step 4**: Pass the extracted dimensions to your 3D rendering logic.
- **Step 5**: Display the model and overlay labels in the HoloLens scene.

## 12. Sample Workflow

1. User enters or selects a product number.
2. App fetches product JSON from efobasen.no.
3. App parses JSON, extracts dimensions, and generates 3D model.
4. Model is rendered in HoloLens with labels and metadata.

---

## IMPLEMENTATION STATUS: PHASES 1-6 COMPLETE

### Phase 1 ✅ - Data Model Refactoring
- Created `RenderableProduct` class with all dimensions normalized to meters
- Replaced satellite position/orbit data with product dimensions (Width/Height/Depth)
- Added fields for selection, hover states, and indoor/outdoor unit types

### Phase 2 ✅ - Scene Management
- Created `ProductSceneManager` replacing `SatelliteRenderer` role
- Manages product loading, positioning, and scene updates
- Implements spatial layout: products spaced 1.5m apart, 2m in front of user
- Async product loading with cancellation token support

### Phase 3 ✅ - Rendering Architecture  
- Created `ProductRenderer` for dimension-aware cube rendering
- Replaces fixed-size spinning cubes with properly scaled boxes
- Maintains unit cube geometry that scales dynamically per product

### Phase 4 ✅ - API Service Layer
- Created `EfobasenApiService` for HTTP fetching
- Decoupled from parsing logic
- Supports cancellation and timeouts
- User-Agent header set to "HololensIKEA/1.0"

### Phase 5 ✅ - Robust Parser
- `ProductParser` handles multiple field name variations (case-insensitive)
- Parses string dimensions ("720 mm", "93,5 cm") and numeric values
- Auto-detects units (mm, cm, m)
- Defaults to safe dimensions if missing (prevents zero-sized objects)
- Recursive group traversal for nested ETIM structures

### Phase 6 ✅ - Repository Pattern
- `ProductRepository` provides caching and orchestration
- Separates API, parsing, and business logic layers
- In-memory cache prevents duplicate fetches
- Ready for persistent storage integration

### Phase 6 ✅ - Integration with BasicHologramMain
- Updated `BasicHologramMain.cs` to use `ProductSceneManager`
- Replaced satellite orbital update loop with product state updates
- Fixed focus point to scene center instead of satellite position
- Loads test product (Inventor heat pump) on startup
- Device resource lifecycle properly integrated

---

## Architecture Diagram (Implemented)

```text
AppView (IFrameworkView)
    ↓
BasicHologramMain (HolographicFrame update/render loop)
    ↓
ProductSceneManager
    ├── Services/
    │   ├── EfobasenApiService (HTTP POST to API)
    │   ├── ProductParser (Dimension extraction + normalization)
    │   └── ProductRepository (Caching + orchestration)
    │
    ├── Rendering/
    │   └── ProductRenderer (Scale cubes based on dimensions)
    │
    └── Models/
        └── RenderableProduct (Normalized data, position, state)
```

---

## Next Steps: PHASES 7-9

### Phase 7 - Floating Labels & Billboarding
- Text rendering for product name and dimensions
- Billboard geometry that faces camera
- Position labels above/below products

### Phase 8 - Product Selection UI
- Hardcoded test products first
- Voice input for product number entry
- Gaze-based selection on products
- Gesture-based reset scene

### Phase 9 - Advanced Models
- glTF model loading (future)
- Parametric HVAC geometry generation
- Real CAD file integration

---

## Production-Readiness Checklist

- ✅ Async/await with cancellation tokens
- ✅ Dimension normalization to meters
- ✅ Robust string parsing
- ✅ Loose coupling (API ↔ Parser ↔ Renderer)
- ✅ Device resource lifecycle handling
- ✅ Caching to prevent redundant API calls
- ⚠ TODO: Error handling & retry logic for network failures
- ⚠ TODO: Performance optimization for GPU (low triangle count verified)
- ⚠ TODO: Spatial anchors for persistent product positions
- ⚠ TODO: Voice input for product selection
- ⚠ TODO: Testing on actual HoloLens device

---

## File Locations

- Models: `Models/RenderableProduct.cs`
- Services: `Services/ProductServices.cs` (contains EfobasenApiService, ProductParser, ProductRepository)
- Rendering: `Content/ProductRenderer.cs` + `Content/ProductSceneManager.cs`
- Main Loop: `BasicHologramMain.cs` (updated)
- Original Fetcher: `Helpers/EfobasenProductFetcher.cs` (archived, use ProductServices instead)
