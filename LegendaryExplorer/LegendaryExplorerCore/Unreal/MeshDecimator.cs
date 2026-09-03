using LegendaryExplorerCore.Unreal.BinaryConverters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LegendaryExplorerCore.Unreal
{
    public sealed class MeshLodVertexLimitInfo
    {
        public string MeshName { get; }
        public int LodIndex { get; }
        public int VertexCount { get; }

        public MeshLodVertexLimitInfo(string meshName, int lodIndex, int vertexCount)
        {
            MeshName = meshName;
            LodIndex = lodIndex;
            VertexCount = vertexCount;
        }
    }

    public partial class GLTF
    {
        private static IReadOnlyList<MeshLodVertexLimitInfo> GetOversizedLods(IEnumerable<IntermediateMesh> meshes)
        {
            List<MeshLodVertexLimitInfo> oversizedLods = [];
            foreach (IntermediateMesh mesh in meshes)
            {
                foreach (IntermediateLOD lod in mesh.LODs)
                {
                    int vertexCount = GetAllVertices(lod).Count;
                    if (vertexCount > MeshDecimator.MaxSupportedVertexCount)
                    {
                        oversizedLods.Add(new MeshLodVertexLimitInfo(mesh.Name, lod.Index, vertexCount));
                    }
                }
            }
            return oversizedLods;
        }

        private static void DecimateToVertexLimit(IntermediateMesh mesh)
        {
            foreach (IntermediateLOD lod in mesh.LODs)
            {
                List<IntermediateVertex> vertices = GetAllVertices(lod);
                if (vertices.Count <= MeshDecimator.MaxSupportedVertexCount)
                {
                    continue;
                }

                List<IntermediateSectionGeometry> sectionGeometry = [];
                foreach (IntermediateMeshSection section in lod.Sections.Where(section => section.Triangles.Count > 0))
                {
                    int[] globalIndices = [.. section.Triangles.SelectMany<IntermediateTriangle, int>(triangle =>
                        [triangle.VertIndex1, triangle.VertIndex2, triangle.VertIndex3])];
                    int[] sourceVertexIndices = [.. globalIndices.Distinct()];
                    Dictionary<int, int> globalToLocal = sourceVertexIndices.Select((globalIndex, localIndex) => (globalIndex, localIndex))
                        .ToDictionary(pair => pair.globalIndex, pair => pair.localIndex);
                    sectionGeometry.Add(new IntermediateSectionGeometry(section, sourceVertexIndices,
                        [.. globalIndices.Select(index => globalToLocal[index])]));
                }

                int[] sectionVertexCounts = [.. sectionGeometry.Select(section => section.SourceVertexIndices.Length)];
                double[] sectionSurfaceAreas = [.. sectionGeometry.Select(section => CalculateSurfaceArea(section, vertices))];
                int[] sectionMinimums = [.. sectionGeometry.Select(section =>
                    Math.Min(section.SourceVertexIndices.Length, Math.Max(256, section.ComponentCount * 3)))];
                int targetVertexCount = Math.Min(MeshDecimator.MaxSupportedVertexCount, sectionVertexCounts.Sum());
                int[] sectionTargets = AllocateSectionTargets(sectionVertexCounts, sectionSurfaceAreas, sectionMinimums, targetVertexCount);
                List<IntermediateVertex> newVertices = new(targetVertexCount);
                List<IntermediateMeshSection> decimatedSections = [];

                for (int sectionIndex = 0; sectionIndex < sectionGeometry.Count; sectionIndex++)
                {
                    IntermediateSectionGeometry geometry = sectionGeometry[sectionIndex];
                    Vector3[] positions = [.. geometry.SourceVertexIndices.Select(index => vertices[index].Position)];
                    MeshDecimationResult result = MeshDecimator.DecimateGeometry(
                        positions, [geometry.LocalIndices], sectionTargets[sectionIndex], geometry.VertexGroups,
                        minimumVerticesPerGroup: 3);
                    int[] indices = result.SectionIndices[0];
                    if (indices.Length == 0)
                    {
                        throw new InvalidOperationException(
                            $"Mesh '{mesh.Name}' LOD {lod.Index} section {sectionIndex} could not be preserved when reducing " +
                            $"{sectionVertexCounts[sectionIndex]:N0} vertices to its {sectionTargets[sectionIndex]:N0}-vertex budget.");
                    }

                    int vertexOffset = newVertices.Count;
                    for (int i = 0; i < result.SourceVertexIndices.Length; i++)
                    {
                        int sourceIndex = geometry.SourceVertexIndices[result.SourceVertexIndices[i]];
                        IntermediateVertex vertex = vertices[sourceIndex];
                        vertex.Position = result.Positions[i];
                        vertex.OriginalIndex = newVertices.Count;
                        newVertices.Add(vertex);
                    }

                    IntermediateMeshSection section = geometry.Section;
                    section.Triangles = [];
                    for (int i = 0; i < indices.Length; i += 3)
                    {
                        section.Triangles.Add(new IntermediateTriangle
                        {
                            VertIndex1 = indices[i] + vertexOffset,
                            VertIndex2 = indices[i + 1] + vertexOffset,
                            VertIndex3 = indices[i + 2] + vertexOffset
                        });
                    }
                    decimatedSections.Add(section);
                }
                foreach (IntermediateMeshSection section in decimatedSections)
                {
                    section.Vertices = newVertices;
                }
                lod.Sections = decimatedSections;
            }
        }

        private static double CalculateSurfaceArea(IntermediateSectionGeometry geometry, IReadOnlyList<IntermediateVertex> vertices)
        {
            double surfaceArea = 0;
            for (int i = 0; i < geometry.LocalIndices.Length; i += 3)
            {
                Vector3 vertex1 = vertices[geometry.SourceVertexIndices[geometry.LocalIndices[i]]].Position;
                Vector3 vertex2 = vertices[geometry.SourceVertexIndices[geometry.LocalIndices[i + 1]]].Position;
                Vector3 vertex3 = vertices[geometry.SourceVertexIndices[geometry.LocalIndices[i + 2]]].Position;
                surfaceArea += Vector3.Cross(vertex2 - vertex1, vertex3 - vertex1).Length() * 0.5;
            }
            return surfaceArea;
        }

        private static int[] AllocateSectionTargets(IReadOnlyList<int> vertexCounts,
            IReadOnlyList<double> surfaceAreas, IReadOnlyList<int> minimumTargets, int targetVertexCount)
        {
            if (vertexCounts.Count != surfaceAreas.Count || vertexCounts.Count != minimumTargets.Count)
            {
                throw new ArgumentException("The section allocation lists must have the same length.");
            }
            if (vertexCounts.Count == 0 || vertexCounts.Any(count => count < 3)
                || minimumTargets.Where((minimum, index) => minimum < 3 || minimum > vertexCounts[index]).Any())
            {
                throw new InvalidOperationException("Every mesh section must contain a valid minimum of at least three referenced vertices.");
            }
            if (vertexCounts.Sum() <= targetVertexCount)
            {
                return [.. vertexCounts];
            }

            int minimumVertexCount = minimumTargets.Sum();
            if (minimumVertexCount > targetVertexCount)
            {
                throw new InvalidOperationException(
                    $"The mesh has too many disconnected pieces to preserve within the {targetVertexCount:N0}-vertex limit.");
            }

            int[] targets = [.. minimumTargets];
            int remainingVertices = targetVertexCount - minimumVertexCount;
            if (remainingVertices == 0)
            {
                return targets;
            }
            int[] capacities = [.. vertexCounts.Select((count, index) => count - minimumTargets[index])];
            double[] weights = [.. surfaceAreas.Select((area, index) =>
                double.IsFinite(area) && area > 0 ? area : Math.Max(1, capacities[index]))];

            static double AllocatedAtScale(double scale, IReadOnlyList<int> sectionCapacities, IReadOnlyList<double> sectionWeights)
            {
                double allocated = 0;
                for (int i = 0; i < sectionCapacities.Count; i++)
                {
                    allocated += Math.Min(sectionCapacities[i], sectionWeights[i] * scale);
                }
                return allocated;
            }

            double lowerScale = 0;
            double upperScale = 1;
            while (AllocatedAtScale(upperScale, capacities, weights) < remainingVertices)
            {
                upperScale *= 2;
            }
            for (int iteration = 0; iteration < 80; iteration++)
            {
                double middleScale = (lowerScale + upperScale) * 0.5;
                if (AllocatedAtScale(middleScale, capacities, weights) < remainingVertices)
                {
                    lowerScale = middleScale;
                }
                else
                {
                    upperScale = middleScale;
                }
            }

            var remainders = new List<(int SectionIndex, double Fraction)>();
            for (int i = 0; i < vertexCounts.Count; i++)
            {
                double exactShare = Math.Min(capacities[i], weights[i] * upperScale);
                int wholeShare = (int)Math.Floor(exactShare);
                targets[i] += wholeShare;
                remainders.Add((i, exactShare - wholeShare));
            }

            int unallocatedVertices = targetVertexCount - targets.Sum();
            foreach ((int sectionIndex, _) in remainders.OrderByDescending(item => item.Fraction)
                         .ThenBy(item => item.SectionIndex)
                         .Where(item => targets[item.SectionIndex] < vertexCounts[item.SectionIndex])
                         .Take(unallocatedVertices))
            {
                targets[sectionIndex]++;
            }
            return targets;
        }

        private sealed class IntermediateSectionGeometry
        {
            public IntermediateMeshSection Section { get; }
            public int[] SourceVertexIndices { get; }
            public int[] LocalIndices { get; }
            public int[] VertexGroups { get; }
            public int ComponentCount { get; }

            public IntermediateSectionGeometry(IntermediateMeshSection section, int[] sourceVertexIndices, int[] localIndices)
            {
                Section = section;
                SourceVertexIndices = sourceVertexIndices;
                LocalIndices = localIndices;

                int[] parents = Enumerable.Range(0, sourceVertexIndices.Length).ToArray();
                for (int i = 0; i < localIndices.Length; i += 3)
                {
                    Union(localIndices[i], localIndices[i + 1]);
                    Union(localIndices[i + 1], localIndices[i + 2]);
                }
                Dictionary<int, int> rootToGroup = [];
                VertexGroups = new int[sourceVertexIndices.Length];
                for (int i = 0; i < VertexGroups.Length; i++)
                {
                    int root = Find(i);
                    if (!rootToGroup.TryGetValue(root, out int group))
                    {
                        group = rootToGroup.Count;
                        rootToGroup.Add(root, group);
                    }
                    VertexGroups[i] = group;
                }
                ComponentCount = rootToGroup.Count;

                int Find(int vertex)
                {
                    while (parents[vertex] != vertex)
                    {
                        parents[vertex] = parents[parents[vertex]];
                        vertex = parents[vertex];
                    }
                    return vertex;
                }

                void Union(int vertex1, int vertex2)
                {
                    int root1 = Find(vertex1);
                    int root2 = Find(vertex2);
                    if (root1 != root2)
                    {
                        parents[root2] = root1;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Reduces indexed mesh geometry to the 16-bit vertex limit used by Mass Effect meshes.
    /// </summary>
    public static class MeshDecimator
    {
        public const int MaxSupportedVertexCount = ushort.MaxValue;

        public static IReadOnlyList<MeshLodVertexLimitInfo> GetOversizedLods(ObjectBinary mesh, string meshName)
        {
            List<MeshLodVertexLimitInfo> oversizedLods = [];
            switch (mesh)
            {
                case SkeletalMesh skeletalMesh:
                    for (int i = 0; i < skeletalMesh.LODModels.Length; i++)
                    {
                        StaticLODModel lod = skeletalMesh.LODModels[i];
                        int vertexCount = Math.Max(checked((int)lod.NumVertices), lod.VertexBufferGPUSkin?.VertexData?.Length ?? 0);
                        if (vertexCount > MaxSupportedVertexCount)
                        {
                            oversizedLods.Add(new MeshLodVertexLimitInfo(meshName, i, vertexCount));
                        }
                    }
                    break;
                case StaticMesh staticMesh:
                    for (int i = 0; i < staticMesh.LODModels.Length; i++)
                    {
                        StaticMeshRenderData lod = staticMesh.LODModels[i];
                        int vertexCount = Math.Max(checked((int)lod.NumVertices), lod.PositionVertexBuffer?.VertexData?.Length ?? 0);
                        if (vertexCount > MaxSupportedVertexCount)
                        {
                            oversizedLods.Add(new MeshLodVertexLimitInfo(meshName, i, vertexCount));
                        }
                    }
                    break;
            }
            return oversizedLods;
        }

        public static void DecimateToVertexLimit(ObjectBinary mesh)
        {
            switch (mesh)
            {
                case SkeletalMesh skeletalMesh:
                    Decimate(skeletalMesh);
                    break;
                case StaticMesh staticMesh:
                    Decimate(staticMesh);
                    break;
                default:
                    throw new ArgumentException($"'{mesh?.GetType().Name ?? "null"}' is not a supported mesh binary.", nameof(mesh));
            }
        }

        private static void Decimate(StaticMesh mesh)
        {
            for (int lodIndex = 0; lodIndex < mesh.LODModels.Length; lodIndex++)
            {
                StaticMeshRenderData lod = mesh.LODModels[lodIndex];
                int sourceVertexCount = Math.Max(checked((int)lod.NumVertices), lod.PositionVertexBuffer?.VertexData?.Length ?? 0);
                if (sourceVertexCount <= MaxSupportedVertexCount)
                {
                    continue;
                }

                Vector3[] positions = lod.PositionVertexBuffer?.VertexData
                                      ?? throw new InvalidOperationException($"Static mesh LOD {lodIndex} has no position vertex buffer.");
                int[][] sectionIndices = GetStaticSectionIndices(lod);
                MeshDecimationResult result = DecimateGeometry(positions, sectionIndices, MaxSupportedVertexCount);
                if (result.SourceVertexIndices.Length == 0)
                {
                    throw new InvalidOperationException($"Static mesh LOD {lodIndex} contains no usable triangles.");
                }

                lod.PositionVertexBuffer.VertexData = result.Positions;
                lod.PositionVertexBuffer.NumVertices = (uint)result.Positions.Length;
                lod.VertexBuffer.VertexData = Select(lod.VertexBuffer.VertexData, result.SourceVertexIndices);
                lod.VertexBuffer.NumVertices = (uint)result.Positions.Length;

                if (lod.ColorVertexBuffer?.VertexData?.Length == positions.Length)
                {
                    lod.ColorVertexBuffer.VertexData = Select(lod.ColorVertexBuffer.VertexData, result.SourceVertexIndices);
                    lod.ColorVertexBuffer.NumVertices = (uint)result.Positions.Length;
                }
                else if (lod.ColorVertexBuffer != null)
                {
                    lod.ColorVertexBuffer.VertexData = [];
                    lod.ColorVertexBuffer.NumVertices = 0;
                }

                if (lod.ShadowExtrusionVertexBuffer?.VertexData?.Length == positions.Length)
                {
                    lod.ShadowExtrusionVertexBuffer.VertexData = Select(lod.ShadowExtrusionVertexBuffer.VertexData, result.SourceVertexIndices);
                    lod.ShadowExtrusionVertexBuffer.NumVertices = (uint)result.Positions.Length;
                }
                else if (lod.ShadowExtrusionVertexBuffer != null)
                {
                    lod.ShadowExtrusionVertexBuffer.VertexData = [];
                    lod.ShadowExtrusionVertexBuffer.NumVertices = 0;
                }

                List<ushort> newIndexBuffer = [];
                uint firstIndex = 0;
                for (int sectionIndex = 0; sectionIndex < lod.Elements.Length; sectionIndex++)
                {
                    int[] indices = result.SectionIndices[sectionIndex];
                    StaticMeshElement element = lod.Elements[sectionIndex];
                    element.FirstIndex = firstIndex;
                    element.NumTriangles = (uint)(indices.Length / 3);
                    if (indices.Length > 0)
                    {
                        element.MinVertexIndex = (uint)indices.Min();
                        element.MaxVertexIndex = (uint)indices.Max();
                    }
                    else
                    {
                        element.MinVertexIndex = 0;
                        element.MaxVertexIndex = 0;
                    }
                    element.Fragments = [new FragmentRange((int)firstIndex, indices.Length / 3)];
                    newIndexBuffer.AddRange(indices.Select(index => checked((ushort)index)));
                    firstIndex += (uint)indices.Length;
                }

                lod.NumVertices = (uint)result.Positions.Length;
                lod.IndexBuffer = [.. newIndexBuffer];
                lod.RawTriangles = [];
                lod.WireframeIndexBuffer = [];
                lod.Edges = [];
                lod.ShadowTriangleDoubleSided = [];
                lod.AdjacencyIndexBuffer = [];
            }

            RebuildStaticMeshCollisionTree(mesh);
        }

        private static int[][] GetStaticSectionIndices(StaticMeshRenderData lod)
        {
            int[][] sections = new int[lod.Elements.Length][];
            for (int sectionIndex = 0; sectionIndex < lod.Elements.Length; sectionIndex++)
            {
                StaticMeshElement element = lod.Elements[sectionIndex];
                int firstIndex = checked((int)element.FirstIndex);
                int indexCount = checked((int)element.NumTriangles * 3);
                sections[sectionIndex] = lod.IndexBuffer.Skip(firstIndex).Take(indexCount).Select(index => (int)index).ToArray();
            }
            return sections;
        }

        private static void RebuildStaticMeshCollisionTree(StaticMesh mesh)
        {
            if (mesh.LODModels.Length == 0 || mesh.LODModels[0].PositionVertexBuffer?.VertexData == null)
            {
                return;
            }

            StaticMeshRenderData lod = mesh.LODModels[0];
            List<kDOPCollisionTriangle> triangles = [];
            foreach (StaticMeshElement element in lod.Elements)
            {
                int endIndex = checked((int)(element.FirstIndex + element.NumTriangles * 3));
                for (int i = checked((int)element.FirstIndex); i < endIndex; i += 3)
                {
                    triangles.Add(new kDOPCollisionTriangle(lod.IndexBuffer[i], lod.IndexBuffer[i + 1], lod.IndexBuffer[i + 2], (ushort)element.MaterialIndex));
                }
            }
            mesh.kDOPTreeME3UDKLE = KDOPTreeBuilder.ToCompact([.. triangles], lod.PositionVertexBuffer.VertexData);
        }

        private static void Decimate(SkeletalMesh mesh)
        {
            for (int lodIndex = 0; lodIndex < mesh.LODModels.Length; lodIndex++)
            {
                StaticLODModel lod = mesh.LODModels[lodIndex];
                int sourceVertexCount = Math.Max(checked((int)lod.NumVertices), lod.VertexBufferGPUSkin?.VertexData?.Length ?? 0);
                if (sourceVertexCount <= MaxSupportedVertexCount)
                {
                    continue;
                }

                GPUSkinVertex[] vertices = lod.VertexBufferGPUSkin?.VertexData
                                           ?? throw new InvalidOperationException($"Skeletal mesh LOD {lodIndex} has no GPU skin vertex buffer.");
                int[] vertexChunks = GetVertexChunks(lod, vertices.Length);
                int[][] sectionIndices = GetSkeletalSectionIndices(lod);
                MeshDecimationResult result = DecimateGeometry(
                    vertices.Select(vertex => vertex.Position).ToArray(), sectionIndices,
                    MaxSupportedVertexCount, vertexChunks);
                if (result.SourceVertexIndices.Length == 0)
                {
                    throw new InvalidOperationException($"Skeletal mesh LOD {lodIndex} contains no usable triangles.");
                }

                GPUSkinVertex[] newVertices = new GPUSkinVertex[result.SourceVertexIndices.Length];
                for (int i = 0; i < newVertices.Length; i++)
                {
                    newVertices[i] = vertices[result.SourceVertexIndices[i]];
                    newVertices[i].Position = result.Positions[i];
                }

                int[] chunkRemap = Enumerable.Repeat(-1, lod.Chunks.Length).ToArray();
                List<SkelMeshChunk> newChunks = [];
                for (int oldChunkIndex = 0; oldChunkIndex < lod.Chunks.Length; oldChunkIndex++)
                {
                    int[] newVertexIndices = Enumerable.Range(0, result.SourceVertexIndices.Length)
                        .Where(index => vertexChunks[result.SourceVertexIndices[index]] == oldChunkIndex).ToArray();
                    if (newVertexIndices.Length == 0)
                    {
                        continue;
                    }

                    SkelMeshChunk oldChunk = lod.Chunks[oldChunkIndex];
                    int rigidVertexCount = newVertexIndices.Count(index => CountInfluences(newVertices[index]) <= 1);
                    int maxInfluences = newVertexIndices.Max(index => CountInfluences(newVertices[index]));
                    chunkRemap[oldChunkIndex] = newChunks.Count;
                    newChunks.Add(new SkelMeshChunk
                    {
                        BaseVertexIndex = (uint)newVertexIndices[0],
                        RigidVertices = [],
                        SoftVertices = [],
                        BoneMap = oldChunk.BoneMap.ToArray(),
                        NumRigidVertices = rigidVertexCount,
                        NumSoftVertices = newVertexIndices.Length - rigidVertexCount,
                        MaxBoneInfluences = maxInfluences
                    });
                }

                List<ushort> newIndexBuffer = [];
                List<SkelMeshSection> newSections = [];
                for (int sectionIndex = 0; sectionIndex < lod.Sections.Length; sectionIndex++)
                {
                    int[] indices = result.SectionIndices[sectionIndex];
                    if (indices.Length == 0)
                    {
                        continue;
                    }

                    SkelMeshSection oldSection = lod.Sections[sectionIndex];
                    int newChunkIndex = chunkRemap[oldSection.ChunkIndex];
                    if (newChunkIndex < 0)
                    {
                        throw new InvalidOperationException("A decimated skeletal mesh section lost its vertex chunk.");
                    }
                    newSections.Add(new SkelMeshSection
                    {
                        BaseIndex = (uint)newIndexBuffer.Count,
                        ChunkIndex = (ushort)newChunkIndex,
                        MaterialIndex = oldSection.MaterialIndex,
                        NumTriangles = indices.Length / 3,
                        TriangleSorting = oldSection.TriangleSorting
                    });
                    newIndexBuffer.AddRange(indices.Select(index => checked((ushort)index)));
                }

                lod.Sections = [.. newSections];
                lod.IndexBuffer = [.. newIndexBuffer];
                lod.Chunks = [.. newChunks];
                lod.NumVertices = (uint)newVertices.Length;
                lod.VertexBufferGPUSkin.VertexData = newVertices;
                lod.ME1VertexBufferGPUSkin = null;
                lod.RawPointIndices = lod.RawPointIndices?.Length == vertices.Length
                    ? Select(lod.RawPointIndices, result.SourceVertexIndices)
                    : [];
                lod.ShadowIndices = [];
                lod.ShadowTriangleDoubleSided = [];
                lod.Edges = [];
            }
        }

        private static int CountInfluences(GPUSkinVertex vertex)
        {
            int count = 0;
            for (int i = 0; i < 4; i++)
            {
                if (vertex.InfluenceWeights[i] > 0)
                {
                    count++;
                }
            }
            return count;
        }

        private static int[] GetVertexChunks(StaticLODModel lod, int vertexCount)
        {
            int[] vertexChunks = new int[vertexCount];
            for (int chunkIndex = 0; chunkIndex < lod.Chunks.Length; chunkIndex++)
            {
                int start = checked((int)lod.Chunks[chunkIndex].BaseVertexIndex);
                int end = chunkIndex + 1 < lod.Chunks.Length
                    ? checked((int)lod.Chunks[chunkIndex + 1].BaseVertexIndex)
                    : vertexCount;
                for (int vertexIndex = start; vertexIndex < end; vertexIndex++)
                {
                    vertexChunks[vertexIndex] = chunkIndex;
                }
            }
            return vertexChunks;
        }

        private static int[][] GetSkeletalSectionIndices(StaticLODModel lod)
        {
            int[][] sections = new int[lod.Sections.Length][];
            for (int sectionIndex = 0; sectionIndex < lod.Sections.Length; sectionIndex++)
            {
                SkelMeshSection section = lod.Sections[sectionIndex];
                int firstIndex = checked((int)section.BaseIndex);
                int indexCount = checked(section.NumTriangles * 3);
                sections[sectionIndex] = lod.IndexBuffer.Skip(firstIndex).Take(indexCount).Select(index => (int)index).ToArray();
            }
            return sections;
        }

        internal static MeshDecimationResult DecimateGeometry(
            IReadOnlyList<Vector3> positions,
            IReadOnlyList<int[]> sectionIndices,
            int targetVertexCount,
            IReadOnlyList<int> vertexGroups = null,
            int minimumVerticesPerGroup = 0)
        {
            ArgumentNullException.ThrowIfNull(positions);
            ArgumentNullException.ThrowIfNull(sectionIndices);
            if (targetVertexCount < 3)
            {
                throw new ArgumentOutOfRangeException(nameof(targetVertexCount));
            }
            if (vertexGroups != null && vertexGroups.Count != positions.Count)
            {
                throw new ArgumentException("The vertex group count must match the position count.", nameof(vertexGroups));
            }
            if (minimumVerticesPerGroup < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumVerticesPerGroup));
            }

            bool[] referenced = new bool[positions.Count];
            double[] vertexWeights = new double[positions.Count];
            HashSet<ulong> uniqueEdges = [];
            List<(int Vertex1, int Vertex2)> topologyEdges = [];
            foreach (int[] indices in sectionIndices)
            {
                if (indices.Length % 3 != 0)
                {
                    throw new ArgumentException("Section index counts must be divisible by three.", nameof(sectionIndices));
                }
                for (int i = 0; i < indices.Length; i += 3)
                {
                    int a = indices[i];
                    int b = indices[i + 1];
                    int c = indices[i + 2];
                    ValidateIndex(a, positions.Count);
                    ValidateIndex(b, positions.Count);
                    ValidateIndex(c, positions.Count);
                    referenced[a] = referenced[b] = referenced[c] = true;
                    double triangleArea = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]).Length() * 0.5;
                    if (double.IsFinite(triangleArea) && triangleArea > 0)
                    {
                        double vertexArea = triangleArea / 3;
                        vertexWeights[a] += vertexArea;
                        vertexWeights[b] += vertexArea;
                        vertexWeights[c] += vertexArea;
                    }
                    AddEdge(a, b);
                    AddEdge(b, c);
                    AddEdge(c, a);
                }
            }

            int[] parent = Enumerable.Range(0, positions.Count).ToArray();
            int[] clusterSizes = Enumerable.Repeat(1, positions.Count).ToArray();
            int[] clusterVersions = new int[positions.Count];
            Vector3[] clusterPositions = positions.ToArray();
            int[] representatives = Enumerable.Range(0, positions.Count).ToArray();
            int clusterCount = referenced.Count(value => value);
            double smallestPositiveWeight = Enumerable.Range(0, positions.Count)
                .Where(index => referenced[index] && vertexWeights[index] > 0)
                .Select(index => vertexWeights[index]).DefaultIfEmpty(1).Min();
            for (int i = 0; i < vertexWeights.Length; i++)
            {
                if (referenced[i] && (!(vertexWeights[i] > 0) || !double.IsFinite(vertexWeights[i])))
                {
                    vertexWeights[i] = smallestPositiveWeight;
                }
            }
            double[] clusterWeights = vertexWeights.ToArray();
            Dictionary<int, int> groupClusterCounts = vertexGroups == null
                ? []
                : Enumerable.Range(0, positions.Count).Where(index => referenced[index])
                    .GroupBy(index => vertexGroups[index]).ToDictionary(group => group.Key, group => group.Count());

            HashSet<int>[] neighbors = new HashSet<int>[positions.Count];
            PriorityQueue<DecimationCandidate, DecimationPriority> candidates = new();
            HashSet<(int Root1, int Root2, int Version1, int Version2)> queuedCandidates = [];
            foreach ((int vertex1, int vertex2) in topologyEdges)
            {
                AddNeighbor(vertex1, vertex2);
            }
            foreach ((int vertex1, int vertex2) in topologyEdges)
            {
                EnqueueCandidate(vertex1, vertex2);
            }
            CollapseQueuedCandidates();

            if (clusterCount > targetVertexCount)
            {
                // Disconnected triangle islands have no shared edges. Join spatially adjacent
                // roots within the same vertex group so the hard limit is still guaranteed.
                IEnumerable<IGrouping<int, int>> rootsByGroup = Enumerable.Range(0, positions.Count)
                    .Where(index => referenced[index] && Find(index) == index)
                    .GroupBy(index => vertexGroups?[representatives[index]] ?? 0);
                foreach (IGrouping<int, int> group in rootsByGroup)
                {
                    int[] roots = group.OrderBy(index => clusterPositions[index].X)
                        .ThenBy(index => clusterPositions[index].Y)
                        .ThenBy(index => clusterPositions[index].Z).ToArray();
                    for (int i = 1; i < roots.Length; i++)
                    {
                        AddNeighbor(roots[i - 1], roots[i]);
                        EnqueueCandidate(roots[i - 1], roots[i]);
                    }
                }
                CollapseQueuedCandidates();
            }

            if (clusterCount > targetVertexCount)
            {
                throw new InvalidOperationException($"The mesh could not be decimated to {targetVertexCount:N0} vertices without merging incompatible vertex groups.");
            }

            List<(int Root1, int Root2, int Root3)[]> rootTriangles = [];
            foreach (int[] indices in sectionIndices)
            {
                List<(int, int, int)> triangles = [];
                HashSet<(int, int, int)> uniqueTriangles = [];
                for (int i = 0; i < indices.Length; i += 3)
                {
                    int root1 = Find(indices[i]);
                    int root2 = Find(indices[i + 1]);
                    int root3 = Find(indices[i + 2]);
                    if (root1 == root2 || root2 == root3 || root3 == root1)
                    {
                        continue;
                    }
                    int[] sortedRoots = [root1, root2, root3];
                    Array.Sort(sortedRoots);
                    if (!uniqueTriangles.Add((sortedRoots[0], sortedRoots[1], sortedRoots[2])))
                    {
                        continue;
                    }
                    triangles.Add((root1, root2, root3));
                }
                rootTriangles.Add([.. triangles]);
            }

            int[] orderedRoots = Enumerable.Range(0, positions.Count)
                .Where(index => referenced[index] && Find(index) == index)
                .OrderBy(root => vertexGroups?[representatives[root]] ?? 0)
                .ThenBy(root => representatives[root]).ToArray();
            Dictionary<int, int> rootToNewIndex = orderedRoots.Select((root, index) => (root, index))
                .ToDictionary(pair => pair.root, pair => pair.index);
            int[][] remappedSections = rootTriangles.Select(triangles => triangles.SelectMany(triangle => new[]
            {
                rootToNewIndex[triangle.Root1], rootToNewIndex[triangle.Root2], rootToNewIndex[triangle.Root3]
            }).ToArray()).ToArray();

            return new MeshDecimationResult(
                orderedRoots.Select(root => representatives[root]).ToArray(),
                orderedRoots.Select(root => clusterPositions[root]).ToArray(),
                remappedSections);

            void AddEdge(int vertex1, int vertex2)
            {
                if (vertex1 == vertex2 || (vertexGroups != null && vertexGroups[vertex1] != vertexGroups[vertex2]))
                {
                    return;
                }
                ulong key = GetEdgeKey(vertex1, vertex2);
                if (uniqueEdges.Add(key))
                {
                    topologyEdges.Add((vertex1, vertex2));
                }
            }

            void AddNeighbor(int vertex1, int vertex2)
            {
                neighbors[vertex1] ??= [];
                neighbors[vertex2] ??= [];
                neighbors[vertex1].Add(vertex2);
                neighbors[vertex2].Add(vertex1);
            }

            int Find(int vertex)
            {
                while (parent[vertex] != vertex)
                {
                    parent[vertex] = parent[parent[vertex]];
                    vertex = parent[vertex];
                }
                return vertex;
            }

            void EnqueueCandidate(int vertex1, int vertex2)
            {
                int root1 = Find(vertex1);
                int root2 = Find(vertex2);
                if (root1 == root2)
                {
                    return;
                }
                if (vertexGroups != null && vertexGroups[representatives[root1]] != vertexGroups[representatives[root2]])
                {
                    return;
                }
                int group = vertexGroups?[representatives[root1]] ?? 0;
                if (minimumVerticesPerGroup > 0 && groupClusterCounts[group] <= minimumVerticesPerGroup)
                {
                    return;
                }
                if (root2 < root1)
                {
                    (root1, root2) = (root2, root1);
                }
                var identity = (root1, root2, clusterVersions[root1], clusterVersions[root2]);
                if (!queuedCandidates.Add(identity))
                {
                    return;
                }
                double combinedWeight = clusterWeights[root1] + clusterWeights[root2];
                double mergeCost = Vector3.DistanceSquared(clusterPositions[root1], clusterPositions[root2])
                                   * clusterWeights[root1] * clusterWeights[root2] / combinedWeight;
                candidates.Enqueue(
                    new DecimationCandidate(root1, root2, clusterVersions[root1], clusterVersions[root2]),
                    new DecimationPriority(mergeCost, GetEdgeKey(root1, root2)));
            }

            void CollapseQueuedCandidates()
            {
                while (clusterCount > targetVertexCount && candidates.TryDequeue(out DecimationCandidate candidate, out _))
                {
                    queuedCandidates.Remove((candidate.Vertex1, candidate.Vertex2, candidate.Version1, candidate.Version2));
                    int root1 = Find(candidate.Vertex1);
                    int root2 = Find(candidate.Vertex2);
                    if (root1 == root2)
                    {
                        continue;
                    }
                    if (root1 != candidate.Vertex1 || root2 != candidate.Vertex2
                        || clusterVersions[root1] != candidate.Version1 || clusterVersions[root2] != candidate.Version2)
                    {
                        EnqueueCandidate(root1, root2);
                        continue;
                    }
                    Collapse(root1, root2);
                }
            }

            void Collapse(int root1, int root2)
            {
                if (clusterSizes[root1] < clusterSizes[root2])
                {
                    (root1, root2) = (root2, root1);
                }
                int group = vertexGroups?[representatives[root1]] ?? 0;
                HashSet<int> mergedNeighbors = [];
                if (neighbors[root1] != null)
                {
                    mergedNeighbors.UnionWith(neighbors[root1].Select(Find));
                }
                if (neighbors[root2] != null)
                {
                    mergedNeighbors.UnionWith(neighbors[root2].Select(Find));
                }
                mergedNeighbors.Remove(root1);
                mergedNeighbors.Remove(root2);

                int combinedSize = clusterSizes[root1] + clusterSizes[root2];
                double combinedWeight = clusterWeights[root1] + clusterWeights[root2];
                Vector3 mergedPosition = (clusterPositions[root1] * (float)clusterWeights[root1]
                                          + clusterPositions[root2] * (float)clusterWeights[root2]) / (float)combinedWeight;
                int representative1 = representatives[root1];
                int representative2 = representatives[root2];
                representatives[root1] = Vector3.DistanceSquared(positions[representative1], mergedPosition)
                    <= Vector3.DistanceSquared(positions[representative2], mergedPosition)
                        ? representative1
                        : representative2;
                clusterPositions[root1] = mergedPosition;
                clusterSizes[root1] = combinedSize;
                clusterWeights[root1] = combinedWeight;
                parent[root2] = root1;
                clusterVersions[root1]++;
                clusterVersions[root2]++;
                clusterCount--;
                if (vertexGroups != null)
                {
                    groupClusterCounts[group]--;
                }

                neighbors[root1] = mergedNeighbors;
                neighbors[root2] = null;
                foreach (int neighbor in mergedNeighbors)
                {
                    neighbors[neighbor] ??= [];
                    neighbors[neighbor].Remove(root1);
                    neighbors[neighbor].Remove(root2);
                    neighbors[neighbor].Add(root1);
                    EnqueueCandidate(root1, neighbor);
                }
            }
        }

        private static ulong GetEdgeKey(int vertex1, int vertex2)
        {
            uint min = (uint)Math.Min(vertex1, vertex2);
            uint max = (uint)Math.Max(vertex1, vertex2);
            return ((ulong)min << 32) | max;
        }

        private static void ValidateIndex(int index, int vertexCount)
        {
            if ((uint)index >= vertexCount)
            {
                throw new InvalidOperationException($"Mesh index {index} is outside its {vertexCount:N0}-vertex buffer.");
            }
        }

        private static T[] Select<T>(IReadOnlyList<T> source, IReadOnlyList<int> indices)
        {
            T[] result = new T[indices.Count];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = source[indices[i]];
            }
            return result;
        }

        private readonly struct DecimationCandidate
        {
            public int Vertex1 { get; }
            public int Vertex2 { get; }
            public int Version1 { get; }
            public int Version2 { get; }

            public DecimationCandidate(int vertex1, int vertex2, int version1, int version2)
            {
                Vertex1 = vertex1;
                Vertex2 = vertex2;
                Version1 = version1;
                Version2 = version2;
            }
        }

        private readonly struct DecimationPriority : IComparable<DecimationPriority>
        {
            private double Cost { get; }
            private ulong Key { get; }

            public DecimationPriority(double cost, ulong key)
            {
                Cost = cost;
                Key = key;
            }

            public int CompareTo(DecimationPriority other)
            {
                int costComparison = Cost.CompareTo(other.Cost);
                return costComparison != 0 ? costComparison : Key.CompareTo(other.Key);
            }
        }
    }

    internal sealed class MeshDecimationResult
    {
        public int[] SourceVertexIndices { get; }
        public Vector3[] Positions { get; }
        public int[][] SectionIndices { get; }

        public MeshDecimationResult(int[] sourceVertexIndices, Vector3[] positions, int[][] sectionIndices)
        {
            SourceVertexIndices = sourceVertexIndices;
            Positions = positions;
            SectionIndices = sectionIndices;
        }
    }
}
