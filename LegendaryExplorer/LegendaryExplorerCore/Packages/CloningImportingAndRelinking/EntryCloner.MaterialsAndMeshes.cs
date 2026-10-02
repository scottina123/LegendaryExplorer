using System;
using System.Collections.Generic;
using System.Linq;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorerCore.Packages.CloningImportingAndRelinking
{
    /// <summary>
    /// Assets used by a mesh's material slots, including local material parents, expressions, and
    /// cube faces. Imported references are reported but remain shared when the mesh is cloned.
    /// </summary>
    public sealed class MeshMaterialCloneInfo
    {
        public int MaterialSlotCount { get; }
        public IReadOnlyList<IEntry> Materials { get; }
        public IReadOnlyList<IEntry> Textures { get; }
        public IReadOnlyList<IEntry> Expressions { get; }
        public IReadOnlyList<ImportEntry> SharedImports { get; }

        internal MeshMaterialCloneInfo(int materialSlotCount, List<IEntry> materials, List<IEntry> textures, List<IEntry> expressions)
        {
            MaterialSlotCount = materialSlotCount;
            Materials = materials.AsReadOnly();
            Textures = textures.AsReadOnly();
            Expressions = expressions.AsReadOnly();
            SharedImports = materials.Concat(textures).Concat(expressions).OfType<ImportEntry>().Distinct().ToList().AsReadOnly();
        }
    }

    public static partial class EntryCloner
    {
        /// <summary>
        /// Discovers unique material assets for every mesh slot, including every static-mesh LOD.
        /// The slot count includes null and imported slots; the asset lists contain valid references.
        /// </summary>
        public static MeshMaterialCloneInfo GetMeshMaterialCloneInfo(ExportEntry mesh)
        {
            int[] slots = GetMeshMaterialSlots(mesh);

            var materials = new List<IEntry>();
            var textures = new List<IEntry>();
            var expressions = new List<IEntry>();
            var seen = new HashSet<IEntry>();
            var pending = new Queue<ExportEntry>();
            foreach (int slot in slots)
            {
                if (mesh.FileRef.GetEntry(slot) is IEntry material && IsCloneMaterial(material))
                {
                    AddReference(slot);
                }
            }

            while (pending.TryDequeue(out ExportEntry asset))
            {
                if (asset.IsA("TextureCube"))
                {
                    var properties = asset.GetProperties();
                    foreach (string faceName in new[] { "FacePosX", "FaceNegX", "FacePosY", "FaceNegY", "FacePosZ", "FaceNegZ" })
                    {
                        if (properties.GetProp<ObjectProperty>(faceName) is ObjectProperty face
                            && asset.FileRef.GetEntry(face.Value) is IEntry faceTexture && faceTexture.IsA("Texture2D"))
                        {
                            AddReference(face.Value);
                        }
                    }
                    continue;
                }
                if (asset.IsA("Texture"))
                {
                    continue;
                }

                // Material parents and expression graphs use object references, including nested
                // expression inputs. Follow only material, expression, and texture assets.
                foreach (int reference in EnumeratePropertyReferences(asset.GetProperties()))
                {
                    AddReference(reference);
                }
                if (GetCloneMaterialBinary(asset) is ObjectBinary materialBinary)
                {
                    foreach (int reference in materialBinary.GetUIndexes(asset.Game))
                    {
                        AddReference(reference);
                    }
                }
            }

            return new MeshMaterialCloneInfo(slots.Length, materials, textures, expressions);

            void AddReference(int uIndex)
            {
                if (uIndex == 0 || mesh.FileRef.GetEntry(uIndex) is not IEntry entry || seen.Contains(entry))
                {
                    return;
                }
                List<IEntry> target;
                if (IsCloneMaterial(entry))
                {
                    target = materials;
                }
                else if (entry.IsA("Texture"))
                {
                    target = textures;
                }
                else if (entry.IsA("MaterialExpression"))
                {
                    target = expressions;
                }
                else
                {
                    return;
                }

                seen.Add(entry);
                target.Add(entry);
                if (entry is ExportEntry export)
                {
                    pending.Enqueue(export);
                }
            }
        }

        /// <summary>
        /// Clones a mesh, optionally copying its local material parents, expression graphs, and
        /// textures. Each original asset is cloned once per mesh; imported assets remain shared.
        /// A suffix renames the mesh and materials with instance index zero. Numeric name suffixes
        /// resolve collisions. Without a suffix, the normal indexed cloning names are retained.
        /// </summary>
        public static ExportEntry CloneMesh(ExportEntry mesh, string nameSuffix = null, bool cloneMaterialsAndTextures = false, bool cloneTree = false)
        {
            nameSuffix = NormalizeCloneSuffix(nameSuffix);
            if (!cloneMaterialsAndTextures && !cloneTree)
            {
                // A mesh-only clone needs no material dependency parsing or binary rewriting.
                GetMeshMaterialSlots(mesh);
                ExportEntry meshOnlyClone = CloneEntry(mesh);
                if (!string.IsNullOrEmpty(nameSuffix))
                {
                    RenameCloneWithZeroIndex(mesh, meshOnlyClone, nameSuffix);
                }
                return meshOnlyClone;
            }

            // Discovery also validates and parses all material dependencies before adding exports.
            MeshMaterialCloneInfo info = cloneMaterialsAndTextures ? GetMeshMaterialCloneInfo(mesh) : null;
            if (info == null)
            {
                GetMeshMaterialSlots(mesh);
            }
            var materialExports = cloneMaterialsAndTextures ? info.Materials.OfType<ExportEntry>().ToList() : new List<ExportEntry>();
            var textureExports = cloneMaterialsAndTextures ? info.Textures.OfType<ExportEntry>().ToList() : new List<ExportEntry>();
            var assets = materialExports.Concat(cloneMaterialsAndTextures ? info.Expressions.OfType<ExportEntry>() : Enumerable.Empty<ExportEntry>())
                .Concat(textureExports).Distinct().ToList();
            var plannedExports = new HashSet<ExportEntry>(assets) { mesh };
            if (cloneTree)
            {
                plannedExports.UnionWith(mesh.FileRef.Tree.FlattenTreeOf(mesh, false).OfType<ExportEntry>());
            }
            HashSet<ExportEntry> externalME1Textures = FindExternalME1TexturesWithClonedParents(plannedExports);

            var objectMap = new ListenableDictionary<IEntry, IEntry>();
            ExportEntry clone = cloneTree ? CloneTree(mesh, objectMap, true) : CloneEntry(mesh, objectMap);
            foreach (ExportEntry asset in assets)
            {
                if (!objectMap.ContainsKey(asset))
                {
                    CloneEntry(asset, objectMap);
                }
            }
            // All parents are mapped before assigning outers, so discovery order cannot affect
            // whether expressions or cube faces are placed under their cloned owner.
            foreach (ExportEntry asset in assets)
            {
                if (asset.Parent != null && objectMap.TryGetValue(asset.Parent, out IEntry clonedParent))
                {
                    ((ExportEntry)objectMap[asset]).idxLink = clonedParent.UIndex;
                }
            }

            if (!string.IsNullOrEmpty(nameSuffix))
            {
                RenameCloneWithZeroIndex(mesh, clone, nameSuffix);
                foreach (ExportEntry material in materialExports)
                {
                    RenameCloneWithZeroIndex(material, (ExportEntry)objectMap[material], nameSuffix);
                }
                foreach (ExportEntry texture in textureExports)
                {
                    var textureClone = (ExportEntry)objectMap[texture];
                    var name = new NameReference(texture.ObjectName.Name + nameSuffix);
                    while (CloneNameExists(textureClone, name))
                    {
                        name = new NameReference(name.Name, name.Number + 1);
                    }
                    textureClone.ObjectName = name;
                }
            }

            PreserveME1ExternalTextureParents(externalME1Textures, objectMap);
            RelinkClonedEntries(mesh.FileRef, objectMap);
            foreach (ExportEntry material in materialExports)
            {
                // ObjectBinary.From recognizes standard material classes. Parse the inherited
                // format explicitly too, so BioMaterialInstanceConstant and other subclasses'
                // static-permutation texture references receive the same mapping.
                if (GetCloneMaterialBinary(material) is ObjectBinary binary)
                {
                    binary.ForEachUIndex(material.Game, new CloneAssetReferenceAction(material.FileRef, objectMap));
                    ((ExportEntry)objectMap[material]).WriteBinary(binary);
                }
            }
            return clone;
        }

        private static int[] GetMeshMaterialSlots(ExportEntry mesh)
        {
            ArgumentNullException.ThrowIfNull(mesh);
            return ObjectBinary.From(mesh) switch
            {
                SkeletalMesh skeletal => skeletal.Materials,
                StaticMesh staticMesh => staticMesh.LODModels.SelectMany(lod => lod.Elements).Select(element => element.Material).ToArray(),
                _ => throw new ArgumentException("The export must be a supported SkeletalMesh or StaticMesh.", nameof(mesh))
            };
        }

        private static ObjectBinary GetCloneMaterialBinary(ExportEntry material)
        {
            if (material.IsDefaultObject)
            {
                return null;
            }
            if (material.IsA("Material"))
            {
                return ObjectBinary.From<Material>(material);
            }
            if (material.IsA("MaterialInstance") && material.GetProperty<BoolProperty>("bHasStaticPermutationResource")?.Value == true)
            {
                return ObjectBinary.From<MaterialInstance>(material);
            }
            return null;
        }

        private static bool IsCloneMaterial(IEntry entry) => entry.IsA("MaterialInterface") || entry.IsA("RvrEffectsMaterialUser");

        private static IEnumerable<int> EnumeratePropertyReferences(IEnumerable<Property> properties)
        {
            foreach (Property property in properties)
            {
                switch (property)
                {
                    case ObjectProperty objectProperty:
                        yield return objectProperty.Value;
                        break;
                    case StructProperty structProperty:
                        foreach (int reference in EnumeratePropertyReferences(structProperty.Properties))
                            yield return reference;
                        break;
                    case ArrayProperty<ObjectProperty> objectArray:
                        foreach (ObjectProperty item in objectArray)
                            yield return item.Value;
                        break;
                    case ArrayProperty<StructProperty> structArray:
                        foreach (StructProperty item in structArray)
                            foreach (int reference in EnumeratePropertyReferences(item.Properties))
                                yield return reference;
                        break;
                }
            }
        }

        private static string NormalizeCloneSuffix(string suffix)
        {
            suffix = suffix?.Trim();
            return string.IsNullOrEmpty(suffix) || suffix.StartsWith('_') ? suffix : '_' + suffix;
        }

        private static void RenameCloneWithZeroIndex(ExportEntry source, ExportEntry clone, string suffix)
        {
            string baseName = source.ObjectName.Name + suffix;
            var name = new NameReference(baseName, 0);
            int number = 2;
            while (CloneNameExists(clone, name))
            {
                name = new NameReference($"{baseName}_{number++}", 0);
            }
            clone.ObjectName = name;
        }

        private static bool CloneNameExists(ExportEntry clone, NameReference name)
        {
            string parentPath = clone.ParentInstancedFullPath;
            string path = string.IsNullOrEmpty(parentPath) ? name.Instanced : $"{parentPath}.{name.Instanced}";
            return clone.FileRef.Exports.Concat<IEntry>(clone.FileRef.Imports).Any(entry => entry != clone
                && string.Equals(entry.InstancedFullPath, path, StringComparison.OrdinalIgnoreCase));
        }

        private static HashSet<ExportEntry> FindExternalME1TexturesWithClonedParents(HashSet<ExportEntry> exports)
        {
            var textures = new HashSet<ExportEntry>();
            foreach (ExportEntry texture in exports.Where(export => export.Game == MEGame.ME1 && export.IsA("Texture2D")))
            {
                IEntry topParent = texture.Parent;
                while (topParent?.Parent != null)
                {
                    topParent = topParent.Parent;
                }
                if (topParent is ExportEntry topExport && exports.Contains(topExport)
                    && ObjectBinary.From<UTexture2D>(texture).Mips.Any(mip => !mip.IsLocallyStored))
                {
                    textures.Add(texture);
                }
            }
            return textures;
        }

        private static void PreserveME1ExternalTextureParents(HashSet<ExportEntry> textures, ListenableDictionary<IEntry, IEntry> objectMap)
        {
            foreach (ExportEntry texture in textures)
            {
                var clone = (ExportEntry)objectMap[texture];
                if (!string.Equals(texture.ParentInstancedFullPath.Split('.')[0], clone.ParentInstancedFullPath.Split('.')[0], StringComparison.OrdinalIgnoreCase))
                {
                    // ME1's external mip archive is selected by the top-level parent package.
                    clone.idxLink = texture.idxLink;
                    while (CloneNameExists(clone, clone.ObjectName))
                    {
                        clone.ObjectName = new NameReference(clone.ObjectName.Name, clone.ObjectName.Number + 1);
                    }
                }
            }
        }

        private readonly struct CloneAssetReferenceAction(IMEPackage package, IDictionary<IEntry, IEntry> map) : IUIndexAction
        {
            public void Invoke(ref int uIndex, string propName)
            {
                if (uIndex != 0 && package.GetEntry(uIndex) is IEntry source && map.TryGetValue(source, out IEntry clone))
                {
                    uIndex = clone.UIndex;
                }
            }
        }
    }
}
