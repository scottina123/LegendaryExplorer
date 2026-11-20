using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.AssetViewer;
using LegendaryExplorer.Tools.PackageEditor.Experiments;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MessageBox = LegendaryExplorer.SharedUI.ThemedMessageBox;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// Fork: the actor menu, built for the actor (outliner rows and viewport right-clicks). It has upstream's actions (Delete, Duplicate, Copy, Paste,
/// Upgrade to Actor, Focus Camera, Open in Package Editor; upstream's delete and duplicate replace the fork's Trash Actor and Clone Tree) and the fork's:
/// the Create submenu, per-export Properties/Metadata, Snap Selected Actor Here, the live material and morph editors, lighting channels, collision and
/// shadow flags, Generate Lightmass, Strip LightMap/ShadowMap, Replace Static/Skeletal Mesh, Edit volume geometry and the gesture animation importer
/// </summary>
public partial class LevelEditor
{
    private static readonly (string PropertyName, string DisplayName)[] LightingChannelMenuItems =
    [
        ("bInitialized", "bInitialized"),
        ("BSP", "BSP"),
        ("Static", "Static"),
        ("Dynamic", "Dynamic"),
        ("CompositeDynamic", "Composite Dynamic"),
        ("Skybox", "Skybox"),
        ("Unnamed_1", "Unnamed 1"),
        ("Unnamed_2", "Unnamed 2"),
        ("Unnamed_3", "Unnamed 3"),
        ("Unnamed_4", "Unnamed 4"),
        ("Unnamed_5", "Unnamed 5"),
        ("Unnamed_6", "Unnamed 6"),
        ("Cinematic_1", "Cinematic 1"),
        ("Cinematic_2", "Cinematic 2"),
        ("Cinematic_3", "Cinematic 3"),
        ("Cinematic_4", "Cinematic 4"),
        ("Cinematic_5", "Cinematic 5"),
        ("Cinematic_6", "Cinematic 6"),
        ("Cinematic_7", "Cinematic 7"),
        ("Cinematic_8", "Cinematic 8"),
        ("Cinematic_9", "Cinematic 9"),
        ("Cinematic_10", "Cinematic 10"),
        ("Gameplay_1", "Gameplay 1"),
        ("Gameplay_2", "Gameplay 2"),
        ("Gameplay_3", "Gameplay 3"),
        ("Gameplay_4", "Gameplay 4"),
        ("Crowd", "Crowd")
    ];

    private static readonly (string PropertyName, string DisplayName)[] CollisionMenuItems =
    [
        ("CollideActors", "Collide Actors"),
        ("BlockActors", "Block Actors"),
        ("BlockRigidBody", "Block Rigid Body"),
        ("BlockNonZeroExtent", "Block Non Zero Extent"),
        ("BlockZeroExtent", "Block Zero Extent")
    ];

    private static readonly (string PropertyName, string DisplayName)[] LightingMenuItems =
    [
        ("bAcceptsLights", "Accepts Lights"),
        ("bAcceptsDynamicLights", "Accepts Dynamic Lights")
    ];

    private static readonly (string PropertyName, string DisplayName)[] ShadowMenuItems =
    [
        ("bCastDynamicShadow", "Cast Dynamic Shadow"),
        ("CastShadow", "Cast Shadow"),
        ("bCastHiddenShadow", "Cast Hidden Shadow")
    ];

    #region Outliner

    private void MeshExportsList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement fe)
        {
            e.Handled = true;
            return;
        }

        ActorProxy actor = null;
        DependencyObject current = fe;
        while (current is not null)
        {
            if (current is ListBoxItem { DataContext: ActorProxy a })
            {
                actor = a;
                break;
            }
            current = VisualTreeHelper.GetParent(current);
        }

        e.Handled = true;
        if (actor is null) return;

        SelectActor(actor, false);
        var contextMenu = BuildActorContextMenu(actor);
        contextMenu.PlacementTarget = fe;
        contextMenu.Placement = PlacementMode.MousePoint;
        contextMenu.IsOpen = true;
    }

    private void MeshExportsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject current) return;

        if (ItemsControl.ContainerFromElement(MeshExportsList, current) is ListBoxItem { DataContext: ActorProxy actor })
        {
            // Selecting alone does nothing when the row was already selected, so focus explicitly on every double-click
            SelectActor(actor, false);
            if (actor.HasSpatialRepresentation) FocusOnBounds(actor.GetBounds());
            e.Handled = true;
        }
    }

    #endregion

    #region Viewport

    /// <summary>A right-click on empty viewport space: the Create submenu, Snap Selected Actor Here and Paste</summary>
    private void OnViewportRightClickViewport()
    {
        if (IsDrawingVolume || !HasAnyFileOpen) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            Point viewportPoint = Mouse.GetPosition(SceneViewer);
            viewportMenuPoint = viewportPoint;
            var contextMenu = new ContextMenu
            {
                PlacementTarget = SceneViewer,
                Placement = PlacementMode.MousePoint
            };

            var snapItem = new MenuItem
            {
                Header = "Snap Selected Actor Here",
                IsEnabled = CanMoveSelectedActor()
            };
            snapItem.Click += (_, _) => SnapSelectedActorToViewportPoint(viewportPoint);
            contextMenu.Items.Add(snapItem);
            contextMenu.Items.Add(PasteMenuItem(contextMenu));
            contextMenu.Opened += (_, _) => NotifyPasteRefusal();

            contextMenu.IsOpen = true;
        });
    }

    #endregion

    #region Actor menu

    private ContextMenu BuildActorContextMenu(ActorProxy actor, Point? viewportPoint = null)
    {
        var contextMenu = new ContextMenu { DataContext = actor };
        contextMenu.Opened += (_, _) =>
        {
            actor.NotifyRefusals();
            NotifyPasteRefusal();
        };
        ExportEntry actorExport = actor.Export;
        bool canEdit = CanEditScene && !actor.IsReadOnly;

        if (actorExport is not null)
        {
            AddPropertiesMenuItems(contextMenu, actorExport, $"{actorExport.UIndex}: {actorExport.ObjectName.Instanced} ({actorExport.ClassName})");
            if (canEdit && VolumeGeometryEdit.FindComponent(actorExport) != null)
            {
                var editVolumeItem = new MenuItem { Header = "Edit volume geometry..." };
                editVolumeItem.Click += (_, _) => BeginVolumeEdit(actor);
                contextMenu.Items.Add(editVolumeItem);
            }

            var componentExports = actor.Components.Select(component => component.Export).Where(export => export is not null && export != actorExport).Distinct().ToList();
            if (componentExports.Count > 0)
            {
                contextMenu.Items.Add(new Separator());
                foreach (ExportEntry componentExport in componentExports)
                {
                    AddPropertiesMenuItems(contextMenu, componentExport, $"{componentExport.UIndex}: {componentExport.ObjectName.Instanced} ({componentExport.ClassName})");
                }
            }
            contextMenu.Items.Add(new Separator());
        }

        // Upstream's actions
        if (CanEditScene)
        {
            contextMenu.Items.Add(CommandMenuItem("Delete Actor", DeleteActorCommand, actor, actor.DeleteRefusal, "Delete",
                "Removes the actor when changes are committed (the fork's Trash Actor)"));
            contextMenu.Items.Add(CommandMenuItem("Duplicate", DuplicateActorCommand, actor, actor.DuplicateRefusal, null,
                "A copy next to it, committed with the other changes (the fork's Clone Tree). Alt-drag a gizmo axis to duplicate while moving"));
            //the viewport menu offers Duplicate only (Ctrl+C still copies); the outliner keeps Copy for pasting into another window
            if (!viewportPoint.HasValue)
            {
                var copyItem = CommandMenuItem("Copy", CopyActorCommand, actor, actor.CopyRefusal, "Ctrl+C", null);
                copyItem.IsEnabled = actor.CanCopy;
                contextMenu.Items.Add(copyItem);
            }
            contextMenu.Items.Add(PasteMenuItem(contextMenu));
            if (actor.IsCollectionMember)
            {
                contextMenu.Items.Add(CommandMenuItem("Upgrade to Actor", UpgradeActorCommand, actor, actor.UpgradeRefusal, null,
                    "Makes this collection member an actor of its own (the StaticMeshActor or light it was cooked from) now. If it has other uncommitted changes, it's made one when changes are committed"));
            }
            contextMenu.Items.Add(new Separator());
        }

        if (actor.HasSpatialRepresentation)
        {
            var focusItem = new MenuItem { Header = "Focus Camera", InputGestureText = "F" };
            focusItem.Click += (_, _) =>
            {
                SelectActor(actor, false);
                FocusOnBounds(actor.GetBounds());
            };
            contextMenu.Items.Add(focusItem);
        }

        if (viewportPoint.HasValue)
        {
            var snapHereItem = new MenuItem
            {
                Header = "Snap Selected Actor Here",
                IsEnabled = CanMoveSelectedActor()
            };
            Point snapPoint = viewportPoint.Value;
            snapHereItem.Click += (_, _) => SnapSelectedActorToViewportPoint(snapPoint);
            contextMenu.Items.Add(snapHereItem);
        }

        if (CanEditScene)
        {
            var openPEItem = new MenuItem
            {
                Header = "Open in Package Editor",
                Icon = TryFindResource("PackageEditorMenuIcon"),
                IsEnabled = actor.PackageEditorRefusal is null,
                ToolTip = actor.PackageEditorRefusal
            };
            ToolTipService.SetShowOnDisabled(openPEItem, true);
            openPEItem.Click += (_, _) =>
            {
                if (actor.Export is { } export) OpenInPackageEditor(export);
            };
            contextMenu.Items.Add(openPEItem);
        }

        if (actorExport is null || !CanEditScene) return contextMenu;

        if (CanOpenLiveMaterialEditor(actor))
        {
            var liveMaterialEditorItem = new MenuItem { Header = "Open Live Material Editor..." };
            liveMaterialEditorItem.Click += (_, _) => OpenActorPreviewEditor(actor, openMorphEditor: false);
            contextMenu.Items.Add(liveMaterialEditorItem);
        }

        if (ActorHasEditableMorph(actor))
        {
            var morphEditorItem = new MenuItem { Header = "Open Morph Editor..." };
            morphEditorItem.Click += (_, _) => OpenActorPreviewEditor(actor, openMorphEditor: true);
            contextMenu.Items.Add(morphEditorItem);
        }

        // Lights: their channels, shadows and Enabled are edited through upstream's EditableLight (undoable, committed with the other changes)
        if (actor is ILightProxy { Light: { } light })
        {
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(BuildLightChannelsMenu(light, canEdit));
            contextMenu.Items.Add(BuildLightShadowMenu(light, canEdit));
            var enabledItem = new MenuItem
            {
                Header = "Enabled",
                IsCheckable = true,
                IsChecked = light.IsEnabled,
                IsEnabled = canEdit,
                StaysOpenOnClick = true
            };
            enabledItem.Click += (_, _) => EditMenuLight(actor, () => light.IsEnabled = enabledItem.IsChecked);
            contextMenu.Items.Add(enabledItem);
        }
        else if (GetLightingChannelsTargetExport(actor) is { } lightingTargetExport)
        {
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(BuildLightingChannelsMenu(actor, lightingTargetExport, canEdit));
        }

        ExportEntry smcExport = GetStaticMeshComponentExport(actor);
        if (smcExport is not null)
        {
            if (GetCollisionMenu(actor, smcExport, canEdit) is { } collisionMenu) contextMenu.Items.Add(collisionMenu);
            contextMenu.Items.Add(BuildShadowMenu(actor, smcExport, canEdit));

            if (GetStaticLightingComponents(actor).Length > 0)
            {
                var generateLightmassMenu = new MenuItem
                {
                    Header = "Generate Lightmass",
                    IsEnabled = canEdit && !IsBusy,
                    ToolTip = "The Level Editor's own static lighting baker (Create Lightmass), for this actor"
                };
                generateLightmassMenu.Items.Add(BuildActorLightmassResolutionMenu(actor,
                    "Automatic (detail-aware)", StaticLightingMappingMode.Automatic,
                    "Uses LightMap2D for architectural, large, or broad low-poly receivers and LightMap1D for compact or dense receivers."));
                generateLightmassMenu.Items.Add(BuildActorLightmassResolutionMenu(actor,
                    "LightMap2D (texture)", StaticLightingMappingMode.Texture2D,
                    "Generates a texture mapping at the selected resolution, repairs isolated collapsed UV triangles, and falls back to LightMap1D when no valid runtime lightmap UV exists."));
                var vertexLightmassItem = new MenuItem
                {
                    Header = "LightMap1D (vertex)",
                    ToolTip = "Forces interpolated per-vertex lighting; no lightmap texture is generated."
                };
                vertexLightmassItem.Click += async (_, _) => await GenerateStaticLightingForActor(actor, LightmassResolution, StaticLightingMappingMode.Vertex1D);
                generateLightmassMenu.Items.Add(vertexLightmassItem);
                contextMenu.Items.Add(generateLightmassMenu);
            }

            var stripLightMapItem = new MenuItem { Header = "Strip LightMap (EXPERIMENTAL)", IsEnabled = canEdit };
            stripLightMapItem.Click += (_, _) => StripStaticMeshComponentLightmap(actor, smcExport);
            contextMenu.Items.Add(stripLightMapItem);

            var stripShadowMapItem = new MenuItem { Header = "Strip ShadowMap (EXPERIMENTAL)", IsEnabled = canEdit };
            stripShadowMapItem.Click += (_, _) => StripStaticMeshComponentShadowmap(actor, smcExport);
            contextMenu.Items.Add(stripShadowMapItem);

            var replaceMeshItem = new MenuItem
            {
                Header = "Replace Static Mesh...",
                IsEnabled = canEdit,
                ToolTip = "Choose another static mesh for this actor (an uncommitted, undoable change, like the Mesh panel's Choose)"
            };
            replaceMeshItem.Click += (_, _) => ReplaceStaticMesh(actor, smcExport);
            contextMenu.Items.Add(replaceMeshItem);
        }

        var skeletalMeshComponents = GetSkeletalMeshComponentExports(actor);
        if (skeletalMeshComponents.Count > 0)
        {
            contextMenu.Items.Add(new Separator());
            if (skeletalMeshComponents.Count == 1)
            {
                var replaceSkeletalItem = new MenuItem { Header = "Replace Skeletal Mesh...", IsEnabled = canEdit };
                replaceSkeletalItem.Click += (_, _) => ReplaceSkeletalMesh(actor, skeletalMeshComponents[0]);
                contextMenu.Items.Add(replaceSkeletalItem);
            }
            else
            {
                var skeletalMenu = new MenuItem { Header = "Replace Skeletal Mesh" };
                foreach (var smc in skeletalMeshComponents)
                {
                    var subItem = new MenuItem { Header = $"{smc.UIndex}: {smc.ObjectName.Instanced}", IsEnabled = canEdit };
                    subItem.Click += (_, _) => ReplaceSkeletalMesh(actor, smc);
                    skeletalMenu.Items.Add(subItem);
                }
                contextMenu.Items.Add(skeletalMenu);
            }
        }

        AddGestureImporterItems(contextMenu, actor, canEdit);
        return contextMenu;
    }

    private static MenuItem CommandMenuItem(string header, ICommand command, ActorProxy actor, string refusal, string gesture, string toolTip)
    {
        var item = new MenuItem
        {
            Header = header,
            Command = command,
            CommandParameter = actor,
            InputGestureText = gesture,
            ToolTip = refusal ?? toolTip
        };
        ToolTipService.SetShowOnDisabled(item, true);
        return item;
    }

    /// <summary>Upstream's Paste: a copy of the copied actor, where the menu was opened</summary>
    private MenuItem PasteMenuItem(ContextMenu menu)
    {
        var item = new MenuItem
        {
            Header = "Paste",
            InputGestureText = "Ctrl+V",
            Command = PasteActorCommand,
            CommandParameter = menu
        };
        menu.Opened += (_, _) =>
        {
            item.IsEnabled = CanPaste;
            item.ToolTip = PasteRefusal;
        };
        ToolTipService.SetShowOnDisabled(item, true);
        return item;
    }

    private void AddPropertiesMenuItems(ContextMenu contextMenu, ExportEntry export, string label)
    {
        var parentItem = new MenuItem { Header = label.Replace("_", "__") };

        var propsItem = new MenuItem { Header = "Properties" };
        propsItem.Click += (_, _) => LoadExportIntoTabs(export, 0);
        parentItem.Items.Add(propsItem);

        var metaItem = new MenuItem { Header = "Metadata" };
        metaItem.Click += (_, _) => LoadExportIntoTabs(export, 1);
        parentItem.Items.Add(metaItem);

        contextMenu.Items.Add(parentItem);
    }

    private void AddGestureImporterItems(ContextMenu contextMenu, ActorProxy actor, bool canEdit)
    {
        object icon = TryFindResource("AnimationImporterMenuIcon");
        if (actor.Export.ClassName == "SFXSkeletalMeshActor" || actor is SkeletalMeshActorProxy && actor.Export.IsA("SFXSkeletalMeshActor"))
        {
            contextMenu.Items.Add(new Separator());
            var gestureItem = new MenuItem { Header = "Open Gesture Animation Importer...", Icon = icon, IsEnabled = canEdit };
            gestureItem.Click += (_, _) => new GestureAnimationImporterDialog(actor.Export, this).ShowDialog();
            contextMenu.Items.Add(gestureItem);
            return;
        }
        if (actor is not SFXStuntActorProxy) return;

        var gestureModules = actor.Export.FileRef.Exports
            .Where(e => e.idxLink == actor.Export.UIndex && e.ClassName == "SFXModule_Gestures")
            .ToList();
        if (gestureModules.Count == 0) return;
        contextMenu.Items.Add(new Separator());
        if (gestureModules.Count == 1)
        {
            var gestureItem = new MenuItem { Header = "Open Gesture Animation Importer...", Icon = icon, IsEnabled = canEdit };
            gestureItem.Click += (_, _) => new GestureAnimationImporterDialog(gestureModules[0], this).ShowDialog();
            contextMenu.Items.Add(gestureItem);
        }
        else
        {
            var gestureMenu = new MenuItem { Header = "Open Gesture Animation Importer", Icon = icon };
            foreach (var module in gestureModules)
            {
                var subItem = new MenuItem { Header = $"{module.UIndex}: {module.ObjectName.Instanced}", Icon = TryFindResource("AnimationImporterMenuIcon"), IsEnabled = canEdit };
                subItem.Click += (_, _) => new GestureAnimationImporterDialog(module, this).ShowDialog();
                gestureMenu.Items.Add(subItem);
            }
            contextMenu.Items.Add(gestureMenu);
        }
    }

    #endregion

    #region Lighting channels, collision and shadow flags

    /// <summary>An edit of the selected light, which upstream records as an undo step (the menu's actor is selected when it opens)</summary>
    private void EditMenuLight(ActorProxy actor, Action edit)
    {
        if (!CanMutateScene) return;
        if (actor != selectedActor) SelectActor(actor, false);
        edit();
    }

    private MenuItem BuildLightChannelsMenu(EditableLight light, bool canEdit)
    {
        var menu = new MenuItem { Header = "Lighting Channels", IsEnabled = canEdit };
        foreach (LightChannelOption channel in light.Channels)
        {
            var channelItem = new MenuItem
            {
                Header = channel.Name,
                IsCheckable = true,
                IsChecked = channel.IsChecked,
                IsEnabled = canEdit,
                StaysOpenOnClick = true
            };
            channelItem.Click += (_, _) => EditMenuLight(light.OwnerActor, () => channel.IsChecked = channelItem.IsChecked);
            menu.Items.Add(channelItem);
        }
        return menu;
    }

    private MenuItem BuildLightShadowMenu(EditableLight light, bool canEdit)
    {
        var menu = new MenuItem { Header = "Shadows", IsEnabled = canEdit };
        AddLightFlag("Cast Dynamic Shadows", () => light.CastDynamicShadows, value => light.CastDynamicShadows = value);
        AddLightFlag("Cast Shadows", () => light.CastShadows, value => light.CastShadows = value);
        AddLightFlag("Cast Static Shadows", () => light.CastStaticShadows, value => light.CastStaticShadows = value);
        return menu;

        void AddLightFlag(string header, Func<bool> get, Action<bool> set)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = get(), IsEnabled = canEdit, StaysOpenOnClick = true };
            item.Click += (_, _) => EditMenuLight(light.OwnerActor, () => set(item.IsChecked));
            menu.Items.Add(item);
        }
    }

    /// <summary>A mesh component's channels, written to its export (the preview refresh rereads it)</summary>
    private MenuItem BuildLightingChannelsMenu(ActorProxy actor, ExportEntry componentExport, bool canEdit)
    {
        var lightingChannelsMenu = new MenuItem { Header = "Lighting Channels", IsEnabled = canEdit };

        foreach ((string propertyName, string displayName) in LightingChannelMenuItems)
        {
            var channelItem = new MenuItem
            {
                Header = displayName,
                IsCheckable = true,
                IsChecked = GetLightingChannelValue(componentExport, propertyName),
                IsEnabled = canEdit,
                StaysOpenOnClick = true
            };
            channelItem.Click += (_, _) => WriteMenuFlag(() => SetLightingChannelValue(componentExport, propertyName, channelItem.IsChecked));
            lightingChannelsMenu.Items.Add(channelItem);
        }

        if (componentExport.IsA("StaticMeshComponent"))
        {
            var lightingMenu = new MenuItem { Header = "Lighting", IsEnabled = canEdit };
            foreach ((string propertyName, string displayName) in LightingMenuItems)
            {
                var lightingItem = new MenuItem
                {
                    Header = displayName,
                    IsCheckable = true,
                    IsChecked = GetBoolPropertyValue(componentExport, propertyName),
                    IsEnabled = canEdit,
                    StaysOpenOnClick = true
                };
                lightingItem.Click += (_, _) => WriteMenuFlag(() => SetBoolPropertyValue(componentExport, propertyName, lightingItem.IsChecked));
                lightingMenu.Items.Add(lightingItem);
            }

            lightingChannelsMenu.Items.Add(new Separator());
            lightingChannelsMenu.Items.Add(lightingMenu);
        }

        return lightingChannelsMenu;
    }

    private MenuItem GetCollisionMenu(ActorProxy actor, ExportEntry componentExport, bool canEdit)
    {
        var collisionMenu = new MenuItem { Header = "Collision", IsEnabled = canEdit };
        foreach ((string propertyName, string displayName) in CollisionMenuItems)
        {
            if (!ComponentSupportsBoolProperty(componentExport, propertyName)) continue;
            var collisionItem = new MenuItem
            {
                Header = displayName,
                IsCheckable = true,
                IsChecked = GetBoolPropertyValue(componentExport, propertyName),
                IsEnabled = canEdit,
                StaysOpenOnClick = true
            };
            collisionItem.Click += (_, _) => WriteMenuFlag(() => SetBoolPropertyValue(componentExport, propertyName, collisionItem.IsChecked));
            collisionMenu.Items.Add(collisionItem);
        }
        return collisionMenu.Items.Count > 0 ? collisionMenu : null;
    }

    private MenuItem BuildShadowMenu(ActorProxy actor, ExportEntry componentExport, bool canEdit)
    {
        var shadowMenu = new MenuItem { Header = "Shadow", IsEnabled = canEdit };
        foreach ((string propertyName, string displayName) in ShadowMenuItems)
        {
            var shadowItem = new MenuItem
            {
                Header = displayName,
                IsCheckable = true,
                IsChecked = GetBoolPropertyValue(componentExport, propertyName),
                IsEnabled = canEdit,
                StaysOpenOnClick = true
            };
            shadowItem.Click += (_, _) => WriteMenuFlag(() => SetBoolPropertyValue(componentExport, propertyName, shadowItem.IsChecked));
            shadowMenu.Items.Add(shadowItem);
        }
        return shadowMenu;
    }

    /// <summary>
    /// A flag the fork's menus write straight to the package (upstream's change model has no edit for it): upstream's preview refresh then redraws the
    /// component. Not while the scene is being changed or read
    /// </summary>
    private void WriteMenuFlag(Action write)
    {
        if (!CanMutateScene)
        {
            MessageBox.Show(this, "Wait for the current operation (such as a preview refresh) to finish, then try again.", "Level Editor");
            return;
        }
        write();
    }

    private static bool GetLightingChannelValue(ExportEntry export, string propertyName)
    {
        var lightingChannels = export.GetProperty<StructProperty>("LightingChannels");
        if (lightingChannels is null) return false;

        if (propertyName == "bInitialized")
        {
            return lightingChannels.Properties.GetProp<BoolProperty>("bInitialized")?.Value
                   ?? lightingChannels.Properties.GetProp<BoolProperty>("bIsInitialized")?.Value
                   ?? false;
        }

        return lightingChannels.Properties.GetProp<BoolProperty>(NameReference.FromInstancedString(propertyName))?.Value ?? false;
    }

    private static bool GetBoolPropertyValue(ExportEntry export, string propertyName) => export.GetProperty<BoolProperty>(propertyName)?.Value ?? false;

    private static bool ComponentSupportsBoolProperty(ExportEntry export, string propertyName) =>
        GlobalUnrealObjectInfo.GetPropertyInfo(export.Game, NameReference.FromInstancedString(propertyName), export.ClassName, containingExport: export)?.Type == PropertyType.BoolProperty;

    private static void SetBoolPropertyValue(ExportEntry export, string propertyName, bool value)
    {
        PropertyCollection props = export.GetProperties();
        props.AddOrReplaceProp(new BoolProperty(value, propertyName));
        export.WriteProperties(props);
    }

    private static void SetLightingChannelValue(ExportEntry export, string propertyName, bool value)
    {
        PropertyCollection props = export.GetProperties();
        StructProperty lightingChannels = GetOrCreateLightingChannelsProperty(props);
        if (propertyName == "bInitialized")
        {
            string initPropertyName = lightingChannels.Properties.GetProp<BoolProperty>("bInitialized") is not null
                ? "bInitialized"
                : lightingChannels.Properties.GetProp<BoolProperty>("bIsInitialized") is not null
                    ? "bIsInitialized"
                    : "bInitialized";
            lightingChannels.Properties.AddOrReplaceProp(new BoolProperty(value, initPropertyName));
        }
        else
        {
            lightingChannels.Properties.AddOrReplaceProp(new BoolProperty(value, NameReference.FromInstancedString(propertyName)));
        }
        props.AddOrReplaceProp(lightingChannels);
        export.WriteProperties(props);
    }

    private static StructProperty GetOrCreateLightingChannelsProperty(PropertyCollection props)
    {
        var lightingChannels = props.GetProp<StructProperty>("LightingChannels");
        if (lightingChannels is null)
        {
            return new StructProperty("LightingChannelContainer", false, new BoolProperty(true, "bInitialized"))
            {
                Name = "LightingChannels"
            };
        }

        if (lightingChannels.Properties.GetProp<BoolProperty>("bIsInitialized") is null
            && lightingChannels.Properties.GetProp<BoolProperty>("bInitialized") is null)
        {
            lightingChannels.Properties.AddOrReplaceProp(new BoolProperty(true, "bInitialized"));
        }

        return lightingChannels;
    }

    private static StructProperty CreateInitializedLightingChannelsProperty(params string[] enabledChannels)
    {
        PropertyCollection properties = [];
        foreach ((string propertyName, _) in LightingChannelMenuItems)
        {
            bool isEnabled = propertyName == "bInitialized" || enabledChannels.Contains(propertyName, StringComparer.Ordinal);
            properties.Add(new BoolProperty(isEnabled, propertyName));
        }

        return new StructProperty("LightingChannelContainer", properties, "LightingChannels");
    }

    private static ExportEntry GetLightingChannelsTargetExport(ActorProxy actor)
    {
        if (actor.Export.IsA("StaticMeshComponent") || actor.Export.IsA("SkeletalMeshComponent") || actor.Export.IsA("LightComponent"))
        {
            return actor.Export;
        }

        return actor.Components
            .Select(component => component.Export)
            .FirstOrDefault(export => export is not null && (export.IsA("StaticMeshComponent") || export.IsA("SkeletalMeshComponent") || export.IsA("LightComponent")));
    }

    #endregion

    #region Lightmass, lightmap and mesh actions

    private MenuItem BuildActorLightmassResolutionMenu(ActorProxy actor, string header, StaticLightingMappingMode mappingMode, string toolTip)
    {
        var mappingMenu = new MenuItem { Header = header, ToolTip = toolTip };
        foreach (int resolution in LightmassResolutions)
        {
            int selectedResolution = resolution;
            var resolutionItem = new MenuItem
            {
                Header = $"{resolution} × {resolution}",
                IsCheckable = true,
                IsChecked = _actorLightmassResolution == resolution
            };
            resolutionItem.Click += async (_, _) => await GenerateStaticLightingForActor(actor, selectedResolution, mappingMode);
            mappingMenu.Items.Add(resolutionItem);
        }
        return mappingMenu;
    }

    private static ExportEntry GetStaticMeshComponentExport(ActorProxy actor)
    {
        if (actor.Export.IsA("StaticMeshComponent")) return actor.Export;
        return actor.Components
            .OfType<StaticMeshComponentProxy>()
            .Select(component => component.Export)
            .FirstOrDefault(export => export is not null && export.IsA("StaticMeshComponent"));
    }

    private static List<ExportEntry> GetSkeletalMeshComponentExports(ActorProxy actor) =>
        actor.Components
            .Select(c => c.Export)
            .Where(e => e is not null && e.IsA("SkeletalMeshComponent"))
            .Distinct()
            .ToList();

    private void StripStaticMeshComponentLightmap(ActorProxy actor, ExportEntry componentExport)
    {
        if (componentExport is null) return;
        WriteMenuFlag(() =>
        {
            SelectActor(actor, false);
            PackageEditorExperimentsM.StripLightmap(componentExport);
        });
    }

    private void StripStaticMeshComponentShadowmap(ActorProxy actor, ExportEntry componentExport)
    {
        if (componentExport is null) return;
        WriteMenuFlag(() =>
        {
            SelectActor(actor, false);
            PackageEditorExperimentsM.StripShadowmap(componentExport);
        });
    }

    /// <summary>
    /// The fork's asset pickers (local package assets and the asset database, with a live preview) give a file and an export index. The choice is
    /// referred to as upstream's pickers' are: the destination's own entry, or another file's export kept in a package of its own until a commit
    /// copies it in (<see cref="KeepAsset"/>)
    /// </summary>
    private AssetReference ForkPickToAsset((string FilePath, int UIndex) pick, IMEPackage destination)
    {
        if (destination.FilePath is not null
            && string.Equals(Path.GetFullPath(pick.FilePath), Path.GetFullPath(destination.FilePath), StringComparison.OrdinalIgnoreCase))
        {
            return AssetReference.To(destination.GetEntry(pick.UIndex));
        }
        string instancedFullPath;
        using (IMEPackage source = MEPackageHandler.OpenMEPackage(pick.FilePath))
        {
            instancedFullPath = source.GetEntry(pick.UIndex)?.InstancedFullPath
                                ?? throw new InvalidOperationException($"{Path.GetFileName(pick.FilePath)} has no export {pick.UIndex}.");
        }
        return KeepAsset(AssetReference.InFile(pick.FilePath, instancedFullPath), destination);
    }

    /// <summary>
    /// Replace Static Mesh: another mesh for the component, chosen with the fork's static mesh picker, drawn until a commit writes it, as one undo step
    /// (upstream's mesh choice: <see cref="StaticMeshComponentProxy.ChooseMesh"/>)
    /// </summary>
    private void ReplaceStaticMesh(ActorProxy actor, ExportEntry componentExport)
    {
        if (actor.Components.OfType<StaticMeshComponentProxy>().FirstOrDefault(component => component.Export == componentExport) is not { } mesh
            || MeshChangeRefused(mesh))
        {
            return;
        }
        var picker = new StaticMeshPickerDialog(Game, componentExport.FileRef, this);
        if (picker.ShowDialog() != true || picker.SelectedResult is not { } pick) return;
        try
        {
            pickInUse = ForkPickToAsset(pick, mesh.Actor.Package);
            if (pickInUse is { } chosen && !MeshChangeRefused(mesh)) ChangeMesh(() => mesh.ChooseMesh(chosen));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MessageBox.Show(this, $"Failed to replace static mesh:\n{ex.Message}", "Replace Static Mesh");
        }
        finally { ReleaseUnusedPick(); }
    }

    /// <summary>
    /// Replace Skeletal Mesh (upstream has no skeletal mesh choice): the picked mesh is imported and written to the component, its materials matched;
    /// upstream's preview refresh then redraws the actor
    /// </summary>
    private async void ReplaceSkeletalMesh(ActorProxy actor, ExportEntry componentExport)
    {
        if (!CanMutateScene) return;
        var picker = new SkeletalMeshPickerDialog(Game, componentExport.FileRef, this);
        if (picker.ShowDialog() != true || picker.SelectedResult is null || !CanMutateScene) return;

        SetBusy("Replacing skeletal mesh...");
        await Task.Delay(1).ConfigureAwait(true);
        try
        {
            AssetImportResult importResult = AssetImportHelper.GetOrImportAsset(picker.SelectedResult.Value, componentExport.FileRef, "SkeletalMesh");
            var props = componentExport.GetProperties();
            props.AddOrReplaceProp(new ObjectProperty(importResult.Entry.UIndex, "SkeletalMesh"));
            componentExport.WriteProperties(props);

            if (importResult.RelinkWarnings.Count > 0)
            {
                string warnings = string.Join("\n", importResult.RelinkWarnings);
                MessageBox.Show(this, $"Import completed with {importResult.RelinkWarnings.Count} relink warning(s):\n{warnings}", "Import Warnings");
            }

            // Match MaterialInstanceConstants to the new SkeletalMesh
            if (InterpreterExportLoader.CanMatchMaterialsToSkeletalMesh(componentExport))
            {
                InterpreterExportLoader.MatchMaterialsToSkeletalMesh(this, componentExport);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to replace skeletal mesh:\n{ex.Message}", "Error");
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>
    /// Fork API (volume editing): shows <paramref name="oldActor"/> as its package now has it, keeping the selection and camera. Its earlier undo steps
    /// are orphaned, as when another tool changes an actor
    /// </summary>
    private void RefreshActorInViewport(ActorProxy oldActor)
    {
        if (oldActor?.Export is not { } export || oldActor.OwningFile is not { } file || !OpenFiles.Contains(file)) return;
        bool wasSelected = SelectedActor == oldActor;
        var camera = RenderContext.Camera;
        var savedCamera = (camera.Position, camera.Pitch, camera.Yaw, camera.Roll);

        List<ActorProxy> rebuilt;
        if (oldActor.CollectionOwner is { } collection)
        {
            rebuilt = RebuildCollection(collection, file);
        }
        else
        {
            RenderContext.Registry.Invalidate(export);
            RemoveActor(oldActor);
            rebuilt = [];
            if (file.Package.GetEntry(export.UIndex) is ExportEntry actorExport)
            {
                foreach (var view in CreateActorViews(actorExport, file))
                {
                    AddActor(view);
                    rebuilt.Add(view);
                }
            }
            if (rebuilt.Count != 0) file.ReattachTo(rebuilt);
        }
        file.RecalculateDirty();
        UpdateGlobalDirtyState();
        UndoHistory.RemoveOrphaned();

        if (wasSelected && rebuilt.FirstOrDefault(view => view.Export == export) is { } replacement)
        {
            SelectActor(replacement, false);
            (camera.Position, camera.Pitch, camera.Yaw, camera.Roll) = savedCamera;
        }
    }

    #endregion

    #region Sky Light and Point of Interest (the Place Actors tab's Other section)

    private ICommand createSkyLightCommand, createPointOfInterestCommand;

    /// <summary>Place Actors → Other → SkyLight: a SkyLight in front of the camera</summary>
    public ICommand CreateSkyLightCommand => createSkyLightCommand ??=
        new GenericCommand(() => CreateSkyLight(InFrontOfCamera), () => CanEditScene && CreateTargetFile is not null);

    /// <summary>Place Actors → Other → SFXPointOfInterest: one in front of the camera (Mass Effect 3)</summary>
    public ICommand CreatePointOfInterestCommand => createPointOfInterestCommand ??=
        new GenericCommand(() => CreatePointOfInterest(InFrontOfCamera), () => CanEditScene && CreateTargetFile is { } file && file.Package.Game.IsGame3());

    private Vector3 InFrontOfCamera => RenderContext.Camera.EyePosition + RenderContext.Camera.CameraForward * 500;

    /// <summary>The level Sky Light and Point of Interest go in: the Place Actors tab's, if it's writable, else the active file, if that is</summary>
    private OpenLevelFile CreateTargetFile =>
        PlacementFile is { IsReadOnly: false } placement && OpenFiles.Contains(placement) ? placement
        : ActiveFile is { IsReadOnly: false } active ? active : null;

    //a new actor the fork's Create menu writes to the package, which the preview refresh adds to the scene: selected once it's there
    private (IMEPackage Package, int UIndex)? _pendingForkSelect;

    /// <summary>
    /// Writes a new actor to the target level directly (for classes upstream has no placement recipe for: SFXPointOfInterest, SkyLight). The preview
    /// refresh adds it to the scene; it's selected then
    /// </summary>
    private void CreateActorInPackage(string description, Func<OpenLevelFile, ExportEntry> create)
    {
        if (!CanMutateScene || CreateTargetFile is not { } file) return;
        if (IsDirty && MessageBox.Show(this, $"The new {description} is written to {file.FileName} now, not as an uncommitted change, and creating it clears the undo history. Create it?",
                $"Create {description}", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        ExportEntry actorExport = null;
        try
        {
            actorExport = create(file);
            _pendingForkSelect = (file.Package, actorExport.UIndex);
            Level level = file.LevelExport.GetBinaryData<Level>();
            level.Actors.Add(actorExport.UIndex);
            file.LevelExport.WriteBinary(level);
            ClearUndoHistory();
        }
        catch (Exception ex)
        {
            _pendingForkSelect = null;
            if (actorExport is not null && !actorExport.IsTrash())
            {
                EntryPruner.TrashEntryAndDescendants(actorExport);
            }
            MessageBox.Show(this, $"Failed to create {description}:\n{ex.Message}", "Error");
        }
    }

    private void CreatePointOfInterest(Vector3 location)
    {
        if (CreateTargetFile is not { } target || !target.Package.Game.IsGame3()) return;
        CreateActorInPackage("SFXPointOfInterest", file =>
        {
            IMEPackage package = file.Package;
            ExportEntry actorExport = ExportCreator.CreateExport(package, "SFXPointOfInterest", "SFXPointOfInterest", file.LevelExport, createWithStack: true);
            ExportEntry selectionModule = ExportCreator.CreateExport(package, "tempSelectionModule", "SFXSimpleUseModule", actorExport, indexed: false);
            selectionModule.ObjectFlags |= UnrealFlags.EObjectFlags.Transactional;
            selectionModule.Archetype = PreviewLevelBuilder.GetImportArchetype(package, "SFXGame", "Default__SFXPointOfInterest.tempSelectionModule");
            selectionModule.WriteProperties([
                new StringRefProperty(0, "m_srGameName"),
                new BoolProperty(false, "m_bTargetable"),
                new EnumProperty("ETargetTipText", package.Game, "m_TargetTipText")
            ]);

            var modules = new ArrayProperty<ObjectProperty>("Modules") { new ObjectProperty(selectionModule) };
            actorExport.WriteProperties([
                modules,
                CommonStructs.Vector3Prop(location, "location"),
                new NameProperty("SFXPointOfInterest", "Tag")
            ]);
            return actorExport;
        });
    }

    private void CreateSkyLight(Vector3 location)
    {
        CreateActorInPackage("SkyLight", file =>
        {
            IMEPackage package = file.Package;
            ExportEntry actorExport = ExportCreator.CreateExport(package, "SkyLight", "SkyLight", file.LevelExport, createWithStack: true);
            ExportEntry componentExport = ExportCreator.CreateExport(package, "SkyLightComponent", "SkyLightComponent", actorExport, prePropBinary: new byte[8]);
            componentExport.ObjectFlags |= UnrealFlags.EObjectFlags.Transactional;
            componentExport.Archetype = PreviewLevelBuilder.GetImportArchetype(package, "Engine", "Default__SkyLight.SkyLightComponent0");

            actorExport.WriteProperties([
                new ObjectProperty(componentExport, "LightComponent"),
                CommonStructs.Vector3Prop(location, "Location")
            ]);
            componentExport.WritePropertiesAndBinary(CreateSkyLightProperties(), LightComponent.Create());
            return actorExport;
        });
    }

    private static PropertyCollection CreateSkyLightProperties() =>
    [
        new FloatProperty(1.1f, "Brightness"),
        CommonStructs.ColorProp(System.Drawing.Color.White, "LightColor"),
        new FloatProperty(1.1f, "LowerBrightness"),
        CommonStructs.ColorProp(System.Drawing.Color.White, "LowerColor"),
        new BoolProperty(true, "bHasLightEverBeenBuiltIntoLightMap"),
        CreateInitializedLightingChannelsProperty(),
        CommonStructs.ColorProp(System.Drawing.Color.White, "LightEnv_BouncedModulationColor"),
        new FloatProperty(0.129841f, "LightEnv_BouncedLightBrightness"),
        CommonStructs.GuidProp(Guid.NewGuid(), "LightGuid"),
        CommonStructs.GuidProp(Guid.NewGuid(), "LightmapGuid"),
        new ArrayProperty<NameProperty>("OtherLevelsToAffect")
    ];

    #endregion
}
