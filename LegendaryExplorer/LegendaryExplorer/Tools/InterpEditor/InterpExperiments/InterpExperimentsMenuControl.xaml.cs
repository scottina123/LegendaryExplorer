using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.UserControls.PackageEditorControls;

namespace LegendaryExplorer.Tools.InterpEditor.InterpExperiments
{
    /// <summary>
    /// Class that holds toolset development experiments. Actual experiment code should be in the Experiments classes
    /// </summary>
    public partial class InterpExperimentsMenuControl : MenuItem
    {
        private readonly List<MenuItem> _experimentMenuItems;

        public InterpExperimentsMenuControl()
        {
            LoadCommands();
            InitializeComponent();

            _experimentMenuItems = Items.OfType<MenuItem>().ToList();
            foreach (MenuItem menuItem in _experimentMenuItems)
            {
                menuItem.DataContext = this;
            }

            Items.Clear();
            Click += InterpExperimentsMenuControl_Click;
        }

        private void LoadCommands() { }

        private void InterpExperimentsMenuControl_Click(object sender, RoutedEventArgs e)
        {
            InterpEditorWindow owner = GetIEWindow();
            if (owner == null)
            {
                return;
            }

            IReadOnlyList<ExperimentBrowserItem> experiments = ExperimentBrowserCatalog.Create(
                _experimentMenuItems, ExperimentBrowserCatalog.GetHeader);
            var browser = new ExperimentsBrowserWindow(owner, "Interp Editor Experiments", experiments);
            if (browser.ShowDialog() == true)
            {
                browser.SelectedExperiment?.Invoke();
            }
        }

        public InterpEditorWindow GetIEWindow()
        {
            if (Window.GetWindow(this) is InterpEditorWindow iew)
            {
                return iew;
            }

            return null;
        }

        // EXPERIMENTS: EXKYWOR------------------------------------------------------------
        #region Exkywor's experiments
        private void InsertTrackMoveKey_Click(object sender, RoutedEventArgs e)
        {
            InterpEditorExperimentsE.InsertTrackMoveKeyExperiment(GetIEWindow());
        }
        private void DeleteTrackMoveKey_Click(object sender, RoutedEventArgs e)
        {
            InterpEditorExperimentsE.DeleteTrackMoveKey(GetIEWindow());
        }
        private void InsertDOFKey_Click(object sender, RoutedEventArgs e)
        {
            InterpEditorExperimentsE.InsertDOFKey(GetIEWindow());
        }
        private void DeleteDOFKey_Click(object sender, RoutedEventArgs e)
        {
            InterpEditorExperimentsE.DeleteDOFKey(GetIEWindow());
        }
        private void InsertGestureKey_Click(object sender, RoutedEventArgs e)
        {
            InterpEditorExperimentsE.InsertGestureKey(GetIEWindow());
        }
        private void DeleteGestureKey_Click(object sender, RoutedEventArgs e)
        {
            InterpEditorExperimentsE.DeleteGestureKey(GetIEWindow());
        }

        private void AddPresetDirectorGroup_Click(object sender, RoutedEventArgs e)
        {
            InterpEditorExperimentsE.AddPresetGroup("Director", GetIEWindow());
        }

        private void AddPresetCameraGroup_Click(object sender, RoutedEventArgs e)
        {
            InterpEditorExperimentsE.AddPresetGroup("Camera", GetIEWindow());
        }
        private void AddPresetCameraGroupWithKeys_Click(object sender, RoutedEventArgs e)
        {
            InterpEditorExperimentsE.AddPresetCameraWithKeys(GetIEWindow());
        }

        private void AddPresetActorGroup_Click(object sender, RoutedEventArgs e)
        {
            InterpEditorExperimentsE.AddPresetGroup("Actor", GetIEWindow());
        }

        private void AddPresetGestureTrack_Click(object sender, RoutedEventArgs e)
        {
            InterpEditorExperimentsE.AddPresetTrack("Gesture", GetIEWindow());
        }

        private void AddPresetGestureTrack2_Click(object sender, RoutedEventArgs e)
        {
            InterpEditorExperimentsE.AddPresetTrack("Gesture2", GetIEWindow());
        }

        private void SetStartingPose_Click(object sender, RoutedEventArgs e)
        {
            InterpEditorExperimentsE.SetStartingPose(GetIEWindow());
        } 
        #endregion

        // EXPERIMENTS: HenBagle------------------------------------------------------------
        #region HenBagle's Experiments
        private void OpenFovoLineAudio_Click(object sender, RoutedEventArgs e)
        {
            bool isMale = (string) (sender as FrameworkElement)?.Tag == "M";
            InterpEditorExperimentsH.OpenFovoLineAudio(isMale, GetIEWindow());
        }

        private void OpenFovoLineFXA_Click(object sender, RoutedEventArgs e)
        {
            bool isMale = (string) (sender as FrameworkElement)?.Tag == "M";
            InterpEditorExperimentsH.OpenFovoLineFXA(isMale, GetIEWindow());
        }

        private void OpenFovoLineDlg_Click(object sender, RoutedEventArgs e)
        {
            InterpEditorExperimentsH.OpenFovoLineDialogueEditor(GetIEWindow());
        }
        #endregion
    }
}
