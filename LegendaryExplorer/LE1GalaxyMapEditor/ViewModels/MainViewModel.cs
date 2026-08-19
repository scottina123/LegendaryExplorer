using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using LE1GalaxyMapEditor.Workflows;
using LE1GalaxyMapEditor.Workflows.Editing;
using LE1GalaxyMapEditor.Workflows.Ports;
using LE1GalaxyMapEditor.Workflows.Queries;
using LE1GalaxyMapEditor.Controls;
using LE1GalaxyMapEditor.Infrastructure;
using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Presentation;
using LE1GalaxyMapEditor.Services;
using LE1GalaxyMapEditor.Views;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;

namespace LE1GalaxyMapEditor.ViewModels;

public sealed record PlanetDesignerRequestedEventArgs(GalaxyMapRowKey PlanetKey, string ModuleTag);

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly CsvGalaxyMapLoader _loader;
    private readonly GalaxyMapTextureService _textures;
    private readonly GalaxyMapModuleManifestStore _manifestStore = new();
    private readonly GalaxyMapWorkspaceStore _workspaceStore;
    private readonly bool _usesPccModules;
    private readonly GalaxyMapTlkService _tlkService;
    private readonly Action<MELocalization>? _saveBaseGameLocale;
    private readonly IEditorDialogs _dialogs;
    private readonly ValidationCoordinator _validation;
    private readonly ValidationRepairPolicy _identityRepairs;
    private readonly EditorSession _session = new();
    private readonly EditSessionService _edits;
    private readonly WorkspaceWorkflowService _workspaceWorkflows;
    private readonly RelayWorkflow _relay;
    private readonly ClusterTextureWorkflow _clusterTextures;
    private readonly PlanetTextureWorkflow _planetTextures;
    private readonly PlanetRelationshipWorkflow _planetRelationships;
    private readonly InspectorEditWorkflow _inspectorEdits;
    private readonly RowAuthoringWorkflow _rowAuthoring;
    private readonly PlanetDesignerWorkflow _planetDesigner;
    private readonly HierarchyNavigationCoordinator _navigation;
    private readonly CommitPreviewBuilder _commitPreviewBuilder;
    private readonly object _planetTextureWarmupLock = new();
    private Task? _planetTextureWarmupTask;

    private GalaxyMapWorkspace? _workspace;
    private GalaxyMapDocument? _document;
    private string _statusMessage = "Loading the built-in LE1 galaxy map.";
    private string _errorMessage = string.Empty;
    private bool _isCoordinateGridVisible;
    private bool _isShiftDragMode;
    private CoordinateDragSession? _coordinateDrag;
    private bool _isDiagnosticsPanelOpen;
    private bool _isTableViewVisible;
    private bool _isApplyingTableCellEdit;
    private bool _isApplying;
    private bool _isDisposed;
    private string _hierarchySearch = string.Empty;
    private MELocalization _baseGameTlkLocale;

    public MainViewModel(
        CsvGalaxyMapLoader loader,
        GalaxyMapTextureService? textures = null,
        GalaxyMapWorkspaceStore? workspaceStore = null,
        Func<GalaxyMapRow, IReadOnlyList<GalaxyMapModule>, GalaxyMapModule?>? editTargetSelector = null,
        Func<string, bool>? confirmAction = null,
        IEditorDialogs? dialogs = null,
        IDeferredScheduler? deferredScheduler = null,
        Func<PlanetShaderNameRequest, string?>? shaderNameSelector = null,
        Func<CommitPreview, bool>? commitReviewAction = null,
        Func<ClusterLabelRequest, string?>? clusterLabelSelector = null,
        GalaxyMapProfileWorkspaceStore? profileWorkspaceStore = null,
        GalaxyMapModuleProfileStore? profileStore = null,
        GalaxyMapTlkService? tlkService = null,
        MELocalization baseGameTlkLocale = MELocalization.INT,
        Action<MELocalization>? saveBaseGameLocale = null)
    {
        _loader = loader;
        _textures = textures ?? new GalaxyMapTextureService();
        _workspaceStore = workspaceStore ?? new GalaxyMapWorkspaceStore();
        _usesPccModules = workspaceStore is null || profileWorkspaceStore is not null;
        var activeProfileWorkspaceStore = _usesPccModules
            ? profileWorkspaceStore ?? new GalaxyMapProfileWorkspaceStore()
            : null;
        var activeProfileStore = profileStore ?? new GalaxyMapModuleProfileStore();
        _tlkService = tlkService ?? new GalaxyMapTlkService();
        _baseGameTlkLocale = GalaxyMapModule.SupportedTlkLocales.Contains(baseGameTlkLocale)
            ? baseGameTlkLocale
            : MELocalization.INT;
        _saveBaseGameLocale = saveBaseGameLocale;
        _tlkService.Reload();
        _dialogs = dialogs ?? new WpfEditorDialogs(
            editTargetSelector,
            confirmAction,
            shaderNameSelector,
            commitReviewAction,
            clusterLabelSelector,
            _tlkService,
            () => ActiveModule);
        _commitPreviewBuilder = new CommitPreviewBuilder(_manifestStore);
        _validation = new ValidationCoordinator(deferredScheduler ?? new DispatcherDeferredScheduler());
        _validation.Completed += ValidationOnCompleted;
        _identityRepairs = new ValidationRepairPolicy(() => ValidationDiagnostics);
        _edits = new EditSessionService(
            _session,
            manifestStore: _manifestStore,
            profileStore: activeProfileStore);
        _workspaceWorkflows = new WorkspaceWorkflowService(
            _session,
            _edits,
            _loader,
            _manifestStore,
            _workspaceStore,
            activeProfileWorkspaceStore,
            activeProfileStore);
        _relay = new RelayWorkflow(_session, _edits);
        _relay.StateChanged += RelayStateOnChanged;
        _clusterTextures = new ClusterTextureWorkflow(_session, _edits, _textures);
        _planetTextures = new PlanetTextureWorkflow(_session, _edits, _textures);
        _planetRelationships = new PlanetRelationshipWorkflow(_session, _edits);
        _inspectorEdits = new InspectorEditWorkflow(_session, _edits, _identityRepairs.CanRepair);
        TableViewer = new TableViewerViewModel(
            new TableProjectionService(_session),
            ApplyTableCellEdit,
            () => Workspace?.Modules.Any(module => !module.IsReadOnly && !module.IsBaseGame) == true,
            _identityRepairs,
            _tlkService,
            ResolveTlkLocale);
        _rowAuthoring = new RowAuthoringWorkflow(_session, _edits, _inspectorEdits);
        _planetDesigner = new PlanetDesignerWorkflow(_session, _edits);
        _session.Changed += SessionOnChanged;
        Inspector = new PropertyInspectorViewModel(new MainInspectorPresentationWorkflow(
            _session,
            _relay,
            _identityRepairs,
            () => HasActiveModule,
            BeginUserEdit,
            _inspectorEdits.ValidateEdit,
            ApplyManagedInspectorEdit,
            ExecuteInspectorAction), _tlkService, ResolveTlkLocale);
        _navigation = new HierarchyNavigationCoordinator(
            _session,
            Inspector,
            _textures,
            GetClusterTexture,
            () => HasActiveModule,
            () => IsAddingRelay,
            HandlePendingRelaySelection,
            CloneRow,
            DeleteRow,
            MoveRowDialog,
            RequestPlanetDesigner,
            CanMoveOwnedRow,
            AddChildToHierarchyNode,
            ModelOnPropertyChanged,
            module => SetActiveModule(module));
        _navigation.Changed += NavigationOnChanged;

        CreateModuleCommand = new RelayCommand(CreateModuleDialog, () => HasDocument);
        OpenModuleCommand = new RelayCommand(OpenModuleDialog, () => HasDocument);
        RefreshRememberedWorkspaceCommand = new RelayCommand(
            () => RefreshRememberedWorkspace(),
            () => HasDocument);
        ConfigureBaseGameCommand = new RelayCommand(ConfigureBaseGame);
        DismissErrorCommand = new RelayCommand(
            () => ErrorMessage = string.Empty,
            () => HasError);
        AddClusterCommand = new RelayCommand(AddCluster, () => HasActiveModule);
        AddSystemCommand = new RelayCommand(AddSystem, () => HasActiveModule && CurrentCluster is not null);
        AddPlanetCommand = new RelayCommand(AddPlanet, () => HasActiveModule && CurrentSystem is not null);
        ContextualAddCommand = new RelayCommand(AddForCurrentView, CanAddForCurrentView);
        NavigateGalaxyCommand = new RelayCommand(NavigateGalaxy, () => HasDocument);
        NavigateClusterCommand = new RelayCommand(
            () =>
            {
                if (CurrentCluster is not null)
                {
                    NavigateCluster(CurrentCluster);
                }
            },
            () => CurrentCluster is not null);
        ToggleCoordinateGridCommand = new RelayCommand(ToggleCoordinateGrid);
        ToggleTableViewCommand = new RelayCommand(ToggleTableView, () => HasDocument);
        ToggleDiagnosticsCommand = new RelayCommand(ToggleDiagnostics);
        NavigateDiagnosticCommand = new RelayCommand<ValidationDiagnostic>(NavigateToDiagnostic);
        CommitCommand = new RelayCommand(ReviewAndCommitPendingChanges, () => HasPendingChanges);
        UndoCommand = new RelayCommand(Undo, () => _edits.CanUndo);
        RedoCommand = new RelayCommand(Redo, () => _edits.CanRedo);
        DiscardChangesCommand = new RelayCommand(ConfirmDiscardChanges, () => HasPendingChanges);
        CancelRelayCommand = new RelayCommand(CancelRelayCreation);
    }

    public ObservableCollection<HierarchyNodeViewModel> HierarchyRoots => _navigation.HierarchyRoots;
    public string HierarchySearch
    {
        get => _hierarchySearch;
        set
        {
            if (SetProperty(ref _hierarchySearch, value ?? string.Empty))
            {
                ApplyHierarchySearch();
            }
        }
    }
    public event EventHandler<PlanetDesignerRequestedEventArgs>? PlanetDesignerRequested;
    public BulkObservableCollection<GalaxyMapModule> MountedModules { get; } = [];
    public BulkObservableCollection<ModuleBarItemViewModel> ModuleBarItems { get; } = [];
    public ObservableCollection<RowInstanceTabViewModel> RowInstanceTabs => _navigation.RowInstanceTabs;
    public BulkObservableCollection<ValidationDiagnostic> ValidationDiagnostics { get; } = [];
    public PropertyInspectorViewModel Inspector { get; }
    public TableViewerViewModel TableViewer { get; }

    public GalaxyMapWorkspace? Workspace
    {
        get => _workspace;
        private set
        {
            if (SetProperty(ref _workspace, value))
            {
                _session.Workspace = value;
                OnPropertyChanged(nameof(ActiveModule));
                OnPropertyChanged(nameof(HasActiveModule));
                OnPropertyChanged(nameof(ActiveModuleDisplay));
                OnPropertyChanged(nameof(ActiveModuleColor));
            }
        }
    }

    public GalaxyMapDocument? Document
    {
        get => _document;
        private set
        {
            if (SetProperty(ref _document, value))
            {
                OnPropertyChanged(nameof(HasDocument));
                OnPropertyChanged(nameof(SourceFolder));
            }
        }
    }

    public GalaxyMapModule? ActiveModule => Workspace?.ActiveModule;
    public bool HasActiveModule => ActiveModule is { IsReadOnly: false, IsBaseGame: false };
    public EditorSession Session => _session;
    public bool HasPendingChanges => _session.Changes.HasChanges;
    public int PendingChangeCount => _session.Changes.Count;
    public string CommitButtonText => HasPendingChanges ? $"Commit ({PendingChangeCount})" : "Commit";
    public bool HasMultipleRowInstances => _navigation.HasMultipleRowInstances;
    public string ActiveModuleDisplay => ActiveModule is null
        ? "No active authoring module"
        : ActiveModule.Name;
    public ModuleColor ActiveModuleColor => ActiveModule?.Color ?? ModuleColor.BaseGameBlue;
    public MELocalization BaseGameTlkLocale
    {
        get => _baseGameTlkLocale;
        private set
        {
            if (SetProperty(ref _baseGameTlkLocale, value))
            {
                OnPropertyChanged(nameof(BaseGameToolTip));
            }
        }
    }
    public string BaseGameToolTip => $"Click to configure BASEGAME settings (TLK locale: {BaseGameTlkLocale})";
    public object? CurrentViewModel => _navigation.CurrentViewModel;

    public bool HasContextualAddAction => CurrentViewModel is GalaxyViewModel or ClusterViewModel or SystemViewModel;
    public string ContextualAddButtonText => CurrentViewModel switch
    {
        GalaxyViewModel => "Add Cluster",
        ClusterViewModel => "Add System",
        SystemViewModel => "Add Planet/Object",
        _ => string.Empty
    };
    public string ContextualAddToolTip => CurrentViewModel switch
    {
        GalaxyViewModel => "Add a Cluster to the active module",
        ClusterViewModel => "Add a System to this Cluster",
        SystemViewModel => "Add a Planet or system object to this System",
        _ => string.Empty
    };

    public Cluster? CurrentCluster => _navigation.CurrentCluster;

    public GalaxySystem? CurrentSystem => _navigation.CurrentSystem;

    public bool HasDocument => Document is not null;
    public bool HasCurrentCluster => CurrentCluster is not null;
    public bool HasCurrentSystem => CurrentSystem is not null;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasDiagnostics => ValidationDiagnostics.Count > 0;
    public int DiagnosticCount => ValidationDiagnostics.Count;
    public int ValidationErrorCount => ValidationDiagnostics.Count(item => item.Severity == ValidationSeverity.Error);
    public int ValidationWarningCount => ValidationDiagnostics.Count(item => item.Severity == ValidationSeverity.Warning);

    public bool IsAddingRelay => PendingRelaySource is not null;
    public string RelayLinkPrompt => _relay.Prompt;
    public string SourceFolder => Workspace is not null
        ? Workspace.ModuleLayers.Count == 0
            ? CsvGalaxyMapLoader.BuiltInSourceName
            : string.Join(" + ", Workspace.Layers.Select(layer => layer.Module.Tag))
        : Document?.SourceFolder ?? "No galaxy map loaded";
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                DismissErrorCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public Cluster? PendingRelaySource => _relay.Source;

    public bool IsCoordinateGridVisible
    {
        get => _isCoordinateGridVisible;
        private set
        {
            if (SetProperty(ref _isCoordinateGridVisible, value))
            {
                OnPropertyChanged(nameof(CoordinateGridButtonText));
                OnPropertyChanged(nameof(IsCoordinateOverlayVisible));
            }
        }
    }

    public bool IsShiftDragMode
    {
        get => _isShiftDragMode;
        private set
        {
            if (SetProperty(ref _isShiftDragMode, value))
            {
                OnPropertyChanged(nameof(IsCoordinateOverlayVisible));
            }
        }
    }

    public bool IsCoordinateOverlayVisible => IsCoordinateGridVisible || IsShiftDragMode;
    public bool HasActiveCoordinateDrag => _coordinateDrag is not null;
    public string CoordinateGridButtonText => IsCoordinateGridVisible ? "Hide coordinate grid" : "Show coordinate grid";

    public bool IsTableViewVisible
    {
        get => _isTableViewVisible;
        private set
        {
            if (SetProperty(ref _isTableViewVisible, value))
            {
                OnPropertyChanged(nameof(IsGalaxyViewVisible));
                OnPropertyChanged(nameof(TableViewToggleText));
            }
        }
    }

    public bool IsGalaxyViewVisible => !IsTableViewVisible;
    public string TableViewToggleText => IsTableViewVisible ? "View Galaxy Map" : "View 2DA Tables";

    public bool IsDiagnosticsPanelOpen
    {
        get => _isDiagnosticsPanelOpen;
        private set => SetProperty(ref _isDiagnosticsPanelOpen, value);
    }

    public RelayCommand CreateModuleCommand { get; }
    public RelayCommand OpenModuleCommand { get; }
    public RelayCommand RefreshRememberedWorkspaceCommand { get; }
    public RelayCommand ConfigureBaseGameCommand { get; }
    public RelayCommand DismissErrorCommand { get; }
    public RelayCommand AddClusterCommand { get; }
    public RelayCommand AddSystemCommand { get; }
    public RelayCommand AddPlanetCommand { get; }
    public RelayCommand ContextualAddCommand { get; }
    public RelayCommand NavigateGalaxyCommand { get; }
    public RelayCommand NavigateClusterCommand { get; }
    public RelayCommand ToggleCoordinateGridCommand { get; }
    public RelayCommand ToggleTableViewCommand { get; }
    public RelayCommand ToggleDiagnosticsCommand { get; }
    public RelayCommand<ValidationDiagnostic> NavigateDiagnosticCommand { get; }
    public RelayCommand CommitCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand DiscardChangesCommand { get; }
    public RelayCommand CancelRelayCommand { get; }

    public bool LoadBuiltIn()
    {
        var result = _workspaceWorkflows.LoadBuiltIn();
        if (!result.Succeeded)
        {
            ErrorMessage = result.Error ?? result.Message;
            StatusMessage = "The built-in LE1 galaxy map could not be loaded.";
            return false;
        }

        Workspace = _session.Workspace;
        _tlkService.Reload(Workspace!.Modules);
        AttachWorkspace(Workspace!, result.Message);
        ErrorMessage = string.Empty;
        return true;
    }

    /// <summary>
    /// Loads BASEGAME and every remembered module as one session and presentation
    /// transition.
    /// </summary>
    public bool LoadRememberedWorkspace()
        => ApplyRememberedWorkspaceResult(
            _workspaceWorkflows.LoadRememberedWorkspace(),
            resetInspectionState: false,
            startupLoad: true);

    private bool ReloadRememberedWorkspace(bool requireCurrentlyMountedModules = true)
        => ApplyRememberedWorkspaceResult(
            _workspaceWorkflows.ReloadRememberedWorkspace(requireCurrentlyMountedModules),
            resetInspectionState: true,
            startupLoad: false);

    private bool ApplyRememberedWorkspaceResult(
        WorkflowResult result,
        bool resetInspectionState,
        bool startupLoad)
    {
        if (_session.Workspace is null || result.Impact is null)
        {
            ErrorMessage = result.Error ?? result.Message;
            StatusMessage = startupLoad
                ? "The built-in LE1 galaxy map could not be loaded."
                : result.Error ?? result.Message;
            return false;
        }

        if (resetInspectionState)
        {
            _navigation.RestoreInspectionState(null, false);
        }
        Workspace = _session.Workspace;
        var currentWorkspace = Workspace!;
        _tlkService.Reload(currentWorkspace.Modules);
        AttachWorkspace(currentWorkspace, result.Message);
        ErrorMessage = result.Error ?? string.Empty;
        return result.Succeeded;
    }

    /// <summary>
    /// Legacy full-export loading retained for fixture/reference inspection. It is
    /// always read-only and never becomes an authoring target.
    /// </summary>
    public bool LoadFolder(string folderPath)
    {
        var result = _workspaceWorkflows.LoadReferenceFolder(folderPath);
        if (result.Succeeded && _session.Document is { } document)
        {
            Workspace = null;
            RefreshMountedModules();
            AttachDocument(document, null, ViewContext.Galaxy);
            UpdateValidation();
            UpdateDocumentSummary(result.Message);
            ErrorMessage = string.Empty;
            RaiseCommandStates();
            return true;
        }

        ErrorMessage = result.Error ?? result.Message;
        StatusMessage = "The CSV folder could not be loaded. The current document was left unchanged.";
        return false;
    }

    public bool CreateModule(
        string parentFolder,
        string name,
        string tag,
        ModuleColor color,
        ModuleIdReservations reservations,
        int? loadOrder = null)
    {
        var result = _workspaceWorkflows.CreateModule(parentFolder, name, tag, color, reservations, loadOrder);
        return ApplyWorkspaceResult(result);
    }

    public bool OpenExistingModule(string folderPath)
    {
        var result = _workspaceWorkflows.OpenExistingModule(folderPath);
        return ApplyWorkspaceResult(result);
    }

    private void ConfigureBaseGame()
    {
        if (_dialogs.ConfigureBaseGameLocale(BaseGameTlkLocale) is not { } locale ||
            locale == BaseGameTlkLocale)
        {
            return;
        }

        BaseGameTlkLocale = locale;
        _saveBaseGameLocale?.Invoke(locale);
        if (_navigation.SelectedRow is { } selectedRow)
        {
            Inspector.Inspect(selectedRow);
        }
        TableViewer.Invalidate();
        if (IsTableViewVisible)
        {
            TableViewer.RefreshIfNeeded();
        }
        StatusMessage = $"BASEGAME TLK locale changed to {locale}.";
    }

    private MELocalization ResolveTlkLocale(GalaxyMapModule module)
        => module.IsBaseGame ? BaseGameTlkLocale : module.TlkLocale;

    public bool CreatePccModule(
        string destinationPackagePath,
        ModuleColor color,
        ModuleIdReservations reservations,
        MELocalization tlkLocale = MELocalization.INT,
        IReadOnlyList<string>? resourcePackagePaths = null,
        string? displayName = null)
    {
        var result = _workspaceWorkflows.CreatePccModule(
            destinationPackagePath,
            color,
            reservations,
            tlkLocale,
            resourcePackagePaths,
            displayName);
        return ApplyWorkspaceResult(result);
    }

    public bool MountReadOnlyModule(
        string folderPath,
        string name,
        string tag,
        ModuleColor color,
        ModuleIdReservations reservations,
        int? loadOrder = null)
    {
        var result = _workspaceWorkflows.MountReadOnlyModule(
            folderPath, name, tag, color, reservations, loadOrder);
        return ApplyWorkspaceResult(result);
    }

    public void ActivateHierarchyNode(HierarchyNodeViewModel node)
        => _navigation.ActivateHierarchyNode(node);

    public void SelectMapNode(HierarchyNodeViewModel node)
        => _navigation.SelectMapNode(node);

    private void CreateModuleDialog()
    {
        if (_usesPccModules)
        {
            var destination = _dialogs.PickNewModulePackage();
            if (destination is null)
            {
                return;
            }
            var cookedDirectory = Path.GetDirectoryName(destination) ?? string.Empty;
            var dlcDirectory = Directory.GetParent(cookedDirectory);
            var dlcName = dlcDirectory?.Name ?? "Galaxy Map Module";
            var autoLoadPath = dlcDirectory is null ? string.Empty : Path.Combine(dlcDirectory.FullName, "AutoLoad.ini");
            var autoLoad = File.Exists(autoLoadPath) ? new AutoloadIni(autoLoadPath) : null;
            var setupResult = _dialogs.ConfigureModule(new ModuleSetupDialogRequest(
                SelectParentFolder: false,
                FolderPath: destination,
                SuggestedName: string.IsNullOrWhiteSpace(autoLoad?.ModName) ? dlcName : autoLoad.ModName,
                SuggestedTag: dlcName,
                SuggestedReservations: ModuleIdReservations.Empty,
                SuggestedLoadOrder: autoLoad?.ModMount ?? 0,
                IdentityReadOnly: true));
            if (setupResult is not null)
            {
                CreatePccModule(
                    destination,
                    setupResult.Color,
                    setupResult.Reservations,
                    setupResult.TlkLocale,
                    setupResult.ResourcePackagePaths,
                    setupResult.Name);
            }
            return;
        }

        var parent = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var result = _dialogs.ConfigureModule(new ModuleSetupDialogRequest(
            SelectParentFolder: true,
            FolderPath: parent,
            SuggestedName: "New Galaxy Map Module",
            SuggestedTag: "NEW_GALAXY_MAP_MODULE",
            SuggestedReservations: ModuleIdReservations.Empty,
            SuggestedLoadOrder: _workspaceWorkflows.NextLoadOrder()));
        if (result is not null)
        {
            CreateModule(result.FolderPath, result.Name, result.Tag, result.Color, result.Reservations, result.LoadOrder);
        }
    }

    private void OpenModuleDialog()
    {
        var selectedPath = _dialogs.PickModuleFolder();
        if (selectedPath is null)
        {
            return;
        }

        if (_usesPccModules)
        {
            OpenExistingModule(selectedPath);
            return;
        }

        var folder = selectedPath;

        if (File.Exists(Path.Combine(folder, GalaxyMapModuleManifestStore.FileName)))
        {
            OpenExistingModule(folder);
            return;
        }

        try
        {
            var name = new DirectoryInfo(folder).Name;
            var result = _dialogs.ConfigureModule(new ModuleSetupDialogRequest(
                SelectParentFolder: false,
                FolderPath: folder,
                SuggestedName: name,
                SuggestedTag: GalaxyMapModule.SuggestTag(name),
                SuggestedReservations: _workspaceWorkflows.InferReservations(folder),
                SuggestedLoadOrder: _workspaceWorkflows.NextLoadOrder()));
            if (result is not null)
            {
                MountReadOnlyModule(folder, result.Name, result.Tag, result.Color, result.Reservations, result.LoadOrder);
            }
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            Fail(exception.Message);
        }
    }

    private void EditModule(GalaxyMapModule module)
    {
        if (Workspace is null || module.IsBaseGame || module.FolderPath is null)
        {
            return;
        }

        var result = _dialogs.ConfigureModule(new ModuleSetupDialogRequest(
            SelectParentFolder: false,
            FolderPath: module.GalaxyMapPackagePath ?? module.FolderPath,
            SuggestedName: module.Name,
            SuggestedTag: module.Tag,
            SuggestedReservations: module.Reservations,
            SuggestedLoadOrder: module.LoadOrder,
            IsEditing: true,
            SuggestedColor: module.Color,
            CanSetActive: !module.IsReadOnly,
            IsActive: ReferenceEquals(module, ActiveModule),
            SetActiveAction: () => SetActiveModule(module),
            UnlinkAction: () => UnlinkModule(module),
            IdentityReadOnly: module.IsPccBacked,
            SuggestedTlkLocale: module.TlkLocale,
            SuggestedResourcePackages: module.ResourcePackagePaths,
            ForgetAction: module.IsPccBacked ? () => ForgetModule(module) : null));
        if (result is null)
        {
            return;
        }

        try
        {
            UpdateModuleMetadata(
                module,
                result.Name,
                result.Tag,
                result.Color,
                result.LoadOrder,
                result.Reservations,
                result.TlkLocale,
                result.ResourcePackagePaths);
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            Fail(exception.Message);
        }
    }

    public bool SetActiveModule(GalaxyMapModule module)
    {
        var result = _workspaceWorkflows.SetActiveModule(module);
        if (result.Succeeded)
        {
            OnActiveModuleChanged();
            StatusMessage = result.Message;
            ErrorMessage = string.Empty;
            return true;
        }

        return Fail(result.Error ?? result.Message);
    }

    public bool UnlinkModule(GalaxyMapModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (Workspace is null || module.IsBaseGame ||
            Workspace.ModuleLayers.All(layer => !ReferenceEquals(layer.Module, module)))
        {
            return false;
        }

        var hasStagedChanges = _session.Changes.ContainsModule(module.Tag);
        var stagedWarning = hasStagedChanges
            ? "\n\nThis module has staged changes. Unlinking it will discard those changes."
            : string.Empty;
        if (!Confirm(
                $"Unlink {module.Name} [{module.Tag}] from this workspace? Its folder and files will not be deleted.{stagedWarning}"))
        {
            StatusMessage = "Module unlink cancelled.";
            return false;
        }

        var result = _workspaceWorkflows.UnlinkModule(module);
        if (!result.Succeeded)
        {
            OnActiveModuleChanged();
            return Fail(result.Error ?? result.Message);
        }

        if (string.Equals(_navigation.PreferredInstanceTag, module.Tag, StringComparison.OrdinalIgnoreCase))
        {
            _navigation.RestoreInspectionState(null, false);
        }

        _tlkService.Reload(Workspace?.Modules);
        RefreshWorkspace(null, ViewContext.Galaxy, result.Message);
        NotifyPendingChanges();
        ErrorMessage = string.Empty;
        return true;
    }

    public bool ForgetModule(GalaxyMapModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (!module.IsPccBacked)
        {
            return Fail("Only PCC-backed module profiles can be forgotten.");
        }
        if (!Confirm(
                $"Forget {module.Name} [{module.Tag}]? This removes it from the workspace and deletes its " +
                "LE1 Galaxy Map Editor profile. The DLC and every PCC remain untouched."))
        {
            StatusMessage = "Forget module cancelled.";
            return false;
        }

        var result = _workspaceWorkflows.ForgetModule(module);
        if (!result.Succeeded)
        {
            return Fail(result.Error ?? result.Message);
        }

        _tlkService.Reload(Workspace?.Modules);
        RefreshWorkspace(null, ViewContext.Galaxy, result.Message);
        NotifyPendingChanges();
        ErrorMessage = string.Empty;
        return true;
    }

    public bool UpdateModuleMetadata(
        GalaxyMapModule module,
        string name,
        string tag,
        ModuleColor color,
        int loadOrder,
        ModuleIdReservations reservations,
        MELocalization? tlkLocale = null,
        IReadOnlyList<string>? resourcePackagePaths = null)
    {
        var oldTag = module.Tag;
        var result = _workspaceWorkflows.UpdateModuleMetadata(
            module,
            name,
            tag,
            color,
            loadOrder,
            reservations,
            CaptureHistoryPresentation(),
            tlkLocale,
            resourcePackagePaths);
        if (!result.Succeeded)
        {
            return Fail(result.Error ?? result.Message);
        }

        if (string.Equals(_navigation.PreferredInstanceTag, oldTag, StringComparison.OrdinalIgnoreCase))
        {
            _navigation.PreferredInstanceTag = tag;
        }

        RefreshWorkspace(result.SelectionKey, CaptureView(), result.Message);
        return true;
    }

    private void OnActiveModuleChanged()
    {
        OnPropertyChanged(nameof(ActiveModule));
        OnPropertyChanged(nameof(HasActiveModule));
        OnPropertyChanged(nameof(ActiveModuleDisplay));
        OnPropertyChanged(nameof(ActiveModuleColor));
        RaiseCommandStates();
        RefreshModuleBarItems();
    }

    private void AddCluster()
    {
        if (Workspace is null)
        {
            return;
        }

        string suggestedLabel;
        try
        {
            suggestedLabel = _rowAuthoring.GetSuggestedClusterLabel();
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            Fail(exception.Message);
            return;
        }

        string? ValidateLabel(string candidate)
        {
            if (!InspectorEditWorkflow.TryLabelSuffix(candidate, "Cluster", out var suffix) ||
                suffix is < GalaxyMapIdentityLimits.MinAuthoredClusterLabel or > GalaxyMapIdentityLimits.MaxClusterLabel ||
                !string.Equals(candidate, $"Cluster{suffix:D2}", StringComparison.OrdinalIgnoreCase))
            {
                return "Use the exact form ClusterNN, from Cluster22 to Cluster99.";
            }

            var conflict = Workspace.Layers
                .SelectMany(layer => layer.Clusters.Select(cluster => (layer.Module, Cluster: cluster)))
                .FirstOrDefault(item =>
                    InspectorEditWorkflow.TryLabelSuffix(item.Cluster.Label, "Cluster", out var existing) &&
                    existing == suffix);
            return conflict.Cluster is null
                ? null
                : $"Cluster{suffix:D2} is already used by {conflict.Module.Name} [{conflict.Module.Tag}].";
        }

        var mountedLabels = Workspace.Layers
            .SelectMany(layer => layer.Clusters.Select(cluster => $"{cluster.Label} — {layer.Module.Tag}"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var request = new ClusterLabelRequest(suggestedLabel, mountedLabels, ValidateLabel);
        var label = _dialogs.ChooseClusterLabel(request);
        if (label is null)
        {
            StatusMessage = "Cluster creation cancelled.";
            return;
        }
        if (ValidateLabel(label) is { } error)
        {
            Fail(error);
            return;
        }

        ApplyMutationResult(_rowAuthoring.CreateCluster(label, CaptureHistoryPresentation()));
    }

    private bool CanAddForCurrentView() => HasActiveModule && CurrentViewModel switch
    {
        GalaxyViewModel => true,
        ClusterViewModel => CurrentCluster is not null,
        SystemViewModel => CurrentSystem is not null,
        _ => false
    };

    private void AddForCurrentView()
    {
        switch (CurrentViewModel)
        {
            case GalaxyViewModel:
                AddCluster();
                break;
            case ClusterViewModel when CurrentCluster is not null:
                AddSystem();
                break;
            case SystemViewModel when CurrentSystem is not null:
                AddPlanet();
                break;
        }
    }

    private void AddChildToHierarchyNode(HierarchyNodeViewModel node)
    {
        switch (node)
        {
            case { IsGalaxyRoot: true }:
                _navigation.ActivateHierarchyNode(node);
                NavigateGalaxy();
                AddCluster();
                break;
            case { Item: Cluster cluster }:
                _navigation.ActivateHierarchyNode(node);
                NavigateCluster(cluster);
                AddSystem();
                break;
            case { Item: GalaxySystem system }:
                _navigation.ActivateHierarchyNode(node);
                NavigateSystem(system);
                AddPlanet();
                break;
        }
    }

    private void AddSystem()
    {
        if (CurrentCluster is not { } cluster)
        {
            return;
        }

        ApplyMutationResult(_rowAuthoring.CreateSystem(cluster, CaptureHistoryPresentation()));
    }

    private void AddPlanet()
    {
        if (CurrentSystem is not { } system)
        {
            return;
        }

        var request = _dialogs.CreatePlanet();
        if (request is null)
        {
            return;
        }

        var destination = request.Destination is null
            ? null
            : new LandableDestinationChange(
                request.Destination.MapName,
                request.Destination.StartPoint,
                request.Destination.Event,
                request.Destination.ButtonLabel,
                request.Destination.AddPlotPlanet);
        var result = _rowAuthoring.CreatePlanet(
            system,
            new PlanetCreationChange(request.Template, request.NameText, request.Name, request.Scale, destination),
            CaptureHistoryPresentation());
        ApplyMutationResult(result);
        if (result.Succeeded && result.SelectionKey is { } key &&
            Workspace?.Resolve(key) is Planet planet && PlanetAppearanceCodec.IsAppearanceCapable(planet))
        {
            RequestPlanetDesigner(planet);
        }
    }

    private void CloneRow(GalaxyMapRow source)
    {
        CloneDefaults defaults;
        try
        {
            defaults = _rowAuthoring.GetCloneDefaults(source);
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            Fail(exception.Message);
            return;
        }

        if (_dialogs.ConfigureClone(source, defaults.RowId, defaults.Label) is { } request)
        {
            CloneRow(source, request);
        }
    }

    public bool CloneRow(GalaxyMapRow source, CloneContentRequest request)
    {
        var result = _rowAuthoring.Clone(
            source,
            new CloneRowChange(
                request.RowId,
                request.Label,
                request.Name,
                request.NameText,
                request.CloneChildren),
            CaptureHistoryPresentation());
        if (!result.Succeeded)
        {
            return Fail(result.Error ?? result.Message);
        }
        ApplyMutationResult(result);
        if (result.SelectionKey is { } key && Workspace?.Resolve(key) is Planet planet &&
            PlanetAppearanceCodec.IsAppearanceCapable(planet))
        {
            RequestPlanetDesigner(planet);
        }
        return true;
    }

    public PlanetDesignerViewModel CreatePlanetDesigner(GalaxyMapRowKey key, string? moduleTag = null)
    {
        if (ResolvePlanetForDesigner(key, moduleTag) is not { } planet)
        {
            throw new InvalidOperationException("The selected Planet row is no longer present.");
        }

        var designer = _planetDesigner.Open(planet);
        return new PlanetDesignerViewModel(
            () => Workspace,
            designer,
            ApplyPlanetDesigner,
            UndoPlanetDesigner,
            RedoPlanetDesigner,
            () => _edits.CanUndo,
            () => _edits.CanRedo,
            ResolvePlanetForDesigner,
            request => LinkPlanetTexture(designer, request),
            ResolvePlanetPreviewTexture,
            unlinkTexture: (moduleTag, linkId) => UnlinkPlanetTexture(designer, moduleTag, linkId),
            packageTextureOptions: () => _textures
                .GetPlanetPackageTextureOptions(EffectiveTextureModules(), ReferencedPlanetTexturePaths())
                .Select(texture => texture.InMemoryPath)
                .ToArray(),
            resolvePreviewTextures: ResolvePlanetPreviewTextures);
    }

    public void WarmPlanetPreviewTextures()
    {
        lock (_planetTextureWarmupLock)
        {
            _planetTextureWarmupTask ??= Task.Run(() => _textures.GetPackageTextureData(
                [],
                VanillaPlanetTextureReferences()));
        }
    }

    private Planet? ResolvePlanetForDesigner(GalaxyMapRowKey key, string? moduleTag)
    {
        if (Workspace is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(moduleTag))
        {
            return Workspace.Layers.FirstOrDefault(candidate =>
                    string.Equals(candidate.Module.Tag, moduleTag, StringComparison.OrdinalIgnoreCase))
                ?.Find(key) as Planet;
        }

        return Workspace.Resolve(key) as Planet;
    }

    private void RequestPlanetDesigner(Planet planet)
    {
        if (!PlanetAppearanceCodec.IsAppearanceCapable(planet))
        {
            Fail("The Planet Designer is available only for planet material rows.");
            return;
        }

        PlanetDesignerRequested?.Invoke(this, new PlanetDesignerRequestedEventArgs(
            planet.Key,
            planet.Origin?.ModuleTag ?? GalaxyMapModule.BaseGameTag));
    }

    private WorkflowResult ApplyPlanetDesigner(PlanetDesignerSession designer)
    {
        GalaxyMapModule? targetModule = null;
        var sourcePlanet = ResolvePlanetForDesigner(designer.Key, designer.ModuleTag);
        if (sourcePlanet is null)
        {
            var missing = WorkflowResult.Failure(
                "The Planet row is no longer present in the workspace.",
                designer.Key);
            ApplyMutationResult(missing);
            return missing;
        }

        if (sourcePlanet.Origin?.Module is not { IsReadOnly: false, IsBaseGame: false })
        {
            targetModule = ChooseEditTarget(sourcePlanet);
            if (targetModule is null)
            {
                var cancelled = new WorkflowResult(
                    false,
                    "Planet appearance apply cancelled; the draft was left unchanged.",
                    designer.Key);
                ApplyMutationResult(cancelled);
                return cancelled;
            }

            string? ValidateShaderName(string shaderName)
            {
                if (Workspace is null)
                {
                    return "The galaxy-map workspace is no longer available.";
                }

                var candidate = designer.Draft.Clone();
                candidate.Shader = shaderName.Trim();
                var validation = PlanetShaderNameValidator.Validate(
                    Workspace,
                    designer.Key,
                    candidate,
                    targetModule.Tag);
                return validation.IsValid ? null : validation.Message;
            }

            var suggestedShader = designer.Draft.Shader.Trim();
            if (string.Equals(suggestedShader, designer.Original.Shader.Trim(), StringComparison.OrdinalIgnoreCase) ||
                ValidateShaderName(suggestedShader) is not null)
            {
                var root = $"{targetModule.Tag}_Planet{designer.Key.RowId}";
                suggestedShader = root;
                for (var suffix = 2; ValidateShaderName(suggestedShader) is not null; suffix++)
                {
                    suggestedShader = $"{root}_{suffix}";
                }
            }

            var shaderRequest = new PlanetShaderNameRequest(
                designer.DisplayName,
                designer.Key.RowId,
                targetModule,
                suggestedShader,
                ValidateShaderName);
            var shaderName = _dialogs.ChoosePlanetShaderName(shaderRequest);
            if (shaderName is null)
            {
                var cancelled = new WorkflowResult(
                    false,
                    "Planet appearance apply cancelled; the draft was left unchanged.",
                    designer.Key);
                ApplyMutationResult(cancelled);
                return cancelled;
            }

            if (ValidateShaderName(shaderName) is { } shaderError)
            {
                var invalid = WorkflowResult.Failure(shaderError, designer.Key);
                ApplyMutationResult(invalid);
                return invalid;
            }

            designer.Draft.Shader = shaderName.Trim();
        }

        var result = _planetDesigner.Apply(
            designer,
            CaptureHistoryPresentation(designer.Key),
            targetModule);
        ApplyMutationResult(result);
        return result;
    }

    private WorkflowResult LinkPlanetTexture(
        PlanetDesignerSession designer,
        PlanetTextureLinkRequest request)
    {
        var planet = ResolvePlanetForDesigner(designer.Key, designer.ModuleTag) ??
                     ResolvePlanetForDesigner(designer.Key, null);
        if (planet is null)
        {
            return WorkflowResult.Failure("The Planet row is no longer present in the workspace.", designer.Key);
        }

        var target = ResolveWritableTarget(planet, preferActiveModule: true);
        if (target is null)
        {
            return WorkflowResult.Failure(
                HasError ? ErrorMessage : "Planet texture link cancelled; no writable module was selected.",
                designer.Key);
        }
        if (target.IsPccBacked)
        {
            return WorkflowResult.Failure(
                "Loose Planet texture links are not used by PCC modules. Register a resource PCC instead.",
                designer.Key);
        }

        var result = _planetTextures.Stage(
            planet,
            target,
            request,
            CaptureHistoryPresentation(designer.Key));
        if (result.Succeeded)
        {
            _navigation.PreferredInstanceTag = target.Tag;
            RefreshWorkspace(designer.Key, CaptureView(), result.Message, refreshModules: true);
        }
        return result;
    }

    private WorkflowResult UnlinkPlanetTexture(
        PlanetDesignerSession designer,
        string moduleTag,
        string linkId)
    {
        if (Workspace is null)
        {
            return WorkflowResult.Failure("The galaxy-map workspace is no longer available.", designer.Key);
        }

        var target = Workspace.ModuleLayers
            .Select(layer => layer.Module)
            .FirstOrDefault(module => string.Equals(module.Tag, moduleTag, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return WorkflowResult.Failure("The module containing that Planet texture is no longer mounted.", designer.Key);
        }

        var planet = ResolvePlanetForDesigner(designer.Key, designer.ModuleTag) ??
                     ResolvePlanetForDesigner(designer.Key, null);
        if (planet is null)
        {
            return WorkflowResult.Failure("The Planet row is no longer present in the workspace.", designer.Key);
        }

        var result = _planetTextures.Unlink(
            planet,
            target,
            linkId,
            CaptureHistoryPresentation(designer.Key));
        if (result.Succeeded)
        {
            RefreshWorkspace(designer.Key, CaptureView(), result.Message, refreshModules: true);
        }
        return result;
    }

    private LE1GalaxyMapEditor.Rendering.PlanetPreviewTextureSource? ResolvePlanetPreviewTexture(string inMemoryPath)
    {
        var source = _planetTextures.ResolvePreview(inMemoryPath);
        if (source is not null)
        {
            return new LE1GalaxyMapEditor.Rendering.PlanetPreviewTextureSource(source.CacheKey, source.Contents);
        }
        if (Workspace is null)
        {
            return null;
        }

        var packageData = _textures.GetPackageTextureData(EffectiveTextureModules(), inMemoryPath);
        return packageData is null
            ? null
            : new LE1GalaxyMapEditor.Rendering.PlanetPreviewTextureSource(
                packageData.CacheKey,
                packageData.Contents);
    }

    private IReadOnlyDictionary<string, LE1GalaxyMapEditor.Rendering.PlanetPreviewTextureSource>
        ResolvePlanetPreviewTextures(IEnumerable<string> inMemoryPaths)
    {
        Task? warmup;
        lock (_planetTextureWarmupLock)
        {
            warmup = _planetTextureWarmupTask;
        }
        warmup?.GetAwaiter().GetResult();

        var references = inMemoryPaths
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => reference.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = new Dictionary<string, LE1GalaxyMapEditor.Rendering.PlanetPreviewTextureSource>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            if (_planetTextures.ResolvePreview(reference) is { } linked)
            {
                result[reference] = new LE1GalaxyMapEditor.Rendering.PlanetPreviewTextureSource(
                    linked.CacheKey,
                    linked.Contents);
            }
        }

        if (Workspace is null)
        {
            return result;
        }
        var packageData = _textures.GetPackageTextureData(
            EffectiveTextureModules(),
            references.Where(reference => !result.ContainsKey(reference)));
        foreach (var (reference, data) in packageData)
        {
            result[reference] = new LE1GalaxyMapEditor.Rendering.PlanetPreviewTextureSource(
                data.CacheKey,
                data.Contents);
        }
        var unresolvedVanilla = references.Where(reference =>
                reference.StartsWith("BIOA_GXM10_T.GXM_", StringComparison.OrdinalIgnoreCase) &&
                !result.ContainsKey(reference))
            .ToArray();
        if (unresolvedVanilla.Length > 0)
        {
            var diagnostics = _textures.PackageTextureDiagnostics.Count == 0
                ? "No package diagnostic was reported."
                : string.Join(" ", _textures.PackageTextureDiagnostics);
            throw new InvalidOperationException(
                $"Vanilla planet textures were not resolved: {string.Join(", ", unresolvedVanilla)}. {diagnostics}");
        }
        return result;
    }

    private static IReadOnlyList<string> VanillaPlanetTextureReferences() =>
        PlanetAppearanceSchema.Properties
            .SelectMany(property => property.TextureOptions ?? [])
            .Append("BIOA_GXM10_T.GXM_CoronaGradient")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private IReadOnlyList<string> ReferencedPlanetTexturePaths()
    {
        if (Workspace is null)
        {
            return [];
        }

        var textureColumns = PlanetAppearanceSchema.Properties
            .Where(property => property.Editor == PlanetAppearanceEditorKind.Texture)
            .SelectMany(property => property.Columns)
            .ToArray();
        return Workspace.Layers
            .SelectMany(layer => layer.Planets)
            .SelectMany(planet => textureColumns.Select(column => planet.ExtraFields.GetValueOrDefault(column)))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool UndoPlanetDesigner(GalaxyMapRowKey key)
    {
        var result = _edits.Undo(CaptureHistoryPresentation(key));
        ApplyHistoryRestore(result);
        return result.Succeeded;
    }

    private bool RedoPlanetDesigner(GalaxyMapRowKey key)
    {
        var result = _edits.Redo(CaptureHistoryPresentation(key));
        ApplyHistoryRestore(result);
        return result.Succeeded;
    }

    private void ToggleCoordinateGrid() => IsCoordinateGridVisible = !IsCoordinateGridVisible;

    public void SetShiftDragMode(bool enabled)
        => IsShiftDragMode = enabled;

    public bool BeginCoordinateDrag(GalaxyMapRow row)
    {
        if (Workspace is null || row is not (Cluster or GalaxySystem or Planet))
        {
            return false;
        }

        if (_coordinateDrag is not null)
        {
            CompleteCoordinateDrag(cancel: true);
        }

        var layer = MovableOwningLayer(row);
        if (layer is null)
        {
            var target = ResolveWritableTarget(row);
            if (target is null)
            {
                return false;
            }
            layer = Workspace.ModuleLayers.First(candidate =>
                string.Equals(candidate.Module.Tag, target.Tag, StringComparison.OrdinalIgnoreCase));
        }

        var (x, y) = Coordinates(row);
        _coordinateDrag = new CoordinateDragSession(row, layer, x, y, CaptureView());
        OnPropertyChanged(nameof(HasActiveCoordinateDrag));
        ErrorMessage = string.Empty;
        StatusMessage = $"Dragging {RowDisplayName(row)}. Release Shift or the mouse button to stage its coordinates.";
        return true;
    }

    public Point PreviewCoordinateDrag(GalaxyMapRow row, Point normalizedPosition)
    {
        var rounded = CoordinateGridLayer.RoundNormalizedPosition(normalizedPosition);
        if (_coordinateDrag is not { } session || session.Row.Key != row.Key)
        {
            return rounded;
        }

        try
        {
            _isApplying = true;
            SetCoordinates(session.Row, rounded.X, rounded.Y);
            if (CurrentViewModel is MapViewModelBase map)
            {
                map.Refresh();
            }
        }
        finally
        {
            _isApplying = false;
        }

        return rounded;
    }

    public bool CompleteCoordinateDrag(bool cancel = false)
    {
        if (_coordinateDrag is not { } session)
        {
            return false;
        }

        var (finalX, finalY) = Coordinates(session.Row);
        try
        {
            _isApplying = true;
            SetCoordinates(session.Row, session.OriginalX, session.OriginalY);
            if (CurrentViewModel is MapViewModelBase map)
            {
                map.Refresh();
            }
        }
        finally
        {
            _isApplying = false;
            _coordinateDrag = null;
            OnPropertyChanged(nameof(HasActiveCoordinateDrag));
        }

        if (cancel)
        {
            StatusMessage = $"Cancelled coordinate move for {RowDisplayName(session.Row)}.";
            return true;
        }

        if (finalX.Equals(session.OriginalX) && finalY.Equals(session.OriginalY))
        {
            StatusMessage = $"{RowDisplayName(session.Row)} stayed at {finalX:0.00}, {finalY:0.00}.";
            return true;
        }

        var result = _rowAuthoring.StageCoordinates(
            session.Row,
            session.Layer,
            finalX,
            finalY,
            CaptureHistoryPresentation(session.Row.Key, session.View));
        if (!result.Succeeded)
        {
            return Fail(result.Error ?? result.Message);
        }

        RefreshWorkspace(
            result.SelectionKey,
            session.View,
            result.Message,
            preserveHierarchy: true,
            refreshModules: false,
            deferValidation: true);
        OnActiveModuleChanged();
        ErrorMessage = string.Empty;
        return true;
    }

    private static (double X, double Y) Coordinates(GalaxyMapRow row) => row switch
    {
        Cluster cluster => (cluster.X, cluster.Y),
        GalaxySystem system => (system.X, system.Y),
        Planet planet => (planet.X, planet.Y),
        _ => throw new ArgumentException($"{row.Table} rows do not have map coordinates.", nameof(row))
    };

    private static void SetCoordinates(GalaxyMapRow row, double x, double y)
    {
        switch (row)
        {
            case Cluster cluster:
                cluster.X = x;
                cluster.Y = y;
                break;
            case GalaxySystem system:
                system.X = x;
                system.Y = y;
                break;
            case Planet planet:
                planet.X = x;
                planet.Y = y;
                break;
            default:
                throw new ArgumentException($"{row.Table} rows do not have map coordinates.", nameof(row));
        }
    }

    private static string RowDisplayName(GalaxyMapRow row) => row switch
    {
        Cluster cluster => cluster.DisplayName,
        GalaxySystem system => system.DisplayName,
        Planet planet => planet.DisplayName,
        _ => $"{row.Table} row {row.RowId}"
    };

    private void ToggleDiagnostics()
    {
        IsDiagnosticsPanelOpen = HasDiagnostics && !IsDiagnosticsPanelOpen;
    }

    private void ToggleTableView()
    {
        if (!HasDocument)
        {
            return;
        }

        IsTableViewVisible = !IsTableViewVisible;
        if (IsTableViewVisible)
        {
            TableViewer.RefreshIfNeeded();
        }
    }

    public bool CommitPendingChanges()
    {
        var selectionKey = _navigation.SelectedKey;
        var view = CaptureView();
        var result = _edits.Commit(_workspaceWorkflows.CommitCurrentWorkspace);
        if (!result.Succeeded)
        {
            if (result.Impact is not null && Workspace is not null)
            {
                RefreshWorkspace(selectionKey, view, null);
            }
            NotifyPendingChanges();
            return Fail(result.Error ?? result.Message);
        }

        if (Workspace is null || string.IsNullOrEmpty(result.Message))
        {
            return true;
        }

        RefreshWorkspace(_navigation.SelectedKey, CaptureView(), result.Message);
        ErrorMessage = string.Empty;
        NotifyPendingChanges();
        return true;
    }

    public CommitPreview CreateCommitPreview() => _commitPreviewBuilder.Build(_session);

    private void ReviewAndCommitPendingChanges()
    {
        var preview = CreateCommitPreview();
        if (preview.ChangeCount > 0 && _dialogs.ReviewCommit(preview))
        {
            CommitPendingChanges();
        }
    }

    public void DiscardPendingChanges()
    {
        if (!HasPendingChanges)
        {
            return;
        }

        var revision = _session.Revision;
        ReloadRememberedWorkspace();
        if (_session.Revision == revision)
        {
            return;
        }
        StatusMessage = "Discarded all uncommitted changes.";
    }

    /// <summary>
    /// Abandons process-local edit state when the application is committed to
    /// shutting down. This deliberately does not publish, recompose, validate, or
    /// rebuild presentation state: the remaining in-memory workspace is not safe to
    /// continue editing and is about to be released with the window.
    /// </summary>
    public void AbandonPendingChangesForShutdown()
    {
        _validation.Cancel();
        ClearTransientEditState();
    }

    public bool RefreshRememberedWorkspace()
    {
        var protectLiveEdits = HasPendingChanges;
        if (protectLiveEdits && !Confirm(
                "Refreshing reloads BASEGAME and every module remembered in workspace.json. Discard all uncommitted changes?"))
        {
            StatusMessage = "Refresh cancelled; staged changes were left intact.";
            return false;
        }

        // With no staged data at risk, Refresh follows workspace.json as the
        // authority and may intentionally unmount an externally removed entry.
        var restoredCleanly = ReloadRememberedWorkspace(
            requireCurrentlyMountedModules: protectLiveEdits);
        if (restoredCleanly)
        {
            StatusMessage = Workspace?.ModuleLayers.Count > 0
                ? $"Refreshed BASEGAME and {Workspace.ModuleLayers.Count} remembered module(s); validation is up to date."
                : "Refreshed BASEGAME; no remembered modules were configured.";
        }

        return restoredCleanly;
    }

    private void ClearTransientEditState()
    {
        _session.Changes.Clear();
        _edits.ClearHistory();
    }

    private GalaxyMapModule? ChooseEditTarget(GalaxyMapRow row)
    {
        if (Workspace is null)
        {
            return null;
        }

        var candidates = Workspace.Modules.Where(module => !module.IsReadOnly && !module.IsBaseGame)
            .OrderByDescending(module => module.LoadOrder)
            .ThenBy(module => module.Tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
        {
            Fail("Create or open a writable module before editing source rows.");
            return null;
        }

        return _dialogs.ChooseEditTarget(row, candidates, ActiveModule);
    }

    private GalaxyMapModule? ResolveWritableTarget(GalaxyMapRow row, bool preferActiveModule = false)
    {
        if (Workspace is null)
        {
            return null;
        }

        if (row.Origin?.Module is { IsReadOnly: false, IsBaseGame: false } origin)
        {
            return Workspace.Modules.FirstOrDefault(module =>
                string.Equals(module.Tag, origin.Tag, StringComparison.OrdinalIgnoreCase));
        }

        if (preferActiveModule && ActiveModule is { IsReadOnly: false, IsBaseGame: false } active)
        {
            return active;
        }

        return ChooseEditTarget(row);
    }

    private void SessionOnChanged(object? sender, SessionChangedEventArgs eventArgs)
    {
        TableViewer.Invalidate(eventArgs.Impact);
        if (!ReferenceEquals(_workspace, _session.Workspace))
        {
            Workspace = _session.Workspace;
        }

        NotifyPendingChanges();
        NotifyHistoryChanged();
    }

    private void NavigationOnChanged(object? sender, EventArgs eventArgs)
    {
        OnPropertyChanged(nameof(CurrentViewModel));
        OnPropertyChanged(nameof(CurrentCluster));
        OnPropertyChanged(nameof(CurrentSystem));
        OnPropertyChanged(nameof(HasCurrentCluster));
        OnPropertyChanged(nameof(HasCurrentSystem));
        OnPropertyChanged(nameof(HasMultipleRowInstances));
        OnPropertyChanged(nameof(HasContextualAddAction));
        OnPropertyChanged(nameof(ContextualAddButtonText));
        OnPropertyChanged(nameof(ContextualAddToolTip));
        NavigateClusterCommand.RaiseCanExecuteChanged();
        AddSystemCommand.RaiseCanExecuteChanged();
        AddPlanetCommand.RaiseCanExecuteChanged();
        ContextualAddCommand.RaiseCanExecuteChanged();
        ApplyHierarchySearch();
    }

    private void ApplyHierarchySearch()
    {
        var terms = HierarchySearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var root in HierarchyRoots)
        {
            root.ApplySearch(terms);
        }
    }

    private void RelayStateOnChanged(object? sender, EventArgs eventArgs)
    {
        OnPropertyChanged(nameof(PendingRelaySource));
        OnPropertyChanged(nameof(IsAddingRelay));
        OnPropertyChanged(nameof(RelayLinkPrompt));
    }

    private void ApplyMutationResult(WorkflowResult result)
    {
        if (!result.Succeeded)
        {
            if (result.Error is not null)
            {
                Fail(result.Error);
            }
            else
            {
                StatusMessage = result.Message;
            }
            return;
        }

        var navigation = result.Navigation ?? NavigationTarget.Galaxy;
        RefreshWorkspace(
            result.SelectionKey,
            new ViewContext(navigation.ClusterRowId, navigation.SystemRowId),
            result.Message);
        OnActiveModuleChanged();
        ErrorMessage = string.Empty;
    }

    private void ExecuteInspectorAction(GalaxyMapRow row, InspectorActionDescriptor action)
    {
        switch (action.Id)
        {
            case InspectorActionId.LinkClusterTexture when row is Cluster cluster:
                LinkClusterTexture(cluster);
                break;
            case InspectorActionId.ConfigureLandableDestination when row is Planet planet:
                ConfigureLandableDestination(planet);
                break;
            case InspectorActionId.AddPlotPlanet when row is Planet planet:
                AddPlotPlanet(planet);
                break;
            case InspectorActionId.AddLinkedMap when row is Planet planet:
                AddLinkedMap(planet);
                break;
            case InspectorActionId.DeleteLinkedPlotPlanet when row is Planet planet:
                DeleteLinkedPlotPlanet(planet);
                break;
            case InspectorActionId.DeleteLinkedMap when row is Planet planet:
                DeleteLinkedMap(planet);
                break;
            case InspectorActionId.BeginRelayCreation when row is Cluster cluster:
                BeginRelayCreation(cluster);
                break;
            case InspectorActionId.CancelRelayEdit:
                CancelRelayCreation();
                break;
            case InspectorActionId.RedirectRelay when row is Cluster cluster &&
                                                       action.Payload is RelayConnection relay:
                BeginRelayRedirect(cluster, relay);
                break;
            case InspectorActionId.RemoveRelay when action.Payload is RelayConnection relay:
                RemoveRelay(relay);
                break;
        }
    }

    private void NotifyPendingChanges()
    {
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(PendingChangeCount));
        OnPropertyChanged(nameof(CommitButtonText));
        CommitCommand.RaiseCanExecuteChanged();
        DiscardChangesCommand.RaiseCanExecuteChanged();
        RefreshModuleBarItems();
    }

    private void NavigateToDiagnostic(ValidationDiagnostic? diagnostic)
    {
        if (diagnostic?.RowId is not { } rowId ||
            !Enum.TryParse<GalaxyMapTable>(diagnostic.TableName, ignoreCase: true, out var table) ||
            Document is null)
        {
            return;
        }

        GalaxyMapRow? row = Workspace?.Resolve(new GalaxyMapRowKey(table, rowId)) ?? table switch
        {
            GalaxyMapTable.Cluster => Document.ClustersByRowId.GetValueOrDefault(rowId),
            GalaxyMapTable.System => Document.SystemsByRowId.GetValueOrDefault(rowId),
            GalaxyMapTable.Planet => Document.PlanetsByRowId.GetValueOrDefault(rowId),
            GalaxyMapTable.PlotPlanet => Document.PlotPlanetsByRowId.GetValueOrDefault(rowId),
            GalaxyMapTable.Map => Document.MapsByRowId.GetValueOrDefault(rowId),
            GalaxyMapTable.Relay => Document.Relays.FirstOrDefault(candidate => candidate.RowId == rowId),
            _ => null
        };
        if (row is null)
        {
            return;
        }

        if (_navigation.TrySelect(row.Key))
        {
        }
        else if (row is PlotPlanetEntry && _navigation.TrySelect(
                     new GalaxyMapRowKey(GalaxyMapTable.Planet, row.RowId)))
        {
        }
        else if (row is MapEntry && Document.Planets.FirstOrDefault(planet => planet.MapRowId == row.RowId) is { } linkedPlanet &&
                 _navigation.TrySelect(linkedPlanet.Key))
        {
        }
        else
        {
            NavigateGalaxy();
            Inspector.Inspect(row);
        }

        StatusMessage = $"Selected {diagnostic.Location}: {diagnostic.Message}";
    }

    private void AttachWorkspace(GalaxyMapWorkspace workspace, string status)
    {
        Workspace = workspace;
        RefreshWorkspace(null, ViewContext.Galaxy, status);
    }

    private bool ApplyWorkspaceResult(WorkflowResult result)
    {
        if (!result.Succeeded || _session.Workspace is null)
        {
            return Fail(result.Error ?? result.Message);
        }

        Workspace = _session.Workspace;
        var navigation = result.Navigation ?? NavigationTarget.Galaxy;
        _tlkService.Reload(Workspace.Modules);
        RefreshWorkspace(
            result.SelectionKey,
            new ViewContext(navigation.ClusterRowId, navigation.SystemRowId),
            result.Message);
        ErrorMessage = string.Empty;
        return true;
    }

    private void RefreshWorkspace(
        GalaxyMapRowKey? selectionKey,
        ViewContext view,
        string? status,
        bool preserveHierarchy = false,
        bool refreshModules = true,
        bool deferValidation = false)
    {
        if (Workspace is null)
        {
            return;
        }

        if (refreshModules)
        {
            RefreshMountedModules();
        }
        AttachDocument(Workspace.EffectiveDocument, selectionKey, view, preserveHierarchy);
        if (deferValidation)
        {
            ScheduleValidation(status);
        }
        else
        {
            UpdateValidation();
        }
        UpdateDocumentSummary(status);
        if (refreshModules)
        {
            OnActiveModuleChanged();
        }
        OnPropertyChanged(nameof(SourceFolder));
        RaiseCommandStates();
    }

    private void AttachDocument(
        GalaxyMapDocument document,
        GalaxyMapRowKey? selectionKey,
        ViewContext view,
        bool preserveHierarchy = false)
    {
        _relay.Cancel();
        Document = document;
        _navigation.AttachDocument(
            document,
            selectionKey,
            new NavigationTarget(view.ClusterRowId, view.SystemRowId),
            preserveHierarchy);

        if (IsTableViewVisible && !_isApplyingTableCellEdit)
        {
            TableViewer.RefreshIfNeeded();
        }

        NavigateGalaxyCommand.RaiseCanExecuteChanged();
    }

    private bool HandlePendingRelaySelection(HierarchyNodeViewModel node)
    {
        if (!_relay.State.IsActive)
        {
            return false;
        }

        if (node.Item is Cluster target)
        {
            ApplyMutationResult(_relay.AcceptTarget(target, CaptureHistoryPresentation()));
            return true;
        }

        _relay.Cancel();
        return false;
    }

    private void NavigateGalaxy() => _navigation.NavigateGalaxy();

    private void NavigateCluster(Cluster cluster) => _navigation.NavigateCluster(cluster);

    private void NavigateSystem(GalaxySystem system) => _navigation.NavigateSystem(system);

    private void LinkClusterTexture(Cluster cluster)
    {
        if (Workspace is null)
        {
            return;
        }

        var target = cluster.Origin?.Module is { IsReadOnly: false, IsBaseGame: false } writable
            ? Workspace.Modules.FirstOrDefault(module =>
                string.Equals(module.Tag, writable.Tag, StringComparison.OrdinalIgnoreCase))
            : ChooseEditTarget(cluster);
        if (target is null)
        {
            return;
        }

        if (target.IsPccBacked)
        {
            Fail("Loose Cluster texture links are not used by PCC modules. Reference a Texture2D export instead.");
            return;
        }

        if (_dialogs.PickClusterTexture() is { } sourcePath)
        {
            StageClusterTexture(cluster, target, sourcePath);
        }
    }

    public bool StageClusterTexture(Cluster cluster, GalaxyMapModule target, string sourcePath)
    {
        if (target.IsPccBacked)
        {
            return Fail("Loose Cluster texture links are not used by PCC modules.");
        }

        var result = _clusterTextures.Stage(
            cluster,
            target,
            sourcePath,
            CaptureHistoryPresentation(cluster.Key));
        if (!result.Succeeded)
        {
            return Fail(result.Error ?? result.Message);
        }

        _navigation.PreferredInstanceTag = ActiveModule?.Tag;
        RefreshWorkspace(result.SelectionKey, CaptureView(), result.Message);
        return true;
    }

    public bool StagePlanetTexture(
        Planet planet,
        GalaxyMapModule target,
        PlanetTextureLinkRequest request)
    {
        var result = _planetTextures.Stage(
            planet,
            target,
            request,
            CaptureHistoryPresentation(planet.Key));
        if (!result.Succeeded)
        {
            return Fail(result.Error ?? result.Message);
        }

        _navigation.PreferredInstanceTag = target.Tag;
        RefreshWorkspace(planet.Key, CaptureView(), result.Message, refreshModules: true);
        return true;
    }

    private System.Windows.Media.ImageSource? GetClusterTexture(Cluster cluster)
    {
        var source = _clusterTextures.ResolveSource(cluster, Document);
        if (source.PendingContents is not null && source.CacheKey is not null)
        {
            return _textures.LoadTextureBytes(source.CacheKey, source.PendingContents);
        }

        if (source.Module is not null && source.LinkedClusterRowId is { } linkedClusterRowId &&
            _textures.GetModuleClusterTexture(source.Module, linkedClusterRowId) is { } moduleTexture)
        {
            return moduleTexture;
        }

        var packageModule = source.Module ?? cluster.Origin?.Module ?? GalaxyMapModule.BaseGame;
        if (_textures.GetPackageTexture(EffectiveTextureModules(packageModule), source.Background) is { } packageTexture)
        {
            return packageTexture;
        }

        return _textures.GetClusterTexture(source.Background);
    }

    private IReadOnlyList<GalaxyMapModule> EffectiveTextureModules(GalaxyMapModule? preferred = null)
    {
        var modules = Workspace?.Modules.Where(module => module.IsPccBacked)
            .OrderByDescending(module => module.LoadOrder)
            .ThenByDescending(module => module.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        if (preferred is { IsPccBacked: true })
        {
            modules.RemoveAll(module => ReferenceEquals(module, preferred));
            modules.Insert(0, preferred);
        }
        return modules;
    }

    private void AddPlotPlanet(Planet planet)
    {
        var target = ResolveWritableTarget(planet);
        if (target is null)
        {
            return;
        }

        ApplyMutationResult(_planetRelationships.AddPlotPlanet(
            planet,
            target,
            CaptureHistoryPresentation(
                planet.Key,
                new ViewContext(planet.System?.ClusterRowId, planet.SystemRowId))));
    }

    private void ConfigureLandableDestination(Planet planet)
    {
        if (Workspace is null)
        {
            return;
        }
        if (planet.OrbitRing == 2)
        {
            Fail("Asteroid belts cannot be configured as landable destinations.");
            return;
        }

        var request = _dialogs.ConfigureLandableDestination(new LandableDestinationDefaults(
            planet.LinkedMap?.MapName ?? string.Empty,
            planet.LinkedMap?.StartPoint ?? string.Empty,
            planet.Event,
            planet.ButtonLabel,
            planet.PlotPlanet is null));
        if (request is null)
        {
            return;
        }

        var target = ResolveWritableTarget(planet);
        if (target is null)
        {
            return;
        }
        ApplyMutationResult(_planetRelationships.ConfigureLandableDestination(
            planet,
            target,
            new LandableDestinationChange(
                request.MapName,
                request.StartPoint,
                request.Event,
                request.ButtonLabel,
                request.AddPlotPlanet),
            CaptureHistoryPresentation(
                planet.Key,
                new ViewContext(planet.System?.ClusterRowId, planet.SystemRowId))));
    }

    private void AddLinkedMap(Planet planet)
    {
        var target = ResolveWritableTarget(planet);
        if (target is null)
        {
            return;
        }

        ApplyMutationResult(_planetRelationships.AddLinkedMap(
            planet,
            target,
            CaptureHistoryPresentation(
                planet.Key,
                new ViewContext(planet.System?.ClusterRowId, planet.SystemRowId))));
    }

    private void DeleteLinkedPlotPlanet(Planet planet)
    {
        if (Workspace is null || planet.PlotPlanet is not { } linked || !Confirm($"Delete PlotPlanet row {linked.RowId}?")) return;
        ApplyMutationResult(_planetRelationships.DeleteLinkedPlotPlanet(
            planet,
            CaptureHistoryPresentation(
                planet.Key,
                new ViewContext(planet.System?.ClusterRowId, planet.SystemRowId))));
    }

    private void DeleteLinkedMap(Planet planet)
    {
        if (Workspace is null || planet.LinkedMap is not { } linked || !Confirm($"Delete Map row {linked.RowId} and clear its Planet link?")) return;
        var target = ResolveWritableTarget(planet);
        if (target is null) return;
        ApplyMutationResult(_planetRelationships.DeleteLinkedMap(
            planet,
            target,
            CaptureHistoryPresentation(
                planet.Key,
                new ViewContext(planet.System?.ClusterRowId, planet.SystemRowId))));
    }

    private GalaxyMapLayer? MovableOwningLayer(GalaxyMapRow row)
        => _rowAuthoring.MovableOwningLayer(row);

    public bool CanMoveOwnedRow(GalaxyMapRow row)
        => row is GalaxySystem or Planet;

    private void MoveRowDialog(GalaxyMapRow source)
    {
        if (Document is null || source is not (GalaxySystem or Planet))
        {
            return;
        }

        var target = ResolveWritableTarget(source);
        if (target is null)
        {
            return;
        }

        IReadOnlyList<MoveDestinationChoice> choices;
        try
        {
            choices = _rowAuthoring.GetMoveDestinations(source);
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            Fail(exception.Message);
            return;
        }

        if (choices.Count == 0)
        {
            Fail($"No alternative {(source is GalaxySystem ? "Clusters" : "Systems")} are available.");
            return;
        }

        var options = choices.Select(choice => new MoveDestinationOption(
            choice.RowId,
            choice.DisplayName,
            choice.Detail,
            choice.CurrentLabel,
            choice.ResultingLabel)).ToArray();
        if (_dialogs.ChooseMoveDestination(source, options) is { } destination)
        {
            MoveRow(source, destination.RowId, target);
        }
    }

    public bool MoveRow(GalaxyMapRow source, int destinationParentRowId)
    {
        var target = ResolveWritableTarget(source);
        return target is not null && MoveRow(source, destinationParentRowId, target);
    }

    private bool MoveRow(GalaxyMapRow source, int destinationParentRowId, GalaxyMapModule target)
    {
        var result = _rowAuthoring.Move(
            source,
            destinationParentRowId,
            target,
            CaptureHistoryPresentation(source.Key, CaptureView()));
        if (!result.Succeeded)
        {
            return Fail(result.Error ?? result.Message);
        }
        ApplyMutationResult(result);
        return true;
    }

    private void DeleteRow(GalaxyMapRow row)
    {
        if (!Confirm($"Delete {row.Table} row {row.RowId} ({RowDisplayName(row)})?"))
        {
            return;
        }
        ApplyMutationResult(_rowAuthoring.Delete(row, CaptureHistoryPresentation()));
    }

    private void BeginRelayCreation(Cluster source)
    {
        var result = _relay.BeginCreation(source);
        if (!result.Succeeded)
        {
            Fail(result.Error ?? result.Message);
            return;
        }

        ErrorMessage = string.Empty;
        NavigateGalaxy();
        _navigation.TrySelectWithoutNavigation(source.Key);
        _navigation.RefreshInspector(source);
        StatusMessage = result.Message;
    }

    private void BeginRelayRedirect(Cluster source, RelayConnection relay)
    {
        var targetModule = ResolveWritableTarget(relay);
        if (targetModule is null)
        {
            return;
        }

        var result = _relay.BeginRedirect(source, relay, targetModule);
        if (!result.Succeeded)
        {
            Fail(result.Error ?? result.Message);
            return;
        }

        OnActiveModuleChanged();
        ErrorMessage = string.Empty;
        NavigateGalaxy();
        _navigation.TrySelectWithoutNavigation(source.Key);
        _navigation.RefreshInspector(source);
        StatusMessage = result.Message;
    }

    private void CancelRelayCreation()
    {
        if (_relay.Cancel() is not { } sourceKey)
        {
            return;
        }

        NavigateGalaxy();
        if (Workspace?.Resolve(sourceKey) is Cluster source)
        {
            _navigation.TrySelectWithoutNavigation(source.Key);
            _navigation.RefreshInspector(source);
        }
        UpdateDocumentSummary(null);
    }

    private void RemoveRelay(RelayConnection relay)
    {
        if (_navigation.SelectedRow is not Cluster selectedCluster)
        {
            Fail("Create or open a writable module before removing Relay connections.");
            return;
        }

        ApplyMutationResult(_relay.Remove(
            relay,
            selectedCluster,
            CaptureHistoryPresentation(selectedCluster.Key, ViewContext.Galaxy)));
    }

    private bool ApplyManagedInspectorEdit(GalaxyMapRow inspectedRow, string propertyName, object? value)
    {
        if (!_inspectorEdits.IsManaged(inspectedRow, propertyName, value))
        {
            return false;
        }

        var target = ResolveWritableTarget(inspectedRow);
        if (target is null)
        {
            _edits.CancelUserEdit();
            return true;
        }

        var edit = _inspectorEdits.ApplyEdit(
            inspectedRow,
            propertyName,
            value,
            target,
            CaptureHistoryPresentation(inspectedRow.Key));
        if (!edit.Handled)
        {
            return false;
        }

        if (edit.Result is not null)
        {
            if (!edit.Result.Succeeded)
            {
                _edits.CancelUserEdit();
            }
            ApplyMutationResult(edit.Result);
        }
        return true;
    }

    private WorkflowResult ApplyTableCellEdit(GalaxyMapRowKey key, string column, string token)
    {
        var row = Workspace?.Resolve(key);
        if (row is null)
        {
            return WorkflowResult.Failure($"{key.Table} row {key.RowId} is no longer present.", key);
        }

        var target = ResolveWritableTarget(row, preferActiveModule: true);
        if (target is null)
        {
            var message = HasError
                ? ErrorMessage
                : "Cell edit cancelled; the source row was left unchanged.";
            return WorkflowResult.Failure(message, key);
        }

        GalaxyMapModule? renamedTextureModule = null;
        if (row is Planet planet && PlanetAppearanceSchema.Properties.Any(definition =>
                definition.Editor == PlanetAppearanceEditorKind.Texture &&
                definition.Columns.Contains(column, StringComparer.OrdinalIgnoreCase)))
        {
            var oldPath = PlanetAppearanceCodec.Decode(planet)[column];
            renamedTextureModule = PlanetTextureWorkflow.CreateRenamedReference(
                target,
                oldPath,
                token,
                out var renameError);
            if (renameError is not null)
            {
                return WorkflowResult.Failure(renameError, key);
            }
        }

        var view = CaptureView();
        _isApplyingTableCellEdit = true;
        try
        {
            var result = _inspectorEdits.ApplyTableCellEdit(
                row,
                column,
                token,
                target,
                CaptureHistoryPresentation(key, view));
            if (!result.Succeeded)
            {
                StatusMessage = result.Error ?? result.Message;
                return result;
            }

            if (renamedTextureModule is not null && Workspace is { } workspace)
            {
                workspace.ReplaceModule(target, renamedTextureModule);
                _edits.MarkMetadataDirty(renamedTextureModule);
            }

            _navigation.PreferredInstanceTag = target.Tag;
            RefreshWorkspace(
                result.SelectionKey ?? key,
                view,
                result.Message,
                preserveHierarchy: result.Impact?.IsStructural != true,
                refreshModules: false,
                deferValidation: true);
            OnActiveModuleChanged();
            ErrorMessage = string.Empty;
            return result;
        }
        finally
        {
            _isApplyingTableCellEdit = false;
        }
    }

    private void ModelOnPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (_isApplying || _edits.IsApplying || sender is not GalaxyMapRow row ||
            !_inspectorEdits.TryGetCsvColumn(row, eventArgs.PropertyName, out _))
        {
            return;
        }

        if (Workspace is null)
        {
            if (CurrentViewModel is MapViewModelBase legacyMap)
            {
                legacyMap.Refresh();
            }

            if (row is Cluster changedCluster &&
                eventArgs.PropertyName == nameof(Cluster.Background) &&
                CurrentViewModel is ClusterViewModel clusterView &&
                ReferenceEquals(clusterView.Cluster, changedCluster))
            {
                clusterView.UpdateBackgroundTexture(_textures.GetClusterTexture(changedCluster.Background));
            }

            return;
        }

        var view = CaptureView();
        var selectionKey = _navigation.SelectedKey ?? row.Key;
        var physicalLayer = Workspace.Layers.FirstOrDefault(candidate => ReferenceEquals(candidate.Find(row.Key), row));
        var targetModule = physicalLayer?.Module ?? ResolveWritableTarget(row);
        if (targetModule is null)
        {
            _edits.CancelUserEdit();
            Workspace.Recompose();
            RefreshWorkspace(selectionKey, view, null);
            if (!HasError)
            {
                StatusMessage = "Edit cancelled; the source row was left unchanged.";
            }
            return;
        }

        var result = _inspectorEdits.ApplyScalarEdit(
            row,
            eventArgs.PropertyName!,
            targetModule,
            CaptureHistoryPresentation(selectionKey, view));
        if (!result.Succeeded)
        {
            RefreshWorkspace(selectionKey, view, null);
            Fail(result.Error ?? result.Message);
            return;
        }

        _navigation.PreferredInstanceTag = targetModule.Tag;
        RefreshWorkspace(
            selectionKey,
            view,
            result.Message,
            preserveHierarchy: true,
            refreshModules: false,
            deferValidation: true);
        OnActiveModuleChanged();
        ErrorMessage = string.Empty;
    }

    private void UpdateValidation()
    {
        _validation.Cancel();
        ApplyValidation(_validation.Validate(Workspace, Document, _workspaceWorkflows.StartupDiagnostics));
    }

    private void ApplyValidation(ValidationSnapshot snapshot)
    {
        ValidationDiagnostics.ReplaceAll(snapshot.Diagnostics);
        Inspector.RefreshIdentityEditability();
        TableViewer.RefreshIdentityEditability();

        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(DiagnosticCount));
        OnPropertyChanged(nameof(ValidationErrorCount));
        OnPropertyChanged(nameof(ValidationWarningCount));
        if (!HasDiagnostics)
        {
            IsDiagnosticsPanelOpen = false;
        }
    }

    private void ScheduleValidation(string? status)
        => _validation.Schedule(
            () => _validation.Validate(Workspace, Document, _workspaceWorkflows.StartupDiagnostics),
            status);

    private void ValidationOnCompleted(object? sender, ValidationCompletedEventArgs eventArgs)
    {
        ApplyValidation(eventArgs.Snapshot);
        UpdateDocumentSummary(eventArgs.DeferredStatus);
    }

    private void UpdateDocumentSummary(string? leadingMessage)
    {
        if (Document is null)
        {
            return;
        }

        var validation = ValidationErrorCount == 0 && ValidationWarningCount == 0
            ? "No validation errors or warnings."
            : $"Validation: {ValidationErrorCount} error(s), {ValidationWarningCount} warning(s).";
        var counts = $"{Document.Clusters.Count} clusters, {Document.Systems.Count} systems, " +
                     $"{Document.Planets.Count} planets and {Document.Relays.Count} relays.";
        StatusMessage = string.IsNullOrWhiteSpace(leadingMessage)
            ? $"{counts} {validation}"
            : $"{leadingMessage} {counts} {validation}";
    }

    private void RefreshMountedModules()
    {
        if (Workspace is null)
        {
            MountedModules.ReplaceAll([]);
            ModuleBarItems.ReplaceAll([]);
            return;
        }

        MountedModules.ReplaceAll(Workspace.Layers.Select(layer => layer.Module));
        RefreshModuleBarItems();
    }

    private void RefreshModuleBarItems()
    {
        if (Workspace is null)
        {
            ModuleBarItems.ReplaceAll([]);
            return;
        }

        ModuleBarItems.ReplaceAll(Workspace.Modules.OrderBy(module => module.LoadOrder)
            .ThenBy(module => module.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(module =>
            {
                var isDirty = _session.Changes.ContainsModule(module.Tag);
                return new ModuleBarItemViewModel(
                    module,
                    ReferenceEquals(module, ActiveModule),
                    isDirty,
                    EditModule);
            }));
    }

    private void RaiseCommandStates()
    {
        CreateModuleCommand.RaiseCanExecuteChanged();
        OpenModuleCommand.RaiseCanExecuteChanged();
        RefreshRememberedWorkspaceCommand.RaiseCanExecuteChanged();
        ToggleTableViewCommand.RaiseCanExecuteChanged();
        AddClusterCommand.RaiseCanExecuteChanged();
        AddSystemCommand.RaiseCanExecuteChanged();
        AddPlanetCommand.RaiseCanExecuteChanged();
        ContextualAddCommand.RaiseCanExecuteChanged();
        _navigation.RaiseNodeCommandStates();
        NavigateGalaxyCommand.RaiseCanExecuteChanged();
        NavigateClusterCommand.RaiseCanExecuteChanged();
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
        DiscardChangesCommand.RaiseCanExecuteChanged();
    }

    private ViewContext CaptureView() => CurrentViewModel switch
    {
        SystemViewModel systemView => new ViewContext(systemView.System.ClusterRowId, systemView.System.RowId),
        ClusterViewModel clusterView => new ViewContext(clusterView.Cluster.RowId, null),
        _ => ViewContext.Galaxy
    };

    private bool Fail(string message)
    {
        ErrorMessage = message;
        StatusMessage = message;
        return false;
    }

    private static bool IsExpectedOperationFailure(Exception exception)
        => exception is GalaxyMapLoadException or IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or OverflowException;

    private readonly record struct ViewContext(int? ClusterRowId, int? SystemRowId)
    {
        public static ViewContext Galaxy => new(null, null);
    }

    private sealed record CoordinateDragSession(
        GalaxyMapRow Row,
        GalaxyMapLayer Layer,
        double OriginalX,
        double OriginalY,
        ViewContext View);

    private void BeginUserEdit()
        => _edits.BeginUserEdit(CaptureHistoryPresentation());

    private HistoryPresentationState CaptureHistoryPresentation(
        GalaxyMapRowKey? selectionKey = null,
        ViewContext? view = null)
    {
        var context = view ?? CaptureView();
        return new HistoryPresentationState(
            selectionKey ?? _navigation.SelectedKey,
            new NavigationTarget(context.ClusterRowId, context.SystemRowId),
            _navigation.PreferredInstanceTag,
            _navigation.InspectPhysicalInstance);
    }

    private void Undo()
    {
        var result = _edits.Undo(CaptureHistoryPresentation());
        ApplyHistoryRestore(result);
    }

    private void Redo()
    {
        var result = _edits.Redo(CaptureHistoryPresentation());
        ApplyHistoryRestore(result);
    }

    private void ApplyHistoryRestore(HistoryRestoreResult result)
    {
        if (!result.Succeeded || result.Presentation is not { } presentation)
        {
            return;
        }

        Workspace = _session.Workspace;
        _navigation.RestoreInspectionState(
            presentation.PreferredInstanceTag,
            presentation.InspectPhysicalInstance);
        RefreshWorkspace(
            presentation.SelectionKey,
            new ViewContext(presentation.Navigation.ClusterRowId, presentation.Navigation.SystemRowId),
            result.Message);
        NotifyPendingChanges();
        NotifyHistoryChanged();
    }

    private void ConfirmDiscardChanges()
    {
        if (Confirm("Discard every uncommitted change?"))
        {
            DiscardPendingChanges();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _validation.Completed -= ValidationOnCompleted;
        _validation.Dispose();
        _session.Changed -= SessionOnChanged;
        _relay.StateChanged -= RelayStateOnChanged;
        _navigation.Changed -= NavigationOnChanged;
        _navigation.Dispose();
    }

    private bool Confirm(string message)
        => _dialogs.Confirm(message);

    private void NotifyHistoryChanged()
    {
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
    }
}
