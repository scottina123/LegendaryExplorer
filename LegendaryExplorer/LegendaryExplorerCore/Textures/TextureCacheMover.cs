using System;
using System.IO;
using System.Linq;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Classes;

namespace LegendaryExplorerCore.Textures
{
    /// <summary>Relocates cooked mip payloads without decoding pixels or rebuilding mipmaps.</summary>
    public static class TextureCacheMover
    {
        public static bool IsBakedLighting(ExportEntry export) =>
            export.ClassName is "LightMapTexture2D" or "ShadowMapTexture2D";

        /// <summary>
        /// Appends to the destination cache, preserving its GUID and existing offsets.
        /// Keeps the six smallest resident mips for ordinary texture streaming. Baked lighting
        /// also permits moving its top mip when there are six or fewer mips (including single-mip
        /// shadow maps), without changing NeverStream or the lightmap's streaming flags.
        /// Lighting already using an external cache retains its existing resident/external split.
        /// Returns the number of relocated mips; zero leaves the export and cache unchanged.
        /// </summary>
        public static int MoveToCache(ExportEntry export, string targetPath)
        {
            if (!export.IsTexture() || export.Game <= MEGame.ME1)
                throw new ArgumentException("A ME2/ME3/LE texture is required.", nameof(export));

            var binary = (UTexture2D)ObjectBinary.From(export);
            var props = export.GetProperties();
            string sourceCache = props.GetProp<NameProperty>("TextureFileCacheName")?.Value.Name;
            bool lighting = IsBakedLighting(export);
            bool externalLighting = lighting && binary.Mips.Any(m => m.StorageType != StorageTypes.empty && !m.IsLocallyStored);
            int residentStart = Math.Max(0, binary.Mips.Count(m => m.StorageType != StorageTypes.empty) - 6);
            int populatedIndex = 0;
            var toMove = binary.Mips.Where(m =>
            {
                if (m.StorageType == StorageTypes.empty) return false;
                bool move = !m.IsLocallyStored || (!externalLighting
                    && (populatedIndex < residentStart || (lighting && populatedIndex == 0)));
                populatedIndex++;
                return move;
            }).ToList();
            if (toMove.Count == 0) return 0;

            // Read every source before opening the destination, including when both caches are the same.
            var payloads = toMove.Select(m => m.IsLocallyStored ? m.Mip : Texture2D.GetTextureData(export.Game, m.Mip,
                m.StorageType, false, m.UncompressedSize, m.CompressedSize, m.DataOffset,
                sourceCache, packagePathForLocalLookup: export.FileRef.FilePath)).ToList();
            if (payloads.Where((data, i) => data.Length != toMove[i].CompressedSize).Any())
                throw new InvalidDataException("Cooked mip payload size does not match its header.");

            bool existingCache = File.Exists(targetPath);
            using var stream = new FileStream(targetPath, existingCache ? FileMode.Open : FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            if (!existingCache) stream.WriteGuid(Guid.NewGuid());
            if (stream.Length < 16) throw new InvalidDataException("The destination TFC has no valid GUID header.");
            stream.Position = 0;
            Guid cacheGuid = stream.ReadGuid();
            long originalLength = stream.Length;
            if (originalLength + payloads.Sum(data => (long)data.Length) > int.MaxValue)
                throw new IOException("The destination TFC would exceed the supported 2 GiB offset range.");

            byte[] originalExport = export.Data;
            try
            {
                stream.Position = originalLength;
                for (int i = 0; i < toMove.Count; i++)
                {
                    var mip = toMove[i];
                    mip.DataOffset = checked((int)stream.Position);
                    stream.Write(payloads[i]);
                    mip.StorageType |= (StorageTypes)StorageFlags.externalFile;
                    mip.Mip = [];
                }
                stream.Flush();
                props.AddOrReplaceProp(new NameProperty(Path.GetFileNameWithoutExtension(targetPath), "TextureFileCacheName"));
                props.AddOrReplaceProp(cacheGuid.ToGuidStructProp("TFCFileGuid"));
                if (!lighting) props.RemoveNamedProperty("NeverStream");
                export.WritePropertiesAndBinary(props, binary);
            }
            catch
            {
                export.Data = originalExport;
                stream.SetLength(originalLength);
                throw;
            }
            return toMove.Count;
        }
    }
}
