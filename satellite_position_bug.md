# Satellite Position Bug Report

## Summary

There is a bug in the HololensIKEA project where only one satellite cube is visible in the 3D scene, and its label is garbled. The satellite(s) do not move, and their positions and labels are incorrect. Sometimes, no satellites are visible at all. The debug window shows satellite data, but all satellites have the same azimuth (Az) and elevation (El), and their computed 3D positions are identical. At times, the debug window shows SATS: 0, meaning no satellites are found above the horizon.

## Observed Symptoms
- Only one satellite cube is visible in the 3D scene, even though debug output lists multiple satellites.
- The satellite cube is frozen and does not move.
- The satellite label is garbled or overlapping.
- All satellites in the debug output have the same Azimuth (Az360 or Az0), Elevation (El0), and X/Z position (X0.00 Z-1.00).
- Sometimes, SATS: 0 is shown, and no cubes are rendered.
- The debug window does show correct GPS coordinates for the observer.

## Screenshots
- Satellite debug output with multiple satellites, all with Az360 El0 X0.00 Z-1.00.
- Scene with only one visible cube and garbled label.
- Scene with SATS: 0 and no cubes.

## Root Cause Analysis
- The bug is in the coordinate transformation from ECI (Earth-Centered Inertial) to the local topocentric (observer) frame in `OrbitService.cs`.
- The transformation was either mathematically incorrect (original version) or too strict ("corrected" version filtered out all satellites).
- As a result, all satellites are calculated to have the same azimuth and elevation, so they overlap in the 3D scene.
- The satellite list is being updated, but the positions are not changing due to the transformation bug.

## Related Files
- `Services/OrbitService.cs`  
  (Contains the coordinate transformation logic in `ComputeObserverRelativePosition`)
- `Services/Sgp4Service.cs`  
  (Propagates satellite orbits and outputs ECI coordinates)
- `Content/SatelliteRenderer.cs`  
  (Renders satellites in 3D, computes their positions in the scene)
- `Models/Satellite.cs`  
  (Holds satellite properties including position, azimuth, elevation, etc.)

## Key Code Sections
- `OrbitService.cs: ComputeObserverRelativePosition`  
  (ECI to topocentric transformation, azimuth/elevation calculation)
- `SatelliteRenderer.cs: ComputeSatellitePosition`  
  (Maps azimuth/elevation to 3D scene coordinates)
- `SatelliteRenderer.cs: Update`  
  (Fetches and updates satellite list)

## Attempts to Fix
- Tried several versions of the coordinate transformation (South-East-Zenith, East-North-Up, etc.)
- Added debug output for azimuth, elevation, and 3D positions
- Increased cube size and dome radius for visibility
- Added time acceleration for testing (not yet enabled)

## Next Steps
- Finalize and verify a correct ECI to topocentric transformation
- Ensure satellites have unique azimuth/elevation values
- Confirm satellites move over time
- Fix label rendering after positions are correct

---
