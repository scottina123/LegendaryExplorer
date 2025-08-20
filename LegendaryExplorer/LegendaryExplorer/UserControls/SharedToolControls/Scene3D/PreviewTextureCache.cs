using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using SharpDX.Direct3D11;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Texture2D = SharpDX.Direct3D11.Texture2D;

namespace LegendaryExplorer.UserControls.SharedToolControls.Scene3D
{
    /// <summary>
    /// Loads and caches textures for a Direct3D11 renderer
    /// </summary>
    public class PreviewTextureCache : IDisposable
    {
        /// <summary>
        /// Stores a texture and load state in the cache.
        /// </summary>
        public class TextureEntry : IDisposable
        {
            /// <summary>
            /// Texture export for this cache entry
            /// </summary>
            //public ExportEntry TextureExport { get; set; }
            public string InstanceFullPath { get; }

            /// <summary>
            /// The Direct3D ShaderResourceView for binding to shaders.
            /// </summary>
            public readonly ShaderResourceView TextureView;

            /// <summary>
            /// The Direct3D texture for ShaderResourceView creation.
            /// </summary>
            public readonly Texture2D Texture;

            /// <summary>
            /// The time this object was last accessed.
            /// </summary>
            public DateTime LastUsageTime = DateTime.Now;

            public readonly bool IsTextureCube;

            /// <summary>
            /// Creates a new cache entry for the given texture.
            /// </summary>
            public TextureEntry(RenderContext renderContext, ExportEntry export)
            {
                MemoryAnalyzer.AddTrackedMemoryItem($"PreviewTexture {export.ObjectName}", new WeakReference(this));
                InstanceFullPath = export.InstancedFullPath;
                IsTextureCube = export.ClassName == "TextureCube";

                Texture = IsTextureCube ? renderContext.LoadUnrealTextureCube(export) : renderContext.LoadUnrealTexture(export);
                TextureView = new ShaderResourceView(renderContext.Device, Texture);
            }

            /// <summary>
            /// Disposes <see cref="TextureView"/> and <see cref="Texture"/> if they have been loaded.
            /// </summary>
            public void Dispose()
            {
                TextureView?.Dispose();
                Texture?.Dispose();
            }
        }

        public RenderContext RenderContext { get; }

        /// <summary>
        /// Creates a new PreviewTextureCache.
        /// </summary>
        /// <param name="renderContext">The <see cref="RenderContext"/> to create texture and views for.</param>
        public PreviewTextureCache(RenderContext renderContext)
        {
            RenderContext = renderContext;
        }

        /// <summary>
        /// Removes items from the cache that are over 1 minute old
        /// </summary>
        public void ExpungeStaleCacheItems()
        {
            TimeSpan oneMinute = TimeSpan.FromMinutes(1);
            foreach (var (key, entry) in AssetCache)
            {
                if (DateTime.Now - entry.LastUsageTime > oneMinute)
                {
                    entry.Dispose();
                    //Remove does not actually invalidate the enumerator
                    AssetCache.Remove(key);
                }
            }
        }

        /// <summary>
        /// Disposes all the textures and resource views.
        /// </summary>
        public void Dispose()
        {
            AssetCache.DisposeValuesAndClear();
        }

        /// <summary>
        /// Stores loaded textures by their full name.
        /// </summary>
        private Dictionary<string, TextureEntry> AssetCache { get; } = new();

        /// <summary>
        /// Queues a texture for eventual loading.
        /// </summary>
        public TextureEntry LoadTexture(IEntry textureEntry, PackageCache packageCache = null)
        {
            string ifp = textureEntry.InstancedFullPath;
            if (textureEntry is ImportEntry import)
            {
                textureEntry = EntryImporter.ResolveImport(import, packageCache);
            }
            if (textureEntry is ExportEntry textureExport)
            {
                if (AssetCache.TryGetValue(textureExport.InstancedFullPath, out TextureEntry entry))
                {
                    entry.LastUsageTime = DateTime.Now;
                    return entry;
                }
                try
                {
                    entry = new TextureEntry(RenderContext, textureExport);
                    AssetCache.Add(entry.InstanceFullPath, entry);
                    return entry;
                }
                catch
                {
                    //just do the error path below
                }
            }
            Debug.WriteLine($"Unable to resolve texture: {ifp}");
            return null;
        }
    }
}
