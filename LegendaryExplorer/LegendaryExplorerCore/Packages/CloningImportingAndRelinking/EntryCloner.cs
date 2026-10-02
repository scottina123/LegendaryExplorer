using System.Collections.Generic;
using System.Linq;
using System;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorerCore.Packages.CloningImportingAndRelinking
{
    public static partial class EntryCloner
    {
        public static T CloneTree<T>(T entry, bool incrementIndex = true) where T : IEntry
        {
            var objectMap = new ListenableDictionary<IEntry, IEntry>();
            T newRoot = CloneTree(entry, objectMap, incrementIndex);
            RelinkClonedEntries(entry.FileRef, objectMap);
            return newRoot;
        }

        private static T CloneTree<T>(T entry, ListenableDictionary<IEntry, IEntry> objectMap, bool incrementIndex) where T : IEntry
        {
            T newRoot = CloneEntry(entry, objectMap, incrementIndex);
            EntryTree tree = entry.FileRef.Tree;
            var stack = new Stack<(IEntry, IEntry)>();
            stack.Push((entry, newRoot));
            while (stack.TryPop(out var pair))
            {
                (IEntry originalRootNode, IEntry newRootNode) = pair;
                foreach (IEntry node in tree.GetDirectChildrenOf(originalRootNode.UIndex))
                {
                    IEntry newEntry = CloneEntry(node, objectMap, false, newRootNode.UIndex);
                    stack.Push((node, newEntry));
                }
            }
            return newRoot;
        }

        private static void RelinkClonedEntries(IMEPackage package, ListenableDictionary<IEntry, IEntry> objectMap)
        {
            var relinkerOptions = new RelinkerOptionsPackage { CrossPackageMap = objectMap };
            // Dependencies outside the cloned tree stay shared. Relinking their data in place
            // would redirect the originals' references to cloned objects as well.
            relinkerOptions.RelinkMapEntriesToSkip.UnionWith(package.Exports.Where(export => !objectMap.ContainsKey(export)));
            Relinker.RelinkAll(relinkerOptions);
        }

        /// <summary>
        /// Gets the distinct textures referenced by a material instance's own texture parameters.
        /// Imported textures are included so callers can explain that they will remain shared.
        /// Parent material textures, null values, and invalid references are not included.
        /// </summary>
        public static IReadOnlyList<IEntry> GetMaterialInstanceTextureReferences(ExportEntry material)
        {
            ArgumentNullException.ThrowIfNull(material);
            if (!material.IsA("MaterialInstanceConstant"))
            {
                throw new ArgumentException("The export must be a MaterialInstanceConstant.", nameof(material));
            }

            var textures = new List<IEntry>();
            var seen = new HashSet<int>();
            var parameters = material.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues");
            if (parameters != null)
            {
                foreach (StructProperty parameter in parameters)
                {
                    var value = parameter.GetProp<ObjectProperty>("ParameterValue");
                    if (value != null && value.Value != 0 && seen.Add(value.Value)
                        && material.FileRef.GetEntry(value.Value) is IEntry texture && texture.IsA("Texture"))
                    {
                        textures.Add(texture);
                    }
                }
            }
            return textures;
        }

        /// <summary>
        /// Clones a material instance and its locally exported texture parameter values. Repeated
        /// references reuse one texture clone, including local TextureCube faces. Imported textures
        /// and parent materials remain shared.
        /// Only cloned exports are relinked, including their property and binary texture references.
        /// </summary>
        /// <param name="material">The MaterialInstanceConstant to clone.</param>
        /// <param name="textureSuffix">Optional suffix for the MIC and textures, with or without its leading underscore.
        /// A renamed MIC receives instance index zero. Without a suffix, names use available instance indices.</param>
        /// <param name="cloneTree">Whether to also clone the material instance's descendants.</param>
        public static ExportEntry CloneMaterialInstanceWithTextures(ExportEntry material, string textureSuffix = null, bool cloneTree = false)
        {
            return CloneMaterialInstance(material, textureSuffix, cloneTextures: true, cloneTree: cloneTree);
        }

        /// <summary>
        /// Clones a material instance, optionally cloning its texture parameters and descendants.
        /// A suffix renames the MIC and any cloned parameter textures. The renamed MIC uses instance
        /// index zero; name collisions add a numeric suffix to the base name instead of changing its index.
        /// </summary>
        public static ExportEntry CloneMaterialInstance(ExportEntry material, string nameSuffix = null, bool cloneTextures = false, bool cloneTree = false)
        {
            // Read and validate the source before making any changes to its package.
            var references = GetMaterialInstanceTextureReferences(material);
            var textures = cloneTextures ? references.OfType<ExportEntry>().ToList() : new List<ExportEntry>();
            var seen = new HashSet<ExportEntry>(textures);
            foreach (ExportEntry cube in textures.Where(texture => texture.IsA("TextureCube")).ToList())
            {
                var properties = cube.GetProperties();
                foreach (string faceName in new[] { "FacePosX", "FaceNegX", "FacePosY", "FaceNegY", "FacePosZ", "FaceNegZ" })
                {
                    var faceValue = properties.GetProp<ObjectProperty>(faceName);
                    if (faceValue != null && material.FileRef.GetEntry(faceValue.Value) is ExportEntry face
                        && face.IsA("Texture2D") && seen.Add(face))
                    {
                        textures.Add(face);
                    }
                }
            }

            var externallyStoredME1Textures = new HashSet<ExportEntry>();
            if (material.Game == MEGame.ME1)
            {
                IEnumerable<ExportEntry> candidates = textures;
                if (cloneTree)
                {
                    candidates = candidates.Concat(material.FileRef.Tree.FlattenTreeOf(material, false).OfType<ExportEntry>());
                }
                foreach (ExportEntry texture in candidates.Distinct().Where(texture => texture.IsA("Texture2D")))
                {
                    IEntry topParent = texture.Parent;
                    while (topParent?.Parent != null)
                    {
                        topParent = topParent.Parent;
                    }
                    if ((topParent == material || topParent is ExportEntry topTexture && seen.Contains(topTexture))
                        && ObjectBinary.From<UTexture2D>(texture).Mips.Any(mip => !mip.IsLocallyStored))
                    {
                        externallyStoredME1Textures.Add(texture);
                    }
                }
            }

            nameSuffix = nameSuffix?.Trim();
            if (!string.IsNullOrEmpty(nameSuffix) && !nameSuffix.StartsWith('_'))
            {
                nameSuffix = '_' + nameSuffix;
            }

            NameReference? materialCloneName = null;
            if (!string.IsNullOrEmpty(nameSuffix))
            {
                string baseName = material.ObjectName.Name + nameSuffix;
                string availableName = baseName;
                string parentPath = material.ParentInstancedFullPath;
                int number = 2;
                while (material.FileRef.FindEntry(string.IsNullOrEmpty(parentPath) ? availableName : $"{parentPath}.{availableName}") != null)
                {
                    availableName = $"{baseName}_{number++}";
                }
                materialCloneName = new NameReference(availableName, 0);
            }

            var objectMap = new ListenableDictionary<IEntry, IEntry>();
            ExportEntry clone = cloneTree ? CloneTree(material, objectMap, true) : CloneEntry(material, objectMap);
            if (materialCloneName is NameReference renamedMaterial)
            {
                clone.ObjectName = renamedMaterial;
            }
            foreach (ExportEntry texture in textures)
            {
                if (!objectMap.ContainsKey(texture))
                {
                    CloneEntry(texture, objectMap);
                }
            }

            foreach (ExportEntry texture in textures)
            {
                var textureClone = (ExportEntry)objectMap[texture];
                if (texture.Parent != null && objectMap.TryGetValue(texture.Parent, out IEntry clonedParent))
                {
                    textureClone.idxLink = clonedParent.UIndex;
                }

                if (!string.IsNullOrEmpty(nameSuffix))
                {
                    var name = new NameReference(texture.ObjectName.Name + nameSuffix);
                    string parentPath = textureClone.ParentInstancedFullPath;
                    while (material.FileRef.FindEntry(string.IsNullOrEmpty(parentPath) ? name.Instanced : $"{parentPath}.{name.Instanced}") != null)
                    {
                        name = new NameReference(name.Name, name.Number + 1);
                    }
                    textureClone.ObjectName = name;
                }
            }

            if (material.Game == MEGame.ME1)
            {
                foreach (ExportEntry texture in externallyStoredME1Textures)
                {
                    var textureClone = (ExportEntry)objectMap[texture];
                    if (!string.Equals(texture.ParentInstancedFullPath.Split('.')[0], textureClone.ParentInstancedFullPath.Split('.')[0], StringComparison.OrdinalIgnoreCase))
                    {
                        // ME1 locates external mip data using the top-level parent package name.
                        // Keep that parent path stable even when the MIC tree relocates this texture.
                        textureClone.idxLink = texture.idxLink;
                        while (material.FileRef.Exports.Concat<IEntry>(material.FileRef.Imports).Any(entry => entry != textureClone
                                   && string.Equals(entry.InstancedFullPath, textureClone.InstancedFullPath, StringComparison.OrdinalIgnoreCase)))
                        {
                            textureClone.ObjectName = new NameReference(textureClone.ObjectName.Name, textureClone.ObjectName.Number + 1);
                        }
                    }
                }
            }

            // Texture clones keep their original TFC name and external mip offsets. The package
            // serializer updates offsets for package-stored mips when the package is saved.
            RelinkClonedEntries(material.FileRef, objectMap);
            return clone;
        }
        
        public static T CloneEntry<T>(T entry, IDictionary<IEntry, IEntry> objectMap = null, bool incrementIndex = true, int newParentUIndex = int.MaxValue) where T : IEntry
        {
            bool shouldIncrement = incrementIndex && entry is ExportEntry; // Why is this only for exports?
            IEntry newEntry = entry.Clone(shouldIncrement, newParentUIndex);

            switch (newEntry)
            {
                case ExportEntry export:
                    entry.FileRef.AddExport(export);
                    if (objectMap != null)
                    {
                        objectMap[entry] = export;
                    }
                    break;
                case ImportEntry import:
                    entry.FileRef.AddImport(import);
                    //Imports are not relinked when cloning
                    break;
            }

            return (T)newEntry;
        }
    }
}
