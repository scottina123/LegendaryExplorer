# Project Variety Citadel maps — complete reference and AI handoff

**Final reference date:** October 22, 2026, America/New_York.  
**Game:** Mass Effect Legendary Edition, LE3.  
**Scope completed in this chat:** Archos Lounge, Citadel Tower, Hanar Embassy, Volus Embassy, and C-Sec HQ lower/upper; their numbered NPC/quest points of interest; restoration of the six original Citadel maps; one shared arrow-navigation carousel.

This document describes the final installed implementation. Older READMEs in the individual map folders record intermediate revisions and sometimes contain superseded instructions. Prefer this document, the newest verified/decompiled sources, the combined manifest, and inspection of the actual installed packages.

## Contents

1. [Instructions for a future AI chat](#1-instructions-for-a-future-ai-chat)
2. [Paths, installed files, and source authority](#2-paths-installed-files-and-source-authority)
3. [Final behavior and user confirmations](#3-final-behavior-and-user-confirmations)
4. [Runtime architecture and map activation](#4-runtime-architecture-and-map-activation)
5. [Carousel pages, resources, and layout names](#5-carousel-pages-resources-and-layout-names)
6. [Textures, calibration, player position, and heading](#6-textures-calibration-player-position-and-heading)
7. [District coverage and legacy trigger settings](#7-district-coverage-and-legacy-trigger-settings)
8. [NPC labels and numbered markers](#8-npc-labels-and-numbered-markers)
9. [Off-level POI catalogues and quest conditions](#9-off-level-poi-catalogues-and-quest-conditions)
10. [Build and inspection tools](#10-build-and-inspection-tools)
11. [How to change or extend the implementation](#11-how-to-change-or-extend-the-implementation)
12. [Package saving, validation, and deployment](#12-package-saving-validation-and-deployment)
13. [Backups, rollback, and preserving the project](#13-backups-rollback-and-preserving-the-project)
14. [Troubleshooting and fixes that must be retained](#14-troubleshooting-and-fixes-that-must-be-retained)
15. [Verification checklist and limits](#15-verification-checklist-and-limits)
16. [Final installed fingerprints](#16-final-installed-fingerprints)
17. [Appendix: complete final shared handler](#17-appendix-complete-final-shared-handler)
18. [Appendix: router logic and calibration data](#18-appendix-router-logic-and-calibration-data)

## 1. Instructions for a future AI chat

Attach this Markdown file and use this starting prompt:

> Continue work on my installed Project Variety Citadel maps using this reference. First verify the current installed files and resolve mounted package overrides. Edit the base Project Variety DLC at `C:\Steam\steamapps\common\Mass Effect Legendary Edition\Game\ME3\BioGame\DLC\DLC_MOD_ProjectVariety\CookedPCConsole`, not the Mod Manager library. Preserve the working M-key activation, shared 12-page carousel, correctly aligned vanilla overlays, custom NPC markers, C-Sec floor bool 31255, camera-safe triggers, Narl name, Volus guard-only quest scope, and off-level POI lists. Work from the current files and a fresh revision backup. Do not deploy an old staged package or blindly rerun an initial builder. Read the final shared handler and RemotePOIs source before changing map behavior. Keep map images, calibration, live labels, remote labels, and badge numbering consistent. Stage and validate the concrete change, install only with LE3 closed, then report exactly what changed and what needs an in-game check.

Important project rules:

- The target is the installed **base** `DLC_MOD_ProjectVariety` folder. The user explicitly corrected an earlier assumption about editing the Mod Manager copy.
- Source architecture/NPC/quest packages may resolve from PV2, PV3, Framework, or another DLC. Resolve them before reading. Reading those sources does not mean deploying changes into them.
- Existing source files and build artifacts are in the LegendaryExplorer repository, but the map work is a custom helper project under `Build/`; it is not a normal modification to LegendaryExplorer's application UI.
- Do not assume historical `PV...MapHandler.uc` files or `sources/` snapshots contain the newest shared implementation. See the authority order below.
- The latest confirmed vanilla fix is **explicit Flash frame labels**, not numeric enum-to-string casting. Preserve it.
- A map texture alone is insufficient: map data, movie tag, loaded handler class, rooted movie/texture, layout frame, calibration, and POI data must all agree.
- Inspect before changing gameplay plot state. This implementation reads quest/floor data; it does not set quest flags to make labels appear.
- Back up the current installation before the next change. An older revision's backup is not a valid baseline for a new revision.

## 2. Paths, installed files, and source authority

### Installation and workspace

```text
Game root:
C:\Steam\steamapps\common\Mass Effect Legendary Edition\Game\ME3

Installed base PV DLC:
C:\Steam\steamapps\common\Mass Effect Legendary Edition\Game\ME3\BioGame\DLC\DLC_MOD_ProjectVariety

Installed packages/config:
C:\Steam\steamapps\common\Mass Effect Legendary Edition\Game\ME3\BioGame\DLC\DLC_MOD_ProjectVariety\CookedPCConsole

Workspace:
C:\Users\Scottie\source\repos\LegendaryExplorer

Helper project:
C:\Users\Scottie\source\repos\LegendaryExplorer\Build\pv-archos-map\Map.csproj

Shared carousel sources and revisions:
C:\Users\Scottie\source\repos\LegendaryExplorer\Build\pv-citadel-carousel
```

### Four installed files tracked by the combined manifest

| File | Location relative to base PV DLC | Purpose |
|---|---|---|
| `Startup_DLC_MOD_ProjectVariety_INT.pcc` | `CookedPCConsole/` | Custom and copied vanilla textures, movies, startup references |
| `BioP_CitHub.pcc` | `CookedPCConsole/` | Shared handler/router, individual data/provider classes, map sequences/triggers |
| `Default_DLC_MOD_ProjectVariety.bin` | `CookedPCConsole/` | Coalesced UI movie-library registrations |
| `PCConsoleTOC.bin` | **DLC root**, one directory above CookedPCConsole | Correct byte sizes for changed files |

The custom map images use locally embedded texture mip data in startup. **No change to the Project Variety TFC was required.** The user's permission to edit it was broader than the changes actually needed.

### Authority order

1. The actual installed files, provided their hashes match the current combined manifest.
2. [Combined final manifest](C:/Users/Scottie/source/repos/LegendaryExplorer/Build/pv-archos-map/verification.json). It points to the newest staged copy of each installed file and preserves the original pre-map backups.
3. [Final shared handler, verified from the staged package](C:/Users/Scottie/source/repos/LegendaryExplorer/Build/pv-citadel-carousel/layout-names-fix/validation/PVCitadelMapHandler.verified.uc). This includes remote POI functions and the explicit vanilla layout fix.
4. Editable shared handler template: [PVCitadelMapHandler.uc](C:/Users/Scottie/source/repos/LegendaryExplorer/Build/pv-citadel-carousel/PVCitadelMapHandler.uc), plus [RemotePOIs.uc](C:/Users/Scottie/source/repos/LegendaryExplorer/Build/pv-citadel-carousel/RemotePOIs.uc). `CarouselCorrection.cs` inserts the latter before `defaultproperties` when compiling.
5. [Verified installed router source](C:/Users/Scottie/source/repos/LegendaryExplorer/Build/pv-citadel-carousel/validation/PVCitadelMapRouter.verified.uc). The editable router template has a `__SPECS__` placeholder filled by `CarouselSources.cs`; do not compile the unresolved template.
6. Original shared providers in `Build/pv-citadel-carousel/sources/PVArchosMapHandler.uc`, `PVTowerMapHandler.uc`, `PVTempleMapHandler.uc`, `PVVolusMapHandler.uc`, and `PVCSecMapHandler.uc`. They contain `GetCarouselDrawer` and `CarouselPositions`, added to the previously working handlers. These provider classes were preserved by the later overlay/layout fixes.
7. Individual map folders and older revision READMEs: useful historical evidence, not a current deployment recipe.

### Current staged files are deliberately split across revisions

| Installed file | Current stage directory |
|---|---|
| Startup | `Build/pv-citadel-carousel/overlay-fix/staged/` |
| BioP_CitHub | `Build/pv-citadel-carousel/layout-names-fix/staged/` |
| Default `.bin` | `Build/pv-citadel-carousel/staged/` |
| TOC | `Build/pv-citadel-carousel/layout-names-fix/staged/` |

Do not choose one old `staged/` directory and copy all its packages. Use the combined manifest's per-file entries.

## 3. Final behavior and user confirmations

- M opens the map in custom districts and original Citadel districts.
- Loading directly inside a custom district works; travel into it also works.
- The shared carousel contains six original Citadel maps followed by six custom pages. Keyboard arrows, clickable arrows, and native controller navigation use the same carousel.
- Opening M starts on the player's district/floor. The stock movie hides the player arrow while browsing another district.
- Custom NPC POIs use consecutive red badges on the map and matching badges/names in the drawer.
- Loaded districts use active live actors, current names, and current world positions. Unloaded custom districts use a separate quest-conditioned catalogue, so their lists/markers can still be displayed.
- An empty custom drawer stays empty instead of displaying an uppercase title placeholder. Uppercase **map headings** remain intentional.
- C-Sec bool **31255 false = lower, true = upper**. The map reads this bool; it does not change it.

Recorded user confirmations include:

| Feature | User result |
|---|---|
| Archos travel entry | “Map opens after traveling in” |
| Tower camera and M | “Camera and map both work” |
| Hanar rooms, foyer, marker/camera | Works; elevator was initially excluded |
| Hanar elevator after extension | “Map opens in the elevator” |
| Volus rooms/elevator/marker/camera | “Map, marker, and camera work” |
| C-Sec lower and upper | “Both floors work correctly” |
| Hanar numbered NPC labels | “everything works” |
| C-Sec small badge/name alignment | “Alignment is correct” |
| Archos labels | Looked good; user requested renaming the door POI to Narl, which was done |
| Tower NPC labels | “Everything looks good” |
| Vanilla M and carousel after router | User confirmed M and carousel work |
| Final vanilla overlays after explicit label fix | **“All vanilla markers align”** |

The remote catalogue passed structural/branch/coordinate checks. No separate explicit user confirmation of every off-level NPC list and every quest state was recorded. Do not turn that into a claim of exhaustive in-game testing.

## 4. Runtime architecture and map activation

### Persistent router

`BioP_CitHub.PVCitadelMapRouter` extends Actor. Exactly one router actor was appended to the existing persistent level actor list:

```text
TheWorld.PersistentLevel.PV_CitadelMapRouter
```

It is hidden, non-colliding, non-blocking, and `bNoDelete=true`. `PostBeginPlay` installs a repeating **0.15-second** `UpdateMap` timer and also updates immediately.

`GetPage(Location, bUpper)`:

1. Tests custom district boxes first, pages 6–11.
2. Uses bool 31255 to skip the wrong C-Sec floor page.
3. Tests the six original Citadel boxes.
4. Returns -1 outside covered regions.

`UpdateMap` obtains the local player/controller and SFX UI manager. It skips updates while `WorldInfo.bPlayersOnly` is set. When the page changes or another trigger/native action replaces its assignment, it creates/reassigns suitable map data and then sets:

```unrealscript
Manager.SetAreaMapData(AssignedData);
Manager.MovieTag_AreaMap = 'PVCitadelAreaMap';
```

**Order matters:** `SFXGUIInteraction.SetAreaMapData(non-null)` clears `MovieTag_AreaMap`. Always restore the intended movie tag after assigning data. A valid map object without a matching movie tag previously left M ineffective.

The router keeps an explicit `MapHandlerClass = class'PVCitadelMapHandler'` reference. The early standalone actions also have explicit handler-class references. A class merely named in coalesced may not be loaded with the level; that caused an earlier activation failure.

Outside coverage, the router clears map data only if it still owns the current assignment. It runs as part of Citadel's persistent level rather than replacing global SFXGame logic.

### Shared handler

`BioP_CitHub.PVCitadelMapHandler extends SFXSFHandler_PCAreaMap`:

- Builds `Pages[0..11]` with Asset, Layout, Title, Drawer, and world-position arrays.
- Uses native `SFXAreaMapData` and `GetDrawerText` for original Citadel POI/quest lists.
- Obtains loaded custom NPC data through the five providers' `GetCarouselDrawer`/`CarouselPositions`.
- Chooses remote catalogue data when a custom district's relevant streaming package is not visible.
- Calls `ASSetMapData(Assets, Layouts, Drawers, PlayerPage)`.
- Handles the stock movie's `ExIntUpdateDrawerText(Page)` callback on every page selection, sets the title, and updates/hides the custom marker slots.
- Uses the player's current page calibration for the player arrow.
- Maps keyboard control-left to native `ScrollRight()` and control-right to `ScrollLeft()`; these native function names look reversed but implement previous/next correctly. Preserve the verified behavior.

### Movie registration

In coalesced `bioui.ini`, section `sfxgame.sfxguiinteraction`, `movielibrary` contains:

```text
(Tag=PVCitadelAreaMap,ZOrder=360,MovieClass="BioP_CitHub.PVCitadelMapHandler",GFxResource="GUI_SF_PV_CitadelMap.ME2_AreaMap",HUDTypeToAutoLoadFor="None",CurvePixelError=1.0,UseEdgeAA=true,bAutoStart=true,bAutoVisible=true,bSwfDevAutoReopen=false,bRequiresScaling=true,Platform=CONSOLE_Any,StrokeStyle=SF_MSS_Normal)
```

The five pre-existing custom tags `PVArchosAreaMap`, `PVTowerAreaMap`, `PVTempleAreaMap`, `PVVolusAreaMap`, and `PVCSecAreaMap` also point at this shared handler/movie. This lets old custom triggers cooperate with the router. Keep the original global `AreaMap` registration available for other maps.

## 5. Carousel pages, resources, and layout names

All assets below are in the PV INT startup. Native texture copies allow browsing a district whose package is unloaded.

| Zero-based page | Map | Texture path | Flash layout label |
|---:|---|---|---|
| 0 | Normandy Docks | `PV_AreaMaps.VanillaDocks` | `AM_CitDock` |
| 1 | Human Embassies | `PV_AreaMaps.VanillaEmbassy` | `AM_CitEmb` |
| 2 | Huerta Memorial Hospital | `PV_AreaMaps.VanillaHospital` | `AM_CitHosp` |
| 3 | Purgatory | `PV_AreaMaps.VanillaPurgatory` | `AM_CitPurg` |
| 4 | Presidium Commons | `PV_AreaMaps.VanillaCommons` | `AM_CitCommons` |
| 5 | Refugee Docks | `PV_AreaMaps.VanillaCamps` | `AM_CitCamp` |
| 6 | Archos Lounge | `PV_AreaMaps.ArchosLounge` | `AM_Biop_nor_5` |
| 7 | Citadel Tower | `PV_AreaMaps.CitadelTower` | `AM_Biop_nor_5` |
| 8 | Hanar Embassy | `PV_AreaMaps.HanarEmbassy` | `AM_Biop_nor_5` |
| 9 | Volus Embassy | `PV_AreaMaps.VolusEmbassy` | `AM_Biop_nor_5` |
| 10 | C-Sec HQ lower | `PV_AreaMaps.CSecLower` | `AM_Biop_nor_5` |
| 11 | C-Sec HQ upper | `PV_AreaMaps.CSecUpper` | `AM_Biop_nor_5` |

**Page index 11 is not Flash frame 11.** All custom pages share Flash frame 11, whose label is `AM_Biop_nor_5`; their page index, texture, title, and dynamic positions distinguish them.

The shared movie is `GUI_SF_PV_CitadelMap.ME2_AreaMap`, cloned with dependencies from base-game `BioD_CitHub_Presidium.pcc`, export `GUI_SF_ME2_AreaMap.ME2_AreaMap`.

Relevant native symbols in this exact donor:

| Symbol | Role |
|---|---|
| 141 | Floors timeline; original Citadel frames 1–6, Normandy frames 7–11 |
| 153 | Headers timeline |
| 154 | MapOverlay animation/container |
| 125 at depth 27 | `PlayerPosition`, with inner Player clip |
| 108, 111, 114, 117, 128, 134, 137, 140 | Number artwork 1–8 |

The final movie preserves the native Citadel frames, instance names, placements, and original root action bytecode. On **frame 11 only**, native depths 1, 3, and 5 are removed, and eight separate clips named `PVNum01` through `PVNum08` are placed at depths 101–108. Custom functions touch these PV clips, not native map icon instances. This frame is repurposed only inside the shared Citadel movie; the original Normandy map movie is not replaced.

Do not recreate the earlier implementation that duplicated/hid every stock marker on a camp frame. That was replaced. Its old explanation of stale transforms was not the final confirmed cause of vanilla misalignment.

### Critical vanilla layout fix

Never generate the layout list like this:

```unrealscript
Pages[i].Layout = string(SFXAreaMapLayout(i));
```

The actual compiled source became `string(byte(i))`, sending `"0"` through `"5"`. Flash `gotoAndStop` interpreted numeric strings as **one-based frame numbers**, not enum labels. The hospital texture on page 2 therefore received the Human Embassies overlay on frame 2, including a fifth number. Hiding/restoring icons did not fix that selection error.

Use literal labels, as the installed handler now does:

```unrealscript
Pages[0].Layout = "AM_CitDock";
Pages[1].Layout = "AM_CitEmb";
Pages[2].Layout = "AM_CitHosp";
Pages[3].Layout = "AM_CitPurg";
Pages[4].Layout = "AM_CitCommons";
Pages[5].Layout = "AM_CitCamp";
```

The user confirmed all vanilla markers align after this change. `GetDrawerText(NativeData, SFXAreaMapLayout(i))` still uses the enum's numeric value as a typed function argument and is correct; the problem was specifically converting that value to a Flash layout string.

## 6. Textures, calibration, player position, and heading

### Source images and retained copies

| Area | Original user file | Retained working image(s), relative to workspace |
|---|---|---|
| Archos | `C:\Users\Scottie\Desktop\Screenshot 2026-10-21 011248.png` | `Build/pv-archos-map/ArchosLounge.png`, `.bgra` |
| Tower | `C:\Users\Scottie\Desktop\Tower.png` | `Build/pv-tower-map/Tower.source.png`, `Tower-UI.png`, `Tower.bgra` |
| Hanar | `C:\Users\Scottie\Desktop\Temple.png` | `Build/pv-temple-map/Temple.source.png`, `Temple-UI.png`, `Temple.bgra` |
| Volus | `C:\Users\Scottie\Desktop\vembassy.png` | `Build/pv-volus-map/vembassy.source.png`, `Volus-UI.png`, `Volus.bgra` |
| C-Sec lower | `C:\Users\Scottie\Desktop\CSec_Lower.png` | `Build/pv-csec-map/CSec_Lower.source.png`, `CSecLower-UI.png`, `CSecLower.bgra` |
| C-Sec upper | `C:\Users\Scottie\Desktop\CSec_Upper.png` | `Build/pv-csec-map/CSec_Upper.source.png`, `CSecUpper-UI.png`, `CSecUpper.bgra` |

At documentation time, the two C-Sec originals remained on Desktop; the four earlier Desktop originals were not found at their old paths. The retained Build images are therefore important. Archos's retained PNG is its prepared image, not a separately named original `.source.png`.

The custom textures are 2048×2048, `TEXTUREGROUP_UI`, ARGB/BGRA pixel data, locally stored, with **SRGB=false**. Retain startup ObjectReferencer references to both textures and movies. Import movie dependencies with proper relinking; raw movie bytes alone are not the whole resource.

The six vanilla copies were imported from base-game `AreaMap_CitHub_Docks.pcc`, `AreaMap_CitHub_Embassy.pcc`, `AreaMap_CitHub_Hospital.pcc`, `AreaMap_CitHub_Purgatory.pcc`, `AreaMap_CitHub_Commons.pcc`, and `AreaMap_CitHub_Camps.pcc`. Their existing pixel data, gamma properties, and texture storage references were preserved. Preserve those game texture-storage dependencies when moving the installation.

Preserve image aspect ratio by padding to a square before resizing, rather than stretching the floor plan. The native logical map canvas is **1024×1024**, which differs from the physical 2048 texture resolution.

### Final world-to-image transforms

Let `X,Y` be actor world coordinates, `u,v` be coordinates in source-image pixel units, `W` the square source canvas width, and `P` top padding. Use:

```text
Px = u * 1024 / W
Py = (v + P) * 1024 / W
```

| Page | u | v | W | P |
|---:|---|---|---:|---:|
| 6 Archos | `-1546.52694 - 0.289*X` | `-446.291127 - 0.289*Y` | 2600 | 0 in the stored Archos transform |
| 7 Tower | `5150 - 0.1*X` | `-5682 - 0.1*Y` | 2649 | 522 |
| 8 Hanar | `-17522 - 0.35*X` | `8624 - 0.35*Y` | 2397 | 277 |
| 9 Volus | `-14680 - 0.43*X` | `9420 - 0.43*Y` | 2525 | 404 |
| 10 C-Sec lower | `655 - 0.075*X` | `1000 - 0.075*Y` | 1552 | 231 |
| 11 C-Sec upper | `286 - 0.105*X` | `1584 - 0.105*Y` | 1552 | 231 |

Tower original dimensions are 2649×1604, Hanar 2397×1842, Volus 2525×1716, and each C-Sec source 1552×1090. Odd padding differences put the extra pixel below (Tower 523, Hanar 278, Volus 405). The table's Archos constants already describe the prepared canvas; do not add another padding offset.

The shared handler's `CustomCoordinates` is the active calculation for custom player and NPC arrows. Preserve matching formulas in the individual data classes and router reference generation. `PVCSecMapData.GetPixelCoordinates` directly implements its two separate floor transforms; do not replace its dummy reference pair with native calibration math.

Player heading for custom images:

```text
rotationDegrees = Pawn.Rotation.Yaw * 0.00549316406 - 90
```

Native pages keep their native `GetPixelCoordinates` and heading behavior. The custom -90 adjustment must not be applied globally.

### Flash marker transform

`PVSetNPCMarker(Number, Px, Py, bShow)` positions the PV marker in the same coordinate space as the native PlayerPosition child:

```text
xScale = yScale = 650 / 1024
marker._x = Px * xScale * PlayerPosition._xscale / 100 + PlayerPosition._x
marker._y = Py * yScale * PlayerPosition._yscale / 100 + PlayerPosition._y
```

The donor's PlayerPosition matrix has scale `0.826995849609375` and translation `(-361.6,-271.5)` in the Floors space. Other UI/container transforms apply afterward. Avoid converting screen pixels directly into world-map coordinates.

## 7. District coverage and legacy trigger settings

### Current router boxes

Inclusive `(X,Y,Z)` boxes, evaluated before vanilla boxes:

| Page | Minimum | Maximum |
|---:|---|---|
| 6 | `(-14750,-11500,-33000)` | `(-4750,-1500,-30000)` |
| 7 | `(25500,-77833,58500)` | `(50500,-52833,64500)` |
| 8 | `(-57200,17800,14800)` | `(-48900,25700,17000)` |
| 9 | `(-39800,17000,12400)` | `(-34200,22600,15800)` |
| 10 and 11 | `(-14500,-3500,17500)` | `(5500,16500,23500)` |

For C-Sec, current page selection uses bool 31255, not a Z guess. Its loaded NPC filtering separately uses Z=20550 to distinguish floors. These are different jobs.

Native boxes/calibration are generated from each original district's SetAreaMap reference actors, with a 600 UU horizontal border and 600 UU extension above/below reference heights. Exact numbers are retained in the appendix and `Build/pv-citadel-carousel/validation/page-bounds.json`.

### Earlier native triggers still present

They are retained alongside the router. Their individual movie tags now alias the shared movie/handler. Do not remove them casually or restore their old standalone packages over the current BioP.

| Area | Sequence under `TheWorld.PersistentLevel.Main_Sequence` | Cylinder coverage |
|---|---|---|
| Archos | `PV_ArchosAreaMap` | center `(-9750,-6500,-31500)`, radius 5000, half-height 1500 |
| Tower | `PV_TowerAreaMap` | center `(38000,-65333,61500)`, radius 12500, half-height 3000 |
| Hanar | `PV_TempleAreaMap` | six room cylinders at X=-55100/-53030/-50900, Y=21600/23800, Z=15900, radius 1900; foyer `(-53100,20000,15900)`, radius **2200**; all half-height 1100 |
| Volus | `PV_VolusAreaMap` | center `(-37000,19800,14100)`, radius 2800, half-height 1700 |
| C-Sec | `PV_CSecAreaMap` | center `(-4500,6500,20500)`, radius 10000, half-height 3000 |

Correct native cylinder flags:

```text
CollideActors      = true
BlockNonZeroExtent = true
BlockActors        = false
BlockZeroExtent    = false
CanBlockCamera     = false
```

The actor also enables `bCollideActors`. Touch events use unlimited activations (`MaxTriggerCount=0`); Touched assigns the map, UnTouched clears/restores after genuinely leaving coverage, and Level Is Live covers initial save loading. Hanar's multiple overlapping triggers must not clear the map while Shepard remains inside another embassy trigger.

The Hanar elevator point `(-51425,19676,15921)` is about 1706 UU from the foyer center. An earlier 1500 radius missed it; 2200 covers it. Preserve this extension and matching initial-load bounds.

The camera problem was real: inherited camera blocking caused Shepard to disappear as the camera rotated. Explicitly disable camera/zero-extent blocking while retaining non-zero-extent pawn overlaps.

### Source packages relevant to each custom district

| Area | Architecture/level sources | NPC/quest sources |
|---|---|---|
| Archos | `BioA_CitHub_Chora`, `BioA_CitHub_ChoraBack`; relevant mounted architecture in PV3 | `BioD_CitHub_Chora` (mounted PV2), `BioD_CitHub_Aria1` (PV2) |
| Tower | Base PV `BioA_CitHub_Tower`, `BioA_CitHub_TowerLighting`; PV2 TowerDeco | Base PV `BioD_CitHub_TowerP3`, `BioD_CitHub_Prothean` |
| Hanar | PV3 `BioA_CitHub_Temple`, `BioA_CitHub_Temple_Vista`; PV2 Temple_DECO | Base PV `BioD_CitHub_Temple`, relevant Thane variant |
| Volus | PV3 `BioA_CitHub_VEmbassy`, `BioA_CitHub_VEmbassy2`; base `BioA_CitHub_EmbassyV_Vista` | Base PV `BioD_CitHub_VEmbassy`, `BioD_CitHub_VolusPlot` |
| C-Sec | Base PV `BioA_CitHub_CSec_000Global`, 200Lobby, 300Reception, 325ReceptionB, 350LockersTrans, 400LockersP2, 500CSecLift, 600Cells1/2, 700Desks, 750Vista and streaming | `BioD_CitHub_CSec_LowerP3`, `BioD_CitHub_CSec_UpperP3`, `BioD_CitHub_CSec`, `BioD_CitHub_Bailey` |

Names may have exact suffixes/case such as `_P2`; use mounted resolution rather than reconstructing filenames from this shorthand. During final inspection, native Embassy dialogue resolved from `DLC_MOD_Legion`, Dock/WardsFlux from PV2, Hospital from base PV, Presidium from PV3. All six mounted and base native movies had identical Floors sprite hashes. Future installations can differ.

## 8. NPC labels and numbered markers

### Loaded actor rules

The providers locate active, visible, non-deleted actors inside district bounds. They inspect Tag/UniqueTag and, where supported, current `SFXSimpleUseModule`/`SFXSelectionModule` names. This supports framework appearance replacements whose live tags or names are assigned at runtime.

NPC names use existing TLK string references. Prefer the active actor's current use/selection name when available. Do not hardcode translated display text unnecessarily.

For C-Sec, NPCs must be in X/Y coverage, Z roughly 19000–23000, and on the requested floor (`Z >= 20550` upper). The shared provider accepts page 10/11, so browsing the opposite floor is not incorrectly tied to the player's current floor bool.

Append a drawer row and world position together, then use that array index for both numbers. When an earlier NPC disappears, following entries renumber consecutively. Hide unused marker slots every page change.

### Archos Lounge

| Display | Actor tag / identity | TLK ref | Source notes |
|---|---|---:|---|
| Sayn | `citwrd_sayn` or current matching use name | 663712 | local appearance variants; source `(-11350,-5880,-31210)` |
| Darner Vosque | `citwrd_vosque` | 663450 | global NPC; source `(-9637,-7372,-31243)` |
| Zaeed | `global_zaeed` | 306237 | lounge teleport `(-6250,-7511,-31239)` |
| **Narl** | `SFXPointOfInterest` `poi_blood` in Aria1 | **663139** | door interaction at `(-12159,-6350,-31180)` |

Narl is deliberately the door encounter destination. Do not revert the label to “Door” or the original 307347 label. The live POI requires a targetable, non-deactivated use module. `door_aria1` is the physical door, not the interaction POI. Exclude off-map radio/conversation actors and decorative bodyguards.

### Citadel Tower

| Display | Tag | TLK ref | Source position |
|---|---|---:|---|
| Samara | `global_samara` | 349390 | `(42906,-65292,61015)` |
| Elcor ambassador | `citprs_elcor_ambassador` | current name; remote **334752**, generic fallback 341191 | `(35276,-69919,61527)` |
| Angry Elcor | `cit_fetch_art_code_elcor` | 705022 | `(35343.6,-60776.7,61527.1)` |
| Javik | `hench_prothean` | 524655 fallback | `(37315,-65250,61548)` |

Javik's live marker additionally requires conditionals 22 and 2704 and a targetable selection module, so a following squadmate is not mistaken for an available Tower character moment. Both BioPawn and SFXStuntActor variants are supported. Exclude cinematic bystanders in remote staging.

### Hanar Embassy

| Display | Tag | TLK ref | Source position |
|---|---|---:|---|
| Jondum Bau | `cit_sal_spectre` | 694517 | ordinary destination `(-52373,23406,15921)`; another quest teleport `(-52377,23389,15921)` |
| Kolyat | `global_kolyat` | 302165 | `(-53018,23500,15921)` |

Use active actors for loaded positions. Zymandis and Kasumi references in Temple include conversation staging; no speculative persistent marker was added for them.

### Volus Embassy — exact requested scope

| Display | Tag | TLK ref | Source position |
|---|---|---:|---|
| Diplomat | `cit_fetch_art_obelisk_vol` | 705027 | `(-37337,18943,14491)` |
| Angry Accountant | `cit_fetch_art_book_volus` | 705021 | `(-38358,19470,14491)` |
| Embassy Guard | `citprs_kidnapped_guard` | 695833 | actor `(-36428,18841,14491)` |

The user requested both VEmbassy NPCs and **only** `citprs_kidnapped_guard` from VolusPlot. Do not add the ambassador, radio, office bug, Cerberus contact, Zaeed, or bounty-hunter POIs from that package without a new request.

An earlier note treated `(-35521,21123,14491)` as a guard relocation. Inspection showed that teleport targets **the player**. The remote guard marker therefore uses actor 202's actual source position. Loaded markers still follow the guard's live position.

### C-Sec HQ, both floors

| Display/fallback | Tag | TLK ref | Notes |
|---|---|---:|---|
| Commander Bailey | `global_bailey` | 218732 | use current physical floor/location |
| Khalisah al-Jilani | `citprs_khalisah` | 290612 | moves between lower reception and upper offices |
| C-Sec Assistant | `citwrd_bailey_assistant` | 663711 when resolved | live-only if actually in HQ; the inspected default actor was outside HQ in Presidium, so no speculative remote HQ row |
| Dellk | `fetch_dish_questgiver` | 705032 | PV localization supplies Dellk |
| N7 quest C-Sec officer | `cit_fetch_kron7b_csec` lower / `cit_fetch_gthn7a_turian` upper | 704417 for both | name fallback selects the actor physically on the floor; do not duplicate |
| Mysterious Figure / Sommers | `citdoc_sommers` | 690463 | honor PV's localized “Mysterious Figure” |
| Gung-ho / Reluctant Civilian | `citdoc_gung_ho`, `citdoc_unsure` | 662819 / 662820 | one centered badge when both present |
| Officer Noles | `citprs_batmil_csec_officer` | 619637 | source event spelling includes “Nolles”; displayed name is localized |
| Patient / Angry C-Sec Officers | `citprs_ambis_csec_guard1`, `citprs_ambis_csec_guard2` | 705971 / 705972 | one centered badge |

For paired entries, average the two live world positions and concatenate names with ` / `. If only one actor exists, use its position/name. Maximum custom capacity is eight badge slots per page.

### Confirmed drawer formatting

Use explicit paragraph metrics for **every** row:

```html
<textformat leftmargin="30" rightmargin="0" blockindent="0" indent="-30" leading="2"><p align="left"><b></b>[AreaMap_Label_01] <font color="#ffffff">NPC name</font></p></textformat>
```

The actual token comes from `LocationIcons[zeroBasedIndex]`. The `<b></b>` reset and explicit indentation prevent small badges 1/2 and following rows from drifting. The hanging margin keeps wrapped labels beneath the text column rather than the badge. This fixed the user-reported C-Sec spacing and was confirmed in game. Do not replace it with variable runs of spaces/newlines.

## 9. Off-level POI catalogues and quest conditions

`IsPageLoaded(Page)` checks `oWorldInfo.StreamingLevels` for a visible quest package, with current player page as a fallback:

| Page | Visible package checked |
|---:|---|
| 6 | `BioD_CitHub_Chora` |
| 7 | `BioD_CitHub_TowerP3` |
| 8 | `BioD_CitHub_Temple` |
| 9 | `BioD_CitHub_VEmbassy` |
| 10 | `BioD_CitHub_CSec_LowerP3` |
| 11 | `BioD_CitHub_CSec_UpperP3` |

If loaded, the active-actor provider remains authoritative, including an intentionally empty list. If unloaded, `BuildRemotePage` clears/rebuilds that page's drawer and positions from static source destinations and current plot tests. It never loads a district just to obtain labels.

Notation below: `B(n)=GV.GetBool(n)`, `I(n)=GV.GetInt(n)`, `C(n)=oWorldInfo.CheckConditional(n,0)`; `!` means false/not.

| Page | Remote row | Exact installed availability |
|---:|---|---|
| 6 | Sayn | Always in the unloaded catalogue |
| 6 | Darner Vosque | `C(1151)` |
| 6 | Zaeed | `C(182) && C(1455) && B(21839)` |
| 6 | Narl | `C(1148)` |
| 7 | Samara | `C(181) && I(10195)>0 && !B(19303) && !B(22155)` |
| 7 | Elcor ambassador, Angry Elcor | Always in unloaded catalogue |
| 7 | Javik | `C(22) && C(2704)` |
| 8 | Jondum Bau | `!B(21196) && B(21189) && !B(31274) && C(1454)` |
| 8 | Kolyat | `I(11153)>=2 && B(185) && C(574) && !B(23950)` |
| 9 | Diplomat, Angry Accountant | Always in unloaded catalogue |
| 9 | Embassy Guard | `!B(21200)` |
| 10 | Khalisah lower | `B(20207) && !B(22099)` |
| 10 | Dellk and lower N7 officer | `C(801)` |
| 10 | Sommers | `C(801) && !B(19607)` |
| 10 | Gung-ho + Reluctant pair | `C(801) && !B(19607) && !B(21062)` |
| 10 | Gung-ho alone | `C(801)` and the paired condition is false |
| 11 | Bailey upper | Always in unloaded catalogue |
| 11 | Khalisah upper | `!B(20207) && !C(420)` |
| 11 | Upper N7 officer, Noles, Patient/Angry pair | `C(801)` |

Conditions are the installed implementation, not a promise that every possible future PV quest modification will match it. If PV quest files change, re-inspect their source sequences and update the remote catalogue. Loaded actors still override these static assumptions whenever the relevant level is visible.

Additional remote destinations not already listed above:

| Row | World destination |
|---|---|
| Khalisah lower | `(-4955,3882,19991)` |
| Dellk | `(-2675,11300,19991)` |
| Lower N7 officer | `(-3719,5421,19991)` |
| Sommers | `(-2988,5950,20006)` |
| Gung-ho alone | `(-1147,1238,19791)` |
| Gung-ho / Reluctant midpoint | `(-1098.5,1245,19791)` |
| Bailey upper | `(-2253,8159,21091)` |
| Khalisah upper | `(-2198,8476,21091)` |
| Upper N7 officer | `(-4400,7039,21191)` |
| Noles | `(-3593,11142,21191)` |
| Patient / Angry midpoint | `(-6727,9173,21191)` |

`Build/pv-citadel-carousel/test_remote.py` executes the implemented catalogue's conditional subset across **714 plot combinations**, checks limits/unique primary names/map bounds/floor heights, and compares **21 source-export coordinates** with inspection dumps. These tests validate the implemented branches and copied locations; they do not independently prove all gameplay availability rules.

## 10. Build and inspection tools

Run commands from the workspace in **PowerShell 7**. The helper is net10.0, x64, and references the already working LEC/dependency DLLs under `Build/save-menu-search/bin/Debug/net10.0`. Preserve those DLLs. It loads Oodle from `Game/ME3/Binaries/Win64/oo2core_8_win64.dll`. The game path is hardcoded in `Program.cs`; update it if moving installations.

```powershell
Set-Location -LiteralPath 'C:\Users\Scottie\source\repos\LegendaryExplorer'
dotnet build Build/pv-archos-map --no-restore -p:UseAppHost=false -v q
if ($LASTEXITCODE -ne 0) { throw 'Helper build failed' }
```

Do not rebuild the entire LegendaryExplorerCore/application just to run these map tools. The helper intentionally uses the previously working library binaries.

### Read-only game inspection modes

These can create diagnostic output in the workspace, but do not deploy game changes:

```powershell
# Resolve currently mounted package winners, including DLC overrides.
dotnet Build/pv-archos-map/bin/Debug/net10.0/Map.dll resolve BioD_CitHub_Temple.pcc BioD_CitHub_Chora.pcc

# Dump selected export indices, class-name fragments, or object-name fragments.
# Replace this example path with the actual resolved file for your installation.
dotnet Build/pv-archos-map/bin/Debug/net10.0/Map.dll dump 'C:\Steam\steamapps\common\Mass Effect Legendary Edition\Game\ME3\BioGame\DLC\DLC_MOD_ProjectVariety\CookedPCConsole\BioD_CitHub_Temple.pcc' 144 150 340 342 346

# Inspect package exports/properties and decompile requested classes.
dotnet Build/pv-archos-map/bin/Debug/net10.0/Map.dll inspect 'C:\Steam\steamapps\common\Mass Effect Legendary Edition\Game\ME3\BioGame\DLC\DLC_MOD_ProjectVariety\CookedPCConsole\BioP_CitHub.pcc' PVCitadelMapHandler PVCitadelMapRouter

# Search installed INT talk tables. Numeric arguments match exact string IDs.
dotnet Build/pv-archos-map/bin/Debug/net10.0/Map.dll tlk-search 663139 694517

# Compare all six mounted/base native GFx floor layouts.
dotnet Build/pv-archos-map/bin/Debug/net10.0/Map.dll native-overlay-inspect

# Find physical actors by tag/archetype among relevant mounted packages.
dotnet Build/pv-archos-map/bin/Debug/net10.0/Map.dll poi-scan citwrd_bailey_assistant
```

Other useful modes:

| Mode | Input / result |
|---|---|
| `gfx <raw.gfx>` | Writes decoded actions and deep function dump under `Build/pv-archos-map/inspect` |
| `scene <raw.gfx>` | Prints sprite frames, labels, instance names, depths, matrices |
| `classcheck <package>` | Parses each class binary and prints OK/BAD; inspect output, not just exit status |
| `geometry <package>` | Writes actor/component locations and mesh references |
| `mesh-full <package>` | Resolves imported meshes/collection transforms for calibration |
| `live <PID> <output-name.json>` | Read-only process snapshot through `MapLiveProbe.cs`; can be large |
| `carousel-inspect` | Collects original native calibration, writes workspace inspection data |

`inspect` and `gfx` reuse output filenames. Copy important diagnostics into the new revision directory before another inspection overwrites them. Some generic diagnostic modes are less strictly error-handled than builder modes; always inspect errors/output and ensure a failed build did not leave an older helper binary being run.

### Source-file map

All C# paths below are under `Build/pv-archos-map/` unless stated otherwise.

| File | Responsibility |
|---|---|
| `Program.cs`, `Map.csproj` | CLI modes, runtime setup, dependency references |
| `MapBuilder.cs` | Original texture/movie import and reusable `SaveAndRepair` |
| `SequenceBuilder.cs`, `TowerSequenceBuilder.cs`, `TempleSequenceBuilder.cs`, `VolusSequenceBuilder.cs`, `CSecSequenceBuilder.cs` | Original native trigger/action construction |
| `HandlerBuilder.cs`, `GammaBuilder.cs`, `EntryBuilder.cs`, `CameraBuilder.cs`, `TempleElevatorBuilder.cs` | Historical focused fixes |
| `TempleLabelsBuilder.cs` | Original custom label movie/handler revisions for all five areas |
| `CSecDrawerBuilder.cs` | Drawer alignment fix and Archos Narl rename |
| `CarouselBuilder.cs` | Native calibration inspection |
| `CarouselSources.cs` | Router Specs generation and provider method generation |
| `CarouselInstall.cs` | Initial shared router/movie/config installation |
| `CarouselMovie.cs` | Final isolated custom frame and AVM1 marker/title functions |
| `CarouselTests.cs` | Movie function execution/placement tests |
| `CarouselCorrection.cs` | Fresh revision staging, shared-handler correction, preservation checks, TOC/manifest generation, native label verification |
| `NativeOverlayInspect.cs` | Mounted/base native movie comparison |
| `MeshExtractor.cs` | Geometry resolution for calibration |
| `Build/pv-citadel-carousel/PVCitadelMapHandler.uc` | Editable current shared-handler body |
| `Build/pv-citadel-carousel/RemotePOIs.uc` | Remote catalogue/helper functions appended to shared class |
| `Build/pv-citadel-carousel/PVCitadelMapRouter.uc` | Router template; `__SPECS__` must be expanded |
| `Build/pv-citadel-carousel/test_remote.py` | Remote branch and source-coordinate checks |

### Historical builder modes — do not blindly rerun

`build`, `sequence-build`, `handler-build`, `gamma-build`, `entry-build`, `tower-build`, `camera-build`, `temple-build`, `temple-elevator-build`, `volus-build`, `csec-build`, `temple-labels-build`, `csec-labels-build`, `csec-drawer-build`, `archos-labels-build`, `archos-narl-build`, `tower-labels-build`, `volus-labels-build`, `carousel-build`, `carousel-correct`, and `carousel-layout-fix` target fixed historical revision directories/baselines.

Most reject a current installation whose hash differs from their old backup. This is intentional protection. Do not bypass that guard, overwrite an old backup, or assume running the modes in order is a supported fresh rebuild. The code evolved between revisions. Make a **new revision directory and a new entry point** for future work.

## 11. How to change or extend the implementation

### Change a custom map image

1. Copy the source into the appropriate working folder; preserve the old source/prepared image.
2. Determine square padding and fit world landmarks against exported floor/room meshes. Collection actors and imported meshes require their full transforms; `mesh-full` exists for this reason.
3. Prepare a 2048-square BGRA image with preserved aspect ratio and `SRGB=false`.
4. Replace only the intended startup texture, preserving identity/references where possible. Keep it rooted in startup's ObjectReferencer.
5. If crop, scale, rotation, or padding changed, update `CustomCoordinates`, the area's MapData implementation, router reference generation, and calibration metadata together. Legacy action references must remain consistent if retained.
6. Test the player marker and multiple NPC landmarks at widely separated points, including image edges. A visually correct texture does not prove coordinates are correct.
7. Save with local mip-offset repair; update TOC and manifest; validate; deploy with LE3 closed.

### Add or rename an NPC/quest POI

1. Resolve the mounted quest package and NPC appearance package. Inspect Tag/UniqueTag, active/hidden/deleted state, use/selection name, quest activation/teleport actions, and destination links.
2. Distinguish a live NPC, cinematic staging actor, player teleport, physical door, and interaction POI. Do not infer ownership of a teleport from nearby conversation names.
3. Choose an existing TLK name or a deliberate user-requested override. Preserve Narl's override and Volus's exact scope.
4. Update the **loaded provider** for that area, including `GetCarouselDrawer` and its world-position array. If regenerating through `ProviderSource`, verify it retains all current helper methods. Compiling an old `.labels.uc` directly can remove the carousel provider methods.
5. Update **RemotePOIs.uc** as well: availability, source position, floor, localized name, and ordering. Changing the loaded provider alone will leave off-level browsing inconsistent.
6. Keep the drawer and position appends coupled. Pair nearby actors intentionally rather than allowing overlapping badges. Preserve hanging-indent HTML. Do not exceed eight custom markers per page without expanding both movie and handler.
7. Compile the affected provider class from current source. `CarouselCorrection.Correct` by itself recompiles the **shared handler only**, not all providers.
8. Recompile the combined shared handler when remote data changes. Re-run catalogue/source-coordinate checks and test both loaded and unloaded views in game.

### Add a new district/page

This requires more than importing a texture:

1. Define the source image, world region, height coverage/elevators, calibration, heading, and NPC/quest scope.
2. Add/root its startup texture and any new movie artwork.
3. Add/compile its MapData/provider classes and explicit class-loading references.
4. Extend router Specs, GetPage's loops/priorities, and any CDO/page counts. Avoid overlapping nearby district boxes or swallowing unrelated levels.
5. Extend shared `Pages.Length`, texture/title/layout assignments, player-page fallback, `CustomCoordinates`, and `ExIntUpdateDrawerText`/loop limits if necessary.
6. Extend `IsPageLoaded`, loaded provider collection, remote catalogue, tests, and the page table in this document.
7. For custom artwork, the isolated shared frame may still serve the new page. Do not use numeric layout strings. Do not replace global Normandy maps.
8. If keeping individual native triggers, use the verified cylinder flags and initial-load/travel coverage; ensure their aliases select the shared handler/movie. The persistent router must remain able to reconcile external assignments.
9. Validate all original pages and existing custom areas after the extension.

### Start a fresh focused shared-handler revision

For a future code change, add a new CLI case in `Program.cs` with a fresh path, for example:

```csharp
CarouselBuilder.Correct(
    Path.GetFullPath("Build/pv-citadel-carousel/YOUR-NEW-REVISION"),
    game,
    handlerOnly: true);
```

Use `handlerOnly:false` only when intentionally rebuilding the shared movie too. This helper appends RemotePOIs, recompiles the existing shared class, saves/repairs packages, validates preservation, updates TOC, and writes a revision manifest/deploy script. It does **not** add a new router, recompile providers, import a new image, or update movie-library aliases for you. Extend it deliberately for those tasks.

Its generated deployment script is copied from the older common script. Use PowerShell 7. For PowerShell 5 compatibility the JSON assignment should be `$taskFiles = Get-Content ... | ConvertFrom-Json`, without wrapping the command in an extra `@(...)`; older behavior wrapped the returned JSON array as one item and broke GetFullPath before deployment. Also correct the copied success message so it names the actual revision.

## 12. Package saving, validation, and deployment

### Local texture bulk-offset repair

Package growth changes absolute offsets of locally stored texture mip data. A normal package save is insufficient for these custom builds. Reuse `MapBuilder.SaveAndRepair(IMEPackage p, string dest)`:

1. Save compressed to `dest + ".first.pcc"`.
2. Reopen that saved file from disk.
3. For every `Texture2D` and `LightMapTexture2D`, inspect non-empty locally stored mips.
4. Compute the offset field inside export data:

```csharp
int off = export.propsEnd() + mip.MipInfoOffsetFromBinStart + 12;
int absoluteBulkOffset = export.DataOffset + off + 4;
```

5. Write the little-endian absolute offset into that field, then save the final file.
6. Reopen the final file and verify every repaired field, plus unchanged pixel bytes.

Texture data differences caused solely by those absolute offset repairs are expected during preservation comparison. Normalize only those fields before comparing unrelated texture exports; do not ignore arbitrary texture differences.

### Compile and preserve

Use `UnrealScriptCompiler.CompileClass` with the existing class export, `UnrealScriptOptionsPackage.GamePathOverride`, a PackageCache, and an initialized FileLib. Fail on compiler errors; reopen/decompile the staged class and verify the actual generated behavior. The enum-to-string bug demonstrates why successful compilation alone is insufficient.

Check existing export identity: full path, class index, parent/link, flags, and data. Allow only intended script/property changes plus repaired texture offsets. Preserve existing actor-list membership; when adding a router, exactly one intended actor should be added, not a replacement actor list.

### TOC

Use `TOCBinFile` to find each changed `CookedPCConsole/<file>` entry and write the new byte length at `entry.offset + 4`. The TOC belongs in the **DLC root**. Keep it coherent with the package/config set being deployed.

### Revision manifest contract

Each `verification.json` entry contains:

```text
Source          installed destination
SourceSHA256    expected original hash for this revision
Backup          exact pre-revision copy
Staged          new validated file
StagedSHA256    expected new hash
```

The deploy script checks LE3 is closed, confines destinations to the requested base PV root, validates current/backup/stage hashes, copies files, validates installed hashes, and rolls back files already copied if an error occurs. Do not remove those guards to force a stale revision through.

The filesystem sandbox used during this chat allowed workspace writes but required scoped elevated approval for installed game writes. In a future AI environment, follow its current permission tools; do not assume workspace permission covers Steam's game directory. There is no need to terminate the game without the user's authorization. The usual workflow was for the user to save/close LE3 before installation.

Example **for a future validated revision only**, with LE3 closed:

```powershell
& 'C:\Users\Scottie\source\repos\LegendaryExplorer\Build\pv-citadel-carousel\YOUR-NEW-REVISION\Deploy.ps1'
```

After a successful install, update the combined manifest by matching normalized full destination paths. Replace only its **Staged** and **StagedSHA256** fields for changed files; preserve its original **Backup** and **SourceSHA256** entries. Validate all installed hashes again. Earlier entries use mixed slash styles, so compare `[IO.Path]::GetFullPath(...)`, not raw strings.

## 13. Backups, rollback, and preserving the project

**Reference commands below are not an instruction to restore now.** Restoring is a separate action that removes changes.

### Restore all work from this map project

With LE3 closed, the combined original manifest and original backups support:

```powershell
& 'C:\Users\Scottie\source\repos\LegendaryExplorer\Build\pv-archos-map\Deploy.ps1' -Restore
```

This restores the four original pre-map files, including the original TOC. It is not a “remove only the latest label” operation. If other tools/mod updates have since changed these files, the hash guard should stop it; compare and merge rather than forcing the restore.

### Focused recent revision restores

| Revision directory | Restore returns to |
|---|---|
| `Build/pv-citadel-carousel/layout-names-fix/` | Immediately before explicit native frame labels; vanilla misalignment can return |
| `Build/pv-citadel-carousel/overlay-fix/` | Before custom-frame isolation and unloaded POI catalogues |
| `Build/pv-citadel-carousel/` | Before shared carousel/router, retaining earlier individual maps/labels |

Historical per-map snapshots similarly restore the state before that addition/fix, not the latest unrelated features. Restore in reverse revision order when deliberately walking backward. Never use an older backup as a shortcut to remove one feature while keeping newer package changes.

### Preserve these files outside disposable build cleanup

- This Markdown file.
- All five `Build/pv-*-map` directories and `Build/pv-citadel-carousel`.
- The combined and per-revision manifests, backup/staged packages, validation files, prepared/source images, `.bgra` data, calibration JSON and scripts.
- The working dependency DLLs in `Build/save-menu-search/bin/Debug/net10.0` and the helper source/project.
- Original relevant game/mod package versions if future rebuilds must be reproducible. Imported base textures can still reference installed game texture storage.

`Build/` content may be ignored by Git or removed by cleanup. A repository checkout or this Markdown alone is not a replacement for the package backups, image assets, and working binaries. For transfer to another PC, preserve the folder tree and adapt all absolute paths/game-root constants.

## 14. Troubleshooting and fixes that must be retained

| Symptom | Check / established fix |
|---|---|
| M does nothing everywhere in original Citadel districts | Router exists/in actor list, player page resolves, valid MapData, handler class loaded, movie aliases correct; set movie tag **after** SetAreaMapData |
| Works only when initially loading inside custom district | Initial-load path alone is insufficient; check repeatable touch/overlap flags and router region reconciliation |
| Shepard disappears when camera rotates | Trigger cylinder must explicitly set `CanBlockCamera=false`, `BlockZeroExtent=false`; preserve non-zero-extent overlaps |
| Map too dark | Compare texture SRGB with native UI textures; final custom maps use **false**. The historical gamma fix changed the flag, not image pixels |
| Hanar map absent inside elevator | Preserve foyer radius **2200** and matching load/router coverage |
| Wrong C-Sec floor | Bool **31255** selects map page; false lower, true upper. Ensure both images/calibrations and CDO Specs remain present |
| C-Sec NPC on wrong floor | Actor filtering uses Z threshold 20550; remote tables have explicit floor positions. Do not replace page selection bool with NPC Z logic |
| Native numbers/elevator/shop icons displaced, extra fifth hospital badge | Verify **compiled literal layout names**. Numeric enum strings select wrong one-based Flash frame; this was the final confirmed cause |
| Custom markers leak onto original maps | Keep PVNum instances on isolated frame 11; original frames/instances should not be manipulated by custom marker code |
| Small red drawer badges uneven, especially 1/2; wrapped names start under badges | Use explicit textformat/p metrics on every row and hanging indent; retain `<b></b>` reset and consistent badge/text space |
| Off-level custom page only displays uppercase district name | Do not add an uppercase title fallback to empty drawers; retain remote catalogue build when district is unloaded |
| Off-level list empty or stale | Check visible streaming package detection, source quest gates, and catalogue data; don't mistake an inactive loaded level for an unloaded one |
| Loaded NPC markers missing | Check actor Tag/UniqueTag, current use-name fallback, active/hidden state, bounds/floor, source teleport and framework appearance replacement |
| Texture corruption after adding script/classes | Validate local mip bulk offsets after save, startup references, texture storage dependencies, and DLC TOC sizes |
| Builder says installed file changed | Expected when using an old baseline. Create a fresh revision from current files; don't overwrite its backup/hash guard |

The final two carousel fixes are distinct: `overlay-fix` isolates custom artwork and adds off-level lists; `layout-names-fix` fixes native frame selection. The latter is what resolved the user-confirmed vanilla misalignment.

## 15. Verification checklist and limits

For future functional changes, scale checks to the change, but preserve these established contracts:

1. Compile changed classes, reopen staged packages, decompile, and inspect actual assignments/conditions. Parse all class binaries.
2. Resolve six literal vanilla labels against both Floors and Headers timelines: pages 0–5 must select frames 1–6 respectively. Reject `Layout = string(byte(i))`.
3. Check custom badge identity, marker math, visibility, title changes, and transitions to/from vanilla. Preserve native placements/names and original action prefix.
4. Check all intended live and remote labels, consecutive numbering, pair positions, eight-slot limit, map coordinate bounds, and floor filtering.
5. Check router Specs count/order, custom priority, current district centers, native reference points, and handler-loading references.
6. Compare unchanged texture pixels, package exports/identities, actor membership, config sections, and local bulk offsets.
7. Verify TOC lengths and every installed/staged/backup hash required by the revision.
8. In game: load inside an area, travel in from another area, exit/re-enter, test elevators and boundaries, rotate camera, open M on both C-Sec floors, browse native/custom pages both directions, and inspect marker/list alignment.
9. Test remote lists from an unrelated district and compare them with loaded actors after travel. Exercise relevant quest transitions where a save is available.

Existing validation artifacts:

- `Build/pv-citadel-carousel/validation/router-tests.json`: native reference and district-center routing checks.
- `Build/pv-citadel-carousel/overlay-fix/validation/movie-transition-tests.json`: 24 forward/reverse page cases and marker behavior checks.
- `Build/pv-citadel-carousel/overlay-fix/validation/remote-catalogue-tests.json`: 714 implemented plot-branch combinations and 21 source-coordinate checks.
- `Build/pv-citadel-carousel/layout-names-fix/validation/native-layout-label-tests.json`: actual compiled six literal labels resolving to correct Floors/Headers frames.
- Each revision's `preservation.json`, compile log, verified `.uc`, and manifest.
- `Build/pv-citadel-carousel/native-overlay-inspect/comparison.json`: mounted/base native layout comparison.

The movie tests are a small AVM1 interpreter/structural model, not the game's Flash renderer. Remote branch tests execute implemented logic, not a full quest simulator. Final in-game confirmation is separate evidence; the last user result specifically confirmed every vanilla marker aligns.

## 16. Final installed fingerprints

The table below was read from the actual installed files while creating this reference. Each matched the combined manifest, and all four original backup hashes also matched. Future legitimate changes will change these values; re-read the manifest rather than assuming these stay current forever.

| File | Bytes | SHA-256 |
|---|---:|---|
| `Startup_DLC_MOD_ProjectVariety_INT.pcc` | 33,385,143 | `D6B3DD342B68E744C12CDE92AFFB3689B5DFD91AE3FC639B36E4C381580F5462` |
| `BioP_CitHub.pcc` | 21,403,462 | `764052FA6C9D6AB177A4E598D2F80BBBBBB4F69693D87A08C7DCE9D937CC7B2D` |
| `Default_DLC_MOD_ProjectVariety.bin` | 747,583 | `71852EE9835CFFC6233407590B4A098399111C08A597CAF4FD80EB5AFDDD2DB4` |
| `PCConsoleTOC.bin` | 49,220 | `9ACEE5B860D8D56E04FBCDC5A305FDB4AA89EA700A6655ABD41C70479EF2C3A6` |

## 17. Appendix: complete final shared handler

The following is the **decompiled staged package source** from the final `layout-names-fix`, included so a future chat can understand the implementation even without the workspace. Editable source is split between the main template and RemotePOIs.uc. Decompilation may show typed enum arguments as byte casts; preserve the explicit string layout assignments.

```unrealscript
Class PVCitadelMapHandler extends SFXSFHandler_PCAreaMap
    config(UI);

struct CarouselPage 
{
    var string Asset;
    var string Layout;
    var string Title;
    var string Drawer;
    var array<Object.Vector> Positions;
};

var transient array<PVCitadelMapHandler.CarouselPage> Pages;
var transient int PlayerPage;
var transient int ViewedPage;
var transient PVArchosMapHandler Archos;
var transient PVTowerMapHandler Tower;
var transient PVTempleMapHandler Temple;
var transient PVVolusMapHandler Volus;
var transient PVCSecMapHandler CSec;

public event function bool HandleInputEvent(BioDefine.BioGuiEvents Event, optional float fValue = 1.0)
{
    if (Event == BioGuiEvents.BIOGUI_EVENT_CONTROL_LEFT)
    {
        ScrollRight();
        return TRUE;
    }
    if (Event == BioGuiEvents.BIOGUI_EVENT_CONTROL_RIGHT)
    {
        ScrollLeft();
        return TRUE;
    }
    return Super(SFXSFHandler_AreaMap).HandleInputEvent(Event, fValue);
}
public function ASSetPage(string Title, bool bCustom)
{
    ActionScriptVoid("PVSetPage");
}
public function ASSetNPCMarker(int Number, float Px, float Py, bool bShow)
{
    ActionScriptVoid("PVSetNPCMarker");
}
public function CustomCoordinates(int Page, Object.Vector Loc, out float Px, out float Py)
{
    switch (Page)
    {
        case 6:
            Px = (-1546.52698 - 0.289000005 * Loc.X) * (1024.0 / 2600.0);
            Py = (-446.291138 - 0.289000005 * Loc.Y) * (1024.0 / 2600.0);
            break;
        case 7:
            Px = (5150.0 - 0.100000001 * Loc.X) * (1024.0 / 2649.0);
            Py = (-5682.0 - 0.100000001 * Loc.Y + 522.0) * (1024.0 / 2649.0);
            break;
        case 8:
            Px = (-17522.0 - 0.349999994 * Loc.X) * (1024.0 / 2397.0);
            Py = (8624.0 - 0.349999994 * Loc.Y + 277.0) * (1024.0 / 2397.0);
            break;
        case 9:
            Px = (-14680.0 - 0.430000007 * Loc.X) * (1024.0 / 2525.0);
            Py = (9420.0 - 0.430000007 * Loc.Y + 404.0) * (1024.0 / 2525.0);
            break;
        case 10:
            Px = (655.0 - 0.075000003 * Loc.X) * (1024.0 / 1552.0);
            Py = (1000.0 - 0.075000003 * Loc.Y + 231.0) * (1024.0 / 1552.0);
            break;
        case 11:
            Px = (286.0 - 0.104999997 * Loc.X) * (1024.0 / 1552.0);
            Py = (1584.0 - 0.104999997 * Loc.Y + 231.0) * (1024.0 / 1552.0);
            break;
        default:
    }
}
public function ExIntUpdateDrawerText(int Page)
{
    local int i;
    local float Px;
    local float Py;
    
    if (Page < 0 || Page >= Pages.Length)
    {
        return;
    }
    ViewedPage = Page;
    ASSetPage(Pages[Page].Title, Page >= 6);
    if (Page >= 6)
    {
        for (i = 0; i < 8; i++)
        {
            if (i < Pages[Page].Positions.Length)
            {
                CustomCoordinates(Page, Pages[Page].Positions[i], Px, Py);
                ASSetNPCMarker(i + 1, Px, Py, TRUE);
                continue;
            }
            ASSetNPCMarker(i + 1, 0.0, 0.0, FALSE);
        }
    }
}
public function ProcessPlayer(SFXAreaMapData oMapData)
{
    local BioPlayerController PC;
    local float Px;
    local float Py;
    local float Angle;
    
    PC = oWorldInfo.GetLocalPlayerController();
    if (PC == None || PC.Pawn == None || oMapData == None || oPanel == None)
    {
        return;
    }
    if (PlayerPage >= 6)
    {
        CustomCoordinates(PlayerPage, PC.Pawn.location, Px, Py);
        Angle = -90.0;
    }
    else
    {
        oMapData.GetPixelCoordinates(PC.Pawn.location.X, PC.Pawn.location.Y, Px, Py);
    }
    ASSetPlayerLocRot(Px, Py, float(PC.Pawn.Rotation.Yaw) * 0.00549316406 + Angle);
}
public function SetMap(SFXAreaMapData oMapData)
{
    local SFXAreaMapData NativeData;
    local array<string> Assets;
    local array<string> Layouts;
    local array<string> Drawers;
    local BioPlayerController PC;
    local int i;
    
    Pages.Length = 12;
    NativeData = new Class'SFXAreaMapData';
    Pages[0].Asset = "PV_AreaMaps.VanillaDocks";
    Pages[1].Asset = "PV_AreaMaps.VanillaEmbassy";
    Pages[2].Asset = "PV_AreaMaps.VanillaHospital";
    Pages[3].Asset = "PV_AreaMaps.VanillaPurgatory";
    Pages[4].Asset = "PV_AreaMaps.VanillaCommons";
    Pages[5].Asset = "PV_AreaMaps.VanillaCamps";
    Pages[0].Title = GetUIString($589197);
    Pages[1].Title = GetUIString($589195);
    Pages[2].Title = GetUIString($590224);
    Pages[3].Title = GetUIString($723592);
    Pages[4].Title = GetUIString($589196);
    Pages[5].Title = GetUIString($677977);
    // Flash expects frame labels. Casting the enum's byte to string produces
    // "0".."5", which selects one-based frame numbers instead of these labels.
    Pages[0].Layout = "AM_CitDock";
    Pages[1].Layout = "AM_CitEmb";
    Pages[2].Layout = "AM_CitHosp";
    Pages[3].Layout = "AM_CitPurg";
    Pages[4].Layout = "AM_CitCommons";
    Pages[5].Layout = "AM_CitCamp";
    for (i = 0; i < 6; i++)
    {
        Pages[i].Drawer = GetDrawerText(NativeData, byte(i));
    }
    Pages[6].Asset = "PV_AreaMaps.ArchosLounge";
    Pages[7].Asset = "PV_AreaMaps.CitadelTower";
    Pages[8].Asset = "PV_AreaMaps.HanarEmbassy";
    Pages[9].Asset = "PV_AreaMaps.VolusEmbassy";
    Pages[10].Asset = "PV_AreaMaps.CSecLower";
    Pages[11].Asset = "PV_AreaMaps.CSecUpper";
    Pages[6].Title = "ARCHOS LOUNGE";
    Pages[7].Title = "CITADEL TOWER";
    Pages[8].Title = "HANAR EMBASSY";
    Pages[9].Title = "VOLUS EMBASSY";
    Pages[10].Title = "C-SEC HQ: LOWER LEVEL";
    Pages[11].Title = "C-SEC HQ: UPPER LEVEL";
    Archos = new Class'PVArchosMapHandler';
    Tower = new Class'PVTowerMapHandler';
    Temple = new Class'PVTempleMapHandler';
    Volus = new Class'PVVolusMapHandler';
    CSec = new Class'PVCSecMapHandler';
    Archos.oWorldInfo = oWorldInfo;
    Tower.oWorldInfo = oWorldInfo;
    Temple.oWorldInfo = oWorldInfo;
    Volus.oWorldInfo = oWorldInfo;
    CSec.oWorldInfo = oWorldInfo;
    Pages[6].Drawer = Archos.GetCarouselDrawer(6);
    Pages[6].Positions = Archos.CarouselPositions;
    Pages[7].Drawer = Tower.GetCarouselDrawer(7);
    Pages[7].Positions = Tower.CarouselPositions;
    Pages[8].Drawer = Temple.GetCarouselDrawer(8);
    Pages[8].Positions = Temple.CarouselPositions;
    Pages[9].Drawer = Volus.GetCarouselDrawer(9);
    Pages[9].Positions = Volus.CarouselPositions;
    Pages[10].Drawer = CSec.GetCarouselDrawer(10);
    Pages[10].Positions = CSec.CarouselPositions;
    Pages[11].Drawer = CSec.GetCarouselDrawer(11);
    Pages[11].Positions = CSec.CarouselPositions;
    PC = oWorldInfo.GetLocalPlayerController();
    PlayerPage = Class'PVCitadelMapRouter'.static.GetPage(PC.Pawn.location, oWorldInfo.GetGlobalVariables().GetBool(31255));
    if (PlayerPage < 0)
    {
        PlayerPage = int(oMapData.Floor);
    }
    for (i = 0; i < 12; i++)
    {
        if (i >= 6)
        {
            Pages[i].Layout = "AM_Biop_nor_5";
            if (!IsPageLoaded(i))
            {
                BuildRemotePage(i);
            }
        }
        Assets.AddItem(Pages[i].Asset);
        Layouts.AddItem(Pages[i].Layout);
        Drawers.AddItem(Pages[i].Drawer);
    }
    ASSetMapData(Assets, Layouts, Drawers, PlayerPage);
    ExIntUpdateDrawerText(PlayerPage);
}
public function bool IsPageLoaded(int Page)
{
    local LevelStreaming Stream;
    local Name PackageName;
    
    switch (Page)
    {
        case 6:
            PackageName = 'BioD_CitHub_Chora';
            break;
        case 7:
            PackageName = 'BioD_CitHub_TowerP3';
            break;
        case 8:
            PackageName = 'BioD_CitHub_Temple';
            break;
        case 9:
            PackageName = 'BioD_CitHub_VEmbassy';
            break;
        case 10:
            PackageName = 'BioD_CitHub_CSec_LowerP3';
            break;
        case 11:
            PackageName = 'BioD_CitHub_CSec_UpperP3';
            break;
        default:
    }
    foreach oWorldInfo.StreamingLevels(Stream, )
    {
        if (Stream != None && Stream.PackageName == PackageName && Stream.bIsVisible)
        {
            return TRUE;
        }
    }
    return Page == PlayerPage;
}
public function AddRemotePOI(int Page, stringref NameRef, Object.Vector Position, optional stringref OtherNameRef)
{
    local string Label;
    local int Number;
    
    Number = Pages[Page].Positions.Length;
    if (Number >= 8)
    {
        return;
    }
    Label = GetUIString(NameRef);
    if (OtherNameRef != $0)
    {
        Label $= " / " $ GetUIString(OtherNameRef);
    }
    Pages[Page].Drawer $= "<textformat leftmargin=\"30\" rightmargin=\"0\" blockindent=\"0\" indent=\"-30\" leading=\"2\"><p align=\"left\"><b></b>" $ LocationIcons[Number] $ " <font color=\"#ffffff\">" $ Label $ "</font></p></textformat>";
    Pages[Page].Positions.AddItem(Position);
}
public function BuildRemotePage(int Page)
{
    local BioGlobalVariableTable gv;
    
    gv = oWorldInfo.GetGlobalVariables();
    Pages[Page].Drawer = "";
    Pages[Page].Positions.Length = 0;
    switch (Page)
    {
        case 6:
            AddRemotePOI(Page, $663712, vect(-11350.0, -5880.0, -31210.0));
            if (oWorldInfo.CheckConditional(1151, 0))
            {
                AddRemotePOI(Page, $663450, vect(-9637.0, -7372.0, -31243.0));
            }
            if (oWorldInfo.CheckConditional(182, 0) && oWorldInfo.CheckConditional(1455, 0) && gv.GetBool(21839))
            {
                AddRemotePOI(Page, $306237, vect(-6250.0, -7511.0, -31239.0));
            }
            if (oWorldInfo.CheckConditional(1148, 0))
            {
                AddRemotePOI(Page, $663139, vect(-12159.0, -6350.0, -31180.0));
            }
            break;
        case 7:
            if (oWorldInfo.CheckConditional(181, 0) && gv.GetInt(10195) > 0 && !gv.GetBool(19303) && !gv.GetBool(22155))
            {
                AddRemotePOI(Page, $349390, vect(42906.0, -65292.0, 61015.0));
            }
            AddRemotePOI(Page, $334752, vect(35276.0, -69919.0, 61527.0));
            AddRemotePOI(Page, $705022, vect(35343.6016, -60776.6992, 61527.1016));
            if (oWorldInfo.CheckConditional(22, 0) && oWorldInfo.CheckConditional(2704, 0))
            {
                AddRemotePOI(Page, $524655, vect(37315.0, -65250.0, 61548.0));
            }
            break;
        case 8:
            if (!gv.GetBool(21196) && gv.GetBool(21189) && !gv.GetBool(31274) && oWorldInfo.CheckConditional(1454, 0))
            {
                AddRemotePOI(Page, $694517, vect(-52373.0, 23406.0, 15921.0));
            }
            if (gv.GetInt(11153) >= 2 && gv.GetBool(185) && oWorldInfo.CheckConditional(574, 0) && !gv.GetBool(23950))
            {
                AddRemotePOI(Page, $302165, vect(-53018.0, 23500.0, 15921.0));
            }
            break;
        case 9:
            AddRemotePOI(Page, $705027, vect(-37337.0, 18943.0, 14491.0));
            AddRemotePOI(Page, $705021, vect(-38358.0, 19470.0, 14491.0));
            if (!gv.GetBool(21200))
            {
                AddRemotePOI(Page, $695833, vect(-36428.0, 18841.0, 14491.0));
            }
            break;
        case 10:
            if (gv.GetBool(20207) && !gv.GetBool(22099))
            {
                AddRemotePOI(Page, $290612, vect(-4955.0, 3882.0, 19991.0));
            }
            if (oWorldInfo.CheckConditional(801, 0))
            {
                AddRemotePOI(Page, $705032, vect(-2675.0, 11300.0, 19991.0));
                AddRemotePOI(Page, $704417, vect(-3719.0, 5421.0, 19991.0));
                if (!gv.GetBool(19607))
                {
                    AddRemotePOI(Page, $690463, vect(-2988.0, 5950.0, 20006.0));
                }
                if (!gv.GetBool(19607) && !gv.GetBool(21062))
                {
                    AddRemotePOI(Page, $662819, vect(-1098.5, 1245.0, 19791.0), $662820);
                }
                else
                {
                    AddRemotePOI(Page, $662819, vect(-1147.0, 1238.0, 19791.0));
                }
            }
            break;
        case 11:
            AddRemotePOI(Page, $218732, vect(-2253.0, 8159.0, 21091.0));
            if (!gv.GetBool(20207) && !oWorldInfo.CheckConditional(420, 0))
            {
                AddRemotePOI(Page, $290612, vect(-2198.0, 8476.0, 21091.0));
            }
            if (oWorldInfo.CheckConditional(801, 0))
            {
                AddRemotePOI(Page, $704417, vect(-4400.0, 7039.0, 21191.0));
                AddRemotePOI(Page, $619637, vect(-3593.0, 11142.0, 21191.0));
                AddRemotePOI(Page, $705971, vect(-6727.0, 9173.0, 21191.0), $705972);
            }
            break;
        default:
    }
}

//class default properties can be edited in the Properties tab for the class's Default__ object.
defaultproperties
{
}
```

## 18. Appendix: router logic and calibration data

The router logic below is from the current template, with its generated defaults omitted from the code excerpt. The tables that follow preserve the calibration/bounds inputs. For exact compiled float values and the full CDO, use `validation/PVCitadelMapRouter.verified.uc`.

```unrealscript
class PVCitadelMapRouter extends Actor;

struct PageSpec
{
    var vector Min;
    var vector Max;
    var vector World1;
    var vector World2;
    var vector Image1;
    var vector Image2;
    var class<SFXAreaMapData> DataClass;
    var SFXSFHandler_AreaMap.SFXAreaMapLayout Floor;
};
var array<PageSpec> Specs;
var class<SFXSFHandler_AreaMap> MapHandlerClass;
var transient int LastPage;
var transient SFXAreaMapData AssignedData;

static function int GetPage(vector Loc, bool bUpper)
{
    local int i;
    // Custom districts first; then the original six Citadel map bounds.
    for (i = 6; i < 12; i++)
    {
        if ((i == 10 && bUpper) || (i == 11 && !bUpper))
        {
            continue;
        }
        if (InBox(Loc, default.Specs[i].Min, default.Specs[i].Max))
        {
            return i;
        }
    }
    for (i = 0; i < 6; i++)
    {
        if (InBox(Loc, default.Specs[i].Min, default.Specs[i].Max))
        {
            return i;
        }
    }
    return -1;
}

static function bool InBox(vector Loc, vector Low, vector High)
{
    return Loc.X >= Low.X && Loc.X <= High.X
        && Loc.Y >= Low.Y && Loc.Y <= High.Y
        && Loc.Z >= Low.Z && Loc.Z <= High.Z;
}

event function PostBeginPlay()
{
    Super.PostBeginPlay();
    SetTimer(0.15, true, 'UpdateMap');
    UpdateMap();
}

function SFXAreaMapData MakeData(int Page)
{
    local SFXAreaMapData Data;
    local class<SFXAreaMapData> DataType;
    DataType = Specs[Page].DataClass;
    Data = new DataType;
    Data.Floor = Specs[Page].Floor;
    Data.OverrideGroupID = 0;
    Data.RefPoint1_Unreal_UU = Specs[Page].World1;
    Data.RefPoint2_Unreal_UU = Specs[Page].World2;
    Data.RefPoint1_Image_Px = Specs[Page].Image1;
    Data.RefPoint2_Image_Px = Specs[Page].Image2;
    if (PVCSecMapData(Data) != None)
    {
        PVCSecMapData(Data).bUpperFloor = Page == 11;
    }
    return Data;
}

function UpdateMap()
{
    local BioPlayerController PC;
    local SFXGUIInteraction Manager;
    local int Page;
    PC = BioWorldInfo(WorldInfo).GetLocalPlayerController();
    if (PC == None || PC.Pawn == None)
    {
        return;
    }
    Manager = PC.GetSFXUIController();
    if (Manager == None || WorldInfo.bPlayersOnly)
    {
        return;
    }
    Page = GetPage(PC.Pawn.Location, BioWorldInfo(WorldInfo).GetGlobalVariables().GetBool(31255));
    if (Page < 0)
    {
        if (AssignedData != None && Manager.GetAreaMapData() == AssignedData)
        {
            Manager.SetAreaMapData(None);
        }
        AssignedData = None;
        LastPage = -1;
        return;
    }
    // Keep data and movie tag together after native assignments or streaming
    // trigger exits. A previous district's saved map cannot disable this one.
    if (Page != LastPage || AssignedData == None || Manager.GetAreaMapData() != AssignedData)
    {
        AssignedData = MakeData(Page);
        Manager.SetAreaMapData(AssignedData);
        LastPage = Page;
    }
    Manager.MovieTag_AreaMap = 'PVCitadelAreaMap';
}
```

### Native reference calibration

Source nodes are from original base-game BioD_CitHub packages: Dock, Embassy, Hospital, WardsFlux, Presidium, Underbelly. Ref1/Ref2 are world coordinates; image coordinates are logical native 1024-canvas pixels.

| Page / source | Floor enum | Ref1 world | Ref2 world | Image1 | Image2 |
|---|---|---|---|---|---|
| 0 / Dock | `AM_CitDock` | `(-16189.324, 6544.7896, -170394.9)` | `(-11007.474, 11649.802, -170378.14)` | `(211, 845)` | `(963, 88)` |
| 1 / Embassy | `AM_CitEmb` | `(-19208.37, -3244.542, -175188.69)` | `(-13854.106, 6695.16, -175650)` | `(58, 773)` | `(935, 308)` |
| 2 / Hospital | `AM_CitHosp` | `(-7650.0996, 8297.186, -182300.98)` | `(1248.4708, 10298.708, -182299.9)` | `(412, 912)` | `(606, 51)` |
| 3 / WardsFlux | `AM_CitPurg` | `(-9080.069, 230.13737, -177284.7)` | `(-17987.941, -2649.478, -176800)` | `(686, 41)` | `(383, 978)` |
| 4 / Presidium | `AM_CitCommons` | `(-11311.216, 5973.094, -184434.7)` | `(-5711.732, 19444.049, -185442.42)` | `(24, 731)` | `(966, 332)` |
| 5 / Underbelly | `AM_CitCamp` | `(-15493.468, 906.2866, -171694.9)` | `(-12685.851, 10251.289, -171699.9)` | `(22, 747)` | `(987, 458)` |

### Custom reference pairs

| Page | Ref1 world | Ref2 world |
|---:|---|---|
| 6 | `(-7275,-6899,-31389)` | `(-12888,-5423,-31105)` |
| 7 | `(44000,-65333,61015)` | `(35000,-61108,61527)` |
| 8 | `(-53010,23644,15921)` | `(-55130,22546,15921)` |
| 9 | `(-37990,19775,14491)` | `(-36194,20994,14491)` |
| 10 and 11 | `(0,0,0)` | `(0,1024,0)` |

Custom image references are generated by applying the documented per-page transform to those world points. C-Sec uses a dummy pair because its data class directly implements both coordinate transforms.

The router's **typed MapData Floor** is `AM_CitPurg` for custom Specs. That legacy enum value is distinct from the shared handler's custom **Flash layout label** `AM_Biop_nor_5`, and distinct again from the carousel **page index** 6–11. Do not substitute one for another. The shared handler selects the custom texture/page independently of that typed legacy Floor value.

### Exact generated router bounds

These are generator values; package serialization rounds to float precision. Use the verified class CDO when inspecting an actual runtime discrepancy.

| Page | Data class | Minimum world | Maximum world |
|---:|---|---|---|
| 0 | `SFXAreaMapData` | `(-18009.5955, 4506.36906, -170994.9)` | `(-9828.82467, 12687.1398, -169778.14)` |
| 1 | `SFXAreaMapData` | `(-22663.1619, -4504.21502, -176250)` | `(-9816.52097, 8342.42587, -174588.69)` |
| 2 | `SFXAreaMapData` | `(-9407.53971, 3439.45989, -182900.98)` | `(2374.76985, 15221.7694, -181699.9)` |
| 3 | `SFXAreaMapData` | `(-19024.9636, -6891.32517, -177884.7)` | `(-8090.30229, 4043.33611, -176200)` |
| 4 | `SFXAreaMapData` | `(-16089.4389, 5030.85049, -186042.42)` | `(-287.049008, 20833.2404, -183834.7)` |
| 5 | `SFXAreaMapData` | `(-18776.6272, 93.1837019, -172299.9)` | `(-7657.65596, 11212.155, -171094.9)` |
| 6 | `PVArchosMapData` | `(-14750, -11500, -33000)` | `(-4750, -1500, -30000)` |
| 7 | `PVTowerMapData` | `(25500, -77833, 58500)` | `(50500, -52833, 64500)` |
| 8 | `PVTempleMapData` | `(-57200, 17800, 14800)` | `(-48900, 25700, 17000)` |
| 9 | `PVVolusMapData` | `(-39800, 17000, 12400)` | `(-34200, 22600, 15800)` |
| 10 | `PVCSecMapData` | `(-14500, -3500, 17500)` | `(5500, 16500, 23500)` |
| 11 | `PVCSecMapData` | `(-14500, -3500, 17500)` | `(5500, 16500, 23500)` |

### Completion record

The final functional change was the explicit vanilla layout-name correction. After installation, the user tested Huerta Memorial and the other original maps and replied: **“All vanilla markers align.”** No additional map changes were made while writing this reference.
