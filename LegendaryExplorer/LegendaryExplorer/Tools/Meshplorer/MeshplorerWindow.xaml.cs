using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.SharedUI.Controls;
using LegendaryExplorer.SharedUI.Interfaces;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using LegendaryExplorer.Misc;

namespace LegendaryExplorer.Tools.Meshplorer
{
    /// <summary>
    /// Interaction logic for MeshplorerWindow.xaml
    /// </summary>
    public partial class MeshplorerWindow : WPFBase, IRecents
    {
        private bool _isRendererBusy;
        public bool IsRendererBusy
        {
            get => _isRendererBusy;
            set => SetProperty(ref _isRendererBusy, value);
        }

        public ObservableCollectionExtended<ExportEntry> MeshExports { get; } = new();
        private ExportEntry _currentExport;
        public ExportEntry CurrentExport
        {
            get => _currentExport;
            set
            {
                _animationLoading?.Cancel();
                if (_loadedAnimation != null && value?.Game != _loadedAnimation.Sequence.Export.Game)
                    ClearAnimation();
                SetProperty(ref _currentExport, value);
                if (value == null)
                {
                    BinaryInterpreterTab_BinaryInterpreter.UnloadExport();
                    InterpreterTab_Interpreter.UnloadExport();
                    Mesh3DViewer.UnloadExport();
                }
                else
                {
                    BinaryInterpreterTab_BinaryInterpreter.LoadExport(CurrentExport);
                    InterpreterTab_Interpreter.LoadExport(CurrentExport);
                    Mesh3DViewer.LoadExport(CurrentExport);
                }
            }
        }

        private string FileQueuedForLoad;
        private ExportEntry ExportQueuedForFocusing;

        /// <summary>
        /// Inits a new instance of Meshplorer. If you are auto loading an export use the ExportEntry constructor instead.
        /// </summary>
        public MeshplorerWindow() : this(enableRecents: true)
        {
        }

        internal MeshplorerWindow(bool enableRecents) : base("Meshplorer")
        {
            LoadCommands();
            InitializeComponent();
            Mesh3DViewer.ShowLiveMaterialTintRandomizationControl = true;
            Mesh3DViewer.IsBusyChanged += RendererIsBusyChanged;
            BinaryInterpreterTab_BinaryInterpreter.MaterialTextureChanged += (_, _) =>
            {
                if (CurrentExport != null)
                    Mesh3DViewer.LoadExport(CurrentExport);
            };
            MeshesView.Filter = FilterExportList;
            RecentsController.InitRecentControl(enableRecents ? Toolname : null, Recents_MenuItem, fileToOpen => LoadFile(fileToOpen));
        }

        private void RendererIsBusyChanged(object sender, EventArgs e)
        {
            IsRendererBusy = Mesh3DViewer.IsBusy;
        }

        public MeshplorerWindow(ExportEntry exportToLoad) : this()
        {
            FileQueuedForLoad = exportToLoad.FileRef.FilePath;
            ExportQueuedForFocusing = exportToLoad;
        }

        private bool _showStaticMeshes = true;
        private bool _showSkeletalMeshes = true;
        private bool _showBrushes = true;
        private string _meshSearchText;

        public bool ShowStaticMeshes
        {
            get => _showStaticMeshes;
            set
            {
                SetProperty(ref _showStaticMeshes, value);
                MeshesView.Refresh();
            }
        }
        public bool ShowSkeletalMeshes
        {
            get => _showSkeletalMeshes;
            set
            {
                SetProperty(ref _showSkeletalMeshes, value);
                MeshesView.Refresh();
            }
        }

        public bool ShowBrushes
        {
            get => _showBrushes;
            set
            {
                SetProperty(ref _showBrushes, value);
                MeshesView.Refresh();
            }
        }

        public string MeshSearchText
        {
            get => _meshSearchText;
            set
            {
                SetProperty(ref _meshSearchText, value);
                MeshesView.Refresh();
            }
        }

        public ICollectionView MeshesView => CollectionViewSource.GetDefaultView(MeshExports);
        private bool FilterExportList(object obj)
        {
            if (obj is ExportEntry exp)
            {
                bool matchesMeshType = (exp.ClassName == "SkeletalMesh" && ShowSkeletalMeshes)
                                       || (exp.ClassName == "Brush" && ShowBrushes)
                                       || (exp.ClassName == "StaticMesh" && ShowStaticMeshes);

                if (!matchesMeshType)
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(MeshSearchText))
                {
                    return true;
                }

                return ContainsSearchText(exp.ObjectName.Instanced)
                       || ContainsSearchText(exp.ParentFullPath)
                       || ContainsSearchText(exp.ClassName)
                       || ContainsSearchText(exp.UIndex.ToString());
            }

            return false;
        }

        private bool ContainsSearchText(string value) => value?.Contains(MeshSearchText.Trim(), StringComparison.OrdinalIgnoreCase) == true;

        private MeshToolsCommands MeshTools { get; set; }

        public ICommand OpenFileCommand { get; set; }
        public ICommand SaveFileCommand { get; set; }
        public ICommand SaveAsCommand { get; set; }
        public ICommand FindCommand { get; set; }
        public ICommand GotoCommand { get; set; }
        public ICommand ConvertToStaticMeshCommand => MeshTools.ConvertToStaticMeshCommand;
        public ICommand ImportFromUDKCommand => MeshTools.ImportFromUDKCommand;
        public ICommand ReplaceFromUDKCommand => MeshTools.ReplaceFromUDKCommand;
        public ICommand ExportToUDKCommand => MeshTools.ExportToUDKCommand;
        public ICommand ReplaceLODFromUDKCommand => MeshTools.ReplaceLODFromUDKCommand;
        public ICommand ExportToPSKUModelCommand => MeshTools.ExportToPSKUModelCommand;
        public ICommand ExportToPSKCommand => MeshTools.ExportToPSKCommand;
        public ICommand ExportToGltfCommand => MeshTools.ExportToGltfCommand;
        public ICommand ExportToGltfTexturesCommand => MeshTools.ExportToGltfTexturesCommand;
        public ICommand ReplaceFromGltfCommand => MeshTools.ReplaceFromGltfCommand;
        public ICommand ImportNewFromGltfCommand => MeshTools.ImportNewFromGltfCommand;
        public ICommand ImportFromAssetDatabaseCommand { get; set; }
        private void LoadCommands()
        {
            MeshTools = new MeshToolsCommands(() => this, () => CurrentExport, () => Mesh3DViewer);
            OpenFileCommand = new GenericCommand(OpenFile);
            SaveFileCommand = new GenericCommand(SaveFile, PackageIsLoaded);
            SaveAsCommand = new GenericCommand(SaveFileAs, PackageIsLoaded);
            //FindCommand = new GenericCommand(FocusSearch, PackageIsLoaded);
            //GotoCommand = new GenericCommand(FocusGoto, PackageIsLoaded);
            ImportFromAssetDatabaseCommand = new GenericCommand(OpenMeshAssetImporter, CanImportMeshAssets);
        }

        private bool PackageIsLoaded() => Pcc != null;

        private async void SaveFile()
        {
            if (!EndInlineMeshNameEdit(commit: true))
                return;

            if (!await Pcc.SaveWithMountWarningAsync(this)) return;
        }

        private async void SaveFileAs()
        {
            if (!EndInlineMeshNameEdit(commit: true))
                return;

            string fileFilter;
            switch (Pcc.Game)
            {
                case MEGame.ME1:
                    fileFilter = GameFileFilters.ME1SaveFileFilter;
                    break;
                case MEGame.ME2:
                case MEGame.ME3:
                    fileFilter = GameFileFilters.ME3ME2SaveFileFilter;
                    break;
                default:
                    string extension = Path.GetExtension(Pcc.FilePath);
                    fileFilter = $"*{extension}|*{extension}";
                    break;
            }
            var d = new SaveFileDialog { Filter = fileFilter };
            if (DirectoryMemory.ShowDialog(d) == true)
            {
                if (!await Pcc.SaveWithMountWarningAsync(this, d.FileName)) return;
                MessageBox.Show("Done");
            }
        }

        private void OpenFile()
        {
            var d = AppDirectories.GetOpenPackageDialog();
            if (DirectoryMemory.ShowDialog(d) == true)
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

        public void LoadFile(string s, int goToIndex = 0)
        {
            if (!EndInlineMeshNameEdit(commit: true))
                return;

            try
            {
                //BusyText = "Loading " + Path.GetFileName(s);
                //IsBusy = true;
                StatusBar_LeftMostText.Text =
                    $"Loading {Path.GetFileName(s)} ({FileSize.FormatSize(new FileInfo(s).Length)})";
                Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ContextIdle, null);
                LoadMEPackage(s);

                MeshExports.ReplaceAll(Pcc.Exports.Where(Mesh3DViewer.CanParse));

                StatusBar_LeftMostText.Text = Path.GetFileName(s);
                Title = $"Meshplorer - {s}";

                RecentsController.AddRecent(s, false, Pcc?.Game);
                RecentsController.SaveRecentList(true);
                if (goToIndex != 0)
                {
                    CurrentExport = MeshExports.FirstOrDefault(x => x.UIndex == goToIndex);
                    ExportQueuedForFocusing = CurrentExport;
                }
                Mesh3DViewer.SceneViewer.SetShouldRender(true); // Set it to enable rendering
            }
            catch (Exception e)
            {
                StatusBar_LeftMostText.Text = "Failed to load " + Path.GetFileName(s);
                MessageBox.Show($"Error loading {Path.GetFileName(s)}:\n{e.Message}");
                IsBusy = false;
                IsBusyTaskbar = false;
                //throw e;
            }
        }

        public override void HandleUpdate(List<PackageUpdate> updates)
        {
            ExportEntry currentExport = CurrentExport;
            if (currentExport != null
             && updates.Any(update => update.Change == PackageChange.ExportData && update.Index == currentExport.UIndex)
             && Mesh3DViewer.CanParse(currentExport))
            {
                CurrentExport = currentExport;//trigger propertyset stuff
            }

            List<PackageUpdate> exportUpdates = updates.Where(upd => upd.Change.HasFlag(PackageChange.Export)).ToList();
            bool shouldUpdateList = false;
            foreach (ExportEntry meshExport in MeshExports)
            {
                if (exportUpdates.Any(upd => upd.Index == meshExport.UIndex))
                {
                    shouldUpdateList = true;
                    break;
                }
            }

            if (!shouldUpdateList)
            {
                foreach (PackageUpdate update in exportUpdates)
                {
                    if (Pcc.GetEntry(update.Index) is ExportEntry exp && Mesh3DViewer.CanParse(exp))
                    {
                        shouldUpdateList = true;
                        break;
                    }
                }
            }

            if (shouldUpdateList)
            {
                RefreshMeshExports();
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Note that you can have more than one file.
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                string ext = Path.GetExtension(files[0]).ToLower();
                if (ext != ".upk" && ext != ".pcc" && ext != ".sfm")
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                }
            }
            else
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
            }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Note that you can have more than one file.
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                string ext = Path.GetExtension(files[0]).ToLower();
                if (ext is ".upk" or ".pcc" or ".sfm")
                {
                    LoadFile(files[0]);
                }
            }
        }

        private void MeshplorerWPF_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (e.Cancel)
                return;

            _animationWindowClosed = true;
            ClearAnimation();
            _animationCatalog = null;
            CurrentExport = null;
            Mesh3DViewer.IsBusyChanged -= RendererIsBusyChanged;
            BinaryInterpreterTab_BinaryInterpreter.Dispose();
            InterpreterTab_Interpreter.Dispose();
            Mesh3DViewer.Dispose();
            RecentsController?.Dispose();
            UnLoadMEPackage();
            MeshExports.Clear();
        }

        private void OpenInPackageEditor_Clicked(object sender, RoutedEventArgs e)
        {
            if (MeshExportsList.SelectedItem is ExportEntry export)
            {
                var p = new PackageEditor.PackageEditorWindow();
                p.Show();
                p.LoadFile(export.FileRef.FilePath, export.UIndex);
                p.Activate(); //bring to front
            }
        }

        private void MeshSearchBox_OnTextChanged(SearchBox sender, string newText)
        {
            MeshSearchText = newText;
        }

        private void MeshplorerWPF_OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(FileQueuedForLoad))
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    //Wait for all children to finish loading
                    LoadFile(FileQueuedForLoad);
                    FileQueuedForLoad = null;

                    if (MeshExports.Contains(ExportQueuedForFocusing))
                    {
                        CurrentExport = ExportQueuedForFocusing;
                    }
                    ExportQueuedForFocusing = null;

                    Activate();
                }));
            }
        }

        public void PropogateRecentsChange(string propogationSource, IEnumerable<RecentsControl.RecentItem> newRecents)
        {
            RecentsController.PropogateRecentsChange(false, newRecents);
        }

        public string Toolname => "Meshplorer";
    }
}
