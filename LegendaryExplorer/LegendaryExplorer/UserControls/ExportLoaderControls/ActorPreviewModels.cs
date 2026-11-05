using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using ShaderFlags = LegendaryExplorer.Tools.LevelEditor.Scene3D.RenderContext.ShaderFlags;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public partial class ActorPreviewControl
{
    internal static bool CanPreviewModelActor(ExportEntry export)
    {
        try { return ModelGeometry.HasActorGeometry(export); }
        catch { return false; } // A failed property resolution must not hide the other export tabs.
    }

    private ActorProxy CreatePreviewActor(ExportEntry export)
    {
        ActorProxy actor = ActorProxy.CanCreate(export) ? ActorProxy.Create(this, export)
            : CanPreviewModelActor(export) ? new ModelActorPreviewProxy(this, export)
            : new SkeletalMeshComponentPreviewActorProxy(this, export);
        if (actor == null && CanPreviewModelActor(export)) actor = new ModelActorPreviewProxy(this, export);
        if (actor != null && CanPreviewModelActor(export))
        {
            var faces = ModelGeometry.GetActorFaces(export, RenderContext.PackageCache);
            if (faces.Count > 0)
            {
                // Replace the ordinary brush wireframe with the same actor-scoped geometry
                // used for solid preview and bounds. Retain any mesh/animation components.
                foreach (var brush in actor.Components.OfType<BrushComponentProxy>().ToArray())
                {
                    actor.Components.Remove(brush);
                    brush.Dispose();
                }
                actor.Components.Add(new ActorModelPreviewComponent(RenderContext, export, actor, faces));
            }
        }
        return actor;
    }
}

internal sealed class ModelActorPreviewProxy(IActorEditorContext context, ExportEntry export) : ActorProxy(context, export);

internal sealed class ActorModelPreviewComponent : PrimitiveComponentProxy
{
    private readonly Mesh<WorldVertex> _mesh;

    public ActorModelPreviewComponent(MeshRenderContext context, ExportEntry actorExport, ActorProxy actor, IReadOnlyList<ModelGeometry.Face> faces)
        : base(context, actorExport, actor)
    {
        var vertices = new List<WorldVertex>();
        var triangles = new List<Triangle>();
        foreach (var face in faces)
        {
            uint first = (uint)vertices.Count;
            // Volumes have no surface material or light environment. Give their faces
            // stable preview shading so their depth stays visible in any lighting mode.
            float shade = 0.4f + 0.6f * MathF.Abs(Vector3.Dot(face.Normal, Vector3.Normalize(new Vector3(0.6f, -0.4f, 1))));
            var color = new Vector4(0.1f * shade, 0.65f * shade, 0.95f * shade, 1);
            vertices.AddRange(face.Vertices.Select(p => new WorldVertex(p, color, Vector3.Zero)));
            int[] indices = ModelGeometry.Triangulate(face.Vertices);
            for (int i = 0; i < indices.Length; i += 3)
                triangles.Add(new Triangle(first + (uint)indices[i], first + (uint)indices[i + 1], first + (uint)indices[i + 2]));
        }
        _mesh = new Mesh<WorldVertex>(context.Device, triangles, vertices);
        UpdateLocalToWorld();
    }

    // Faces are already in actor-local space, including each source component's transform.
    public override void UpdateLocalToWorld()
    {
        LocalToWorld = Actor.LocalToWorld;
        if (_mesh != null) _mesh.LocalToWorld = LocalToWorld;
    }

    public override void ApplyWorldOffset(Vector3 offset)
    {
        base.ApplyWorldOffset(offset);
        _mesh.LocalToWorld = LocalToWorld;
    }

    public override BoxSphereBounds GetBounds() => _mesh?.TransformedBounds ?? base.GetBounds();

    public override void Render(MeshRenderContext context, RenderPass pass)
    {
        if (!IsVisible || pass != RenderPass.Base) return;
        var constants = context.GetWorldConstants(LocalToWorld);
        constants.Flags |= ShaderFlags.PrimitiveRendering;
        context.DefaultEffect.PrepDraw(context.ImmediateContext, context.AlphaBlendState, constants);
        context.DefaultEffect.RenderObject(context.ImmediateContext, _mesh, context.WhiteTexView);
    }

    protected override void Dispose(bool disposing) { _mesh.Dispose(); base.Dispose(disposing); }
}
