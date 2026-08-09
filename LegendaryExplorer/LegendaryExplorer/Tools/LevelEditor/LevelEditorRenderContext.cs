using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorer.Tools.AssetDatabase.VFXPreview;
using LegendaryExplorerCore.Gammtek.Extensions;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.SharpDX;
using LegendaryExplorerCore.Unreal.Collections;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace LegendaryExplorer.Tools.LevelEditor;

public class LevelEditorRenderContext : MeshRenderContext, IVfxDepthStateProvider
{
    protected override bool SupportsConcurrentResourceCreation => true;

    /// <summary>
    /// Uses the compiled local-vertex-factory material preview for actor mesh components instead of
    /// the diffuse-only Level Editor preview. Actor Preview enables this so character materials match
    /// the in-game shader preview used by Meshplorer and Morph Editor.
    /// </summary>
    public bool UseGameShaderMeshPreviews { get; set; }

    public event Action<ActorProxy> SelectActor;
    public event Action<ActorProxy> RightClickActor;
    public event Action<IHitProxy> SelectHitProxy;
    public event Action<IHitProxy> RightClickHitProxy;
    public event Action RightClickViewport;
    public List<ActorProxy> DrawList_3D = [];
    public List<UIElement> DrawList_UI = [];
    private readonly LightIconOverlay LightIcons = new();
    private readonly EmitterIconOverlay EmitterIcons = new();
    private readonly PointOfInterestIconOverlay PointOfInterestIcons = new();
    private readonly Dictionary<ActorProxy, SceneLight> sceneLightCache = [];

    internal LevelVfxRenderer VfxRenderer { get; }

    private USparseArray<IHitProxy> HitProxies = [];

    public readonly Widget TransformWidget;

    private Texture2D _hitStagingTexture;
    private readonly object renderResourceQueueLock = new();
    private readonly Queue<ILevelRenderResource> pendingRenderResources = new();
    private readonly HashSet<ILevelRenderResource> pendingRenderResourceSet = [];
    private CancellationTokenSource renderResourceCancellation = new();
    private Task renderResourceWorker;
    private int renderResourceRedrawRequested;
    private ActorProxy prioritizedResourceActor;
    private long lastUserActivityTimestamp;
    private long lastHoverHitTestTimestamp;
    private int visibleEmitterInstanceCount;
    private int sceneOverlayRevision;

    internal int SceneOverlayRevision => Volatile.Read(ref sceneOverlayRevision);

    public bool ForceContinuousRendering { get; set; }
    public override bool RenderOnUnhandledMouseMove => false;
    // The worker does not need the composition callback to advance. Wake the render path only when
    // it publishes a completed scene/selected actor, rather than polling throughout the whole load.
    public override bool HasPendingBackgroundWork => Volatile.Read(ref renderResourceRedrawRequested) != 0;
    public bool ShouldRenderHitTestPass => !HasActiveInput && !TransformWidget.IsDragging;

    public override bool IsActivelyUpdating() => ForceContinuousRendering || base.IsActivelyUpdating()
        || TransformWidget.IsDragging || (ShowEmitterVfx && Volatile.Read(ref visibleEmitterInstanceCount) > 0);

    internal bool UseVfxSceneDepthFallback { get; set; }
    // Match the native VFX preview's far-depth fallback only while particles draw; other Level Editor materials
    // retain their previous scene-depth behavior.
    public override ShaderResourceView PreviewSceneDepthTextureView
        => UseVfxSceneDepthFallback ? WhiteTexView : base.PreviewSceneDepthTextureView;

    /// <summary>
    /// Resource construction runs on a dedicated worker. The composition callback only consumes a redraw
    /// flag, keeping package, material, texture, shader, and buffer work off the WPF UI thread.
    /// </summary>
    public override bool ProcessBackgroundWork() =>
        Interlocked.Exchange(ref renderResourceRedrawRequested, 0) != 0;

    /// <summary>
    /// Gives interactive work priority over bulk mesh and material preparation. Input from the whole editor
    /// window feeds this timestamp, so scrolling or editing either side panel remains responsive too.
    /// </summary>
    internal void NotifyUserActivity()
        => Interlocked.Exchange(ref lastUserActivityTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());

    private void StartRenderResourceWorker()
    {
        lock (renderResourceQueueLock)
        {
            if (pendingRenderResourceSet.Count == 0 || renderResourceCancellation.IsCancellationRequested
                || renderResourceWorker is { IsCompleted: false })
            {
                return;
            }

            CancellationToken token = renderResourceCancellation.Token;
            renderResourceWorker = Task.Factory.StartNew(() => RunRenderResourceWorker(token), token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    private void RunRenderResourceWorker(CancellationToken token)
    {
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            while (!token.IsCancellationRequested)
            {
                while (SecondsSince(Interlocked.Read(ref lastUserActivityTimestamp)) < 0.15)
                {
                    if (token.WaitHandle.WaitOne(20)) return;
                }

                ILevelRenderResource component = null;
                lock (renderResourceQueueLock)
                {
                    while (pendingRenderResources.Count > 0)
                    {
                        ILevelRenderResource candidate = pendingRenderResources.Dequeue();
                        if (pendingRenderResourceSet.Remove(candidate))
                        {
                            component = candidate;
                            break;
                        }
                    }
                }

                if (component is null) break;
                // Resource preparation may create D3D11 device resources, but it must not use the immediate
                // context. The render thread is the sole owner of that context.
                component.PrepareRenderResources();

                bool priorityReady = false;
                lock (renderResourceQueueLock)
                {
                    ActorProxy priorityActor = prioritizedResourceActor;
                    if (priorityActor is not null
                        && priorityActor.Components.OfType<ILevelRenderResource>()
                            .Where(IsRenderResourceEnabled)
                            .All(candidate => candidate.RenderResourcesInitialized))
                    {
                        prioritizedResourceActor = null;
                        priorityReady = true;
                    }
                }
                if (priorityReady)
                {
                    Interlocked.Exchange(ref renderResourceRedrawRequested, 1);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (renderResourceQueueLock)
            {
                pendingRenderResources.Clear();
                pendingRenderResourceSet.Clear();
            }
            ErrorText = exception.FlattenException();
            Interlocked.Exchange(ref renderResourceRedrawRequested, 1);
        }
        finally
        {
            bool restart;
            lock (renderResourceQueueLock)
            {
                renderResourceWorker = null;
                restart = pendingRenderResourceSet.Count > 0 && !renderResourceCancellation.IsCancellationRequested;
            }
            Interlocked.Exchange(ref renderResourceRedrawRequested, 1);
            if (restart) StartRenderResourceWorker();
        }
    }

    private static double SecondsSince(long timestamp)
    {
        if (timestamp == 0)
        {
            return double.PositiveInfinity;
        }
        return (System.Diagnostics.Stopwatch.GetTimestamp() - timestamp)
               / (double)System.Diagnostics.Stopwatch.Frequency;
    }

    public readonly BatchedPrimitives Primitives = new();
    public readonly NavigationOverlay NavigationOverlay = new();

    public bool ShowLightIcons = true;
    public bool ShowEmitterVfx { get; private set; }
    public bool ShowDecalActors = true;
    public bool ShowPointsOfInterest = true;
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
        sceneLightCache.Clear();
        foreach (ActorProxy actor in DrawList_3D)
        {
            if (actor.TryGetSceneLight(out SceneLight light))
            {
                SceneLights.Add(light);
                sceneLightCache.Add(actor, light);
            }
        }
    }

    private static bool CanAffectSceneLight(string propertyName)
        => string.IsNullOrEmpty(propertyName) || propertyName is
            nameof(ActorProxy.Location) or
            nameof(ActorProxy.XPos) or
            nameof(ActorProxy.YPos) or
            nameof(ActorProxy.ZPos) or
            nameof(ActorProxy.Rotation) or
            nameof(ActorProxy.PitchDegrees) or
            nameof(ActorProxy.YawDegrees) or
            nameof(ActorProxy.RollDegrees) or
            nameof(ActorProxy.LightRadius) or
            nameof(ActorProxy.Brightness) or
            nameof(ActorProxy.LightColor) or
            nameof(ActorProxy.InnerConeAngle) or
            nameof(ActorProxy.OuterConeAngle);

    private void Actor_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        Interlocked.Increment(ref sceneOverlayRevision);
        if (sender is ActorProxy actor && actor.HasLightSettings && CanAffectSceneLight(e.PropertyName))
        {
            RefreshCachedSceneLight(actor);
        }
    }

    private void CacheSceneLight(ActorProxy actor)
    {
        if (actor.TryGetSceneLight(out SceneLight light))
        {
            sceneLightCache[actor] = light;
            SceneLights.Add(light);
        }
    }

    private void RefreshCachedSceneLight(ActorProxy actor)
    {
        sceneLightCache.Remove(actor);
        if (actor.TryGetSceneLight(out SceneLight light))
        {
            sceneLightCache[actor] = light;
        }

        SceneLights.Clear();
        SceneLights.AddRange(sceneLightCache.Values);
    }

    private void RemoveSceneLight(ActorProxy actor)
    {
        if (sceneLightCache.Remove(actor))
        {
            SceneLights.Clear();
            SceneLights.AddRange(sceneLightCache.Values);
        }
    }

    public LevelEditorRenderContext(bool readOnly = false) : base()
    {
        BackgroundColor = System.Windows.Media.Color.FromRgb(0x99, 0x99, 0x99);
        Camera.FirstPerson = true;
        TransformWidget = new Widget();
        VfxRenderer = new LevelVfxRenderer(this);
        IsReadOnly = readOnly;
    }

    public DepthStencilState GetVfxDepthState(bool depthTest, bool depthWrite)
        => VfxRenderer.GetDepthState(depthTest, depthWrite);

    internal void SetVisibleEmitterInstanceCount(int count)
        => Interlocked.Exchange(ref visibleEmitterInstanceCount, Math.Max(0, count));

    public void SetShowEmitterVfx(bool show)
    {
        if (ShowEmitterVfx == show)
        {
            return;
        }

        ShowEmitterVfx = show;
        if (!show)
        {
            SetVisibleEmitterInstanceCount(0);
        }
        lock (renderResourceQueueLock)
        {
            if (show)
            {
                foreach (ActorProxy actor in DrawList_3D)
                {
                    QueueRenderResourcesLocked(actor);
                }
            }
            else
            {
                foreach (ILevelRenderResource resource in pendingRenderResourceSet
                             .OfType<ParticleSystemComponentProxy>().ToArray())
                {
                    pendingRenderResourceSet.Remove(resource);
                }
            }
        }
        Interlocked.Exchange(ref renderResourceRedrawRequested, 1);
        if (show)
        {
            StartRenderResourceWorker();
        }
    }

    internal void QueueVisibleEmitterResources()
    {
        if (!ShowEmitterVfx)
        {
            return;
        }
        lock (renderResourceQueueLock)
        {
            foreach (EmitterActorProxy emitter in DrawList_3D.OfType<EmitterActorProxy>())
            {
                QueueRenderResourcesLocked(emitter);
            }
        }
        StartRenderResourceWorker();
    }

    const int HitTestSize = 3;
    private IHitProxy mouseDownHitProxy;

    public override bool MouseUp(MouseButtons button, int x, int y)
    {
        if (TransformWidget.IsDragging)
        {
            mouseDownHitProxy = null;
            TransformWidget.EndDrag();
            return true;
        }
        bool wasNavigationDrag = base.MouseUp(button, x, y);
        if (button == MouseButtons.Middle)
        {
            mouseDownHitProxy = null;
            return wasNavigationDrag;
        }
        if (wasNavigationDrag)
        {
            mouseDownHitProxy = null;
            return true;
        }

        if (HitBufferView is not null)
        {
            // A mouse-down frame may omit the hit-test pass while navigation input is active. Preserve
            // the hit captured at the start of the gesture instead of reading that intentionally-cleared
            // buffer on mouse-up.
            IHitProxy selected = mouseDownHitProxy ?? GetHitProxy(x, y);
            mouseDownHitProxy = null;

            if (button == MouseButtons.Right)
            {
                if (selected is ActorProxy rightClickedActor)
                {
                    RightClickActor?.Invoke(rightClickedActor);
                }
                else if (selected is not null)
                {
                    RightClickHitProxy?.Invoke(selected);
                }
                else
                {
                    RightClickViewport?.Invoke();
                }
            }
            else
            {
                TransformWidget.CurrentAxis = EWidgetAxis.None;
                switch (selected)
                {
                    case ActorProxy actor:
                        TransformWidget.Attach = actor;
                        SelectActor?.Invoke(actor);
                        break;
                    case AxisHitProxy axisProxy:
                        TransformWidget.CurrentAxis = axisProxy.Axis;
                        break;
                    case not null:
                        TransformWidget.Attach = null;
                        SelectHitProxy?.Invoke(selected);
                        break;
                }
            }
        }

        return false;
    }

    public override bool MouseDown(MouseButtons button, int x, int y)
    {
        NotifyUserActivity();
        mouseDownHitProxy = null;
        if (TransformWidget.IsDragging)
        {
            //failsafe if mouseup event was not captured
            if (Mouse.LeftButton is MouseButtonState.Released)
            {
                TransformWidget.EndDrag();
            }
        }
        // Middle mouse is navigation-only. Avoid synchronous GPU readback on both press and release.
        if (button == MouseButtons.Middle)
        {
            return base.MouseDown(button, x, y);
        }
        if (HitBufferView is not null)
        {
            IHitProxy selected = GetHitProxy(x, y);
            mouseDownHitProxy = selected;

            // Right mouse uses the saved hit only if the gesture ends as a click. It must never begin a
            // transform-widget drag or select a helper before camera rotation has had a chance to start.
            if (button == MouseButtons.Right)
            {
                return base.MouseDown(button, x, y);
            }

            TransformWidget.CurrentAxis = EWidgetAxis.None;
            switch (selected)
            {
                case AxisHitProxy axisProxy:
                    TransformWidget.CurrentAxis = axisProxy.Axis;
                    TransformWidget.BeginDrag(x, y);
                    return true;
                case ActorProxy:
                    return base.MouseDown(button, x, y);
                case not null:
                    base.MouseDown(button, x, y);
                    SelectHitProxy?.Invoke(selected);
                    return true;
            }
        }
        return base.MouseDown(button, x, y);
    }

    private int _lastHitTestX = -100;
    private int _lastHitTestY = -100;

    public override bool MouseMove(int x, int y)
    {
        NotifyUserActivity();
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
        if (TransformWidget.Attach is ActorProxy attachedActor
            && attachedActor.Components.OfType<MeshComponentProxy>()
                .All(component => component.RenderResourcesInitialized)
            && HitBufferView is not null && dx * dx + dy * dy >= 9
            && SecondsSince(lastHoverHitTestTimestamp) >= 1.0 / 30.0)
        {
            lastHoverHitTestTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            _lastHitTestX = x;
            _lastHitTestY = y;
            IHitProxy selected = GetHitProxy(x, y);

            EWidgetAxis previousAxis = TransformWidget.CurrentAxis;
            TransformWidget.CurrentAxis = EWidgetAxis.None;
            switch (selected)
            {
                case AxisHitProxy axisProxy:
                    TransformWidget.CurrentAxis = axisProxy.Axis;
                    break;
            }
            return previousAxis != TransformWidget.CurrentAxis;
        }
        return false;
    }

    public override bool MouseScroll(int delta)
    {
        NotifyUserActivity();
        return base.MouseScroll(delta);
    }

    public override bool KeyDown(Key key)
    {
        NotifyUserActivity();
        return base.KeyDown(key);
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
        try
        {
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
        }
        finally
        {
            ImmediateContext.UnmapSubresource(_hitStagingTexture, 0);
        }

        return data;
    }

    public void DrawUI()
    {
        NavigationOverlay.Draw(this);
        Primitives.Render(this, clearDepth: false);

        bool drawTransformWidgetLast = DrawList_UI.Contains(TransformWidget);
        foreach (UIElement uiElem in DrawList_UI)
        {
            if (drawTransformWidgetLast && ReferenceEquals(uiElem, TransformWidget))
            {
                continue;
            }

            uiElem.Draw(this);
        }
        // Billboard icons contain coplanar outline/fill layers. Normal depth writes make the second layer
        // contend with the first at equal depth, which appears as flicker while the camera moves.
        ImmediateContext.OutputMerger.SetDepthStencilState(GetVfxDepthState(depthTest: false, depthWrite: false));
        try
        {
            Primitives.Render(this);
        }
        finally
        {
            ImmediateContext.OutputMerger.SetDepthStencilState(null);
        }

        if (drawTransformWidgetLast)
        {
            TransformWidget.Draw(this);
            Primitives.Render(this);
        }
    }

    public void LoadActors(IList<ActorProxy> actors)
    {
        DrawList_3D.AddRange(actors);
        foreach (var actor in actors)
        {
            actor.HitID = HitProxies.Add(actor);
            actor.PropertyChanged += Actor_PropertyChanged;
            CacheSceneLight(actor);
            QueueRenderResources(actor);
        }
        StartRenderResourceWorker();
        if (!DrawList_UI.Contains(LightIcons))
        {
            DrawList_UI.Add(LightIcons);
        }
        if (!DrawList_UI.Contains(EmitterIcons))
        {
            DrawList_UI.Add(EmitterIcons);
        }
        if (!DrawList_UI.Contains(PointOfInterestIcons))
        {
            DrawList_UI.Add(PointOfInterestIcons);
        }
        Interlocked.Increment(ref sceneOverlayRevision);
        EnableTransformWidget();
    }

    private void QueueRenderResources(ActorProxy actor)
    {
        lock (renderResourceQueueLock)
        {
            QueueRenderResourcesLocked(actor);
        }
    }

    private bool IsRenderResourceEnabled(ILevelRenderResource resource)
        => resource is not ParticleSystemComponentProxy || ShowEmitterVfx;

    private bool ShouldPrepareRenderResource(ILevelRenderResource resource)
        => IsRenderResourceEnabled(resource)
           && (resource is not ParticleSystemComponentProxy particle
               || IsBoundsVisible(particle.Actor.GetBounds()));

    private void QueueRenderResourcesLocked(ActorProxy actor)
    {
        foreach (ILevelRenderResource component in actor.Components.OfType<ILevelRenderResource>())
        {
            // The visibility test for particle systems can walk actor bounds. Most components are already
            // initialized after the first pass, so reject them before doing that work every update frame.
            if (!component.RenderResourcesInitialized && ShouldPrepareRenderResource(component)
                && pendingRenderResourceSet.Add(component))
            {
                pendingRenderResources.Enqueue(component);
            }
        }
    }

    private void RemoveQueuedRenderResources(ActorProxy actor)
    {
        lock (renderResourceQueueLock)
        {
            foreach (ILevelRenderResource component in actor.Components.OfType<ILevelRenderResource>())
            {
                pendingRenderResourceSet.Remove(component);
            }
            if (ReferenceEquals(prioritizedResourceActor, actor))
            {
                prioritizedResourceActor = null;
            }
        }
    }

    /// <summary>
    /// Moves the selected actor to the front of the preparation queue without doing any resource work
    /// on the UI thread. This makes the selection outline and transform widget appear as soon as possible.
    /// </summary>
    internal void PrioritizeActorResources(ActorProxy actor)
    {
        if (actor is null)
        {
            lock (renderResourceQueueLock)
            {
                prioritizedResourceActor = null;
            }
            return;
        }

        List<ILevelRenderResource> priorityComponents = actor.Components.OfType<ILevelRenderResource>()
            .Where(IsRenderResourceEnabled)
            .Where(component => !component.RenderResourcesInitialized).ToList();
        lock (renderResourceQueueLock)
        {
            prioritizedResourceActor = actor;
            foreach (ILevelRenderResource component in priorityComponents)
            {
                pendingRenderResourceSet.Add(component);
            }

            var prioritySet = priorityComponents.ToHashSet();
            ILevelRenderResource[] remainder = pendingRenderResources
                .Where(component => pendingRenderResourceSet.Contains(component) && !prioritySet.Contains(component))
                .ToArray();
            pendingRenderResources.Clear();
            foreach (ILevelRenderResource component in priorityComponents)
            {
                if (pendingRenderResourceSet.Contains(component))
                {
                    pendingRenderResources.Enqueue(component);
                }
            }
            foreach (ILevelRenderResource component in remainder)
            {
                pendingRenderResources.Enqueue(component);
            }
        }

        if (priorityComponents.Count == 0)
        {
            lock (renderResourceQueueLock)
            {
                if (ReferenceEquals(prioritizedResourceActor, actor))
                {
                    prioritizedResourceActor = null;
                }
            }
            Interlocked.Exchange(ref renderResourceRedrawRequested, 1);
        }
        StartRenderResourceWorker();
    }

    private void StopRenderResourceWorker()
    {
        Task worker;
        CancellationTokenSource cancellation;
        lock (renderResourceQueueLock)
        {
            cancellation = renderResourceCancellation;
            cancellation.Cancel();
            pendingRenderResources.Clear();
            pendingRenderResourceSet.Clear();
            worker = renderResourceWorker;
        }

        if (worker is not null)
        {
            try
            {
                worker.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }

        lock (renderResourceQueueLock)
        {
            if (ReferenceEquals(renderResourceCancellation, cancellation))
            {
                renderResourceWorker = null;
                renderResourceCancellation.Dispose();
                renderResourceCancellation = new CancellationTokenSource();
            }
            prioritizedResourceActor = null;
            Interlocked.Exchange(ref renderResourceRedrawRequested, 0);
        }
    }

    public void EnableTransformWidget()
    {
        if (!IsReadOnly && !DrawList_UI.Contains(TransformWidget))
        {
            DrawList_UI.Add(TransformWidget);
            TransformWidget.GetAxisHitProxies(ref HitProxies);
        }
    }

    public int AddHitProxy(IHitProxy hitProxy)
    {
        hitProxy.HitID = HitProxies.Add(hitProxy);
        return hitProxy.HitID;
    }

    public void RemoveHitProxy(IHitProxy hitProxy)
    {
        if (hitProxy.HitID > 0)
        {
            HitProxies.RemoveAt(hitProxy.HitID);
            hitProxy.HitID = 0;
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
        StopRenderResourceWorker();
        EmptyCaches();
        HitProxies.Reset();
        foreach (ActorProxy actor in DrawList_3D)
        {
            actor.PropertyChanged -= Actor_PropertyChanged;
        }
        DrawList_3D.DisposeAndClear();
        VfxRenderer.Clear();
        Interlocked.Exchange(ref visibleEmitterInstanceCount, 0);
        DrawList_UI.Clear();
        SceneLights.Clear();
        sceneLightCache.Clear();
        Interlocked.Increment(ref sceneOverlayRevision);
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

    public override void DisposeResources()
    {
        StopRenderResourceWorker();
        VfxRenderer.Dispose();
        base.DisposeResources();
    }

    internal void RemoveActor(ActorProxy actor)
    {
        if (DrawList_3D.Remove(actor))
        {
            RemoveQueuedRenderResources(actor);
            actor.PropertyChanged -= Actor_PropertyChanged;
            RemoveSceneLight(actor);
            HitProxies.RemoveAt(actor.HitID);
            Interlocked.Increment(ref sceneOverlayRevision);
        }
    }

    internal void AddActor(ActorProxy actor)
    {
        if (!DrawList_3D.Contains(actor))
        {
            DrawList_3D.Add(actor);
            actor.HitID = HitProxies.Add(actor);
            actor.PropertyChanged += Actor_PropertyChanged;
            CacheSceneLight(actor);
            QueueRenderResources(actor);
            Interlocked.Increment(ref sceneOverlayRevision);
            if (!DrawList_UI.Contains(EmitterIcons))
            {
                DrawList_UI.Add(EmitterIcons);
            }
            if (!DrawList_UI.Contains(PointOfInterestIcons))
            {
                DrawList_UI.Add(PointOfInterestIcons);
            }
            StartRenderResourceWorker();
        }
    }
}
