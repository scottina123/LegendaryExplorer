using System.Collections.Generic;
using System.Linq;

namespace LegendaryExplorer.UserControls.ExportLoaderControls.Scaleform;

public partial class ScaleformVisualEditor
{
    public bool CanExitGroup => draft?.SpriteId.HasValue == true;

    public void EnterGroup(ushort? spriteId)
    {
        if (draft == null || draft.SpriteId == spriteId) return;
        if (spriteId.HasValue && !IsGroupScope(spriteId)) return;
        if (!CommitPendingFields()) return;

        if (!spriteId.HasValue)
        {
            draft.GroupNavigationHistory.Clear();
        }
        else
        {
            // Layers can be expanded several groups deep. Keep the actual containing
            // instances, so Up still visits each parent when opening a nested row.
            var selected = SelectedElement?.CharacterId == spriteId && SelectedElement.Item.Definition?.Code == 39
                ? SelectedElement : AllElements(VisibleElements).FirstOrDefault(element => element.CharacterId == spriteId && element.Item.Definition?.Code == 39);
            var path = new List<VisualElement>();
            if (selected != null && FindGroupPath(VisibleElements, selected, path))
            {
                foreach (var group in path)
                    draft.GroupNavigationHistory.Push(new ScaleformVisualDraft.GroupNavigationLocation(
                        group.OwnerSpriteId, group.Frame, group.Identity, group.OwnerSpriteId, group.Depth));
            }
            else
            {
                draft.GroupNavigationHistory.Push(new ScaleformVisualDraft.GroupNavigationLocation(
                    draft.SpriteId, draft.Frame, SelectedElement?.Identity, SelectedElement?.OwnerSpriteId, SelectedElement?.Depth));
            }
        }

        draft.SpriteId = spriteId;
        draft.Frame = ChooseAuthoringFrame(working, spriteId);
        draft.InitialFrameChosen = true;
        SelectedElement = null;
        RefreshView();
        CanvasViewport.Fit();
    }

    public void ExitGroup()
    {
        if (!CanExitGroup || !CommitPendingFields()) return;
        ScaleformVisualDraft.GroupNavigationLocation parent = null;
        while (draft.GroupNavigationHistory.Count > 0)
        {
            var candidate = draft.GroupNavigationHistory.Pop();
            if (candidate.SpriteId == draft.SpriteId || !IsGroupScope(candidate.SpriteId)) continue;
            parent = candidate;
            break;
        }

        draft.SpriteId = parent?.SpriteId;
        draft.Frame = parent?.Frame ?? ChooseAuthoringFrame(working, null);
        draft.InitialFrameChosen = true;
        SelectedElement = null;
        RefreshView(parent?.SelectionIdentity, parent?.SelectionOwnerSpriteId, parent?.SelectionDepth);
        CanvasViewport.Fit();
    }

    internal void UpdateGroupNavigation()
    {
        if (ParentGroupButton != null) ParentGroupButton.IsEnabled = CanExitGroup;
    }

    private void ResetInvalidGroupNavigation()
    {
        draft.GroupNavigationHistory.Clear();
        draft.SpriteId = null;
        draft.Frame = 1;
        draft.InitialFrameChosen = false;
    }

    private bool IsGroupScope(ushort? spriteId) => !spriteId.HasValue ||
        (renderer.GetScene().Definitions.TryGetValue(spriteId.Value, out var definition) && definition.Code == 39);

    private static bool FindGroupPath(IReadOnlyList<VisualElement> elements, VisualElement target, List<VisualElement> path)
    {
        foreach (var element in elements)
        {
            path.Add(element);
            if (ReferenceEquals(element, target) || FindGroupPath(element.Children, target, path)) return true;
            path.RemoveAt(path.Count - 1);
        }
        return false;
    }

}
