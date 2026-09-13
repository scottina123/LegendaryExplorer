using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorer.Tools.Sequence_Editor;

public partial class SequenceEditorWPF
{
    private ExportEntry GetEditableContextSequence(object sender)
    {
        return sender is MenuItem
        {
            Parent: ContextMenu
            {
                PlacementTarget: TreeViewItem { DataContext: TreeViewEntry { Entry: ExportEntry sequence } }
            }
        } && CanEditSequence(sequence) ? sequence : null;
    }

    private void SequencesTree_Clone_Click(object sender, RoutedEventArgs e)
    {
        if (GetEditableContextSequence(sender) is not { } sequence)
        {
            return;
        }

        var clone = CloneSequenceTreeExport(sequence);
        SequenceExports.ClearEx();
        LoadSequences();
        GoToExport(clone);
    }

    private void SequencesTree_Trash_Click(object sender, RoutedEventArgs e)
    {
        if (GetEditableContextSequence(sender) is not { } sequence)
        {
            return;
        }

        var parent = KismetHelper.GetParentSequence(GetSequenceTreeActionExport(sequence));
        saveView(false);
        TrashSequenceTreeExport(sequence);

        // The selected sequence and its displayed nodes may now be trash (or removed exports).
        SelectedSequence = null;
        _selectedItem = null;
        ClearSelectionHistory();
        CurrentObjects.ClearEx();
        SelectedObjects.ClearEx();
        graphEditor.nodeLayer.RemoveAllChildren();
        graphEditor.edgeLayer.RemoveAllChildren();
        Properties_InterpreterWPF.UnloadExport();
        ClearInterpDataTree();
        InterpData_InterpreterWPF.UnloadExport();
        InterpData_MetadataEditor.UnloadExport();
        customSaveData.Clear();

        SequenceExports.ClearEx();
        LoadSequences();
        var next = TreeViewRootNodes.SelectMany(node => node.FlattenTree())
            .FirstOrDefault(node => node.Entry == parent)
            ?? TreeViewRootNodes.SelectMany(node => node.FlattenTree())
                .FirstOrDefault(node => node.Entry is ExportEntry export && export.IsA("Sequence"));
        if (next != null)
        {
            SelectedItem = next;
        }
        graphEditor.Refresh();
    }

    private static ExportEntry GetSequenceTreeActionExport(ExportEntry sequence)
    {
        // Referenced sequences appear directly in the tree, but the viewport operates on their wrapper.
        return sequence.Parent is ExportEntry { ClassName: "SequenceReference" } reference
               && reference.GetProperty<ObjectProperty>("oSequenceReference")?.Value == sequence.UIndex
            ? reference : sequence;
    }

    internal static ExportEntry CloneSequenceTreeExport(ExportEntry sequence)
    {
        var source = GetSequenceTreeActionExport(sequence);
        var parent = KismetHelper.GetParentSequence(source);
        if (parent != null && source.ClassName is "Sequence" or "SequenceReference")
        {
            return KismetHelper.CloneObject(source, parent);
        }

        // Top-level sequences have no ParentSequence; clone their export tree in place.
        var clone = EntryCloner.CloneTree(source);
        KismetHelper.RemoveAllLinks(clone);
        if (parent?.IsA("Sequence") == true)
        {
            KismetHelper.AddObjectToSequence(clone, parent);
        }
        else if (source.Parent is ExportEntry { ClassName: "Level" } levelExport)
        {
            var level = ObjectBinary.From<Level>(levelExport);
            if (level.GameSequences.Contains(source.UIndex))
            {
                level.GameSequences = level.GameSequences.Append(clone.UIndex).ToArray();
                levelExport.WriteBinary(level);
            }
        }
        return clone;
    }

    internal static void TrashSequenceTreeExport(ExportEntry sequence)
    {
        var source = GetSequenceTreeActionExport(sequence);
        ClearAllIncomingConnections(source.FileRef, source.UIndex, isAction: true, isVariable: false, isEvent: false);
        KismetHelper.RemoveAllLinks(source);
        var parent = KismetHelper.GetParentSequence(source);
        KismetHelper.SynchronizeSequenceObjectMembership(source, parent, null);
        if (source.Parent is ExportEntry { ClassName: "Level" } levelExport)
        {
            var level = ObjectBinary.From<Level>(levelExport);
            level.GameSequences = level.GameSequences.Where(index => index != source.UIndex).ToArray();
            levelExport.WriteBinary(level);
        }
        EntryPruner.TrashEntryAndDescendants(source);
    }
}
