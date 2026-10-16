using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Classes;
using TextureImage = LegendaryExplorerCore.Textures.Image;

namespace LegendaryExplorer.UserControls.ExportLoaderControls.Scaleform;

/// <summary>Resolves GFx external images through the movie's Unreal texture references.</summary>
internal sealed class ScaleformTextureResolver
{
    private readonly Dictionary<string, ExportEntry> textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource> cache = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; } = new();

    public ScaleformTextureResolver(ExportEntry movie)
    {
        var references = movie.GetProperty<ArrayProperty<ObjectProperty>>("References");
        if (references != null)
            foreach (var reference in references)
                if (movie.FileRef.GetEntry(reference.Value) is ExportEntry { ClassName: "Texture2D" } texture)
                    Add(texture);
        foreach (var texture in movie.FileRef.Exports.Where(e => e.ClassName == "Texture2D" && !e.IsDefaultObject && e.idxLink == movie.idxLink)) Add(texture);
    }

    private void Add(ExportEntry texture)
    {
        textures.TryAdd(texture.ObjectName.Instanced, texture);
        textures.TryAdd(texture.ObjectName.Name, texture);
    }

    public ImageSource Resolve(string fileName)
    {
        string key = Path.GetFileNameWithoutExtension(fileName.Replace('\\', '/'));
        if (cache.TryGetValue(key, out var existing)) return existing;
        ImageSource image = null;
        if (textures.TryGetValue(key, out var texture))
        {
            try
            {
                if (texture.FileRef is ILazyLoadPackage lazy) lazy.LoadExport(texture.UIndex);
                var unreal = new Texture2D(texture);
                var requested = unreal.Mips.FirstOrDefault(mip => mip.storageType != StorageTypes.empty)
                    ?? throw new InvalidDataException("The texture has no available mip.");
                byte[] encoded = unreal.GetImageBytesForMip(requested, texture.Game, true, out var used);
                int width = used.width, height = used.height;
                byte[] pixels = TextureImage.convertRawToARGB(encoded, ref width, ref height, TextureImage.getPixelFormatType(unreal.TextureFormat));
                BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, checked(width * 4));
                if (width != used.width || height != used.height) bitmap = new CroppedBitmap(bitmap, new Int32Rect(0, 0, used.width, used.height));
                bitmap.Freeze(); image = bitmap;
            }
            catch (Exception ex) { Warnings.Add($"Could not preview {key}: {ex.Message}"); }
        }
        else Warnings.Add($"Shared image {fileName} is not available in this package.");
        cache[key] = image;
        return image;
    }
}
