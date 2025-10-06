
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using System.Numerics;
using LegendaryExplorerCore.Gammtek;
using SharpDX.Direct3D11;
using StaticMesh = LegendaryExplorerCore.Unreal.BinaryConverters.StaticMesh;
using SkeletalMesh = LegendaryExplorerCore.Unreal.BinaryConverters.SkeletalMesh;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Classes;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

// MODEL RENDERING OVERVIEW:
// Construct a ModelPreview instance with an existing SkeletalMesh or StaticMesh.
// Call ModelPreview.Render(...) every frame. Boom.
namespace LegendaryExplorer.UserControls.SharedToolControls.Scene3D;

/// <summary>
/// Stores the material information of triangles in a <see cref="ModelPreviewLOD"/> mesh.
/// </summary>
public struct ModelPreviewSection
{
    /// <summary>
    /// The name of the material to be applied to the triangles in this section.
    /// </summary>
    public string MaterialName;

    /// <summary>
    /// The first index into the LOD mesh index buffer that this section describes.
    /// </summary>
    public uint StartIndex;

    /// <summary>
    /// How many triangles, starting from the vertex at <see cref="StartIndex"/>, that this section describes.
    /// </summary>
    public uint TriangleCount;

    /// <summary>
    /// Constructs a new MaterialPreviewSection.
    /// </summary>
    /// <param name="materialname">The name of the material to be applied to the triangles in this section.</param>
    /// <param name="startindex">The first index into the LOD mesh index buffer that this section describes.</param>
    /// <param name="trianglecount">How many triangles, starting from the vertex at <see cref="StartIndex"/>, that this section describes.</param>
    public ModelPreviewSection(string materialname, uint startindex, uint trianglecount)
    {
        MaterialName = materialname;
        StartIndex = startindex;
        TriangleCount = trianglecount;
    }
}

/// <summary>
/// Stores the geometry and the associated material information for a single level-of-detail in a <see cref="ModelPreview"/>.
/// </summary>
public class ModelPreviewLOD
{
    /// <summary>
    /// The geometry of this level of detail.
    /// </summary>
    public MeshElement Mesh;

    /// <summary>
    /// A list of which materials are applied to which triangles.
    /// </summary>
    public List<ModelPreviewSection> Sections;

    /// <summary>
    /// Creates a new ModelPreviewLOD.
    /// </summary>
    /// <param name="mesh">The geometry of this level of detail.</param>
    /// <param name="sections">A list of which materials are applied to which triangles.</param>
    public ModelPreviewLOD(MeshElement mesh, List<ModelPreviewSection> sections)
    {
        Mesh = mesh;
        Sections = sections;
    }
}

public enum RenderPass
{
    //material types
    Base,
    Hair,
    
    //special types
    Collision,

    //override, most always be last
    ANY
}

/// <summary>
/// ModelPreviewMaterial is responsible for rendering sections of meshes.
/// </summary>
public class ModelPreviewMaterial : IDisposable
{
    private readonly RenderTargetBlendDescription BlendDesc;

    public RenderPass Pass;

    protected readonly MaterialRenderProxy Material;

    public string InstancedFullPath => Material.InstancedFullPath;

    /// <summary>
    /// A Dictionary of string properties. Useful because some materials have properties that others don't.
    /// </summary>
    public readonly Dictionary<string, string> Properties = [];

    public readonly Dictionary<string, PreviewTextureCache.TextureEntry> TextureMap = [];

    /// <summary>
    /// The IFP of the best guess at the diffuse texture. Only used by the fallback shader
    /// </summary>
    public string DiffuseTextureFullName = null;
    private string matPackage = null;

    /// <summary>
    /// Creates a ModelPreviewMaterial that renders as close to what the given <see cref="MaterialInstanceConstant"/> looks like as possible. 
    /// </summary>
    public ModelPreviewMaterial(MeshRenderContext renderContext, ExportEntry export)
    {
        Material = new MaterialRenderProxy(renderContext, export);
        if (export.Parent != null)
        {
            matPackage = export.Parent.InstancedFullPath.ToLower();
        }
        Properties.Add("Name", export.ObjectName.Instanced);
        foreach (IEntry textureEntry in Material.Textures)
        {
            if (!TextureMap.ContainsKey(textureEntry.FullPath))
            {
                PreviewTextureCache.TextureEntry texture = renderContext.TextureCache.LoadTexture(textureEntry, renderContext.PackageCache);
                if (texture is not null)
                {
                    TextureMap.Add(textureEntry.FullPath, texture);
                }
            }
        }

        Material.TextureMap = TextureMap;
        Pass = Material.UseHairPass ? RenderPass.Hair : default;
        BlendDesc = Material.BlendMode switch
        {
            EBlendMode.BLEND_Opaque => new RenderTargetBlendDescription
            {
                RenderTargetWriteMask = ColorWriteMaskFlags.All,
                BlendOperation = BlendOperation.Add,
                AlphaBlendOperation = BlendOperation.Add,
                SourceBlend = BlendOption.One,
                DestinationBlend = BlendOption.Zero,
                SourceAlphaBlend = BlendOption.One,
                DestinationAlphaBlend = BlendOption.Zero,
                IsBlendEnabled = false
            },
            EBlendMode.BLEND_Masked => new RenderTargetBlendDescription
            {
                RenderTargetWriteMask = ColorWriteMaskFlags.All,
                BlendOperation = BlendOperation.Add,
                AlphaBlendOperation = BlendOperation.Add,
                SourceBlend = BlendOption.One,
                DestinationBlend = BlendOption.Zero,
                SourceAlphaBlend = BlendOption.One,
                DestinationAlphaBlend = BlendOption.Zero,
                IsBlendEnabled = false
            },
            EBlendMode.BLEND_Translucent => new RenderTargetBlendDescription
            {
                RenderTargetWriteMask = ColorWriteMaskFlags.All,
                BlendOperation = BlendOperation.Add,
                AlphaBlendOperation = BlendOperation.Add,
                SourceBlend = BlendOption.SourceAlpha,
                DestinationBlend = BlendOption.InverseSourceAlpha,
                SourceAlphaBlend = BlendOption.SourceAlphaSaturate,
                DestinationAlphaBlend = BlendOption.InverseSourceAlpha,
                IsBlendEnabled = true
            },
            //TODO: the ones above this comment seem to work properly, but the rest need verifying
            EBlendMode.BLEND_Additive => new RenderTargetBlendDescription
            {
                RenderTargetWriteMask = ColorWriteMaskFlags.All,
                BlendOperation = BlendOperation.Add,
                AlphaBlendOperation = BlendOperation.Add,
                SourceBlend = BlendOption.One,
                DestinationBlend = BlendOption.One,
                SourceAlphaBlend = BlendOption.Zero,
                DestinationAlphaBlend = BlendOption.One,
                IsBlendEnabled = true
            },
            EBlendMode.BLEND_Modulate => new RenderTargetBlendDescription
            {
                RenderTargetWriteMask = ColorWriteMaskFlags.All,
                BlendOperation = BlendOperation.Add,
                AlphaBlendOperation = BlendOperation.Add,
                SourceBlend = BlendOption.DestinationColor,
                DestinationBlend = BlendOption.Zero,
                SourceAlphaBlend = BlendOption.Zero,
                DestinationAlphaBlend = BlendOption.One,
                IsBlendEnabled = true
            },
            EBlendMode.BLEND_SoftMasked => new RenderTargetBlendDescription
            {
                RenderTargetWriteMask = ColorWriteMaskFlags.All,
                BlendOperation = BlendOperation.Add,
                AlphaBlendOperation = BlendOperation.Add,
                SourceBlend = BlendOption.SourceAlpha,
                DestinationBlend = BlendOption.InverseSourceAlpha,
                SourceAlphaBlend = BlendOption.Zero,
                DestinationAlphaBlend = BlendOption.InverseSourceAlpha,
                IsBlendEnabled = true
            },
            EBlendMode.BLEND_AlphaComposite => new RenderTargetBlendDescription
            {
                RenderTargetWriteMask = ColorWriteMaskFlags.All,
                BlendOperation = BlendOperation.Add,
                AlphaBlendOperation = BlendOperation.Add,
                SourceBlend = BlendOption.One,
                DestinationBlend = BlendOption.InverseSourceAlpha,
                SourceAlphaBlend = BlendOption.One,
                DestinationAlphaBlend = BlendOption.InverseSourceAlpha,
                IsBlendEnabled = false
            },
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private void FindDiffuse()
    {
        MaterialInstanceConstant mat = Material;
        foreach (var textureEntry in mat.Textures)
        {
            var texObjectName = textureEntry.InstancedFullPath.ToLower();
            if ((matPackage == null || texObjectName.StartsWith(matPackage)) && texObjectName.Contains("diff"))
            {
                // we have found the diffuse texture!
                DiffuseTextureFullName = textureEntry.InstancedFullPath;
                Debug.WriteLine("Diffuse texture of new material <" + Properties["Name"] + "> is " + DiffuseTextureFullName);
                return;
            }
        }

        foreach (var textureEntry in mat.Textures)
        {
            var texObjectName = textureEntry.ObjectName.Name.ToLower();
            if (texObjectName.Contains("diff") || texObjectName.Contains("tex"))
            {
                // we have found the diffuse texture!
                DiffuseTextureFullName = textureEntry.InstancedFullPath;
                Debug.WriteLine("Diffuse texture of new material <" + Properties["Name"] + "> is " + DiffuseTextureFullName);
                return;
            }
        }
        foreach (var texparam in mat.Textures)
        {
            var texObjectName = texparam.ObjectName.Name.ToLower();

            if (texObjectName.Contains("detail"))
            {
                // I guess a detail texture is good enough if we didn't return for a diffuse texture earlier...
                DiffuseTextureFullName = texparam.InstancedFullPath;
                Debug.WriteLine("Diffuse (Detail) texture of new material <" + Properties["Name"] + "> is " + DiffuseTextureFullName);
                return;
            }
        }
        foreach (var texparam in mat.Textures)
        {
            var texObjectName = texparam.ObjectName.Name.ToLower();
            if (!texObjectName.Contains("norm") && !texObjectName.Contains("opac"))
            {
                //Anything is better than nothing I suppose
                DiffuseTextureFullName = texparam.InstancedFullPath;
                Debug.WriteLine("Using first found texture (last resort)  of new material <" + Properties["Name"] + "> as diffuse: " + DiffuseTextureFullName);
                return;
            }
        }
        DiffuseTextureFullName = "";
    }

    /// <summary>
    /// Uses LEX's default shader to render the given <see cref="ModelPreviewSection"/> of a <see cref="ModelPreviewLOD"/>. 
    /// </summary>
    /// <param name="lod">The LOD to render.</param>
    /// <param name="s">Which faces to render.</param>
    public void RenderSection(ModelPreviewLOD lod, ModelPreviewSection s, MeshRenderContext context)
    {
        context.DefaultEffect.PrepDraw(context.ImmediateContext, context.AlphaBlendState, context.GetWorldConstants(lod.Mesh.LocalToWorld));

        if (DiffuseTextureFullName is null)
        {
            //lazily do this, as it's not needed if we aren't using the default shader
            FindDiffuse();
        }
        TextureMap.TryGetValue(DiffuseTextureFullName, out PreviewTextureCache.TextureEntry diffTexture);
        ShaderResourceView diffTextureView = diffTexture?.TextureView ?? context.DefaultTextureView;

        context.DefaultEffect.RenderObject(
            context.ImmediateContext,
            lod.Mesh,
            (int)s.StartIndex,
            (int)s.TriangleCount * 3,
            context.Wireframe ? null : diffTextureView);
    }

    /// <summary>
    /// Renders the given <see cref="ModelPreviewSection"/> of a <see cref="ModelPreviewLOD"/> using the game's shader. 
    /// </summary>
    /// <param name="lod">The LOD to render.</param>
    /// <param name="s">Which faces to render.</param>
    /// <param name="context"></param>
    public void RenderSectionWithGameShader(ModelPreviewLOD lod, ModelPreviewSection s, MeshRenderContext context)
    {
        MeshElement mesh = lod.Mesh;
        var mat = Material;
        LEEffect effect = context.LEEffect;
        VertexShader vs;
        PixelShader ps;
        InputLayout inputLayout;

        if (mat.UnrealPixelShader is null || mat.UnrealVertexShader is null)
        {
            return;
        }

        ps = context.GetCachedPixelShader(mat.UnrealPixelShader.Guid, mat.UnrealPixelShader.ShaderByteCode);
        (vs, inputLayout) = context.GetCachedVertexShader(mat.UnrealVertexShader.Guid, mat.UnrealVertexShader.ShaderByteCode);

        effect.PrepDraw(context.ImmediateContext, vs, ps, inputLayout, context.GetCachedBlendState(BlendDesc));

        mat.UpdateShaderParams(effect.VertexShaderConstantBuffer, effect.PixelShaderConstantBuffer, context, mesh);

        effect.RenderObject(context.ImmediateContext, mesh, (int)s.StartIndex, (int)s.TriangleCount * 3);
    }

    public void Dispose()
    {

    }
}

/// <summary>
/// Contains all the necessary resources (minus textures, which are cached in a <see cref="PreviewTextureCache"/>) needed to render a static preview of <see cref="SkeletalMesh"/> or <see cref="StaticMesh"/> instances.  
/// </summary>
public class ModelPreview : IDisposable
{
    /// <summary>
    /// Contains the geometry and section information for each level-of-detail in the model.
    /// </summary>
    public List<ModelPreviewLOD> LODs { get; } = [];

    /// <summary>
    /// Stores materials for this preview, stored by material name.
    /// </summary>
    public Dictionary<string, ModelPreviewMaterial> Materials { get; } = [];

    /// <summary>
    /// Creates a preview of a generic untextured mesh
    /// </summary>
    public ModelPreview(MeshRenderContext renderContext, MeshElement mesh, PreloadedModelData preloadedData = null)
    {
        //Preloaded
        var sections = new List<ModelPreviewSection>();
        if (preloadedData != null)
        {
            sections = preloadedData.sections;
            foreach (ExportEntry mat in preloadedData.Materials.Distinct())
            {
                AddMaterial(renderContext, mat);
            }
        }
        LODs.Add(new ModelPreviewLOD(mesh, sections));
    }

    /// <summary>
    /// Creates a preview of the given <see cref="StaticMesh"/>.
    /// </summary>
    public ModelPreview(MeshRenderContext renderContext, StaticMesh m, int selectedLOD)
    {
        if (selectedLOD < 0)  //PREVIEW BUG WORKAROUND
            return;

        // STEP 1: MESH
        var lodModel = m.LODModels[selectedLOD];
        var triangles = new List<Triangle>(lodModel.IndexBuffer.Length / 3);
        var vertices = new List<LEVertex>((int)lodModel.NumVertices);
        // Gather all the vertex data
        // Only one LOD? odd but I guess that's just how it rolls.

        StaticMeshVertexBuffer vertexBuffer = lodModel.VertexBuffer;
        for (int i = 0; i < lodModel.NumVertices; i++)
        {
            var position = lodModel.PositionVertexBuffer.VertexData[i];
            var vertex = vertexBuffer.VertexData[i];
            Fixed4<Vector4> uvs = default;
            if (vertexBuffer.bUseFullPrecisionUVs)
            {
                for (int j = 0; j < uvs.Length && j < vertex.FullPrecisionUVs.Length; j++)
                {
                    uvs[j] = new Vector4(vertex.FullPrecisionUVs[j], 0, 0);
                }
            }
            else
            {
                for (int j = 0; j < uvs.Length && j < vertex.HalfPrecisionUVs.Length; j++)
                {
                    uvs[j] = new Vector4(vertex.HalfPrecisionUVs[j], 0, 0);
                }
            }
            vertices.Add(LEVertex.Create(new Vector3(position.X, position.Y, position.Z), (Vector3)vertex.TangentX, (Vector4)vertex.TangentZ, uvs));
        }

        //OLD CODE
        //for (int i = 0; i < m.L.Vertices.Points.Count; i++)
        //{
        //    // Note the reversal of the Z and Y coordinates. Unreal seems to think that Z should be up.
        //    vertices.Add(new Scene3D.WorldVertex(new SharpDX.Vector3(-m.Mesh.Vertices.Points[i].X, m.Mesh.Vertices.Points[i].Z, m.Mesh.Vertices.Points[i].Y), SharpDX.Vector3.Zero, new SharpDX.Vector2(m.Mesh.Edges.UVSet[i].UVs[0].X, m.Mesh.Edges.UVSet[i].UVs[0].Y)));
        //}

        // Sometimes there might not be an index buffer.
        // If there is one, use that. 
        // Otherwise, assume that each vertex is used exactly once.
        // Note that this is based on the earlier implementation which didn't take LODs into consideration, which is odd considering that both the hit testing and the skeletalmesh class do.
        if (lodModel.IndexBuffer.Length > 0)
        {
            // Hey, we have indices all set up for us. How considerate.
            for (int i = 0; i < lodModel.IndexBuffer.Length; i += 3)
            {
                triangles.Add(new Triangle(lodModel.IndexBuffer[i], lodModel.IndexBuffer[i + 1], lodModel.IndexBuffer[i + 2]));
            }
        }
        else
        {
            // Gather all the vertex data from the raw triangles, not the Mesh.Vertices.Point list.
            if (m.Export.Game <= MEGame.ME2)
            {
                var kdop = m.kDOPTreeME1ME2;
                for (int i = 0; i < kdop.Triangles.Length; i++)
                {
                    triangles.Add(new Triangle(kdop.Triangles[i].Vertex1, kdop.Triangles[i].Vertex2, kdop.Triangles[i].Vertex3));
                }
            }
            else
            {
                var kdop = m.kDOPTreeME3UDKLE;
                for (int i = 0; i < kdop.Triangles.Length; i++)
                {
                    triangles.Add(new Triangle(kdop.Triangles[i].Vertex1, kdop.Triangles[i].Vertex2, kdop.Triangles[i].Vertex3));
                }
            }
        }

        // STEP 3: SECTIONS

        var sections = new List<ModelPreviewSection>();
        foreach (var element in lodModel.Elements)
        {
            if (element.Material is not 0)
            {
                IEntry matEntry = m.Export.FileRef.GetEntry(element.Material);
                AddMaterial(renderContext, matEntry);
                sections.Add(new ModelPreviewSection(matEntry.InstancedFullPath, element.FirstIndex, element.NumTriangles));
            }
        }
        LODs.Add(new ModelPreviewLOD(new MeshElement(renderContext.Device, triangles, vertices), sections));
    }

    /// <summary>
    /// Creates a preview of the given <see cref="SkeletalMesh"/>.
    /// </summary>
    public ModelPreview(MeshRenderContext renderContext, SkeletalMesh m)
    {
        var mats = new string[m.Materials.Length];
        // STEP 1: MATERIALS
        for (int i = 0; i < m.Materials.Length; i++)
        {
            int materialUIndex = m.Materials[i];
            if (materialUIndex is not 0)
            {
                IEntry matEntry = m.Export.FileRef.GetEntry(materialUIndex);
                mats[i] = matEntry.InstancedFullPath;
                AddMaterial(renderContext, matEntry);
            }
        }

        // STEP 2: LODS
        foreach (var lodmodel in m.LODModels)
        {
            // Vertices
            var vertices = new List<LEVertex>(m.Export.Game == MEGame.ME1 ? lodmodel.ME1VertexBufferGPUSkin.Length : lodmodel.VertexBufferGPUSkin.VertexData.Length);
            Fixed4<Vector4> uvs = default;
            if (m.Export.Game == MEGame.ME1)
            {
                foreach (SoftSkinVertex vertex in lodmodel.ME1VertexBufferGPUSkin)
                {
                    uvs[0] = new Vector4(vertex.UV, 0, 0);
                    vertices.Add(LEVertex.Create(new Vector3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z), (Vector3)vertex.TangentX, (Vector4)vertex.TangentZ, uvs));
                }
            }
            else
            {
                foreach (GPUSkinVertex vertex in lodmodel.VertexBufferGPUSkin.VertexData)
                {
                    uvs[0] = new Vector4(vertex.UV, 0, 0);
                    vertices.Add(LEVertex.Create(new Vector3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z), (Vector3)vertex.TangentX, (Vector4)vertex.TangentZ, uvs));
                }
            }
            // Triangles
            var triangles = new List<Triangle>(lodmodel.IndexBuffer.Length / 3);
            for (int i = 0; i < lodmodel.IndexBuffer.Length; i += 3)
            {
                triangles.Add(new Triangle(lodmodel.IndexBuffer[i], lodmodel.IndexBuffer[i + 1], lodmodel.IndexBuffer[i + 2]));
            }
            var mesh = new MeshElement(renderContext.Device, triangles, vertices);
            // Sections
            var sections = new List<ModelPreviewSection>();
            foreach (var section in lodmodel.Sections)
            {
                if (section.MaterialIndex < Materials.Count)
                {
                    sections.Add(new ModelPreviewSection(mats[section.MaterialIndex], section.BaseIndex, (uint)section.NumTriangles));
                }
            }
            LODs.Add(new ModelPreviewLOD(mesh, sections));
        }
    }

    /// <summary>
    /// Adds a <see cref="ModelPreviewMaterial"/> to this model, or adds another reference of any conflicting material.
    /// </summary>
    private ModelPreviewMaterial AddMaterial(MeshRenderContext renderContext, IEntry matEntry)
    {
        string ifp = matEntry.InstancedFullPath;
        if (!Materials.TryGetValue(ifp, out var mat))
        {
            mat = renderContext.GetCachedMaterial(matEntry);
            if (mat is not null)
            {
                Materials.Add(ifp, mat);
            }
        }
        return mat;
    }

    /// <summary>
    /// Renders the ModelPreview at the specified level of detail, with the in-game shader
    /// </summary>
    /// <param name="renderPass">Only render materials that use this pass.</param>
    /// <param name="view">The SceneRenderControl to render the preview into.</param>
    /// <param name="lod">Which level of detail to render at. Level 0 is traditionally the most detailed.</param>
    public void RenderWithGameShader(RenderPass renderPass, MeshRenderContext view, int lod)
    {
        if (lod >= LODs.Count) return;

        foreach (ModelPreviewSection section in LODs[lod].Sections)
        {
            if (Materials.TryGetValue(section.MaterialName, out ModelPreviewMaterial material)
                && (material.Pass == renderPass || renderPass is RenderPass.ANY))
            {
                material.RenderSectionWithGameShader(LODs[lod], section, view);
            }
        }
    }

    /// <summary>
    /// Renders the ModelPreview at the specified level of detail, with the fallback shader
    /// </summary>
    /// <param name="renderPass">Only render materials that use this pass.</param>
    /// <param name="view">The SceneRenderControl to render the preview into.</param>
    /// <param name="lod">Which level of detail to render at. Level 0 is traditionally the most detailed.</param>
    public void Render(RenderPass renderPass, MeshRenderContext view, int lod)
    {
        if (lod >= LODs.Count) return;

        foreach (ModelPreviewSection section in LODs[lod].Sections)
        {
            if (Materials.TryGetValue(section.MaterialName, out ModelPreviewMaterial material)
                && (material.Pass == renderPass || renderPass is RenderPass.ANY))
            {
                material.RenderSection(LODs[lod], section, view);
            }
        }
    }

    public void UpdateLocalToWorld(Matrix4x4 ltw)
    {
        foreach (var lod in LODs)
        {
            lod.Mesh.LocalToWorld = ltw;
        }
    }

    /// <summary>
    /// Disposes any outstanding resources.
    /// </summary>
    public void Dispose()
    {
        foreach (ModelPreviewMaterial mat in Materials.Values)
        {
            mat.Dispose();
        }
        Materials.Clear();
        foreach (ModelPreviewLOD lod in LODs)
        {
            lod.Mesh.Dispose();
        }
        LODs.Clear();
    }
}

public class PreloadedModelData
{
    public object meshObject;
    public List<ModelPreviewSection> sections;
    public List<IEntry> Materials;

    public static PreloadedModelData LoadModel(ExportEntry export, PackageCache assetCache)
    {
        List<string> alreadyLoadedImportMaterials = new();
        var modelComp = ObjectBinary.From<Model>(export);
        var pmd = new PreloadedModelData
        {
            meshObject = modelComp,
            sections = [],
            Materials = [],
        };
        foreach (var mcExp in modelComp.Export.FileRef.Exports.Where(x =>
            x.ClassName == "ModelComponent" && !x.IsDefaultObject))
        {
            var mc = ObjectBinary.From<ModelComponent>(mcExp);
            if (mc.Model == modelComp.Self)
            {
                foreach (var element in mc.Elements)
                {
                    if (export == null) return pmd;
                    if (export.FileRef.IsUExport(element.Material))
                    {
                        ExportEntry entry = export.FileRef.GetUExport(element.Material);
                        AddMaterial(pmd.Materials, entry);
                    }
                    else if (export.FileRef.TryGetImport(element.Material, out var matImp) &&
                             alreadyLoadedImportMaterials.All(x => x != matImp.InstancedFullPath))
                    {
                        var extMaterialExport = EntryImporter.ResolveImport(matImp, assetCache);
                        if (extMaterialExport != null)
                        {
                            AddMaterial(pmd.Materials, extMaterialExport);
                            alreadyLoadedImportMaterials.Add(extMaterialExport.InstancedFullPath);
                        }
                        else
                        {
                            Debug.WriteLine("Could not find import material from FModelElement.");
                            Debug.WriteLine("Import material: " +
                                            export.FileRef.GetEntryString(element.Material));
                        }
                    }
                }
            }
        }
        return pmd;
    }
    public static PreloadedModelData LoadModelComponent(ExportEntry export, PackageCache assetCache)
    {
        var modelComp = ObjectBinary.From<ModelComponent>(export);
        var pmd = new PreloadedModelData
        {
            meshObject = modelComp,
            sections = [],
            Materials = [],
        };

        foreach (var element in modelComp.Elements)
        {
            if (export != null)
            {
                if (export.FileRef.TryGetUExport(element.Material, out var matExp))
                {
                    AddMaterial(pmd.Materials, matExp);
                    pmd.sections.Add(new ModelPreviewSection(matExp.InstancedFullPath, 0, 3)); //???
                }
                else if (export.FileRef.TryGetImport(element.Material, out var matImp))
                {
                    var extMaterialExport = EntryImporter.ResolveImport(matImp, assetCache);
                    //var extMaterialExport = ModelPreview.FindExternalAsset(matImp, pmd.texturePreviewMaterials.Select(x => x.Mip.Export).ToList(), cachedPackages);
                    if (extMaterialExport != null)
                    {
                        AddMaterial(pmd.Materials, extMaterialExport);
                    }
                    else
                    {
                        Debug.WriteLine("Could not find import material from section.");
                        Debug.WriteLine("Import material: " + export.FileRef.GetEntryString(element.Material));
                    }
                }
            }
        }
        return pmd;
    }

    private static void AddMaterial(List<IEntry> texturePreviewMaterials, ExportEntry entry)
    {
        if (texturePreviewMaterials.Any(x => x.InstancedFullPath == entry.InstancedFullPath))
            return; //already cached

        texturePreviewMaterials.Add(entry);
    }
}
