# LE2 Level Editor navigation performance

The LE2 apartment scene spent most of its rendering time in additive mesh lighting. It queued 1,627 light draws per frame, compared with 177 in the LE3 apartment scene. Most visible LE2 meshes use the no-lightmap shader policy, so their authored lights remain necessary.

The renderer now rejects point and spot lights whose finite influence cannot reach the viewport. For the remaining lights, conservative screen rectangles restrict their additive mesh draws. Shadow masks and light functions render with their existing states, and rasterizer state is restored before modulated shadows and later passes.

Bounds use camera-relative coordinates to preserve small light radii far from the world origin. Camera/near-plane crossings, orthographic views, directional and sky lights, and invalid or unbounded attenuation retain unrestricted rendering. Side-plane rejection omits depth clipping so materials that disable it retain their lighting.

Material preparation also reuses the context's parsed package shader cache and selects a mesh's authored lightmap policy on its first shader load. Direct material and VFX consumers keep their existing eager preparation. Cached samplers can be retrieved without acquiring the shader-object creation lock.

## Measurements

Baseline: repository commit `9efe72553`. Final output: `Build/le2-performance/optimized`. Measurements use a hardware Direct3D 11 device, the actual Level Editor actors and native material/light shaders, 4x MSAA, 60 warmup frames, and 90 measured frames with GPU timestamp queries. Camera yaw changes each frame. Loading and resource preparation are outside the measured interval.

The loaded assemblies are verified by SHA-256: baseline `A8BB501671FE688EB3B1A9190DF0B8FDB982F5097E29662CEF739B44A1A9425D`, updated `C6EE4C51577C0EB783358470D9C1C8DF10C2A7EB31F182386398925EED6E7B68`.

| LE2 apartment view | Baseline rendering time | Updated rendering time | Reduction |
| --- | ---: | ---: | ---: |
| 1920 x 1080 | 6.457 ms | 4.751 ms | 26% |
| 3840 x 2160 | 12.789 ms | 10.561 ms | 17% |
| Second camera, 1920 x 1080 | 6.431 ms | 3.504 ms | 46% |
| Letterboxed, 1920 x 1080 | 7.674 ms | 5.108 ms | 33% |
| Position/yaw motion path, 1920 x 1080 | 6.483 ms | 3.729 ms | 42% |
| Position/yaw motion path, 3840 x 2160 | 15.633 ms | 8.400 ms | 46% |

The motion path interpolates camera position and orientation between the two tested views. Its LE2 lighting GPU time falls from 4.778 to 1.787 ms at 1080p and from 11.955 to 4.718 ms at 4K. The final path-frame images differ by only one pixel at 1080p and two pixels at 4K, each by one 8-bit color value.

LE3 rendering time remains comparable: 1.705 to 1.741 ms at 1080p, and 2.695 to 2.788 ms at 4K. Its images differ by three pixels of one color value at 1080p and match exactly at 4K.

These are offscreen rendering measurements, excluding WPF presentation. The timing scene prepares mesh, light, and decal resources; particle resources are checked separately for visual parity.

The tested packages are `BioA_IntHub_100Apartment.pcc` from Interactive Intai'sei Apartment (LE2) and `BioA_Vegas_APT.pcc` from Pinnacle Station Apartment (LE3).

## Validation

The final application's full test suite passes 1,020 tests, with zero failures and one intentionally skipped benchmark. The focused Level Editor suite passes 231 tests with the same skip. New tests cover cooked LE2/LE3 shader-cache reuse, initial lightmap policy, projected light volumes, camera rotations and orbit views, fractional viewports, attenuation edge cases, side-only culling, and small lights at coordinates of 100,000,000.

VFX checks prepare all 15 apartment particle systems and 8 lens flares, then advance their simulation deterministically. Both versions render 124 live particles, 7 visible emitters, and 8 visible flares without a render error. Full-scene baseline/updated images differ by one pixel of one color value with mesh shaders enabled, and two pixels with them disabled. An isolated VFX-only scene is pixel-identical with the mesh shader checkbox enabled and disabled. The check uses the actual checkbox contract: the global mesh shader flag changes while VFX components retain their native material rendering.

Profiling harness, image comparisons, logs, and test reports are saved under the ignored `Build/le2-performance` directory.
