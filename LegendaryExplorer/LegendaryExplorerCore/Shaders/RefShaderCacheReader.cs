using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using JetBrains.Annotations;
using LegendaryExplorerCore.DebugTools;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Gammtek.IO;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using LegendaryExplorerCore.Unreal.Collections;

namespace LegendaryExplorerCore.Shaders
{
    /*
     * This class is for reading each game's reference shader cache. Because the ShaderCache in those files is so large,
     * parsing it with the ShaderCache ObjectBinary class is very slow and uses an enormous amount of memory.
     * This class parses only what it needs to, and then caches file offsets to make subsequent reads even faster
     */
    public static class RefShaderCacheReader
    {
        public static string ShaderCacheName(MEGame game) => game.IsLEGame() ? "RefShaderCache-PC-D3D-SM5.upk" : "RefShaderCache-PC-D3D-SM3.upk";

        public static string ShaderFilePath(MEGame game, string gamePathOverride = null)
        {
            var cookedPath = MEDirectories.GetCookedPath(game, gamePathOverride);
            if (cookedPath == null)
            {
                LECLog.Error(@"Cannot determine game path - cannot lookup shader file path");
                return null; // We cannot find the game!
            }
            return Path.Combine(cookedPath, ShaderCacheName(game));
        }

        private static Dictionary<Guid, int> ME3ShaderOffsets;
        private static Dictionary<Guid, int> ME2ShaderOffsets;
        private static Dictionary<Guid, int> ME1ShaderOffsets;

        private static Dictionary<Guid, int> LE3ShaderOffsets;
        private static Dictionary<Guid, int> LE2ShaderOffsets;
        private static Dictionary<Guid, int> LE1ShaderOffsets;

        private static int[] OffsetOfShaderTypeCRCMap = new int[7];
        private static int[] OffsetOfVertexFactoryTypeCRCMap = new int[7];
        private static long[] CachedRefShaderCacheSize = new long[7];

        private static Dictionary<Guid, int> ShaderOffsets(MEGame game) => game switch
        {
            MEGame.ME3 => ME3ShaderOffsets,
            MEGame.ME2 => ME2ShaderOffsets,
            MEGame.ME1 => ME1ShaderOffsets,
            MEGame.LE3 => LE3ShaderOffsets,
            MEGame.LE2 => LE2ShaderOffsets,
            MEGame.LE1 => LE1ShaderOffsets,
            _ => null
        };

        public static bool IsShaderOffsetsDictInitialized(MEGame game) => ShaderOffsets(game)?.Count > 0;

        private static int ME3MaterialShaderMapsOffset = VanillaMaterialShaderMapsOffset(MEGame.ME3);
        private static int ME2MaterialShaderMapsOffset = VanillaMaterialShaderMapsOffset(MEGame.ME2);
        private static int ME1MaterialShaderMapsOffset = VanillaMaterialShaderMapsOffset(MEGame.ME1);

        private static int LE3MaterialShaderMapsOffset = VanillaMaterialShaderMapsOffset(MEGame.LE3);
        private static int LE2MaterialShaderMapsOffset = VanillaMaterialShaderMapsOffset(MEGame.LE2);
        private static int LE1MaterialShaderMapsOffset = VanillaMaterialShaderMapsOffset(MEGame.LE1);

        private static int VanillaMaterialShaderMapsOffset(MEGame game) => game switch
        {
            MEGame.ME3 => 206341927,
            MEGame.ME2 => 132795914,
            MEGame.ME1 => 69550225,
            MEGame.LE3 => 1263553925,
            MEGame.LE2 => 1014140890,
            MEGame.LE1 => 720539980,
            _ => 0
        };

        private static long ME3RefShaderCacheSize = 232355586;
        private static long ME2RefShaderCacheSize = 151649957;
        private static long ME1RefShaderCacheSize = 76618220;

        private static long LE3RefShaderCacheSize = 1296009525;
        private static long LE2RefShaderCacheSize = 1035352391;
        private static long LE1RefShaderCacheSize = 731880291;

        private static long VanillaRefShaderCacheSize(MEGame game) => game switch
        {
            MEGame.ME3 => ME3RefShaderCacheSize,
            MEGame.ME2 => ME2RefShaderCacheSize,
            MEGame.ME1 => ME1RefShaderCacheSize,
            MEGame.LE3 => LE3RefShaderCacheSize,
            MEGame.LE2 => LE2RefShaderCacheSize,
            MEGame.LE1 => LE1RefShaderCacheSize,
            _ => 0
        };

        private static int MaterialShaderMapsOffset(MEGame game, string gamePathOverride)
        {
            return game switch
            {
                MEGame.ME3 => ME3MaterialShaderMapsOffset,
                MEGame.ME2 => ME2MaterialShaderMapsOffset,
                MEGame.ME1 => ME1MaterialShaderMapsOffset,
                MEGame.LE3 => LE3MaterialShaderMapsOffset,
                MEGame.LE2 => LE2MaterialShaderMapsOffset,
                MEGame.LE1 => LE1MaterialShaderMapsOffset,
                _ => 0
            };
        }

        /// <summary>
        /// Reads the texture parameter FNames declared by all compiled materials in the game's
        /// reference shader cache without loading shader bytecode or retaining the shader maps.
        /// ME1 and ME2 store named uniforms in individual Material exports instead of these maps.
        /// Missing caches and games without named uniforms in shader maps return an empty list.
        /// </summary>
        public static IReadOnlyList<NameReference> GetTextureParameterNames(MEGame game, string gamePathOverride = null)
        {
            if (game is not (MEGame.ME3 or MEGame.LE1 or MEGame.LE2 or MEGame.LE3))
                return [];

            return GetTextureParameterNamesFromFile(game, ShaderFilePath(game, gamePathOverride));
        }

        internal static IReadOnlyList<NameReference> GetTextureParameterNamesFromFile(MEGame game, string filePath)
        {
            if (game is not (MEGame.ME3 or MEGame.LE1 or MEGame.LE2 or MEGame.LE3) || !File.Exists(filePath))
                return [];

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 100, FileOptions.SequentialScan);
            using IMEPackage shaderCachePackage = MEPackageHandler.OpenMEPackageFromStream(fs, quickLoad: true);
            if (shaderCachePackage.Game != game || shaderCachePackage.Platform != MEPackage.GamePlatform.PC
                || shaderCachePackage.IsCompressed || shaderCachePackage.ExportCount == 0)
                throw new InvalidDataException("The reference shader cache must be an uncompressed PC package for this game.");

            ReadNames(fs, shaderCachePackage);
            // Use immutable vanilla offsets. Existing helpers can change their cached offsets
            // after visiting a modified cache, so those shared offsets cannot identify this file.
            int mapOffset = fs.Length == VanillaRefShaderCacheSize(game)
                ? VanillaMaterialShaderMapsOffset(game)
                : FindMaterialShaderMapsOffset(fs, shaderCachePackage);
            fs.JumpTo(mapOffset);
            int count = ReadBoundedCount(fs, sizeof(int));
            var sc = new SerializingContainer(fs, shaderCachePackage, true);
            var names = new Dictionary<(string Name, int Number), NameReference>();
            for (int i = 0; i < count; i++)
            {
                StaticParameterSet parameters = null;
                sc.Serialize(ref parameters);
                MaterialShaderMap map = null;
                sc.Serialize(ref map);
                AddTextureParameterNames(map.UniformPixelScalarExpressions, names);
                AddTextureParameterNames(map.UniformPixelVectorExpressions, names);
                AddTextureParameterNames(map.UniformVertexScalarExpressions, names);
                AddTextureParameterNames(map.UniformVertexVectorExpressions, names);
                AddTextureParameterNames(map.Uniform2DTextureExpressions, names);
                AddTextureParameterNames(map.UniformCubeTextureExpressions, names);
            }

            return names.Values.OrderBy(name => name.Instanced, StringComparer.OrdinalIgnoreCase)
                .ThenBy(name => name.Name, StringComparer.OrdinalIgnoreCase).ThenBy(name => name.Number).ToList();
        }

        private static int FindMaterialShaderMapsOffset(FileStream fs, IMEPackage package)
        {
            // The reference cache's first export has a twelve-byte pre-property/None header.
            fs.JumpTo(package.ExportOffset + 36);
            int exportOffset = fs.ReadInt32();
            if (exportOffset < 0 || exportOffset > fs.Length - 13)
                throw new InvalidDataException("The reference shader cache export offset is invalid.");
            fs.JumpTo(exportOffset + 12);
            fs.Skip(1); // shader platform
            SkipNameCrcMap(fs);
            SkipNameCrcMap(fs); // ME3 and LE shader name map
            int shaderCount = ReadBoundedCount(fs, 28);
            for (int i = 0; i < shaderCount; i++)
            {
                fs.Skip(24); // shader type FName and GUID
                int shaderEndOffset = fs.ReadInt32();
                if (shaderEndOffset < fs.Position || shaderEndOffset > fs.Length)
                    throw new InvalidDataException("A shader bytecode end offset is invalid.");
                fs.JumpTo(shaderEndOffset);
            }
            SkipNameCrcMap(fs); // vertex factory CRC map
            return checked((int)fs.Position);
        }

        private static void SkipNameCrcMap(FileStream fs)
        {
            int count = ReadBoundedCount(fs, 12);
            fs.Skip((long)count * 12);
        }

        private static int ReadBoundedCount(FileStream fs, int minimumItemSize)
        {
            int count = fs.ReadInt32();
            if (count < 0 || count > (fs.Length - fs.Position) / minimumItemSize)
                throw new InvalidDataException("The reference shader cache contains an invalid collection count.");
            return count;
        }

        private static void AddTextureParameterNames(IEnumerable<MaterialUniformExpression> expressions,
            Dictionary<(string Name, int Number), NameReference> names)
        {
            if (expressions is null)
                return;
            var pending = new Stack<MaterialUniformExpression>(expressions.Where(expression => expression is not null));
            var visited = new HashSet<MaterialUniformExpression>(ReferenceEqualityComparer.Instance);
            while (pending.TryPop(out MaterialUniformExpression expression))
            {
                if (!visited.Add(expression))
                    continue;
                if (expression is MaterialUniformExpressionTextureParameter parameter
                    && !string.IsNullOrWhiteSpace(parameter.ParameterName.Name))
                {
                    NameReference name = parameter.ParameterName;
                    // NameReference's hash is case-sensitive although FName equality is not.
                    names.TryAdd((name.Name.ToUpperInvariant(), name.Number), name);
                }
                switch (expression)
                {
                    case MaterialUniformExpressionUnaryOp unary:
                        Push(unary.X);
                        break;
                    case MaterialUniformExpressionBinaryOp binary:
                        Push(binary.A);
                        Push(binary.B);
                        break;
                    case MaterialUniformExpressionClamp clamp:
                        Push(clamp.Input);
                        Push(clamp.Min);
                        Push(clamp.Max);
                        break;
                }
            }

            void Push(MaterialUniformExpression expression)
            {
                if (expression is not null)
                    pending.Push(expression);
            }
        }

        public static void PopulateOffsets(MEGame game)
        {
            if (!IsShaderOffsetsDictInitialized(game))
            {
                GetMaterialShaderMap(game, null, out _);
            }
        }

        private static void PopulateOffsets(MEGame game, int offsetOfShaderCacheOffset)
        {
            string filePath = ShaderFilePath(game);
            if (File.Exists(filePath))
            {
                Dictionary<Guid, int> offsetDict = game switch
                {
                    MEGame.ME3 => ME3ShaderOffsets ??= [],
                    MEGame.ME2 => ME2ShaderOffsets ??= [],
                    MEGame.ME1 => ME1ShaderOffsets ??= [],
                    MEGame.LE3 => LE3ShaderOffsets ??= [],
                    MEGame.LE2 => LE2ShaderOffsets ??= [],
                    MEGame.LE1 => LE1ShaderOffsets ??= [],
                    _ => null
                };
                if (offsetDict == null) return;
                lock (offsetDict)
                {
                    long fileSize = File.GetSize(filePath);

                    if (offsetDict.Count > 0 && fileSize == CachedRefShaderCacheSize[(int)game])
                    {
                        return;
                    }
                    //do not change the filestream creation options without testing what effect it has on the runtime of this method
                    //default File.OpenRead is 10x slower than this. (keep in mind OS file caching when testing)
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 100, FileOptions.SequentialScan);
                    fs.JumpTo(offsetOfShaderCacheOffset);
                    int binaryOffset = fs.ReadInt32() + 12;
                    fs.JumpTo(binaryOffset);
                    fs.Skip(1);
                    OffsetOfShaderTypeCRCMap[(int)game] = (int)fs.Position;
                    int nameCount = fs.ReadInt32();
                    fs.Skip(nameCount * 12);
                    if (game is not MEGame.ME2)
                    {
                        if (game is MEGame.ME1)
                        {
                            OffsetOfVertexFactoryTypeCRCMap[(int)game] = (int)fs.Position;
                        }
                        nameCount = fs.ReadInt32();
                        fs.Skip(nameCount * 12);
                    }

                    int shaderCount = fs.ReadInt32();
                    for (int i = 0; i < shaderCount; i++)
                    {
                        fs.Skip(8);
                        Guid shaderGuid = fs.ReadGuid();
                        int shaderEndOffset = fs.ReadInt32();
                        offsetDict.Add(shaderGuid, (int)fs.Position + 2);
                        fs.Skip(shaderEndOffset - fs.Position);
                    }

                    if (game != MEGame.ME1)
                    {
                        OffsetOfVertexFactoryTypeCRCMap[(int)game] = (int)fs.Position;
                        nameCount = fs.ReadInt32();
                        fs.Skip(nameCount * 12);
                    }
                    switch (game)
                    {
                        case MEGame.ME3:
                            ME3MaterialShaderMapsOffset = (int)fs.Position;
                            break;
                        case MEGame.ME2:
                            ME2MaterialShaderMapsOffset = (int)fs.Position;
                            break;
                        case MEGame.ME1:
                            ME1MaterialShaderMapsOffset = (int)fs.Position;
                            break;
                        case MEGame.LE3:
                            LE3MaterialShaderMapsOffset = (int)fs.Position;
                            break;
                        case MEGame.LE2:
                            LE2MaterialShaderMapsOffset = (int)fs.Position;
                            break;
                        case MEGame.LE1:
                            LE1MaterialShaderMapsOffset = (int)fs.Position;
                            break;
                    }
                    CachedRefShaderCacheSize[(int)game] = fileSize;
                }
            }
        }

        public static MaterialShaderMap GetMaterialShaderMap(MEGame game, StaticParameterSet staticParameterSet, out int fileOffset, string gamePathOverride = null)
        {
            fileOffset = -1;
            string filePath = ShaderFilePath(game);
            if (File.Exists(filePath))
            {
                using FileStream fs = File.OpenRead(filePath);
                using IMEPackage shaderCachePackage = MEPackageHandler.OpenMEPackageFromStream(fs, quickLoad: true);
                ReadNames(fs, shaderCachePackage);

                int offsetOfShaderCacheOffset = shaderCachePackage.ExportOffset + 36;
                PopulateOffsets(game, offsetOfShaderCacheOffset);
                if (staticParameterSet is null)
                {
                    return null;
                }

                var sc = new SerializingContainer(fs, shaderCachePackage, true);
                sc.ms.JumpTo(MaterialShaderMapsOffset(game, gamePathOverride));

                int count = fs.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    StaticParameterSet sps = null;
                    sc.Serialize(ref sps);
                    if (sps == staticParameterSet)
                    {
                        fileOffset = sc.FileOffset;
                        MaterialShaderMap msm = null;
                        sc.Serialize(ref msm);
                        return msm;
                    }

                    if (game >= MEGame.ME3)
                    {
                        sc.ms.Skip(8);
                    }

                    int nextMSMOffset = sc.ms.ReadInt32();
                    sc.ms.Skip(nextMSMOffset - sc.ms.Position);
                }
            }

            return null;
        }

        public static string GetD3D9ShaderDissasembly(MEGame game, Guid shaderGuid)
        {
            if (!game.IsMEGame())
            {
                throw new InvalidOperationException("This method is invalid for LE games, as they use D3D11");
            }
            if (!IsShaderOffsetsDictInitialized(game))
            {
                PopulateOffsets(game);
            }
            var offsets = ShaderOffsets(game);
            if (offsets != null && offsets.TryGetValue(shaderGuid, out int offset))
            {
                using FileStream fs = File.OpenRead(ShaderFilePath(game));
                fs.JumpTo(offset);
                int size = fs.ReadInt32();
                ShaderReader.DisassembleShader(fs.ReadToBuffer(size), out string disassembly);
                return disassembly;
            }

            return "";
        }

        public static byte[] GetShaderBytecode(MEGame game, Guid shaderGuid)
        {
            if (!IsShaderOffsetsDictInitialized(game))
            {
                PopulateOffsets(game);
            }
            var offsets = ShaderOffsets(game);
            if (offsets != null && offsets.TryGetValue(shaderGuid, out int offset))
            {
                using FileStream fs = File.OpenRead(ShaderFilePath(game));
                fs.JumpTo(offset);
                int size = fs.ReadInt32();
                return fs.ReadToBuffer(size);
            }

            return null;
        }

        public static void RemoveStaticParameterSetsThatAreInTheGlobalCache(HashSet<StaticParameterSet> paramSets, MEGame game, string gamePathOverride = null)
        {
            string filePath = ShaderFilePath(game);
            if (File.Exists(filePath))
            {
                using FileStream fs = File.OpenRead(filePath);
                //read just the header of the package, then read the name list
                using IMEPackage shaderCachePackage = MEPackageHandler.OpenMEPackageFromStream(fs, quickLoad: true);
                ReadNames(fs, shaderCachePackage);
                var sc = new SerializingContainer(fs, shaderCachePackage, true);


                long fileSize = File.GetSize(filePath);
                //this method does not normally require populating the offsets, as we hardcode the offset to the MaterialShaderMaps.
                //It is only neccesary in the very rare case that the RefShaderCache is in a non-vanilla state
                if (fileSize != CachedRefShaderCacheSize[(int)game] && fileSize != VanillaRefShaderCacheSize(game))
                {
                    int offsetOfShaderCacheOffset = shaderCachePackage.ExportOffset + 36;
                    PopulateOffsets(game, offsetOfShaderCacheOffset);
                }

                sc.ms.JumpTo(MaterialShaderMapsOffset(game, gamePathOverride));

                int count = fs.ReadInt32();
                for (int i = 0; i < count && paramSets.Count > 0; i++)
                {
                    StaticParameterSet sps = null;
                    sc.Serialize(ref sps);
                    if (paramSets.Contains(sps))
                    {
                        paramSets.Remove(sps);
                    }

                    if (game >= MEGame.ME3)
                    {
                        sc.ms.Skip(8);
                    }

                    int nextMSMOffset = sc.ms.ReadInt32();
                    sc.ms.Skip(nextMSMOffset - sc.ms.Position);
                }
            }
        }

        private static void ReadNames(FileStream fs, IMEPackage shaderCachePackage)
        {
            fs.JumpTo(shaderCachePackage.NameOffset);
            var names = new List<string>(shaderCachePackage.NameCount);
            for (int i = 0; i < shaderCachePackage.NameCount; i++)
            {
                var name = fs.ReadUnrealString();
                names.Add(name);
                if (shaderCachePackage.Game == MEGame.ME1 && shaderCachePackage.Platform != MEPackage.GamePlatform.PS3)
                    fs.Skip(8);
                else if (shaderCachePackage.Game == MEGame.ME2 && shaderCachePackage.Platform != MEPackage.GamePlatform.PS3)
                    fs.Skip(4);
            }

            shaderCachePackage.restoreNames(names);
        }

        /// <summary>
        /// Popules the CRC maps of a shader cache. These should always be identical across the shipped game, as the game will try to recompile
        /// shaders if they aren't.
        /// </summary>
        /// <param name="game"></param>
        /// <param name="cache"></param>
        public static void PopulateCRCMaps(MEGame game, ShaderCache cache)
        {
            UMultiMap<NameReference, uint> shaderTypeCRCMap = [];
            UMultiMap<NameReference, uint> vertexFactoryTypeCRCMap = [];

            PopulateOffsets(game);

            string filePath = ShaderFilePath(game);
            if (File.Exists(filePath))
            {
                using FileStream fs = File.OpenRead(filePath);
                //read just the header of the package, then read the name list
                using IMEPackage shaderCachePackage = MEPackageHandler.OpenMEPackageFromStream(fs, quickLoad: true);
                ReadNames(fs, shaderCachePackage);
                var sc = new SerializingContainer(fs, shaderCachePackage, true);

                int offsetOfShaderCacheOffset = shaderCachePackage.ExportOffset + 36;
                PopulateOffsets(game, offsetOfShaderCacheOffset);

                sc.ms.JumpTo(OffsetOfShaderTypeCRCMap[(int)game]);
                sc.Serialize(ref shaderTypeCRCMap, sc.Serialize, sc.Serialize);
                
                sc.ms.JumpTo(OffsetOfVertexFactoryTypeCRCMap[(int)game]);
                sc.Serialize(ref vertexFactoryTypeCRCMap, sc.Serialize, sc.Serialize);
            }

            cache.ShaderTypeCRCMap = shaderTypeCRCMap;
            cache.VertexFactoryTypeCRCMap = vertexFactoryTypeCRCMap;
        }


        [CanBeNull]
        public static Shader[] GetShaders(MEGame game, ICollection<Guid> shaderGuids,
            out UMultiMap<NameReference, uint> shaderTypeCRCMap, out UMultiMap<NameReference, uint> vertexFactoryTypeCRCMap)
        {
            shaderTypeCRCMap = null;
            vertexFactoryTypeCRCMap = null;
            string filePath = ShaderFilePath(game);
            if (File.Exists(filePath))
            {
                using FileStream fs = File.OpenRead(filePath);
                //read just the header of the package, then read the name list
                using IMEPackage shaderCachePackage = MEPackageHandler.OpenMEPackageFromStream(fs, quickLoad: true);
                ReadNames(fs, shaderCachePackage);
                var sc = new SerializingContainer(fs, shaderCachePackage, true);

                int offsetOfShaderCacheOffset = shaderCachePackage.ExportOffset + 36;
                PopulateOffsets(game, offsetOfShaderCacheOffset);

                sc.ms.JumpTo(OffsetOfShaderTypeCRCMap[(int)game]);
                sc.Serialize(ref shaderTypeCRCMap, sc.Serialize, sc.Serialize);

                Dictionary<Guid, int> offsets = ShaderOffsets(game); //0x1E

                var shaders = new Shader[shaderGuids.Count];

                int i = 0;
                foreach (Guid shaderGuid in shaderGuids)
                {
                    if (offsets.TryGetValue(shaderGuid, out int offset))
                    {
                        sc.ms.JumpTo(offset - 0x1E); //offset is to the bytecode, not the start of the shader structure.
                        sc.Serialize(ref shaders[i]);
                    }
                    ++i;
                }
                sc.ms.JumpTo(OffsetOfVertexFactoryTypeCRCMap[(int)game]);
                sc.Serialize(ref vertexFactoryTypeCRCMap, sc.Serialize, sc.Serialize);
                return shaders;
            }
            return null;
        }
    }
}
