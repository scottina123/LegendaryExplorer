using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Sound.Wwise;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public partial class BinaryInterpreterWPF
{
    private readonly Dictionary<uint, ExportEntry> WwiseStreamIdMap = [];

    private Visibility _wwiseStreamFilterVisibility = Visibility.Collapsed;
    public Visibility WwiseStreamFilterVisibility
    {
        get => _wwiseStreamFilterVisibility;
        set => SetProperty(ref _wwiseStreamFilterVisibility, value);
    }

    private string _wwiseStreamFilterText;
    public string WwiseStreamFilterText
    {
        get => _wwiseStreamFilterText;
        set
        {
            if (SetProperty(ref _wwiseStreamFilterText, value))
            {
                ApplyWwiseStreamFilter();
            }
        }
    }

    private void BuildWwiseStreamIdMap()
    {
        WwiseStreamIdMap.Clear();
        foreach (ExportEntry stream in Pcc.Exports.Where(export => export.ClassName == "WwiseStream"))
        {
            if (stream.GetProperty<IntProperty>("Id") is { } idProperty)
            {
                WwiseStreamIdMap.TryAdd(unchecked((uint)idProperty.Value), stream);
            }
        }
    }

    private void AttachWwiseStreamReference(BinInterpNode node, ExportEntry stream, bool includeStreamPathInHeader = false)
    {
        if (node is null || stream?.ClassName != "WwiseStream")
        {
            return;
        }

        node.ReferencedWwiseStreamUIndex = stream.UIndex;
        node.WwiseStreamTlkId = WwiseHelper.GetTlkIdFromWwiseStreamName(stream.ObjectName.Name);
        if (node.WwiseStreamTlkId is int tlkId)
        {
            try
            {
                string subtitle = TLKManagerWPF.GlobalFindStrRefbyID(tlkId, stream.FileRef);
                if (!string.IsNullOrWhiteSpace(subtitle)
                    && !subtitle.Equals("No Data", StringComparison.OrdinalIgnoreCase))
                {
                    node.WwiseStreamSubtitle = subtitle;
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Binary Interpreter: Could not resolve TLK {tlkId}: {exception.Message}");
            }

            node.WwiseStreamTlkDisplayText = node.WwiseStreamSubtitle is null
                ? $"TLK {tlkId}: No subtitle found"
                : $"TLK {tlkId}: {node.WwiseStreamSubtitle}";
        }
        else
        {
            node.WwiseStreamTlkDisplayText = "No TLK ID found";
        }

        if (includeStreamPathInHeader)
        {
            node.Header += $" | WwiseStream #{stream.UIndex}: {stream.InstancedFullPath}";
        }
    }

    private void ApplyWwiseStreamFilter()
    {
        foreach (BinInterpNode root in TreeViewItems)
        {
            ApplyWwiseStreamFilter(root, WwiseStreamFilterText);
        }
    }

    private static void ApplyWwiseStreamFilter(BinInterpNode node, string filterText)
    {
        node.IsVisible = !node.IsWwiseStreamReference
                         || WwiseHelper.MatchesWwiseStreamTlkFilter(
                             node.WwiseStreamTlkId,
                             node.WwiseStreamSubtitle,
                             filterText);

        foreach (BinInterpNode child in node.Items.OfType<BinInterpNode>())
        {
            ApplyWwiseStreamFilter(child, filterText);
        }
    }

    private void NavigateToReferencedWwiseStream_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BinInterpNode node }
            || CurrentLoadedExport?.FileRef.GetEntry(node.ReferencedWwiseStreamUIndex) is not ExportEntry stream
            || stream.ClassName != "WwiseStream"
            || NavigateToEntryCommand?.CanExecute(stream) != true)
        {
            return;
        }

        NavigateToEntryCommand.Execute(stream);
        e.Handled = true;
    }
}
