using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// Fork: the live material editor (an ActorPreviewControl over the right column, editing the actor's materials in the level viewport, with the material
/// under a clicked pixel picked) and the morph editor overlay
/// </summary>
public partial class LevelEditor
{
    #region Live material editor

    private ActorProxy _levelLiveMaterialActor;
    private IMEPackage _levelLiveMaterialActorPackage;
    private int _levelLiveMaterialActorUIndex;
    private Point? _levelMaterialPickMouseDownPosition;

    private bool _isLevelMaterialEditorOpen;
    public bool IsLevelMaterialEditorOpen
    {
        get => _isLevelMaterialEditorOpen;
        private set => SetProperty(ref _isLevelMaterialEditorOpen, value);
    }

    private static bool CanOpenLiveMaterialEditor(ActorProxy actor) => actor is { Export: not null } and (SkeletalMeshActorProxy or SFXStuntActorProxy);

    private void OpenActorPreviewEditor(ActorProxy actor, bool openMorphEditor)
    {
        if (actor?.Export is null) return;
        SelectedActor = actor;
        if (openMorphEditor) OpenLevelMorphEditor(actor);
        else OpenLevelLiveMaterialEditor(actor);
    }

    private void OpenLevelLiveMaterialEditor(ActorProxy actor)
    {
        CloseLevelMorphEditor();
        _levelLiveMaterialActor = actor;
        _levelLiveMaterialActorPackage = actor.Export.FileRef;
        _levelLiveMaterialActorUIndex = actor.Export.UIndex;
        LevelLiveMaterialEditor.LoadExternalLiveMaterialEditor(actor, RenderContext);
        IsLevelMaterialEditorOpen = true;
    }

    private void CloseLevelLiveMaterialEditor()
    {
        bool wasOpen = IsLevelMaterialEditorOpen || _levelLiveMaterialActor is not null;
        IsLevelMaterialEditorOpen = false;
        _levelMaterialPickMouseDownPosition = null;
        _levelLiveMaterialActor = null;
        _levelLiveMaterialActorPackage = null;
        _levelLiveMaterialActorUIndex = 0;
        if (wasOpen) LevelLiveMaterialEditor?.UnloadExternalLiveMaterialEditor();
    }

    /// <summary>Keeps the editor on the selected actor: it closes when another actor is selected, and follows its actor's view when it's rebuilt</summary>
    private void SynchronizeLevelLiveMaterialEditor(ActorProxy actor)
    {
        if (!IsLevelMaterialEditorOpen) return;

        if (actor?.Export is null
            || actor.Export.FileRef != _levelLiveMaterialActorPackage
            || actor.Export.UIndex != _levelLiveMaterialActorUIndex)
        {
            CloseLevelLiveMaterialEditor();
            return;
        }

        if (!ReferenceEquals(actor, _levelLiveMaterialActor))
        {
            _levelLiveMaterialActor = actor;
            LevelLiveMaterialEditor.LoadExternalLiveMaterialEditor(actor, RenderContext);
        }
    }

    private void LevelLiveMaterialEditor_CloseRequested(object sender, EventArgs e) => CloseLevelLiveMaterialEditor();

    private void LevelLiveMaterialEditor_PreviewChanged(object sender, EventArgs e) => SceneViewer.MarkRenderDirty();

    /// <summary>Viewport PreviewMouseDown: volume drawing's clicks, then the start of a material pick</summary>
    private void SceneViewer_PreviewMouseDownForMaterialPicking(object sender, MouseButtonEventArgs e)
    {
        if (VolumeDrawingMouseDown(e)) return;
        if (IsLevelMaterialEditorOpen && e.ChangedButton == MouseButton.Left && _levelLiveMaterialActor is not null)
        {
            _levelMaterialPickMouseDownPosition = e.GetPosition(SceneViewer);
        }
    }

    /// <summary>Viewport PreviewMouseUp: volume drawing's clicks, then a click (not a drag) picks the material under the cursor</summary>
    private void SceneViewer_PreviewMouseUpForMaterialPicking(object sender, MouseButtonEventArgs e)
    {
        if (VolumeDrawingMouseUp(e)) return;
        if (e.ChangedButton != MouseButton.Left || _levelMaterialPickMouseDownPosition is not { } mouseDownPosition) return;

        _levelMaterialPickMouseDownPosition = null;
        Point mouseUpPosition = e.GetPosition(SceneViewer);
        Vector clickMovement = mouseUpPosition - mouseDownPosition;
        if (clickMovement.LengthSquared <= 16 && _levelLiveMaterialActor is not null)
        {
            LevelLiveMaterialEditor.TrySelectLiveMaterialVectorAtPixel(_levelLiveMaterialActor, mouseUpPosition, SceneViewer, RenderContext);
        }
    }

    #endregion

    #region Morph editor

    private IMEPackage _levelMorphEditorActorPackage;
    private int _levelMorphEditorActorUIndex;

    private bool _isLevelMorphEditorOpen;
    public bool IsLevelMorphEditorOpen
    {
        get => _isLevelMorphEditorOpen;
        private set => SetProperty(ref _isLevelMorphEditorOpen, value);
    }

    private string _levelMorphEditorTitle = "Morph Editor";
    public string LevelMorphEditorTitle
    {
        get => _levelMorphEditorTitle;
        private set => SetProperty(ref _levelMorphEditorTitle, value);
    }

    private void OpenLevelMorphEditor(ActorProxy actor)
    {
        if (IsLevelMorphEditorOpen
            && actor.Export.FileRef == _levelMorphEditorActorPackage
            && actor.Export.UIndex == _levelMorphEditorActorUIndex)
        {
            return;
        }

        CloseLevelLiveMaterialEditor();
        _levelMorphEditorActorPackage = actor.Export.FileRef;
        _levelMorphEditorActorUIndex = actor.Export.UIndex;
        LevelMorphEditorTitle = $"Morph Editor - {actor.Export.UIndex}: {actor.Export.ObjectName.Instanced}";
        IsLevelMorphEditorOpen = true;
        LevelMorphEditor.LoadExport(actor.Export);
    }

    private void CloseLevelMorphEditor()
    {
        if (LevelMorphEditor is not null && (IsLevelMorphEditorOpen || LevelMorphEditor.CurrentLoadedExport is not null))
        {
            LevelMorphEditor.UnloadExport();
        }
        IsLevelMorphEditorOpen = false;
        _levelMorphEditorActorPackage = null;
        _levelMorphEditorActorUIndex = 0;
        LevelMorphEditorTitle = "Morph Editor";
    }

    private void SynchronizeLevelMorphEditor(ActorProxy actor)
    {
        if (!IsLevelMorphEditorOpen) return;

        if (actor?.Export is null
            || actor.Export.FileRef != _levelMorphEditorActorPackage
            || actor.Export.UIndex != _levelMorphEditorActorUIndex)
        {
            CloseLevelMorphEditor();
        }
    }

    private void CloseLevelMorphEditor_Click(object sender, RoutedEventArgs e) => CloseLevelMorphEditor();

    /// <summary>Whether the actor, or an actor attached to it, has a MorphHead the morph editor can edit</summary>
    private bool ActorHasEditableMorph(ActorProxy rootActor)
    {
        var pending = new Stack<ActorProxy>();
        var visited = new HashSet<ActorProxy>();
        pending.Push(rootActor);

        while (pending.Count > 0)
        {
            ActorProxy actor = pending.Pop();
            if (actor?.Export is null || !visited.Add(actor)) continue;

            if (HasResolvableMorphHead(actor.Export)
                || actor.Components.OfType<SkeletalMeshComponentProxy>().Any(component => HasResolvableMorphHead(component.Export)))
            {
                return true;
            }

            foreach (ActorProxy attachedActor in actor.Attached)
            {
                pending.Push(attachedActor);
            }
        }

        return false;
    }

    private bool HasResolvableMorphHead(ExportEntry owner)
    {
        if (owner is null) return false;
        try
        {
            ObjectProperty morphHead = owner.GetCondensedProperties().GetProp<ObjectProperty>("MorphHead")
                                       ?? owner.GetProperty<ObjectProperty>("MorphHead");
            return morphHead?.ResolveToExport(owner.FileRef, RenderContext.PackageCache) is not null;
        }
        catch
        {
            return false;
        }
    }

    #endregion
}
