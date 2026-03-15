using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Be.Windows.Forms;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.SharedUI.Interfaces;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.PlotDatabase;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorer.Tools.ConditionalsEditor.GraphView;
using Microsoft.Win32;
using Xceed.Wpf.Toolkit;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace LegendaryExplorer.Tools.ConditionalsEditor
{
    /// <summary>
    /// Interaction logic for ConditionalsEditorWindow.xaml
    /// </summary>
    public partial class ConditionalsEditorWindow : TrackingNotifyPropertyChangedWindowBase, IRecents
    {
        #region DependencyProperties

        public int HexBoxMinWidth
        {
            get => (int)GetValue(HexBoxMinWidthProperty);
            set => SetValue(HexBoxMinWidthProperty, value);
        }
        public static readonly DependencyProperty HexBoxMinWidthProperty = DependencyProperty.Register(
            nameof(HexBoxMinWidth), typeof(int), typeof(ConditionalsEditorWindow), new PropertyMetadata(default(int)));

        public int HexBoxMaxWidth
        {
            get => (int)GetValue(HexBoxMaxWidthProperty);
            set => SetValue(HexBoxMaxWidthProperty, value);
        }
        public static readonly DependencyProperty HexBoxMaxWidthProperty = DependencyProperty.Register(
            nameof(HexBoxMaxWidth), typeof(int), typeof(ConditionalsEditorWindow), new PropertyMetadata(default(int)));

        public bool HideHexBox
        {
            get => (bool)GetValue(HideHexBoxProperty);
            set => SetValue(HideHexBoxProperty, value);
        }
        public static readonly DependencyProperty HideHexBoxProperty = DependencyProperty.Register(
            nameof(HideHexBox), typeof(bool), typeof(ConditionalsEditorWindow), new PropertyMetadata(false, (obj, e) =>
            {
                var window = (ConditionalsEditorWindow)obj;
                if ((bool)e.NewValue)
                {
                    window.hexboxContainer.Visibility = window.HexProps_GridSplitter.Visibility = Visibility.Collapsed;
                    window.HexboxColumn_GridSplitter_ColumnDefinition.Width = new GridLength(0);
                    window.HexboxColumnDefinition.MinWidth = 0;
                    window.HexboxColumnDefinition.MaxWidth = 0;
                    window.HexboxColumnDefinition.Width = new GridLength(0);
                }
                else
                {
                    window.hexboxContainer.Visibility = window.HexProps_GridSplitter.Visibility = Visibility.Visible;
                    window.HexboxColumnDefinition.Width = new GridLength(window.HexBoxMinWidth);
                    window.HexboxColumn_GridSplitter_ColumnDefinition.Width = new GridLength(1);
                    window.HexboxColumnDefinition.bind(ColumnDefinition.MinWidthProperty, window, nameof(HexBoxMinWidth));
                    window.HexboxColumnDefinition.bind(ColumnDefinition.MaxWidthProperty, window, nameof(HexBoxMaxWidth));
                }
            }));

        #endregion

        public const string CNDFileFilter = "ME3/LE3 conditional file|*.cnd";
        private const string ConditionalsDragFormat = "LegendaryExplorer.ConditionalsEditor.Conditional";

        private HexBox hexBox;
        private readonly Guid _windowInstanceId = Guid.NewGuid();
        private Point? _conditionalsDragStartPoint;
        private CondListEntry _draggedConditional;

        public ObservableCollectionExtended<CondListEntry> Conditionals { get; } = new();

        private CondListEntry _selectedCond;
        public CondListEntry SelectedCond
        {
            get => _selectedCond;
            set
            {
                if (SetProperty(ref _selectedCond, value))
                {
                    if (_selectedCond is null)
                    {
                        ConditionalTextBox.Text = "";
                        hexBox.ByteProvider = new ReadOptimizedByteProvider();
                        GraphViewControl.DataContext = null;
                    }
                    else
                    {
                        if (_isGraphViewActive)
                        {
                            SwitchToGraphView();
                        }
                        DisplayCondition();
                    }
                    compilationMsgBox.Clear();
                }
            }
        }

        private CNDFile _file;
        public CNDFile File
        {
            get => _file;
            set => SetProperty(ref _file, value);
        }

        private string _currentFileName;
        public string CurrentFileName
        {
            get => _currentFileName;
            set => SetProperty(ref _currentFileName, value);
        }

        private string _currentFilePath;
        public string CurrentFilePath
        {
            get => _currentFilePath;
            set
            {
                if (SetProperty(ref _currentFilePath, value))
                {
                    OnPropertyChanged(nameof(CurrentLastSavedText));
                }
            }
        }

        public string CurrentLastSavedText
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(CurrentFilePath) && System.IO.File.Exists(CurrentFilePath))
                {
                    return $"Last saved at {System.IO.File.GetLastWriteTime(CurrentFilePath):G}";
                }

                return string.Empty;
            }
        }

        public ConditionalsEditorWindow() : base("Conditionals Editor", true)
        {
            LoadCommands();
            InitializeComponent();
            HideHexBox = true;
            RecentsController.InitRecentControl(Toolname, Recents_MenuItem, LoadFile);
        }

        private void ConditionalsEditorWindow_OnLoaded(object sender, RoutedEventArgs e)
        {
            hexBox = (HexBox)hexbox_Host.Child;
            hexBox.ByteProvider = new ReadOptimizedByteProvider();
            this.bind(HexBoxMinWidthProperty, hexBox, nameof(hexBox.MinWidth));
            this.bind(HexBoxMaxWidthProperty, hexBox, nameof(hexBox.MaxWidth));

            // Register HexBox for theme management
            Misc.ThemeManager.RegisterHexBox(hexBox);

            hexBox.InsertActiveChanged += HexBox_InsertActiveChanged;

            GraphViewToggle.IsChecked = Settings.ConditionalsEditor_DefaultGraphView;
        }

        private void HexBox_InsertActiveChanged(object sender, EventArgs e)
        {
            ToggleInsertMode_Button.IsChecked = hexBox.InsertActive;
        }

        private void ToggleInsertMode_Click(object sender, RoutedEventArgs e)
        {
            hexBox.InsertActive = ToggleInsertMode_Button.IsChecked == true;
        }

        public ICommand OpenCommand { get; set; }
        public ICommand NewFileCommand { get; set; }
        public ICommand SaveCommand { get; set; }
        public ICommand SaveAsCommand { get; set; }
        public ICommand CompileCommand { get; set; }
        public ICommand CloneCommand { get; set; }
        public ICommand ChangeIDCommand { get; set; }
        public ICommand DeleteCommand { get; set; }
        public ICommand ToggleHexBoxCommand { get; set; }
        public ICommand SaveHexChangesCommand { get; set; }
        public ICommand SearchCommand { get; set; }
        public ICommand SearchAgainCommand { get; set; }
        public ICommand AddBlankCommand { get; set; }
        public ICommand OpenCurrentFileLocationCommand { get; set; }
        public ICommand CopyCurrentFileNameCommand { get; set; }

        private void LoadCommands()
        {
            OpenCommand = new GenericCommand(OpenFile);
            NewFileCommand = new GenericCommand(NewFile);
            SaveCommand = new GenericCommand(Save, FileIsLoaded);
            SaveAsCommand = new GenericCommand(SaveAs, FileIsLoaded);
            CompileCommand = new GenericCommand(Compile, CanCompile);
            CloneCommand = new GenericCommand(CloneEntry, EntryIsSelected);
            ChangeIDCommand = new GenericCommand(ChangeID, EntryIsSelected);
            DeleteCommand = new GenericCommand(DeleteEntry, EntryIsSelected);
            ToggleHexBoxCommand = new GenericCommand(ToggleHexBox, FileIsLoaded);
            SaveHexChangesCommand = new GenericCommand(SaveHexChanges, EntryIsSelected);
            SearchCommand = new GenericCommand(SearchPrompt, FileIsLoaded);
            SearchAgainCommand = new GenericCommand(Search, CanSearchAgain);
            AddBlankCommand = new GenericCommand(AddBlankConditional, FileIsLoaded);
            OpenCurrentFileLocationCommand = new GenericCommand(OpenCurrentFileLocation, CanOpenCurrentFileLocation);
            CopyCurrentFileNameCommand = new GenericCommand(CopyCurrentFileName, CanCopyCurrentFileName);
        }

        private bool CanSearchAgain() => FileIsLoaded() && !string.IsNullOrEmpty(searchText);

        private void Search()
        {
            foreach (CondListEntry entry in Conditionals.AfterThenBefore(SelectedCond))
            {
                try
                {
                    string text = entry.Conditional.Decompile();
                    string entryPlotPath = entry.PlotPath ?? "";
                    if (text.Contains(searchText, StringComparison.OrdinalIgnoreCase) || entryPlotPath.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                    {
                        SelectedCond = entry;
                        ConditionalsListBox.ScrollIntoView(entry);
                        return;
                    }
                }
                catch
                {
                    //
                }
            }

            MessageBox.Show($"'{searchText}' was not found!");
        }

        private string searchText = "";
        private void SearchPrompt()
        {
            var s = PromptDialog.Prompt(this, "Input string to search for", "Search Input", searchText, true);
            if (s is not null)
            {
                searchText = s;
                if (searchText is not "")
                {
                    Search();
                }
            }
        }

        private void SaveHexChanges()
        {
            if (SelectedCond is not null)
            {
                var originalData = _selectedCond.Conditional.Data;
                var newData = ((ReadOptimizedByteProvider)hexBox.ByteProvider).Span;
                if (!newData.SequenceEqual(originalData))
                {
                    _selectedCond.Conditional.Data = newData.ToArray();
                    _selectedCond.IsModified = true;
                    DisplayCondition();
                }
            }
        }

        private void ToggleHexBox()
        {
            HideHexBox = !HideHexBox;
        }

        private void ChangeID()
        {
            if (PromptDialog.Prompt(this, "Enter new ID", defaultValue: SelectedCond.ID.ToString(), selectText: true) is string txt)
            {
                if (int.TryParse(txt, out int newID) && newID > 0)
                {
                    SelectedCond.ID = newID;
                }
                else
                {
                    MessageBox.Show($"'{txt}' is not a positive integer!");
                }
            }
        }

        private void DeleteEntry()
        {
            Conditionals.Remove(SelectedCond);
        }

        private void CloneEntry()
        {
            if (PromptDialog.Prompt(this, "Enter ID for new entry", defaultValue: SelectedCond.ID.ToString(), selectText: true) is string txt)
            {
                if (int.TryParse(txt, out int newID) && newID > 0)
                {
                    var newCond = new CondListEntry(new CNDFile.ConditionalEntry
                    {
                        Data = SelectedCond.Conditional.Data.ArrayClone(),
                        ID = newID
                    })
                    {
                        IsModified = true
                    };
                    Conditionals.Add(newCond);
                    SelectedCond = newCond;
                    ConditionalsListBox.ScrollIntoView(SelectedCond);
                }
                else
                {
                    MessageBox.Show($"'{txt}' is not a positive integer!");
                }
            }
        }

        private bool EntryIsSelected() => SelectedCond is not null;

        private void Save()
        {
            if (Validate())
            {
                if (File.FilePath is null)
                {
                    // Unsaved new file
                    var d = new SaveFileDialog { Filter = CNDFileFilter };
                    if (d.ShowDialog() == false) return;
                    File.FilePath = d.FileName;
                    CurrentFileName = Path.GetFileName(d.FileName);
                    CurrentFilePath = d.FileName;
                    RecentsController.AddRecent(d.FileName, false, null); // Can we infer game this file is for?
                    RecentsController.SaveRecentList(true);
                    Title = $"Conditionals Editor - {d.FileName}";
                }

                SaveFile();
            }
        }

        private void SaveAs()
        {
            if (Validate())
            {
                var d = new SaveFileDialog { Filter = CNDFileFilter };
                if (d.ShowDialog() == true)
                {
                    SaveFile(d.FileName);
                    MessageBox.Show(this, "Done.");
                }
            }
        }

        private void SaveFile(string filePath = null)
        {
            File.ConditionalEntries.Clear();
            File.ConditionalEntries.AddRange(Conditionals.Select(c => c.Conditional).OrderBy(c => c.ID));
            File.ToFile(filePath);
            OnPropertyChanged(nameof(CurrentLastSavedText));

            //don't reset modified state on save as
            if (filePath is null)
            {
                foreach (CondListEntry listEntry in Conditionals)
                {
                    listEntry.IsModified = false;
                }
            }
        }

        private bool Validate()
        {
            int id = 0;
            try
            {
                foreach (CondListEntry entry in Conditionals)
                {
                    id = entry.ID;
                    entry.Conditional.Decompile();
                }
            }
            catch
            {
                MessageBox.Show($"Cannot save this file: Conditional {id} is malformed!", "Broken Conditional!", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            return true;
        }

        private bool FileIsLoaded() => File is not null;

        private void OpenFile()
        {
            var d = new OpenFileDialog
            {
                Filter = CNDFileFilter,
                Title = "Open Conditionals file",
                CustomPlaces = AppDirectories.GameCustomPlaces
            };
            if (d.ShowDialog() == true)
            {
                try
                {
                    LoadFile(d.FileName);
                }
                catch (Exception ex) when (!App.IsDebug)
                {
                    MessageBox.Show(this, "Unable to open file:\n" + ex.Message);
                }
            }
        }

        private void Compile()
        {
            if (SelectedCond is not null)
            {
                bool error = true;
                string textToCompile;
                if (_isGraphViewActive && GraphViewControl.DataContext is ConditionGraphRootViewModel graphRoot)
                {
                    if (!graphRoot.IsFullyParsed)
                    {
                        compilationMsgBox.Text = "This expression is too complex for the graph editor. Switch to text view to edit it.";
                        return;
                    }
                    if (!graphRoot.TryValidate(out var validationMessage))
                    {
                        compilationMsgBox.Text = validationMessage;
                        return;
                    }
                    textToCompile = graphRoot.Serialize();
                    SelectedCond.GraphViewModel = graphRoot;
                    SelectedCond.PreserveGraphView = true;
                }
                else
                {
                    textToCompile = ConditionalTextBox.Text;
                    SelectedCond.GraphViewModel = null;
                    SelectedCond.PreserveGraphView = false;
                }
                compilationMsgBox.Text = SelectedCond?.Compile(textToCompile, out error);
                if (!error)
                {
                    // Don't re-parse the graph after compile. The graph is the user's
                    // source of truth while in graph view. Re-parsing from the compiled
                    // data would lose sub-groups with a single child because the bytecode
                    // format cannot represent single-operand &&/|| groups.
                    DisplayCondition();
                }
            }
        }

        private void DisplayCondition()
        {
            try
            {
                hexBox.ByteProvider = new ReadOptimizedByteProvider(_selectedCond.Conditional.Data);
                ConditionalTextBox.Text = _selectedCond.Conditional.Decompile();
            }
            catch (Exception e)
            {
                ConditionalTextBox.Text = $"ERROR! COULD NOT DECOMPILE!\n{e.FlattenException()}";
            }
        }

        private bool CanCompile()
        {
            return SelectedCond is not null && !string.IsNullOrWhiteSpace(ConditionalTextBox.Text);
        }

        public void LoadFile(string filePath, int cndId)
        {
            LoadFile(filePath);
            SelectedCond = Conditionals.FirstOrDefault(c => c.ID == cndId);
            ConditionalsListBox.ScrollIntoView(SelectedCond);
        }

        public void LoadFile(string filePath)
        {
            Conditionals.ClearEx();
            SelectedCond = null;
            try
            {
                File = CNDFile.FromFile(filePath);
                RecentsController.AddRecent(filePath, false, null); // Can we infer game this file is for?
                RecentsController.SaveRecentList(true);
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message);

                File = null;
                CurrentFileName = null;
                CurrentFilePath = null;
                Title = "Conditionals Editor";
                return;
            }

            CurrentFileName = Path.GetFileName(filePath);
            CurrentFilePath = filePath;
            Title = $"Conditionals Editor - {filePath}";
            Conditionals.AddRange(File.ConditionalEntries.OrderBy(c => c.ID).Select(c => new CondListEntry(c)));
        }

        private void NewFile()
        {
            if (FileIsLoaded()) Save();
            File = new CNDFile
            {
                ConditionalEntries = new List<CNDFile.ConditionalEntry>()
            };
            CurrentFileName = null;
            CurrentFilePath = null;
            Conditionals.Clear();
            SelectedCond = null;
            Save();
        }

        private bool CanOpenCurrentFileLocation()
        {
            return !string.IsNullOrWhiteSpace(CurrentFilePath) && System.IO.File.Exists(CurrentFilePath);
        }

        private void OpenCurrentFileLocation()
        {
            if (!string.IsNullOrWhiteSpace(CurrentFilePath))
            {
                LegendaryExplorerCoreUtilities.OpenAndSelectFileInExplorer(CurrentFilePath);
            }
        }

        private bool CanCopyCurrentFileName()
        {
            return !string.IsNullOrWhiteSpace(CurrentFilePath);
        }

        private void CopyCurrentFileName()
        {
            if (!string.IsNullOrWhiteSpace(CurrentFilePath))
            {
                Clipboard.SetText(Path.GetFileName(CurrentFilePath));
            }
        }

        private void AddBlankConditional()
        {
            if (PromptDialog.Prompt(this, "Enter ID for new entry", selectText: true) is string txt)
            {
                if (int.TryParse(txt, out int newID) && newID > 0)
                {
                    var newCond = new CondListEntry(new CNDFile.ConditionalEntry
                    {
                        Data = ME3ConditionalsCompiler.Compile("Bool false"),
                        ID = newID
                    })
                    {
                        IsModified = true
                    };
                    if (Conditionals.Any(c => c.ID == newCond.ID))
                    {
                        var wdlg = MessageBox.Show("This conditional ID already exists in this file. Continue?", "Warning", MessageBoxButton.OKCancel);
                        if (wdlg == MessageBoxResult.Cancel)
                            return;
                    }
                    Conditionals.Add(newCond);
                    SelectedCond = newCond;
                    ConditionalsListBox.ScrollIntoView(SelectedCond);
                }
                else
                {
                    MessageBox.Show($"'{txt}' is not a positive integer!");
                }
            }
        }

        public void PropogateRecentsChange(string propogationSource, IEnumerable<RecentsControl.RecentItem> newRecents)
        {
            RecentsController.PropogateRecentsChange(false, newRecents);
        }

        public string Toolname => "ConditionalsEditor";

        private void ConditionalsEditorWindow_OnClosing(object sender, CancelEventArgs e)
        {
            if (e.Cancel)
                return;
            if (Conditionals.Any(c => c.IsModified) &&
                MessageBoxResult.No == MessageBox.Show($"{Path.GetFileName(File.FilePath) ?? "Untitled file"} has unsaved changes. Do you really want to close Conditionals Editor?",
                                                       "Unsaved changes", MessageBoxButton.YesNo))
            {
                e.Cancel = true;
                return;
            }

            RecentsController?.Dispose();
            hexBox = null;
        }

        private void ConditionalTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            compilationMsgBox.Clear();
        }

        private void ConditionalsListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _conditionalsDragStartPoint = e.GetPosition(ConditionalsListBox);
            _draggedConditional = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as CondListEntry;
        }

        private void ConditionalsListBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _conditionalsDragStartPoint is null || _draggedConditional is null)
            {
                return;
            }

            Point currentPosition = e.GetPosition(ConditionalsListBox);
            Vector dragDelta = _conditionalsDragStartPoint.Value - currentPosition;
            if (Math.Abs(dragDelta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(dragDelta.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            var draggedConditional = _draggedConditional;
            _conditionalsDragStartPoint = null;
            _draggedConditional = null;

            var data = new DataObject(ConditionalsDragFormat, new ConditionalDragData(_windowInstanceId, draggedConditional.ID, draggedConditional.Conditional.Data.ArrayClone()));
            DragDrop.DoDragDrop(ConditionalsListBox, data, DragDropEffects.Copy);
        }

        private void ConditionalsListBox_DragOver(object sender, DragEventArgs e)
        {
            if (File is null || !e.Data.GetDataPresent(ConditionalsDragFormat) || e.Data.GetData(ConditionalsDragFormat) is not ConditionalDragData dragData)
            {
                return;
            }

            e.Effects = dragData.SourceWindowId == _windowInstanceId ? DragDropEffects.None : DragDropEffects.Copy;
            e.Handled = true;
        }

        private void ConditionalsListBox_Drop(object sender, DragEventArgs e)
        {
            if (File is null || !e.Data.GetDataPresent(ConditionalsDragFormat) || e.Data.GetData(ConditionalsDragFormat) is not ConditionalDragData dragData)
            {
                return;
            }

            e.Handled = true;
            if (dragData.SourceWindowId == _windowInstanceId)
            {
                return;
            }

            var newCond = new CondListEntry(new CNDFile.ConditionalEntry
            {
                Data = dragData.Data.ArrayClone(),
                ID = dragData.ID
            })
            {
                IsModified = true
            };

            if (Conditionals.Any(c => c.ID == newCond.ID))
            {
                var wdlg = MessageBox.Show("This conditional ID already exists in this file. Continue?", "Warning", MessageBoxButton.OKCancel);
                if (wdlg == MessageBoxResult.Cancel)
                {
                    return;
                }
            }

            int insertIndex = GetDropIndex(e.GetPosition(ConditionalsListBox));
            Conditionals.Insert(insertIndex, newCond);
            SelectedCond = newCond;
            ConditionalsListBox.ScrollIntoView(newCond);
        }

        private int GetDropIndex(Point dropPosition)
        {
            var dropTarget = FindVisualParent<ListBoxItem>(ConditionalsListBox.InputHitTest(dropPosition) as DependencyObject);
            if (dropTarget?.DataContext is not CondListEntry targetEntry)
            {
                return Conditionals.Count;
            }

            int index = Conditionals.IndexOf(targetEntry);
            Point itemTopLeft = dropTarget.TranslatePoint(new Point(), ConditionalsListBox);
            if (dropPosition.Y - itemTopLeft.Y > dropTarget.ActualHeight / 2)
            {
                index++;
            }

            return Math.Min(index, Conditionals.Count);
        }

        private static T FindVisualParent<T>(DependencyObject source) where T : DependencyObject
        {
            while (source is not null)
            {
                if (source is T typedSource)
                {
                    return typedSource;
                }

                source = VisualTreeHelper.GetParent(source);
            }

            return null;
        }

        public class CondListEntry : NotifyPropertyChangedBase
        {
            private bool _isModified;
            public bool IsModified
            {
                get => _isModified;
                set => SetProperty(ref _isModified, value);
            }

            private int _iD;
            public int ID
            {
                get => _iD;
                set
                {
                    if (SetProperty(ref _iD, value))
                    {
                        IsModified = true;
                        Conditional.ID = value;
                        PlotPath = PlotDatabases.FindPlotConditionalByID(value, MEGame.LE3)?.Path;
                    }
                }
            }

            private string _plotPath;

            public string PlotPath
            {
                get => _plotPath;
                set => SetProperty(ref _plotPath, value);
            }

            public CNDFile.ConditionalEntry Conditional;

            public ConditionGraphRootViewModel GraphViewModel { get; set; }

            public bool PreserveGraphView { get; set; }

            public CondListEntry(CNDFile.ConditionalEntry conditional)
            {
                Conditional = conditional;
                _iD = conditional.ID;
                PlotPath = PlotDatabases.FindPlotConditionalByID(conditional.ID, MEGame.LE3)?.Path;
            }

            public string Compile(string text, out bool error)
            {
                var original = Conditional.Data;
                try
                {
                    Conditional.Compile(text);
                    //the compiler is somewhat... lacking, in proper validation, so we use decompiler to see if compilation
                    //produced something useful (it should throw if there's an error)
                    Conditional.Decompile();
                }
                catch (Exception e)
                {
                    Conditional.Data = original;
                    error = true;
                    return $"Compilation Error!\n{e.GetType().Name}: {e.Message}";
                }
                if (!original.AsSpan().SequenceEqual(Conditional.Data))
                {
                    IsModified = true;
                }

                error = false;
                return "Compiled!";
            }
        }

        [Serializable]
        private sealed class ConditionalDragData
        {
            public Guid SourceWindowId { get; }
            public int ID { get; }
            public byte[] Data { get; }

            public ConditionalDragData(Guid sourceWindowId, int id, byte[] data)
            {
                SourceWindowId = sourceWindowId;
                ID = id;
                Data = data;
            }
        }

        private bool _isGraphViewActive;

        private void GraphViewToggle_Checked(object sender, RoutedEventArgs e)
        {
            _isGraphViewActive = true;
            SwitchToGraphView();
            Settings.ConditionalsEditor_DefaultGraphView = true;
            Settings.Save();
        }

        private void GraphViewToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            SwitchToTextView();
            _isGraphViewActive = false;
            Settings.ConditionalsEditor_DefaultGraphView = false;
            Settings.Save();
        }

        private void SwitchToGraphView()
        {
            if (SelectedCond == null) return;

            try
            {
                if (SelectedCond.PreserveGraphView && SelectedCond.GraphViewModel != null)
                {
                    GraphViewControl.DataContext = SelectedCond.GraphViewModel;
                }
                else
                {
                    string text = SelectedCond.Conditional.Decompile();
                    var graphRoot = ConditionGraphRootViewModel.FromDecompiledText(text);
                    SelectedCond.GraphViewModel = graphRoot;
                    SelectedCond.PreserveGraphView = false;
                    GraphViewControl.DataContext = graphRoot;
                }
                TextViewPanel.Visibility = Visibility.Collapsed;
                GraphViewControl.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                compilationMsgBox.Text = $"Failed to parse conditional for graph view: {ex.Message}";
                GraphViewToggle.IsChecked = false;
            }
        }

        private void SwitchToTextView()
        {
            TextViewPanel.Visibility = Visibility.Visible;
            GraphViewControl.Visibility = Visibility.Collapsed;

            if (SelectedCond != null)
            {
                DisplayCondition();
            }
        }

        private void RecompileAll_Click(object sender, RoutedEventArgs e)
        {
            var modified = new List<string>();
            foreach (CondListEntry condListEntry in Conditionals)
            {
                condListEntry.Compile(condListEntry.Conditional.Decompile(), out bool error);
                if (error)
                {
                    modified.Add(condListEntry.ID.ToString());
                }
            }

            modified.AddRange(Conditionals.Where(c => c.IsModified).Select(c => c.ID.ToString()).ToList());

            if (modified.Any())
            {
                new ListDialog(modified, "Modified Conditionals", "These conditionals did not recompile properly!", this).Show();
            }
            else
            {
                MessageBox.Show("All conditionals recompiled identically!");
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Note that you can have more than one file.
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                string ext = Path.GetExtension(files[0]).ToLower();
                if (ext != ".cnd")
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                }
            }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Note that you can have more than one file.
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                LoadFile(files[0]);
            }
        }
    }
}
