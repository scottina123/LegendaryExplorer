using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.IO;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using System.Media;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.TLK.ME1;
using LegendaryExplorerCore.TLK.ME2ME3;
using HuffmanCompression = LegendaryExplorerCore.TLK.ME1.HuffmanCompression;
using ME2ME3HuffmanCompression = LegendaryExplorerCore.TLK.ME2ME3.HuffmanCompression;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.UserControls.ExportLoaderControls
{
    /// <summary>
    /// Interaction logic for TLKEditor.xaml
    /// </summary>
    public partial class TLKEditorExportLoader : FileExportLoaderControl
    {
        private ME2ME3TalkFile _currentMe2Me3Me2Me3TalkFile;
        private bool _suppressInlineEditEvents;
        public List<TLKStringRef> LoadedStrings; //Loaded TLK
        public ObservableCollectionExtended<TLKStringRef> CleanedStrings { get; } = new(); // Displayed
        private bool xmlUp;

        public bool StringSelected
        {
            get
            {
                return GetActiveString() is not null;
            }
        }

        public TLKEditorExportLoader() : base("TLKEditor")
        {
            DataContext = this;
            LoadCommands();
            InitializeComponent();
        }

        public ICommand CommitCommand { get; set; }
        public ICommand SetIDCommand { get; set; }
        public ICommand ExportXmlCommand { get; set; }
        public ICommand ImportXmlCommand { get; set; }
        public ICommand ViewXmlCommand { get; set; }
        public ICommand DeleteStringCommand { get; set; }
        public ICommand SearchCommand { get; set; }
        public ICommand AddStringCommand { get; set; }
        public ICommand AddStringRangeCommand { get; set; }

        private void LoadCommands()
        {
            CommitCommand = new RelayCommand(CommitTLK, CanCommitTLK);
            SetIDCommand = new RelayCommand(SetStringID, StringIsSelected);
            DeleteStringCommand = new RelayCommand(DeleteString, StringIsSelected);

            SearchCommand = new GenericCommand(TextSearch, HasTLKLoaded);
            AddStringCommand = new GenericCommand(AddString, HasTLKLoaded);
            AddStringRangeCommand = new GenericCommand(AddStringRange, HasTLKLoaded);

            ExportXmlCommand = new GenericCommand(ExportToXml, HasTLKLoaded);
            ImportXmlCommand = new GenericCommand(ImportFromXml, HasTLKLoaded);
            ViewXmlCommand = new GenericCommand(ViewAsXml, HasTLKLoaded);
        }

        private void DeleteString(object obj)
        {
            var selectedItem = GetActiveString();
            if (selectedItem is null)
            {
                return;
            }

            CleanedStrings.Remove(selectedItem);
            LoadedStrings.Remove(selectedItem);
            FileModified = true;
        }

        private void SetStringID(object obj)
        {
            SetNewID();
        }

        public override void PopOut()
        {
            if (CurrentLoadedExport != null)
            {
                var elhw = new ExportLoaderHostedWindow(new TLKEditorExportLoader(), CurrentLoadedExport)
                {
                    Title = $"TLK Editor - {CurrentLoadedExport.UIndex} {CurrentLoadedExport.InstancedFullPath} - {CurrentLoadedExport.FileRef.FilePath}"
                };
                elhw.Show();
            }
        }

        private bool StringIsSelected(object obj)
        {
            return StringSelected;
        }

        private bool CanCommitTLK(object obj)
        {
            return FileModified;
        }

        private void CommitTLK(object obj)
        {
            MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            NormalizeLoadedStrings();
            var huff = new HuffmanCompression();
            huff.LoadInputData(LoadedStrings);
            huff.SerializeTalkfileToExport(CurrentLoadedExport);
            FileModified = false;
        }

        //SirC "efficiency is next to godliness" way of Checking export is ME1/TLK
        public override bool CanParse(ExportEntry exportEntry) => exportEntry.FileRef.Game.IsGame1() && exportEntry.ClassName == "BioTlkFile" && !exportEntry.IsDefaultObject;
        public override void PoppedOut(ExportLoaderHostedWindow elhw)
        {

            //Recents_MenuItem = recentsMenuItem;
            //LoadRecentList();
            //RefreshRecent(false);
        }

        /// <summary>
        /// Memory cleanup when this control is unloaded
        /// </summary>
        public override void Dispose()
        {
            CurrentLoadedExport = null;
            _currentMe2Me3Me2Me3TalkFile = null;
            LoadedStrings?.Clear();
            CleanedStrings?.ClearEx();
        }

        public override void LoadExport(ExportEntry exportEntry)
        {
            CurrentLoadedFile = null;
            var tlkFile = new ME1TalkFile(exportEntry); // Setup object as TalkFile
            LoadedStrings = tlkFile.StringRefs.ToList(); //This is not binded to so reassigning is fine
            _suppressInlineEditEvents = true;
            CleanedStrings.ClearEx(); //clear strings Ex does this in bulk (faster)
            CleanedStrings.AddRange(LoadedStrings.Where(x => x.StringID > 0).ToList()); //nest it remove 0 strings.
            _suppressInlineEditEvents = false;
            CurrentLoadedExport = exportEntry;
            FileModified = false;
        }

        public string CurrentLoadedFile { get; set; }

        public override void UnloadExport()
        {
            FileModified = false;
        }

        public bool HasTLKLoaded() => CurrentLoadedFile != null || CurrentLoadedExport != null;

        private void DisplayedString_ListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(StringSelected)); //Propogate this change
        }

        public int DlgStringID(int curID) //Dialog tlkstring id
        {
            int newID;
            while (true)
            {
                var inst = new PromptDialog("Set new string ID", "TLK Editor", curID.ToString(), true)
                {
                    Owner = Window.GetWindow(this)
                };
                //center to parent
                if (inst.ShowDialog() == true)
                {
                    if (int.TryParse(inst.ResponseText, out int newIDInt) &&
                        newIDInt > 0) //test result is an acceptable input
                    { 
                        if (LoadedStrings.Any(x => x.StringID == newIDInt))
                        {
                            MessageBox.Show($"String ID must be unique.\n{newIDInt} is currently in use in this TLK.");
                            continue;
                        }

                        newID = newIDInt;
                        break;
                    }

                    MessageBox.Show("String ID must be a positive integer");
                }
                else
                {
                    return curID; //cancel
                }
            }
            return newID;
        }

        private void AddString()
        {
            var blankstringref = new TLKStringRef(100, "New Blank Line", 1);
            LoadedStrings.Add(blankstringref);
            CleanedStrings.Add(blankstringref);
            FocusString(blankstringref, 1);
            SetNewID();
            FileModified = true;
        }

        private void AddStringRange()
        {
            // Get set of existing string IDs for efficient duplicate checking
            var existingIDs = LoadedStrings.Select(x => x.StringID).ToHashSet();

            // Show dialog
            var dialog = new AddStringRangeDialog(existingIDs)
            {
                Owner = Window.GetWindow(this)
            };

            if (dialog.ShowDialog() == true)
            {
                int startID = dialog.StartStringID;
                int endID = dialog.EndStringID;

                // Add strings in the range
                var newStrings = new List<TLKStringRef>();
                for (int id = startID; id <= endID; id++)
                {
                    var blankstringref = new TLKStringRef(id, "", 1);
                    newStrings.Add(blankstringref);
                }

                // Add all strings at once for better performance
                LoadedStrings.AddRange(newStrings);
                CleanedStrings.AddRange(newStrings);

                // Select the first added string
                FocusString(newStrings[0], 1);

                FileModified = true;
            }
        }

        private void ExportToXml()
        {
            var fnameBase = CurrentLoadedExport?.ObjectName.Name;
            if (fnameBase == null && CurrentLoadedFile != null) fnameBase = Path.GetFileNameWithoutExtension(CurrentLoadedFile);
            if (fnameBase == null) fnameBase = "TalkFile";
            var saveFileDialog = new SaveFileDialog
            {
                Filter = "XML Files (*.xml)|*.xml",
                FileName = fnameBase + ".xml"
            };
            if (saveFileDialog.ShowDialog() == true)
            {
                if (CurrentLoadedExport != null)
                {
                    var talkfile = new ME1TalkFile(CurrentLoadedExport);
                    talkfile.SaveToXML(saveFileDialog.FileName);
                } 
                else if (_currentMe2Me3Me2Me3TalkFile is not null)
                {
                    if (FileModified)
                    {
                        _currentMe2Me3Me2Me3TalkFile.LoadTlkDataFromStream(ME2ME3HuffmanCompression.SaveToTlkStream(LoadedStrings).SeekBegin());
                    }
                    _currentMe2Me3Me2Me3TalkFile?.SaveToXML(saveFileDialog.FileName);
                }
            }
        }

        private void ImportFromXml()
        {
            var openFileDialog = new OpenFileDialog
            {
                Multiselect = false,
                Filter = "XML Files (*.xml)|*.xml",
                CustomPlaces = AppDirectories.GameCustomPlaces
            };
            if (openFileDialog.ShowDialog() == true)
            {
                if (CurrentLoadedExport is not null)
                {
                    var compressor = new HuffmanCompression();
                    compressor.LoadInputData(openFileDialog.FileName);
                    compressor.SerializeTalkfileToExport(CurrentLoadedExport);
                }
                else if (_currentMe2Me3Me2Me3TalkFile is not null)
                {
                    ME2ME3HuffmanCompression compressor = new ();
                    compressor.LoadInputData(openFileDialog.FileName);
                    _currentMe2Me3Me2Me3TalkFile.LoadTlkDataFromStream(compressor.SaveToStream().SeekBegin());
                    RefreshME2ME3TLK();
                }
                FileModified = true; //this is not always technically true, but we'll assume it is
            }
        }

        private void ViewAsXml()
        {
            if (!xmlUp)
            {
                string xmlString = "";
                if (CurrentLoadedExport is not null)
                {
                    xmlString = ME1TalkFile.TLKtoXmlstring(CurrentLoadedExport.InstancedFullPath, LoadedStrings);
                }
                else if (_currentMe2Me3Me2Me3TalkFile is not null)
                {
                    if (FileModified)
                    {
                        _currentMe2Me3Me2Me3TalkFile.LoadTlkDataFromStream(ME2ME3HuffmanCompression.SaveToTlkStream(LoadedStrings).SeekBegin());
                    }
                    xmlString = _currentMe2Me3Me2Me3TalkFile.WriteXMLString();
                }
                popoutXmlBox.Text = xmlString;

                popupDlg.Height = ActualHeight;
                popupDlg.Width = ActualWidth;
                btnViewXML.ToolTip = "Close XML View.";
                popupDlg.IsOpen = true;
                xmlUp = true;
            }
        }

        private async void Evt_CloseXML(object sender, EventArgs e)
        {
            await System.Threading.Tasks.Task.Delay(100);  //Catch double clicks of XML button 
            xmlUp = false;
            btnViewXML.ToolTip = "View as XML.";
        }

        private void SetNewID()
        {
            if (GetActiveString() is TLKStringRef selectedItem)
            {
                var stringRefNewID = DlgStringID(selectedItem.StringID); //Run popout box to set tlkstring id
                if (selectedItem.StringID != stringRefNewID)
                {
                    selectedItem.StringID = stringRefNewID;
                    FileModified = true;
                }
            }
        }

        private void Evt_KeyUp(object sender, KeyEventArgs k)
        {
            if (k.Key == Key.Return)
            {
                TextSearch();
            }
        }

        private void TextSearch()
        {
            string searchTerm = boxSearch.Text.Trim().ToLower();
            if (searchTerm == "") return; //don't search blank

            int pos = CleanedStrings.IndexOf(GetActiveString());
            pos += 1; //search this and 1 forward
            for (int i = 0; i < CleanedStrings.Count; i++)
            {
                int curIndex = (i + pos) % CleanedStrings.Count;
                TLKStringRef node = CleanedStrings[curIndex];

                if (node.StringID.ToString().Contains(searchTerm))
                {
                    //ID Search
                    FocusString(node, 0);
                    return;
                }
                else if (node.Data != null && node.Data.ToLower().Contains(searchTerm))
                {
                    FocusString(node, 1);
                    return;
                }
            }
            //Not found
            SystemSounds.Beep.Play();
        }

        public override void LoadFile(string filepath)
        {
            UnloadExport();
            CurrentLoadedFile = filepath;
            _currentMe2Me3Me2Me3TalkFile = new ME2ME3TalkFile(filepath);
            LoadedFile = filepath;
            RefreshME2ME3TLK();
            FileModified = false;
            Window.GetWindow(this).Title = "TLK Editor - " + filepath;
            OnFileLoaded(EventArgs.Empty);
        }

        private void RefreshME2ME3TLK()
        {
            LoadedStrings = _currentMe2Me3Me2Me3TalkFile.StringRefs.ToList(); //This is not bound to so reassigning is fine
            _suppressInlineEditEvents = true;
            CleanedStrings.ReplaceAll(LoadedStrings.Where(x => x.StringID > 0).ToList()); //remove 0 or null strings.
            _suppressInlineEditEvents = false;
        }

        public void LoadFileFromStream(Stream stream, string source)
        {
            UnloadExport();
            CurrentLoadedFile = null;
            _currentMe2Me3Me2Me3TalkFile = new ME2ME3TalkFile(stream, source);

            // Need way to load a file without having it show up in the recents

            RefreshME2ME3TLK();
            FileModified = false;
        }

        public override bool CanLoadFile()
        {
            //this doesn't do any background threading so we can always load files
            return true;
        }

        internal override void OpenFile()
        {
            var d = new OpenFileDialog
            {
                Title = "Open TLK file",
                Filter = "ME2/ME3/LE2/LE3 Talk Files|*.tlk",
                CustomPlaces = AppDirectories.GameCustomPlaces
            };
            if (d.ShowDialog() == true)
            {
#if !DEBUG
                try
                {
#endif
                LoadFile(d.FileName);
#if !DEBUG
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Unable to open file:\n" + ex.Message);
                }
#endif
            }
        }

        public override void Save()
        {
            if (CurrentLoadedExport != null)
            {
                if (FileModified)
                {
                    CommitTLK(null);
                }

                CurrentLoadedExport.FileRef.Save();
                RefreshLoadedTlksAfterSave(CurrentLoadedExport.FileRef.Game, CurrentLoadedExport.FileRef.FilePath, CurrentLoadedExport.UIndex);
            }
            else if (_currentMe2Me3Me2Me3TalkFile is not null)
            {
                if (CurrentLoadedFile is null)
                {
                    MessageBox.Show("Cannot save TLK File loaded from an SFAR. Use the Save As option to save your changes to a new file.");
                    return;
                }
                // CurrentME2ME3TalkFile.
                NormalizeLoadedStrings();
                ME2ME3HuffmanCompression.SaveToTlkFile(_currentMe2Me3Me2Me3TalkFile.FilePath, LoadedStrings);
                _currentMe2Me3Me2Me3TalkFile = new ME2ME3TalkFile(CurrentLoadedFile);
                FileModified = false; //you can only commit to file, not to export and then file in file mode.
                RefreshLoadedTlksAfterSave(GetCurrentLoadedFileGame(), CurrentLoadedFile);
            }
            //throw new NotImplementedException();

        }

        private void RefreshLoadedTlksAfterSave(MEGame game, string tlkPath, int exportNumber = 0)
        {
            if (game == MEGame.Unknown)
            {
                return;
            }

            try
            {
                TLKManagerWPF.AutoFindAndReloadTlks(game, tlkPath, exportNumber);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"TLK was saved, but refreshing loaded TLKs failed:\n{ex.Message}", "TLK Refresh Error");
            }
        }

        private MEGame GetCurrentLoadedFileGame()
        {
            if (string.IsNullOrEmpty(CurrentLoadedFile))
            {
                return MEGame.Unknown;
            }

            if (ME2TalkFiles.LoadedTlks.Any(x => string.Equals(x.FilePath, CurrentLoadedFile, StringComparison.OrdinalIgnoreCase))) return MEGame.ME2;
            if (ME3TalkFiles.LoadedTlks.Any(x => string.Equals(x.FilePath, CurrentLoadedFile, StringComparison.OrdinalIgnoreCase))) return MEGame.ME3;
            if (LE2TalkFiles.LoadedTlks.Any(x => string.Equals(x.FilePath, CurrentLoadedFile, StringComparison.OrdinalIgnoreCase))) return MEGame.LE2;
            if (LE3TalkFiles.LoadedTlks.Any(x => string.Equals(x.FilePath, CurrentLoadedFile, StringComparison.OrdinalIgnoreCase))) return MEGame.LE3;

            string fullPath = Path.GetFullPath(CurrentLoadedFile);
            if (IsUnderDirectory(fullPath, ME2Directory.BioGamePath)) return MEGame.ME2;
            if (IsUnderDirectory(fullPath, ME3Directory.BioGamePath)) return MEGame.ME3;
            if (IsUnderDirectory(fullPath, LE2Directory.BioGamePath)) return MEGame.LE2;
            if (IsUnderDirectory(fullPath, LE3Directory.BioGamePath)) return MEGame.LE3;

            return MEGame.Unknown;
        }

        private static bool IsUnderDirectory(string path, string directory)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            string normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalizedPath.StartsWith(normalizedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(normalizedPath, normalizedDirectory, StringComparison.OrdinalIgnoreCase);
        }

        public override void SaveAs()
        {
            if (CurrentLoadedExport != null)
            {
                SaveFileDialog d = new() { Filter = $"*{Path.GetExtension(CurrentLoadedExport.FileRef.FilePath)}|*{Path.GetExtension(CurrentLoadedExport.FileRef.FilePath)}" };
                if (d.ShowDialog() == true)
                {
                    if (FileModified)
                    {
                        CommitTLK(null);
                    }

                    CurrentLoadedExport.FileRef.Save(d.FileName);
                }
            }
            else if (_currentMe2Me3Me2Me3TalkFile is not null)
            {
                SaveFileDialog d = new() { Filter = "ME2/ME3/LE2/LE3 talk files|*.tlk" };
                if (d.ShowDialog() == true)
                {
                    // CurrentME2ME3TalkFile.
                    NormalizeLoadedStrings();
                    ME2ME3HuffmanCompression.SaveToTlkFile(d.FileName, LoadedStrings);
                }
            }
        }

        public override bool CanSave() => CurrentLoadedExport is not null || _currentMe2Me3Me2Me3TalkFile is not null;

        //internal override void RecentFile_click(object sender, RoutedEventArgs e)
        //{
        //    string s = ((FrameworkElement)sender).Tag.ToString();
        //    if (File.Exists(s))
        //    {
        //        LoadFile(s);
        //    }
        //    else
        //    {
        //        MessageBox.Show("File does not exist: " + s);
        //    }
        //}

        public override string Toolname => "TLKEditor";

        internal override bool CanLoadFileExtension(string extension)
        {
            switch (extension)
            {
                case ".sfm":
                case ".u":
                case ".upk":
                case ".pcc":
                case ".tlk":
                    return true;
                default:
                    return false;
            }
        }

        private void InlineEditor_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: TLKStringRef item })
            {
                FocusString(item, 1);
            }
        }

        private void StringIdTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox textBox && textBox.DataContext is TLKStringRef item)
            {
                FocusString(item, 0);
                textBox.Tag = item.StringID;
                textBox.SelectAll();
            }
        }

        private TLKStringRef GetActiveString()
        {
            if (DisplayedString_ListBox?.CurrentCell.Item is TLKStringRef currentItem)
            {
                return currentItem;
            }

            return DisplayedString_ListBox?.SelectedItem as TLKStringRef;
        }

        private void FocusString(TLKStringRef item, int columnIndex)
        {
            if (DisplayedString_ListBox == null || item == null || DisplayedString_ListBox.Columns.Count == 0)
            {
                return;
            }

            columnIndex = Math.Clamp(columnIndex, 0, DisplayedString_ListBox.Columns.Count - 1);
            var cellInfo = new DataGridCellInfo(item, DisplayedString_ListBox.Columns[columnIndex]);
            DisplayedString_ListBox.CurrentCell = cellInfo;
            DisplayedString_ListBox.SelectedCells.Clear();
            DisplayedString_ListBox.SelectedCells.Add(cellInfo);
            DisplayedString_ListBox.ScrollIntoView(item, DisplayedString_ListBox.Columns[columnIndex]);
        }

        private void StringDataTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressInlineEditEvents)
            {
                return;
            }

            if (sender is TextBox textBox && textBox.IsKeyboardFocusWithin)
            {
                FileModified = true;
            }
        }

        private void StringIdTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = e.Text.Any(c => !char.IsDigit(c));
        }

        private void StringIdTextBox_OnPasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.SourceDataObject.GetDataPresent(DataFormats.Text))
            {
                e.CancelCommand();
                return;
            }

            if (e.SourceDataObject.GetData(DataFormats.Text) is not string pastedText || pastedText.Any(c => !char.IsDigit(c)))
            {
                e.CancelCommand();
            }
        }

        private void StringIdTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox textBox)
            {
                return;
            }

            if (e.Key == Key.Enter)
            {
                CommitStringIdEdit(textBox);
                e.Handled = true;
                textBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            }
            else if (e.Key == Key.Escape)
            {
                ResetStringIdText(textBox);
                e.Handled = true;
            }
        }

        private void StringIdTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                CommitStringIdEdit(textBox);
            }
        }

        private void CommitStringIdEdit(TextBox textBox)
        {
            if (_suppressInlineEditEvents || textBox.DataContext is not TLKStringRef item)
            {
                return;
            }

            int originalId = textBox.Tag is int taggedId ? taggedId : item.StringID;
            string proposedText = textBox.Text.Trim();

            if (string.IsNullOrEmpty(proposedText) || !int.TryParse(proposedText, out int newId) || newId <= 0)
            {
                MessageBox.Show("String ID must be a positive integer");
                ResetStringIdText(textBox, originalId);
                return;
            }

            if (LoadedStrings.Any(x => !ReferenceEquals(x, item) && x.StringID == newId))
            {
                MessageBox.Show($"String ID must be unique.\n{newId} is currently in use in this TLK.");
                ResetStringIdText(textBox, originalId);
                return;
            }

            if (newId != originalId)
            {
                item.StringID = newId;
                FileModified = true;
            }

            textBox.Tag = item.StringID;
            textBox.Text = item.StringID.ToString();
        }

        private void ResetStringIdText(TextBox textBox, int? originalId = null)
        {
            int resetId = originalId ?? (textBox.Tag is int taggedId ? taggedId : 0);
            textBox.Text = resetId > 0 ? resetId.ToString() : string.Empty;
        }

        private void NormalizeLoadedStrings()
        {
            if (LoadedStrings == null)
            {
                return;
            }

            foreach (TLKStringRef stringRef in LoadedStrings)
            {
                if (stringRef.Data is not null)
                {
                    stringRef.Data = stringRef.Data.Replace("\r\n", "\n");
                }
            }
        }

        private void CloneLineMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: TLKStringRef sourceString } } })
            {
                return;
            }

            string response = PromptDialog.Prompt(
                this,
                "How many copies of this TLK line should be created?",
                "Clone TLK Line",
                "1",
                selectText: true,
                validator: text => int.TryParse(text, out int count) && count > 0
                    ? (true, null)
                    : (false, "Enter a positive integer."));

            if (response is null || !int.TryParse(response, out int cloneCount) || cloneCount <= 0)
            {
                return;
            }

            HashSet<int> clonedIds = Enumerable.Range(1, cloneCount)
                                               .Select(i => sourceString.StringID + i)
                                               .ToHashSet();

            int? conflictingId = LoadedStrings.Where(x => !ReferenceEquals(x, sourceString))
                                              .Select(x => (int?)x.StringID)
                                              .FirstOrDefault(id => id.HasValue && clonedIds.Contains(id.Value));
            if (conflictingId.HasValue)
            {
                MessageBox.Show($"Cannot clone line because TLK ID {conflictingId.Value} already exists.", "Clone TLK Line");
                return;
            }

            int loadedInsertIndex = LoadedStrings.IndexOf(sourceString);
            int cleanedInsertIndex = CleanedStrings.IndexOf(sourceString);
            if (loadedInsertIndex < 0 || cleanedInsertIndex < 0)
            {
                return;
            }

            var clones = Enumerable.Range(1, cloneCount)
                                   .Select(i => new TLKStringRef(sourceString.StringID + i, sourceString.Data, sourceString.Flags))
                                   .ToList();

            foreach (TLKStringRef clone in clones)
            {
                LoadedStrings.Insert(++loadedInsertIndex, clone);
                CleanedStrings.Insert(++cleanedInsertIndex, clone);
            }

            FocusString(clones[0], 1);
            FileModified = true;
        }

        private void CloseViewAsXml(object sender, RoutedEventArgs e)
        {
            Evt_CloseXML(sender, e);
        }
    }
}