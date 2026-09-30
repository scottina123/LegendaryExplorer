using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore.Gammtek.Extensions.Collections.Generic;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace LegendaryExplorer.Misc
{
    /// <summary>
    /// Meshplorer's mesh operations, shared with the Package Editor's mesh preview.
    /// Resolve the host and selection when invoked so commands follow the current export.
    /// </summary>
    public sealed class MeshToolsCommands
    {
        private readonly Func<WPFBase> getOwner;
        private readonly Func<ExportEntry> getExport;
        private readonly Func<MeshRenderer> getRenderer;
        private WPFBase Owner => getOwner();
        private IMEPackage Pcc => Owner?.Pcc;
        private ExportEntry CurrentExport => getExport();

        public ICommand ConvertToStaticMeshCommand { get; }
        public ICommand ImportFromUDKCommand { get; }
        public ICommand ReplaceFromUDKCommand { get; }
        public ICommand ExportToUDKCommand { get; }
        public ICommand ReplaceLODFromUDKCommand { get; }
        public ICommand ExportToPSKUModelCommand { get; }
        public ICommand ExportToPSKCommand { get; }
        public ICommand ExportToGltfCommand { get; }
        public ICommand ExportToGltfTexturesCommand { get; }
        public ICommand ReplaceFromGltfCommand { get; }
        public ICommand ImportNewFromGltfCommand { get; }

        public MeshToolsCommands(Func<WPFBase> getOwner, Func<ExportEntry> getExport, Func<MeshRenderer> getRenderer)
        {
            this.getOwner = getOwner;
            this.getExport = getExport;
            this.getRenderer = getRenderer;
            ConvertToStaticMeshCommand = new GenericCommand(ConvertToStaticMesh, CanConvertToStaticMesh);
            ImportFromUDKCommand = new GenericCommand(ImportFromUDK, CanUseTools);
            ReplaceFromUDKCommand = new GenericCommand(ReplaceFromUDK, IsMeshSelected);
            ExportToUDKCommand = new GenericCommand(ExportToUDK, IsMeshSelected);
            ReplaceLODFromUDKCommand = new GenericCommand(ImportLODFromUDK, IsSkeletalMeshSelected);
            ExportToPSKUModelCommand = new GenericCommand(() => getRenderer().EnsureUModelAndExport(), IsMeshSelected);
            ExportToPSKCommand = new GenericCommand(ExportToPSK, IsSkeletalMeshSelected);
            ExportToGltfCommand = new GenericCommand(() => ExportToGltf(GLTF.MaterialExportLevel.NameOnly), IsMeshSelected);
            ExportToGltfTexturesCommand = new GenericCommand(() => ExportToGltf(GLTF.MaterialExportLevel.Basic), IsMeshSelected);
            ReplaceFromGltfCommand = new GenericCommand(ReplaceFromGltf, CanReplaceFromGltf);
            ImportNewFromGltfCommand = new GenericCommand(ImportNewFromGltf, CanUseTools);
        }

        private bool CanUseTools() => Owner is { Pcc: not null, IsBusy: false } && getRenderer()?.IsBusy != true;

        private bool HasSelectedExport() => CanUseTools() && CurrentExport is { IsDefaultObject: false } export
            && export.FileRef == Pcc && Pcc.GetEntry(export.UIndex) == export;

        private bool CanReplaceFromGltf() => HasSelectedExport() && CurrentExport.ClassName is "StaticMesh" or "SkeletalMesh";

        private void ExportToGltf(GLTF.MaterialExportLevel materialExportLevel)
        {
            GltfHelper.ExportMeshToGltf(Owner, null, Pcc, CurrentExport, materialExportLevel);
        }

        private void ReplaceFromGltf()
        {
            GltfHelper.ReplaceFromGltf(Owner, CurrentExport);
        }

        private void ImportNewFromGltf()
        {
            GltfHelper.ImportNewFromGltf(Owner);
        }

        private void ExportToPSK()
        {
            var d = new SaveFileDialog { Filter = "PSK|*.psk" };
            if (DirectoryMemory.ShowDialog(d) == true)
            {
                try
                {
                    switch (ObjectBinary.From(CurrentExport))
                    {
                        case SkeletalMesh skelMesh:
                            PSK.CreateFromSkeletalMesh(skelMesh).ToFile(d.FileName);
                            break;
                        default:
                            MessageBox.Show($"Cannot export a '{CurrentExport.ClassName}' to PSK");
                            return;
                    }
                    MessageBox.Show(Owner, "Done!");
                }
                catch (Exception e)
                {
                    new ExceptionHandlerDialog(e).ShowDialog();
                }
            }
        }

        private void ImportLODFromUDK()
        {
            ReplaceFromUDK(true);
        }

        private void ExportToUDK()
        {
            var d = new SaveFileDialog { Filter = GameFileFilters.UDKFileFilter };
            if (DirectoryMemory.ShowDialog(d) == true)
            {
                try
                {
                    MEPackageHandler.CreateAndSavePackage(d.FileName, MEGame.UDK);
                    using (IMEPackage upk = MEPackageHandler.OpenUDKPackage(d.FileName))
                    {
                        bool cachedDataChanged = CurrentExport.DataChanged;
                        bool cachedHeaderChanged = CurrentExport.HeaderChanged;
                        bool pendingChangesBackup = CurrentExport.EntryHasPendingChanges;
                        byte[] dataBackup = CurrentExport.Data;
                        ObjectBinary objBin = ObjectBinary.From(CurrentExport);
                        objBin.ForEachUIndex(CurrentExport.Game, new UIndexZeroer());
                        CurrentExport.WritePropertiesAndBinary(new PropertyCollection(), objBin);

                        EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.AddSingularAsChild, CurrentExport, upk, null, true, new RelinkerOptionsPackage(), out IEntry _);
                        CurrentExport.Data = dataBackup;
                        if (!cachedDataChanged)
                        {
                            CurrentExport.DataChanged = false;
                        }
                        if (!cachedHeaderChanged)
                        {
                            CurrentExport.HeaderChanged = false;
                        }
                        if (!pendingChangesBackup)
                        {
                            CurrentExport.EntryHasPendingChanges = false;
                        }

                        upk.Save();
                    }
                    MessageBox.Show(Owner, "Done!");
                }
                catch (Exception e)
                {
                    new ExceptionHandlerDialog(e).ShowDialog();
                }
            }
        }

        private void ReplaceFromUDK()
        {
            ReplaceFromUDK(false);
        }

        private void ReplaceFromUDK(bool lodOnly)
        {
            var d = new OpenFileDialog
            {
                Filter = GameFileFilters.UDKFileFilter, Title = "Select UDK package file",
                CustomPlaces = AppDirectories.GameCustomPlaces
            };
            if (DirectoryMemory.ShowDialog(d) == true)
            {
                try
                {
                    using IMEPackage udk = MEPackageHandler.OpenUDKPackage(d.FileName);
                    string className = CurrentExport.ClassName;
                    if (EntrySelector.GetEntry<ExportEntry>(Owner, udk, $"Select {className} to import:", exp => exp.ClassName == className) is ExportEntry meshExport)
                    {
                        if (className == "SkeletalMesh")
                        {
                            SkeletalMesh newMesh = ObjectBinary.From<SkeletalMesh>(meshExport);
                            if (!GltfHelper.PrepareMeshForImport(Owner, newMesh, meshExport.ObjectName.Instanced))
                            {
                                return;
                            }
                            SkeletalMesh originalMesh = ObjectBinary.From<SkeletalMesh>(CurrentExport);

                            if (newMesh.RefSkeleton.Length != originalMesh.RefSkeleton.Length)
                            {
                                if (!lodOnly)
                                {
                                    var msgBoxResult = MessageBox.Show(Owner, "This SkeletalMesh has a different number of bones than the one you are replacing! " +
                                                                             "This may cause animations to no longer work for this mesh. " +
                                                                             "Are you SURE you want to continue?", "Bone count differs!", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                                    if (msgBoxResult != MessageBoxResult.Yes)
                                    {
                                        return;
                                    }
                                }
                                else
                                {
                                    MessageBox.Show(Owner, "Cannot replace a SkeletalMesh LOD with one that has a different number of bones!");
                                    return;
                                }
                            }

                            if (newMesh.RotOrigin.Pitch != originalMesh.RotOrigin.Pitch ||
                                newMesh.RotOrigin.Yaw != originalMesh.RotOrigin.Yaw ||
                                newMesh.RotOrigin.Roll != originalMesh.RotOrigin.Roll)
                            {
                                var messageBoxResult = MessageBox.Show(Owner, "The rotation origin of this mesh has changed. The original value is:" +
                                                $"\nPitch {originalMesh.RotOrigin.Roll}, Yaw {originalMesh.RotOrigin.Yaw}, Roll {originalMesh.RotOrigin.Roll}\n" +
                                                "The new value is:\n" +
                                                $"Pitch {newMesh.RotOrigin.Roll}, Yaw {newMesh.RotOrigin.Yaw}, Roll {newMesh.RotOrigin.Roll}\n" +
                                                "Would you like to preserve the original value?", "Rotation origin changed", MessageBoxButton.YesNo, MessageBoxImage.Question);

                                if (messageBoxResult == MessageBoxResult.Yes)
                                {
                                    newMesh.RotOrigin = originalMesh.RotOrigin;
                                }
                            }

                            newMesh.Materials = originalMesh.Materials.ArrayClone();

                            var lods = CurrentExport.GetProperty<ArrayProperty<StructProperty>>("LODInfo");
                            if (!lodOnly)
                            {
                                CurrentExport.WriteBinary(newMesh);

                                //Check LODs count
                                if (lods != null)
                                {
                                    if (lods.Count != originalMesh.LODModels.Length)
                                    {
                                        MessageBox.Show("ASSERT: The amount of items in the LODInfo array (in the export properties) doesn't match the amount of LODs in the original mesh! You need to correct this. LODInfo count should match the amount of LODModels in the binary.");
                                    }
                                }

                                if (newMesh.LODModels.Length < originalMesh.LODModels.Length)
                                {
                                    // we need to update the LOD models
                                    var newlods = lods.Take(newMesh.LODModels.Length).ToList();
                                    lods.Clear();
                                    lods.AddRange(newlods);
                                    CurrentExport.WriteProperty(lods);
                                }

                                if (newMesh.LODModels.Length > originalMesh.LODModels.Length)
                                {
                                    MessageBox.Show("ASSERT: The amount of LODs has increased for this mesh. You must adjust the amount of items in the LODInfo struct to match.");
                                }

                            }
                            else
                            {
                                //Transfer the top LOD in. This is due to some weird shit going on in UDK that makes it blow up.

                                //Build map of new bone names to old bone names so we can translate them
                                var newToOldBoneListIndexMapping = new List<int>(); //Maps old index to new index. This is WV code... but seems to work in a roundabout way
                                var incomingToExistingBoneMapping = new Dictionary<string, string>();
                                for (int i = 0; i < originalMesh.RefSkeleton.Length; i++)
                                {
                                    var incomingName = newMesh.RefSkeleton[i].Name.Name;
                                    //var existingName = originalMesh.RefSkeleton[i].Name.Name;
                                    //incomingToExistingBoneMapping[incomingName] = existingName;
                                    var mappedIndex = originalMesh.RefSkeleton.FindIndex(x => x.Name.Name == incomingName);
                                    if (mappedIndex < 0) Debug.WriteLine("Could not map bone! Name: " + incomingName);
                                    newToOldBoneListIndexMapping.Add(mappedIndex);
                                }

                                var incomingLOD = newMesh.LODModels[0];

                                //Map ActiveBoneIndexes
                                for (int i = 0; i < incomingLOD.ActiveBoneIndices.Length; i++)
                                {
                                    var existingId = incomingLOD.ActiveBoneIndices[i];
                                    var mappedId = newToOldBoneListIndexMapping[existingId];
                                    incomingLOD.ActiveBoneIndices[i] = (ushort)mappedId;
                                }

                                foreach (var chunk in incomingLOD.Chunks)
                                {
                                    //Map the BoneMap to the existing map
                                    for (int i = 0; i < chunk.BoneMap.Length; i++)
                                    {
                                        var existingId = chunk.BoneMap[i];
                                        var mappedId = newToOldBoneListIndexMapping[existingId];
                                        chunk.BoneMap[i] = (ushort)mappedId;
                                    }
                                }
                                //Remove the other LODs, otherwise this could look really weird when it tries to change lods. I don't know anyone who is adding LOD levels
                                originalMesh.LODModels = new[] { incomingLOD };

                                //write it out
                                CurrentExport.WriteBinary(originalMesh); //used to be NEW MESH
                                if (lods.Count > 1)
                                {
                                    // we need to update the LOD models
                                    var newlods = lods.Take(1).ToList();
                                    lods.Clear();
                                    lods.AddRange(newlods);
                                    CurrentExport.WriteProperty(lods);
                                }
                            }
                        }
                        else
                        {
                            StaticMesh newMesh;
                            StaticMesh originalMesh;
                            if (className == "FracturedStaticMesh")
                            {
                                newMesh = ObjectBinary.From<FracturedStaticMesh>(meshExport);
                                originalMesh = ObjectBinary.From<FracturedStaticMesh>(CurrentExport);
                            }
                            else
                            {
                                newMesh = ObjectBinary.From<StaticMesh>(meshExport);
                                originalMesh = ObjectBinary.From<StaticMesh>(CurrentExport);
                            }

                            if (!GltfHelper.PrepareMeshForImport(Owner, newMesh, meshExport.ObjectName.Instanced))
                            {
                                return;
                            }

                            newMesh.BodySetup = 0;
                            if (originalMesh.LODModels.Any())
                            {
                                int[] mats = originalMesh.LODModels[0].Elements.Select(el => el.Material).ToArray();
                                foreach (StaticMeshRenderData lodModel in newMesh.LODModels)
                                {
                                    for (int i = 0; i < lodModel.Elements.Length; i++)
                                    {
                                        int matIndex = 0;
                                        if (i < mats.Length)
                                        {
                                            matIndex = mats[i];
                                        }
                                        lodModel.Elements[i].Material = matIndex;
                                    }
                                }
                            }
                            CurrentExport.WriteBinary(newMesh);
                        }
                        MessageBox.Show(Owner, "Done!");
                    }
                }
                catch (Exception e)
                {
                    new ExceptionHandlerDialog(e).ShowDialog();
                }
            }
        }

        private void ImportFromUDK()
        {
            var d = new OpenFileDialog
            {
                Filter = GameFileFilters.UDKFileFilter, Title = "Select UDK package file",
                CustomPlaces = AppDirectories.GameCustomPlaces
            };
            if (DirectoryMemory.ShowDialog(d) == true)
            {
                try
                {
                    using IMEPackage udk = MEPackageHandler.OpenUDKPackage(d.FileName);
                    string[] meshClasses = { "StaticMesh", "FracturedStaticMesh", "SkeletalMesh" };
                    if (EntrySelector.GetEntry<ExportEntry>(Owner, udk, "Select mesh to import:", exp => meshClasses.Contains(exp.ClassName)) is ExportEntry meshExport)
                    {
                        ObjectBinary objBin = ObjectBinary.From(meshExport);
                        if (!GltfHelper.PrepareMeshForImport(Owner, objBin, meshExport.ObjectName.Instanced))
                        {
                            return;
                        }
                        objBin.ForEachUIndex(MEGame.UDK, new UIndexZeroer());
                        meshExport.WritePropertiesAndBinary(new PropertyCollection(), objBin);
                        var results = EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.AddSingularAsChild, meshExport, Pcc,
                                                                           null, true, new RelinkerOptionsPackage(), out _);
                        if (results.Any())
                        {
                            var ld = new ListDialog(results, "Relink report",
                                                           "The following items failed to relink.(This does not mean the import was unsuccessful, " +
                                                           "just that the listed values will have to be corrected in the Properties editor and Binary Interpreter)", Owner);
                            ld.Show();
                        }
                        else
                        {
                            MessageBox.Show("Mesh has been imported with no reported issues.");
                        }
                    }
                }
                catch (Exception e)
                {
                    new ExceptionHandlerDialog(e).ShowDialog();
                }
            }
        }

        private bool IsMeshSelected() => HasSelectedExport() && CurrentExport.ClassName is "StaticMesh" or "FracturedStaticMesh" or "SkeletalMesh";
        private bool IsSkeletalMeshSelected() => HasSelectedExport() && CurrentExport.ClassName == "SkeletalMesh";

        private bool CanConvertToStaticMesh() => IsSkeletalMeshSelected() && (Pcc.Game is MEGame.ME3 || Pcc.Game.IsLEGame());

        private void ConvertToStaticMesh()
        {
            if (CurrentExport.ClassName == "SkeletalMesh")
            {
                StaticMesh stm = CurrentExport.GetBinaryData<SkeletalMesh>().ConvertToME3LEStaticMesh();
                CurrentExport.Class = Pcc.GetEntryOrAddImport("Engine.StaticMesh", "Class");
                CurrentExport.WritePropertiesAndBinary(new PropertyCollection
                {
                    new BoolProperty(true, "UseSimpleBoxCollision"),
                    new NoneProperty()
                }, stm);
            }
        }

    }
}
