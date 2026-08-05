using LegendaryExplorerCore.Packages;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LegendaryExplorer.Tools.AssetDatabase.VFXPreview;

public partial class VfxPreviewControl : UserControl, INotifyPropertyChanged
{
    private readonly IVfxSourceAdapter sourceAdapter = new VfxSourceAdapter();
    private readonly DispatcherTimer statusTimer;
    private string statusText = "Select a VFX to preview.";
    private string warningText;

    public event PropertyChangedEventHandler PropertyChanged;
    public VfxPreviewRenderContext RenderContext { get; } = new();
    public Array ShadingModes { get; } = Enum.GetValues<VfxPreviewShadingMode>();
    public Array Backgrounds { get; } = Enum.GetValues<VfxPreviewBackground>();

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public string WarningText
    {
        get => warningText;
        private set => SetProperty(ref warningText, value);
    }

    public bool Loop
    {
        get => RenderContext.Simulation.Loop;
        set
        {
            RenderContext.Simulation.Loop = value;
            OnPropertyChanged();
        }
    }

    public VfxPreviewShadingMode ShadingMode
    {
        get => RenderContext.ShadingMode;
        set
        {
            RenderContext.ShadingMode = value;
            Viewport.MarkRenderDirty();
            OnPropertyChanged();
        }
    }

    public bool UseGameShader
    {
        get => RenderContext.UseGameShader;
        set
        {
            RenderContext.UseGameShader = value;
            WarningText = RenderContext.RuntimeWarning;
            Viewport.MarkRenderDirty();
            OnPropertyChanged();
        }
    }

    public VfxPreviewBackground Background
    {
        get => RenderContext.Background;
        set
        {
            RenderContext.Background = value;
            Viewport.MarkRenderDirty();
            OnPropertyChanged();
        }
    }

    public bool ShowAxis
    {
        get => RenderContext.ShowAxis;
        set => SetOverlay(value, current => RenderContext.ShowAxis = current);
    }

    public bool ShowGrid
    {
        get => RenderContext.ShowGrid;
        set => SetOverlay(value, current => RenderContext.ShowGrid = current);
    }

    public bool ShowGround
    {
        get => RenderContext.ShowGroundPlane;
        set => SetOverlay(value, current => RenderContext.ShowGroundPlane = current);
    }

    public bool ShowBounds
    {
        get => RenderContext.ShowBoundingBox;
        set => SetOverlay(value, current => RenderContext.ShowBoundingBox = current);
    }

    public bool ShowOrigin
    {
        get => RenderContext.ShowOrigin;
        set => SetOverlay(value, current => RenderContext.ShowOrigin = current);
    }

    public VfxPreviewControl()
    {
        InitializeComponent();
        Viewport.Context = RenderContext;
        statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        statusTimer.Tick += StatusTimer_Tick;
        Loaded += VfxPreviewControl_Loaded;
        Unloaded += VfxPreviewControl_Unloaded;
    }

    private void VfxPreviewControl_Loaded(object sender, RoutedEventArgs e)
    {
        RenderContext.ApplyTheme(Settings.Global_DarkMode_Enabled);
        ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
        ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
        statusTimer.Start();
        Viewport.MarkRenderDirty();
    }

    private void VfxPreviewControl_Unloaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
        statusTimer.Stop();
    }

    private void ThemeManager_ThemeChanged(object sender, bool isDarkMode)
    {
        RenderContext.ApplyTheme(isDarkMode);
        Viewport.MarkRenderDirty();
    }

    public void LoadExport(ExportEntry export)
    {
        if (export is null || !sourceAdapter.CanAdapt(export))
        {
            UnloadExport();
            WarningText = export is null ? null : $"{export.ClassName} is not supported by the VFX preview source adapter.";
            return;
        }

        try
        {
            VfxPreviewDefinition definition = sourceAdapter.CreateDefinition(export);
            RenderContext.Load(definition);
            WarningText = RenderContext.RuntimeWarning;
            Viewport.MarkRenderDirty();
            StatusTimer_Tick(null, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            RenderContext.Unload();
            StatusText = "Preview could not be loaded.";
            WarningText = exception.Message;
        }
    }

    public void UnloadExport()
    {
        RenderContext.Unload();
        StatusText = "Select a VFX to preview.";
        WarningText = null;
        Viewport.MarkRenderDirty();
    }

    public void ShowUnavailable(string warning)
    {
        RenderContext.Unload();
        StatusText = "Preview unavailable.";
        WarningText = warning;
        Viewport.MarkRenderDirty();
    }

    private void StatusTimer_Tick(object sender, EventArgs e)
    {
        if (RenderContext.Simulation.Definition is null)
        {
            return;
        }

        string playback = RenderContext.Simulation.IsPlaying ? "Playing" : "Paused";
        StatusText = $"{RenderContext.Simulation.Definition.Name} — {playback} — {RenderContext.Simulation.Time:F2}s — {RenderContext.Simulation.ParticleCount:N0} particles";
        WarningText = RenderContext.RuntimeWarning;
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        RenderContext.Simulation.Play();
        Viewport.MarkRenderDirty();
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => RenderContext.Simulation.Pause();

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        RenderContext.Restart();
        Viewport.MarkRenderDirty();
    }

    private void Focus_Click(object sender, RoutedEventArgs e)
    {
        RenderContext.Focus();
        Viewport.MarkRenderDirty();
    }

    private void SetOverlay(bool value, Action<bool> setter, [CallerMemberName] string propertyName = null)
    {
        setter(value);
        Viewport.MarkRenderDirty();
        OnPropertyChanged(propertyName);
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
