using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace LegendaryExplorer.Tools.AssetDatabase
{
    public partial class AssetDatabaseWindow
    {
        internal void UsageToolsContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu menu || !ReferenceEquals(e.OriginalSource, menu))
            {
                return;
            }

            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                if (item.Command == OpenUsagePkgCommand && item.Command.CanExecute(item.CommandParameter))
                {
                    item.Icon = FindResource(GetUsageToolMenuIconKey(item.CommandParameter as string));
                }
                else if (item.Command == OpenPEDefinitionCommand)
                {
                    string iconKey = GetSelectedPlotRecord()?.BaseUsage?.Context switch
                    {
                        PlotUsageContext.CndFile => "ConditionalsEditorMenuIcon",
                        PlotUsageContext.Transition => "PlotEditorMenuIcon",
                        _ => "PackageEditorMenuIcon"
                    };
                    item.Icon = FindResource(iconKey);
                }
            }
        }

        private string GetUsageToolMenuIconKey(string tool)
        {
            if (currentView == 9 && lstbx_PlotUsages.SelectedItem is PlotUsage plotUsage)
            {
                tool = plotUsage.Context.ToTool();
            }
            else if (currentView == 11 && tlkUsagesPanel?.SelectedItem is TlkUsage tlkUsage)
            {
                if (string.Equals(tool, "CoalescedEd", StringComparison.OrdinalIgnoreCase))
                {
                    return "CoalescedEditorMenuIcon";
                }

                if (tlkUsage.ReferenceName?.StartsWith("Conversation:", StringComparison.OrdinalIgnoreCase) == true)
                {
                    tool = "DlgEd";
                }
                else
                {
                    tool = tlkUsage.Context switch
                    {
                        TlkUsageContext.Codex or TlkUsageContext.Quest => "PlotEd",
                        TlkUsageContext.Coalesced => "CoalescedEd",
                        _ => "PackageEditor"
                    };
                }
            }

            // OpenInToolkit routes coalesced files to their editor regardless of the requested package tool.
            if (tool != "PlotEd" && GetSelectedUsageInfo().Item1?.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "CoalescedEditorMenuIcon";
            }

            return tool switch
            {
                "SeqEd" => "SequenceEditorMenuIcon",
                "DlgEd" => "DialogueEditorMenuIcon",
                "Meshplorer" => "MeshplorerMenuIcon",
                "PathEd" => "PathfindingEditorMenuIcon",
                "SoundExplorer" => "SoundplorerMenuIcon",
                "CndEd" => "ConditionalsEditorMenuIcon",
                "PlotEd" => "PlotEditorMenuIcon",
                "CoalescedEd" => "CoalescedEditorMenuIcon",
                _ => "PackageEditorMenuIcon"
            };
        }
    }
}
