using LegendaryExplorer.SharedUI;
using System.Windows;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.DialogueEditor
{
    public partial class SingleDialogueNodeCreatorDialog : Window
    {
        public int NodeTlk { get; private set; }
        public string OwnerFindActor { get; private set; }

        public SingleDialogueNodeCreatorDialog(Window owner, bool isReply)
        {
            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);
            Owner = owner;
            Title = $"Create {(isReply ? "Reply" : "Entry")} with Sequence";
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
