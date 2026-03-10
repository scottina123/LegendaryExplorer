using System.Windows;
using LegendaryExplorer.SharedUI;

namespace LegendaryExplorer.Dialogs
{
    /// <summary>
    /// Dialog that lets the user choose which file types to target when cloning an export tree to a folder.
    /// </summary>
    public partial class CloneTreeFileFilterDialog : Window
    {
        /// <summary>
        /// The filter mode selected by the user.
        /// </summary>
        public enum FileFilterMode
        {
            /// <summary>Only LOC (localization) files.</summary>
            LocOnly,
            /// <summary>Only base (non-LOC) files.</summary>
            BaseOnly,
            /// <summary>All package files.</summary>
            AllFiles,
            /// <summary>User cancelled.</summary>
            Cancel
        }

        /// <summary>
        /// The result chosen by the user.
        /// </summary>
        public FileFilterMode SelectedMode { get; private set; } = FileFilterMode.Cancel;

        public CloneTreeFileFilterDialog(Window owner)
        {
            Owner = owner;
            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);
        }

        private void LocOnly_Click(object sender, RoutedEventArgs e)
        {
            SelectedMode = FileFilterMode.LocOnly;
            DialogResult = true;
            Close();
        }

        private void BaseOnly_Click(object sender, RoutedEventArgs e)
        {
            SelectedMode = FileFilterMode.BaseOnly;
            DialogResult = true;
            Close();
        }

        private void AllFiles_Click(object sender, RoutedEventArgs e)
        {
            SelectedMode = FileFilterMode.AllFiles;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            SelectedMode = FileFilterMode.Cancel;
            DialogResult = false;
            Close();
        }
    }
}
