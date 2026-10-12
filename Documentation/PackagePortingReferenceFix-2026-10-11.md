# Existing dependency corruption during package porting

Cloning LE2 `BioP_N7Mmnt8.pcc` export 11110 (`BioS_N7_Mountain_Ext.Terrain.N7_ME_Terrain_Sml`) into `BioA_IntHub_100Apartment.pcc` under export 1110 with Clone all references reused existing destination materials, then incorrectly queued those materials for relinking.

Their binary references already contained destination indices. Relinking interpreted those indices against the source package: for example, material 1441's texture reference 1480 was interpreted as an unrelated source particle distribution. This modified existing materials and textures, imported an unrelated dependency tree, and produced 45 new bad-reference issues.

Reused dependencies now remain in the mapping cache but are excluded from data relinking. The same rule applies to existing children during Clone all references, shader copying, and cross-game function recompilation. Newly copied dependencies, intentional merges, explicit replacements, and caller-supplied mappings retain their existing behavior.

## Exact reproduction

The source and destination packages were opened and cloned in memory. Original files were not saved.

| Result | Before fix | After fix |
| --- | ---: | ---: |
| Destination export count | 3,709 -> 4,904 | 3,709 -> 3,711 |
| Destination import count | 301 -> 341 | 301 -> 301 |
| New bad-reference issues | 45 | 0 |

After the fix, all 3,709 existing exports retain identical data, headers, and change flags. The new mesh is export 3710 under parent 1110; it references new body setup 3711 and existing material 1441. Only those two exports are added.

The bad-reference checker has one issue both before and after the corrected clone: material 1570 (`Wall02_Inst07`) has an existing SM2 texture reference to material-expression export 667. The clone does not alter it.

The tested Core assembly's SHA-256 is `0094FE2DC07D2E482205A369BDEBAD5B4E52AAEABBA37441B80637DF33163D21`. The application output uses the same Core assembly. Reproduction logs are under `Build/porting-repro`.

## Vanilla LE3 material warning

Export 395 in the base-game `BioD_CitHub_DockP2.pcc`, `EffectsMaterials.Users.HMF_Miranda_MASTER_MAT_ARM_USER.CombinedEffect_Damaged_Armor`, already contains this SM3 compilation error:

> Arithmetic between types float3 and float1 are undefined

Its material ID is `767b4613-1137-4175-a865-a56d4fbb5a4a`. There is no local shader cache in that package and no corresponding map in the configured global cache. The highest mounted copy, from `DLC_MOD_Framework`, contains the same ID and compiler error.

Export 6161 selects this material for its `Damaged_Armor` tag. Its default Miranda material, export 336, has a valid global shader map and no compiler errors. This warning predates the clone operation, so the checker continues to report it. Read-only inspection reports are under `Build/material-check-probe`.

## Regression coverage

The full application suite passes 1,020 tests with one intentionally skipped benchmark. The full Core suite passes all 244 tests. Six new LE2/LE3 Core cases cover preservation of reused material/texture exports, repeated material references, correctly relinked new dependencies, package-child reuse, explicit replacement, and reference checks after save/reload.
