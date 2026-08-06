using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using System;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public partial class ActorPreviewControl : ExportLoaderControl, IActorEditorContext
{
    private static readonly Color LightThemeDefaultBackgroundColor = Color.FromRgb(153, 153, 153);
    private static readonly Color DarkThemeDefaultBackgroundColor = Color.FromRgb(30, 30, 30);

    public LevelEditorRenderContext RenderContext { get; } = new()
    {
        UseGameShaderMeshPreviews = true
    };
    public bool IsApplyingUndoRedo => false;

    private ActorProxy _actor;
    private bool _controlIsLoaded;
    private int _actorLoadVersion;

    private bool _showWireframe;
    public bool ShowWireframe
    {
        get => _showWireframe;
        set
        {
            SetProperty(ref _showWireframe, value);
            RenderContext.Wireframe = value;
        }
    }

    private bool _showCollision;
    public bool ShowCollision
    {
        get => _showCollision;
        set => SetProperty(ref _showCollision, value);
    }

    private Color _backgroundColor = LightThemeDefaultBackgroundColor;
    public Color BackgroundColor
    {
        get => _backgroundColor;
        set
        {
            if (SetProperty(ref _backgroundColor, value))
            {
                RenderContext.BackgroundColor = value;
                Settings.ActorPreview_BackgroundColor = value.ToString();
                Settings.Save();
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    public static Color GetThemeDefaultBackgroundColor()
    {
        return Settings.Global_DarkMode_Enabled
            ? DarkThemeDefaultBackgroundColor
            : LightThemeDefaultBackgroundColor;
    }

    private static bool IsThemeDefaultBackgroundColor(Color color)
    {
        return color == LightThemeDefaultBackgroundColor || color == DarkThemeDefaultBackgroundColor;
    }

    public ActorPreviewControl() : base("Actor Preview")
    {
        DataContext = this;
        InitializeComponent();
        SceneViewer.Context = RenderContext;
        if (ColorConverter.ConvertFromString(Settings.ActorPreview_BackgroundColor) is Color savedColor)
        {
            BackgroundColor = IsThemeDefaultBackgroundColor(savedColor)
                ? GetThemeDefaultBackgroundColor()
                : savedColor;
        }
        else
        {
            BackgroundColor = GetThemeDefaultBackgroundColor();
        }
        RenderContext.Camera.FirstPerson = false;
        SceneViewer.Loaded += SceneViewer_Loaded;
        SceneViewer.Unloaded += SceneViewer_Unloaded;
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    private void OnThemeChanged(object sender, bool isDarkMode)
    {
        if (IsThemeDefaultBackgroundColor(BackgroundColor))
        {
            BackgroundColor = GetThemeDefaultBackgroundColor();
        }
    }

    private void SceneViewer_Loaded(object sender, RoutedEventArgs e)
    {
        if (_controlIsLoaded)
        {
            if (CurrentLoadedExport is not null && _actor is null)
            {
                LoadActor();
            }
            return;
        }

        _controlIsLoaded = true;
        RenderContext.UpdateScene += OnUpdateScene;
        RenderContext.RenderScene += OnRenderScene;
        if (Parent is TabItem { Parent: TabControl tc })
            tc.SelectionChanged += HostingTabSelectionChanged;
    }

    private void SceneViewer_Unloaded(object sender, RoutedEventArgs e)
    {
        if (Parent is TabItem { Parent: TabControl tc })
            tc.SelectionChanged -= HostingTabSelectionChanged;
    }

    private void HostingTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Parent is TabItem ti)
        {
            bool shouldRender = e.AddedItems.Contains(ti);
            SceneViewer?.SetShouldRender(shouldRender);
        }
    }

    private void OnUpdateScene(object sender, float deltaTime)
    {
        _actor?.UpdateScene(RenderContext, deltaTime);
    }

    private void OnRenderScene(object sender, EventArgs e)
    {
        ConfigurePreviewLighting();

        Span<RenderPass> passes = ShowCollision
            ? [RenderPass.Base, RenderPass.Hair, RenderPass.Collision]
            : [RenderPass.Base, RenderPass.Hair];
        foreach (RenderPass pass in passes)
            _actor?.Render(RenderContext, pass);
        RenderContext.DrawUI();
    }

    private void ConfigurePreviewLighting()
    {
        RenderContext.SceneLights.Clear();

        var cam = RenderContext.Camera;
        var keyPos = cam.Position - cam.CameraForward * 150f + cam.CameraUp * 75f;
        var fillPos = cam.Position + cam.CameraRight * 150f + cam.CameraUp * 25f;

        RenderContext.SceneLights.Add(new SceneLight(
            keyPos,
            100000f,
            new Vector3(1f, 1f, 1f),
            3.0f,
            false,
            Vector3.Zero,
            0,
            0));

        RenderContext.SceneLights.Add(new SceneLight(
            fillPos,
            100000f,
            new Vector3(0.85f, 0.9f, 1f),
            1.0f,
            false,
            Vector3.Zero,
            0,
            0));
    }

    public override bool CanParse(ExportEntry exportEntry) =>
        !exportEntry.IsDefaultObject && ActorProxy.CanCreate(exportEntry);

    public override void LoadExport(ExportEntry exportEntry)
    {
        UnloadExport();
        CurrentLoadedExport = exportEntry;
        if (IsLoaded)
        {
            LoadActor();
        }
    }

    private async void LoadActor()
    {
        ExportEntry requestedExport = CurrentLoadedExport;
        int loadVersion = ++_actorLoadVersion;
        IsBusy = true;
        BusyText = "Reading shader cache";
        try
        {
            if (requestedExport.Game.IsMEGame())
            {
                await Task.Run(() => RefShaderCacheReader.PopulateOffsets(requestedExport.Game));
            }
            if (loadVersion != _actorLoadVersion || !ReferenceEquals(CurrentLoadedExport, requestedExport))
            {
                return;
            }

            BusyText = "Loading actor";
            _actor = ActorProxy.Create(this, requestedExport);
            if (_actor is null)
            {
                RenderContext.ErrorText = $"Could not create preview object of type: '{requestedExport.ClassName}'";
            }
            else
            {
                RenderContext.ErrorText = null;
                RenderContext.LoadActors([_actor]);
                RecenterActorAtOrigin(_actor);
                BoxSphereBounds bounds = _actor.GetBounds();
                RenderContext.Camera.Position = bounds.Origin;
                ConfigureDepthRangeForBounds(bounds);
            }
        }
        catch (Exception ex)
        {
            if (loadVersion == _actorLoadVersion)
            {
                RenderContext.ErrorText = ex.FlattenException();
            }
        }
        finally
        {
            if (loadVersion == _actorLoadVersion)
            {
                BusyText = null;
                IsBusy = false;
            }
        }
    }

    // The level editor's very wide depth range is fine when flying around a whole level, but a single actor is
    // previewed at its original world coordinates, which can be hundreds of thousands of units from the origin.
    // At that magnitude the 0.1 -> 100000 range leaves almost no usable depth precision, which shows up as
    // z-fighting speckles between layered mesh sections (cloth over body, head over neck, etc).
    // Scaling the range to the size of the previewed actor restores precision.
    private const float DefaultZNear = 0.1f;
    private const float DefaultZFar = 100_000f;

    // Actors are created at their original level coordinates, which are frequently hundreds of thousands of units
    // from the world origin. float32 world positions at that magnitude lose ~0.01 units of precision, which is more
    // than the spacing between layered mesh sections (armor/cloth over body), so those sections z-fight.
    // Nothing in the preview cares about the real world position, so shift the actor to the origin.
    private static void RecenterActorAtOrigin(ActorProxy actor)
    {
        ApplyWorldOffset(actor, actor.LocalToWorld.Translation);
    }

    private static void ApplyWorldOffset(ActorProxy actor, Vector3 offset)
    {
        actor.LocalToWorld.Translation -= offset;
        foreach (PrimitiveComponentProxy component in actor.Components)
        {
            component.ApplyWorldOffset(offset);
        }
        foreach (ActorProxy attached in actor.Attached)
        {
            ApplyWorldOffset(attached, offset);
        }
    }

    private void ConfigureDepthRangeForBounds(BoxSphereBounds bounds)
    {
        float radius = Math.Max(bounds.SphereRadius, 1f);
        RenderContext.Camera.ZNear = radius / 50f;
        RenderContext.Camera.ZFar = radius * 500f;
    }

    public override void UnloadExport()
    {
        _actorLoadVersion++;
        RenderContext.Camera.ZNear = DefaultZNear;
        RenderContext.Camera.ZFar = DefaultZFar;
        BusyText = null;
        IsBusy = false;
        if (_actor is not null)
        {
            RenderContext.UnloadActors([_actor]);
            _actor.Dispose();
            _actor = null;
        }
        RenderContext.EmptyCaches();
        CurrentLoadedExport = null;
    }

    public override void PopOut() =>
        new ExportLoaderHostedWindow(new ActorPreviewControl(), CurrentLoadedExport).Show();

    public override void Dispose()
    {
        _actorLoadVersion++;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        RenderContext.UpdateScene -= OnUpdateScene;
        RenderContext.RenderScene -= OnRenderScene;
        _actor?.Dispose();
        _actor = null;
        SceneViewer?.Dispose();
    }


    #region Busy variables
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

    #endregion
}
