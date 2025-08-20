using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.SharedUI.Interfaces;
using LegendaryExplorer.Tools.LiveLevelEditor;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorer.UserControls.SharedToolControls.Scene3D;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TerraFX.Interop.Windows;

namespace LegendaryExplorer.Tools.LevelEditor
{
    /// <summary>
    /// Interaction logic for LevelEditor.xaml
    /// </summary>
    public partial class LevelEditor : WPFBase, IRecents
    {
        private readonly MeshRenderContext RenderContext;

        public ObservableCollectionExtended<ActorProxy> Actors { get; } = new();

        private ActorProxy selectedActor;
        public ActorProxy SelectedActor
        {
            get => selectedActor;
            set
            {
                if (SetProperty(ref selectedActor, value) && selectedActor is not null)
                {
                    FocusOnBounds(selectedActor.GetBounds());
                }
            }
        }

        public bool ShowCollision { get; set; }

        public string Toolname => "LevelEditor";
        public LevelEditor() : base("LevelEditor")
        {
            LoadCommands();
            InitializeComponent();
            RecentsController.InitRecentControl(Toolname, Recents_MenuItem, LoadFile);

            RenderContext = new MeshRenderContext
            {
                BackgroundColor = System.Windows.Media.Color.FromRgb(0x99, 0x99, 0x99)
            };
            RenderContext.Camera.FirstPerson = true;
            SceneViewer.Context = RenderContext;
        }

        private void UpdateScene(object sender, float e)
        {

        }

        private void RenderScene(object sender, EventArgs e)
        {
            RenderContext.UpdateLECameraConstants();
            DoRenderPass(RenderPass.Base);
            DoRenderPass(RenderPass.Hair);

            if (ShowCollision)
            {
                DoRenderPass(RenderPass.Collision);
            }

            void DoRenderPass(RenderPass pass)
            {
                foreach (var actor in Actors)
                {
                    actor.Render(RenderContext, pass);
                }
            }
        }

        private void CenterView()
        {
            if (Actors.Count > 0)
            {
                //place camera at the edge of the bounding sphere containing all actors, 30 degrees up, facing the midpoint 
                BoxSphereBounds fullBounds = Actors[0].GetBounds();
                for (int i = 1; i < Actors.Count; i++)
                {
                    fullBounds = fullBounds.Union(Actors[i].GetBounds());
                }
                FocusOnBounds(fullBounds);
            }
            else
            {
                RenderContext.Camera.Position = Vector3.Zero;
                RenderContext.Camera.Pitch = -MathF.PI / 5.0f;
                RenderContext.Camera.Yaw = MathF.PI / 4.0f;
            }
        }

        private void FocusOnBounds(BoxSphereBounds fullBounds)
        {
            Vector3 origin = fullBounds.Origin;
            float hyp = fullBounds.SphereRadius;
            (float sin, float cos) = MathF.SinCos(MathF.PI / 6);
            RenderContext.Camera.Position = new Vector3(origin.X, origin.Y + sin * hyp, origin.Y + cos * hyp);
            RenderContext.Camera.OrientTowards(origin);
        }

        private void LoadLevel(Level level)
        {
            IsBusy = true;
            BusyText = "Loading level...";
            SceneViewer.SetShouldRender(false);
            Task.Run(() =>
            {
                var actorExports = level.Actors.Where(Pcc.IsUExport).Select(Pcc.GetUExport);
                var actors = new List<ActorProxy>();
                foreach (var actorExport in actorExports)
                {
                    var className = actorExport.ClassName;
                    if (className is "StaticMeshCollectionActor")
                    {
                        var smca = actorExport.GetBinaryData<StaticMeshCollectionActor>();
                        for (int i = 0; i < smca.Components.Count; i++)
                        {
                            if (Pcc.TryGetUExport(smca.Components[i], out ExportEntry smcExport))
                            {
                                var smcActor = new StaticMeshComponentActorProxy(RenderContext, smcExport, smca, i);
                                actors.Add(smcActor);
                            }
                        }
                    }
                    else if (className is "StaticLightCollectionActor")
                    {
                        //var slca = actorExport.GetBinaryData<StaticLightCollectionActor>();
                        //for (int i = 0; i < slca.Components.Count; i++)
                        //{
                        //    if (Pcc.TryGetUExport(slca.Components[i], out ExportEntry lightComponentExport))
                        //    {

                        //    }
                        //}
                    }
                    else if (ActorProxy.Create(RenderContext, actorExport) is { } actorProxy)
                    {
                        actors.Add(actorProxy);
                    }
                }
                return actors;

            }).ContinueWithOnUIThread(prevTask =>
            {
                Actors.AddRange(prevTask.Result);
                CenterView();

                SceneViewer.SetShouldRender(true);
                IsBusy = false;
            });
        }

        public void LoadFile(string s)
        {
            try
            {
                UnloadLevel();
                Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ContextIdle, null);
                LoadMEPackage(s);

                StatusBar_LeftMostText.Text = Path.GetFileName(s);
                Title = $"Level Editor - {s}";

                RecentsController.AddRecent(s, false, Pcc?.Game);
                RecentsController.SaveRecentList(true);

                if (Pcc.Exports.FirstOrDefault(exp => exp.ClassName == "Level") is { } levelExport)
                {
                    Level levelBin = levelExport.GetBinaryData<Level>();
                    LoadLevel(levelBin);
                }
                else
                {
                    MessageBox.Show(this, "This is not a level file!");
                    UnloadLevel();
                    UnLoadMEPackage();
                }

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

        public void UnloadLevel()
        {
            RenderContext.EmptyCaches();
            Actors.DisposeAndClear();
        }

        public ICommand OpenFileCommand { get; set; }
        public ICommand SaveFileCommand { get; set; }
        public ICommand SaveAsCommand { get; set; }
        private void LoadCommands()
        {
            OpenFileCommand = new GenericCommand(OpenFile);
            SaveFileCommand = new GenericCommand(SaveFile, PackageIsLoaded);
            SaveAsCommand = new GenericCommand(SaveFileAs, PackageIsLoaded);
        }

        public override void HandleUpdate(List<PackageUpdate> updates)
        {
            //TODO
        }

        private bool PackageIsLoaded() => Pcc != null;

        private async void SaveFile()
        {
            await Pcc.SaveAsync();
        }

        private async void SaveFileAs()
        {
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
            if (d.ShowDialog() == true)
            {
                IsBusy = true;
                BusyText = "Saving...";
                await Pcc.SaveAsync(d.FileName);
                IsBusy = false;
            }
        }

        private void OpenFile()
        {
            var d = AppDirectories.GetOpenPackageDialog();
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
        private void LevelEditor_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (e.Cancel)
                return;

            RenderContext.UpdateScene -= UpdateScene;
            RenderContext.RenderScene -= RenderScene;

            UnloadLevel();
            SceneViewer.Dispose();
            RecentsController?.Dispose();
            UnLoadMEPackage();
        }
        public void PropogateRecentsChange(string propogationSource, IEnumerable<RecentsControl.RecentItem> newRecents)
        {
            RecentsController.PropogateRecentsChange(false, newRecents);
        }

        private void LevelEditor_Loaded(object sender, RoutedEventArgs e)
        {
            RenderContext.UpdateScene += UpdateScene;
            RenderContext.RenderScene += RenderScene;
        }
    }
}
