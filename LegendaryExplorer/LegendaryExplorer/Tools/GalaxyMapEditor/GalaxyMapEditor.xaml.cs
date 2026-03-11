using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.UserControls.Interfaces;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.SharpDX;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
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

    private static readonly Vector4 OutlineColor = new(0f, 0f, 0f, 1f);
    private static readonly Vector4 ClusterColor = new(0.2f, 0.6f, 1f, 0.9f);
    private static readonly Vector4 SystemColor = new(1f, 0.85f, 0.2f, 0.9f);
    private static readonly Vector4 PlanetColor = new(0.3f, 0.9f, 0.4f, 0.9f);
    private static readonly Vector4 SunColor = new(1f, 0.55f, 0.1f, 0.95f);
    private static readonly Vector4 RelayColor = new(0.7f, 0.3f, 1f, 0.9f);
    private static readonly Vector4 DefaultColor = new(0.7f, 0.7f, 0.7f, 0.9f);
    private static readonly Vector4 SelectedHighlight = new(1f, 1f, 0.2f, 1f);

    public ActorProxy SelectedActor;

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
        if (actor == SelectedActor)
            fillColor = SelectedHighlight;

        // Outer ring (dark outline)
        DrawDisk(context, center, right, up, OuterRadius, OutlineColor with { W = 0.85f }, hitId);
        // Inner fill
        DrawDisk(context, center, right, up, InnerRadius, fillColor, hitId);

        // Add text label below the icon
        if (actor is GalaxyMapObjectProxy gmoLabel)
        {
            context.ScreenLabels.Add(new ScreenLabel(pixel.X, pixel.Y + 16f, gmoLabel.Export.ObjectName.Instanced));
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
        if (actor is GalaxyMapObjectProxy gmo && gmo.CanNavigateInto && gmo.MapChildren.Count > 0)
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
}

/// <summary>
/// Proxy for a galaxy map object (SFXGalaxy, SFXCluster, SFXSystem, BioPlanet, etc.)
/// Galaxy map objects use PosX/PosY (IntProperty) for 2D positioning rather than
/// the standard Actor Location vector. Positions are mapped to the XY plane in 3D.
/// </summary>
public class GalaxyMapObjectProxy : ActorProxy
{
    public GalaxyMapLevel MapLevel { get; }
    public List<GalaxyMapObjectProxy> MapChildren { get; } = [];
    public GalaxyMapObjectProxy MapParent { get; set; }

    public bool CanNavigateInto => MapLevel is GalaxyMapLevel.Galaxy or GalaxyMapLevel.Cluster or GalaxyMapLevel.System;

    public GalaxyMapObjectProxy(IActorEditorContext context, ExportEntry export, GalaxyMapLevel level)
        : base(context, export)
    {
        MapLevel = level;

        // Galaxy map objects store position as PosX/PosY integer properties
        // Map them to the XY plane for 3D visualization
        int posX = Properties.GetProp<IntProperty>("PosX")?.Value ?? 0;
        int posY = Properties.GetProp<IntProperty>("PosY")?.Value ?? 0;
        if (posX != 0 || posY != 0)
        {
            location = new Vector3(posX, posY, 0);
            UpdateLocalToWorld();
            _cleanSnapshot = SnapshotTransform();
        }

        // Load mesh components from sub-exports (StaticMeshComponent, SkeletalMeshComponent)
        LoadMeshComponents(context.RenderContext);
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

    public override void CommitChanges(PackageCache packageCache = null)
    {
        var props = Properties;

        // Write position back as PosX/PosY integers
        props.AddOrReplaceProp(new IntProperty((int)MathF.Round(location.X), "PosX"));
        props.AddOrReplaceProp(new IntProperty((int)MathF.Round(location.Y), "PosY"));

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
                parts.Add(node.Export.ObjectName.Instanced);
            }
            return string.Join(" > ", parts);
        }
    }

    #endregion

    #region Selection

    private GalaxyMapObjectProxy _selectedObject;
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
        }
        catch
        {
            _galaxyBgPackage = null;
        }
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

        if (objectsToShow.Count > 0)
        {
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

        // Build a quad on the XY plane covering the bounding box of all visible
        // objects with generous padding, slightly behind at Z=-1.
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var obj in objectsAtLevel)
        {
            float x = obj.Location.X;
            float y = obj.Location.Y;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        float padX = (maxX - minX) * 0.4f + 300f;
        float padY = (maxY - minY) * 0.4f + 300f;
        float left = minX - padX;
        float right = maxX + padX;
        float bottom = minY - padY;
        float top = maxY + padY;

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
        _backgroundQuad = new Mesh<WorldVertex>(RenderContext.Device, triangles, vertices);
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

        // Build a quad on the XY plane centered on the children's bounding box
        // slightly behind at Z=-1 so it doesn't z-fight with icons
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var child in cluster.MapChildren)
        {
            float x = child.Location.X;
            float y = child.Location.Y;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        // Add generous padding around the children
        float padX = (maxX - minX) * 0.3f + 100f;
        float padY = (maxY - minY) * 0.3f + 100f;
        float left = minX - padX;
        float right = maxX + padX;
        float bottom = minY - padY;
        float top = maxY + padY;

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
        _backgroundQuad = new Mesh<WorldVertex>(RenderContext.Device, triangles, vertices);
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

    public override void HandleUpdate(List<PackageUpdate> updates) { }

    public void HandleSaveStateChange(bool isSaving)
    {
        if (isSaving)
            SetBusy("Saving");
        else
            EndBusy();
    }
}
