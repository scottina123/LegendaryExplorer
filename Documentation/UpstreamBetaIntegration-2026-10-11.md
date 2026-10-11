# Upstream Beta integration (2026-10-11)

Fork baseline: `8758efec73a9a1c270837b7d04c5c01dab7bf4df`.
Upstream: `ME3Tweaks/LegendaryExplorer` Beta at
`b8869f92c7b80be7818447b5079f65c99b4a97b8` (33 incoming commits).

The merge incorporates the upstream changes while retaining the fork's Level
Editor interface. `Tools/LevelEditor/LevelEditor.xaml` is unchanged. The upstream
redesign's styles, grouped outliner, and lighting selection menu are excluded.
The existing Unlit control selects unlit versus level lighting; native game
shaders, lightmaps, and dynamic lighting are enabled in the level renderer.

Integrated renderer changes include lightmap policies and 1D sample swizzling,
skeletal light environments and dynamic lights, shadows, HDR rendering, MSAA,
reverse depth, mip sampling, backface/frustum culling, shader resource binding
caches, and frame-based material uniform caching. The fork's background resource
preparation, static scene cache, animation/morph support, character attachment
transforms, material editing, VFX, navigation, and lightmass workflows remain.

Compatibility adaptations preserve the VFX preview's Hable transfer and legacy
material colors, texture channel diagnostics, camera-relative lighting, native
picking, CPU/GPU tangent skinning, and lightmap refresh after repeated bakes.
Legacy matrix rows remain readable when refreshing edited lights.
Mesh transforms are set before light shader preparation. Materials missing a
cooked lightmap policy receive the baked lights dynamically when compatible
shaders exist, with a diffuse preview fallback otherwise. Flat Shaded suppresses
additive lighting and shadows. Masked hit proxies preserve discarded coverage,
selection highlights, and the background's color transfer.

Outside the Level Editor, the merge includes the upstream script compiler and
decompiler fixes, generated binary reference type checking, TLK synchronization,
dialog filtering, experiment shortcuts, and mesh/texture import fixes. Fork
reference issue navigation/actions and null skeletal material slots are retained.
The fork's newer TexConverter binary is retained, including its existing small
block-compressed mip fix. UI lifetime fixes also cancel stale Interpreter restores
and safely handle detached Recents controls.
Explicit reference navigation runs after binding/layout work without waiting for
background input processing; binary selection retains export, scan, and request
guards for asynchronous parsing and rapid navigation.

Validation uses the x64 WinDebug application/core test projects and the WinRelease
application build. WPF tests run with
`RunConfiguration.ExecutionThreadApartmentState=STA` and
`RunConfiguration.DisableParallelization=true`; a hang timeout prevents modal
exceptions from blocking unattended runs. Test assets are copied declaratively
into the application test output. Added regressions exercise light edits, matrix
layouts, reference checking, glTF tangents/materials/normals, shader selection and
compilation, channel diagnostics, projection precision, and offscreen HDR/VFX
rendering, baked-light fallback, and masked hit-proxy coverage. Visual comparison
against gameplay is still a separate manual check.

Final results: 238 core tests passed; 930 application tests passed, with the
existing `NativeBackend_Benchmark1024Texture` benchmark intentionally skipped.
All 26 focused rendering/navigation checks passed. The x64 WinDebug test build
and x64 WinRelease application build completed with zero errors.

Viewport cache follow-up: the existing static-cache allocation stripped all bind
flags from the new multisampled scene textures, causing `E_INVALIDARG` during
`CreateTexture2D`. Cache textures now retain render-target bindings for HDR color
and hit IDs and a depth-stencil binding for depth, with the original sample count,
quality, format, and dimensions. Allocation publishes the resources together and
disposes partial allocations on failure. The hit-cache guard checks the actual
multisampled source. Native light versions also participate in the backdrop key
so sky-light changes discard cached ambient lighting.

The existing View options menu now includes Copy viewport issues, which copies
resource and render exceptions with current level diagnostics. Viewport error
text is white. Regression checks use real D3D11 hardware and WARP devices at 1x
and 4x samples, verify every HDR/depth/hit sample after capture and restore, and
cover stale keys/cameras, invalidation, native sky-light changes, and copying
current exception details. Before the fix, all three 4x cache cases reproduced
the reported allocation failure.

Follow-up validation: all 9 focused checks passed in x64 WinDebug. The full x64
WinRelease application suite passed 939 tests with only the existing benchmark
intentionally skipped. Both managed configurations built with zero errors; the
native WinRelease lightmap backend built with zero warnings and zero errors.
