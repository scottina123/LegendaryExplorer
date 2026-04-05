using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Gammtek.Extensions;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.SharpDX;
using LegendaryExplorerCore.Unreal.Collections;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace LegendaryExplorer.Tools.LevelEditor;

public class LevelEditorRenderContext : MeshRenderContext
{
    public event Action<ActorProxy> SelectActor;
    public event Action<ActorProxy> RightClickActor;
    public List<ActorProxy> DrawList_3D = [];
    public List<UIElement> DrawList_UI = [];
    private readonly LightIconOverlay LightIcons = new();

    private USparseArray<IHitProxy> HitProxies = [];

    public readonly Widget TransformWidget;

    private Texture2D _hitStagingTexture;

    public override bool IsActivelyUpdating() => base.IsActivelyUpdating() || TransformWidget.IsDragging;

    public readonly BatchedPrimitives Primitives = new();

    public bool ShowLightIcons = true;
    public bool ShowVolumes;
    public bool ShowVolumetrics;

    // Maximum distance to show light icons (world units). <=0 = unlimited.
    public float LightIconRadius = 20000f;

    // Maximum number of light icons to display (nearest first)
    public int MaxLightIcons = 200;

    private bool IsReadOnly;

    public void RefreshSceneLights()
    {
        SceneLights.Clear();
        foreach (ActorProxy actor in DrawList_3D)
        {
            if (actor.TryGetSceneLight(out SceneLight light))
            {
                SceneLights.Add(light);
            }
        }
    }

    public LevelEditorRenderContext(bool readOnly = false) : base()
    {
        BackgroundColor = System.Windows.Media.Color.FromRgb(0x99, 0x99, 0x99);
        Camera.FirstPerson = true;
        TransformWidget = new Widget();
        IsReadOnly = readOnly;
    }

    const int HitTestSize = 3;

    public override bool MouseUp(MouseButtons button, int x, int y)
    {
        if (TransformWidget.IsDragging)
        {
            TransformWidget.EndDrag();
            return true;
        }
        if (base.MouseUp(button, x, y)) return true;

        if (HitBufferView is not null)
        {
            IHitProxy selected = GetHitProxy(x, y);

            if (button == MouseButtons.Right)
            {
                if (selected is ActorProxy rightClickedActor)
                {
                    RightClickActor?.Invoke(rightClickedActor);
                }
            }
            else
            {
                TransformWidget.CurrentAxis = EWidgetAxis.None;
                switch (selected)
                {
                    case ActorProxy actor:
                        SelectActor?.Invoke(actor);
                        TransformWidget.Attach = actor;
                        break;
                    case AxisHitProxy axisProxy:
                        TransformWidget.CurrentAxis = axisProxy.Axis;
                        break;
                }
            }
        }

        return false;
    }

    public override bool MouseDown(MouseButtons button, int x, int y)
    {
        if (TransformWidget.IsDragging)
        {
            //failsafe if mouseup event was not captured
            if (Mouse.LeftButton is MouseButtonState.Released)
            {
                TransformWidget.EndDrag();
            }
        }
        if (HitBufferView is not null)
        {
            IHitProxy selected = GetHitProxy(x, y);

            TransformWidget.CurrentAxis = EWidgetAxis.None;
            switch (selected)
            {
                case AxisHitProxy axisProxy:
                    TransformWidget.CurrentAxis = axisProxy.Axis;
                    TransformWidget.BeginDrag(x, y);
                    return true;
            }
        }
        return base.MouseDown(button, x, y);
    }

    private int _lastHitTestX = -100;
    private int _lastHitTestY = -100;

    public override bool MouseMove(int x, int y)
    {
        if (TransformWidget.IsDragging)
        {
            //failsafe if mouseup event was not captured
            if (Mouse.LeftButton is MouseButtonState.Released)
            {
                TransformWidget.EndDrag();
                return true;
            }
            TransformWidget.Drag(this, x, y);
            return true;
        }
        if (base.MouseMove(x, y)) return true;

        // Only run the GPU hit-test readback when the cursor has moved enough
        // to potentially pick a different widget axis. This avoids a costly
        // GPU→CPU sync (MapSubresource) on every single mouse-move event.
        int dx = x - _lastHitTestX;
        int dy = y - _lastHitTestY;
        if (HitBufferView is not null && dx * dx + dy * dy >= 9)
        {
            _lastHitTestX = x;
            _lastHitTestY = y;
            IHitProxy selected = GetHitProxy(x, y);

            TransformWidget.CurrentAxis = EWidgetAxis.None;
            switch (selected)
            {
                case AxisHitProxy axisProxy:
                    TransformWidget.CurrentAxis = axisProxy.Axis;
                    break;
            }
        }
        return false;
    }

    private IHitProxy GetHitProxy(int x, int y)
    {
        int minX = Math.Max(x - HitTestSize, 0);
        int maxX = Math.Min(x + HitTestSize, Width - 1);
        int minY = Math.Max(y - HitTestSize, 0);
        int maxY = Math.Min(y + HitTestSize, Height - 1);

        var hitData = ReadHitTestData(minX, minY, maxX, maxY);

        var indexes = MemoryMarshal.Cast<SharpDX.Color, int>(hitData);

        IHitProxy selected = null;

        for (int i = 0; i < indexes.Length; i += 1)
        {
            if (HitProxies.TryGetAt(indexes[i], out IHitProxy hitProxy) && (selected is null || selected.HitPriority < hitProxy.HitPriority))
            {
                selected = hitProxy;
            }
        }

        return selected;
    }

    private unsafe SharpDX.Color[] ReadHitTestData(int minX, int minY, int maxX, int maxY)
    {
        if (_hitStagingTexture is null) return [];

        int sizeX = maxX - minX + 1;
        int sizeY = maxY - minY + 1;

        var data = new SharpDX.Color[sizeX * sizeY];

        ImmediateContext.CopySubresourceRegion(HitBuffer, 0, new ResourceRegion(minX, minY, 0, maxX + 1, maxY + 1, 1), _hitStagingTexture, 0);
        var lockedData = ImmediateContext.MapSubresource(_hitStagingTexture, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);

        for (int y = 0; y < sizeY; y++)
        {
            var srcSpan = new Span<SharpDX.Color>((lockedData.DataPointer + y * lockedData.RowPitch).ToPointer(), sizeX);
            var destSpan = data.AsSpan(sizeX * y, sizeX);
            for (int x = 0; x < sizeX; x++)
            {
                var srcColor = srcSpan[x];
                destSpan[x] = new(srcColor.B, srcColor.G, srcColor.R, (byte)0);
            }
        }

        ImmediateContext.UnmapSubresource(_hitStagingTexture, 0);

        return data;
    }

    public void DrawUI()
    {
        foreach (UIElement uiElem in DrawList_UI)
        {
            uiElem.Draw(this);
        }
        Primitives.Render(this);
    }

    public void LoadActors(IList<ActorProxy> actors)
    {
        DrawList_3D.AddRange(actors);
        foreach (var actor in actors)
        {
            actor.HitID = HitProxies.Add(actor);
        }
        if (!DrawList_UI.Contains(LightIcons))
        {
            DrawList_UI.Add(LightIcons);
        }
        if (!IsReadOnly && !DrawList_UI.Contains(TransformWidget))
        {
            DrawList_UI.Add(TransformWidget);
            TransformWidget.GetAxisHitProxies(ref HitProxies);
        }
    }

    public void UnloadActors(IList<ActorProxy> actors)
    {
        foreach (var actor in actors)
        {
            RemoveActor(actor);
        }
    }

    public void UnloadLevel()
    {
        EmptyCaches();
        HitProxies.Reset();
        DrawList_3D.DisposeAndClear();
        DrawList_UI.Clear();
        TransformWidget.Attach = null;
    }

    public override void CreateSizeDependentResources(int width, int height, Texture2D newBackBuffer)
    {
        base.CreateSizeDependentResources(width, height, newBackBuffer);
        _hitStagingTexture?.Dispose();
        _hitStagingTexture = new Texture2D(Device, new Texture2DDescription
        {
            ArraySize = 1,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
            Height = HitTestSize * 2 + 1,
            Width = HitTestSize * 2 + 1,
            MipLevels = 1,
            OptionFlags = ResourceOptionFlags.None,
            SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Staging
        });
    }

    public override void DisposeSizeDependentResources()
    {
        _hitStagingTexture?.Dispose();
        _hitStagingTexture = null;
        base.DisposeSizeDependentResources();
    }

    internal void RemoveActor(ActorProxy actor)
    {
        if (DrawList_3D.Remove(actor))
        {
            HitProxies.RemoveAt(actor.HitID);
        }
    }

    internal void AddActor(ActorProxy actor)
    {
        if (!DrawList_3D.Contains(actor))
        {
            DrawList_3D.Add(actor);
            actor.HitID = HitProxies.Add(actor);
        }
    }
}
