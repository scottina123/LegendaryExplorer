# LightmassNative

`LightmassNative.dll` is the compute backend for the Level Editor static-lighting baker. Its C ABI is
declared in `LightmassNative.h`; no C++ or STL layout crosses the managed boundary.

Managed code remains responsible for PCC/package parsing, resolving actor/component/light objects,
receiver UV rasterization, Unreal lightmap quantization, texture/package serialization, TFC writes, UI,
and job orchestration. The native library owns deduplicated mesh topology and UV validation, all
instance transforms, bounds/surface metrics, mesh/light relevance scanning, the immutable occluder
scene, deterministic BVH, any-hit shadow traversal, direct and emissive-light evaluation, soft-shadow
samples, receiver-local scheduling, and compute diagnostics.

The concrete ownership boundary is:

- `StaticLightingBaker.BuildScene`, `StaticLightingWriter`, `StaticLightingModels`, and the Level Editor
  UI stay managed. `BuildScene` only resolves package-owned data before its batched native scan.
  `StaticLightingBaker` also retains UV rasterization, coefficient dilation, UE3 color quantization, and
  construction of `StaticLightingTextureBake` / `StaticLightingVertexBake`.
- `NativeStaticLightingSceneScanner` flattens every unique raw LOD once. `LmnScanScene` validates unique
  topology, transforms all instances, calculates receiver bounds/area, and culls lights in parallel.
- `NativeStaticLightingContext` assigns process-local source IDs, flattens immutable collision/surface/
  light arrays, pins each bulk buffer for one call, and maps native coefficients and diagnostics back to
  the existing managed models.
- `LmnCreateBakeContext` and the native `build_node` path own triangle precomputation and the BVH.
  The tree uses binned surface-area splits with median fallback and reports rate-limited determinate
  construction progress through the ABI callback. Build-only primitive data is released afterward,
  leaving a compact traversal triangle array.
  `LmnBakeSamples`, `is_occluded`, and `evaluate_sample` own receiver scheduling, shadow traversal,
  direct/emissive accumulation, and LightMap1D/occupied-LightMap2D sample computation.

The ABI is deliberately coarse grained:

1. `LmnScanScene` receives unique raw meshes plus all instance/light descriptors and exposes immutable
   batched output through `LmnGetSceneScanView`.
2. `LmnCreateBakeContext` receives all flattened world-space occluder triangles once and reports BVH
   construction progress without transferring ownership of the callback state.
3. `LmnBakeSamples` receives a complete LightMap1D receiver or the occupied samples of a complete
   LightMap2D receiver, reports rate-limited sample progress, and returns its coefficient buffer in
   one call. The managed scheduler bounds simultaneous high-resolution buffers and assigns the
   remaining CPU workers inside each active native receiver.
4. `LmnDestroySceneScan` and `LmnDestroyBakeContext` release all native scene memory.

Increment `LMN_ABI_VERSION` whenever a public POD layout or function contract changes.

`WinDebug` deliberately compiles this DLL with native max-speed optimization and PDB symbols. The
managed host remains a normal Debug build, but million-sample receiver ray tracing never runs through
unoptimized C++ or the checked debug STL.
