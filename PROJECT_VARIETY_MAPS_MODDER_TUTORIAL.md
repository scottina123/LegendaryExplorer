# Building and editing LE3 area maps: a Project Variety modder's tutorial

**Written October 22, 2026.** This is a practical tutorial for human modders. It accompanies the unchanged [Project Variety maps reference](C:/Users/Scottie/source/repos/LegendaryExplorer/PROJECT_VARIETY_MAPS_REFERENCE.md), which contains the full final scripts, exact NPC tables, installed hashes, and development history.

You will learn how a map opens, how to inspect its assets, how to make a small working change, how to prepare a map image, how to align the player/NPC markers, and how to add points of interest that also work while browsing from another district.

The examples use the completed PV installation. You do not need an AI assistant to follow them. You do need the project's working files for its custom build commands. Basic asset inspection uses LegendaryExplorer's graphical tools; rebuilding the shared system also uses a text editor, UnrealScript, and the supplied C# helper. This is not a claim that a fresh multi-map system can be built with one Level Editor checkbox.

## Choose a starting point

| Your goal | Start here |
|---|---|
| Understand what the packages do | [Lesson 1](#lesson-1-understand-the-map-system) |
| Find and inspect an existing map in LegendaryExplorer | [Lesson 2](#lesson-2-find-the-correct-files-and-inspect-a-working-map) |
| Make your first complete, reversible edit | [Lesson 3](#lesson-3-your-first-edit-change-a-map-heading) |
| Create or replace the floor-plan image | [Lesson 4](#lesson-4-create-and-prepare-a-map-image) |
| Fix a player or NPC marker that is in the wrong place | [Lesson 5](#lesson-5-calibrate-the-map-to-world-coordinates) |
| Add numbered NPC labels | [Lesson 6](#lesson-6-add-numbered-points-of-interest) |
| Make labels work from other districts | [Lesson 7](#lesson-7-make-pois-work-when-the-level-is-unloaded) |
| Add another district to the carousel | [Lesson 8](#lesson-8-add-a-new-map-page) |
| Package, test, and distribute your work | [Lesson 9](#lesson-9-save-test-and-package-your-change) |
| Diagnose a problem | [Troubleshooting](#troubleshooting-by-symptom) |

## Before you begin

### What you need

- A working LE3 installation and the PV setup this tutorial is based on.
- LegendaryExplorer with Package Editor, Sequence Editor, Level Editor, Texture Viewer, and Script Editor.
- An image editor that can resize a canvas without stretching its contents, such as an editor you already use for PNGs.
- A plain-text/code editor for `.uc`, `.cs`, and `.json` files.
- PowerShell **7** and the .NET **10 SDK** for the supplied helper project.
- The `Build/pv-archos-map`, `Build/pv-citadel-carousel`, individual map folders, and the helper's working dependency DLLs under `Build/save-menu-search/bin/Debug/net10.0`.
- Python with Pillow only if you use the optional image-preparation script in Lesson 4.

Having the LegendaryExplorer application alone does not supply these project-specific builders and backups. Keep the working folder tree from this project, or obtain it before using those commands.

### Use a working copy

Close LE3 before installing changed packages. You can read source files and prepare edits while the game is open, but the deployment step checks that it is closed.

For this project, the user's requested installation target is:

```text
C:\Steam\steamapps\common\Mass Effect Legendary Edition\Game\ME3\BioGame\DLC\DLC_MOD_ProjectVariety\CookedPCConsole
```

Do not accidentally edit a Mod Manager library copy while testing this installation. In your own public mod project, choose your deployment method deliberately; this tutorial's direct-install paths describe this particular project.

Create a fresh revision folder for each experiment. Preserve the current versions of:

```text
CookedPCConsole/Startup_DLC_MOD_ProjectVariety_INT.pcc
CookedPCConsole/BioP_CitHub.pcc
CookedPCConsole/Default_DLC_MOD_ProjectVariety.bin
PCConsoleTOC.bin                 <-- in the DLC root, not CookedPCConsole
```

The supplied revision builder performs its own baseline backups and hash checks. Do not overwrite those backups when a later build says the installed files have changed; choose a new revision folder instead.

---

## Lesson 1: understand the map system

**Goal:** know which component to change before opening an editor.

Think of the area map as several parts working together:

```text
Player enters a district
        |
        v
Router selects map data + movie tag
        |
        v
Handler supplies image, layout, heading, list, and positions
        |
        v
Flash/GFx movie draws the map window and its markers
```

The picture is only one part. Importing a PNG does not, by itself, make M open a map.

### The useful terms

| Term | Meaning here |
|---|---|
| PCC | Unreal package containing exports such as classes, actors, textures, or movies |
| Export | An object stored in a package |
| Import | A reference to an object stored elsewhere |
| Startup package | An early-loaded package used to keep shared map images and movies available |
| Handler | UnrealScript class that prepares the map UI's data |
| Router | Actor that chooses the player's district and assigns the map |
| GFx movie | Flash-based UI artwork, animation, and ActionScript |
| Layout | A named frame containing the map's overlay artwork |
| POI | Point of interest: a name/list entry plus its numbered map marker |
| TLK string reference | A numeric ID for localized text, such as an NPC's displayed name |
| World coordinates | The actor's X/Y/Z position in the level |
| Logical map coordinates | The map's 1024×1024 coordinate system, independent of texture resolution |
| TOC | File-size table that must agree with the packages you install |

### Where this project stores things

| What you want to inspect | Package/object |
|---|---|
| Custom map images | PV startup, under `PV_AreaMaps` |
| Shared map window | PV startup: `GUI_SF_PV_CitadelMap.ME2_AreaMap` |
| Current shared UI logic | `BioP_CitHub.PVCitadelMapHandler` |
| District selection | `BioP_CitHub.PVCitadelMapRouter` |
| Existing custom trigger sequences | `BioP_CitHub`, under `TheWorld.PersistentLevel.Main_Sequence` |
| NPC/quest logic | The relevant `BioD_CitHub_...` and sometimes `BioNPC_...` packages |
| Movie registrations | `Default_DLC_MOD_ProjectVariety.bin`, its `bioui.ini` content |

**Checkpoint:** if only a label is wrong, start with the handler/provider and TLK name. If M does nothing, start with activation, class loading, and the movie tag. If every marker is offset, start with layout selection or calibration.

## Lesson 2: find the correct files and inspect a working map

**Goal:** trace the installed Hanar Embassy map without changing it.

### Step 1 — resolve which package wins

Several DLCs can contain the same filename. The first copy you find in Explorer may not be the one the game loads.

Open PowerShell 7 in the workspace:

```powershell
Set-Location -LiteralPath 'C:\Users\Scottie\source\repos\LegendaryExplorer'
dotnet Build/pv-archos-map/bin/Debug/net10.0/Map.dll resolve BioD_CitHub_Temple.pcc BioA_CitHub_Temple.pcc
```

The output gives the mounted source for each file. In this project's installed setup, Temple architecture came from PV3 while Temple dialogue/quest data came from base PV. Resolve again if your installed mods differ.

### Step 2 — inspect the texture

1. Open **Package Editor**.
2. Use **File → Open** to open a **working copy** of `Startup_DLC_MOD_ProjectVariety_INT.pcc`.
3. Find `PV_AreaMaps.HanarEmbassy` in the export tree.
4. Select the **Texture** tab to preview it.
5. Use **Export to File** if you want a PNG to inspect in an image editor.
6. Select **Properties** and inspect the texture settings, especially `SRGB`.

The expected custom map texture is 2048×2048, belongs to the UI texture group, and has `SRGB=false`. The image should be padded rather than stretched. Exporting a preview does not change the package.

For this one embedded image, Package Editor's Texture tab is the useful starting point. Texture Studio's whole-workspace/TFC operations have broader effects and are not needed for a simple map-image inspection.

### Step 3 — inspect the handler

1. Open a working copy of `BioP_CitHub.pcc` in Package Editor.
2. Select the class export `PVCitadelMapHandler`.
3. Open the **Script Editor** tab.
4. Find `SetMap`, `CustomCoordinates`, and `ExIntUpdateDrawerText`.
5. Read them without compiling anything yet.

You should find Hanar on page **8**, using `PV_AreaMaps.HanarEmbassy`. You will also find remote POI functions in the complete compiled class.

The editable source is split across two text files:

```text
Build/pv-citadel-carousel/PVCitadelMapHandler.uc
Build/pv-citadel-carousel/RemotePOIs.uc
```

The builder combines them. Do not paste just the first file over the whole installed class and accidentally remove the remote functions.

### Step 4 — inspect the old entry sequence

1. In the same package, locate:

   ```text
   TheWorld.PersistentLevel.Main_Sequence.PV_TempleAreaMap
   ```

2. Right-click it and choose **Open in Sequence Editor**.
3. Follow the Touch/UnTouch and Level Is Live connections to its Set Area Map action.
4. Inspect the linked reference points and trigger references.

This shows how the original per-area activation was built. The final installation also has a persistent router that reconciles map assignments. Preserve both when making a small edit.

### Step 5 — find the NPC's real destination

Open the resolved `BioD_CitHub_Temple.pcc`. In Sequence Editor, follow the code that finds `cit_sal_spectre` and teleports/activates it. Check the teleport's **Target** variable before recording its position.

For Jondum Bau, one ordinary embassy destination is:

```text
X=-52373, Y=23406, Z=15921
```

Do not substitute a camera's location, a conversation staging location, or a teleport targeting the player. In VolusPlot, a nearby office teleport initially looked like a guard destination but actually targeted Shepard.

**Checkpoint:** you can now identify the image, the handler page, the entry sequence, and the NPC's actual world position. Nothing has been saved into the game.

## Lesson 3: your first edit: change a map heading

**Goal:** complete a small change from source edit through staging, deployment, testing, and rollback.

This exercise temporarily changes the Tower heading to `CITADEL TOWER TEST`. It uses the existing shared system, so you can learn the workflow before changing geometry or NPC logic.

### Step 1 — save the source you will edit

Copy these files into your experiment notes folder or commit them to your own working source history:

```text
Build/pv-citadel-carousel/PVCitadelMapHandler.uc
Build/pv-archos-map/Program.cs
```

Use a new revision folder name that does not already contain another experiment, such as:

```text
Build/pv-citadel-carousel/tutorial-title-01
```

### Step 2 — change one line

In `PVCitadelMapHandler.uc`, find:

```unrealscript
Pages[7].Title = "CITADEL TOWER";
```

Change it to:

```unrealscript
Pages[7].Title = "CITADEL TOWER TEST";
```

Leave the texture path and layout alone. A heading change is a UI string change, not an object rename.

### Step 3 — give the experiment its own build command

In `Build/pv-archos-map/Program.cs`, add this case beside the other carousel cases, before the general inspection code:

```csharp
if (args[0] == "tutorial-title-build")
{
    try
    {
        CarouselBuilder.Correct(
            Path.GetFullPath("Build/pv-citadel-carousel/tutorial-title-01"),
            game,
            handlerOnly: true);
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        Environment.Exit(1);
    }
    return;
}
```

`handlerOnly:true` keeps startup unchanged. This experiment should deploy only `BioP_CitHub.pcc` and the DLC TOC. The builder may still copy startup into its working folders for comparison; that does not mean it is in the deployment manifest.

Do not reuse `carousel-build` or an older fixed revision command. Those commands expect earlier backup hashes and can rebuild more than your intended change.

### Step 4 — build the helper, then stage the edit

```powershell
dotnet build Build/pv-archos-map --no-restore -p:UseAppHost=false -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed; do not run an older helper binary' }

dotnet Build/pv-archos-map/bin/Debug/net10.0/Map.dll tutorial-title-build
if ($LASTEXITCODE -ne 0) { throw 'Map staging failed' }
```

Expected result: a successful compile and `Carousel correction staged and validated.`

The fresh revision folder now contains:

| Item | What to do with it |
|---|---|
| `backup/` | Keep it; this is your exact pre-experiment state |
| `staged/` | Inspect the new packages here |
| `validation/compile.txt` | Read compiler output |
| `validation/PVCitadelMapHandler.verified.uc` | Check what actually compiled |
| `validation/native-layout-label-tests.json` | Confirm native layouts still resolve correctly |
| `verification.json` | Check the destination files and their hashes |
| `Deploy.ps1` | Install/restore this revision only |

### Step 5 — inspect the staged result

Open `tutorial-title-01/staged/BioP_CitHub.pcc` in Package Editor. Select `PVCitadelMapHandler`, open Script Editor, and verify the new Tower heading.

Also verify that the original map assignments still contain literal names such as:

```unrealscript
Pages[2].Layout = "AM_CitHosp";
```

Do not change these to numeric strings. That previously put the hospital image under the Human Embassies marker overlay.

Close the inspection window without introducing another edit to the staged package. If you change the staged file after its manifest was generated, the deployment hash check should reject it; rebuild the revision instead.

### Step 6 — install and test

Save and close LE3, then use **PowerShell 7**:

```powershell
& 'C:\Users\Scottie\source\repos\LegendaryExplorer\Build\pv-citadel-carousel\tutorial-title-01\Deploy.ps1'
```

Check the individual `Verified ...` lines. Some copied historical scripts use an old success-message description; the revision manifest and verified destination filenames define what was installed.

Reopen LE3, press M, and browse to Citadel Tower. You should see the test heading. Check an original map too, then close the game before restoring.

### Step 7 — finish the experiment cleanly

If this was only practice:

1. Close LE3.
2. Restore the revision:

   ```powershell
   & 'C:\Users\Scottie\source\repos\LegendaryExplorer\Build\pv-citadel-carousel\tutorial-title-01\Deploy.ps1' -Restore
   ```

3. Change the source heading back to `CITADEL TOWER` and save it.
4. Keep the experiment's backup, manifest, and notes if you want a record.

Restoring packages does not undo your text-file edits. Leaving `TEST` in the source can make it return in a later build.

For a permanent change, update the project's combined manifest after a successful install, retaining its original backup fields. Lesson 9 explains that step. During a temporary experiment the combined manifest will intentionally differ from the test installation; restoring the experiment returns it to the original baseline.

**Checkpoint:** you have completed the full workflow without changing a map image or quest rule.

## Lesson 4: create and prepare a map image

**Goal:** make an image suitable for the area-map UI without distorting its geometry.

### Step 1 — capture the correct floor

If you already have a good top-down PNG, start with that. Otherwise:

1. Open **Level Editor** and use **File → Open** for the relevant architectural package.
2. Use **File → Add File...** or **Load Related Levels** to assemble the required pieces.
3. Check the loaded files. Related-level loading can include other floors or variants you do not want in the screenshot.
4. Arrange a straight-down view of the floor, using an orthographic/top view if your editor build offers it. Camera controls differ between builds; do not assume a perspective screenshot has uniform scale.
5. Temporarily hide obstructing roofs/upper-floor objects in the editor view or in a disposable working scene. You are preparing a picture, not deleting geometry from the released level.
6. Frame the full walkable area and recognizable landmarks.
7. Use **File → Capture Viewport...** to save a PNG.

Do not use **Save All** merely to export a screenshot. That command saves open level files; **Capture Viewport** exports an image.

For a multi-floor area such as C-Sec, capture separate images. Keep the floor/variant selection in your notes so you can reproduce the capture later.

### Step 2 — keep a source master

Save an untouched source, for example:

```text
MyMap.source.png
```

Do not paint the moving player arrow or quest-dependent number badges into it. The game draws those separately. If you remove empty background or crop the image later, you must update its calibration too.

### Step 3 — pad to a square

The source floor plan may be rectangular. Pad the canvas rather than stretching the image.

Worked example: Tower is **2649×1604**.

```text
Square canvas width/height = 2649
Total vertical padding    = 2649 - 1604 = 1045
Top padding               = 522
Bottom padding            = 523
```

Paste the original at `(0,522)` on the square canvas, then resize the completed square to **2048×2048**. The extra odd pixel belongs below in this project's Tower calibration.

Use an opaque background matching the intended map UI. The existing preparations use a dark opaque background; do not accidentally turn all background pixels transparent.

### Optional: prepare it with Python

This script creates a new UI PNG and raw BGRA data. It does not modify the source or install anything. Run it in a separate texture-work folder with `MyMap.source.png` present; it needs Pillow.

```python
from pathlib import Path
from PIL import Image

source = Image.open("MyMap.source.png").convert("RGBA")
side = max(source.width, source.height)
left = (side - source.width) // 2
top = (side - source.height) // 2

canvas = Image.new("RGBA", (side, side), (31, 31, 31, 255))
canvas.paste(source, (left, top))
ui = canvas.resize((2048, 2048), Image.Resampling.LANCZOS)
ui.save("MyMap-UI.png")
Path("MyMap.bgra").write_bytes(ui.tobytes("raw", "BGRA"))
print(f"Source={source.size}, square={side}, left={left}, top={top}")
```

Record `side`, `left`, and `top`. They are part of the coordinate transform. If the source is taller than wide, the horizontal padding matters too; the current PV formulas happen to use width-dominant source images.

### Step 4 — import the image into a working package

For an existing map:

1. Open a working copy of startup in Package Editor.
2. Select the existing map texture, such as `PV_AreaMaps.HanarEmbassy`.
3. Open **Texture** and use **Import from File** with the prepared PNG.
4. Inspect the imported dimensions and **Properties**. Preserve its UI grouping and set/retain `SRGB=false` for this implementation.
5. Keep the export's identity and startup references. A replacement does not require renaming the asset.

For a new asset, Package Editor has **Create → Create blank texture** and **Create → Create new Package export**. Use a unique texture export inside `PV_AreaMaps`, import the prepared image, and add it to the startup ObjectReferencer's `ReferencedObjects` array.

Texture replacement dialogs may offer storage choices. This project embeds the custom UI images locally and does not require moving them to a TFC. If you choose a TFC workflow, that introduces another file/storage dependency you must track and package.

**Do not install this manually edited working package yet.** Complete the coordinate update, repair/save, TOC, and manifest steps in Lesson 9. A GUI texture edit is an asset-editing step, not the complete deployment process.

**Checkpoint:** the image is square, undistorted, correctly padded, and previewable in the texture export.

## Lesson 5: calibrate the map to world coordinates

**Goal:** make an actor's location correspond to the correct place in the picture.

### Step 1 — separate the three coordinate systems

| System | Example |
|---|---|
| World | Samara stands at `(42906,-65292,61015)` |
| Source image | A landmark lies at source pixel `(u,v)` |
| Logical map | The handler sends `(Px,Py)` on a 1024-square canvas |

The physical texture is 2048-square, but the handler does not send 2048-based marker coordinates. Screen pixels are another coordinate system again.

### Step 2 — collect landmarks

Choose at least three recognizable, widely separated points: a round table center, elevator threshold, large planter, or corridor corner.

For each point:

1. Read its actual world X/Y from the actor or resolved mesh transform.
2. Measure its source-image pixel position in your image editor.
3. Write both into a worksheet.

For collection actors or imported meshes, the visible geometry may have additional transforms. Use the helper's `mesh-full` output when a simple component translation does not match the rendered position.

Two points can fit a simple scale/offset, but a third point checks whether the fit is actually right. More points reveal perspective or rotation errors.

### Step 3 — fit scale and offset

For an axis-aligned source view, use:

```text
u = a + b*WorldX
v = c + d*WorldY
```

From two landmarks with different X values:

```text
b = (u2-u1)/(X2-X1)
a = u1-b*X1
```

Do the same for Y and v. A negative slope is normal if the image axis runs opposite the world axis. Do not force the sign to be positive.

If world axes are swapped in your screenshot, match u/v to the correct axes. If the capture has an arbitrary rotation or perspective, these simple formulas may not fit. Reorient the image or implement an appropriate affine transform; changing only the intercept cannot solve rotation/perspective errors.

### Step 4 — include padding and logical scaling

For a square source canvas of size `S`, with left/top padding `L,P`:

```text
Px = (u + L) * 1024 / S
Py = (v + P) * 1024 / S
```

Worked Tower example:

```text
u = 5150 - 0.1*WorldX
v = -5682 - 0.1*WorldY
S = 2649
L = 0
P = 522
```

For Samara at X=42906, Y=-65292:

```text
u  = 859.4
v  = 847.2
Px = 859.4 * 1024/2649       ≈ 332.2105
Py = (847.2+522)*1024/2649   ≈ 529.2793
```

That point should sit on Samara's Tower destination when the marker is drawn.

### Step 5 — update the relevant code together

In the current shared system, custom player/NPC positions use `PVCitadelMapHandler.CustomCoordinates(Page, Location, Px, Py)`.

When changing calibration, also review:

- The area's `PV...MapData.GetPixelCoordinates` implementation.
- Reference points generated for the router and retained native actions.
- Calibration JSON and prepared image padding.
- Any diagnostic images that might otherwise be mistaken for the final calibration.

Do not change a formula only in the older standalone handler and expect the shared carousel to use it.

### Step 6 — check heading separately

For these custom images, the player heading uses:

```text
Yaw * 0.00549316406 - 90 degrees
```

The -90 adjustment is specific to the custom image orientation. The original Citadel pages retain their native heading behavior. Position can be right while heading is wrong, so test both.

### Step 7 — interpret common calibration errors

| What you see | Likely cause |
|---|---|
| Same displacement everywhere | Wrong intercept or missing padding |
| Correct near one point, farther wrong across the map | Wrong scale |
| Mirrored movement | Wrong axis sign |
| East/west movement appears as north/south | Swapped axes or rotated image |
| All native icons fit a different district | Wrong Flash layout, not a calibration problem |
| Player and every NPC agree but are offset from the texture | Shared world-to-image transform is wrong |
| Player is right but one NPC is wrong | Wrong NPC world position/actor, not global calibration |

**Checkpoint:** at least three landmarks fit, the player arrow follows movement, and heading matches rotation.

## Lesson 6: add numbered points of interest

**Goal:** create a list row and a matching red number at the NPC's location.

### Step 1 — identify the correct actor

Inspect the quest package in Sequence Editor and the NPC in Package Editor. Record:

```text
Actor tag / unique tag:
Current use/selection name:
TLK string ID:
Activation condition:
World destination:
Floor:
```

For Hanar, the two intended actors are `cit_sal_spectre` (Jondum Bau, TLK 694517) and `global_kolyat` (Kolyat, TLK 302165). Other actors in the package may be cinematic-only.

### Step 2 — filter for an actor actually present

A useful loaded-actor search checks:

- Correct Tag/UniqueTag or supported current-name fallback.
- Actor active and not hidden/deleted.
- Inside the intended district's X/Y/Z bounds.
- On the requested floor.
- Targetable, where the POI represents an available interaction.

Do not accept a matching actor in a remote conversation stage. A global NPC can exist in memory while being nowhere near the map you are displaying.

### Step 3 — append text and position as one operation

The Hanar provider illustrates the pattern:

```unrealscript
NPC = FindEmbassyNPC('cit_sal_spectre');
if (NPC != None)
{
    Drawer $= CarouselRow($694517);
    CarouselPositions.AddItem(NPC.Location);
}
```

`CarouselRow` uses the position array's current length as the zero-based list icon index. The position is then appended. If Bau is absent, Kolyat becomes the first row and marker 1; there is no gap.

The shared handler converts each stored world position with the page's calibration and calls:

```unrealscript
ASSetNPCMarker(i + 1, Px, Py, true);
```

Unused slots are hidden. The movie currently provides **eight** custom badges per page. Increasing a list past eight requires expanding both the handler and movie.

### Step 4 — use consistent text formatting

Use a hanging indent so badges align and wrapped names stay under the name column:

```html
<textformat leftmargin="30" rightmargin="0" blockindent="0" indent="-30" leading="2"><p align="left"><b></b>[AreaMap_Label_01] <font color="#ffffff">NPC Name</font></p></textformat>
```

In code, obtain the numbered token from `LocationIcons[index]`. Do not manually concatenate a different number or line up rows with spaces.

The `<b></b>` reset and explicit paragraph metrics fixed an actual C-Sec problem where small red badges 1 and 2 were uneven. Retain them for each row.

### Step 5 — handle close pairs

If two people stand together, a shared marker can be clearer:

```text
Position = (First.Location + Second.Location) / 2
Label    = FirstName + " / " + SecondName
```

C-Sec uses this for Gung-ho/Reluctant Civilian and Patient/Angry C-Sec Officers. If only one person is present, show that person's name/location rather than a stale pair.

### Step 6 — edit the active provider, not just an old source copy

The loaded providers are the five `PV...MapHandler` classes with `GetCarouselDrawer` and `CarouselPositions` added. The shared handler calls those methods.

Use the generated provider sources under `Build/pv-citadel-carousel/sources/`, or regenerate carefully with `CarouselSources.ProviderSource`. An old standalone `.uc` without these methods is not a complete replacement for the installed provider class.

The Lesson 3 shared-handler builder does **not** compile provider changes for you. To alter a provider, extend a fresh revision builder to compile that existing class from its complete current source, or compile it in a working package using Package Editor's Script Editor **Compile** button and then use the save/validation process in Lesson 9. Check the compiler output before saving.

### Preserve the user's intended POI scope

- Archos's `poi_blood` interaction is labeled **Narl**, not Door.
- Volus includes Diplomat, Angry Accountant, and only `citprs_kidnapped_guard` from VolusPlot.
- Do not add every object from a quest package merely because it has an interaction or a name.

**Checkpoint:** every visible list number has one matching map number; absent NPCs do not leave stale badges.

## Lesson 7: make POIs work when the level is unloaded

**Goal:** keep a useful list when the player browses this map from somewhere else.

### Why live actor searches are insufficient

If Temple is unloaded, `AllActors` cannot find its NPCs. This does not necessarily mean the quest NPC is unavailable; the level may simply not exist in memory.

The final system therefore uses two sources:

| District state | Data source |
|---|---|
| Relevant quest level visible | Live actor provider, including an intentionally empty result |
| District unloaded | Static source destinations plus current quest tests |

The first rule matters: do not replace an empty **loaded** list with a speculative full list. An NPC may intentionally have disappeared.

### Step 1 — define the level-loaded test

In `RemotePOIs.uc`, `IsPageLoaded` checks visible streaming package names. Hanar uses `BioD_CitHub_Temple`; C-Sec uses separate LowerP3 and UpperP3 package names.

Use `bIsVisible` rather than merely noticing a package name in a streaming array. A listed package is not necessarily visible. Keep the current-page fallback so a package-name mismatch does not replace the player's live list with static guesses.

### Step 2 — copy an actual NPC destination

Use the quest sequence's target-specific teleport or actor location. For example, Kolyat's embassy destination is:

```text
(-53018,23500,15921)
```

The remote entry stores world coordinates, not measured screen coordinates. The shared calibration then positions the badge in the same way as a live actor.

### Step 3 — copy the availability rules

Trace the source sequence's checks instead of guessing from the quest's name. The installed Kolyat rule is:

```unrealscript
if (GV.GetInt(11153) >= 2
    && GV.GetBool(185)
    && oWorldInfo.CheckConditional(574, 0)
    && !GV.GetBool(23950))
{
    AddRemotePOI(Page, $302165, vect(-53018,23500,15921));
}
```

`AddRemotePOI` appends both the list row and the corresponding position. Use the same order and names as the loaded list. Do not set these plot flags just to force a map label to appear.

If you add a live NPC, update the remote catalogue too. Otherwise your mod can appear correct inside the district and incomplete from another map.

### Step 4 — check both C-Sec floors independently

Bool **31255** tells the map which floor the **player** is on:

```text
false = lower
true  = upper
```

It does not tell you where every NPC belongs while browsing the other floor. Loaded NPC filtering uses the requested page and Z threshold **20550**, while remote rows have their own explicit lower/upper positions and quest conditions.

### Step 5 — test the two views

1. Open the map while standing in the district; compare names and marker positions with live NPCs.
2. Travel to an unrelated district and browse back to this page.
3. Confirm available rows still appear and numbers match.
4. Where saves allow, test before/after a quest state that removes a person.

The supplied `test_remote.py` checks implemented catalogue branches and source positions, but it is not a complete quest simulator. If you add rows, extend the tests too. Its current coordinate provenance list contains source-export indices for the inspected package versions; update those checks if your package versions differ.

**Checkpoint:** loaded and unloaded lists have a clear source, and missing actors are distinguished from missing levels.

## Lesson 8: add a new map page

**Goal:** understand every integration point required for another district.

Do this after completing the smaller lessons. The current router and tests are written for **12 pages**, so adding a thirteenth page is not just an array append.

### Step 1 — fill in a design worksheet

```text
District name:
Source architectural packages:
Source quest/NPC packages:
Texture export path:
Source image size / square padding:
Page index:
World coverage minimum X/Y/Z:
World coverage maximum X/Y/Z:
World-to-image formulas:
Player heading adjustment:
Floor-selection rule, if any:
Visible streaming package:
NPC tags / TLK IDs / quest gates:
Elevator and boundary test points:
```

Do not choose a huge coverage box simply to make M work everywhere. It can swallow another district and select the wrong map.

### Step 2 — add the persistent assets

Add the prepared texture to startup, give it a unique path under `PV_AreaMaps`, and root it in ObjectReferencer. Reuse the shared map movie unless the new map needs artwork it does not provide.

The movie's custom marker frame is currently the frame labeled `AM_Biop_nor_5`. It contains separate `PVNum01`–`PVNum08` instances; the original Citadel overlay instances remain on their own frames.

### Step 3 — add map data and the provider

Use an existing area's data/provider classes as a starting point. Rename class identities consistently, supply the new calibration and live NPC search, and keep an explicit reference that loads the class with the level/router.

Package Editor offers **Create → Create new Class** for manual work, but creating a class export does not create a persistent actor instance or add it to a level actor list. The builder code handles those as separate operations.

### Step 4 — extend the shared handler

Update all of the following as required:

- `Pages.Length` and page loops.
- Asset, Title, and literal Layout assignments.
- `CustomCoordinates`.
- Provider creation, WorldInfo assignment, drawer collection, and positions.
- Player-page selection/fallback.
- Page-change marker updates and marker capacity assumptions.
- `IsPageLoaded` and remote catalogue cases.

For custom pages, use the existing named custom frame if appropriate. For original maps, preserve literal labels such as `AM_CitHosp`. Never use `string(Enum(i))` to generate Flash frame names.

### Step 5 — extend the router

Add the new `PageSpec` with coverage, references, data class, and typed floor information. Update custom/native search loop ranges and any fixed counts in generation/validation.

Three values are easy to confuse:

| Value | Example | Job |
|---|---|---|
| Carousel page index | `8` | Chooses Hanar's image/title/list |
| Flash frame label | `AM_Biop_nor_5` | Chooses shared custom overlay artwork |
| MapData floor enum | `AM_CitPurg` in current custom Specs | Typed legacy map-data field |

They are not interchangeable.

### Step 6 — check entry, initial load, and elevator coverage

The router handles the current persistent system. If you also add a native trigger sequence, follow the existing Touch, UnTouch, and Level Is Live pattern. Its movie tag must resolve to the shared handler/movie.

Use the verified nonblocking cylinder settings:

```text
CollideActors      true
BlockNonZeroExtent true
BlockActors        false
BlockZeroExtent    false
CanBlockCamera     false
```

Keep the actor's collision enabled and Touch activations unlimited (`MaxTriggerCount=0`). Non-zero-extent overlaps allow the player to trigger the volume; camera/zero-extent blocking must remain off.

For overlapping triggers, an UnTouch must not clear the map while the player remains in another trigger for the same district. For Hanar, a foyer radius of 1500 missed the elevator; the working radius is **2200**. Include actual elevator test coordinates in your design worksheet.

### Step 7 — reconcile map data and movie selection

When assigning through the UI manager, retain this order:

```unrealscript
Manager.SetAreaMapData(AssignedData);
Manager.MovieTag_AreaMap = 'PVCitadelAreaMap';
```

Assigning non-null area-map data clears the movie tag. Setting the tag first can leave M unable to open the intended movie.

For an added persistent actor, retain existing `Level.Actors` entries and append the intended actor. Keep a single router, not a second competing router for each new district.

### Step 8 — expand tests and build from current files

Extend routing, page/frame, custom marker, remote catalogue, and preservation checks. Add a fresh builder revision for all affected classes/assets/config. The focused shared-handler helper from Lesson 3 does not automatically import a new texture, extend Specs, or compile your new provider.

**Checkpoint:** you can trace the new district from coverage selection to a rooted texture and a matching POI list, without changing original map behavior.

## Lesson 9: save, test, and package your change

**Goal:** install a coherent set of files and retain a dependable rollback.

### Step 1 — compile and inspect the compiled result

For GUI script editing, select the relevant class/function in Package Editor's **Script Editor**, make the edit, press **Compile**, and read its output. Compiling and saving the package are separate actions.

For source-based revisions, use the helper's compile logs and verified/decompiled `.uc` output. Check what actually compiled, not just whether the log says success.

The native layout bug is the example to remember:

```unrealscript
// Looked plausible, but compiled to numeric byte strings:
string(SFXAreaMapLayout(i))

// Correct layout selection for the hospital:
"AM_CitHosp"
```

Flash interpreted `"2"` as frame 2, not page 2's intended frame 3. That caused displaced numbers and an extra fifth hospital marker. Explicit labels fixed it and were confirmed in game.

### Step 2 — save with texture-offset repair

The project helper uses `MapBuilder.SaveAndRepair`. It saves an intermediate package, reopens it, repairs absolute offsets for locally stored mips, and saves the final package.

This matters even when your change is a script: package layout/size changes can move embedded textures. An ordinary edited PCC must not bypass this project's final save/validation step.

If building a new revision around manually edited working PCCs, open those edited files in your revision code, call the same `SaveAndRepair` method for the final staged outputs, and validate them. Do not overwrite the clean baseline backups with the edited working copies.

When comparing texture exports, account for repaired offset fields but still compare actual pixel data. Ignoring every texture-data difference would hide real corruption.

### Step 3 — update the TOC

For every changed installed package/config, update its recorded byte size in the DLC's `PCConsoleTOC.bin`. The provided builders use `TOCBinFile` and write the new length at the selected entry's `offset + 4`.

Do not copy an old TOC from a different revision just because its filename matches. It must describe the package set you will install.

### Step 4 — inspect the revision manifest

Confirm each `verification.json` entry:

| Field | Meaning |
|---|---|
| `Source` | Installed destination |
| `SourceSHA256` | Expected pre-revision file hash |
| `Backup` | Exact pre-revision copy |
| `Staged` | Validated replacement |
| `StagedSHA256` | Expected replacement hash |

A title-only change should not unexpectedly deploy a new texture package. A new texture should not silently replace coalesced registrations with an older revision's copy.

### Step 5 — deploy with the game closed

Run your fresh revision's `Deploy.ps1` in PowerShell 7. Let it validate the baseline, backups, staged files, destination scope, and installed hashes.

If it refuses because an installed file changed, stop and inspect the difference. It may be another mod update or another experiment. Do not remove the guard or pretend the stale backup is current.

### Step 6 — run a short in-game test matrix

| Test | Expected result |
|---|---|
| Load a save already inside the district, press M | Correct map opens |
| Load elsewhere and travel in | M still works |
| Exit and re-enter | Map selection recovers correctly |
| Stand in the elevator/foyer/edge room | Coverage includes the intended space |
| Rotate camera around Shepard | No new camera blocking or disappearing Shepard |
| Check several player positions/headings | Arrow tracks the floor plan |
| Check present and absent NPCs | Matching consecutive list/map numbers; no stale slots |
| Browse away and back | Heading, texture, markers, and player visibility reset correctly |
| Browse native Hospital/Embassies/Docks/Purgatory | Original numbers, shop, and elevator icons align |
| Browse custom pages from another district | Remote POI lists/markers appear as intended |
| Use both C-Sec floors | Bool 31255 selects the correct image; NPCs belong to the displayed floor |

Use screenshots and a short note listing save/quest state, district, page, and expected/actual result. “It looks wrong” is harder to debug than “Hospital image is showing Embassy's fifth badge.”

### Step 7 — retain the change or restore it

For an unwanted experiment, close LE3 and run that revision's script with `-Restore`. Restore source text separately if you changed it.

For a permanent successful change, update the combined manifest at:

```text
Build/pv-archos-map/verification.json
```

For each matching installed destination, update only its `Staged` and `StagedSHA256` to the new revision. Preserve the combined manifest's original `Backup` and `SourceSHA256`, which support restoring the whole map project to its starting state.

Normalize destination paths when comparing them; some existing manifest paths use mixed slash styles. Verify installed hashes again after updating the record.

Do not use the combined `-Restore` to remove only your latest NPC experiment: it restores the project's original four pre-map files. Use your experiment's focused restore instead.

### Step 8 — preserve and distribute responsibly

Keep the source images, calibration values, source code, dependency requirements, revision manifest, validation output, and backups with your working project. `Build/` folders may be ignored by Git or removed by cleanup.

The staged startup/BioP/config files are full packages based on this particular installed PV setup. They can overwrite another user's unrelated changes. For a distributable mod, record the supported PV/dependency versions and choose an installer/merge strategy suitable for your intended audience. The direct-copy deployment script is a local development method, not a compatibility solution for every installation.

## Troubleshooting by symptom

| Symptom | First checks |
|---|---|
| M does nothing | Valid map data, loaded handler, correct registration/tag, tag assigned after SetAreaMapData |
| Map only works when loading a save inside | Travel entry/overlap events, unlimited triggers, router bounds |
| Map too dark | Custom texture `SRGB=false`; don't immediately repaint all source pixels brighter |
| Shepard disappears as camera turns | Trigger's `CanBlockCamera=false` and `BlockZeroExtent=false`; keep pawn overlaps |
| Elevator excluded | Measure elevator point against coverage; preserve Hanar's 2200 foyer radius |
| Player and NPCs all offset | Calibration, padding, sign/axis, selected layout |
| One NPC offset | Wrong actor/destination, cinematic stage, or player teleport mistaken for NPC teleport |
| Extra/misplaced native numbers | Check literal named layouts; never substitute enum byte strings |
| Small list badges uneven | Explicit textformat/p metrics, reset, consistent spacing, hanging indent |
| Labels disappear when browsing elsewhere | Unloaded-level catalogue missing or loaded-state detection wrong |
| NPC listed after it is gone | Loaded active/hidden checks or remote quest gate stale |
| Wrong C-Sec map floor | Player bool 31255 false lower / true upper |
| Correct C-Sec image, wrong NPC floor | Requested page, actor Z filter, or remote floor position |
| Corrupt/missing texture after adding code | Local mip offsets, startup rooting, storage dependency, TOC |
| Build succeeded but old behavior remains | Wrong installed target, stale helper binary, wrong mounted package, old stage copied |
| Deployment hash check fails | Baseline or stage changed; use a fresh revision and investigate, rather than removing checks |

## A one-page work order for your next map edit

Fill this out before starting:

```text
What visible behavior am I changing?

Which installed package(s) must change?

Which mounted source package supplies the actor/quest/geometry?

Fresh revision directory:

Files backed up and their hashes:

Texture source / padding / calibration, if applicable:

Live provider change:

Remote catalogue change:

Literal layout label:

Floor and elevator coverage:

Compiled-result checks:

TOC and manifest checks:

In-game saves/quest states tested:

Result and screenshots:

Restore command for this revision:
```

For the full installed constants, complete handler source, every remote NPC rule, exact map resources, and original backup/fingerprint details, use the companion [Project Variety maps reference](C:/Users/Scottie/source/repos/LegendaryExplorer/PROJECT_VARIETY_MAPS_REFERENCE.md).
