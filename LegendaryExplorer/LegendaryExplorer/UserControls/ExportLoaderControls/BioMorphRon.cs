using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows;
using LegendaryExplorer.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Save;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Microsoft.Win32;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public partial class MeshRenderer
{
    private List<string> ImportedMorphAccessoryPaths;
    private const string MorphRonFilter = "Trilogy Save Editor head morph (*.ron)|*.ron";

    private void ImportMorphRon_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentLoadedExport is null || !CanEditMorph) return;
        var dialog = new OpenFileDialog { Title = "Import Trilogy Save Editor head morph", Filter = MorphRonFilter };
        if (DirectoryMemory.ShowDialog(dialog) != true) return;
        try
        {
            ImportMorphRon(HeadMorph.FromRonFile(dialog.FileName));
        }
        catch (Exception exception)
        {
            MorphEditorStatus = $"Could not import head morph: {exception.Message}";
        }
    }

    private void ExportMorphRon_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentLoadedExport is null || !HasMorphEditorData) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export Trilogy Save Editor head morph", Filter = MorphRonFilter,
            DefaultExt = ".ron", AddExtension = true, FileName = CurrentLoadedExport.ObjectName.Instanced + ".ron"
        };
        if (DirectoryMemory.ShowDialog(dialog) != true) return;
        try
        {
            CreateMorphRon().ToRonFile(dialog.FileName);
            MorphEditorStatus = $"Exported current edits to {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception exception)
        {
            MorphEditorStatus = $"Could not export head morph: {exception.Message}";
        }
    }

    private HeadMorph CreateMorphRon()
    {
        if (WorkingMorphLods.Length > 4) throw new InvalidDataException("Trilogy Save Editor supports at most four head morph LODs.");
        PropertyCollection properties = CurrentLoadedExport.GetProperties();
        return new HeadMorph
        {
            HairMesh = RemoveMorphHairMesh ? "None"
                : MorphRonAssetPath(properties.GetProp<ObjectProperty>("m_oHairMesh")?.Value ?? 0),
            AccessoryMeshes = ImportedMorphAccessoryPaths?.ToList()
                ?? properties.GetProp<ArrayProperty<ObjectProperty>>("m_oOtherMeshes")?.Select(mesh => MorphRonAssetPath(mesh.Value)).ToList() ?? [],
            MorphFeatures = MorphFeatureItems.ToDictionary(feature => feature.Name, feature => feature.Value),
            OffsetBones = MorphSkeletonItems.ToDictionary(bone => bone.Name, bone => bone.Position),
            Lod0Vertices = Lod(0), Lod1Vertices = Lod(1), Lod2Vertices = Lod(2), Lod3Vertices = Lod(3),
            ScalarParameters = MorphScalarOverrides.ToDictionary(scalar => scalar.Name, scalar => scalar.Value),
            VectorParameters = MorphColorOverrides.ToDictionary(color => color.Name, color => color.Value),
            TextureParameters = MorphTextureOverrides.ToDictionary(texture => texture.Name,
                texture => texture.ImportedAssetPath ?? MorphRonAssetPath(texture.EntryIndex))
        };

        List<Vector3> Lod(int index) => index < WorkingMorphLods.Length ? [.. WorkingMorphLods[index] ?? []] : [];
    }

    private string MorphRonAssetPath(int index) => index == 0 ? "None"
        : CurrentLoadedExport.FileRef.GetEntry(index)?.MemoryFullPath
          ?? throw new InvalidDataException($"Asset entry {index} does not exist in the current package.");

    private void ImportMorphRon(HeadMorph morph)
    {
        if (CurrentLoadedExport is null || !CanEditMorph) return;
        int[] expectedCounts = MorphPreviewSkeletalMesh?.LODModels?
            .Select(lod => lod.VertexBufferGPUSkin.VertexData.Length).ToArray()
            ?? StoredMorphLods.Select(lod => lod?.Length ?? 0).ToArray();
        Vector3[][] lods = GetCompatibleRonLods(morph, expectedCounts);
        // Validate every reference before replacing any live edits. Missing assets are kept as paths,
        // and become package imports only when the user saves the morph.
        var faceTextures = morph.TextureParameters.Where(pair => !IsMorphHairTexture(pair.Key)).ToList();
        foreach (string path in morph.AccessoryMeshes.Concat(faceTextures.Select(pair => pair.Value)))
            ValidateMorphRonAssetPath(path);
        foreach (string path in morph.AccessoryMeshes)
            FindLocalMorphRonAsset(CurrentLoadedExport.FileRef, path, "SkeletalMesh");
        foreach (string path in faceTextures.Select(pair => pair.Value))
            FindLocalMorphRonAsset(CurrentLoadedExport.FileRef, path, "Texture2D");

        StopMorphFaceFx_Click(null, new RoutedEventArgs());
        SuppressMorphEditorChanges = true;
        try
        {
            StoredMorphLods = CloneLods(lods);
            WorkingMorphLods = CloneLods(lods);
            OriginalMorphFeatures = morph.MorphFeatures.Select(pair => new MorphFeatureSnapshot(pair.Key, pair.Value)).ToList();
            MorphFeatureItems.ClearEx();
            foreach (var (name, value) in morph.MorphFeatures)
                MorphFeatureItems.Add(new MorphFeatureEditorItem(name, value, OnMorphFeatureChanged) { HasMorphTarget = MorphTargets.ContainsKey(name) });
            RemovedMorphBones.Clear();
            MorphSkeletonItems.ClearEx();
            foreach (var (name, value) in morph.OffsetBones)
                MorphSkeletonItems.Add(new MorphBoneEditorItem(name, value, OnMorphBoneChanged));
            MorphScalarOverrides.ClearEx();
            foreach (var (name, value) in morph.ScalarParameters)
                MorphScalarOverrides.Add(new MorphScalarOverrideItem(name, value, OnMorphMaterialChanged));
            MorphColorOverrides.ClearEx();
            foreach (var (name, value) in morph.VectorParameters)
                MorphColorOverrides.Add(new MorphColorOverrideItem(name, value, OnMorphMaterialChanged));
            // Keep the textures belonging to the retained hair mesh, including custom hairstyles.
            var hairTextures = MorphTextureOverrides.Where(texture => IsMorphHairTexture(texture.Name)).ToArray();
            MorphTextureOverrides.ClearEx();
            MorphTextureOverrides.AddRange(hairTextures);
            foreach (var (name, path) in faceTextures)
            {
                IEntry local = FindLocalMorphRonAsset(CurrentLoadedExport.FileRef, path, "Texture2D");
                MorphTextureOverrides.Add(new MorphTextureOverrideItem(name, local?.UIndex ?? 0, OnMorphMaterialChanged)
                    { ImportedAssetPath = path, ResolvedPath = path });
            }
            ImportedMorphAccessoryPaths = morph.AccessoryMeshes.ToList();
            MorphTexturePreviewCache.Clear();
            ClearMorphViewportSelection();
            InvalidateMorphRegions();
            RefreshMorphEditorFilters();
        }
        finally
        {
            SuppressMorphEditorChanges = false;
        }
        HasUnsavedMorphChanges = true;
        MorphEditorStatus = "Imported head morph; kept the existing hair mesh. Use Override morph or Make new morph to save to the package.";
        try
        {
            RecalculateMorphFromFeatures();
            ApplyMorphMaterialOverridePreview();
        }
        catch (Exception exception)
        {
            MorphEditorStatus += $" Preview could not be fully refreshed: {exception.Message}";
        }
    }

    private static Vector3[][] GetCompatibleRonLods(HeadMorph morph, int[] expectedCounts)
    {
        List<Vector3>[] source = [morph.Lod0Vertices, morph.Lod1Vertices, morph.Lod2Vertices, morph.Lod3Vertices];
        int count = Array.FindLastIndex(source, lod => lod.Count > 0) + 1;
        if (count == 0) throw new InvalidDataException("The head morph has no vertices. Load a head morph with geometry matching the current base head.");
        for (int i = 0; i < count; i++)
            if (i >= expectedCounts.Length || source[i].Count == 0 || source[i].Count != expectedCounts[i])
                throw new InvalidDataException($"LOD {i} has {source[i].Count} vertices; the loaded base head expects {(i < expectedCounts.Length ? expectedCounts[i] : 0)}. Use a matching base head from the same game and sex.");
        return source.Take(count).Select(lod => lod.ToArray()).ToArray();
    }

    private static bool IsEmptyMorphRonAsset(string path) => string.IsNullOrEmpty(path) || path.Equals("None", StringComparison.OrdinalIgnoreCase);

    private static bool IsMorphHairTexture(string name) => name.StartsWith("HAIR_", StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldKeepMorphMaterialTexture(string importedPath, bool hasTexture) =>
        !IsEmptyMorphRonAsset(importedPath) && !hasTexture;

    private static void ValidateMorphRonAssetPath(string path)
    {
        if (IsEmptyMorphRonAsset(path)) return;
        if (path.Split('.').Any(part => part.Length == 0 || part.Any(character => !char.IsLetterOrDigit(character) && character != '_')))
            throw new InvalidDataException($"Invalid Unreal asset path '{path}'.");
    }

    private static IEntry FindLocalMorphRonAsset(IMEPackage package, string path, string className)
    {
        if (IsEmptyMorphRonAsset(path)) return null;
        IEntry entry = package.FindEntry(path)
            ?? package.Exports.FirstOrDefault(export => export.MemoryFullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (entry is not null && !entry.IsA(className))
            throw new InvalidDataException($"'{path}' is {entry.ClassName}, expected {className}.");
        return entry;
    }

    private int GetMorphRonAssetIndex(IMEPackage package, string path, string className)
    {
        if (IsEmptyMorphRonAsset(path)) return 0;
        ValidateMorphRonAssetPath(path);
        if (FindLocalMorphRonAsset(package, path, className) is { } local) return local.UIndex;
        using var cache = new PackageCache();
        if (ResolveMorphRonAsset(path, className, cache) is { } source)
        {
            // Seek-free NPC packages cannot load arbitrary asset packages on demand. Port resolved
            // assets and their dependencies instead of writing an import that only previews correctly.
            var issues = EntryExporter.ExportExportToPackage(source, package, out IEntry ported, cache);
            if (issues.Count > 0) throw new InvalidDataException($"Could not port '{path}': {issues.Count} dependency error(s).");
            return ported.UIndex;
        }
        return package.GetEntryOrAddImport(path, className, "Engine").UIndex;
    }

    private ExportEntry ResolveMorphRonAsset(string path, string className, PackageCache cache)
    {
        if (IsEmptyMorphRonAsset(path)) return null;
        IEntry entry = FindLocalMorphRonAsset(CurrentLoadedExport.FileRef, path, className);
        if (entry is ExportEntry export) return export;
        if (entry is ImportEntry import && EntryImporter.ResolveImport(import, cache) is { } resolved) return resolved;
        // Resolve through a transient package so importing/previewing does not modify the open package.
        using var previewPackage = MEPackageHandler.CreateMemoryEmptyPackage(CurrentLoadedExport.FileRef.FilePath, CurrentLoadedExport.Game);
        // RON paths name assets that are not necessarily resident with the current NPC. Empty cooked
        // packages default to RequireImportsAlreadyLoaded, which suppresses the named BIOG package lookup.
        previewPackage.setFlags(previewPackage.Flags & ~UnrealFlags.EPackageFlags.RequireImportsAlreadyLoaded);
        var previewImport = (ImportEntry)previewPackage.GetEntryOrAddImport(path, className, "Engine");
        return EntryImporter.ResolveImport(previewImport, cache);
    }

    private void WriteMorphRonAssetReferences(ExportEntry target, PropertyCollection properties)
    {
        if (ImportedMorphAccessoryPaths is not null)
            properties.AddOrReplaceProp(new ArrayProperty<ObjectProperty>(ImportedMorphAccessoryPaths
                .Select(path => new ObjectProperty(GetMorphRonAssetIndex(target.FileRef, path, "SkeletalMesh"))), "m_oOtherMeshes"));
    }
}
