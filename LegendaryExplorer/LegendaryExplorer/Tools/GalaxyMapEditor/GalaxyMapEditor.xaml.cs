using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorer.UserControls.Interfaces;
using LegendaryExplorerCore.Gammtek;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.SharpDX;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Classes;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Tools.GalaxyMapEditor;

public enum GalaxyMapLevel
{
    Galaxy,
    Cluster,
    System,
    Planet
}

/// <summary>
/// Draws billboard icons for every galaxy map object in the viewport so that
/// objects without mesh components are still visible and clickable.
/// </summary>
public sealed class GalaxyMapIconOverlay : LevelEditor.UIElement
{
    private const int CircleSegments = 20;
    private const float OuterRadius = 10f;
    private const float InnerRadius = 8.5f;
    private const float ClusterTextureRadius = 12f;
    private const float PlanetTextureRadius = 11.5f;

    private static readonly Vector4 OutlineColor = new(0f, 0f, 0f, 1f);
    private static readonly Vector4 ClusterColor = new(0.2f, 0.6f, 1f, 0.9f);
    private static readonly Vector4 SystemColor = new(1f, 0.85f, 0.2f, 0.9f);
    private static readonly Vector4 PlanetColor = new(0.3f, 0.9f, 0.4f, 0.9f);
    private static readonly Vector4 SunColor = new(1f, 0.55f, 0.1f, 0.95f);
    private static readonly Vector4 RelayColor = new(0.7f, 0.3f, 1f, 0.9f);
    private static readonly Vector4 DefaultColor = new(0.7f, 0.7f, 0.7f, 0.9f);
    private static readonly Vector4 SelectedHighlight = new(1f, 1f, 0.2f, 1f);

    public ActorProxy SelectedActor;
    public PreviewTextureCache.TextureEntry ClusterIconTexture;
    public Mesh<WorldVertex> ClusterIconMesh;

    public override void Draw(LevelEditorRenderContext context)
    {
        foreach (ActorProxy actor in context.DrawList_3D)
        {
            DrawIcon(context, actor);
        }
    }

    private void DrawIcon(LevelEditorRenderContext context, ActorProxy actor)
    {
        Vector4 screenPoint = context.WorldToScreen(actor.LocalToWorld.Translation);
        if (screenPoint.W <= 0f)
            return;

        float scale = screenPoint.W * (4f / context.Width / context.Camera.ProjectionMatrix[0, 0]);
        Vector3 right = context.Camera.CameraRight * scale;
        Vector3 up = context.Camera.CameraUp * scale;
        Vector3 center = actor.LocalToWorld.Translation;

        if (!context.WorldToPixel(center, out Vector2 pixel))
            return;

        int hitId = actor.HitID;
        Vector4 fillColor = GetColorForActor(actor);
        bool isSelected = actor == SelectedActor;
        if (isSelected)
            fillColor = SelectedHighlight;

        if (actor is GalaxyMapObjectProxy gmo && ClusterIconMesh is not null)
        {
            if (gmo.MapLevel is GalaxyMapLevel.Cluster or GalaxyMapLevel.System && ClusterIconTexture is not null)
            {
                DrawClusterIcon(context, center, right, up, hitId);
                if (isSelected)
                {
                    DrawDisk(context, center, right, up, OuterRadius + 2f, SelectedHighlight with { W = 0.75f }, hitId);
                }
            }
            else if (gmo.MapLevel == GalaxyMapLevel.Planet
                     && gmo.HasSharedPlanetMesh)
            {
                // Shared mesh planets can be difficult to hit-test directly due to mesh/texture setup.
                // Draw a very faint billboard disk to provide a reliable selection target.
                DrawDisk(context, center, right, up, OuterRadius, OutlineColor with { W = 0.03f }, hitId);
                DrawDisk(context, center, right, up, InnerRadius, fillColor with { W = 0.02f }, hitId);
                if (isSelected)
                {
                    DrawDisk(context, center, right, up, OuterRadius + 2f, SelectedHighlight with { W = 0.75f }, hitId);
                }
            }
            else if (gmo.MapLevel == GalaxyMapLevel.Planet
                     && gmo.PlanetSurfaceTexture is not null)
            {
                DrawPlanetIcon(context, gmo, center, right, up, hitId);
                if (isSelected)
                {
                    DrawDisk(context, center, right, up, OuterRadius + 2f, SelectedHighlight with { W = 0.75f }, hitId);
                }
            }
            else
            {
                DrawDisk(context, center, right, up, OuterRadius, OutlineColor with { W = 0.85f }, hitId);
                DrawDisk(context, center, right, up, InnerRadius, fillColor, hitId);
            }
        }
        else
        {
            DrawDisk(context, center, right, up, OuterRadius, OutlineColor with { W = 0.85f }, hitId);
            DrawDisk(context, center, right, up, InnerRadius, fillColor, hitId);
        }

        // Add text label below the icon
        if (actor is GalaxyMapObjectProxy gmoLabel)
        {
            context.ScreenLabels.Add(new ScreenLabel(pixel.X, pixel.Y + 16f, gmoLabel.PreferredDisplayName));
        }

        // Draw rays for suns
        if (actor is GalaxyMapObjectProxy { Export.ClassName: "BioSun" })
        {
            for (int i = 0; i < 8; i++)
            {
                float angle = MathF.PI * 0.25f * i;
                Vector3 direction = (right * MathF.Cos(angle)) + (up * MathF.Sin(angle));
                context.Primitives.AddLine(center + (direction * 11f), center + (direction * 16f), SunColor, hitId);
            }
        }

        // Draw navigate indicator (small triangle) for objects that can be navigated into
        if (actor is GalaxyMapObjectProxy navigableObject && navigableObject.CanNavigateInto && navigableObject.MapChildren.Count > 0)
        {
            float arrowOffset = OuterRadius + 4f;
            Vector3 arrowCenter = center + (right * arrowOffset);
            Vector3 arrowTip = arrowCenter + (right * 3f);
            Vector3 arrowTop = arrowCenter + (up * 2f);
            Vector3 arrowBot = arrowCenter - (up * 2f);
            context.Primitives.AddLine(arrowTop, arrowTip, fillColor, hitId);
            context.Primitives.AddLine(arrowBot, arrowTip, fillColor, hitId);
            context.Primitives.AddLine(arrowTop, arrowBot, fillColor, hitId);
        }
    }

    public void ClearClusterIconResources()
    {
        ClusterIconTexture = null;
        ClusterIconMesh?.Dispose();
        ClusterIconMesh = null;
    }

    private static Vector4 GetColorForActor(ActorProxy actor)
    {
        if (actor is not GalaxyMapObjectProxy gmo)
            return DefaultColor;

        string className = gmo.Export.ClassName;
        if (className.StartsWith("SFXCluster", StringComparison.OrdinalIgnoreCase))
            return ClusterColor;
        if (className.StartsWith("SFXSystem", StringComparison.OrdinalIgnoreCase))
            return SystemColor;
        if (className is "BioSun")
            return SunColor;
        if (className is "SFXMassRelay")
            return RelayColor;
        if (className.StartsWith("BioPlanet", StringComparison.OrdinalIgnoreCase)
            || className.StartsWith("SFXPlanet", StringComparison.OrdinalIgnoreCase))
            return PlanetColor;
        if (className.StartsWith("SFXGalaxyMap", StringComparison.OrdinalIgnoreCase))
            return DefaultColor;

        return DefaultColor;
    }

    private static void DrawDisk(LevelEditorRenderContext context, Vector3 center, Vector3 right, Vector3 up, float radius, Vector4 color, int hitId)
    {
        var mesh = context.Primitives.BuildMesh(color, hitId, Matrix4x4.Identity);
        mesh.AddVertex(center);

        for (int i = 0; i <= CircleSegments; i++)
        {
            float angle = MathF.PI * 2f * i / CircleSegments;
            Vector3 point = center + (right * radius * MathF.Cos(angle)) + (up * radius * MathF.Sin(angle));
            mesh.AddVertex(point);
        }

        for (int i = 1; i <= CircleSegments; i++)
        {
            mesh.AddTriangle(0, i, i + 1);
        }
    }

    private void DrawClusterIcon(LevelEditorRenderContext context, Vector3 center, Vector3 right, Vector3 up, int hitId)
    {
        DrawTexturedIcon(context, ClusterIconTexture, center, right, up, ClusterTextureRadius, hitId);
    }

    private void DrawPlanetIcon(LevelEditorRenderContext context, GalaxyMapObjectProxy actor, Vector3 center, Vector3 right, Vector3 up, int hitId)
    {
        if (actor.PlanetSurfaceTexture is not null)
        {
            DrawTexturedIcon(context, actor.PlanetSurfaceTexture, center, right, up, PlanetTextureRadius, hitId);
        }
    }

    private void DrawTexturedIcon(LevelEditorRenderContext context, PreviewTextureCache.TextureEntry texture, Vector3 center, Vector3 right, Vector3 up, float radius, int hitId)
    {
        context.CurrentHitTestId = new Vector3(
            (hitId & 0xFF) / 255f,
            ((hitId >> 8) & 0xFF) / 255f,
            ((hitId >> 16) & 0xFF) / 255f);

        Matrix4x4 model = new(
            right.X * radius, right.Y * radius, right.Z * radius, 0f,
            up.X * radius, up.Y * radius, up.Z * radius, 0f,
            0f, 0f, 1f, 0f,
            center.X, center.Y, center.Z, 1f);

        var constants = context.GetWorldConstants(model);
        constants.Flags |= LevelEditorRenderContext.ShaderFlags.PreserveTextureAlpha;
        constants.AmbientColor = Vector4.One;
        context.DefaultEffect.PrepDraw(context.ImmediateContext, context.AlphaBlendState, constants);
        context.DefaultEffect.RenderObject(context.ImmediateContext, ClusterIconMesh, texture.TextureView);
    }
}

public sealed class GalaxyMapSharedStaticMeshProxy : PrimitiveComponentProxy
{
    private readonly ModelPreview<LEVertex> _meshLE;
    private readonly ModelPreview<WorldVertex> _meshWorld;

    public GalaxyMapSharedStaticMeshProxy(LevelEditorRenderContext context, ExportEntry meshExport, ActorProxy parent, IEntry materialOverride = null, bool useLEShader = true)
        : base(context, meshExport, parent)
    {
        if (meshExport.ClassName == "StaticMesh")
        {
            StaticMesh staticMesh = meshExport.GetBinaryData<StaticMesh>();
            bool useDirectOverride = materialOverride is not null && ReferenceEquals(materialOverride.FileRef, meshExport.FileRef);
            if (useDirectOverride)
            {
                staticMesh.SetMaterials([materialOverride], true);
            }

            if (useLEShader)
            {
                _meshLE = new ModelPreview<LEVertex>(context, staticMesh, 0);
                if (!useDirectOverride && materialOverride is ExportEntry materialExport)
                {
                    _meshLE.OverrideSectionMaterials(context, materialExport);
                }
            }
            else
            {
                _meshWorld = new ModelPreview<WorldVertex>(context, staticMesh, 0);
                if (!useDirectOverride && materialOverride is ExportEntry materialExport)
                {
                    _meshWorld.OverrideSectionMaterials(context, materialExport);
                }
            }
        }
        else if (meshExport.ClassName == "SkeletalMesh")
        {
            SkeletalMesh skeletalMesh = meshExport.GetBinaryData<SkeletalMesh>();
            if (materialOverride is not null)
            {
                skeletalMesh.SetMaterials([materialOverride], true);
            }

            if (useLEShader)
            {
                _meshLE = new ModelPreview<LEVertex>(context, skeletalMesh);
            }
            else
            {
                _meshWorld = new ModelPreview<WorldVertex>(context, skeletalMesh);
            }
        }
        else if (meshExport.ClassName == "Model")
        {
            Mesh<LEVertex> mesh = BuildModelMesh(context, meshExport);
            if (mesh is not null)
            {
                _meshLE = new ModelPreview<LEVertex>(context, mesh, PreloadedModelData.LoadModel(meshExport, context.PackageCache));
            }
        }

        _meshLE?.UpdateLocalToWorld(LocalToWorld);
        _meshWorld?.UpdateLocalToWorld(LocalToWorld);
    }

    private static Mesh<LEVertex> BuildModelMesh(LevelEditorRenderContext context, ExportEntry modelExport)
    {
        Model model = ObjectBinary.From<Model>(modelExport);
        if (model.VertexBuffer is null || model.VertexBuffer.Length == 0)
            return null;

        var triangles = new List<Triangle>();
        var positions = new Vector3[model.VertexBuffer.Length];
        var normals = new Vector3[model.VertexBuffer.Length];
        var uvs = new Vector2[model.VertexBuffer.Length];

        for (int i = 0; i < model.VertexBuffer.Length; i++)
        {
            var vertex = model.VertexBuffer[i];
            positions[i] = new Vector3(-vertex.Position.X, vertex.Position.Z, vertex.Position.Y);
            uvs[i] = new Vector2(vertex.TexCoord.X, vertex.TexCoord.Y);
        }

        var sections = new List<ModelPreviewSection>();
        foreach (ExportEntry mcExp in model.Export.FileRef.Exports.Where(x => x.ClassName == "ModelComponent" && !x.IsDefaultObject))
        {
            ModelComponent mc = ObjectBinary.From<ModelComponent>(mcExp);
            if (mc.Model != model.Self)
                continue;

            foreach (var modelElement in mc.Elements)
            {
                foreach (var nodeIndex in modelElement.Nodes)
                {
                    var matchingNode = model.Nodes[nodeIndex];
                    var surface = model.Surfs[matchingNode.iSurf];
                    IEntry materialEntry = model.Export.FileRef.GetEntry(surface.Material);
                    sections.Add(new ModelPreviewSection(materialEntry?.InstancedFullPath, (uint)triangles.Count * 3, (uint)matchingNode.NumVertices - 2));

                    for (uint i = 2; i < matchingNode.NumVertices; i++)
                    {
                        triangles.Add(new Triangle((uint)matchingNode.iVertexIndex, (uint)matchingNode.iVertexIndex + i - 1, (uint)matchingNode.iVertexIndex + i));
                    }

                    Vector3 normal = model.Vectors[surface.vNormal];
                    Vector3 transformedNormal = new(-normal.X, normal.Z, normal.Y);
                    for (int i = 0; i < matchingNode.NumVertices; i++)
                    {
                        normals[matchingNode.iVertexIndex + i] = transformedNormal;
                    }
                }
            }
        }

        var vertices = new List<LEVertex>(positions.Length);
        for (int i = 0; i < positions.Length; i++)
        {
            Fixed4<Vector4> uvSet = default;
            uvSet[0] = new Vector4(uvs[i], 0, 0);
            vertices.Add((LEVertex)LEVertex.Create(positions[i], Vector3.Zero, new Vector4(normals[i], 1f), uvSet));
        }

        return new Mesh<LEVertex>(context.Device, triangles, vertices, isDynamic: false);
    }

    public override void Render(MeshRenderContext context, RenderPass pass)
    {
        if (pass is not RenderPass.Base)
            return;

        _meshLE?.Render(pass, context, 0);
        _meshWorld?.Render(pass, context, 0);
    }

    public override void UpdateLocalToWorld()
    {
        base.UpdateLocalToWorld();
        _meshLE?.UpdateLocalToWorld(LocalToWorld);
        _meshWorld?.UpdateLocalToWorld(LocalToWorld);
    }

    public override BoxSphereBounds GetBounds()
    {
        if (_meshLE is not null && _meshLE.LODs.Count > 0)
        {
            return _meshLE.LODs[0].Mesh.TransformedBounds;
        }

        if (_meshWorld is not null && _meshWorld.LODs.Count > 0)
        {
            return _meshWorld.LODs[0].Mesh.TransformedBounds;
        }

        return base.GetBounds();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _meshLE?.Dispose();
            _meshWorld?.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Proxy for a galaxy map object (SFXGalaxy, SFXCluster, SFXSystem, BioPlanet, etc.)
/// Galaxy map objects use PosX/PosY (IntProperty) for 2D positioning rather than
/// the standard Actor Location vector. Positions are mapped to the XY plane in 3D.
/// </summary>
public class GalaxyMapObjectProxy : ActorProxy
{
    private const float PlanetCloudScale = 1.015f;
    private static readonly Rotator RelayDisplayRotation = new(
        (-39.990234f).DegreesToUnrealRotationUnits(),
        (89.97f).DegreesToUnrealRotationUnits(),
        (94.97681f).DegreesToUnrealRotationUnits());
    private const float RelayDisplayScale = 0.1f;
    private static readonly Rotator SolarArrayDisplayRotation = new(
        (0f).DegreesToUnrealRotationUnits(),
        (90f).DegreesToUnrealRotationUnits(),
        (90f).DegreesToUnrealRotationUnits());
    private const float SolarArrayDisplayScale = 0.6f;

    public GalaxyMapLevel MapLevel { get; }
    public List<GalaxyMapObjectProxy> MapChildren { get; } = [];
    public GalaxyMapObjectProxy MapParent { get; set; }
    public PreviewTextureCache.TextureEntry PlanetSurfaceTexture { get; private set; }
    public ExportEntry PlanetMaterialExport { get; private set; }
    public ExportEntry CloudMaterialExport { get; private set; }
    public string ListDisplaySubtitle { get; private set; }
    public bool HasListDisplaySubtitle => !string.IsNullOrWhiteSpace(ListDisplaySubtitle);
    public string PreferredDisplayName => HasListDisplaySubtitle ? ListDisplaySubtitle : Export.ObjectName.Instanced;
    public bool HasSharedPlanetMesh => _sharedPlanetMesh is not null;
    public bool IsMassRelay => GlobalUnrealObjectInfo.IsA(Export.ClassName, "SFXMassRelay", Export.Game)
                               || GlobalUnrealObjectInfo.IsA(Export.ClassName, "SFXGalaxyMapMassRelay", Export.Game)
                               || Export.ClassName.Contains("MassRelay", StringComparison.OrdinalIgnoreCase);

    private GalaxyMapSharedStaticMeshProxy _sharedPlanetMesh;
    private GalaxyMapSharedStaticMeshProxy _sharedPlanetCloudMesh;
    private GalaxyMapSharedStaticMeshProxy _sharedPlanetRingMesh;

    public bool CanNavigateInto => MapLevel is GalaxyMapLevel.Galaxy or GalaxyMapLevel.Cluster or GalaxyMapLevel.System;

    public GalaxyMapObjectProxy(IActorEditorContext context, ExportEntry export, GalaxyMapLevel level)
        : base(context, export)
    {
        MapLevel = level;
        ListDisplaySubtitle = ResolveListDisplaySubtitle();

        // Galaxy map objects store position as PosX/PosY integer properties.
        // The camera looks straight down with UnitZ as world-up, which makes:
        //   world +X → screen DOWN,  world -Y → screen RIGHT
        // The game uses screen-space coords (PosX right+, PosY down+), so map:
        //   worldX = PosY,  worldY = -PosX
        int posX = Properties.GetProp<IntProperty>("PosX")?.Value ?? 0;
        int posY = Properties.GetProp<IntProperty>("PosY")?.Value ?? 0;
        if (posX != 0 || posY != 0)
        {
            location = new Vector3(posY, -posX, 0);
            UpdateLocalToWorld();
            _cleanSnapshot = SnapshotTransform();
        }

        // Load mesh components from sub-exports (StaticMeshComponent, SkeletalMeshComponent)
        LoadMeshComponents(context.RenderContext);
        LoadPlanetTextures(context.RenderContext);
    }

    public void RefreshFromExport(PackageCache packageCache)
    {
        Properties = Export.GetCondensedProperties();

        int posX = Properties.GetProp<IntProperty>("PosX")?.Value ?? 0;
        int posY = Properties.GetProp<IntProperty>("PosY")?.Value ?? 0;
        location = new Vector3(posY, -posX, 0);

        drawScale = Properties.GetProp<FloatProperty>("DrawScale")?.Value ?? 1f;
        drawScale3D = Properties.GetProp<StructProperty>("DrawScale3D") is { } drawScale3DProp
            ? CommonStructs.GetVector3(drawScale3DProp)
            : Vector3.One;
        rotation = Properties.GetProp<StructProperty>("Rotation") is { } rotationProp
            ? CommonStructs.GetRotator(rotationProp)
            : new Rotator(0, 0, 0);

        UpdateLocalToWorld();
        _cleanSnapshot = SnapshotTransform();
        hasAuxiliaryChanges = false;
        IsDirty = false;

        string oldSubtitle = ListDisplaySubtitle;
        ListDisplaySubtitle = ResolveListDisplaySubtitle();
        if (!string.Equals(oldSubtitle, ListDisplaySubtitle, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(ListDisplaySubtitle));
            OnPropertyChanged(nameof(HasListDisplaySubtitle));
            OnPropertyChanged(nameof(PreferredDisplayName));
        }

        if (MapLevel == GalaxyMapLevel.Planet)
        {
            LoadPlanetTextures((Editor as IActorEditorContext)?.RenderContext);
        }
    }

    private string ResolveListDisplaySubtitle()
    {
        if (MapLevel is not (GalaxyMapLevel.Cluster or GalaxyMapLevel.System or GalaxyMapLevel.Planet))
            return null;

        string[] candidateNames = MapLevel switch
        {
            GalaxyMapLevel.Cluster => ["DisplayName", "ClusterName", "Name", "NameStrRef"],
            GalaxyMapLevel.System => ["DisplayName", "SystemName", "Name", "NameStrRef"],
            GalaxyMapLevel.Planet => ["DisplayName", "PlanetName", "Name", "NameStrRef"],
            _ => ["DisplayName", "Name", "NameStrRef"]
        };

        string subtitle = ResolveSubtitleFromProperties(Properties, candidateNames);
        if (!string.IsNullOrWhiteSpace(subtitle))
            return subtitle;

        // Fallback: scan the full property tree for any likely display-name fields
        subtitle = ResolveSubtitleFromPropertyTree(Properties);
        if (!string.IsNullOrWhiteSpace(subtitle))
            return subtitle;

        if (Properties.GetProp<ObjectProperty>("Appearance")?.ResolveToExport(Export.FileRef, null) is ExportEntry appearanceFromProp)
        {
            subtitle = ResolveSubtitleFromPropertyTree(appearanceFromProp.GetProperties());
            if (!string.IsNullOrWhiteSpace(subtitle))
                return subtitle;
        }

        if (MapLevel == GalaxyMapLevel.Planet)
        {
            ExportEntry appearanceExport = ResolvePlanetAppearanceExport(null);
            if (appearanceExport is not null)
            {
                subtitle = ResolveSubtitleFromProperties(appearanceExport.GetProperties(), candidateNames);
                if (!string.IsNullOrWhiteSpace(subtitle))
                    return subtitle;

                subtitle = ResolveSubtitleFromPropertyTree(appearanceExport.GetProperties());
                if (!string.IsNullOrWhiteSpace(subtitle))
                    return subtitle;
            }
        }

        return null;
    }

    private string ResolveSubtitleFromProperties(PropertyCollection props, IEnumerable<string> propertyNames)
    {
        if (props is null)
            return null;

        foreach (string propName in propertyNames)
        {
            string strValue = props.GetProp<StrProperty>(propName)?.Value;
            if (IsUsefulDisplayName(strValue))
            {
                return strValue.Trim();
            }

            string nameValue = props.GetProp<NameProperty>(propName)?.Value.Instanced;
            if (IsUsefulDisplayName(nameValue))
            {
                return nameValue.Trim();
            }

            int strRef = props.GetProp<StringRefProperty>(propName)?.Value
                         ?? props.GetProp<IntProperty>(propName)?.Value
                         ?? 0;
            if (strRef <= 0)
                continue;

            string resolved = TLKManagerWPF.GlobalFindStrRefbyID(strRef, Export.FileRef);
            if (IsUsefulDisplayName(resolved))
            {
                return resolved.Trim();
            }
        }

        return null;
    }

    private string ResolveSubtitleFromPropertyTree(PropertyCollection props)
    {
        if (props is null)
            return null;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in EnumerateDisplayNameCandidates(props, 0, seen))
        {
            if (IsUsefulDisplayName(candidate))
                return candidate.Trim();
        }

        return null;
    }

    private IEnumerable<string> EnumerateDisplayNameCandidates(PropertyCollection props, int depth, HashSet<string> seen)
    {
        if (props is null || depth > 4)
            yield break;

        foreach (Property prop in props)
        {
            string propName = prop.Name.Instanced;
            bool looksLikeDisplayName = propName.Contains("name", StringComparison.OrdinalIgnoreCase)
                                        || propName.Contains("display", StringComparison.OrdinalIgnoreCase)
                                        || propName.Contains("title", StringComparison.OrdinalIgnoreCase)
                                        || propName.Contains("label", StringComparison.OrdinalIgnoreCase);

            switch (prop)
            {
                case StrProperty strProp when looksLikeDisplayName:
                    if (seen.Add(strProp.Value ?? string.Empty))
                        yield return strProp.Value;
                    break;

                case NameProperty nameProp when looksLikeDisplayName:
                    if (seen.Add(nameProp.Value.Instanced ?? string.Empty))
                        yield return nameProp.Value.Instanced;
                    break;

                case StringRefProperty stringRefProp when looksLikeDisplayName || propName.Contains("strref", StringComparison.OrdinalIgnoreCase):
                {
                    string resolved = TLKManagerWPF.GlobalFindStrRefbyID(stringRefProp.Value, Export.FileRef);
                    if (seen.Add(resolved ?? string.Empty))
                        yield return resolved;
                    break;
                }

                case IntProperty intProp when propName.Contains("strref", StringComparison.OrdinalIgnoreCase):
                {
                    string resolved = TLKManagerWPF.GlobalFindStrRefbyID(intProp.Value, Export.FileRef);
                    if (seen.Add(resolved ?? string.Empty))
                        yield return resolved;
                    break;
                }

                case StructProperty structProp:
                    foreach (string nested in EnumerateDisplayNameCandidates(structProp.Properties, depth + 1, seen))
                        yield return nested;
                    break;

                case ArrayPropertyBase arrayProp:
                    foreach (Property item in arrayProp.Properties)
                    {
                        if (item is StructProperty itemStruct)
                        {
                            foreach (string nested in EnumerateDisplayNameCandidates(itemStruct.Properties, depth + 1, seen))
                                yield return nested;
                        }
                        else if (item is StrProperty itemStr && looksLikeDisplayName)
                        {
                            if (seen.Add(itemStr.Value ?? string.Empty))
                                yield return itemStr.Value;
                        }
                        else if (item is NameProperty itemName && looksLikeDisplayName)
                        {
                            if (seen.Add(itemName.Value.Instanced ?? string.Empty))
                                yield return itemName.Value.Instanced;
                        }
                        else if (item is StringRefProperty itemStringRef)
                        {
                            string resolved = TLKManagerWPF.GlobalFindStrRefbyID(itemStringRef.Value, Export.FileRef);
                            if (seen.Add(resolved ?? string.Empty))
                                yield return resolved;
                        }
                    }
                    break;
            }
        }
    }

    private bool IsUsefulDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string trimmed = value.Trim();
        if (string.Equals(trimmed, Export.ObjectName.Instanced, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(trimmed, Export.ClassName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (trimmed.StartsWith("SFX", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Bio", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Default__", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void LoadMeshComponents(LevelEditorRenderContext renderContext)
    {
        // Look for mesh components as direct sub-exports of this object
        TryLoadMeshChildrenOf(Export, renderContext);

        // Galaxy map objects may have an Appearance property pointing to an object
        // that contains the mesh components
        if (Properties.GetProp<ObjectProperty>("Appearance")?.ResolveToEntry(Export.FileRef) is ExportEntry appearanceExport)
        {
            TryLoadMeshChildrenOf(appearanceExport, renderContext);
        }
    }

    private void TryLoadMeshChildrenOf(ExportEntry parent, LevelEditorRenderContext renderContext)
    {
        foreach (var child in parent.FileRef.Exports)
        {
            if (child.idxLink != parent.UIndex)
                continue;

            string className = child.ClassName;
            if (GlobalUnrealObjectInfo.IsA(className, "StaticMeshComponent", child.Game)
                || GlobalUnrealObjectInfo.IsA(className, "SkeletalMeshComponent", child.Game))
            {
                var cmp = PrimitiveComponentProxy.Create(renderContext, child, this);
                if (cmp is not null)
                {
                    Components.Add(cmp);
                }
            }
        }
    }

    private void LoadPlanetTextures(LevelEditorRenderContext renderContext)
    {
        if (MapLevel != GalaxyMapLevel.Planet)
            return;

        ExportEntry appearanceExport = ResolvePlanetAppearanceExport(renderContext.PackageCache);
        ExportEntry planetMaterial = ResolveMaterialExport("PlanetMaterial", appearanceExport, renderContext.PackageCache);
        ExportEntry cloudMaterial = ResolveMaterialExport("CloudMaterial", appearanceExport, renderContext.PackageCache);

        PlanetMaterialExport = planetMaterial;
        CloudMaterialExport = cloudMaterial;
        PlanetSurfaceTexture = LoadMaterialTexture(renderContext, planetMaterial, preferCloudTexture: false);
    }

    public void LoadSharedPlanetMeshes(LevelEditorRenderContext renderContext, IMEPackage sharedMeshPackage)
    {
        if (MapLevel != GalaxyMapLevel.Planet)
            return;

        if (_sharedPlanetMesh is not null)
            return;

        if (sharedMeshPackage is null)
            return;

        string sharedMeshName = GetSharedPlanetMeshName();
        bool usesDefaultPlanetSphere = sharedMeshName.Equals("Planet", StringComparison.OrdinalIgnoreCase);
        bool isSolarArray = sharedMeshName.Equals("Solar_Array", StringComparison.OrdinalIgnoreCase);

        ExportEntry planetMeshExport = Export.FileRef.Exports.FirstOrDefault(e =>
            (e.ClassName == "StaticMesh" || e.ClassName == "SkeletalMesh" || e.ClassName == "Model")
            && e.ObjectName.Name.Equals(sharedMeshName, StringComparison.OrdinalIgnoreCase))
            ?? sharedMeshPackage.Exports.FirstOrDefault(e =>
                (e.ClassName == "StaticMesh" || e.ClassName == "SkeletalMesh" || e.ClassName == "Model")
                && e.ObjectName.Name.Equals(sharedMeshName, StringComparison.OrdinalIgnoreCase));
        if (planetMeshExport is null)
            return;

        _sharedPlanetMesh = new GalaxyMapSharedStaticMeshProxy(renderContext, planetMeshExport, this,
            usesDefaultPlanetSphere ? PlanetMaterialExport : null,
            useLEShader: usesDefaultPlanetSphere);
        if (IsMassRelay)
        {
            _sharedPlanetMesh.Scale = RelayDisplayScale;
            _sharedPlanetMesh.Scale3D = Vector3.One;
            _sharedPlanetMesh.Rotation = RelayDisplayRotation;
        }
        else if (isSolarArray)
        {
            _sharedPlanetMesh.Scale = SolarArrayDisplayScale;
            _sharedPlanetMesh.Scale3D = Vector3.One;
            _sharedPlanetMesh.Rotation = SolarArrayDisplayRotation;
        }
        Components.Add(_sharedPlanetMesh);

        if (usesDefaultPlanetSphere && CloudMaterialExport is not null)
        {
            _sharedPlanetCloudMesh = new GalaxyMapSharedStaticMeshProxy(renderContext, planetMeshExport, this, CloudMaterialExport)
            {
                Scale3D = new Vector3(PlanetCloudScale)
            };
            Components.Add(_sharedPlanetCloudMesh);
        }

        if (usesDefaultPlanetSphere && ShouldUsePlanetRing())
        {
            ExportEntry ringMeshExport = sharedMeshPackage.Exports.FirstOrDefault(e =>
                e.ClassName == "StaticMesh" && e.ObjectName.Name.Equals("PlanetRing", StringComparison.OrdinalIgnoreCase));
            if (ringMeshExport is not null)
            {
                _sharedPlanetRingMesh = new GalaxyMapSharedStaticMeshProxy(renderContext, ringMeshExport, this);
                Components.Add(_sharedPlanetRingMesh);
            }
        }
    }

    private string GetSharedPlanetMeshName()
    {
        if (GlobalUnrealObjectInfo.IsA(Export.ClassName, "SFXMassRelay", Export.Game)
            || GlobalUnrealObjectInfo.IsA(Export.ClassName, "SFXGalaxyMapMassRelay", Export.Game)
            || Export.ClassName.Contains("MassRelay", StringComparison.OrdinalIgnoreCase))
        {
            return "Mass_Relay_GM_MDL";
        }

        string systemLevelType = Properties.GetProp<EnumProperty>("SystemLevelType")?.Value ?? string.Empty;
        if (systemLevelType.Equals("SL_DEPOT", StringComparison.OrdinalIgnoreCase))
        {
            return "Solar_Array";
        }

        return Properties.GetProp<StrProperty>("MapName")?.Value is "BioP_CitHub"
            ? "Model_Oculon"
            : "Planet";
    }

    private void UnloadSharedPlanetMeshes()
    {
        if (_sharedPlanetMesh is not null)
        {
            Components.Remove(_sharedPlanetMesh);
            _sharedPlanetMesh.Dispose();
            _sharedPlanetMesh = null;
        }

        if (_sharedPlanetRingMesh is not null)
        {
            Components.Remove(_sharedPlanetRingMesh);
            _sharedPlanetRingMesh.Dispose();
            _sharedPlanetRingMesh = null;
        }

        if (_sharedPlanetCloudMesh is not null)
        {
            Components.Remove(_sharedPlanetCloudMesh);
            _sharedPlanetCloudMesh.Dispose();
            _sharedPlanetCloudMesh = null;
        }
    }

    private bool ShouldUsePlanetRing()
    {
        static bool PropertyIndicatesRing(PropertyCollection props)
        {
            string planetType = props.GetProp<EnumProperty>("PlanetType")?.Value ?? string.Empty;
            if (planetType.Contains("Ring", StringComparison.OrdinalIgnoreCase))
                return true;

            string systemLevelType = props.GetProp<EnumProperty>("SystemLevelType")?.Value ?? string.Empty;
            if (systemLevelType.Contains("RING", StringComparison.OrdinalIgnoreCase))
                return true;

            string orbitRingType = props.GetProp<EnumProperty>("OrbitRingType")?.Value ?? string.Empty;
            return orbitRingType.Contains("RING", StringComparison.OrdinalIgnoreCase)
                   || orbitRingType.Contains("ASTEROID", StringComparison.OrdinalIgnoreCase);
        }

        if (PropertyIndicatesRing(Properties))
            return true;

        ExportEntry appearanceExport = ResolvePlanetAppearanceExport(null);
        return appearanceExport is not null && PropertyIndicatesRing(appearanceExport.GetProperties());
    }

    private ExportEntry ResolvePlanetAppearanceExport(PackageCache packageCache)
    {
        if (Properties.GetProp<ObjectProperty>("Appearance")?.ResolveToExport(Export.FileRef, packageCache) is ExportEntry appearanceExport
            && IsPlanetAppearanceClass(appearanceExport))
        {
            return appearanceExport;
        }

        foreach (ExportEntry child in Export.FileRef.Exports)
        {
            if (child.idxLink == Export.UIndex && IsPlanetAppearanceClass(child))
            {
                return child;
            }
        }

        return null;
    }

    private static bool IsPlanetAppearanceClass(ExportEntry export)
    {
        return export.ClassName.StartsWith("SFXGalaxyMapPlanetAppearance", StringComparison.OrdinalIgnoreCase)
               || GlobalUnrealObjectInfo.IsA(export.ClassName, "SFXGalaxyMapPlanetAppearance", export.Game);
    }

    private ExportEntry ResolveMaterialExport(string propName, ExportEntry appearanceExport, PackageCache packageCache)
    {
        return Properties.GetProp<ObjectProperty>(propName)?.ResolveToExport(Export.FileRef, packageCache)
               ?? appearanceExport?.GetProperties(packageCache: packageCache).GetProp<ObjectProperty>(propName)?.ResolveToExport(appearanceExport.FileRef, packageCache);
    }

    private static PreviewTextureCache.TextureEntry LoadMaterialTexture(LevelEditorRenderContext renderContext, ExportEntry materialExport, bool preferCloudTexture)
    {
        ExportEntry textureExport = ResolveMaterialTextureExport(materialExport, renderContext.PackageCache, preferCloudTexture);
        return textureExport is null ? null : renderContext.TextureCache.LoadTexture(textureExport, renderContext.PackageCache);
    }

    private static ExportEntry ResolveMaterialTextureExport(ExportEntry materialExport, PackageCache packageCache, bool preferCloudTexture)
    {
        if (materialExport is null)
            return null;

        var candidates = new List<MaterialTextureCandidate>();
        var visitedMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectMaterialTextureCandidates(materialExport, packageCache, candidates, visitedMaterials);

        ExportEntry bestTexture = null;
        int bestScore = int.MinValue;
        foreach (MaterialTextureCandidate candidate in candidates)
        {
            int score = ScoreMaterialTexture(candidate, materialExport.ObjectName.Name, preferCloudTexture);
            if (score > bestScore)
            {
                bestScore = score;
                bestTexture = candidate.TextureExport;
            }
        }

        if (preferCloudTexture && bestScore < 40)
            return null;

        return bestTexture;
    }

    private static void CollectMaterialTextureCandidates(ExportEntry materialExport, PackageCache packageCache, List<MaterialTextureCandidate> candidates, HashSet<string> visitedMaterials)
    {
        if (!visitedMaterials.Add(materialExport.InstancedFullPath))
            return;

        if (materialExport.IsA("MaterialInstanceConstant"))
        {
            PropertyCollection props = materialExport.GetProperties(packageCache: packageCache);
            if (props.GetProp<ArrayProperty<StructProperty>>("TextureParameterValues") is { } textureParams)
            {
                foreach (StructProperty textureParam in textureParams)
                {
                    if (textureParam.GetProp<ObjectProperty>("ParameterValue")?.ResolveToExport(materialExport.FileRef, packageCache) is ExportEntry textureExport)
                    {
                        string parameterName = textureParam.GetProp<NameProperty>("ParameterName")?.Value.Instanced;
                        AddMaterialTextureCandidate(candidates, textureExport, parameterName, isDirectParameter: true);
                    }
                }
            }

            if (props.GetProp<ArrayProperty<ObjectProperty>>("ReferencedTextures") is { } referencedTextures)
            {
                foreach (ObjectProperty textureProp in referencedTextures)
                {
                    if (textureProp.ResolveToExport(materialExport.FileRef, packageCache) is ExportEntry textureExport)
                    {
                        AddMaterialTextureCandidate(candidates, textureExport, materialExport.ObjectName.Name, isDirectParameter: false);
                    }
                }
            }

            if (props.GetProp<ObjectProperty>("Parent")?.ResolveToExport(materialExport.FileRef, packageCache) is ExportEntry parentMaterial)
            {
                CollectMaterialTextureCandidates(parentMaterial, packageCache, candidates, visitedMaterials);
            }
        }
        else if (materialExport.ClassName == "Material")
        {
            foreach (int uIndex in ObjectBinary.From<Material>(materialExport).SM3MaterialResource.UniformExpressionTextures)
            {
                if (materialExport.FileRef.GetEntry(uIndex) is ExportEntry textureExport)
                {
                    AddMaterialTextureCandidate(candidates, textureExport, materialExport.ObjectName.Name, isDirectParameter: false);
                }
            }
        }
    }

    private static void AddMaterialTextureCandidate(List<MaterialTextureCandidate> candidates, ExportEntry textureExport, string hint, bool isDirectParameter)
    {
        if (textureExport.ClassName != "Texture2D")
            return;

        if (candidates.Any(candidate => candidate.TextureExport.InstancedFullPath == textureExport.InstancedFullPath && candidate.Hint == hint))
            return;

        candidates.Add(new MaterialTextureCandidate(textureExport, hint ?? string.Empty, isDirectParameter));
    }

    private static int ScoreMaterialTexture(MaterialTextureCandidate candidate, string materialName, bool preferCloudTexture)
    {
        ExportEntry textureExport = candidate.TextureExport;
        string textureName = textureExport.ObjectName.Name;
        string combinedName = $"{materialName} {candidate.Hint} {textureName}";

        bool isCloud = combinedName.Contains("cloud", StringComparison.OrdinalIgnoreCase)
                       || combinedName.Contains("atmos", StringComparison.OrdinalIgnoreCase);
        bool isMask = combinedName.Contains("mask", StringComparison.OrdinalIgnoreCase);
        bool isNormal = combinedName.Contains("norm", StringComparison.OrdinalIgnoreCase)
                        || combinedName.Contains("normal", StringComparison.OrdinalIgnoreCase);
        bool isSpecular = combinedName.Contains("spec", StringComparison.OrdinalIgnoreCase);
        bool isDiffuse = combinedName.Contains("diff", StringComparison.OrdinalIgnoreCase)
                         || combinedName.Contains("albedo", StringComparison.OrdinalIgnoreCase)
                         || combinedName.Contains("base", StringComparison.OrdinalIgnoreCase)
                         || combinedName.Contains("color", StringComparison.OrdinalIgnoreCase)
                         || combinedName.Contains("planet", StringComparison.OrdinalIgnoreCase);
        bool isOpacity = combinedName.Contains("opacity", StringComparison.OrdinalIgnoreCase)
                         || combinedName.Contains("trans", StringComparison.OrdinalIgnoreCase);

        int score = 0;
        if (candidate.IsDirectParameter) score += 30;
        if (preferCloudTexture)
        {
            if (isCloud) score += 100;
            if (isOpacity) score += 25;
            if (isMask) score += 10;
        }
        else
        {
            if (!isCloud) score += 40;
            if (isDiffuse) score += 60;
            if (textureName.Contains(materialName, StringComparison.OrdinalIgnoreCase)) score += 20;
            if (isMask) score -= 20;
            if (isOpacity) score -= 30;
        }

        if (isNormal) score -= 40;
        if (isSpecular) score -= 30;

        return score;
    }

    private readonly record struct MaterialTextureCandidate(ExportEntry TextureExport, string Hint, bool IsDirectParameter);

    public override void CommitChanges(PackageCache packageCache = null)
    {
        var props = Properties;

        // Write position back as PosX/PosY integers.
        // Inverse of the load mapping (worldX = PosY, worldY = -PosX):
        //   PosX = -worldY,  PosY = worldX
        props.AddOrReplaceProp(new IntProperty((int)MathF.Round(-location.Y), "PosX"));
        props.AddOrReplaceProp(new IntProperty((int)MathF.Round(location.X), "PosY"));

        if (props.ContainsNamedProp("DrawScale") || DrawScale != 1f)
        {
            props.AddOrReplaceProp(new FloatProperty(DrawScale, "DrawScale"));
        }
        if (props.ContainsNamedProp("DrawScale3D") || DrawScale3D != Vector3.One)
        {
            props.AddOrReplaceProp(CommonStructs.Vector3Prop(DrawScale3D, "DrawScale3D"));
        }
        if (props.ContainsNamedProp("Rotation") || !Rotation.IsZero)
        {
            props.AddOrReplaceProp(CommonStructs.RotatorProp(Rotation, "Rotation"));
        }

        Export.WriteProperties(props);
    }

    public static GalaxyMapLevel ClassifyExport(ExportEntry export)
    {
        string className = export.ClassName;
        MEGame game = export.Game;
        if (GlobalUnrealObjectInfo.IsA(className, "SFXGalaxy", game)
            || className is "SFXGalaxy")
            return GalaxyMapLevel.Galaxy;
        if (GlobalUnrealObjectInfo.IsA(className, "SFXCluster", game)
            || className.StartsWith("SFXCluster", StringComparison.OrdinalIgnoreCase))
            return GalaxyMapLevel.Cluster;
        if (GlobalUnrealObjectInfo.IsA(className, "SFXSystem", game)
            || className.StartsWith("SFXSystem", StringComparison.OrdinalIgnoreCase))
            return GalaxyMapLevel.System;
        // Everything else (BioPlanet, BioSun, SFXMassRelay, etc.) is a planetary body
        return GalaxyMapLevel.Planet;
    }

    public static bool IsGalaxyMapClass(ExportEntry export)
    {
        string className = export.ClassName;
        MEGame game = export.Game;
        return GlobalUnrealObjectInfo.IsA(className, "SFXGalaxyMapObject", game)
               || className is "SFXGalaxy"
               || className.StartsWith("SFXCluster", StringComparison.OrdinalIgnoreCase)
               || className.StartsWith("SFXSystem", StringComparison.OrdinalIgnoreCase)
               || GlobalUnrealObjectInfo.IsA(className, "BioPlanet", game)
               || className.StartsWith("BioPlanet", StringComparison.OrdinalIgnoreCase)
               || className.StartsWith("SFXPlanet", StringComparison.OrdinalIgnoreCase)
               || className.StartsWith("SFXGalaxyMap", StringComparison.OrdinalIgnoreCase)
               || GlobalUnrealObjectInfo.IsA(className, "BioSun", game)
               || className is "BioSun"
               || GlobalUnrealObjectInfo.IsA(className, "SFXMassRelay", game)
               || className is "SFXMassRelay";
    }
}

/// <summary>
/// Galaxy Map Editor - a visual editor for the galaxy map (SFXGalaxy) hierarchy.
/// Allows navigating into clusters and solar systems and repositioning planets/stars/objects.
/// </summary>
public partial class GalaxyMapEditor : WPFBase, ISceneRenderContextConfigurable, IActorEditorContext
{
    public LevelEditorRenderContext RenderContext { get; }
    private readonly GalaxyMapIconOverlay _iconOverlay = new();

    // Background texture for the current cluster view
    private PreviewTextureCache.TextureEntry _backgroundTexture;
    private Mesh<WorldVertex> _backgroundQuad;

    #region File state

    private IMEPackage _openPackage;
    private string _filePath;

    // Always-loaded background package that supplies the galaxy/cluster textures.
    // Kept open for the lifetime of a loaded session so the TextureCache can
    // reference its exports safely.
    private IMEPackage _galaxyBgPackage;
    private const string GalaxyBgPackageFileName = "BioA_Nor_203aGalaxyMap.pcc";
    private const string ClusterCircleMaterialName = "Circle_MatInst";
    private const string ClusterCircleTextureName = "circle";

    private bool _hasFileOpen;
    public bool HasFileOpen
    {
        get => _hasFileOpen;
        private set => SetProperty(ref _hasFileOpen, value);
    }

    private MEGame _game = MEGame.Unknown;
    public MEGame Game
    {
        get => _game;
        private set => SetProperty(ref _game, value);
    }

    #endregion

    #region Galaxy map data

    private List<GalaxyMapObjectProxy> _allObjects = [];
    private GalaxyMapObjectProxy _galaxyRoot;

    public ObservableCollectionExtended<GalaxyMapObjectProxy> CurrentObjects { get; } = [];
    public ICollectionView CurrentObjectsView { get; }
    private string _filterText = "";

    private readonly Stack<GalaxyMapObjectProxy> _navigationStack = new();

    private GalaxyMapObjectProxy _currentParent;
    public GalaxyMapObjectProxy CurrentParent
    {
        get => _currentParent;
        private set
        {
            if (SetProperty(ref _currentParent, value))
            {
                OnPropertyChanged(nameof(CanNavigateUp));
                OnPropertyChanged(nameof(BreadcrumbText));
            }
        }
    }

    public bool CanNavigateUp => _currentParent is not null;

    public string BreadcrumbText
    {
        get
        {
            if (_currentParent is null) return "Galaxy";
            var parts = new List<string> { "Galaxy" };
            foreach (var node in _navigationStack.Reverse())
            {
                parts.Add(node.PreferredDisplayName);
            }
            return string.Join(" > ", parts);
        }
    }

    #endregion

    #region Selection

    private GalaxyMapObjectProxy _selectedObject;
    private GalaxyMapObjectProxy _lastViewportClickedObject;
    private DateTime _lastViewportClickUtc;
    private static readonly TimeSpan ViewportDoubleClickThreshold = TimeSpan.FromMilliseconds(400);

    public GalaxyMapObjectProxy SelectedObject
    {
        get => _selectedObject;
        set => SelectObject(value, true);
    }

    private bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        set => SetProperty(ref _isDirty, value);
    }

    #endregion

    #region ISceneRenderContextConfigurable

    private bool _setAlphaToBlack = true;
    public bool SetAlphaToBlack
    {
        get => _setAlphaToBlack;
        set
        {
            if (SetProperty(ref _setAlphaToBlack, value))
            {
                if (value)
                    RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.AlphaAsBlack;
                else
                    RenderContext.RenderFlags &= ~LevelEditorRenderContext.ShaderFlags.AlphaAsBlack;
            }
        }
    }

    private bool _showRedChannel = true;
    public bool ShowRedChannel
    {
        get => _showRedChannel;
        set
        {
            if (SetProperty(ref _showRedChannel, value))
            {
                if (value)
                    RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.EnableRedChannel;
                else
                    RenderContext.RenderFlags &= ~LevelEditorRenderContext.ShaderFlags.EnableRedChannel;
            }
        }
    }

    private bool _showGreenChannel = true;
    public bool ShowGreenChannel
    {
        get => _showGreenChannel;
        set
        {
            if (SetProperty(ref _showGreenChannel, value))
            {
                if (value)
                    RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.EnableGreenChannel;
                else
                    RenderContext.RenderFlags &= ~LevelEditorRenderContext.ShaderFlags.EnableGreenChannel;
            }
        }
    }

    private bool _showBlueChannel = true;
    public bool ShowBlueChannel
    {
        get => _showBlueChannel;
        set
        {
            if (SetProperty(ref _showBlueChannel, value))
            {
                if (value)
                    RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.EnableBlueChannel;
                else
                    RenderContext.RenderFlags &= ~LevelEditorRenderContext.ShaderFlags.EnableBlueChannel;
            }
        }
    }

    private bool _showAlphaChannel = true;
    public bool ShowAlphaChannel
    {
        get => _showAlphaChannel;
        set
        {
            if (SetProperty(ref _showAlphaChannel, value))
            {
                if (value)
                    RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.EnableAlphaChannel;
                else
                    RenderContext.RenderFlags &= ~LevelEditorRenderContext.ShaderFlags.EnableAlphaChannel;
            }
        }
    }

    private System.Windows.Media.Color _backgroundColor;
    public System.Windows.Media.Color BackgroundColor
    {
        get => _backgroundColor;
        set
        {
            if (SetProperty(ref _backgroundColor, value))
            {
                RenderContext.BackgroundColor = value;
            }
        }
    }

    #endregion

    #region Widget settings

    public bool UseLocalCoordsForWidget
    {
        get => RenderContext.TransformWidget.UseLocalCoords;
        set => SetProperty(ref RenderContext.TransformWidget.UseLocalCoords, value);
    }

    private string _currentModeName = "Translate";
    public string CurrentModeName
    {
        get => _currentModeName;
        set => SetProperty(ref _currentModeName, value);
    }

    #endregion

    #region Position increment

    private float _posIncrement = 10f;
    public float PosIncrement
    {
        get => _posIncrement;
        set => SetProperty(ref _posIncrement, value);
    }

    private float _rotIncrement = 5f;
    public float RotIncrement
    {
        get => _rotIncrement;
        set => SetProperty(ref _rotIncrement, value);
    }

    private float _scaleIncrement = 0.1f;
    public float ScaleIncrement
    {
        get => _scaleIncrement;
        set => SetProperty(ref _scaleIncrement, value);
    }

    #endregion

    #region Busy

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    private string _busyText;
    public string BusyText
    {
        get => _busyText;
        set => SetProperty(ref _busyText, value);
    }

    public void SetBusy(string text = null)
    {
        BusyText = text;
        IsBusy = true;
    }

    public void EndBusy()
    {
        IsBusy = false;
    }

    #endregion

    #region IActorEditorContext

    // Returning true allows ActorProxy property setters to work (bypasses IsReadOnly check)
    // since ActorProxy.IsReadOnly is: (OwningFile is null || OwningFile.IsReadOnly) && !Editor.IsApplyingUndoRedo
    public bool IsApplyingUndoRedo => true;

    #endregion

    #region Commands

    public ICommand OpenFileCommand { get; set; }
    public ICommand SaveFileCommand { get; set; }
    public ICommand SaveAsCommand { get; set; }
    public ICommand CommitChangesCommand { get; set; }
    public ICommand NavigateUpCommand { get; set; }
    public ICommand NavigateIntoCommand { get; set; }
    public ICommand FocusSelectedCommand { get; set; }
    public ICommand ToggleTranslateCommand { get; set; }
    public ICommand ToggleRotateCommand { get; set; }
    public ICommand ToggleScaleCommand { get; set; }
    public ICommand ToggleUniformScaleCommand { get; set; }
    public ICommand ToggleLocalCoordsCommand { get; set; }
    public ICommand OpenInPackageEditorCommand { get; set; }

    private void LoadCommands()
    {
        OpenFileCommand = new GenericCommand(OpenFile);
        SaveFileCommand = new GenericCommand(SaveFile, () => HasFileOpen);
        SaveAsCommand = new GenericCommand(SaveFileAs, () => HasFileOpen);
        CommitChangesCommand = new GenericCommand(CommitChanges, () => HasFileOpen);
        NavigateUpCommand = new GenericCommand(NavigateUp, () => CanNavigateUp);
        NavigateIntoCommand = new GenericCommand(() =>
        {
            if (SelectedObject?.CanNavigateInto == true)
                NavigateInto(SelectedObject);
        }, () => SelectedObject?.CanNavigateInto == true);
        FocusSelectedCommand = new GenericCommand(() =>
        {
            if (SelectedObject is not null)
                FocusOnBounds(SelectedObject.GetBounds());
        }, () => SelectedObject is not null);
        ToggleTranslateCommand = new GenericCommand(() => { RenderContext.TransformWidget.Mode = EWidgetMode.Translate; CurrentModeName = "Translate"; }, () => HasFileOpen);
        ToggleRotateCommand = new GenericCommand(() => { RenderContext.TransformWidget.Mode = EWidgetMode.Rotate; CurrentModeName = "Rotate"; }, () => HasFileOpen);
        ToggleScaleCommand = new GenericCommand(() => { RenderContext.TransformWidget.Mode = EWidgetMode.Scale; CurrentModeName = "Scale"; }, () => HasFileOpen);
        ToggleUniformScaleCommand = new GenericCommand(() => { RenderContext.TransformWidget.Mode = EWidgetMode.UniformScale; CurrentModeName = "Uniform Scale"; }, () => HasFileOpen);
        ToggleLocalCoordsCommand = new GenericCommand(() => UseLocalCoordsForWidget = !UseLocalCoordsForWidget, () => HasFileOpen);
        OpenInPackageEditorCommand = new GenericCommand(() =>
        {
            if (SelectedObject is not null)
            {
                var p = new PackageEditorWindow();
                p.Show();
                p.LoadFile(SelectedObject.Export.FileRef.FilePath, SelectedObject.Export.UIndex);
                p.Activate();
            }
        }, () => SelectedObject is not null);
    }

    #endregion

    public GalaxyMapEditor() : base("Galaxy Map Editor")
    {
        RenderContext = new GalaxyMap2DRenderContext();
        _backgroundColor = GetThemeDefaultBackgroundColor();
        RenderContext.BackgroundColor = _backgroundColor;

        CurrentObjectsView = CollectionViewSource.GetDefaultView(CurrentObjects);
        CurrentObjectsView.Filter = ObjectFilter;

        LoadCommands();
        InitializeComponent();

        SceneViewer.Context = RenderContext;
    }

    private static System.Windows.Media.Color GetThemeDefaultBackgroundColor()
    {
        return Settings.Global_DarkMode_Enabled
            ? System.Windows.Media.Color.FromRgb(10, 10, 30)
            : System.Windows.Media.Color.FromRgb(20, 20, 50);
    }

    #region Window lifecycle

    private void GalaxyMapEditor_Loaded(object sender, RoutedEventArgs e)
    {
        RenderContext.RenderScene += RenderScene;
        RenderContext.SelectActor += ViewportActorSelect;
    }

    private void GalaxyMapEditor_Closing(object sender, CancelEventArgs e)
    {
        if (e.Cancel) return;

        if (IsDirty)
        {
            var result = MessageBox.Show(this,
                "There are uncommitted changes. Close anyway?",
                "Unsaved Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
                return;
            }
        }

        CloseFile();

        RenderContext.RenderScene -= RenderScene;
        RenderContext.SelectActor -= ViewportActorSelect;

        SceneViewer.Dispose();
    }

    #endregion

    #region File management

    private async void OpenFile()
    {
        var d = AppDirectories.GetOpenPackageDialog();
        if (d.ShowDialog() == true)
        {
            await LoadFileAsync(d.FileName);
        }
    }

    public async Task LoadFileAsync(string path)
    {
        try
        {
            CloseFile();
            IsBusy = true;
            BusyText = $"Loading {Path.GetFileName(path)}...";
            await Task.Delay(1).ConfigureAwait(true);

            _filePath = Path.GetFullPath(path);
            _openPackage = MEPackageHandler.OpenMEPackage(_filePath, this);
            Game = _openPackage.Game;

            var galaxyObjects = DiscoverGalaxyMapObjects(_openPackage);

            if (galaxyObjects.Count == 0)
            {
                MessageBox.Show(this, $"{Path.GetFileName(path)} does not contain galaxy map objects.\n\nLooking for SFXGalaxy, SFXCluster, SFXSystem, BioPlanet, etc.");
                CloseFile();
                IsBusy = false;
                return;
            }

            _allObjects = galaxyObjects;
            BuildHierarchy();
            LoadGalaxyBackgroundPackage();

            HasFileOpen = true;
            NavigateToLevel(null); // show galaxy root level
            CenterView();

            Title = $"Galaxy Map Editor - {Path.GetFileName(path)}";
            StatusBar_LeftMostText.Text = $"{Path.GetFileName(path)} — {_allObjects.Count} galaxy map objects";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error loading file:\n{ex.Message}");
            CloseFile();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CloseFile()
    {
        SceneViewer?.SetShouldRender(false);
        RenderContext.UnloadLevel();

        if (_selectedObject is not null)
        {
            _selectedObject.PropertyChanged -= OnObjectPropertyChanged;
            _selectedObject = null;
        }

        CurrentObjects.Clear();
        _navigationStack.Clear();
        CurrentParent = null;
        _galaxyRoot = null;

        foreach (var obj in _allObjects)
        {
            obj.Dispose();
        }
        _allObjects.Clear();

        UnloadPropertyTabs();
        DisposeBackgroundQuad();
        _iconOverlay.ClearClusterIconResources();

        _galaxyBgPackage?.Release(null);
        _galaxyBgPackage = null;

        if (_openPackage is not null)
        {
            _openPackage.Release(this);
            _openPackage = null;
        }

        _filePath = null;
        HasFileOpen = false;
        IsDirty = false;
        Game = MEGame.Unknown;
        Title = "Galaxy Map Editor";
        StatusBar_LeftMostText.Text = "Open a galaxy map package to begin";
    }

    /// <summary>
    /// Opens <see cref="GalaxyBgPackageFileName"/> from the game's mounted file list so
    /// its textures are available for background quads without depending on the user's
    /// chosen package file.
    /// </summary>
    private void LoadGalaxyBackgroundPackage()
    {
        if (_openPackage is null) return;
        try
        {
            if (!MELoadedFiles.TryGetHighestMountedFile(_openPackage.Game, GalaxyBgPackageFileName, out string bgPath))
            {
                // Fallback: look in the same directory as the currently loaded file
                string fallback = Path.Combine(Path.GetDirectoryName(_filePath)!, GalaxyBgPackageFileName);
                if (!File.Exists(fallback))
                    return;
                bgPath = fallback;
            }

            _galaxyBgPackage = MEPackageHandler.OpenMEPackage(bgPath);
            LoadClusterIconResources();
        }
        catch
        {
            _galaxyBgPackage = null;
            _iconOverlay.ClearClusterIconResources();
        }
    }

    private void LoadSharedPlanetMeshes(IEnumerable<GalaxyMapObjectProxy> objects)
    {
        if (_galaxyBgPackage is null)
            return;

        foreach (GalaxyMapObjectProxy planet in objects.Where(o => o.MapLevel == GalaxyMapLevel.Planet))
        {
            planet.LoadSharedPlanetMeshes(RenderContext, _galaxyBgPackage);
        }
    }

    private void LoadClusterIconResources()
    {
        _iconOverlay.ClearClusterIconResources();
        _iconOverlay.ClusterIconMesh = CreateUnitBillboardQuad();
        if (_galaxyBgPackage is null)
            return;

        ExportEntry materialExport = _galaxyBgPackage.Exports.FirstOrDefault(e =>
            e.ClassName == "MaterialInstanceConstant"
            && e.ObjectName.Name.Equals(ClusterCircleMaterialName, StringComparison.OrdinalIgnoreCase));
        if (materialExport is null)
            return;

        var material = new MaterialInstanceConstantLevelEditor(materialExport, RenderContext.PackageCache);
        ExportEntry textureExport = material.Textures
            .OfType<ExportEntry>()
            .FirstOrDefault(e => e.ClassName == "Texture2D"
                              && e.ObjectName.Name.Equals(ClusterCircleTextureName, StringComparison.OrdinalIgnoreCase))
            ?? material.Textures
                .OfType<ExportEntry>()
                .FirstOrDefault(e => e.ClassName == "Texture2D");
        if (textureExport is null)
            return;

        _iconOverlay.ClusterIconTexture = RenderContext.TextureCache.LoadTexture(textureExport, RenderContext.PackageCache);
        if (_iconOverlay.ClusterIconTexture is null)
            return;
    }

    private async void SaveFile()
    {
        if (!HasFileOpen || _openPackage is null) return;

        if (IsDirty)
        {
            switch (MessageBox.Show(this, "Commit changes before saving?", "Uncommitted changes", MessageBoxButton.YesNoCancel))
            {
                case MessageBoxResult.Yes:
                    CommitChanges();
                    break;
                case MessageBoxResult.No:
                    break;
                default:
                    return;
            }
        }

        IsBusy = true;
        BusyText = "Saving...";
        await _openPackage.SaveAsync();
        IsBusy = false;
    }

    private async void SaveFileAs()
    {
        if (!HasFileOpen || _openPackage is null) return;

        if (IsDirty)
        {
            switch (MessageBox.Show(this, "Commit changes before saving?", "Uncommitted changes", MessageBoxButton.YesNoCancel))
            {
                case MessageBoxResult.Yes:
                    CommitChanges();
                    break;
                case MessageBoxResult.No:
                    break;
                default:
                    return;
            }
        }

        string extension = Path.GetExtension(_filePath);
        var d = new SaveFileDialog { Filter = $"*{extension}|*{extension}" };
        if (d.ShowDialog() == true)
        {
            IsBusy = true;
            BusyText = "Saving...";
            await _openPackage.SaveAsync(d.FileName);
            IsBusy = false;
        }
    }

    #endregion

    #region Galaxy map discovery

    private List<GalaxyMapObjectProxy> DiscoverGalaxyMapObjects(IMEPackage package)
    {
        var objects = new List<GalaxyMapObjectProxy>();

        // Galaxy map objects (SFXGalaxy, SFXCluster, SFXSystem, BioPlanet, etc.)
        // are typically not in the Level's actor array — they are standalone exports
        // in the package. Scan all exports to find them.
        foreach (var export in package.Exports)
        {
            if (GalaxyMapObjectProxy.IsGalaxyMapClass(export))
            {
                var mapLevel = GalaxyMapObjectProxy.ClassifyExport(export);
                var proxy = new GalaxyMapObjectProxy(this, export, mapLevel);
                objects.Add(proxy);
            }
        }

        return objects;
    }

    private void BuildHierarchy()
    {
        // Find the galaxy root
        _galaxyRoot = _allObjects.FirstOrDefault(o => o.MapLevel == GalaxyMapLevel.Galaxy);

        var clusters = _allObjects.Where(o => o.MapLevel == GalaxyMapLevel.Cluster).ToList();
        var systems = _allObjects.Where(o => o.MapLevel == GalaxyMapLevel.System).ToList();
        var planetObjects = _allObjects.Where(o => o.MapLevel == GalaxyMapLevel.Planet).ToList();

        // Link galaxy → clusters
        if (_galaxyRoot is not null)
        {
            var clusterRefs = GetObjectArrayProperty(_galaxyRoot.Export, "Clusters")
                           ?? GetObjectArrayProperty(_galaxyRoot.Export, "Children");
            if (clusterRefs is not null)
            {
                foreach (int uIndex in clusterRefs)
                {
                    var cluster = clusters.FirstOrDefault(c => c.Export.UIndex == uIndex);
                    if (cluster is not null)
                    {
                        cluster.MapParent = _galaxyRoot;
                        _galaxyRoot.MapChildren.Add(cluster);
                    }
                }
            }
            else
            {
                // Fallback: all clusters belong to galaxy
                foreach (var cluster in clusters)
                {
                    cluster.MapParent = _galaxyRoot;
                    _galaxyRoot.MapChildren.Add(cluster);
                }
            }
        }

        // Link cluster → systems
        foreach (var cluster in clusters)
        {
            var systemRefs = GetObjectArrayProperty(cluster.Export, "Systems")
                          ?? GetObjectArrayProperty(cluster.Export, "Children");
            if (systemRefs is not null)
            {
                foreach (int uIndex in systemRefs)
                {
                    var system = systems.FirstOrDefault(s => s.Export.UIndex == uIndex);
                    if (system is not null)
                    {
                        system.MapParent = cluster;
                        cluster.MapChildren.Add(system);
                    }
                }
            }
        }

        // Assign unparented systems to clusters by proximity or just leave them
        foreach (var system in systems.Where(s => s.MapParent is null))
        {
            // Try to find parent cluster through export hierarchy
            var parentCluster = clusters.FirstOrDefault(c =>
                system.Export.Parent == c.Export || system.Export.idxLink == c.Export.UIndex);
            if (parentCluster is not null)
            {
                system.MapParent = parentCluster;
                parentCluster.MapChildren.Add(system);
            }
            else if (_galaxyRoot is not null)
            {
                // Fallback: add to galaxy root
                system.MapParent = _galaxyRoot;
                _galaxyRoot.MapChildren.Add(system);
            }
        }

        // Link system → planets/objects
        foreach (var system in systems)
        {
            // Systems may store children in multiple properties; combine all references
            var childRefs = new HashSet<int>();
            foreach (string propName in new[] { "Children", "SystemObjects", "Planets" })
            {
                var refs = GetObjectArrayProperty(system.Export, propName);
                if (refs is not null)
                {
                    foreach (int uIndex in refs)
                        childRefs.Add(uIndex);
                }
            }

            foreach (int uIndex in childRefs)
            {
                var planet = planetObjects.FirstOrDefault(p => p.Export.UIndex == uIndex);
                if (planet is not null)
                {
                    planet.MapParent = system;
                    system.MapChildren.Add(planet);
                }
            }
        }

        // Assign unparented planets to systems
        foreach (var planet in planetObjects.Where(p => p.MapParent is null))
        {
            var parentSystem = systems.FirstOrDefault(s =>
                planet.Export.Parent == s.Export || planet.Export.idxLink == s.Export.UIndex);
            if (parentSystem is not null)
            {
                planet.MapParent = parentSystem;
                parentSystem.MapChildren.Add(planet);
            }
        }
    }

    private static List<int> GetObjectArrayProperty(ExportEntry export, string propName)
    {
        var props = export.GetProperties();
        var arr = props.GetProp<ArrayProperty<ObjectProperty>>(propName);
        return arr?.Select(o => o.Value).ToList();
    }

    #endregion

    #region Navigation

    private void NavigateToLevel(GalaxyMapObjectProxy parent)
    {
        // Clear current viewport
        RenderContext.UnloadLevel();
        CurrentObjects.Clear();

        CurrentParent = parent;

        List<GalaxyMapObjectProxy> objectsToShow;
        if (parent is null)
        {
            // Galaxy level: show clusters (or all root objects)
            if (_galaxyRoot is not null)
            {
                objectsToShow = _galaxyRoot.MapChildren.Count > 0
                    ? _galaxyRoot.MapChildren
                    : _allObjects.Where(o => o.MapLevel == GalaxyMapLevel.Cluster).ToList();
            }
            else
            {
                objectsToShow = _allObjects.Where(o => o.MapLevel == GalaxyMapLevel.Cluster).ToList();
                if (objectsToShow.Count == 0)
                    objectsToShow = _allObjects.ToList();
            }
        }
        else
        {
            // Show children of the current parent
            objectsToShow = parent.MapChildren;
        }

        // Load background texture for cluster views
        DisposeBackgroundQuad();
        if (parent is null)
        {
            LoadGalaxyBackground(objectsToShow);
        }
        else if (parent.MapLevel == GalaxyMapLevel.Cluster)
        {
            LoadClusterBackground(parent);
        }
        else if (parent.MapLevel == GalaxyMapLevel.System)
        {
            LoadSystemBackground(objectsToShow);
        }

        if (objectsToShow.Count > 0)
        {
            LoadSharedPlanetMeshes(objectsToShow);
            CurrentObjects.AddRange(objectsToShow.OrderBy(o => o.Export.UIndex));
            RenderContext.LoadActors(objectsToShow.Cast<ActorProxy>().ToList());
            // Add the icon overlay so objects are rendered as billboard icons
            if (!RenderContext.DrawList_UI.Contains(_iconOverlay))
            {
                RenderContext.DrawList_UI.Add(_iconOverlay);
            }
            SceneViewer?.SetShouldRender(true);
        }
    }

    public void NavigateInto(GalaxyMapObjectProxy obj)
    {
        if (!obj.CanNavigateInto || obj.MapChildren.Count == 0) return;

        _navigationStack.Push(obj);
        SelectedObject = null;
        NavigateToLevel(obj);
        CenterView();
    }

    public void NavigateUp()
    {
        if (!CanNavigateUp) return;

        _navigationStack.Pop();
        var newParent = _navigationStack.Count > 0 ? _navigationStack.Peek() : null;
        SelectedObject = null;
        NavigateToLevel(newParent);
        CenterView();
    }

    private void LoadGalaxyBackground(List<GalaxyMapObjectProxy> objectsAtLevel)
    {
        if (_galaxyBgPackage is null || objectsAtLevel.Count == 0) return;

        ExportEntry galaxyTexExport = _galaxyBgPackage.Exports
            .FirstOrDefault(e => e.ObjectName.Name.Equals("galaxy", StringComparison.OrdinalIgnoreCase)
                              && e.ClassName == "Texture2D");
        if (galaxyTexExport is null) return;

        _backgroundTexture = RenderContext.TextureCache.LoadTexture(galaxyTexExport, RenderContext.PackageCache);
        if (_backgroundTexture is null) return;

        _backgroundQuad = CreateBackgroundQuad(objectsAtLevel.Select(obj => obj.Location), 0.08f, 64f);
    }

    private void LoadClusterBackground(GalaxyMapObjectProxy cluster)
    {
        var clusterProps = cluster.Export.GetProperties();
        var texRef = clusterProps.GetProp<ObjectProperty>("ClusterTexture");
        if (texRef is null) return;

        var texEntry = texRef.ResolveToEntry(cluster.Export.FileRef);
        if (texEntry is null) return;

        _backgroundTexture = RenderContext.TextureCache.LoadTexture(texEntry, RenderContext.PackageCache);
        if (_backgroundTexture is null) return;

        _backgroundQuad = CreateBackgroundQuad(cluster.MapChildren.Select(child => child.Location), 0.12f, 48f);
    }

    private void LoadSystemBackground(List<GalaxyMapObjectProxy> objectsAtLevel)
    {
        if (_galaxyBgPackage is null || objectsAtLevel.Count == 0)
            return;

        ExportEntry starfieldTexExport = _galaxyBgPackage.Exports
            .FirstOrDefault(e => e.ClassName == "Texture2D"
                              && e.ObjectName.Name.Equals("StarField", StringComparison.OrdinalIgnoreCase));
        if (starfieldTexExport is null)
            return;

        _backgroundTexture = RenderContext.TextureCache.LoadTexture(starfieldTexExport, RenderContext.PackageCache);
        if (_backgroundTexture is null)
            return;

        _backgroundQuad = CreateBackgroundQuad(objectsAtLevel.Select(obj => obj.Location), 0.15f, 96f);
    }

    private Mesh<WorldVertex> CreateBackgroundQuad(IEnumerable<Vector3> points, float paddingFactor, float minimumPadding)
    {
        if (_backgroundTexture is null)
            return null;

        bool hasPoint = false;
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (Vector3 point in points)
        {
            hasPoint = true;
            if (point.X < minX) minX = point.X;
            if (point.X > maxX) maxX = point.X;
            if (point.Y < minY) minY = point.Y;
            if (point.Y > maxY) maxY = point.Y;
        }

        if (!hasPoint)
            return null;

        float width = Math.Max(1f, maxX - minX);
        float height = Math.Max(1f, maxY - minY);
        float padding = Math.Max(Math.Max(width, height) * paddingFactor, minimumPadding);

        width += padding * 2f;
        height += padding * 2f;

        var textureDescription = _backgroundTexture.Texture.Description;
        float textureAspect = textureDescription.Height > 0
            ? (float)textureDescription.Width / textureDescription.Height
            : 1f;
        float boundsAspect = width / height;
        if (boundsAspect < textureAspect)
        {
            width = height * textureAspect;
        }
        else
        {
            height = width / textureAspect;
        }

        float centerX = (minX + maxX) * 0.5f;
        float centerY = (minY + maxY) * 0.5f;
        float left = centerX - (width * 0.5f);
        float right = centerX + (width * 0.5f);
        float bottom = centerY - (height * 0.5f);
        float top = centerY + (height * 0.5f);

        var normal = new Vector4(0, 0, 1, 1);
        var vertices = new List<WorldVertex>
        {
            new(new Vector3(left, bottom, -1f), normal, new Vector2(0, 1)),
            new(new Vector3(right, bottom, -1f), normal, new Vector2(1, 1)),
            new(new Vector3(right, top, -1f), normal, new Vector2(1, 0)),
            new(new Vector3(left, top, -1f), normal, new Vector2(0, 0)),
        };
        var triangles = new List<Triangle>
        {
            new(0, 1, 2),
            new(0, 2, 3)
        };
        return new Mesh<WorldVertex>(RenderContext.Device, triangles, vertices);
    }

    private Mesh<WorldVertex> CreateUnitBillboardQuad()
    {
        var normal = new Vector4(0, 0, 1, 1);
        var vertices = new List<WorldVertex>
        {
            new(new Vector3(-1f, -1f, 0f), normal, new Vector2(0, 1)),
            new(new Vector3(1f, -1f, 0f), normal, new Vector2(1, 1)),
            new(new Vector3(1f, 1f, 0f), normal, new Vector2(1, 0)),
            new(new Vector3(-1f, 1f, 0f), normal, new Vector2(0, 0)),
        };
        var triangles = new List<Triangle>
        {
            new(0, 1, 2),
            new(0, 2, 3)
        };
        return new Mesh<WorldVertex>(RenderContext.Device, triangles, vertices);
    }

    private void DisposeBackgroundQuad()
    {
        _backgroundQuad?.Dispose();
        _backgroundQuad = null;
        _backgroundTexture = null; // texture is owned by the cache, don't dispose
    }

    #endregion

    #region Selection

    private void SelectObject(GalaxyMapObjectProxy obj, bool focusCamera)
    {
        var prev = _selectedObject;
        if (SetProperty(ref _selectedObject, obj, nameof(SelectedObject)))
        {
            _iconOverlay.SelectedActor = _selectedObject;
            SceneViewer?.MarkRenderDirty();
            if (prev is not null)
            {
                prev.PropertyChanged -= OnObjectPropertyChanged;
            }
            if (_selectedObject is not null)
            {
                RenderContext.TransformWidget.Attach = _selectedObject;
                if (focusCamera)
                {
                    FocusOnBounds(_selectedObject.GetBounds());
                }
                _selectedObject.PropertyChanged += OnObjectPropertyChanged;
                LoadExportIntoTabs(_selectedObject.Export);
            }
            else
            {
                RenderContext.TransformWidget.Attach = null;
                UnloadPropertyTabs();
            }
        }
    }

    private void ViewportActorSelect(ActorProxy actor)
    {
        if (actor is GalaxyMapObjectProxy gmObj)
        {
            DateTime now = DateTime.UtcNow;
            bool isDoubleClick = gmObj == _lastViewportClickedObject && (now - _lastViewportClickUtc) <= ViewportDoubleClickThreshold;
            _lastViewportClickedObject = gmObj;
            _lastViewportClickUtc = now;

            if (isDoubleClick && gmObj.CanNavigateInto && gmObj.MapChildren.Count > 0)
            {
                NavigateInto(gmObj);
                _lastViewportClickedObject = null;
                return;
            }

            SelectObject(gmObj, false);
            ObjectsList.ScrollIntoView(_selectedObject);
        }
    }

    private void OnObjectPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ActorProxy.Location) or nameof(ActorProxy.Rotation)
            or nameof(ActorProxy.DrawScale) or nameof(ActorProxy.DrawScale3D))
        {
            SceneViewer?.MarkRenderDirty();
            IsDirty = true;
        }
    }

    #endregion

    #region Rendering

    private void RenderScene(object sender, EventArgs e)
    {
        // Render cluster background texture if present
        if (_backgroundQuad is not null && _backgroundTexture is not null)
        {
            RenderContext.CurrentHitTestId = Vector3.Zero;
            var constants = RenderContext.GetWorldConstants(Matrix4x4.Identity);
            // Background textures should be rendered at full brightness — override the
            // dim ambient (0.2) that GetWorldConstants sets for lit 3D objects.
            constants.AmbientColor = Vector4.One;
            RenderContext.DefaultEffect.PrepDraw(RenderContext.ImmediateContext, RenderContext.AlphaBlendState, constants);
            RenderContext.DefaultEffect.RenderObject(
                RenderContext.ImmediateContext,
                _backgroundQuad,
                _backgroundTexture.TextureView);
        }

        foreach (RenderPass pass in (RenderPass[])[RenderPass.Base])
        {
            for (int i = 0; i < RenderContext.DrawList_3D.Count; i++)
            {
                ActorProxy actor = RenderContext.DrawList_3D[i];
                int hitID = actor.HitID;
                RenderContext.CurrentHitTestId = new Vector3(
                    (hitID & 0xFF) / 255f,
                    ((hitID >> 8) & 0xFF) / 255f,
                    ((hitID >> 16) & 0xFF) / 255f);
                if (actor == _selectedObject)
                {
                    RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.Selected;
                }
                actor.Render(RenderContext, pass);
                RenderContext.RenderFlags &= ~LevelEditorRenderContext.ShaderFlags.Selected;
            }
        }
        RenderContext.DrawUI();
    }

    #endregion

    #region Camera

    private void CenterView()
    {
        if (CurrentObjects.Count > 0)
        {
            BoxSphereBounds fullBounds = CurrentObjects[0].GetBounds();
            for (int i = 1; i < CurrentObjects.Count; i++)
            {
                fullBounds = fullBounds.Union(CurrentObjects[i].GetBounds());
            }
            FocusOnBounds(fullBounds);
        }
        else
        {
            RenderContext.Camera.Position = new Vector3(0, 0, 1000f);
            RenderContext.Camera.Pitch = -MathF.PI / 2f;
            RenderContext.Camera.Yaw = 0f;
            RenderContext.Camera.OrthoSize = 500f;
        }
    }

    private void FocusOnBounds(BoxSphereBounds bounds)
    {
        Vector3 origin = bounds.Origin;
        // Position camera above the XY plane looking straight down
        RenderContext.Camera.Position = new Vector3(origin.X, origin.Y, 1000f);
        RenderContext.Camera.Pitch = -MathF.PI / 2f;
        RenderContext.Camera.Yaw = 0f;
        // Fit the full scene into the orthographic view with a small margin
        RenderContext.Camera.OrthoSize = Math.Max(50f, bounds.SphereRadius * 1.3f);
    }

    #endregion

    #region Commit & Save

    private void CommitChanges()
    {
        if (!HasFileOpen) return;

        foreach (var obj in _allObjects.Where(o => o.IsDirty))
        {
            obj.CommitChanges();
            obj.MarkClean();
        }
        IsDirty = false;
    }

    #endregion

    #region Properties panel

    private ExportEntry _selectedPropertiesExport;

    private void LoadExportIntoTabs(ExportEntry export)
    {
        if (export is null) return;
        _selectedPropertiesExport = export;
        GalaxyMapInterpreter.LoadExport(export);
        GalaxyMapMetadata.LoadExport(export);
    }

    private void UnloadPropertyTabs()
    {
        _selectedPropertiesExport = null;
        GalaxyMapInterpreter.UnloadExport();
        GalaxyMapMetadata.UnloadExport();
    }

    #endregion

    #region UI event handlers

    private bool ObjectFilter(object obj)
    {
        if (string.IsNullOrEmpty(_filterText)) return true;
        return obj is GalaxyMapObjectProxy gmObj &&
               gmObj.Export.ObjectName.Instanced.Contains(_filterText, StringComparison.OrdinalIgnoreCase);
    }

    private void FilterTextBox_KeyUp(object sender, KeyEventArgs e)
    {
        _filterText = FilterTextBox.Text;
        CurrentObjectsView.Refresh();
    }

    private void ObjectsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedObject?.CanNavigateInto == true && SelectedObject.MapChildren.Count > 0)
        {
            NavigateInto(SelectedObject);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            string ext = Path.GetExtension(files[0]).ToLower();
            if (ext is not (".upk" or ".pcc" or ".sfm"))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
            }
        }
        else
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length > 0)
            {
                await LoadFileAsync(files[0]);
            }
        }
    }

    #endregion

    public override void HandleUpdate(List<PackageUpdate> updates)
    {
        if (!HasFileOpen || updates is null || updates.Count == 0)
            return;

        HashSet<int> updatedExports = updates
            .Where(x => x.Change.Has(PackageChange.Export))
            .Select(x => x.Index)
            .ToHashSet();
        if (updatedExports.Count == 0)
            return;

        bool anyRefreshed = false;
        bool selectedExportUpdated = _selectedPropertiesExport is not null && updatedExports.Contains(_selectedPropertiesExport.UIndex);
        foreach (GalaxyMapObjectProxy obj in _allObjects)
        {
            if (updatedExports.Contains(obj.Export.UIndex))
            {
                obj.RefreshFromExport(RenderContext.PackageCache);
                anyRefreshed = true;
            }
        }

        if (!anyRefreshed)
        {
            if (selectedExportUpdated)
            {
                LoadExportIntoTabs(_selectedPropertiesExport);
            }
            return;
        }

        CurrentObjectsView.Refresh();
        OnPropertyChanged(nameof(BreadcrumbText));
        if (selectedExportUpdated)
        {
            LoadExportIntoTabs(_selectedPropertiesExport);
        }
        SceneViewer?.MarkRenderDirty();
    }

    public void HandleSaveStateChange(bool isSaving)
    {
        if (isSaving)
            SetBusy("Saving");
        else
            EndBusy();
    }
}
