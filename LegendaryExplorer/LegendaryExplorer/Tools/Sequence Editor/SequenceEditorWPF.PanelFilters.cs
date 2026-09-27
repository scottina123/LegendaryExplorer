using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Controls;

namespace LegendaryExplorer.Tools.Sequence_Editor;

public partial class SequenceEditorWPF
{
    private string sequencesFilterText = string.Empty;
    private string currentObjectsFilterText = string.Empty;
    private readonly Dictionary<TreeViewEntry, bool> sequenceExpansionBeforeFilter = new();

    public string CurrentObjectsFilterText
    {
        get => currentObjectsFilterText;
        private set => SetProperty(ref currentObjectsFilterText, value);
    }

    private void CurrentObjectsSearchBox_TextChanged(SearchBox sender, string newText)
    {
        // Filter row visibility, keeping the complete list available to graph selection and navigation.
        CurrentObjectsFilterText = newText.Trim();
    }

    private void SequencesSearchBox_TextChanged(SearchBox sender, string newText)
    {
        sequencesFilterText = newText.Trim();
        ApplySequenceTreeFilter();
    }

    private void ApplySequenceTreeFilter()
    {
        foreach (var root in TreeViewRootNodes)
        {
            ApplySequenceTreeFilter(root);
        }

        if (sequencesFilterText.Length == 0)
        {
            sequenceExpansionBeforeFilter.Clear();
        }
    }

    private bool ApplySequenceTreeFilter(TreeViewEntry node)
    {
        bool filtering = sequencesFilterText.Length > 0;
        if (filtering)
        {
            sequenceExpansionBeforeFilter.TryAdd(node, node.IsExpanded);
        }

        bool hasVisibleChild = false;
        foreach (var child in node.Sublinks)
        {
            hasVisibleChild |= ApplySequenceTreeFilter(child);
        }

        node.IsVisibleInTree = hasVisibleChild
            || SequencePanelFilter.Matches(node.DisplayName, node.UIndex, sequencesFilterText)
            || SequencePanelFilter.Matches(node.Entry?.ObjectName.Instanced, node.UIndex, sequencesFilterText);

        if (filtering && hasVisibleChild)
        {
            node.IsExpanded = true;
        }
        else if (!filtering && sequenceExpansionBeforeFilter.TryGetValue(node, out bool wasExpanded))
        {
            node.IsExpanded = wasExpanded;
        }

        return node.IsVisibleInTree;
    }
}

internal static class SequencePanelFilter
{
    internal static bool Matches(string name, int uIndex, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        filter = filter.Trim();
        string indexText = filter.StartsWith('#') ? filter[1..] : filter;
        if (int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int exportId))
        {
            return uIndex == exportId;
        }

        return name?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;
    }
}

public sealed class SequencePanelFilterVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        return values.Length == 3 && values[1] is int uIndex
            && SequencePanelFilter.Matches(values[0] as string, uIndex, values[2] as string)
                ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
