using LegendaryExplorerCore.Gammtek.Extensions;
using LegendaryExplorerCore.Gammtek.Extensions.Collections.Generic;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Classes;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Filters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using IsImage = SixLabors.ImageSharp.Image;

namespace LegendaryExplorer.Tools.PackageEditor.Experiments
{
    public class SquidGltf
    {
        private const float ScaleFactor = 100;
        const float weightUnpackScale = 1f / 255;

        #region export
        public static void ConvertSkeletalMeshToGltf(SkeletalMesh mesh, string filePath, string versionInfo = null)
        {
            var intermediateMesh = ToIntermediateMesh(mesh);
            var gltf = ToGltf(intermediateMesh, versionInfo);
            // allow saving as glTF (human readable json, outputs a bin file and textures next to it)
            // or a glb, which bundles all of that stuff together into a single file. more space efficient and transportable
            if (".glb".CaseInsensitiveEquals(Path.GetExtension(filePath)))
            {
                gltf.SaveGLB(filePath);
            }
            else
            {
                gltf.SaveGLTF(filePath);
            }
        }

        public static void ConvertStaticMeshToGltf(StaticMesh mesh, string filePath, string versionInfo = null)
        {
            var intermediateMesh = ToIntermediateMesh(mesh);
            var gltf = ToGltf(intermediateMesh, versionInfo);
            // allow saving as glTF (human readable json, outputs a bin file and textures next to it)
            // or a glb, which bundles all of that stuff together into a single file. more space efficient and transportable
            if (".glb".CaseInsensitiveEquals(Path.GetExtension(filePath)))
            {
                gltf.SaveGLB(filePath);
            }
            else
            {
                gltf.SaveGLTF(filePath);
            }
        }

        private static IntermediateMesh ToIntermediateMesh(StaticMesh mesh)
        {
            var intermediateMesh = new IntermediateMesh()
            {
                Name = mesh.Export.ObjectName.Instanced
            };

            // materials
            List<int> materialMap = [];
            foreach (var lod in mesh.LODModels)
            {
                foreach (var element in lod.Elements)
                {
                    var matUIndex = element.Material;
                    var matMapIndex = materialMap.IndexOf(matUIndex);
                    // The first time we encounter any material (by UIndex) add it to the mapping list
                    if (matMapIndex == -1)
                    {
                        materialMap.Add(matUIndex);
                    }
                }
            }
            foreach (var mat in materialMap)
            {
                var intermediateMat = ToIntermediateMaterial(mesh.Export.FileRef.GetEntry(mat));
                intermediateMesh.Materials.Add(intermediateMat);
            }

            // LODs
            for (int i = 0; i < mesh.LODModels.Length; i++)
            {
                intermediateMesh.LODs.Add(ToIntermediateLod(mesh.LODModels[i], i, materialMap));
            }

            // Collision mesh
            var collisionMeshGeometry = mesh.GetCollisionMeshProperty(mesh.Export.FileRef);

            if (collisionMeshGeometry != null)
            {
                intermediateMesh.CollisionMeshElements = [];
                if (collisionMeshGeometry?.GetProp<ArrayProperty<StructProperty>>("ConvexElems") is ArrayProperty<StructProperty> convexElems)
                {
                    foreach (StructProperty convexElem in convexElems)
                    {
                        intermediateMesh.CollisionMeshElements.Add(ToIntermediateCollision(convexElem));
                    }
                }
            }
            return intermediateMesh;
        }

        private static IntermediateCollisionElement ToIntermediateCollision(StructProperty convexElem)
        {
            var intermediateCollision = new IntermediateCollisionElement();

            var faceTriData = convexElem.GetProp<ArrayProperty<IntProperty>>("FaceTriData");
            for (int i = 0; i < faceTriData.Count; i += 3)
            {
                intermediateCollision.Triangles.Add(new IntermediateTriangle()
                {
                    VertIndex1 = faceTriData[i].Value,
                    VertIndex2 = faceTriData[i + 1].Value,
                    VertIndex3 = faceTriData[i + 2].Value
                });
            }

            var vertexData = convexElem.GetProp<ArrayProperty<StructProperty>>("VertexData");
            foreach (StructProperty vertex in vertexData)
            {
                float x = vertex.GetProp<FloatProperty>("X").Value;
                float y = vertex.GetProp<FloatProperty>("Y").Value;
                float z = vertex.GetProp<FloatProperty>("Z").Value;
                intermediateCollision.Vertices.Add(new Vector3(x, z, y));
            }

            return intermediateCollision;
        }

        private static IntermediateLOD ToIntermediateLod(StaticMeshRenderData lod, int index, IEnumerable<int> materialMapping)
        {
            var intermediateLod = new IntermediateLOD()
            {
                Index = index
            };

            // shared vertices across all sections
            List<IntermediateVertex> vertices = [];
            for (int i = 0; i < lod.VertexBuffer.VertexData.Length; i++)
            {
                var vert = lod.VertexBuffer.VertexData[i];
                List<Vector2> uvs = [];
                if (lod.VertexBuffer.bUseFullPrecisionUVs)
                {
                    uvs.AddRange(vert.FullPrecisionUVs);
                }
                else
                {
                    uvs.AddRange(vert.HalfPrecisionUVs.Select(x => (Vector2)x));
                }

                var intermediateVert = new IntermediateVertex()
                {
                    Index = i,
                    OriginalIndex = i,
                    Position = lod.PositionVertexBuffer.VertexData[i],
                    Normal = (Vector3)vert.TangentZ,
                    Tangent = (Vector3)vert.TangentX,
                    BiTangentDirection = vert.TangentZ.W / 127.5f - 1,
                    UVs = uvs,
                };
                vertices.Add(intermediateVert);
            }

            foreach (var element in lod.Elements)
            {
                var intermediateSection = new IntermediateMeshSection
                {
                    Vertices = vertices,
                    MaterialIndex = materialMapping.IndexOf(element.Material)
                };

                // TODO other code comments indicate that sometimes the index buffer is not present and we need to look at the kdops data for the triangles
                for (int i = (int)element.FirstIndex; i < element.FirstIndex + element.NumTriangles * 3; i += 3)
                {
                    intermediateSection.Triangles.Add(new IntermediateTriangle()
                    {
                        VertIndex1 = lod.IndexBuffer[i],
                        VertIndex2 = lod.IndexBuffer[i + 1],
                        VertIndex3 = lod.IndexBuffer[i + 2],
                    });
                }

                intermediateLod.Sections.Add(intermediateSection);
            }

            return intermediateLod;
        }

        private static IntermediateMaterial ToIntermediateMaterial(IEntry material)
        {
            var intermediateMat = new IntermediateMaterial();
            if (material == null)
            {
                intermediateMat.Name = "null";
            }
            else
            {
                intermediateMat.Name = material.MemoryFullPath;
                if (material is ImportEntry imp)
                {
                    material = EntryImporter.ResolveImport(imp, new PackageCache());
                }
                FindBestDiffAndNormForMaterial(intermediateMat, material as ExportEntry);
            }
            return intermediateMat;
        }

        private static IntermediateMesh ToIntermediateMesh(SkeletalMesh mesh)
        {
            var intermediateMesh = new IntermediateMesh()
            {
                Name = mesh.Export.ObjectName.Instanced
            };

            // materials
            foreach (var mat in mesh.Materials)
            {
                var intermediateMat = ToIntermediateMaterial(mesh.Export.FileRef.GetEntry(mat));
                intermediateMesh.Materials.Add(intermediateMat);
            }

            // skeleton
            intermediateMesh.Skeleton = [];
            for (int i = 0; i < mesh.RefSkeleton.Length; i++)
            {
                var bone = mesh.RefSkeleton[i];
                intermediateMesh.Skeleton.Add(new IntermediateBone()
                {
                    Index = i,
                    Name = bone.Name.Instanced,
                    ParentIndex = bone.ParentIndex,
                    NumChildren = bone.NumChildren,
                    Position = bone.Position,
                    Rotation = bone.Orientation
                });
            }

            // Sockets
            var socketProp = mesh.Export.GetProperty<ArrayProperty<ObjectProperty>>("Sockets");
            if (socketProp != null)
            {
                foreach (var socket in socketProp)
                {
                    var socketObject = socket.ResolveToExport(mesh.Export.FileRef, new PackageCache());
                    var intermediateSocket = new IntermediateSocket()
                    {
                        Name = socketObject.GetProperty<NameProperty>("SocketName").Value.Instanced,
                        Bone = socketObject.GetProperty<NameProperty>("BoneName").Value.Instanced,
                        RelativeLocation = Vector3.Zero,
                        RelativeRotation = Quaternion.Identity,
                        RelativeScale = Vector3.One
                    };
                    var locationProp = socketObject.GetProperty<StructProperty>("RelativeLocation");
                    if (locationProp != null)
                    {
                        intermediateSocket.RelativeLocation = new Vector3(locationProp.GetProp<FloatProperty>("X"), locationProp.GetProp<FloatProperty>("Y"), locationProp.GetProp<FloatProperty>("Z"));
                    }
                    var rotationProp = socketObject.GetProperty<StructProperty>("RelativeRotation");
                    if (rotationProp != null)
                    {
                        static Quaternion FromYawPitchRoll(int yaw, int pitch, int roll)
                        {
                            var rot = Quaternion.Identity;
                            var yawRad = (yaw % 65536) / 65536f * Math.PI * 2;
                            var pitchRad = (pitch % 65536) / 65536f * Math.PI * 2;
                            var rollRad = (roll % 65536) / 65536f * Math.PI * 2;
                            // apply yaw
                            rot = rot * new Quaternion(0, (float)Math.Sin(yawRad / 2), 0, -(float)Math.Cos(yawRad / 2));
                            // apply pitch
                            rot = rot * new Quaternion(0, 0, (float)Math.Sin(pitchRad / 2), (float)Math.Cos(pitchRad / 2));
                            // apply roll
                            rot = rot * new Quaternion((float)Math.Sin(rollRad / 2), 0, 0, (float)Math.Cos(rollRad / 2));
                            return Quaternion.Normalize(rot);
                        }
                        intermediateSocket.RelativeRotation = FromYawPitchRoll(
                            rotationProp.GetProp<IntProperty>("Yaw").Value,
                            rotationProp.GetProp<IntProperty>("Pitch").Value,
                            rotationProp.GetProp<IntProperty>("Roll").Value);
                    }
                    var scaleProp = socketObject.GetProperty<StructProperty>("RelativeScale");
                    if (scaleProp != null)
                    {
                        intermediateSocket.RelativeScale = new Vector3(scaleProp.GetProp<FloatProperty>("X"), scaleProp.GetProp<FloatProperty>("Y"), scaleProp.GetProp<FloatProperty>("Z"));
                    }
                    intermediateMesh.Sockets.Add(intermediateSocket);
                }
            }

            // LODs
            for (int i = 0; i < mesh.LODModels.Length; i++)
            {
                // some vanilla meshes switch around the material order in lower LODs. I don't know why, but we need to account for it in the export process
                int[] materialMapping = [.. Enumerable.Range(0, mesh.Materials.Length)];
                if (mesh.Export != null)
                {
                    var LODInfo = mesh.Export.GetProperty<ArrayProperty<StructProperty>>("LODInfo");
                    if (LODInfo != null && LODInfo.Count > i)
                    {
                        var matMap = LODInfo[i].GetProp<ArrayProperty<IntProperty>>("LODMaterialMap");
                        if (matMap != null && matMap.Count > 0)
                        {
                            materialMapping = [.. matMap.Select(x => x.Value)];
                        }
                    }
                }
                intermediateMesh.LODs.Add(ToIntermediateLod(mesh.LODModels[i], i, materialMapping));
            }
            return intermediateMesh;
        }

        private static IntermediateLOD ToIntermediateLod(StaticLODModel lod, int index, int[] materialMapping)
        {
            var intermediateLod = new IntermediateLOD()
            {
                Index = index
            };

            List<IntermediateVertex> vertices = [];

            for (int i = 0; i < lod.VertexBufferGPUSkin.VertexData.Length; i++)
            {
                var originalVertex = lod.VertexBufferGPUSkin.VertexData[i];
                // we need to find the chunk containing this vertex
                var chunk = lod.Chunks.Last(x => x.BaseVertexIndex <= i);
                List<(int influenceBone, float weight)> influences = [];
                for (int j = 0; j < 4; j++)
                {
                    var bone = chunk.BoneMap[originalVertex.InfluenceBones[j]];
                    var weight = originalVertex.InfluenceWeights[j] * weightUnpackScale;
                    if (weight > 0)
                    {
                        influences.Add((bone, weight));
                    }
                }
                vertices.Add(new IntermediateVertex()
                {
                    Index = i,
                    Position = originalVertex.Position,
                    Normal = (Vector3)originalVertex.TangentZ,
                    Tangent = (Vector3)originalVertex.TangentX,
                    OriginalIndex = i,
                    UVs = [originalVertex.UV],
                    Influences = influences,
                    BiTangentDirection = originalVertex.TangentZ.W / 127.5f - 1
                });
            }

            foreach (var section in lod.Sections)
            {
                var intermediateSection = new IntermediateMeshSection
                {
                    MaterialIndex = materialMapping[section.MaterialIndex],
                    // use the same vertices for all mesh sections so we don't need to reindex all the triangles
                    Vertices = vertices
                };

                for (int i = (int)section.BaseIndex; i < section.BaseIndex + section.NumTriangles * 3; i += 3)
                {
                    intermediateSection.Triangles.Add(new IntermediateTriangle()
                    {
                        VertIndex1 = lod.IndexBuffer[i],
                        VertIndex2 = lod.IndexBuffer[i + 1],
                        VertIndex3 = lod.IndexBuffer[i + 2],
                    });
                }

                intermediateLod.Sections.Add(intermediateSection);
            }

            return intermediateLod;
        }

        private static ModelRoot ToGltf(IntermediateMesh mesh, string versionInfo = null)
        {
            var scene = new SceneBuilder();

            // TODO do I need this?
            var containerNode = new NodeBuilder(mesh.Name);
            scene.AddNode(containerNode);

            // Materials
            List<MaterialBuilder> mats = [];
            foreach (var intermediateMat in mesh.Materials)
            {
                var mat = new MaterialBuilder(intermediateMat.Name);
                mat.WithDoubleSide(intermediateMat.TwoSided);
                if (intermediateMat.DiffTexture != null)
                {
                    var imageBytes = intermediateMat.DiffTexture.GetPNG(intermediateMat.DiffTexture.GetTopMip());
                    var diffImage = ImageBuilder.From(imageBytes, intermediateMat.DiffTexture.Export.ObjectNameString);
                    diffImage.AlternateWriteFileName = $"{intermediateMat.DiffTexture.Export.ObjectNameString}.*";
                    mat.WithBaseColor(diffImage);
                }
                if (intermediateMat.NormalTexture != null)
                {
                    var normalMapBytes = intermediateMat.NormalTexture.GetPNG(intermediateMat.NormalTexture.GetTopMip());
                    // flip the green channel to match the convention glTF uses
                    var img = IsImage.Load<Rgba32>(normalMapBytes);
                    var colorMatrix = new ColorMatrix(
                        1, 0, 0, 0,
                        0, -1, 0, 0,
                        0, 0, 1, 0,
                        0, 0, 0, 1,
                        0, 1, 0, 0
                    );
                    img.Mutate(x => x.ApplyProcessor(new FilterProcessor(colorMatrix)));
                    using (var ms = new MemoryStream())
                    {
                        img.SaveAsPng(ms);
                        normalMapBytes = ms.ToArray();
                    }
                    var normImage = ImageBuilder.From(normalMapBytes, $"{intermediateMat.NormalTexture.Export.ObjectNameString}_flipped");
                    normImage.AlternateWriteFileName = $"{intermediateMat.NormalTexture.Export.ObjectNameString}_flipped.*";
                    mat.WithNormal(normImage);
                }
                mats.Add(mat);
            }

            // LODs
            foreach (var lod in mesh.LODs)
            {
                var name = lod.Index == 0 ? mesh.Name : $"{mesh.Name}_LOD_{lod.Index}";
                // SkeletalMesh version
                if (mesh.Skeleton != null)
                {
                    var mb = new MeshBuilder<VertexPositionNormalTangent, VertexTextureNOriginalIndex, VertexJoints4>(name);

                    foreach (var section in lod.Sections)
                    {
                        var primitive = mb.UsePrimitive(mats[section.MaterialIndex]);
                        foreach (var tri in section.Triangles)
                        {
                            VertexBuilder<VertexPositionNormalTangent, VertexTextureNOriginalIndex, VertexJoints4> GetVert(int i)
                            {
                                var intermediateVert = section.Vertices[i];
                                var vb = new VertexBuilder<VertexPositionNormalTangent, VertexTextureNOriginalIndex, VertexJoints4>()
                                    .WithGeometry(
                                        TransformVertexPosition(intermediateVert.Position),
                                        TransformDirection(intermediateVert.Normal.Value),
                                        new Vector4(TransformDirection(intermediateVert.Tangent.Value), intermediateVert.BiTangentDirection))
                                    .WithMaterial([.. intermediateVert.UVs])
                                    .WithSkinning(intermediateVert.Influences);
                                vb.Material.OriginalIndex = intermediateVert.OriginalIndex;
                                return vb;
                            }
                            primitive.AddTriangle(GetVert(tri.VertIndex1), GetVert(tri.VertIndex2), GetVert(tri.VertIndex3));
                        }
                    }
                    var meshNode = new NodeBuilder();
                    containerNode.AddNode(meshNode);
                    var rigidMesh = scene.AddRigidMesh(mb, meshNode);
                    rigidMesh.WithName(name);
                }
                // StaticMesh version
                else
                {
                    var mb = new MeshBuilder<VertexPositionNormalTangent, VertexTextureNOriginalIndex, VertexEmpty>(name);

                    foreach (var section in lod.Sections)
                    {
                        var primitive = mb.UsePrimitive(mats[section.MaterialIndex]);
                        foreach (var tri in section.Triangles)
                        {
                            VertexBuilder<VertexPositionNormalTangent, VertexTextureNOriginalIndex, VertexEmpty> GetVert(int i)
                            {
                                var intermediateVert = section.Vertices[i];
                                var vb = new VertexBuilder<VertexPositionNormalTangent, VertexTextureNOriginalIndex, VertexEmpty>()
                                    .WithGeometry(
                                        TransformVertexPosition(intermediateVert.Position),
                                        TransformDirection(intermediateVert.Normal.Value),
                                        new Vector4(TransformDirection(intermediateVert.Tangent.Value), intermediateVert.BiTangentDirection))
                                    .WithMaterial([.. intermediateVert.UVs]);
                                vb.Material.OriginalIndex = intermediateVert.OriginalIndex;
                                return vb;
                            }
                            primitive.AddTriangle(GetVert(tri.VertIndex1), GetVert(tri.VertIndex2), GetVert(tri.VertIndex3));
                        }
                    }
                    var meshNode = new NodeBuilder();
                    containerNode.AddNode(meshNode);
                    var rigidMesh = scene.AddRigidMesh(mb, meshNode);
                    rigidMesh.WithName(name);
                }
            }


            if (mesh.CollisionMeshElements != null)
            {
                var collisionMat = new MaterialBuilder("CollisionMaterial");

                for (int i = 0; i < mesh.CollisionMeshElements.Count; i++)
                {
                    var name = $"{mesh.Name}_Collision_{i}";
                    var collisionElement = mesh.CollisionMeshElements[i];

                    var mb = new MeshBuilder<VertexPosition, VertexEmpty, VertexEmpty>(name);
                    var primitive = mb.UsePrimitive(collisionMat);

                    foreach (var tri in collisionElement.Triangles)
                    {
                        VertexBuilder<VertexPosition, VertexEmpty, VertexEmpty> GetVert(int i)
                        {
                            var intermediateVert = collisionElement.Vertices[i];
                            var vb = new VertexBuilder<VertexPosition, VertexEmpty, VertexEmpty>()
                                .WithGeometry(intermediateVert / ScaleFactor);
                            return vb;
                        }
                        primitive.AddTriangle(GetVert(tri.VertIndex1), GetVert(tri.VertIndex2), GetVert(tri.VertIndex3));
                    }

                    var meshNode = new NodeBuilder();
                    containerNode.AddNode(meshNode);
                    var rigidMesh = scene.AddRigidMesh(mb, meshNode);
                    rigidMesh.WithName(name);
                }
            }

            // skeleton/sockets
            NodeBuilder[] skeletonNodes = [];
            if (mesh.Skeleton != null)
            {
                skeletonNodes = new NodeBuilder[mesh.Skeleton.Count];
                // one pass to create all the nodes without the hierarchy
                for (int i = 0; i < mesh.Skeleton.Count; i++)
                {
                    var bone = mesh.Skeleton[i];
                    var nb = new NodeBuilder(bone.Name);
                    if (bone.ParentIndex == -1 || bone.ParentIndex == i)
                    {
                        // this is a root bone; change the local transform to account for the coordiante system differences
                        nb.WithLocalTranslation(TransformRootBonePosition(bone.Position))
                            .WithLocalRotation(TransformRootBoneRotation(bone.Rotation));
                        containerNode.AddNode(nb);
                    }
                    else
                    {
                        nb.WithLocalTranslation(TransformBonePosition(bone.Position))
                            .WithLocalRotation(TransformBoneRotation(bone.Rotation));
                    }
                    skeletonNodes[i] = nb;
                }
                // another pass to connect the hierarchy up
                for (int i = 0; i < mesh.Skeleton.Count; i++)
                {
                    var bone = mesh.Skeleton[i];
                    var nb = skeletonNodes[i];
                    if (bone.ParentIndex == -1 || bone.ParentIndex == i)
                    {
                        // this is a root bone; we don't need to do anything here
                        continue;
                    }
                    else
                    {
                        var parent = skeletonNodes[bone.ParentIndex];
                        parent.AddNode(nb);
                    }
                }
                // finish sockets by creating nodes under the bones they are attached to
                for (int i = 0; i < mesh.Skeleton.Count; i++)
                {
                    var nb = skeletonNodes[i];
                    var sockets = mesh.Sockets.FindAll(x => x.Bone == nb.Name);
                    foreach (var socket in sockets)
                    {
                        var socketBuilder = new NodeBuilder(socket.Name)
                            .WithLocalTranslation(TransformBonePosition(socket.RelativeLocation))
                            .WithLocalRotation(TransformSocketRotation(socket.RelativeRotation))
                            .WithLocalScale(TransformScale(socket.RelativeScale));
                        nb.AddNode(socketBuilder);
                    }
                }
            }

            var gltf = scene.ToGltf2();
            gltf.Asset.Generator = $"{versionInfo ?? "Legendary Explorer Core"}";

            // collect the real nodes for the skeleton, in the exact same order
            var jointNodes = skeletonNodes.Select(x => gltf.LogicalNodes.First(y => y.Name == x.Name)).ToArray();

            if (mesh.Skeleton != null && mesh.Skeleton.Count > 0)
            {
                // manually create the skin and then connect it up to the nodes containing the meshes
                var skin = gltf.CreateSkin(mesh.Name);
                skin.BindJoints(Matrix4x4.Identity, jointNodes);
                foreach (var node in gltf.LogicalNodes)
                {
                    if (node.Mesh != null)
                    {
                        node.WithSkin(skin);
                    }
                }
            }

            return gltf;
        }

        private static Vector3 TransformVertexPosition(Vector3 input)
        {
            return new Vector3(input.X, input.Z, input.Y) / ScaleFactor;
        }

        private static Vector3 TransformDirection(Vector3 input)
        {
            return Vector3.Normalize(new Vector3(input.X, input.Z, input.Y));
        }

        private static Vector3 TransformBonePosition(Vector3 input)
        {
            return new Vector3(input.X, -input.Y, input.Z) / ScaleFactor;
        }

        // sqrt(2)/2 comes up repeatedly in 90 degree quaternion rotations
        private static readonly float QuatHalf = (float)(Math.Sqrt(2) / 2);


        private static Quaternion TransformRootBoneRotation(Quaternion input)
        {
            // add a -90 degree rotation around the x axis
            var transform = new Quaternion(QuatHalf, 0, 0, -QuatHalf);
            return Quaternion.Normalize(transform * input);
        }

        private static Vector3 TransformRootBonePosition(Vector3 input)
        {
            return new Vector3(input.X, input.Z, input.Y) / ScaleFactor;
        }

        private static Vector3 TransformScale(Vector3 input)
        {
            // TODO check if this is actually the right transform
            return new Vector3(input.X, input.Z, input.Y);
        }

        private static Quaternion TransformSocketRotation(Quaternion input)
        {
            // add a 90 degree rotation around the x axis
            var transform = new Quaternion(QuatHalf, 0, 0, QuatHalf);
            return Quaternion.Normalize(transform * input);
        }

        private static Quaternion TransformBoneRotation(Quaternion input)
        {
            // first, get it into the form glTF expects due to the swapped axes
            var temp = new Quaternion(input.X, input.Z, input.Y, -input.W);
            // next, we undo the rotation introduced by the parent
            temp = new Quaternion(QuatHalf, 0, 0, QuatHalf) * temp;
            // finally, we rotate the child in its local axes
            temp = temp * new Quaternion(QuatHalf, 0, 0, -QuatHalf);
            return Quaternion.Normalize(temp);
        }
        #endregion

        #region import

        public static void ConvertGltfToMesh(ModelRoot gltf, IMEPackage pcc)
        {
            foreach (var node in gltf.LogicalNodes)
            {
                // TODO sort meshes to group them into LODs
                if (!node.Mesh.IsNull())
                {
                    var intermediateMesh = ToIntermediateMesh(node);
                    if (node.Skin.IsNull())
                    {
                        //ImportStaticMesh(node);
                    }
                    else
                    {
                        ImportSkeletalMesh(intermediateMesh, pcc);
                    }
                }
            }
        }

        private static IntermediateMesh ToIntermediateMesh(params Node[] nodes)
        {
            if (nodes.Length == 0)
            {
                throw new ArgumentException();
            }
            int vertIndex = 0;
            int triangleIndex = 0;
            int lodIndex = 0;
            int boneIndex = 0;
            // maps from material index within the gltf file to materials within this mesh (in the array order)
            List<int> materialMap = [];

            // maps from bone order within the gltf file to bone order within this mesh
            List<int> boneMap = [];

            var intermediateMesh = new IntermediateMesh();

            if (nodes[0].Skin != null)
            {
                intermediateMesh.Skeleton = [];
                foreach (var joint in nodes[0].Skin.Joints)
                {
                    boneMap.Add(joint.LogicalIndex);
                    intermediateMesh.Skeleton.Add(new IntermediateBone()
                    {
                        Index = boneIndex++,
                        Name = joint.Name,
                        // position is relative to the parent bone in both storage systems, so we only need to flip y to account for the different y direction convention
                        Position = joint.LocalTransform.Translation with { Y = -joint.LocalTransform.Translation.Y },
                        // rotation is also relative to the parent, and not messed up by the y axis difference, so we need to leave it alone
                        Rotation = joint.LocalTransform.Rotation,
                    });
                }
                // reconstruct the hierarchy of bones from the node hierarchy. There can be other nodes in between joints, so we have to check all ancestors
                List<int> rootJoints = [];
                foreach (var joint in nodes[0].Skin.Joints)
                {
                    Node FindJointParent(Node node)
                    {
                        Node jointParent = null;
                        while (node.VisualParent != null)
                        {
                            node = node.VisualParent;

                            if (nodes[0].Skin.Joints.Contains(node))
                            {
                                return node;
                            }
                        }
                        return jointParent;
                    }
                    var parent = FindJointParent(joint);
                    var intermediateJointIndex = boneMap.IndexOf(joint.LogicalIndex);

                    if (parent == null)
                    {
                        rootJoints.Add(intermediateJointIndex);
                        // for root bones, we need to adjust the rotation and position to account for the coordinate system differences
                        intermediateMesh.Skeleton[intermediateJointIndex].ParentIndex = -1;
                        intermediateMesh.Skeleton[intermediateJointIndex].Position = ScaleForME(Yup2Zup(joint.LocalTransform.Translation));
                        // we need to rotate the root node's rotation to account for the difference in coordinate systems between ME and glTF (glTF uses y up) 
                        intermediateMesh.Skeleton[intermediateJointIndex].Rotation = Yup2Zup(joint.LocalTransform.Rotation);
                    }
                    else
                    {
                        var intermediateParentIndex = boneMap.IndexOf(parent.LogicalIndex);
                        intermediateMesh.Skeleton[intermediateJointIndex].ParentIndex = intermediateParentIndex;
                        intermediateMesh.Skeleton[intermediateParentIndex].NumChildren++;
                    }
                }
                if (rootJoints.Count > 1)
                {
                    // TODO make a new fake root bone?
                    // just leave it alone? does ME technically require a single root bone?
                    throw new NotImplementedException("This skeleton doesn't seem to have a single root bone, and I don't know how to handle that yet.");
                }
            }

            foreach (var node in nodes)
            {
                var LOD = new IntermediateLOD() { Index = lodIndex++ };
                // a primitive, for our uses, will roughly correspond to a material
                // technically it corresponds to a GPU rendering pass, which can be other things, but is most likely to be a material for us.
                foreach (var prim in node.Mesh.Primitives)
                {
                    switch (prim.DrawPrimitiveType)
                    {
                        // we do not support points or lines outside the context of a triangle; ignore these if they come up, which is unlikely
                        case PrimitiveType.POINTS:
                        case PrimitiveType.LINES:
                        case PrimitiveType.LINE_STRIP:
                        case PrimitiveType.LINE_LOOP:
                            continue;
                    }
                    // material; the material indices in the glTF are global to the file, shared between meshes. We need to get to a list of materials for just this mesh
                    // each time we encounter a new material index, we will put it in an array
                    // we will count a null material as having an index of -1 and leave this material empty in LEX
                    var gltfMatIndex = prim.Material?.LogicalIndex ?? -1;
                    var meshMatIndex = materialMap.IndexOf(gltfMatIndex);
                    if (meshMatIndex == -1)
                    {
                        // TODO make sure this is not off by 1
                        meshMatIndex = materialMap.Count;
                        materialMap.Add(gltfMatIndex);
                        intermediateMesh.Materials.Add(new IntermediateMaterial(prim.Material?.Name ?? "null"));
                    }

                    var meshSection = new IntermediateMeshSection()
                    {
                        MaterialIndex = meshMatIndex
                    };

                    // this gets us all the attributes of each vertex in order, each in their own array, where all arrays are the same size
                    var vertColumns = prim.GetVertexColumns();

                    for (int i = 0; i < vertColumns.Positions.Count; i++)
                    {
                        var vert = new IntermediateVertex
                        {
                            Index = vertIndex++,
                            Position = ScaleForME(Yup2Zup(vertColumns.Positions[i]))
                        };

                        // Normals
                        // usually present, but not required
                        if (vertColumns.Normals != null)
                        {
                            vert.Normal = Yup2Zup(vertColumns.Normals[i]);
                        }

                        // Tangents
                        // not required, but will be imported if present, calculated otherwise
                        if (vertColumns.Tangents != null)
                        {
                            var tanX = new Vector3(vertColumns.Tangents[i].X, vertColumns.Tangents[i].Y, vertColumns.Tangents[i].Z);
                            vert.Tangent = Yup2Zup(tanX);
                            vert.BiTangentDirection = vertColumns.Tangents[i].W;
                        }

                        // UVs
                        void AddUV(IList<Vector2>? column)
                        {
                            if (column != null)
                            {
                                vert.UVs.Add(column[i]);
                            }
                        }
                        // usually present
                        AddUV(vertColumns.TexCoords0);
                        // only present for some static meshes
                        AddUV(vertColumns.TexCoords1);
                        AddUV(vertColumns.TexCoords2);
                        AddUV(vertColumns.TexCoords3);

                        // weights
                        // only present for skeletal meshes
                        if (vertColumns.Joints0 != null)
                        {
                            var bones = vertColumns.Joints0[i];
                            var weights = vertColumns.Weights0[i];
                            for (int j = 0; j < 4; j++)
                            {
                                vert.Influences.Add((int)bones[j], weights[j]);
                            }
                        }
                        // unlikely to be present, we just need to add them to make sure we cull the right ones later
                        if (vertColumns.Joints1 != null)
                        {
                            var bones = vertColumns.Joints1[i];
                            var weights = vertColumns.Weights1[i];
                            for (int j = 0; j < 4; j++)
                            {
                                var intermediateBoneIndex = boneMap.IndexOf((int)bones[j]);
                                vert.Influences.Add(intermediateBoneIndex, weights[j]);
                            }
                        }

                        //meshSection.Vertices.Add(vert);
                    }

                    // this gets us a list of int triplets; the indices of each triangle
                    var triIndices = prim.GetTriangleIndices();

                    foreach (var (v1, v2, v3) in triIndices)
                    {
                        // I think the vertex order is correct but need to check
                        var tri = new IntermediateTriangle()
                        {
                            //Index = triangleIndex++,
                            //MaterialIndex = meshMatIndex,
                            VertIndex1 = v2,
                            VertIndex2 = v3,
                            VertIndex3 = v1,
                        };
                        meshSection.Triangles.Add(tri);
                    }
                    LOD.Sections.Add(meshSection);
                }
                intermediateMesh.LODs.Add(LOD);
            }

            return intermediateMesh;
        }

        private static void ImportSkeletalMesh(IntermediateMesh intermediateMesh, IMEPackage package)
        {
            var meshBin = SkeletalMesh.Create();
            SetupSkeleton(intermediateMesh.Skeleton, meshBin);
            SetupBounds(intermediateMesh, meshBin);
            SetupMaterials(intermediateMesh.Materials, meshBin, package);
            foreach (var lod in intermediateMesh.LODs)
            {
                SetupLOD(intermediateMesh, lod, meshBin);
            }

            static void SetupSkeleton(IList<IntermediateBone> skeleton, SkeletalMesh meshBin)
            {
                // initialize the array to the right size
                meshBin.RefSkeleton = new MeshBone[skeleton.Count];
                // keep track of the depth of each bone so we can get the overall skeletal depth
                var skeletalDepth = Enumerable.Repeat(-1, skeleton.Count).ToArray();

                int GetDepth(int i)
                {
                    // check if we have already calculated this one
                    if (skeletalDepth[i] != -1)
                    {
                        return skeletalDepth[i];
                    }
                    var parentIndex = skeleton[i].ParentIndex;
                    // check for the case that this is the root bone of the skeleton, where it points to itself (usually 0) as its own parent
                    if (parentIndex == -1 || parentIndex == i)
                    {
                        skeletalDepth[i] = 1;
                        return 1;
                    }
                    // next, get the depth of the parent + 1
                    skeletalDepth[i] = GetDepth(parentIndex) + 1;
                    return skeletalDepth[i];
                }

                for (var i = 0; i < skeleton.Count; i++)
                {
                    var currentBone = skeleton[i];
                    meshBin.NameIndexMap.Add(currentBone.Name, i);
                    meshBin.RefSkeleton[i] = new MeshBone()
                    {
                        Name = currentBone.Name,
                        NumChildren = currentBone.NumChildren,
                        BoneColor = new LegendaryExplorerCore.SharpDX.Color(new Vector4(1, 1, 1, 1)),
                        Flags = 0,
                        ParentIndex = currentBone.ParentIndex,
                        Position = new Vector3(currentBone.Position.X, currentBone.Position.Y * -1, currentBone.Position.Z),
                        Orientation = new Quaternion(currentBone.Rotation.X, currentBone.Rotation.Y * -1, currentBone.Rotation.Z, currentBone.Rotation.W)
                    };

                    // make sure we calculate the depth
                    GetDepth(i);
                }

                // now find the maximum depth and set that as the skeletal depth
                meshBin.SkeletalDepth = skeletalDepth.Max();
            }

            static void SetupBounds(IntermediateMesh intermediateMesh, SkeletalMesh meshBin)
            {
                //// bounds are important at least for the camera display preview in LEX, and possibly important for when to cull meshes based on visibility in game
                //// separate out the coordinates for each axis so we can operate on them
                //var xCoords = intermediateMesh.LODs[0].Vertices.Select(x => x.Position.X);
                //var yCoords = intermediateMesh.LODs[0].Vertices.Select(x => x.Position.Y);
                //var zCoords = intermediateMesh.LODs[0].Vertices.Select(x => x.Position.Z);

                //// get the origin by averaging all vertex positions; it'll probably be close enough
                //var origin = new Vector3(xCoords.Average(), yCoords.Average(), zCoords.Average());

                //var xRange = xCoords.Select(coord => Math.Abs(coord - origin.X)).Max();
                //var yRange = yCoords.Select(coord => Math.Abs(coord - origin.Y)).Max();
                //var zRange = zCoords.Select(coord => Math.Abs(coord - origin.Z)).Max();
                //var boxExtent = new Vector3(xRange, yRange, zRange);

                //var sphereRad = boxExtent.Length();
                //meshBin.Bounds = new BoxSphereBounds
                //{
                //    Origin = origin,
                //    // best guess at a reasonable margin
                //    BoxExtent = boxExtent * 2,
                //    SphereRadius = sphereRad * 2
                //};
            }

            static void SetupMaterials(IList<IntermediateMaterial> materials, SkeletalMesh meshBin, IMEPackage package)
            {
                SetNumMaterialSlots(meshBin, materials.Count);
                for (int i = 0; i < materials.Count; i++)
                {
                    if (materials[i].Name == "null")
                    {
                        continue;
                    }
                    var entry = FindEntryByMemeroryFullPath(package, materials[i].Name, "MaterialInterface");
                    if (entry != null)
                    {
                        meshBin.Materials[i] = entry.UIndex;
                    }
                }
            }

            static void SetupLOD(IntermediateMesh intermediateMesh, IntermediateLOD lod, SkeletalMesh meshBin)
            {
                //    // TODO implement normal generation, maybe even with welding, angle threshold?
                //    if (lod.Vertices[0].Normal == null)
                //    {
                //        throw new NotImplementedException("I haven't implemented normal generation yet. export your glTF with normals.");
                //    }
                //    // TODO implement normal generation, maybe even with welding, angle threshold?
                //    if (lod.Vertices[0].Tangent == null)
                //    {
                //        throw new NotImplementedException("I haven't implemented tangent generation yet. export your glTF with tangents.");
                //    }
                //    SetupSectionsAndChunks();

                //    void SetupSectionsAndChunks()
                //    {
                //        if (intermediateMesh.Materials.Count == 1)
                //        {

                //        }
                //        else
                //        {
                //            // TODO make this optional?
                //            // this is useful for draw order stuff, but not the only way to do it, and it might be nice to preserve ordering too
                //            if (true)
                //            {
                //                //lod.Triangles = [.. lod.Triangles.OrderBy(x => x.MaterialIndex)];
                //            }

                //            List<List<IntermediateTriangle>> matGroups = [];
                //            //var currentMat = lod.Triangles[0].MaterialIndex;
                //            var currentGroup = new List<IntermediateTriangle>();
                //            foreach (var triangle in lod.Triangles)
                //            {
                //                if (triangle.MaterialIndex == currentMat)
                //                {
                //                    currentGroup.Add(triangle);
                //                }
                //                else
                //                {
                //                    currentMat = triangle.MaterialIndex;
                //                    matGroups.Add(currentGroup);
                //                    currentGroup = [triangle];
                //                }
                //            }

                //            List<MeshSection> sections = [];
                //            var startIndex = 0;
                //            foreach (var matGroup in matGroups)
                //            {
                //                var mat = matGroup[0].MaterialIndex;
                //                var section = new MeshSection
                //                {
                //                    Triangles = [.. matGroup],
                //                    BaseTriIndex = startIndex,
                //                    MatIndex = mat,
                //                };

                //                // calculate the min and max vertex indices within this section
                //                var sectionIndices = matGroup.SelectMany<PSK.PSKTriangle, ushort>(x => [vertsInWedgeOrder[x.WedgeIdx0].Index, vertsInWedgeOrder[x.WedgeIdx1].Index, vertsInWedgeOrder[x.WedgeIdx2].Index]);
                //                section.MinVertIndex = sectionIndices.Min();
                //                section.MaxVertIndex = sectionIndices.Max();

                //                sections.Add(section);
                //                startIndex += matGroup.Count();
                //            }
                //        }

                //        var LOD = new StaticLODModel
                //        {
                //            IndexBuffer = [.. lod.Triangles.SelectMany<IntermediateTriangle, ushort>(x => [(ushort)x.VertIndex1, (ushort)x.VertIndex2, (ushort)x.VertIndex3])],
                //            // TODO filter this down to bones that actually have any weighting?
                //            RequiredBones = [.. Enumerable.Range(0, intermediateMesh.Skeleton.Count).Select(x => (byte)x)]
                //        };


                //    }
            }
        }

        private struct MeshSection
        {
            public IntermediateTriangle[] Triangles;
            public int BaseTriIndex;
            public int ChunkIndex;
            public int MatIndex;
            public int MinVertIndex;
            public int MaxVertIndex;
        }

        private static void SetNumMaterialSlots(SkeletalMesh meshBinary, int numMaterials)
        {
            if (meshBinary.Materials.Length == numMaterials)
            {
                return;
            }

            var tempMaterials = meshBinary.Materials;

            meshBinary.Materials = new int[numMaterials];
            for (int i = 0; i < numMaterials && i < tempMaterials.Length; i++)
            {
                meshBinary.Materials[i] = tempMaterials[i];
            }
        }

        private static Vector3 Yup2Zup(Vector3 input)
        {
            return new Vector3(input.X, input.Z, input.Y);
        }

        private static Quaternion Yup2Zup(Quaternion input)
        {
            var transformQuat = new Quaternion(MathF.Sqrt(2f) / 2f, 0, 0, MathF.Sqrt(2f) / 2f);
            return Quaternion.Normalize(input * transformQuat);
        }

        private static Vector3 ScaleForME(Vector3 input)
        {
            return input * ScaleFactor;
        }
        #endregion

        #region intermediate
        private class IntermediateMesh
        {
            public string Name;
            public List<IntermediateMaterial> Materials = [];
            // will be null for static meshes
            public List<IntermediateBone> Skeleton;
            public List<IntermediateLOD> LODs = [];
            public List<IntermediateSocket> Sockets = [];
            public List<IntermediateCollisionElement> CollisionMeshElements;

            public IntermediateMesh()
            {
            }
        }

        // a collision mesh is made up of one or more convex elements
        // they have vertices and triangles, but no LODs, materials, UVs, etc
        private class IntermediateCollisionElement
        {
            public List<Vector3> Vertices = [];
            public List<IntermediateTriangle> Triangles = [];
        }

        private class IntermediateMaterial
        {
            public IntermediateMaterial() { }
            public IntermediateMaterial(string name)
            {
                Name = name;
            }
            public string Name;
            // export only
            public Texture2D DiffTexture;
            public Texture2D NormalTexture;
            public bool TwoSided;
        }

        private class IntermediateMeshSection
        {
            public int MaterialIndex;
            public List<IntermediateTriangle> Triangles = [];
            public List<IntermediateVertex> Vertices = [];

            public IntermediateMeshSection()
            {
            }
        }

        private struct IntermediateTriangle
        {
            //public int Index;
            public int VertIndex1;
            public int VertIndex2;
            public int VertIndex3;
        }

        private class IntermediateLOD
        {
            public int Index;
            public List<IntermediateMeshSection> Sections = [];

            public IntermediateLOD()
            {
            }
        }

        private struct IntermediateVertex
        {
            public int Index;
            // always required
            public Vector3 Position;
            // can be imported or calculated if need be
            public Vector3? Normal;
            // will be calculated
            public Vector3? Tangent;
            // will be calculated
            public float BiTangentDirection;
            // will usually be present. Expect length 1 for skeletal meshes, but static meshes can have multiple
            public List<Vector2> UVs = [];
            // only present for skeletal meshes. The engine supports a maximum of four influences, so that is the max length
            public List<(int influenceBone, float weight)> Influences = [];
            // no known use yet, but static meshes might support it
            //Vector4 Color;
            // used to store the original index when we export it from ME to glTF; can hopefully help us reconsitutue it later
            public int OriginalIndex;
            public IntermediateVertex()
            {
            }
        }

        private class IntermediateBone
        {
            public int Index;
            public string Name;
            public int NumChildren;
            public int ParentIndex;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private class IntermediateSocket
        {
            public string Name;
            public string Bone;
            public Vector3 RelativeLocation;
            public Quaternion RelativeRotation;
            public Vector3 RelativeScale;
        }

        #endregion

        private static void FindBestDiffAndNormForMaterial(IntermediateMaterial mat, ExportEntry matEntry)
        {
            // TODO hardcode in what params to look for for specific known materials to avoid the stupid gold bars texture, among other things. 
            PackageEditorExperimentsSquid.GetMaterialTextures(matEntry, out var textures, out var baseTextures);
            foreach (var (param, tex) in textures)
            {
                // don't look at the params, it'll pull in things like teeth diff for the scalp which are not what you want
                if (/*param.Contains("Diff", StringComparison.InvariantCultureIgnoreCase) || */tex.ObjectName.ToString().Contains("Diff", StringComparison.InvariantCultureIgnoreCase))
                {
                    mat.DiffTexture ??= new Texture2D(tex);
                }
                else if (/*param.Contains("Norm", StringComparison.InvariantCultureIgnoreCase) ||*/ tex.ObjectName.ToString().Contains("Norm", StringComparison.InvariantCultureIgnoreCase))
                {
                    mat.NormalTexture ??= new Texture2D(tex);
                }
            }
            foreach (var tex in baseTextures)
            {
                if (tex.ObjectName.ToString().Contains("Diff", StringComparison.InvariantCultureIgnoreCase))
                {
                    mat.DiffTexture ??= new Texture2D(tex);
                }
                else if (tex.ObjectName.ToString().Contains("Norm", StringComparison.InvariantCultureIgnoreCase))
                {
                    mat.NormalTexture ??= new Texture2D(tex);
                }
            }
        }

        // TODO this is probably broadly useful and could live somewhere else as an extension method
        public static IEntry FindEntryByMemeroryFullPath(IMEPackage pachage, string memoryFullPath, string className = null)
        {
            foreach (IEntry entry in pachage.Exports.Concat<IEntry>(pachage.Imports))
            {
                if (entry.MemoryFullPath.CaseInsensitiveEquals(memoryFullPath))
                {
                    if (className != null && !(entry.ClassName.CaseInsensitiveEquals(className) || entry.IsA(className)))
                    {
                        continue;
                    }
                    return entry;
                }
            }
            return null;
        }
    }

    public struct VertexTextureNOriginalIndex : IVertexCustom
    {
        #region constructors

        public VertexTextureNOriginalIndex(int originalIndex, IEnumerable<Vector2> UVs)
        {
            _originalIndex = originalIndex;
            _texCoords = UVs.ToList();
        }
        //public static implicit operator VertexTextureNOriginalIndex((Vector4 color, Vector2 tex, Single customId) tuple)
        //{
        //    return new VertexTextureNOriginalIndex(tuple.color, tuple.tex, tuple.customId);
        //}

        //public VertexTextureNOriginalIndex(Vector4 color, Vector2 tex, Single customId)
        //{
        //    Color = color;
        //    TexCoord = tex;
        //    CustomId = customId;
        //}

        //public VertexTextureNOriginalIndex(IVertexMaterial src)
        //{
        //    this.Color = src.MaxColors > 0 ? src.GetColor(0) : Vector4.One;
        //    this.TexCoord = src.MaxTextCoords > 0 ? src.GetTexCoord(0) : Vector2.Zero;

        //    this.CustomId = 0;

        //    if (src is VertexTextureNOriginalIndex custom)
        //    {
        //        this.CustomId = custom.CustomId;
        //    }
        //    else if (src is IVertexCustom otherx)
        //    {
        //        if (otherx.TryGetCustomAttribute(CUSTOMATTRIBUTENAME, out object attr0) && attr0 is float c0) this.CustomId = c0;
        //    }
        //}

        #endregion

        #region data

        public const string OriginalIndexAttributeName = "_original_index";

        private List<Vector2> _texCoords = [];
        private int _originalIndex = -1;

        public int OriginalIndex
        {
            get => _originalIndex;
            set => _originalIndex = value;
        }

        IEnumerable<KeyValuePair<string, AttributeFormat>> IVertexReflection.GetEncodingAttributes()
        {
            for (int i = 0; i < _texCoords.Count; i++)
            {
                yield return new KeyValuePair<string, AttributeFormat>($"TEXCOORD_{i}", new AttributeFormat(DimensionType.VEC2));
            }
            yield return new KeyValuePair<string, AttributeFormat>(OriginalIndexAttributeName, new AttributeFormat(DimensionType.SCALAR));
        }

        public int MaxColors => 0;

        public int MaxTextCoords => _texCoords.Count;

        private static readonly string[] _CustomNames = { OriginalIndexAttributeName };
        public IEnumerable<string> CustomAttributes => _CustomNames;

        #endregion

        #region API

        /// <inheritdoc/>
        public VertexMaterialDelta Subtract(IVertexMaterial baseValue)
        {
            return this.Subtract((VertexTextureNOriginalIndex)baseValue);
        }

        /// <inheritdoc cref="Subtract(IVertexMaterial)"/>
        public VertexMaterialDelta Subtract(in VertexTextureNOriginalIndex baseValue)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc/>
        public void Add(in VertexMaterialDelta delta)
        {
            throw new NotImplementedException();
        }

        void IVertexMaterial.SetColor(int setIndex, Vector4 color)
        {
            throw new ArgumentOutOfRangeException(nameof(setIndex));
        }

        void IVertexMaterial.SetTexCoord(int setIndex, Vector2 coord)
        {
            _texCoords ??= [];
            if (setIndex < _texCoords.Count - 1)
            {
                _texCoords[setIndex] = coord;
            }
            else if (setIndex == _texCoords.Count)
            {
                _texCoords.Add(coord);
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(setIndex));
            }
        }

        public Vector4 GetColor(int index)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        public Vector2 GetTexCoord(int index)
        {
            _texCoords ??= [];
            if (index <= _texCoords.Count - 1)
            {
                return _texCoords[index];
            }
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        public void Validate()
        {
            // TODO do I need this?
        }

        public bool TryGetCustomAttribute(string attribute, out object value)
        {
            if (attribute != OriginalIndexAttributeName)
            {
                value = null; return false;
            }
            value = (float)_originalIndex;
            return true;
        }

        public void SetCustomAttribute(string attributeName, object value)
        {
            if (attributeName == OriginalIndexAttributeName && value is float floatValue)
            {
                _originalIndex = (int)floatValue;
            }
        }
        #endregion
    }
}
