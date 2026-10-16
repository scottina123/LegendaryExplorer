using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using LegendaryExplorer.UserControls.SharedToolControls.LegacyScene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Shaders;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace LegendaryExplorer.UserControls.SharedToolControls;

public partial class AnimationPreviewControl
{
    private ModelPreview<LEVertex> _gameShaderPreview;
    private WorldVertex[] _skeletonVertices = [];
    private Buffer _skeletonVertexBuffer;
    private int _gameShaderLoadGeneration;
    private bool _renderingDisposed;

    private bool _renderWireframe;
    public bool RenderWireframe
    {
        get => _renderWireframe;
        set => SetProperty(ref _renderWireframe, value);
    }

    private bool _showSkeleton;
    public bool ShowSkeleton
    {
        get => _showSkeleton;
        set
        {
            if (!SetProperty(ref _showSkeleton, value)) return;
            if (value) UpdateSkeletonPreview();
            else DisposeSkeletonPreview();
        }
    }

    private bool _renderGameShader;
    public bool RenderGameShader
    {
        get => _renderGameShader;
        set
        {
            if (!SetProperty(ref _renderGameShader, value)) return;
            if (value) QueueGameShaderPreview();
            else
            {
                ++_gameShaderLoadGeneration;
                IsGameShaderLoading = false;
                GameShaderStatus = null;
            }
        }
    }

    private bool _isGameShaderLoading;
    public bool IsGameShaderLoading
    {
        get => _isGameShaderLoading;
        private set
        {
            if (SetProperty(ref _isGameShaderLoading, value))
                OnPropertyChanged(nameof(CanUseGameShaders));
        }
    }

    private string _gameShaderStatus;
    public string GameShaderStatus
    {
        get => _gameShaderStatus;
        private set => SetProperty(ref _gameShaderStatus, value);
    }

    public bool CanUseGameShaders => !_renderingDisposed && !IsGameShaderLoading && _skm?.Export?.Game.IsMEGame() == true;
    internal Task GameShaderLoadingTask { get; private set; } = Task.CompletedTask;

    private void QueueGameShaderPreview()
    {
        if (!RenderGameShader || _renderingDisposed || _skm == null || !_meshContext.IsReady) return;
        if (_gameShaderPreview != null && _additionalMeshComponents.Values.All(component => component.GameShaderPreview != null)) return;
        GameShaderLoadingTask = LoadGameShaderPreviewAsync();
    }

    private async Task LoadGameShaderPreviewAsync()
    {
        var mesh = _skm;
        if (!mesh.Export.Game.IsMEGame()) return;
        int generation = ++_gameShaderLoadGeneration;
        IsGameShaderLoading = true;
        GameShaderStatus = "Loading in-game shaders...";
        try
        {
            // Shader-cache scanning is expensive; only GPU resource creation belongs on the UI thread.
            if (mesh.Materials.Length > 0 || _additionalMeshComponents.Values.Any(component => component.Mesh.Materials.Length > 0))
                await Task.Run(() => RefShaderCacheReader.PopulateOffsets(mesh.Export.Game));
            if (_renderingDisposed || generation != _gameShaderLoadGeneration || !ReferenceEquals(mesh, _skm)) return;

            _gameShaderPreview ??= new ModelPreview<LEVertex>(_meshContext.Device, mesh, _meshContext.TextureCache, _packageCache);
            foreach (PreviewMeshComponent component in _additionalMeshComponents.Values)
                component.GameShaderPreview ??= new ModelPreview<LEVertex>(_meshContext.Device, component.Mesh, _meshContext.TextureCache, _packageCache);
            UpdateSkinningOneShot();
            GameShaderStatus = null;
        }
        catch (Exception ex)
        {
            if (!_renderingDisposed && generation == _gameShaderLoadGeneration)
                ReportGameShaderFailure(ex);
        }
        finally
        {
            if (generation == _gameShaderLoadGeneration)
                IsGameShaderLoading = false;
        }
    }

    private void ReportGameShaderFailure(Exception exception)
    {
        RenderGameShader = false;
        GameShaderStatus = $"Could not preview in-game shaders: {exception.Message}";
        _gameShaderPreview?.Dispose();
        _gameShaderPreview = null;
        foreach (PreviewMeshComponent component in _additionalMeshComponents.Values)
        {
            component.GameShaderPreview?.Dispose();
            component.GameShaderPreview = null;
        }
    }

    private void ResetRenderingMesh()
    {
        ++_gameShaderLoadGeneration;
        IsGameShaderLoading = false;
        GameShaderStatus = null;
        _gameShaderPreview?.Dispose();
        _gameShaderPreview = null;
        DisposeSkeletonPreview();
    }

    private void DisposeSkeletonPreview()
    {
        _skeletonVertexBuffer?.Dispose();
        _skeletonVertexBuffer = null;
        _skeletonVertices = [];
    }

    private void UpdateSkeletonPreview()
    {
        if (!ShowSkeleton || _renderingDisposed || !_meshContext.IsReady || _skm == null) return;
        var player = _animPlayer ?? new LegendaryExplorerCore.Unreal.Animation.AnimSequencePlayer(_skm);
        player.ComputeSkinningMatrices();
        Matrix4x4[] transforms = player.BoneComponentSpaceTransforms;
        var vertices = new List<WorldVertex>();
        for (int bone = 1; bone < _skm.RefSkeleton.Length; bone++)
        {
            int parent = _skm.RefSkeleton[bone].ParentIndex;
            if (parent < 0 || parent >= bone) continue;
            vertices.Add(BoneVertex(transforms[parent]));
            vertices.Add(BoneVertex(transforms[bone]));
        }
        _skeletonVertices = vertices.ToArray();
        if (_skeletonVertices.Length == 0)
        {
            DisposeSkeletonPreview();
            return;
        }

        int floatsPerVertex = WorldVertex.Stride / sizeof(float);
        var data = new float[_skeletonVertices.Length * floatsPerVertex];
        for (int vertex = 0; vertex < _skeletonVertices.Length; vertex++)
            _skeletonVertices[vertex].ToFloats(data.AsSpan(vertex * floatsPerVertex));
        if (_skeletonVertexBuffer?.Description.SizeInBytes == data.Length * sizeof(float))
            _meshContext.ImmediateContext.UpdateSubresource(data, _skeletonVertexBuffer);
        else
        {
            _skeletonVertexBuffer?.Dispose();
            _skeletonVertexBuffer = Buffer.Create(_meshContext.Device, BindFlags.VertexBuffer, data);
        }

        static WorldVertex BoneVertex(Matrix4x4 transform) => new(
            new Vector3(-transform.M41, transform.M43, transform.M42), Vector3.One, Vector2.Zero);
    }

    private void RenderMaterialPreviews()
    {
        foreach (RenderPass pass in Enum.GetValues<RenderPass>())
        {
            if (RenderGameShader && _gameShaderPreview != null)
                _gameShaderPreview.Render(pass, _meshContext, 0, Matrix4x4.Identity);
            else
                _meshPreview.Render(pass, _meshContext, 0, Matrix4x4.Identity);
            foreach (PreviewMeshComponent component in _additionalMeshComponents.Values)
            {
                if (RenderGameShader && component.GameShaderPreview != null)
                    component.GameShaderPreview.Render(pass, _meshContext, 0, Matrix4x4.Identity);
                else
                    component.Preview.Render(pass, _meshContext, 0, Matrix4x4.Identity);
            }
        }
    }

    private MeshRenderContext.WorldConstants PreviewViewConstants() => new(
        Matrix4x4.Transpose(_meshContext.Camera.ProjectionMatrix),
        Matrix4x4.Transpose(_meshContext.Camera.ViewMatrix), Matrix4x4.Identity, _meshContext.CurrentTextureViewFlags);

    private void RenderWireframePreview(ModelPreview<WorldVertex> preview)
    {
        if (preview is not { LODs.Count: > 0 }) return;
        _meshContext.Wireframe = true;
        _meshContext.DefaultEffect.PrepDraw(_meshContext.ImmediateContext, _meshContext.AlphaBlendState);
        _meshContext.DefaultEffect.RenderObject(_meshContext.ImmediateContext, PreviewViewConstants(), preview.LODs[0].Mesh, [null]);
    }

    private void RenderSkeletonPreview()
    {
        if (_skeletonVertexBuffer == null || _skeletonVertices.Length == 0) return;
        DeviceContext context = _meshContext.ImmediateContext;
        if (_meshContext.DepthBufferView != null)
            context.ClearDepthStencilView(_meshContext.DepthBufferView, DepthStencilClearFlags.Depth, 1, 0);
        _meshContext.Wireframe = false;
        _meshContext.DefaultEffect.PrepDraw(context, _meshContext.AlphaBlendState);
        var constants = PreviewViewConstants();
        context.UpdateSubresource(ref constants, _meshContext.DefaultEffect.ConstantBuffer);
        context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_skeletonVertexBuffer, WorldVertex.Stride, 0));
        context.InputAssembler.PrimitiveTopology = PrimitiveTopology.LineList;
        context.PixelShader.SetShaderResource(0, _meshContext.WhiteTexView);
        try { context.Draw(_skeletonVertices.Length, 0); }
        finally { context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList; }
    }
}
