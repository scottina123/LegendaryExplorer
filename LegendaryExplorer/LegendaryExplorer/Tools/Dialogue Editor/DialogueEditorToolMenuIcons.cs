using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.DialogueEditor
{
    public partial class DialogueEditorWindow
    {
        private void NodeOpenIn_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menu || !ReferenceEquals(e.OriginalSource, menu))
            {
                return;
            }

            var conditionalItem = menu.Items.OfType<MenuItem>()
                .FirstOrDefault(item => item.CommandParameter as string == "PlotDbCnd");
            if (conditionalItem != null)
            {
                string iconKey = SelectedDialogueNode?.FiresConditional == true && Pcc?.Game.IsGame3() == true
                    ? "ConditionalsEditorMenuIcon"
                    : "PlotDatabaseMenuIcon";
                conditionalItem.Icon = FindResource(iconKey);
            }
        }
    }
}
