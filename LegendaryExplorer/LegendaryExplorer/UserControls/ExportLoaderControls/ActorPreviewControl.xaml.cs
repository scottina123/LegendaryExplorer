using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorer.UserControls.ExportLoaderControls.MaterialEditor;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

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
    private TabControl _hostingTabControl;
    private TabItem _hostingTabItem;
    private System.Windows.Point? _materialPickMouseDownPosition;
    private BioMorphFaceEditor _actorMorphEditor;
    private ExportEntry _actorMorphExport;
    private ActorProxy _externalLiveMaterialActor;
    private LevelEditorRenderContext _externalLiveMaterialRenderContext;
    private readonly Dictionary<LiveMaterialEditorMaterial, ActorMaterialBinding> _materialBindings = [];

    public event EventHandler CloseMaterialEditorRequested;
    public event EventHandler LiveMaterialPreviewChanged;

    private sealed class ActorMaterialBinding
    {
        public required MeshComponentProxy Component { get; init; }
        public required IEntry SourceEntry { get; init; }
        public required IReadOnlyList<int> SlotIndexes { get; init; }
    }

    public ObservableCollectionExtended<LiveMaterialEditorMaterial> LiveMaterials { get; } = [];

    private LiveMaterialEditorMaterial _selectedLiveMaterial;
    public LiveMaterialEditorMaterial SelectedLiveMaterial
    {
        get => _selectedLiveMaterial;
        set
        {
            if (SetProperty(ref _selectedLiveMaterial, value))
            {
                SelectedLiveScalarParameter = value?.ScalarParameters.FirstOrDefault();
                SelectedLiveVectorParameter = value?.VectorParameters.FirstOrDefault(parameter =>
                                                  parameter.ParameterName.StartsWith("TNT_", StringComparison.OrdinalIgnoreCase))
                                              ?? value?.VectorParameters.FirstOrDefault();
                UpdateLiveMaterialSaveState();
            }
        }
    }

    private LiveScalarMaterialParameter _selectedLiveScalarParameter;
    public LiveScalarMaterialParameter SelectedLiveScalarParameter
    {
        get => _selectedLiveScalarParameter;
        set => SetProperty(ref _selectedLiveScalarParameter, value);
    }

    private LiveVectorMaterialParameter _selectedLiveVectorParameter;
    public LiveVectorMaterialParameter SelectedLiveVectorParameter
    {
        get => _selectedLiveVectorParameter;
        set => SetProperty(ref _selectedLiveVectorParameter, value);
    }

    private int _selectedMaterialEditLevelIndex;
    public int SelectedMaterialEditLevelIndex
    {
        get => _selectedMaterialEditLevelIndex;
        set
        {
            if (SetProperty(ref _selectedMaterialEditLevelIndex, value))
            {
                UpdateLiveMaterialSaveState();
            }
        }
    }

    public bool IsEditingComponentMaterial => SelectedMaterialEditLevelIndex == 0;
    public bool IsEditingParentMaterial => SelectedMaterialEditLevelIndex == 1;
    public bool ShowLiveMaterialEditor => IsMaterialEditorOnly || LiveMaterials.Count > 0;
    public bool CanApplyComponentMicOverrides => IsEditingComponentMaterial && GetSelectedMaterialBinding() is not null;
    public bool CanOverwriteParentMaterial => IsEditingParentMaterial && GetWritableParentMaterial() is not null;
    public bool CanCreateParentMic => IsEditingParentMaterial && GetSelectedParentMaterial() is not null;
    public bool CanRandomizeSelectedLiveMaterialTints =>
        SelectedLiveMaterial?.VectorParameters.Any(IsTintParameter) == true;

    private bool _hasActorMorph;
    public bool HasActorMorph
    {
        get => _hasActorMorph;
        private set => SetProperty(ref _hasActorMorph, value);
    }

    private bool _isMorphEditing;
    public bool IsMorphEditing
    {
        get => _isMorphEditing;
        private set
        {
            if (SetProperty(ref _isMorphEditing, value))
            {
                OnPropertyChanged(nameof(MorphModeButtonLabel));
            }
        }
    }

    public string MorphModeButtonLabel => IsMorphEditing ? "Back to actor" : "Edit morph";

    private bool _isMaterialEditorOnly;
    public bool IsMaterialEditorOnly
    {
        get => _isMaterialEditorOnly;
        set
        {
            if (SetProperty(ref _isMaterialEditorOnly, value))
            {
                OnPropertyChanged(nameof(ShowLiveMaterialEditor));
            }
        }
    }

    /// <summary>
    /// Opens the integrated morph editor as soon as the actor and its MorphHead have loaded.
    /// Used by callers that launch Actor Preview specifically to edit an actor morph.
    /// </summary>
    public bool OpenMorphEditorOnLoad { get; set; }

    public string SelectedMaterialTargetPath
    {
        get
        {
            ActorMaterialBinding binding = GetSelectedMaterialBinding();
            if (binding is null)
            {
                return null;
            }
            string slots = string.Join(", ", binding.SlotIndexes);
            return $"{binding.Component.Export.InstancedFullPath}  •  slot{(binding.SlotIndexes.Count == 1 ? string.Empty : "s")} {slots}";
        }
    }

    public string SelectedParentMaterialPath => GetSelectedParentMaterial()?.InstancedFullPath;

    public string ParentMaterialSaveHelpText => GetWritableParentMaterial() is not null
        ? "Overwrite edits the local parent MIC. Create parent MIC inserts a new MIC in the chain and keeps the component MIC's scalar/vector overrides synchronized."
        : "This parent is a base or imported material, so it cannot be overwritten in this package. Create a parent MIC to keep the edit local.";

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
        RenderContext.Camera.FirstPerson = true;
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
        AttachHostingTabSelectionHandler();
        bool shouldRender = _hostingTabItem is null || ReferenceEquals(_hostingTabControl?.SelectedItem, _hostingTabItem);
        SceneViewer.SetShouldRender(shouldRender && !IsMorphEditing);
        if (shouldRender && IsMorphEditing)
        {
            _actorMorphEditor?.StartRendering();
        }
        SceneViewer.MarkRenderDirty();

        if (!_controlIsLoaded)
        {
            _controlIsLoaded = true;
            RenderContext.UpdateScene += OnUpdateScene;
            RenderContext.RenderScene += OnRenderScene;
        }

        if (CurrentLoadedExport is not null && _actor is null)
        {
            LoadActor();
        }
    }

    private void SceneViewer_Unloaded(object sender, RoutedEventArgs e)
    {
        _actorMorphEditor?.StopRendering();
        DetachHostingTabSelectionHandler();
    }

    private void HostingTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is TabControl tabControl && ReferenceEquals(e.Source, tabControl) && _hostingTabItem is not null)
        {
            bool shouldRender = ReferenceEquals(tabControl.SelectedItem, _hostingTabItem);
            SceneViewer?.SetShouldRender(shouldRender && !IsMorphEditing);
            if (IsMorphEditing)
            {
                if (shouldRender)
                {
                    _actorMorphEditor?.StartRendering();
                }
                else
                {
                    _actorMorphEditor?.StopRendering();
                }
            }
            else if (shouldRender)
            {
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    private void AttachHostingTabSelectionHandler()
    {
        DetachHostingTabSelectionHandler();
        if (Parent is TabItem { Parent: TabControl tabControl } tabItem)
        {
            _hostingTabItem = tabItem;
            _hostingTabControl = tabControl;
            _hostingTabControl.SelectionChanged += HostingTabSelectionChanged;
        }
    }

    private void DetachHostingTabSelectionHandler()
    {
        if (_hostingTabControl is not null)
        {
            _hostingTabControl.SelectionChanged -= HostingTabSelectionChanged;
        }
        _hostingTabControl = null;
        _hostingTabItem = null;
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
        !exportEntry.IsDefaultObject
        && (ActorProxy.CanCreate(exportEntry)
            || exportEntry.IsA("SkeletalMeshComponent") && HasMorphHeadProperty(exportEntry));

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
            _actor = ActorProxy.CanCreate(requestedExport)
                ? ActorProxy.Create(this, requestedExport)
                : new SkeletalMeshComponentPreviewActorProxy(this, requestedExport);
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
                FrameFirstPersonCamera(bounds);
                ConfigureDepthRangeForBounds(bounds);
                PopulateLiveMaterialEditor(_actor);
                UpdateActorMorphAvailability();
                if (OpenMorphEditorOnLoad && HasActorMorph)
                {
                    OpenActorMorphEditor();
                }
                SceneViewer.MarkRenderDirty();
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
        Vector3 offset = actor is SkeletalMeshComponentPreviewActorProxy
            ? actor.GetBounds().Origin
            : actor.LocalToWorld.Translation;
        ApplyWorldOffset(actor, offset);
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

    private void FrameFirstPersonCamera(BoxSphereBounds bounds)
    {
        float radius = Math.Max(bounds.SphereRadius, 1f);
        SceneCamera camera = RenderContext.Camera;
        camera.FirstPerson = true;
        camera.FocusDepth = 0;
        camera.Pitch = 0;
        camera.Yaw = 0;
        camera.Position = bounds.Origin + Vector3.UnitX * Math.Max(radius * 2.2f, 100f);
        camera.OrientTowards(bounds.Origin);
        RenderContext.CameraSpeed = Math.Max(radius * 1.5f, 50f);
    }

    private void SnapCameraToActor_Click(object sender, RoutedEventArgs e)
    {
        if (_actor is null)
        {
            return;
        }

        BoxSphereBounds bounds = _actor.GetBounds();
        FrameFirstPersonCamera(bounds);
        ConfigureDepthRangeForBounds(bounds);
        SceneViewer.SetShouldRender(true);
        SceneViewer.MarkRenderDirty();
        SceneViewer.Focus();
    }

    private static bool HasMorphHeadProperty(ExportEntry export)
    {
        if (export.GetProperty<ObjectProperty>("MorphHead") is { Value: not 0 })
        {
            return true;
        }
        try
        {
            return export.GetCondensedProperties().GetProp<ObjectProperty>("MorphHead") is { Value: not 0 };
        }
        catch
        {
            // CanParse should not hide the other export tabs when inherited-property resolution fails.
            return false;
        }
    }

    private void UpdateActorMorphAvailability()
    {
        _actorMorphExport = ResolveMorphHead(CurrentLoadedExport);
        if (_actorMorphExport is null && _actor is not null)
        {
            _actorMorphExport = EnumerateActors(_actor)
                .SelectMany(actor => actor.Components.OfType<SkeletalMeshComponentProxy>())
                .Select(component => ResolveMorphHead(component.Export))
                .FirstOrDefault(morph => morph is not null);
        }

        HasActorMorph = _actorMorphExport is not null;
        if (!HasActorMorph)
        {
            CloseActorMorphEditor(unload: true);
        }
    }

    private ExportEntry ResolveMorphHead(ExportEntry owner)
    {
        if (owner is null)
        {
            return null;
        }

        ObjectProperty morphProperty = owner.GetCondensedProperties().GetProp<ObjectProperty>("MorphHead")
                                       ?? owner.GetProperty<ObjectProperty>("MorphHead");
        return morphProperty?.ResolveToExport(owner.FileRef, RenderContext.PackageCache);
    }

    private void ToggleMorphEditor_Click(object sender, RoutedEventArgs e)
    {
        if (IsMorphEditing)
        {
            CloseActorMorphEditor(unload: false);
        }
        else
        {
            OpenActorMorphEditor();
        }
    }

    private void OpenActorMorphEditor()
    {
        if (_actorMorphExport is null || CurrentLoadedExport is null)
        {
            return;
        }

        _actorMorphEditor ??= new BioMorphFaceEditor();
        ConfigureActorMorphEditorSaveTargets();
        ActorMorphEditorHost.Content = _actorMorphEditor;
        SceneViewer.SetShouldRender(false);
        IsMorphEditing = true;
        if (!ReferenceEquals(_actorMorphEditor.CurrentLoadedExport, _actorMorphExport))
        {
            _actorMorphEditor.LoadExport(_actorMorphExport);
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(_actorMorphEditor.StartRendering));
    }

    private void CloseActorMorphEditor(bool unload)
    {
        if (_actorMorphEditor is not null)
        {
            _actorMorphEditor.StopRendering();
            if (unload)
            {
                _actorMorphEditor.UnloadExport();
                ActorMorphEditorHost.Content = null;
            }
        }
        IsMorphEditing = false;
        if (SceneViewer is not null && IsLoaded)
        {
            SceneViewer.SetShouldRender(true);
            SceneViewer.MarkRenderDirty();
        }
    }

    private void ConfigureActorMorphEditorSaveTargets()
    {
        string targetType = CurrentLoadedExport.IsA("SkeletalMeshComponent")
            ? "skeletal mesh component"
            : CurrentLoadedExport.IsA("SFXStuntActor") ? "stunt actor" : "actor";
        _actorMorphEditor.MorphOverrideLabel = "Override existing morph";
        _actorMorphEditor.MorphSaveAsNewLabel = $"Apply to {targetType}…";
        bool sourceIsLocal = ReferenceEquals(_actorMorphExport?.FileRef, CurrentLoadedExport.FileRef);
        _actorMorphEditor.AllowMorphOverride = sourceIsLocal;
        _actorMorphEditor.MorphSaveHelpText = sourceIsLocal
            ? $"Override writes the currently linked BioMorphFace. Apply creates a new local morph and writes the MorphHead property on this {targetType}."
            : $"The linked morph is in another package, so it cannot be overwritten here. Apply creates an editable local morph and writes the MorphHead property on this {targetType}.";
        _actorMorphEditor.MorphNewNameValidatorOverride = ValidateActorMorphName;
        _actorMorphEditor.MorphSaveTargetCreatorOverride = CreateActorMorphSaveTarget;
        _actorMorphEditor.MorphOverrideCompletedOverride = OnActorMorphOverwritten;
        _actorMorphEditor.MorphSaveAsNewCompletedOverride = OnActorMorphCreated;
    }

    private (bool IsValid, string Error) ValidateActorMorphName(string value)
    {
        string name = value?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return (false, "Enter a morph name.");
        }
        if (!(char.IsLetter(name[0]) || name[0] == '_')
            || name.Skip(1).Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            return (false, "Use letters, numbers, and underscores; the first character cannot be a number.");
        }

        string path = $"{CurrentLoadedExport.InstancedFullPath}.{name}";
        return CurrentLoadedExport.FileRef.FindEntry(path) is null
            ? (true, null)
            : (false, "An entry with that name already exists on this preview target.");
    }

    private ExportEntry CreateActorMorphSaveTarget(ExportEntry sourceMorph, string name)
    {
        ExportEntry target = CurrentLoadedExport
                             ?? throw new InvalidOperationException("The actor preview no longer has a save target.");
        if (ReferenceEquals(sourceMorph.FileRef, target.FileRef))
        {
            ExportEntry clone = EntryCloner.CloneTree(sourceMorph);
            clone.Parent = target;
            clone.ObjectName = new NameReference(name);
            return clone;
        }

        // Only the head/hair references need to cross the package boundary. They are emitted as
        // imports when resolvable, while the editable BioMorphFace and BioMaterialOverride stay local.
        ExportEntry created = ExportCreator.CreateExport(target.FileRef, name, "BioMorphFace", target, indexed: false);
        PropertyCollection sourceProperties = sourceMorph.GetProperties();
        var createdProperties = new PropertyCollection();
        CopyMorphDependency("m_oBaseHead");
        CopyMorphDependency("m_oHairMesh");
        created.WritePropertiesAndBinary(createdProperties,
            LegendaryExplorerCore.Unreal.BinaryConverters.BioMorphFace.Create());
        return created;

        void CopyMorphDependency(NameReference propertyName)
        {
            ObjectProperty property = sourceProperties.GetProp<ObjectProperty>(propertyName);
            if (property is null || property.Value == 0)
            {
                return;
            }
            IEntry sourceEntry = property.ResolveToEntry(sourceMorph.FileRef);
            if (sourceEntry is null)
            {
                return;
            }
            var relinkerOptions = new RelinkerOptionsPackage
            {
                Cache = RenderContext.PackageCache,
                PortExportsAsImportsWhenPossible = true,
                PortImportsMemorySafe = true
            };
            IEntry localEntry = EntryImporter.GetOrAddCrossImportOrPackage(
                sourceEntry.InstancedFullPath, sourceEntry.FileRef, target.FileRef, relinkerOptions);
            createdProperties.AddOrReplaceProp(new ObjectProperty(localEntry, propertyName));
        }
    }

    private string OnActorMorphOverwritten(ExportEntry morph)
    {
        ApplyMorphToActorPreview(morph);
        return $"Overwrote {morph.InstancedFullPath}; the Actor Preview will use the updated morph.";
    }

    private string OnActorMorphCreated(ExportEntry morph)
    {
        ExportEntry target = CurrentLoadedExport
                             ?? throw new InvalidOperationException("The actor preview no longer has a MorphHead target.");
        PropertyCollection properties = target.GetProperties();
        properties.AddOrReplaceProp(new ObjectProperty(morph, "MorphHead"));
        target.WriteProperties(properties);

        _actorMorphExport = morph;
        HasActorMorph = true;
        ApplyMorphToActorPreview(morph);
        Dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() =>
            {
                if (ReferenceEquals(CurrentLoadedExport, target))
                {
                    _actorMorphEditor?.LoadExport(morph);
                }
            }));
        return $"Created {morph.InstancedFullPath} and applied MorphHead to {target.InstancedFullPath}.";
    }

    private void ApplyMorphToActorPreview(ExportEntry morph)
    {
        _actor?.ApplyMorphHead(morph);
        SceneViewer.MarkRenderDirty();
    }

    public void LoadExternalLiveMaterialEditor(ActorProxy actor, LevelEditorRenderContext renderContext)
    {
        _externalLiveMaterialActor = actor;
        _externalLiveMaterialRenderContext = renderContext;
        PopulateLiveMaterialEditor(actor);
    }

    public void UnloadExternalLiveMaterialEditor()
    {
        _externalLiveMaterialActor = null;
        _externalLiveMaterialRenderContext = null;
        ClearLiveMaterialEditor();
    }

    private PackageCache LiveMaterialPackageCache =>
        _externalLiveMaterialRenderContext?.PackageCache ?? RenderContext.PackageCache;

    private void PopulateLiveMaterialEditor(ActorProxy rootActor)
    {
        ClearLiveMaterialEditor();

        foreach (ActorProxy actor in EnumerateActors(rootActor))
        {
            foreach (MeshComponentProxy component in actor.Components.OfType<MeshComponentProxy>())
            {
                foreach ((IEntry sourceEntry, MaterialRenderProxy renderProxy, IReadOnlyList<int> slotIndexes)
                         in component.GetLiveMaterialBindings())
                {
                    string slots = string.Join(",", slotIndexes);
                    string displayName = $"{component.Export.ObjectName.Instanced} [{slots}] — {sourceEntry.ObjectName.Instanced}";
                    var material = new LiveMaterialEditorMaterial(renderProxy, sourceEntry, displayName,
                        sourceEntry.InstancedFullPath);
                    material.PreviewChanged += LiveMaterial_PreviewChanged;
                    LiveMaterials.Add(material);
                    _materialBindings[material] = new ActorMaterialBinding
                    {
                        Component = component,
                        SourceEntry = sourceEntry,
                        SlotIndexes = slotIndexes
                    };
                }
            }
        }

        SelectedLiveMaterial = LiveMaterials.FirstOrDefault();
        OnPropertyChanged(nameof(ShowLiveMaterialEditor));
    }

    private static IEnumerable<ActorProxy> EnumerateActors(ActorProxy root)
    {
        if (root is null)
        {
            yield break;
        }

        var pending = new Stack<ActorProxy>();
        var visited = new HashSet<ActorProxy>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ActorProxy actor = pending.Pop();
            if (!visited.Add(actor))
            {
                continue;
            }
            yield return actor;
            foreach (ActorProxy attached in actor.Attached)
            {
                pending.Push(attached);
            }
        }
    }

    private void ClearLiveMaterialEditor()
    {
        bool discardedPreviewChanges = false;
        foreach (LiveMaterialEditorMaterial material in LiveMaterials)
        {
            material.PreviewChanged -= LiveMaterial_PreviewChanged;
            discardedPreviewChanges |= material.DiscardUnsavedChanges();
        }
        LiveMaterials.ClearEx();
        _materialBindings.Clear();
        SelectedLiveMaterial = null;
        OnPropertyChanged(nameof(ShowLiveMaterialEditor));
        if (discardedPreviewChanges)
        {
            SceneViewer?.MarkRenderDirty();
            LiveMaterialPreviewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void LiveMaterial_PreviewChanged(object sender, EventArgs e)
    {
        SceneViewer?.MarkRenderDirty();
        LiveMaterialPreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CloseEmbeddedMaterialEditor_Click(object sender, RoutedEventArgs e) =>
        CloseMaterialEditorRequested?.Invoke(this, EventArgs.Empty);

    private ActorMaterialBinding GetSelectedMaterialBinding() =>
        SelectedLiveMaterial is not null && _materialBindings.TryGetValue(SelectedLiveMaterial, out ActorMaterialBinding binding)
            ? binding
            : null;

    private ExportEntry GetAttachedMic(ActorMaterialBinding binding)
    {
        ArrayProperty<ObjectProperty> materials = binding?.Component.Export
            .GetProperty<ArrayProperty<ObjectProperty>>("Materials");
        if (materials is null)
        {
            return null;
        }

        foreach (int slotIndex in binding.SlotIndexes)
        {
            if (slotIndex < materials.Count
                && materials[slotIndex].ResolveToEntry(binding.Component.Export.FileRef) is ExportEntry candidate
                && candidate.IsA("MaterialInstanceConstant"))
            {
                return candidate;
            }
        }
        return null;
    }

    private IEntry GetSelectedParentMaterial()
    {
        ActorMaterialBinding binding = GetSelectedMaterialBinding();
        if (binding is null)
        {
            return null;
        }

        ExportEntry attachedMic = GetAttachedMic(binding);
        return attachedMic?.GetProperty<ObjectProperty>("Parent")?.ResolveToEntry(attachedMic.FileRef)
               ?? binding.SourceEntry;
    }

    private ExportEntry GetWritableParentMaterial()
    {
        ActorMaterialBinding binding = GetSelectedMaterialBinding();
        return GetSelectedParentMaterial() is ExportEntry parent
               && binding is not null
               && parent.FileRef == binding.Component.Export.FileRef
               && parent.IsA("MaterialInstanceConstant")
            ? parent
            : null;
    }

    private void UpdateLiveMaterialSaveState()
    {
        OnPropertyChanged(nameof(IsEditingComponentMaterial));
        OnPropertyChanged(nameof(IsEditingParentMaterial));
        OnPropertyChanged(nameof(CanApplyComponentMicOverrides));
        OnPropertyChanged(nameof(CanOverwriteParentMaterial));
        OnPropertyChanged(nameof(CanCreateParentMic));
        OnPropertyChanged(nameof(CanRandomizeSelectedLiveMaterialTints));
        OnPropertyChanged(nameof(SelectedMaterialTargetPath));
        OnPropertyChanged(nameof(SelectedParentMaterialPath));
        OnPropertyChanged(nameof(ParentMaterialSaveHelpText));
    }

    private void RandomizeActorMaterialTints_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRandomizeSelectedLiveMaterialTints || SelectedLiveMaterial is not { } material)
        {
            return;
        }

        foreach (LiveVectorMaterialParameter parameter in material.VectorParameters.Where(IsTintParameter))
        {
            parameter.SetValue(Random.Shared.NextSingle(), Random.Shared.NextSingle(), Random.Shared.NextSingle(), parameter.A);
        }
    }

    private static bool IsTintParameter(LiveVectorMaterialParameter parameter) =>
        parameter.ParameterName.StartsWith("TNT_", StringComparison.OrdinalIgnoreCase);

    private void SceneViewer_PreviewMouseDownForMaterialPicking(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _actor is not null && LiveMaterials.Count > 0)
        {
            _materialPickMouseDownPosition = e.GetPosition(SceneViewer);
        }
    }

    private void SceneViewer_PreviewMouseUpForMaterialPicking(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _materialPickMouseDownPosition is not { } mouseDownPosition)
        {
            return;
        }

        _materialPickMouseDownPosition = null;
        System.Windows.Point mouseUpPosition = e.GetPosition(SceneViewer);
        System.Windows.Vector clickMovement = mouseUpPosition - mouseDownPosition;
        if (clickMovement.LengthSquared > 16)
        {
            return;
        }

        TrySelectLiveMaterialVectorAtPixel(_actor, mouseUpPosition, SceneViewer, RenderContext);
    }

    public bool TrySelectLiveMaterialVectorAtPixel(ActorProxy actor, System.Windows.Point screenPosition,
        SceneRenderControl viewport, LevelEditorRenderContext renderContext)
    {
        actor ??= _externalLiveMaterialActor;
        if (!TryPickActorMaterials(actor, screenPosition, viewport, renderContext,
                out List<LiveMaterialEditorMaterial> hitMaterials)
            || !TryFindInfluencingVectorParameter(screenPosition, viewport, renderContext, hitMaterials,
                out LiveMaterialEditorMaterial selectedMaterial, out LiveVectorMaterialParameter selectedParameter))
        {
            return false;
        }

        selectedMaterial.VectorFilterText = null;
        SelectedLiveMaterial = selectedMaterial;
        SelectedLiveVectorParameter = selectedParameter;
        FocusSelectedLiveVectorParameter();
        return true;
    }

    private bool TryPickActorMaterials(ActorProxy actor, System.Windows.Point screenPosition,
        SceneRenderControl viewport, LevelEditorRenderContext renderContext,
        out List<LiveMaterialEditorMaterial> hitMaterials)
    {
        hitMaterials = [];
        if (actor is null || viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0)
        {
            return false;
        }

        float normalizedX = (float)(screenPosition.X / viewport.ActualWidth * 2.0 - 1.0);
        float normalizedY = (float)(1.0 - screenPosition.Y / viewport.ActualHeight * 2.0);
        Matrix4x4 viewProjection = renderContext.Camera.ViewMatrix * renderContext.Camera.ProjectionMatrix;
        if (!Matrix4x4.Invert(viewProjection, out Matrix4x4 inverseViewProjection))
        {
            return false;
        }

        Vector4 nearClip = Vector4.Transform(new Vector4(normalizedX, normalizedY, 0, 1), inverseViewProjection);
        Vector4 farClip = Vector4.Transform(new Vector4(normalizedX, normalizedY, 1, 1), inverseViewProjection);
        if (Math.Abs(nearClip.W) < float.Epsilon || Math.Abs(farClip.W) < float.Epsilon)
        {
            return false;
        }

        Vector3 rayOrigin = new(nearClip.X / nearClip.W, nearClip.Y / nearClip.W, nearClip.Z / nearClip.W);
        Vector3 farPoint = new(farClip.X / farClip.W, farClip.Y / farClip.W, farClip.Z / farClip.W);
        Vector3 rayDirection = Vector3.Normalize(farPoint - rayOrigin);
        var nearestByMaterial = new Dictionary<LiveMaterialEditorMaterial, float>();
        foreach (MeshComponentProxy component in EnumerateActors(actor)
                     .SelectMany(actor => actor.Components.OfType<MeshComponentProxy>()))
        {
            foreach ((MaterialRenderProxy renderProxy, float distance) in component.GetLiveMaterialHits(rayOrigin, rayDirection))
            {
                LiveMaterialEditorMaterial material = LiveMaterials.FirstOrDefault(candidate =>
                    ReferenceEquals(candidate.RenderProxy, renderProxy));
                if (material is not null
                    && (!nearestByMaterial.TryGetValue(material, out float nearestDistance) || distance < nearestDistance))
                {
                    nearestByMaterial[material] = distance;
                }
            }
        }

        hitMaterials = nearestByMaterial.OrderBy(pair => pair.Value).Select(pair => pair.Key).ToList();
        return hitMaterials.Count > 0;
    }

    private bool TryFindInfluencingVectorParameter(System.Windows.Point screenPosition,
        SceneRenderControl viewport, LevelEditorRenderContext renderContext,
        IReadOnlyCollection<LiveMaterialEditorMaterial> hitMaterials,
        out LiveMaterialEditorMaterial selectedMaterial,
        out LiveVectorMaterialParameter selectedParameter)
    {
        selectedMaterial = null;
        selectedParameter = null;
        if (renderContext.Backbuffer is null || renderContext.Width <= 0 || renderContext.Height <= 0
            || viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0)
        {
            return false;
        }

        List<(LiveMaterialEditorMaterial Material, LiveVectorMaterialParameter Parameter)> candidates =
            hitMaterials.SelectMany(material => material.VectorParameters
                .Where(parameter => !IsGlobalOverlayParameter(parameter))
                .Select(parameter => (Material: material, Parameter: parameter)))
                .ToList();
        if (candidates.Count == 0)
        {
            return false;
        }

        int pixelX = Math.Clamp((int)(screenPosition.X / viewport.ActualWidth * renderContext.Width),
            0, renderContext.Width - 1);
        int pixelY = Math.Clamp((int)(screenPosition.Y / viewport.ActualHeight * renderContext.Height),
            0, renderContext.Height - 1);

        renderContext.Render();
        if (!renderContext.TryReadBackbufferPixelNeighborhood(pixelX, pixelY, out Vector4 baselineColor))
        {
            return false;
        }

        float strongestResponse = 0;
        try
        {
            foreach ((LiveMaterialEditorMaterial material, LiveVectorMaterialParameter parameter) in candidates)
            {
                var currentValue = new LinearColor(parameter.R, parameter.G, parameter.B, parameter.A);
                material.RenderProxy.SetVectorParameter(parameter.ParameterName, CreateVectorParameterProbe(currentValue));
                try
                {
                    renderContext.Render();
                    if (renderContext.TryReadBackbufferPixelNeighborhood(pixelX, pixelY, out Vector4 probeColor))
                    {
                        Vector3 response = new(probeColor.X - baselineColor.X,
                            probeColor.Y - baselineColor.Y, probeColor.Z - baselineColor.Z);
                        float responseStrength = response.LengthSquared();
                        if (responseStrength > strongestResponse)
                        {
                            strongestResponse = responseStrength;
                            selectedMaterial = material;
                            selectedParameter = parameter;
                        }
                    }
                }
                finally
                {
                    material.RenderProxy.SetVectorParameter(parameter.ParameterName, currentValue);
                }
            }
        }
        finally
        {
            renderContext.Render();
            viewport.MarkRenderDirty();
        }

        const float minimumResponse = 3f / (255f * 255f);
        return selectedParameter is not null && strongestResponse >= minimumResponse;
    }

    private static LinearColor CreateVectorParameterProbe(LinearColor value)
    {
        static float FarthestEndpoint(float component) => Math.Abs(component) >= Math.Abs(component - 1f) ? 0f : 1f;
        return new LinearColor(FarthestEndpoint(value.R), FarthestEndpoint(value.G),
            FarthestEndpoint(value.B), FarthestEndpoint(value.A));
    }

    private static bool IsGlobalOverlayParameter(LiveVectorMaterialParameter parameter)
    {
        string name = parameter.ParameterName;
        return name.Contains("Selection", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Highlight", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Overlay", StringComparison.OrdinalIgnoreCase);
    }

    private void FocusSelectedLiveVectorParameter()
    {
        LiveVectorMaterialParameter parameter = SelectedLiveVectorParameter;
        if (parameter is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            LiveVectorParameterList.ScrollIntoView(parameter);
            LiveVectorParameterList.UpdateLayout();
            if (LiveVectorParameterList.ItemContainerGenerator.ContainerFromItem(parameter) is FrameworkElement container)
            {
                container.BringIntoView();
                FindVisualDescendant<Xceed.Wpf.Toolkit.ColorCanvas>(container)?.Focus();
            }
        }));
    }

    private static T FindVisualDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is null)
        {
            return null;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }
            if (FindVisualDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }
        return null;
    }

    private void AddActorMaterialScalar_Click(object sender, RoutedEventArgs e) => AddActorMaterialParameter(isVector: false);
    private void AddActorMaterialVector_Click(object sender, RoutedEventArgs e) => AddActorMaterialParameter(isVector: true);

    private void AddActorMaterialParameter(bool isVector)
    {
        if (SelectedLiveMaterial is not { } material)
        {
            return;
        }

        IReadOnlyList<string> parameterNames;
        try
        {
            using var cache = new PackageCache();
            var materialInfo = new MaterialInfo { MaterialExport = material.MaterialExport };
            IEnumerable<string> hierarchyNames = isVector
                ? materialInfo.GetVectorParameterNames(cache)
                : materialInfo.GetScalarParameterNames(cache);
            IEnumerable<string> shaderNames = isVector
                ? material.RenderProxy.VectorParameters.Keys
                : material.RenderProxy.ScalarParameters.Keys;
            parameterNames = hierarchyNames.Concat(shaderNames)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"The material's parameter list could not be loaded.\n\n{exception.Message}",
                "Material parameters unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string parameterType = isVector ? "vector" : "scalar";
        if (parameterNames.Count == 0)
        {
            MessageBox.Show($"No {parameterType} parameters were found on this material or its parent material.",
                "No material parameters found", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        HashSet<string> existingNames = new(isVector
                ? material.VectorParameters.Select(parameter => parameter.ParameterName)
                : material.ScalarParameters.Select(parameter => parameter.ParameterName),
            StringComparer.OrdinalIgnoreCase);
        string selectedName = StringSelectorDialog.GetValue(this,
            $"Choose a {parameterType} parameter. Type to search the {parameterNames.Count} values supported by this material.",
            $"Add {parameterType} parameter",
            parameterNames.Select(name => new StringSelectorItem(name, name,
                existingNames.Contains(name) ? "Already present" : $"Available {parameterType} parameter")));
        if (string.IsNullOrWhiteSpace(selectedName))
        {
            return;
        }

        if (isVector)
        {
            SelectedLiveVectorParameter = material.AddVectorParameter(selectedName);
            LiveVectorParameterList.ScrollIntoView(SelectedLiveVectorParameter);
        }
        else
        {
            SelectedLiveScalarParameter = material.AddScalarParameter(selectedName);
            LiveScalarParameterList.ScrollIntoView(SelectedLiveScalarParameter);
        }
        UpdateLiveMaterialSaveState();
    }

    private void RemoveActorMaterialScalar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LiveScalarMaterialParameter parameter }
            || SelectedLiveMaterial is not { } material)
        {
            return;
        }

        int removedIndex = material.ScalarParameters.IndexOf(parameter);
        if (material.RemoveScalarParameter(parameter))
        {
            SelectedLiveScalarParameter = material.ScalarParameters.Count == 0
                ? null
                : material.ScalarParameters[Math.Min(removedIndex, material.ScalarParameters.Count - 1)];
        }
    }

    private void RemoveActorMaterialVector_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LiveVectorMaterialParameter parameter }
            || SelectedLiveMaterial is not { } material)
        {
            return;
        }

        int removedIndex = material.VectorParameters.IndexOf(parameter);
        if (material.RemoveVectorParameter(parameter))
        {
            SelectedLiveVectorParameter = material.VectorParameters.Count == 0
                ? null
                : material.VectorParameters[Math.Min(removedIndex, material.VectorParameters.Count - 1)];
            UpdateLiveMaterialSaveState();
        }
    }

    private void ActorMaterialParameterScrubber_DragDelta(object sender, DragDeltaEventArgs e)
    {
        float speedMultiplier = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10f
            : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 0.1f
            : 1f;
        if (sender is FrameworkElement { DataContext: LiveScalarMaterialParameter scalar })
        {
            float unitsPerPixel = Math.Max(Math.Abs(scalar.Value) * 0.01f, 0.01f);
            scalar.Value += (float)e.HorizontalChange * unitsPerPixel * speedMultiplier;
        }
    }

    private void ApplyComponentMicOverrides_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLiveMaterial is not { } material || GetSelectedMaterialBinding() is not { } binding)
        {
            return;
        }

        try
        {
            ExportEntry attachedMic = EnsureAttachedMic(binding);
            MeshRenderer.WriteLiveMaterialParameters(attachedMic, material);
            // Link after serialization so an external viewport that rebuilds on the component update
            // sees the completed MIC rather than an empty, just-created instance.
            SetComponentMaterialSlots(binding, attachedMic);
            material.MarkSaved();
            UpdateLiveMaterialSaveState();
        }
        catch (Exception exception)
        {
            new ExceptionHandlerDialog(exception).ShowDialog();
        }
    }

    private void OverwriteParentMaterial_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLiveMaterial is not { } material || GetWritableParentMaterial() is not { } parentMic)
        {
            return;
        }

        try
        {
            MeshRenderer.WriteLiveMaterialParameters(parentMic, material);
            material.MarkSaved();
            UpdateLiveMaterialSaveState();
        }
        catch (Exception exception)
        {
            new ExceptionHandlerDialog(exception).ShowDialog();
        }
    }

    private void CreateParentMic_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLiveMaterial is not { } material
            || GetSelectedMaterialBinding() is not { } binding
            || GetSelectedParentMaterial() is not { } currentParent)
        {
            return;
        }

        try
        {
            IEntry localParentReference = InterpreterExportLoader.GetOrAddLocalMaterialReference(
                currentParent, binding.Component.Export.FileRef, LiveMaterialPackageCache)
                ?? throw new InvalidOperationException("Could not create a local reference to the selected parent material.");
            ExportEntry currentParentExport = ResolveMaterialExport(currentParent);
            ExportEntry newParentMic = InterpreterExportLoader.CreateMaterialInstanceConstant(
                binding.Component.Export, localParentReference, currentParentExport);
            MeshRenderer.WriteLiveMaterialParameters(newParentMic, material);

            ExportEntry attachedMic = GetAttachedMic(binding);
            if (attachedMic is null)
            {
                attachedMic = InterpreterExportLoader.CreateMaterialInstanceConstant(
                    binding.Component.Export, newParentMic, newParentMic);
            }
            else
            {
                InterpreterExportLoader.UpdateMaterialInstanceConstantParent(
                    attachedMic, newParentMic, newParentMic, regenerateGuid: false);
            }

            // Keep the component-level instance synchronized as requested, even though the new parent
            // also owns the edited values. This preserves the visible result if the parent is later changed.
            MeshRenderer.WriteLiveMaterialParameters(attachedMic, material);
            SetComponentMaterialSlots(binding, attachedMic);
            material.MarkSaved();
            UpdateLiveMaterialSaveState();
        }
        catch (Exception exception)
        {
            new ExceptionHandlerDialog(exception).ShowDialog();
        }
    }

    private ExportEntry EnsureAttachedMic(ActorMaterialBinding binding)
    {
        if (GetAttachedMic(binding) is { } attachedMic)
        {
            return attachedMic;
        }

        IEntry localParentReference = InterpreterExportLoader.GetOrAddLocalMaterialReference(
            binding.SourceEntry, binding.Component.Export.FileRef, LiveMaterialPackageCache)
            ?? throw new InvalidOperationException("Could not create a local reference to the component material.");
        ExportEntry parentExport = ResolveMaterialExport(binding.SourceEntry);
        attachedMic = InterpreterExportLoader.CreateMaterialInstanceConstant(
            binding.Component.Export, localParentReference, parentExport);
        return attachedMic;
    }

    private ExportEntry ResolveMaterialExport(IEntry entry)
    {
        if (entry is ExportEntry export)
        {
            return export;
        }
        if (entry is ImportEntry import)
        {
            return EntryImporter.ResolveImport(import, LiveMaterialPackageCache);
        }
        return null;
    }

    private static void SetComponentMaterialSlots(ActorMaterialBinding binding, ExportEntry material)
    {
        ExportEntry componentExport = binding.Component.Export;
        PropertyCollection properties = componentExport.GetProperties();
        ArrayProperty<ObjectProperty> materials = properties.GetProp<ArrayProperty<ObjectProperty>>("Materials")
                                                   ?? new ArrayProperty<ObjectProperty>("Materials");
        foreach (int slotIndex in binding.SlotIndexes.OrderBy(index => index))
        {
            while (materials.Count <= slotIndex)
            {
                materials.Add(new ObjectProperty(0));
            }
            materials[slotIndex] = new ObjectProperty(material);
        }
        properties.AddOrReplaceProp(materials);
        componentExport.WriteProperties(properties);
    }

    public override void UnloadExport()
    {
        _actorLoadVersion++;
        CloseActorMorphEditor(unload: true);
        _actorMorphExport = null;
        HasActorMorph = false;
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
        ClearLiveMaterialEditor();
        RenderContext.EmptyCaches();
        CurrentLoadedExport = null;
    }

    public override void PopOut() =>
        new ExportLoaderHostedWindow(new ActorPreviewControl(), CurrentLoadedExport).Show();

    public override void Dispose()
    {
        _actorLoadVersion++;
        DetachHostingTabSelectionHandler();
        ThemeManager.ThemeChanged -= OnThemeChanged;
        RenderContext.UpdateScene -= OnUpdateScene;
        RenderContext.RenderScene -= OnRenderScene;
        _actor?.Dispose();
        _actor = null;
        _actorMorphEditor?.Dispose();
        _actorMorphEditor = null;
        _externalLiveMaterialActor = null;
        _externalLiveMaterialRenderContext = null;
        ClearLiveMaterialEditor();
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
