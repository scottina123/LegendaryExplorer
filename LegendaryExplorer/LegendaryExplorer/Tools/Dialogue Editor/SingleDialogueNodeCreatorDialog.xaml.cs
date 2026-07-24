using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorerCore.Packages;
using System.Windows;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.DialogueEditor
{
    public partial class SingleDialogueNodeCreatorDialog : Window
    {
        private readonly IMEPackage package;

        public int NodeTlk { get; private set; }
        public string OwnerFindActor { get; private set; }

        public SingleDialogueNodeCreatorDialog(Window owner, bool isReply, IMEPackage package)
        {
            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);
            Owner = owner;
            this.package = package;
            Title = $"Create {(isReply ? "Reply" : "Entry")} with Sequence";
        }

        private void TlkTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            TlkPreviewTextBlock.Text = int.TryParse(TlkTextBox.Text, out int stringRef) && stringRef > 0
                ? TLKManagerWPF.GlobalFindStrRefbyID(stringRef, package)
                : string.Empty;
        }

        private void FindTlkText_Click(object sender, RoutedEventArgs e)
        {
            if (TlkStringRefSelector.SelectStringRef(this, package) is int stringRef)
            {
                TlkTextBox.Text = stringRef.ToString();
                TlkTextBox.CaretIndex = TlkTextBox.Text.Length;
            }
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TlkTextBox.Text, out int nodeTlk) || nodeTlk <= 0)
            {
                MessageBox.Show(this, "Enter a positive TLK StringRef.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                TlkTextBox.Focus();
                TlkTextBox.SelectAll();
                return;
            }

            string ownerFindActor = OwnerFindActorTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(ownerFindActor))
            {
                MessageBox.Show(this, "Enter the Owner actor name.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                OwnerFindActorTextBox.Focus();
                return;
            }

            NodeTlk = nodeTlk;
            OwnerFindActor = ownerFindActor;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
