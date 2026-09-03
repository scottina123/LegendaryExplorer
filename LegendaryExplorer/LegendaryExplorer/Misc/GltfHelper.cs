using LegendaryExplorer.Dialogs;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Microsoft.Win32;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Misc
{
    /// <summary>
    /// Wraps the GLTF class in LEC to make the functionality available to various parts of the user interface
    /// </summary>
    public static class GltfHelper
    {
        public static bool CanExportMeshToGltf(WPFBase owningWindow, IMEPackage package, IEntry selectedEntry)
        {
            // TODO is the selectedEntry a valid type?
            // are experiments enabled?
            return true;
        }
        public static void ExportMeshToGltf(WPFBase owningWindow, MeshRenderer meshRenderer, IMEPackage package, IEntry selectedEntry, GLTF.MaterialExportLevel materialExportLevel = GLTF.MaterialExportLevel.NameOnly)
        {
            if (package == null)
            {
                return;
            }
            if (package.Game == MEGame.ME1)
            {
                ShowError("This experiment does not yet support OT1; if you must do this, port it to another game first");
                return;
            }
            if (package.Game == MEGame.UDK)
            {
                ShowError("This experiment does not support UDK files;");
                return;
            }
            if (FilterSelectedItem(selectedEntry, ["SkeletalMesh", "StaticMesh", "SkeletalMeshComponent", "BioPawn", "SFXStuntActor", "SkeletalMeshActor"], out var export))
            {
                if (export.ClassName == "StaticMesh" && !(package.Game.IsGame3() || package.Game.IsLEGame()))
                {
                    ShowError("This experiment does not yet support OT1 or OT2 for static meshes.");
                    return;
                }
                var d = new SaveFileDialog { Filter = "glTF binary|*.glb|glTF|*.glTF", FileName = $"{selectedEntry.ObjectName.Instanced}.glb" };
                if (DirectoryMemory.ShowDialog(d) == true)
                {
                    Task.Run(() =>
                    {
                        if (owningWindow != null)
                        {
                            owningWindow.BusyText = "Exporting to glTF...";
                            owningWindow.IsBusy = true;
                        }
                        else
                        {
                            meshRenderer?.BusyText = "Exporting to glTF...";
                            meshRenderer?.IsBusy = true;
                        }
                        GLTF.ExportMeshToGltf(export, d.FileName, materialExportLevel, $"Legendary Explorer {AppVersion.DisplayedVersion}");
                    }).ContinueWithOnUIThread(x =>
                    {
                        if (owningWindow != null)
                        {
                            owningWindow?.IsBusy = false;
                        }
                        else
                        {
                            meshRenderer?.IsBusy = false;
                        }
                        if (x.Exception != null)
                        {
                            ShowError(x.Exception.FlattenException());
                        }
                    });
                }
            }
            else
            {
                ShowError("You must select a skeletal mesh, static mesh, SkeletalMeshComponent, SFXStuntActor, SkeletalMeshActor, or BioPawn");
            }
        }

        public static void ReplaceFromGltf(WPFBase window, IEntry selectedEntry)
        {
            if (window.Pcc == null)
            {
                return;
            }
            if (window.Pcc.Game == MEGame.ME1)
            {
                ShowError("This experiment does not yet support OT1; if you must do this, import it into another game and port it to OT1");
            }
            if (window.Pcc.Game == MEGame.UDK)
            {
                ShowError("This experiment does not support UDK files;");
            }
            if (GetGltfFromFile(out var gltf, out string _))
            {
                FilterSelectedItem(selectedEntry, ["SkeletalMesh", "StaticMesh"], out ExportEntry selectedMeshToReplace);
                GLTF.QueryMeshes(gltf, out var skeletalMeshes, out var staticMeshes);
                if (selectedMeshToReplace.ClassName == "SkeletalMesh")
                {
                    var meshCount = skeletalMeshes.Count();
                    if (meshCount == 0)
                    {
                        ShowError("You are trying to replace a skeletal mesh but the glTF file does not contain any skeletal meshes.");
                        return;
                    }
                }
                else if (selectedMeshToReplace.ClassName == "StaticMesh")
                {
                    var meshCount = staticMeshes.Count();
                    if (meshCount == 0)
                    {
                        ShowError("You are trying to replace a static mesh but the glTF file does not contain any static meshes.");
                        return;
                    }
                }
                RunGltfImport(window, () => GLTF.ConvertGltfToMesh(gltf, window.Pcc, selectedMeshToReplace,
                    confirmDecimation: oversizedLods => window.Dispatcher.Invoke(() => ConfirmMeshDecimation(window, oversizedLods))));
            }
        }

        public static void ImportNewFromGltf(WPFBase window)
        {
            if (window.Pcc == null)
            {
                return;
            }
            if (window.Pcc.Game == MEGame.ME1)
            {
                ShowError("This experiment does not yet support OT1; if you must do this, import it into another game and port it to OT1");
            }
            if (window.Pcc.Game == MEGame.UDK)
            {
                ShowError("This experiment does not support UDK files;");
            }
            if (GetGltfFromFile(out var gltf, out string filePath))
            {
                GLTF.QueryMeshes(gltf, out var skeletalMeshes, out var staticMeshes);
                if (!skeletalMeshes.Any() && !staticMeshes.Any())
                {
                    ShowError("The gltf you are trying to import does not contain any meshes.");
                    return;
                }
                RunGltfImport(window, () => GLTF.ConvertGltfToMesh(gltf, window.Pcc,
                    confirmDecimation: oversizedLods => window.Dispatcher.Invoke(() => ConfirmMeshDecimation(window, oversizedLods)),
                    combinedMeshName: Path.GetFileNameWithoutExtension(filePath)));
            }
        }

        private static void RunGltfImport(WPFBase window, System.Action import)
        {
            window.SetBusy("Importing glTF mesh...");
            Task.Run(import).ContinueWithOnUIThread(task =>
            {
                window.EndBusy();
                if (task.Exception != null)
                {
                    ShowError(task.Exception.FlattenException());
                }
            });
        }

        public static bool PrepareMeshForImport(WPFBase window, ObjectBinary mesh, string meshName)
        {
            IReadOnlyList<MeshLodVertexLimitInfo> oversizedLods = MeshDecimator.GetOversizedLods(mesh, meshName);
            if (oversizedLods.Count == 0)
            {
                return true;
            }
            if (!ConfirmMeshDecimation(window, oversizedLods))
            {
                return false;
            }
            MeshDecimator.DecimateToVertexLimit(mesh);
            return true;
        }

        private static bool ConfirmMeshDecimation(WPFBase window, IReadOnlyList<MeshLodVertexLimitInfo> oversizedLods)
        {
            StringBuilder details = new();
            foreach (MeshLodVertexLimitInfo lod in oversizedLods)
            {
                details.AppendLine($"• {lod.MeshName}, LOD {lod.LodIndex}: {lod.VertexCount:N0} vertices");
            }
            string message = $"The following mesh LOD{(oversizedLods.Count == 1 ? "" : "s")} exceed{(oversizedLods.Count == 1 ? "s" : "")} " +
                             $"Mass Effect's {MeshDecimator.MaxSupportedVertexCount:N0}-vertex limit:\n\n{details}\n" +
                             $"Decimate {(oversizedLods.Count == 1 ? "this LOD" : "these LODs")} to the limit and continue importing?";
            return MessageBox.Show(window, message, "Mesh exceeds vertex limit", MessageBoxButton.YesNo,
                       MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private static bool FilterSelectedItem(IEntry selectedItem, string[] expectedTypes, out ExportEntry entry)
        {
            entry = null;
            if (selectedItem == null)
            {
                return false;
            }

            foreach (var expectedType in expectedTypes)
            {
                if (selectedItem.IsA(expectedType))
                {
                    entry = (ExportEntry)selectedItem;
                    return entry != null;
                }
            }
            return false;
        }

        private static bool GetGltfFromFile(out SharpGLTF.Schema2.ModelRoot gltf, out string filePath)
        {
            var d = new OpenFileDialog
            {
                Filter = "gLTF|*.gltf;*.glb",
                Title = "Select a gLTF or glb file"
            };
            if (DirectoryMemory.ShowDialog(d) == true)
            {
                filePath = d.FileName;
                gltf = SharpGLTF.Schema2.ModelRoot.Load(filePath, SharpGLTF.Validation.ValidationMode.Skip);
                return true;
            }

            gltf = null;
            filePath = null;
            return false;
        }

        private static void ShowError(string errMsg)
        {
            MessageBox.Show(errMsg, "Warning", MessageBoxButton.OK);
        }
    }
}
