using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorerCore.Unreal.Classes
{
    public class BioMorphFace
    {
        public ExportEntry Export { get; }
        public BinaryConverters.BioMorphFace MorphFace { get; }
        public SkeletalMesh BaseHead { get; }
        public SkeletalMesh HairMesh { get; }
        public List<MorphFeature> MorphFeatures { get; } = [];
        public List<BonePosition> BoneOffsets { get; } = [];

        public IEntry m_oBaseHead;
        public IEntry m_oHairMesh;

        public bool IsExportable => m_oBaseHead != null;
        public BioMorphFace(ExportEntry morphExp)
        {
            Export = morphExp;
            MorphFace = ObjectBinary.From<BinaryConverters.BioMorphFace>(morphExp);
            ParseProperties(Export.GetProperties());

            BaseHead = LoadMeshFromEntry(m_oBaseHead);
            HairMesh = LoadMeshFromEntry(m_oHairMesh);
        }

        public static (BonePosition[], Vector3[][]) GetBoneAndVertexPositions(ExportEntry morphExport)
        {
            var boneOffsets = morphExport.GetProperty<ArrayProperty<StructProperty>>("m_aFinalSkeleton")?.Select(e => new BonePosition(e)).ToArray();
            var vertexOffsets = ObjectBinary.From<BinaryConverters.BioMorphFace>(morphExport)?.LODs;
            return (boneOffsets, vertexOffsets);
        }

        /// <summary>
        /// Builds the edited local-space skeleton represented by m_aFinalSkeleton while retaining the
        /// base-head bind pose for bones that are not present in the morph.
        /// </summary>
        public static MeshBone[] CreateFinalSkeleton(MeshBone[] bindSkeleton, IEnumerable<BonePosition> finalBonePositions)
        {
            MeshBone[] editedSkeleton = bindSkeleton?.Select(bone => new MeshBone
            {
                Name = bone.Name,
                Flags = bone.Flags,
                Orientation = bone.Orientation,
                Position = bone.Position,
                NumChildren = bone.NumChildren,
                ParentIndex = bone.ParentIndex,
                BoneColor = bone.BoneColor
            }).ToArray() ?? [];
            if (finalBonePositions is null)
            {
                return editedSkeleton;
            }

            Dictionary<string, Vector3> editedPositions = finalBonePositions
                .GroupBy(position => position.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Position, StringComparer.OrdinalIgnoreCase);
            foreach (MeshBone bone in editedSkeleton)
            {
                if (editedPositions.TryGetValue(bone.Name.Instanced, out Vector3 position))
                {
                    bone.Position = position;
                }
            }
            return editedSkeleton;
        }

        /// <summary>
        /// Computes the same inverse-bind-to-final-skeleton matrices used by the morph editor preview.
        /// </summary>
        public static Matrix4x4[] ComputePreviewSkinningMatrices(MeshBone[] bindSkeleton, MeshBone[] editedSkeleton)
        {
            int boneCount = Math.Min(bindSkeleton?.Length ?? 0, editedSkeleton?.Length ?? 0);
            if (boneCount == 0)
            {
                return [];
            }

            var bindComponentSpace = new Matrix4x4[boneCount];
            var editedComponentSpace = new Matrix4x4[boneCount];
            var skinningMatrices = new Matrix4x4[boneCount];
            for (int boneIndex = 0; boneIndex < boneCount; boneIndex++)
            {
                MeshBone bindBone = bindSkeleton[boneIndex];
                MeshBone editedBone = editedSkeleton[boneIndex];
                Matrix4x4 bindLocal = Matrix4x4.CreateFromQuaternion(bindBone.Orientation)
                                      * Matrix4x4.CreateTranslation(bindBone.Position);
                Matrix4x4 editedLocal = Matrix4x4.CreateFromQuaternion(editedBone.Orientation)
                                        * Matrix4x4.CreateTranslation(editedBone.Position);
                bindComponentSpace[boneIndex] = bindBone.ParentIndex >= 0 && bindBone.ParentIndex < boneIndex
                    ? bindLocal * bindComponentSpace[bindBone.ParentIndex]
                    : bindLocal;
                editedComponentSpace[boneIndex] = editedBone.ParentIndex >= 0 && editedBone.ParentIndex < boneIndex
                    ? editedLocal * editedComponentSpace[editedBone.ParentIndex]
                    : editedLocal;
                skinningMatrices[boneIndex] = Matrix4x4.Invert(bindComponentSpace[boneIndex], out Matrix4x4 inverseBind)
                    ? inverseBind * editedComponentSpace[boneIndex]
                    : Matrix4x4.Identity;
            }
            return skinningMatrices;
        }

        /// <summary>
        /// Applies the morph editor's normalized four-bone matrix blend to one stored morph vertex.
        /// </summary>
        public static Vector3 SkinPreviewPosition(Vector3 position, Matrix4x4[] skinningMatrices,
            int bone0, float weight0, int bone1, float weight1,
            int bone2, float weight2, int bone3, float weight3)
        {
            if (skinningMatrices is not { Length: > 0 })
            {
                return position;
            }

            Matrix4x4 blended = GetPreviewSkinningMatrix(skinningMatrices, bone0) * weight0;
            if (weight1 > 0) blended += GetPreviewSkinningMatrix(skinningMatrices, bone1) * weight1;
            if (weight2 > 0) blended += GetPreviewSkinningMatrix(skinningMatrices, bone2) * weight2;
            if (weight3 > 0) blended += GetPreviewSkinningMatrix(skinningMatrices, bone3) * weight3;
            return Vector3.Transform(position, blended);
        }

        private static Matrix4x4 GetPreviewSkinningMatrix(Matrix4x4[] matrices, int boneIndex) =>
            boneIndex >= 0 && boneIndex < matrices.Length ? matrices[boneIndex] : Matrix4x4.Identity;

        private void ParseProperties(PropertyCollection props)
        {
            var headProp = props.GetProp<ObjectProperty>("m_oBaseHead");
            m_oBaseHead = headProp?.ResolveToEntry(Export.FileRef);
            var hairProp = props.GetProp<ObjectProperty>("m_oHairMesh");
            m_oHairMesh = hairProp?.ResolveToEntry(Export.FileRef);

            MorphFeatures.AddRange(props.GetProp<ArrayProperty<StructProperty>>("m_aMorphFeatures").Select(e => new MorphFeature(e)));
            BoneOffsets.AddRange(props.GetProp<ArrayProperty<StructProperty>>("m_aFinalSkeleton").Select(e => new BonePosition(e)));
        }

        private SkeletalMesh LoadMeshFromEntry(IEntry mOBaseHead)
        {
            if (mOBaseHead is null) return null;
            if (mOBaseHead.ClassName != "SkeletalMesh") throw new ArgumentException("Entry is not SkeletalMesh!");
            if (mOBaseHead is ExportEntry exp)
            {
                return ObjectBinary.From<SkeletalMesh>(exp);
            }
            else if (mOBaseHead is ImportEntry imp)
            {
                var resolveExp = EntryImporter.ResolveImport(imp, null);
                return ObjectBinary.From<SkeletalMesh>(resolveExp);
            }
            return null;
        }

        /// <summary>
        /// Applies the vertexes from the MorphFace onto the BaseHead SkeletalMesh
        /// </summary>
        /// <returns>The applied head skeletal mesh</returns>
        public SkeletalMesh Apply()
        {
            // apply vertices morph first
            // in skeletalMesh, we load only LOD0, so we only apply for lod0
            for (int lod = 0; lod < 1; lod++)
            {
                for (int v=0; v < BaseHead.LODModels[lod].VertexBufferGPUSkin.VertexData.Length; v++)
                {
                    var vertex = BaseHead.LODModels[lod].VertexBufferGPUSkin.VertexData[v];
                    vertex.Position.X = MorphFace.LODs[lod][v].X;
                    vertex.Position.Y = MorphFace.LODs[lod][v].Y;
                    vertex.Position.Z = MorphFace.LODs[lod][v].Z;
                    BaseHead.LODModels[lod].VertexBufferGPUSkin.VertexData[v] = vertex;
                }
            }

            // return mesh
            return BaseHead;
        }
    }

    public readonly struct MorphFeature
    {
        public string Name { get; }
        public float Offset { get; }

        public MorphFeature(StructProperty featureStruct)
        {
            Name = featureStruct.GetProp<NameProperty>("sFeatureName")?.Value ?? "";
            Offset = featureStruct.GetProp<FloatProperty>("Offset")?.Value ?? 0f;
        }
    }

    public readonly struct BonePosition
    {
        public string Name { get; }
        public Vector3 Position { get; }

        public BonePosition(StructProperty boneOffsetStruct)
        {
            Name = boneOffsetStruct.GetProp<NameProperty>("nName")?.Value ?? "";
            var vectorStruct = boneOffsetStruct.GetProp<StructProperty>("vPos");
            Position = CommonStructs.GetVector3(vectorStruct);
        }
    }
}
