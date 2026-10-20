# Native Scaleform editor: implementation and remaining work

Package Editor now opens Scaleform movies in a native C#/WPF visual designer, with a viewport, layers, an artwork library, and direct manipulation. The native editor does not yet provide full JPEXS editing coverage. Binary round-trip checks establish preservation of the tested files; they do not establish correct game behavior. Runtime testing has not been performed.

## Using the editor

1. Open a package and select a non-default `GFxMovieInfo` or `BioSWF` export, then open its **Scaleform** tab.
2. Use the default **Design** tab. Select an element on the canvas or in **Layers**, then drag it to move it. Drag the bottom-right corner handle to resize it. The inspector provides **X**, **Y**, **Width**, **Height**, and **Rotation** fields for exact changes; press Enter or leave a field to commit it.
3. Select editable text and type in the inspector's **Text** box. Its formatting controls change **Colour**, **Movie font**, **Font size**, **Letter spacing**, and **Align** (left, right, centre, justify) for the whole field. Press Enter or leave a numeric field to commit it. Fonts reuse embedded/shared movie fonts; letter spacing is saved using Scaleform HTML text. Formatting supports Undo/Redo, movie draft navigation, and Apply changes, including newly created text. Open **Library** to reuse movie artwork. **Add UI** creates panels, editable text, buttons and images: choose a package **Texture2D** or PNG/JPG/BMP image, then drag the creation card onto the canvas or choose **Add to canvas**. Images are embedded copies with transparency; the source Texture2D is retained. Text reuses an available opening-frame movie font. Buttons with no available font have a background without a label. Names identify new elements in behavior selectors and are automatically made unique.
4. Double-click a group or choose **Edit group contents** to work inside it. **↑ Up one group** (or Alt+Up on the canvas) returns to the containing group, restoring its frame and selecting the exited instance. This also works when entering a nested group directly from expanded Layers. **← Movie** returns to the main canvas. Each movie draft retains its own navigation trail. Groups are reusable symbols: changing their contents affects every instance of that group.
5. Scrub the frame slider to choose the animation frame to edit. If an opening frame is intentionally blank, the designer initially chooses a visible authoring pose; every original frame remains available on the slider. An edit to an inherited pose adds a transform keyframe at the chosen frame, preserving earlier frames.
6. Use **Undo** and **Redo** to revise draft edits. The mouse wheel zooms, middle-drag pans, and **Fit canvas** frames the canvas. Movie drafts remain separate when navigating between exports.
7. Open **Behaviors** to give UI elements actions without writing code. Choose **+ Button click**, select the named button, and drag actions such as **Show element**, **Change text**, or **Move element** onto the graph. New actions automatically connect after the selected action. Click an output circle followed by an input circle to change a connection. **Check a value** has Yes/No branches. **+ Reusable function** creates a named action sequence, which **Call function** runs from another behavior. **Movie opens** runs a sequence at a selected frame. The click event's installation frame defaults to the current design frame; the button must exist then. Existing game scripts are retained.
8. Choose **Apply changes**. This compiles and reopens artwork and authored AVM1 behaviors before replacing its embedded `RawData` or `Data` property in the package held in memory. Use Package Editor **Save** or **Save As** to persist the package, then test the changed interface in the target game.

**Advanced resources & scripts** retains the searchable tag tree, supported string/background edits, and AVM1 bytecode assembly. Frame rate and package references are under **Movie settings**. **Export movie…** writes compiled movie bytes separately; **Export tag bytes…** exports the selected payload. **External editor** retains the existing JPEXS workflow for operations not implemented natively. The native parser, compiler, and controls do not require Java or WebView2.

Select a named button or group in Design and choose **Add click action** to open its event directly. When the same button name occurs in multiple groups, the shortcut retains the selected element's full location. Newly created elements use a color picker and accept ordinary names with spaces; runtime names are prepared automatically.

## Implemented capabilities

- Read and write uncompressed SWF/GFx (`FWS`, `GFX`) and zlib-compressed variants (`CWS`, `CFX`). No-change compilation returns the original movie bytes, including original compression.
- Evaluate root and nested sprite display lists, navigate frames and groups, and select instances by viewport or layers. Move, resize and rotate instances through their serialized placement matrices. Undo and redo retain separate drafts per movie.
- Preview supported vector artwork, embedded and referenced images, text, sprites, clipping masks, and supported color/visibility transforms with native WPF drawing. The preview uses movie data without executing ActionScript; unsupported artwork produces preview notices.
- Create additional instances of existing visual symbols from Library, and remove instances by adding timeline removal records. Existing symbol definitions, original placement records, scripts, and unknown trailing placement data remain preserved. Recursive group insertion is rejected.
- Create native rectangle artwork, editable text, panel/button movie clips, and embedded lossless images. Imported image pixels use premultiplied ARGB in `DefineBitsLossless2`, with bitmap-filled vector shapes. New artwork IDs are retained across draft rebuilding and removal of other additions; imported/shared IDs are reserved. Existing fonts and original definitions remain intact.
- Author visual AVM1 event/function graphs with connected actions and conditional branches. Compile click events, frame events, reusable functions, visibility, text, position, timeline playback, frame navigation, stored values and function calls to actual action records. Graph metadata is stored inside the owned action records so saved files reopen for visual editing. Unowned action records remain byte-identical. Click handlers install once per runtime instance and call an existing dynamic handler before authored actions.
- Cache movie dictionary indexes, timeline scenes, drawings and unchanged static artwork; defer external texture decoding until used. Frame/group navigation retains the working document, selection reuses the drawing, and drag/keyframe edits retain static resources. Texture preview decodes pixels directly instead of encoding then decoding PNG files.
- Browse nested sprites and tags, search resource names/categories/IDs/text, inspect payloads, and inspect package reference information.
- Edit frame rate, background RGB, frame labels, metadata, and supported `DefineEditText` initial strings while retaining surrounding data.
- Disassemble, edit, validate, and assemble AVM1 bytecode in `DoAction` and `DoInitAction`. Symbolic branch labels are relocated; nested action containers and unknown opcode bytes are retained. This is bytecode assembly, not an ActionScript source decompiler or source compiler.
- Preserve untouched tag records, opaque extensions, identifiers, and trailing data; rebuild modified tag and containing sprite lengths. Reparse compiled output and compare intended tag payloads before applying it.
- Keep movie drafts separate from package properties until apply. Refuse to overwrite embedded movie bytes changed elsewhere after opening the draft.

Parser/assembly validation checks bounds, record framing, supported operand layouts, nested blocks, and branch targets. It does not prove stack types, external game callbacks, resource availability, or game logic.

## Preview and editing limits

The canvas is an authoring preview, not the Scaleform runtime. It does not run ActionScript, game callbacks, initialization scripts, dynamic menus, or game-supplied text/data. Scrubbing selects recorded timeline states; script-driven visibility and runtime animation can differ in game.

Rendering coverage is still incomplete. Complex fonts and glyph layout, mixed rich-text formatting, morphs, filters, blend modes, gradient details, imported symbols, and externally supplied assets require further fidelity work. Available package textures and supported shared artwork can be previewed; unresolved resources remain reported approximations. Editable text supports its initial string and whole-field colour, size, movie font, letter spacing, and alignment. Its preview uses approximate system fonts and the first HTML formatting overrides; mixed styles, shaping with nonzero letter spacing, localization, and runtime substitutions can differ in game. Static glyph text and font/glyph authoring remain outside these inspector controls.

Library reuses artwork already defined in the movie; Add UI also creates the element types above. General path/morph drawing, font and glyph creation/replacement, audio/video authoring and a full ActionScript source compiler remain unfinished. Visual behaviors compile AVM1 (ActionScript 1/2), not AS3 or arbitrary Unreal/game callbacks. One click flow controls each named target; add more actions or call reusable functions in that flow. Execution loops and recursive function calls are rejected. Game scripts can later override an authored handler, and script-driven runtime content can differ from the preview. Applying a draft verifies the supported binary changes and preserves unrelated records; it cannot establish crash-free runtime behavior.

## Responsiveness verification

The read-only WPF probe measured the actual AreaMap package before and after the cache changes, including layout and drawing. The baseline uses one process; final values are medians of three fresh processes. Timing starts after the package is open:

| Interaction | Previous build | Updated build |
|---|---:|---:|
| Open and first layout | 6,949 ms | 1,894 ms |
| Select Close button | 1,259 ms | 16 ms |
| Move selected element | 1,234 ms | 21 ms |
| Undo move | 1,236 ms | 25 ms |
| Change frame | 348–953 ms | 15–17 ms |
| Revisit an already viewed movie | — | 19 ms |

A 24-update continuous drag averaged **1.57 ms** per update (worst observed **2.24 ms** across the three runs), including WPF layout/render. All **29** referenced Texture2D previews matched the earlier PNG-based decoder pixel for pixel in all three runs. These timings describe this machine and fixture, not every movie or hardware configuration. Local evidence: [results](../Build/scaleform-ui-preview/performance-results.json), [before](../Build/scaleform-ui-preview/performance-before.log), [final run](../Build/scaleform-ui-preview/performance-complete3.log).

## UI creation and visual behavior verification

The updated focused suites pass **52 core tests and 13 WPF tests**. These include an independent AVM1 stack interpreter executing emitted functions, click handlers, condition branches, text/position/visibility changes and timeline calls; graph save/reopen/edit/remove; invalid targets and installation frames; duplicate click events; handler preservation; generated-artwork ID stability; new UI and behavior drafts navigating between movies and applying together; and pixel checks for embedded image transparency, shape bounds and solid colors. Local logs: [core](../Build/scaleform-authoring-core-tests.log), [WPF](../Build/scaleform-authoring-ui-tests.log).

A real AreaMap package-copy check imports the existing `GUI_SF_ME2_AreaMap.ME2_AreaMap_G9` Texture2D as new embedded artwork, adds a panel and button at the authoring frame, authors a click event calling a reusable show-panel function, applies the draft and saves/reopens a compressed package copy. All three new elements and both authored flows survive. All **3,269 original movie leaf payloads** and **13,096 other exports** remain identical; the source package hash is unchanged. The copy is [AreaMapNewUiAndBehavior.pcc](../Build/scaleform-authoring-probe/AreaMapNewUiAndBehavior.pcc); [result.json](../Build/scaleform-authoring-probe/result.json) records the check. No game runtime test was performed.

## Read-only corpus verification

The local probe inspected **81 packages**: 71 existing PC package samples and 10 bounded installed-game packages. It found **327 movie exports** with no package-read or movie-parse failures.

| Game | Movie exports | Observed signature/version | Exact no-op passes | Non-script edit/reparse passes |
|---|---:|---|---:|---:|
| ME1 | 22 | CWS/8, FWS/8 | 22 | 22 |
| ME2 | 30 | GFX/8 | 30 | 30 |
| ME3 | 9 | GFX/8, GFX/10 | 9 | 9 |
| LE1 | 143 | GFX/8, GFX/10 | 143 | 143 |
| LE2 | 63 | GFX/8, GFX/9 | 63 | 63 |
| LE3 | 60 | GFX/8, GFX/9, GFX/10, GFX/15 | 60 | 60 |

These are export instances, including repeated content in installed packages and test samples, rather than a claim of 327 unique movies. One UDK sample package contained no target movie exports.

For every movie, parse/compile produced byte-identical output. The probe then changed one frame label, background, or supported text field in memory, compiled, reopened, and verified every untouched leaf-tag payload. All **327/327** passed.

All **19,383 AVM1 streams** passed structural validation and byte-identical disassemble/assemble checks: **16,580 `DoAction`** and **2,803 `DoInitAction`**. One real movie per game also passed an assembled constant-pool string change, `SetActionBytes`, compile/reopen, exact edited-script comparison, and untouched-payload comparison. No `DoABC` or `DoABCDefine` tags occurred in this corpus; this does not prove they never occur in other files.

Observed opaque GFx extensions included tags 1000, 1001, 1002, 1003, and 1009. Installed samples included LE1 startup/inventory, LE2 startup, LE3 startup/fonts, and screenshot package `BioD_Cat002_485HillPath.pcc`, export 570, `GUI_SF_WpnReticleHeavy.WpnReticleHeavy` (GFX version 15, 2,576 bytes).

No original package was saved. SHA-256 values before and after probing matched for all 81 source packages. The probe did not test gameplay, rendering fidelity, or deployed-package behavior.

A separate integration check changed export 570's background from `333333` to `323333` through `ScaleformExportSession.Apply`, then used the normal compressed package serializer to write `Build/scaleform-probe/ReticleRoundTrip.pcc`. Reopening that copy retained the expected color and all 62 untouched movie leaf payloads. All 2,241 other exports retained byte-identical `Data`; the target's other properties and binary data, names, 987 import headers, export identities/object references, texture references, additional package dependencies, and 8,968 dependency-table bytes were unchanged. The original package's SHA-256 remained `534D83D5C321D88B33CBA6E95576DC86BA88E7CF61B8A0C9C36B00C4AC2A294C`. See [package-roundtrip.json](../Build/scaleform-probe/package-roundtrip.json) and [package-roundtrip.md](../Build/scaleform-probe/package-roundtrip.md). This copy was not deployed or tested in game.

Local verification artifacts are [corpus-results.md](../Build/scaleform-probe/corpus-results.md), [corpus-results.json](../Build/scaleform-probe/corpus-results.json), and [Program.cs](../Build/scaleform-probe/Program.cs). The extracted screenshot fixture is `Build/scaleform-probe/wpnReticleHeavy.gfx`. These files are under ignored `Build`; they are local evidence, not permanent regression fixtures.

## Visual editing verification

The visual-edit probe used the actual LE3 movies from both screenshots: `GUI_SF_ME2_AreaMap.ME2_AreaMap` in `BioD_CitHub_Underbelly.pcc`, export 654, and `GUI_SF_WpnReticleHeavy.WpnReticleHeavy` in `BioD_Cat002_485HillPath.pcc`, export 570. The AreaMap movie is 1280 × 1024 with 26 frames and 75 sprite definitions; its first frame contains 18 effective root instances.

All **1,420 placement tags** (**1,403 AreaMap** and **17 reticle**) were translated by +13.25, -7.5 pixels, compiled, and reopened. The intended matrices and original character IDs, instance names, depth and clipping depth were retained. Every unedited leaf payload was identical. Setting an existing decoded matrix without changing it preserved the complete original bytes; moving and then restoring existing matrices also returned the original file byte for byte. The probe rebuilt **181 root/sprite scenes** across the two movies.

Four additional checks added and removed existing artwork on the root timeline and inside a sprite in each movie, preserving all original leaf payloads. Four inherited-frame checks added transform keyframes on AreaMap's root and two sprites and the reticle's sprite: the earlier frame's matrix stayed unchanged, while the selected frame retained its new matrix and instance identity.

A separate package-copy check moved the real AreaMap `PC_CloseBtn` instance from **(959.5, 768.45)** to **(997, 756.2)**, applied the movie in memory, wrote `Build/scaleform-visual-probe/AreaMapVisualRoundTrip.pcc`, and reopened that copy. The intended transform survived; all **3,268 other movie leaf payloads** and all **13,096 other export data blobs** remained identical. Package names, import headers, target binary data and the target's other properties also remained identical. The installed source package hash stayed **`96975E481EB75F43AF63E10C0021534E2D9192DD50858DAC98912A7CC52176F5`** (SHA-256). The copy was not deployed or tested in game.

Checked-in regression coverage includes [ScaleformSceneTests.cs](../LegendaryExplorer/LegendaryExplorerCore.Tests/ScaleformSceneTests.cs), [ScaleformVisualEditorTests.cs](../LegendaryExplorer/LegendaryExplorer.Tests/UserControls/ScaleformVisualEditorTests.cs), [NativeScaleformEditorTests.cs](../LegendaryExplorer/LegendaryExplorer.Tests/UserControls/NativeScaleformEditorTests.cs), and [ScaleformArtworkTests.cs](../LegendaryExplorer/LegendaryExplorer.Tests/UserControls/ScaleformArtworkTests.cs). It exercises binary placement preservation, nested timelines, keyframes, transform editing, parent-scaled movement, library insertion/removal, text edits, undo/redo, draft navigation, failed applies, masks, images and native drawing. Final focused runs passed **10/10 UI tests** and **37/37 core tests**, including an opaque wrapper with an invisible nested intro, pending numeric fields, literal angle brackets, HTML line breaks, and later-frame edits to new library instances. Local results are [UI TRX](../Build/scaleform-visual-probe/test-results/scaleform-visual-ui-final.trx) and [core TRX](../Build/scaleform-visual-probe/test-results/scaleform-core-final.trx).

Detailed local evidence is [validation.md](../Build/scaleform-visual-probe/validation.md), [visual-movie-roundtrip.json](../Build/scaleform-visual-probe/visual-movie-roundtrip.json), [visual-package-roundtrip.json](../Build/scaleform-visual-probe/visual-package-roundtrip.json), and [scaleform-visual-ui.trx](../Build/scaleform-visual-probe/test-results/scaleform-visual-ui.trx). The ignored probe project can be rerun with `dotnet run --project Build/scaleform-visual-probe/Probe.csproj --no-restore -- --verify` after updating its built-library references if the build output location changes.

Run the checked-in regression tests from the repository root:

```powershell
dotnet test LegendaryExplorer/LegendaryExplorerCore.Tests/LegendaryExplorerCore.Tests.csproj -c WinDebug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~Scaleform"
dotnet test LegendaryExplorer/LegendaryExplorer.Tests/LegendaryExplorer.Tests.csproj -c WinDebug --no-restore --filter "FullyQualifiedName~NativeScaleformEditorTests|FullyQualifiedName~ScaleformVisualEditorTests|FullyQualifiedName~ScaleformArtworkTests|FullyQualifiedName~ScaleformBehaviorEditorTests|FullyQualifiedName~ScaleformElementArtworkTests"
```

The local corpus probe can be rerun while its ignored project and the original local game paths remain available:

```powershell
dotnet run --project Build/scaleform-probe/Probe.csproj -c WinDebug -p:Platform=x64 -p:UseAppHost=false --no-restore
```

## Reference projects

- [ScaleformTools](https://github.com/Sopitive/ScaleformTools) targets Halo MCC. Its editor/parser and AS3 tooling are TypeScript with a React UI; WPF/WebView2 supplies its C# desktop shell. It is useful design/reference material, but its stated format support is not Mass Effect compatibility evidence.
- [SwfLib](https://github.com/SavchukSergey/SwfLib) provides C# SWF parsing, modification, and writing. Its README lists AS2 support and AS3 as planned; it is not a complete JPEXS replacement.
- [JPEXS feature documentation](https://github.com/jindrapetrik/jpexs-decompiler/wiki/Features) supplies the broader feature baseline: graphics, fonts, audio, timelines, source and bytecode editing, import/export, and GFx extensions. Its source-editing documentation also identifies experimental limitations.

## Remaining JPEXS coverage and implementation sequence

1. **Visual fidelity and timeline authoring:** extend the implemented display-list designer and renderer to cover remaining fonts, morphs, filters, blend modes, gradients, imported artwork and runtime-dependent content. Add richer timeline authoring and verify rendered output against representative menus and HUDs. Current placement editing, groups, frame selection, masks, library reuse, and undo/redo are implemented, but full renderer fidelity remains unfinished.
2. **Resource editing:** add shape/morph paths, embedded and external images, fonts/glyphs/layout, static text, buttons, and sound import/export. Keep identifiers and references stable; provide supported operations per tag instead of silently treating opaque data as editable.
3. **Source tooling:** add AVM1 control-flow/stack analysis, decompilation, a language syntax tree, source compilation, and verified bytecode generation. Add AVM2/ABC parsing, assembly, decompilation, and source compilation if an expanded supported corpus requires it. Preserve unedited scripts throughout.
4. **Remaining containers and dependencies:** implement bounded LZMA `ZWS` handling and expand shared/imported movie and external-asset resolution. Continue validating package texture/shared-artwork preview against all games and support dependency editing without breaking references.
5. **Runtime acceptance:** establish representative tests for all six games, localization/font sets, menus and HUDs, animation/input paths, script/game callbacks, and save/reload. Compare original and edited package behavior in game, preserve diagnostics for failures, and document supported operations. Compilation and structural validation alone cannot establish crash-free output.

Full parity remains an implementation and validation objective. The current editor should be described by the capabilities above, not as a completed replacement.
