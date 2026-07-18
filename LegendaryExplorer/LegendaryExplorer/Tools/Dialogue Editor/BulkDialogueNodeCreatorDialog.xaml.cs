using LegendaryExplorer.SharedUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.DialogueEditor
{
    public sealed class BulkDialogueNodeRow : INotifyPropertyChanged
    {
        private int? entryTlk;
        private int? replyTlk;

        public int? EntryTlk
        {
            get => entryTlk;
            set => SetProperty(ref entryTlk, value);
        }

        public int? ReplyTlk
        {
            get => replyTlk;
            set => SetProperty(ref replyTlk, value);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class BulkInterpGroupDefinition
    {
        public string GroupName { get; set; }
        public string SFXFindActor { get; set; }
    }

    public partial class BulkDialogueNodeCreatorDialog : Window
    {
        private const int MaximumRangeSize = 1000;

        public ObservableCollection<BulkDialogueNodeRow> Rows { get; } = new();
        public ObservableCollection<BulkInterpGroupDefinition> CustomGroups { get; } = new();
        public IReadOnlyList<BulkDialogueNodeRow> NodesToCreate { get; private set; }
        public IReadOnlyList<BulkInterpGroupDefinition> CustomGroupsToCreate { get; private set; }

        public BulkDialogueNodeCreatorDialog(Window owner)
        {
            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);
            Owner = owner;
        }

        private void GenerateRange_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseRange(FirstEntryTlkTextBox.Text, LastEntryTlkTextBox.Text, "Entry", out List<int> entryTlks)
                || !TryParseRange(FirstReplyTlkTextBox.Text, LastReplyTlkTextBox.Text, "Reply", out List<int> replyTlks))
            {
                return;
            }

            if (entryTlks.Count == 0 && replyTlks.Count == 0)
            {
                MessageBox.Show(this, "Enter an Entry range, a Reply range, or both.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Rows.Clear();
            int rowCount = Math.Max(entryTlks.Count, replyTlks.Count);
            for (int i = 0; i < rowCount; i++)
            {
                Rows.Add(new BulkDialogueNodeRow
                {
                    EntryTlk = i < entryTlks.Count ? entryTlks[i] : null,
                    ReplyTlk = i < replyTlks.Count ? replyTlks[i] : null
                });
            }
        }

        private bool TryParseRange(string firstText, string lastText, string rangeName, out List<int> tlks)
        {
            tlks = new List<int>();
            if (string.IsNullOrWhiteSpace(firstText) && string.IsNullOrWhiteSpace(lastText))
            {
                return true;
            }

            if (!int.TryParse(firstText, out int firstTlk) || firstTlk <= 0
                || !int.TryParse(lastText, out int lastTlk) || lastTlk <= 0)
            {
                MessageBox.Show(this, $"{rangeName} First and Last TLKs must both be positive integers.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (lastTlk < firstTlk)
            {
                MessageBox.Show(this, $"{rangeName} Last TLK must be greater than or equal to its First TLK.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            long rangeSize = (long)lastTlk - firstTlk + 1;
            if (rangeSize > MaximumRangeSize)
            {
                MessageBox.Show(this, $"The {rangeName} range cannot exceed {MaximumRangeSize} TLKs.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            tlks = Enumerable.Range(firstTlk, (int)rangeSize).ToList();
            return true;
        }

        private void ClearEntryColumn_Click(object sender, RoutedEventArgs e)
        {
            foreach (BulkDialogueNodeRow row in Rows)
            {
                row.EntryTlk = null;
            }
        }

        private void AddCustomGroup_Click(object sender, RoutedEventArgs e)
        {
            var group = new BulkInterpGroupDefinition();
            CustomGroups.Add(group);
            CustomGroupsGrid.SelectedItem = group;
            CustomGroupsGrid.ScrollIntoView(group);
            CustomGroupsGrid.BeginEdit();
        }

        private void ClearReplyColumn_Click(object sender, RoutedEventArgs e)
        {
            foreach (BulkDialogueNodeRow row in Rows)
            {
                row.ReplyTlk = null;
            }
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            TlkGrid.CommitEdit();
            TlkGrid.CommitEdit();
            CustomGroupsGrid.CommitEdit();
            CustomGroupsGrid.CommitEdit();

            List<BulkDialogueNodeRow> populatedRows = Rows
                .Where(row => row.EntryTlk.HasValue || row.ReplyTlk.HasValue)
                .Select(row => new BulkDialogueNodeRow { EntryTlk = row.EntryTlk, ReplyTlk = row.ReplyTlk })
                .ToList();

            if (populatedRows.Count == 0)
            {
                MessageBox.Show(this, "Enter at least one Entry or Reply TLK.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (populatedRows.Any(row => row.EntryTlk <= 0 || row.ReplyTlk <= 0))
            {
                MessageBox.Show(this, "All populated TLK values must be positive integers.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            NodesToCreate = populatedRows;
            CustomGroupsToCreate = CustomGroups
                .Select(group => new BulkInterpGroupDefinition
                {
                    GroupName = string.IsNullOrWhiteSpace(group.GroupName) ? null : group.GroupName.Trim(),
                    SFXFindActor = string.IsNullOrWhiteSpace(group.SFXFindActor) ? null : group.SFXFindActor.Trim()
                })
                .ToList();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
