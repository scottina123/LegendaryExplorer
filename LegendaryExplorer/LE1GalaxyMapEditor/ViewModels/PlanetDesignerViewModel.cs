using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using LE1GalaxyMapEditor.Infrastructure;
using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Services;
using LE1GalaxyMapEditor.Workflows;
using LE1GalaxyMapEditor.Workflows.Editing;

namespace LE1GalaxyMapEditor.ViewModels;

public sealed class PlanetAppearanceComponentViewModel : ObservableObject
{
    private readonly PlanetAppearance _appearance;
    private readonly bool _numeric;
    private readonly bool _textureReference;
    private readonly Action _changed;
    private readonly Dictionary<string, string> _textureReferencesByDisplayName =
        new(StringComparer.OrdinalIgnoreCase);
    private string _validationError = string.Empty;

    public PlanetAppearanceComponentViewModel(
        PlanetAppearance appearance,
        string column,
        string label,
        bool numeric,
        bool textureReference,
        Action changed)
    {
        _appearance = appearance;
        Column = column;
        Label = label;
        _numeric = numeric;
        _textureReference = textureReference;
        _changed = changed;
    }

    public string Column { get; }
    public string Label { get; }
    public string RawValue => _appearance[Column];
    public string Value
    {
        get => _textureReference
            ? PlanetAppearanceCodec.TextureDisplayName(_appearance[Column])
            : _appearance[Column];
        set
        {
            value ??= string.Empty;
            var storedValue = _textureReference &&
                              _textureReferencesByDisplayName.TryGetValue(value.Trim(), out var textureReference)
                ? textureReference
                : value;
            if (string.Equals(RawValue, storedValue, StringComparison.Ordinal))
            {
                return;
            }

            _appearance[Column] = storedValue;
            Validate();
            OnPropertyChanged();
            OnPropertyChanged(nameof(NumericValue));
            _changed();
        }
    }

    public double NumericValue
    {
        get => PlanetAppearanceCodec.TryParseFloat(Value, out var value) ? value : 0;
        set => Value = PlanetAppearanceCodec.Format((float)value);
    }

    public string ValidationError
    {
        get => _validationError;
        private set
        {
            if (SetProperty(ref _validationError, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => ValidationError.Length > 0;

    public void Refresh()
    {
        Validate();
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(NumericValue));
    }

    public void SetTextureReferences(IEnumerable<string> references)
    {
        if (!_textureReference)
        {
            return;
        }

        _textureReferencesByDisplayName.Clear();
        foreach (var reference in references.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            _textureReferencesByDisplayName[PlanetAppearanceCodec.TextureDisplayName(reference)] = reference;
        }
    }

    public void EnsureTextureReference(string reference)
    {
        if (!_textureReference || string.IsNullOrWhiteSpace(reference))
        {
            return;
        }

        _textureReferencesByDisplayName[PlanetAppearanceCodec.TextureDisplayName(reference)] = reference;
    }

    private void Validate()
    {
        ValidationError = _numeric && !PlanetAppearanceCodec.TryParseFloat(Value, out _)
            ? "Enter a finite number using a dot as the decimal separator."
            : string.Empty;
    }
}

public sealed class PlanetAppearanceFieldViewModel : ObservableObject
{
    public PlanetAppearanceFieldViewModel(
        PlanetAppearance appearance,
        PlanetAppearancePropertyDefinition definition,
        Action changed)
        : this(appearance, definition, [], changed)
    {
    }

    public PlanetAppearanceFieldViewModel(
        PlanetAppearance appearance,
        PlanetAppearancePropertyDefinition definition,
        IEnumerable<string> additionalTextureOptions,
        Action changed)
    {
        Definition = definition;
        var numeric = definition.Editor is PlanetAppearanceEditorKind.Scalar or
            PlanetAppearanceEditorKind.ColorVector or PlanetAppearanceEditorKind.MixerVector;
        void FieldChanged()
        {
            OnPropertyChanged(nameof(ColorBrush));
            OnPropertyChanged(nameof(ColorSummary));
            changed();
        }

        Components = definition.Columns.Select((column, index) => new PlanetAppearanceComponentViewModel(
            appearance,
            column,
            definition.Columns.Count == 1
                ? definition.DisplayName
                : definition.ComponentLabels?.ElementAtOrDefault(index) ?? "RGBA"[index].ToString(),
            numeric,
            definition.Editor == PlanetAppearanceEditorKind.Texture,
            FieldChanged)).ToArray();
        VisibleComponents = Editor == PlanetAppearanceEditorKind.MixerVector
            ? Components.Take(definition.ComponentLabels?.Count ?? 3).ToArray()
            : Components;
        TextureOptions = [];
        RefreshTextureOptions(additionalTextureOptions);
    }

    public PlanetAppearancePropertyDefinition Definition { get; }
    public string DisplayName => Definition.DisplayName;
    public string Description => Definition.Description;
    public PlanetAppearanceEditorKind Editor => Definition.Editor;
    public IReadOnlyList<PlanetAppearanceComponentViewModel> Components { get; }
    public IReadOnlyList<PlanetAppearanceComponentViewModel> VisibleComponents { get; }
    public ObservableCollection<string> TextureOptions { get; }
    public PlanetAppearanceComponentViewModel Primary => Components[0];
    public double Minimum => Definition.Minimum ?? 0;
    public double Maximum => Definition.Maximum ?? 1;
    public double Step => Definition.Step ?? 0.01;
    public bool IsScalar => Editor == PlanetAppearanceEditorKind.Scalar;
    public bool IsShader => Editor == PlanetAppearanceEditorKind.Shader;
    public bool IsTexture => Editor == PlanetAppearanceEditorKind.Texture;
    public bool IsPackedColor => Editor == PlanetAppearanceEditorKind.PackedColor;
    public bool IsColorVector => Editor == PlanetAppearanceEditorKind.ColorVector;
    public bool IsMixer => Editor == PlanetAppearanceEditorKind.MixerVector;
    public bool HasError => Components.Any(component => component.HasError);
    public Brush ColorBrush => IsPackedColor || IsColorVector
        ? new SolidColorBrush(GetDisplayColor())
        : Brushes.Transparent;
    public string ColorSummary
    {
        get
        {
            if (!IsPackedColor && !IsColorVector)
            {
                return string.Empty;
            }

            var color = GetColorValues();
            var display = GetDisplayColor();
            var intensity = Math.Max(
                GetColorComponent(color, 0),
                Math.Max(GetColorComponent(color, 1), GetColorComponent(color, 2)));
            return IsPackedColor
                ? $"#{display.A:X2}{display.R:X2}{display.G:X2}{display.B:X2}"
                : $"#{display.R:X2}{display.G:X2}{display.B:X2}  x{intensity:0.###}";
        }
    }

    public void Refresh()
    {
        if (IsTexture && !string.IsNullOrWhiteSpace(Primary.Value) &&
            !TextureOptions.Contains(Primary.Value, StringComparer.OrdinalIgnoreCase))
        {
            Primary.EnsureTextureReference(Primary.RawValue);
            TextureOptions.Add(Primary.Value);
        }

        foreach (var component in Components)
        {
            component.Refresh();
        }
        OnPropertyChanged(nameof(ColorBrush));
        OnPropertyChanged(nameof(ColorSummary));
    }

    public void RefreshTextureOptions(IEnumerable<string> additionalTextureOptions)
    {
        if (!IsTexture)
        {
            return;
        }

        var references = (Definition.TextureOptions ?? [])
            .Concat(additionalTextureOptions)
            .Append(Primary.RawValue)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Primary.SetTextureReferences(references);
        var options = references
            .Select(PlanetAppearanceCodec.TextureDisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Keep the current selection present while synchronizing the bound list.
        foreach (var option in options)
        {
            if (!TextureOptions.Contains(option, StringComparer.OrdinalIgnoreCase))
            {
                TextureOptions.Add(option);
            }
        }

        for (var index = TextureOptions.Count - 1; index >= 0; index--)
        {
            if (!options.Contains(TextureOptions[index], StringComparer.OrdinalIgnoreCase))
            {
                TextureOptions.RemoveAt(index);
            }
        }

        for (var targetIndex = 0; targetIndex < options.Length; targetIndex++)
        {
            var currentIndex = IndexOf(TextureOptions, options[targetIndex]);
            if (currentIndex != targetIndex)
            {
                TextureOptions.Move(currentIndex, targetIndex);
            }
        }
    }

    private static int IndexOf(IReadOnlyList<string> values, string target)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], target, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private double[] GetColorValues()
    {
        if (IsPackedColor)
        {
            var packed = long.TryParse(Primary.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed)
                ? unchecked((uint)signed)
                : uint.MaxValue;
            return
            [
                ((packed >> 16) & 0xff) / 255d,
                ((packed >> 8) & 0xff) / 255d,
                (packed & 0xff) / 255d,
                ((packed >> 24) & 0xff) / 255d
            ];
        }

        return Components.Select(component =>
                PlanetAppearanceCodec.TryParseFloat(component.Value, out var value) ? (double)value : 0)
            .ToArray();
    }

    private Color GetDisplayColor()
    {
        var values = GetColorValues();
        var red = GetColorComponent(values, 0);
        var green = GetColorComponent(values, 1);
        var blue = GetColorComponent(values, 2);
        var maximum = Math.Max(red, Math.Max(green, blue));
        var divisor = IsPackedColor || maximum <= 1 ? 1 : maximum;
        return Color.FromArgb(
            255,
            (byte)Math.Round(Math.Clamp(red / divisor, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(green / divisor, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(blue / divisor, 0, 1) * 255));
    }

    private static double GetColorComponent(IReadOnlyList<double> values, int index) =>
        index < values.Count ? values[index] : 0;
}

public sealed record PlanetAppearanceGroupViewModel(
    string Name,
    string Description,
    bool ExpandedByDefault,
    bool ShowLinkTextureButton,
    IReadOnlyList<PlanetAppearanceFieldViewModel> Fields);

public sealed record PlanetTextureLinkOption(
    string ModuleTag,
    string ModuleName,
    string LinkId,
    string InMemoryPath,
    PlanetTextureCategory Categories,
    bool IsAvailable,
    bool CanUnlink,
    int ReferenceCount)
{
    public string Availability => IsAvailable ? "Preview available" : "Preview file missing";
    public string ReferenceDetail => ReferenceCount == 0
        ? "No Planet rows currently reference this texture"
        : $"Referenced by {ReferenceCount} Planet row" + (ReferenceCount == 1 ? string.Empty : "s");
}

public enum PlanetDesignerNavigationChoice
{
    Apply,
    Discard,
    Cancel
}

public sealed class PlanetDesignerViewModel : ObservableObject
{
    private const int MaximumDraftHistoryEntries = 50;
    private static PlanetAppearance? s_appearanceClipboard;
    private static string s_appearanceClipboardSource = string.Empty;
    private readonly Func<GalaxyMapWorkspace?> _workspace;
    private PlanetDesignerSession _session;
    private readonly Func<PlanetDesignerSession, WorkflowResult> _apply;
    private readonly Func<GalaxyMapRowKey, bool> _undo;
    private readonly Func<GalaxyMapRowKey, bool> _redo;
    private readonly Func<bool> _canUndo;
    private readonly Func<bool> _canRedo;
    private readonly Func<GalaxyMapRowKey, string?, Planet?> _resolvePlanet;
    private readonly Func<PlanetTextureLinkRequest, WorkflowResult> _linkTexture;
    private readonly Func<string, string, WorkflowResult> _unlinkTexture;
    private readonly Func<string, Rendering.PlanetPreviewTextureSource?> _resolvePreviewTexture;
    private readonly Func<IEnumerable<string>, IReadOnlyDictionary<string, Rendering.PlanetPreviewTextureSource>>
        _resolvePreviewTextures;
    private readonly Func<IReadOnlyList<string>> _packageTextureOptions;
    private IReadOnlyList<string>? _cachedPackageTextureOptions;
    private readonly Stack<PlanetAppearance> _draftUndo = [];
    private readonly Stack<PlanetAppearance> _draftRedo = [];
    private PlanetAppearance _lastDraftSnapshot;
    private PlanetAppearance? _draftEditStart;
    private int _draftEditDepth;
    private readonly PlanetAppearanceTemplateStore _templates;
    private IReadOnlyList<PlanetAppearancePreset> _allPresets;
    private string _presetSearch = string.Empty;
    private string _statusMessage = string.Empty;
    private string _errorMessage = string.Empty;
    private ImageSource? _previewImage;
    private string _previewDetail = "Preparing preview…";
    private bool _showLighting = true;
    private bool _showPostProcessing = true;
    private bool _showCorona = true;
    private bool _showStars = true;
    private bool _performanceMode = true;
    private double _cloudSpeed = 1;

    public PlanetDesignerViewModel(
        Func<GalaxyMapWorkspace?> workspace,
        PlanetDesignerSession session,
        Func<PlanetDesignerSession, WorkflowResult> apply,
        Func<GalaxyMapRowKey, bool> undo,
        Func<GalaxyMapRowKey, bool> redo,
        Func<bool> canUndo,
        Func<bool> canRedo,
        Func<GalaxyMapRowKey, string?, Planet?> resolvePlanet,
        PlanetAppearanceTemplateStore? templates = null)
        : this(
            workspace,
            session,
            apply,
            undo,
            redo,
            canUndo,
            canRedo,
            resolvePlanet,
            _ => WorkflowResult.Failure("Planet texture linking is unavailable in this context."),
            _ => null,
            templates,
            null,
            null)
    {
    }

    public PlanetDesignerViewModel(
        Func<GalaxyMapWorkspace?> workspace,
        PlanetDesignerSession session,
        Func<PlanetDesignerSession, WorkflowResult> apply,
        Func<GalaxyMapRowKey, bool> undo,
        Func<GalaxyMapRowKey, bool> redo,
        Func<bool> canUndo,
        Func<bool> canRedo,
        Func<GalaxyMapRowKey, string?, Planet?> resolvePlanet,
        Func<PlanetTextureLinkRequest, WorkflowResult> linkTexture,
        Func<string, Rendering.PlanetPreviewTextureSource?> resolvePreviewTexture,
        PlanetAppearanceTemplateStore? templates = null,
        Func<string, string, WorkflowResult>? unlinkTexture = null,
        Func<IReadOnlyList<string>>? packageTextureOptions = null,
        Func<IEnumerable<string>, IReadOnlyDictionary<string, Rendering.PlanetPreviewTextureSource>>?
            resolvePreviewTextures = null)
    {
        _workspace = workspace;
        _session = session;
        _apply = apply;
        _undo = undo;
        _redo = redo;
        _canUndo = canUndo;
        _canRedo = canRedo;
        _resolvePlanet = resolvePlanet;
        _linkTexture = linkTexture;
        _unlinkTexture = unlinkTexture ?? ((_, _) =>
            WorkflowResult.Failure("Planet texture unlinking is unavailable in this context."));
        _resolvePreviewTexture = resolvePreviewTexture;
        _resolvePreviewTextures = resolvePreviewTextures ?? (references => references
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(reference => (Reference: reference, Source: resolvePreviewTexture(reference)))
            .Where(item => item.Source is not null)
            .ToDictionary(
                item => item.Reference,
                item => item.Source!,
                StringComparer.OrdinalIgnoreCase));
        _packageTextureOptions = packageTextureOptions ?? (() => []);
        _templates = templates ?? new PlanetAppearanceTemplateStore();
        _allPresets = workspace() is { } currentWorkspace
            ? PlanetAppearancePresetCatalog.Build(currentWorkspace)
            : [];

        _lastDraftSnapshot = session.Draft.Clone();
        Groups = CreateGroups(session.Draft);
        ApplyCommand = new RelayCommand(Apply, CanApply);
        RandomiseCommand = new RelayCommand(Randomise, () => _workspace() is not null);
        UndoCommand = new RelayCommand(Undo,
            () => _draftEditDepth == 0 && (_draftUndo.Count > 0 || !IsDirty && _canUndo()));
        RedoCommand = new RelayCommand(Redo,
            () => _draftEditDepth == 0 && (_draftRedo.Count > 0 || !IsDirty && _canRedo()));
        UseTemplateCommand = new RelayCommand<PlanetAppearanceTemplate>(UseTemplate, template => template is not null);
        DeleteTemplateCommand = new RelayCommand<PlanetAppearanceTemplate>(DeleteTemplate, template => template is not null);
        DismissErrorCommand = new RelayCommand(
            () => ErrorMessage = string.Empty,
            () => HasError);
        RefreshPresets();
        RefreshTemplates();
        Validate();
        ReportUnavailableTextureLinks();
    }

    public event EventHandler? PreviewRequested;
    public string Title => $"Planet Designer — {_session.DisplayName}";
    public string PlanetName => _session.DisplayName;
    public GalaxyMapRowKey PlanetKey => _session.Key;
    public string ModuleTag => _session.ModuleTag;
    public IReadOnlyList<PlanetAppearanceGroupViewModel> Groups { get; private set; }
    public ObservableCollection<PlanetPresetModuleGroup> PresetModules { get; } = [];
    public ObservableCollection<PlanetAppearanceTemplate> PersonalTemplates { get; } = [];
    public RelayCommand ApplyCommand { get; }
    public RelayCommand RandomiseCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand<PlanetAppearanceTemplate> UseTemplateCommand { get; }
    public RelayCommand<PlanetAppearanceTemplate> DeleteTemplateCommand { get; }
    public RelayCommand DismissErrorCommand { get; }
    public bool IsDirty => _session.HasChanges;
    public bool IsNewPlanet => _session.IsNewPlanet;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasErrors => Groups.SelectMany(group => group.Fields).Any(item => item.HasError) || ErrorMessage.Length > 0;

    public string PresetSearch
    {
        get => _presetSearch;
        set
        {
            if (SetProperty(ref _presetSearch, value ?? string.Empty))
            {
                RefreshPresets();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(HasErrors));
                DismissErrorCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ImageSource? PreviewImage
    {
        get => _previewImage;
        private set => SetProperty(ref _previewImage, value);
    }

    public string PreviewDetail
    {
        get => _previewDetail;
        private set => SetProperty(ref _previewDetail, value);
    }

    public bool ShowLighting
    {
        get => _showLighting;
        set { if (SetProperty(ref _showLighting, value)) RequestPreview(); }
    }

    public bool ShowPostProcessing
    {
        get => _showPostProcessing;
        set { if (SetProperty(ref _showPostProcessing, value)) RequestPreview(); }
    }

    public bool ShowCorona
    {
        get => _showCorona;
        set { if (SetProperty(ref _showCorona, value)) RequestPreview(); }
    }

    public bool ShowStars
    {
        get => _showStars;
        set { if (SetProperty(ref _showStars, value)) RequestPreview(); }
    }

    public bool PerformanceMode
    {
        get => _performanceMode;
        set { if (SetProperty(ref _performanceMode, value)) RequestPreview(); }
    }

    public double CloudSpeed
    {
        get => _cloudSpeed;
        set => SetProperty(ref _cloudSpeed, value);
    }

    public Rendering.PlanetRenderMaterial CreateRenderMaterial() =>
        PlanetAppearanceCodec.ToRenderMaterial(_session.Draft);

    public Rendering.PlanetPreviewTextureSource? ResolvePreviewTexture(string inMemoryPath) =>
        _resolvePreviewTexture(inMemoryPath);

    public IReadOnlyDictionary<string, Rendering.PlanetPreviewTextureSource> ResolvePreviewTextures(
        IEnumerable<string> inMemoryPaths)
        => _resolvePreviewTextures(inMemoryPaths);

    public Rendering.PlanetPreviewOptions CreatePreviewOptions() => new(
        Lit: ShowLighting,
        PointLights: ShowLighting,
        PostProcessed: ShowPostProcessing,
        Corona: ShowCorona,
        Stars: ShowStars);

    public bool TryNavigateToPlanet(
        GalaxyMapRowKey key,
        string? moduleTag,
        PlanetDesignerNavigationChoice choice)
    {
        if (key == _session.Key &&
            (moduleTag is null || string.Equals(moduleTag, _session.ModuleTag, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (IsDirty || IsNewPlanet)
        {
            switch (choice)
            {
                case PlanetDesignerNavigationChoice.Apply when !ApplyCore():
                    return false;
                case PlanetDesignerNavigationChoice.Cancel:
                    return false;
            }
        }

        if (_resolvePlanet(key, moduleTag) is not { } planet || !PlanetAppearanceCodec.IsAppearanceCapable(planet))
        {
            ErrorMessage = "The selected Planet row is no longer available to the designer.";
            return false;
        }

        _session = new PlanetDesignerSession(planet);
        ResetDraftHistory();
        Groups = CreateGroups(_session.Draft);
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PlanetName));
        OnPropertyChanged(nameof(PlanetKey));
        OnPropertyChanged(nameof(ModuleTag));
        OnPropertyChanged(nameof(Groups));
        StatusMessage = $"Editing {planet.DisplayName}.";
        Validate();
        RaiseState();
        RequestPreview();
        return true;
    }

    public void CopyAppearance(PlanetAppearancePreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        s_appearanceClipboard = preset.Appearance.Clone();
        s_appearanceClipboardSource = $"{preset.PlanetName} [{preset.ModuleTag}]";
        StatusMessage = $"Copied the appearance from {s_appearanceClipboardSource}.";
        ErrorMessage = string.Empty;
    }

    public bool PasteAppearance()
    {
        if (s_appearanceClipboard is null)
        {
            StatusMessage = "Copy a Planet appearance from the tree first.";
            return false;
        }

        _session.Draft.CopyVisualsFrom(s_appearanceClipboard);
        RefreshFields();
        StatusMessage = $"Pasted the appearance from {s_appearanceClipboardSource}. Shader name was kept.";
        AppearanceChanged();
        return true;
    }

    public void UseTemplate(PlanetAppearanceTemplate? template)
    {
        if (template is null)
        {
            return;
        }

        _session.Draft.CopyVisualsFrom(template.ToAppearance());
        RefreshFields();
        StatusMessage = $"Applied personal template '{template.Name}'. Shader name was kept.";
        AppearanceChanged();
    }

    public bool SaveTemplate(string name, string? description)
    {
        try
        {
            _templates.SaveNew(name, description, _session.Draft);
            RefreshTemplates();
            StatusMessage = $"Saved personal template '{name.Trim()}'.";
            ErrorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            ErrorMessage = exception.Message;
            return false;
        }
    }

    public bool TryApply() => ApplyCore();

    public void BeginDraftPropertyEdit()
    {
        if (_draftEditDepth++ == 0)
        {
            _draftEditStart = _session.Draft.Clone();
            UndoCommand.RaiseCanExecuteChanged();
            RedoCommand.RaiseCanExecuteChanged();
        }
    }

    public void EndDraftPropertyEdit()
    {
        if (_draftEditDepth == 0 || --_draftEditDepth > 0)
        {
            return;
        }

        if (_draftEditStart is { } start &&
            PlanetAppearanceCodec.ChangedColumns(start, _session.Draft).Count > 0)
        {
            PushDraftHistory(_draftUndo, start);
            _draftRedo.Clear();
            _lastDraftSnapshot = _session.Draft.Clone();
        }

        _draftEditStart = null;
        RaiseState();
    }

    private void Randomise()
    {
        if (_workspace() is not { } workspace)
        {
            ErrorMessage = "The galaxy-map workspace is no longer available.";
            return;
        }

        try
        {
            RefreshTextureOptions();
            var linkedTextures = workspace.Layers
                .SelectMany(layer => layer.Module.PlanetTextureLinks)
                .ToArray();
            var availableTextures = linkedTextures
                .Where(link => _resolvePreviewTexture(link.InMemoryPath) is not null)
                .ToArray();
            var result = PlanetAppearanceRandomizer.Generate(
                _session.Draft,
                workspace.BaseLayer.Planets,
                customTextures: availableTextures);
            _session.Draft.CopyVisualsFrom(result.Appearance);
            RefreshFields();
            var customTextureDetail = result.CustomTexturePaths.Count == 0
                ? string.Empty
                : $" using {result.CustomTexturePaths.Count} linked custom texture" +
                  (result.CustomTexturePaths.Count == 1 ? string.Empty : "s");
            var unavailableTextureDetail = linkedTextures.Length == availableTextures.Length
                ? string.Empty
                : $"; ignored {linkedTextures.Length - availableTextures.Length} unavailable texture link" +
                  (linkedTextures.Length - availableTextures.Length == 1 ? string.Empty : "s");
            StatusMessage =
                $"Randomised from {result.DonorName}{customTextureDetail}{unavailableTextureDetail} " +
                $"(seed {result.Seed}). Shader name was kept.";
            ErrorMessage = string.Empty;
            AppearanceChanged();
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    public bool LinkModuleTexture(PlanetTextureLinkRequest request)
    {
        var result = _linkTexture(request);
        if (!result.Succeeded)
        {
            ErrorMessage = result.Error ?? result.Message;
            return false;
        }

        RefreshTextureOptions();
        StatusMessage = result.Message;
        ErrorMessage = string.Empty;
        return true;
    }

    public IReadOnlyList<PlanetTextureLinkOption> GetLinkedTextureOptions()
    {
        if (_workspace() is not { } workspace)
        {
            return [];
        }

        var textureColumns = PlanetAppearanceSchema.Properties
            .Where(property => property.Editor == PlanetAppearanceEditorKind.Texture)
            .SelectMany(property => property.Columns)
            .ToArray();
        return workspace.Layers
            .SelectMany(layer => layer.Module.PlanetTextureLinks.Select(link =>
            {
                var references = workspace.Layers.SelectMany(candidate => candidate.Planets)
                    .Count(planet => textureColumns.Any(column =>
                    string.Equals(
                        planet.ExtraFields.GetValueOrDefault(column),
                        link.InMemoryPath,
                        StringComparison.OrdinalIgnoreCase)));
                return new PlanetTextureLinkOption(
                    layer.Module.Tag,
                    layer.Module.Name,
                    link.Id,
                    link.InMemoryPath,
                    link.Categories,
                    _resolvePreviewTexture(link.InMemoryPath) is not null,
                    !layer.Module.IsReadOnly && !layer.Module.IsBaseGame,
                    references);
            }))
            .OrderBy(option => option.ModuleTag, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.InMemoryPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void RefreshLinkedTextureState() => RefreshTextureOptions();

    public bool UnlinkModuleTexture(PlanetTextureLinkOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        var result = _unlinkTexture(option.ModuleTag, option.LinkId);
        if (!result.Succeeded)
        {
            ErrorMessage = result.Error ?? result.Message;
            return false;
        }

        RefreshTextureOptions();
        StatusMessage = result.Message;
        ErrorMessage = string.Empty;
        return true;
    }

    public void SetPreview(ImageSource image, TimeSpan renderTime, IReadOnlyList<string> missingTextures)
    {
        PreviewImage = image;
        var resolution = image is System.Windows.Media.Imaging.BitmapSource bitmap
            ? $"{bitmap.PixelWidth}x{bitmap.PixelHeight} · "
            : string.Empty;
        PreviewDetail = missingTextures.Count == 0
            ? $"{resolution}rendered in {renderTime.TotalMilliseconds:0.0} ms"
            : $"{resolution}rendered in {renderTime.TotalMilliseconds:0.0} ms · fallback textures: {string.Join(", ", missingTextures)}";
    }

    public void SetPreviewError(string message)
    {
        PreviewDetail = "Preview unavailable";
        StatusMessage = message;
    }

    public void RequestPreview() => PreviewRequested?.Invoke(this, EventArgs.Empty);

    private bool CanApply() => (IsDirty || IsNewPlanet) && !Groups.SelectMany(group => group.Fields).Any(field => field.HasError);

    private void Apply() => ApplyCore();

    private bool ApplyCore()
    {
        Validate();
        if (HasErrors)
        {
            return false;
        }

        var result = _apply(_session);
        if (!result.Succeeded)
        {
            if (result.Error is null)
            {
                StatusMessage = result.Message;
                ErrorMessage = string.Empty;
            }
            else
            {
                ErrorMessage = result.Error;
            }
            return false;
        }

        StatusMessage = result.Message;
        ErrorMessage = string.Empty;
        ResetDraftHistory();
        OnPropertyChanged(nameof(ModuleTag));
        RefreshFields();
        RefreshPresetCatalog();
        RaiseState();
        return true;
    }

    private void Undo()
    {
        if (_draftUndo.Count > 0)
        {
            RestoreDraft(_draftUndo, _draftRedo, "Undid the last Planet Designer property change.");
            return;
        }

        _draftRedo.Clear();
        if (_undo(_session.Key)) ReloadFromWorkspace("Undid the last staged editor change.");
    }

    private void Redo()
    {
        if (_draftRedo.Count > 0)
        {
            RestoreDraft(_draftRedo, _draftUndo, "Redid the Planet Designer property change.");
            return;
        }

        if (_redo(_session.Key)) ReloadFromWorkspace("Redid the staged editor change.");
    }

    private void ReloadFromWorkspace(string status)
    {
        if (_resolvePlanet(_session.Key, null) is not { } planet)
        {
            ErrorMessage = "The Planet row is no longer present after restoring history.";
            return;
        }

        _session.Reload(planet);
        ResetDraftHistory();
        OnPropertyChanged(nameof(ModuleTag));
        RefreshFields();
        RefreshPresetCatalog();
        StatusMessage = status;
        Validate();
        RequestPreview();
    }

    private void DeleteTemplate(PlanetAppearanceTemplate? template)
    {
        if (template is null)
        {
            return;
        }

        try
        {
            _templates.Delete(template);
            RefreshTemplates();
            StatusMessage = $"Deleted personal template '{template.Name}'.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = exception.Message;
        }
    }

    private void AppearanceChanged()
    {
        if (_draftEditDepth == 0 &&
            PlanetAppearanceCodec.ChangedColumns(_lastDraftSnapshot, _session.Draft).Count > 0)
        {
            PushDraftHistory(_draftUndo, _lastDraftSnapshot);
            _draftRedo.Clear();
            _lastDraftSnapshot = _session.Draft.Clone();
        }
        Validate();
        RaiseState();
        RequestPreview();
    }

    private void RestoreDraft(
        Stack<PlanetAppearance> source,
        Stack<PlanetAppearance> destination,
        string status)
    {
        PushDraftHistory(destination, _session.Draft.Clone());
        var restored = source.Pop();
        _session.Draft.ReplaceFrom(restored);
        _lastDraftSnapshot = restored.Clone();
        RefreshFields();
        StatusMessage = status;
        Validate();
        RaiseState();
        RequestPreview();
    }

    private void ResetDraftHistory()
    {
        _draftUndo.Clear();
        _draftRedo.Clear();
        _draftEditStart = null;
        _draftEditDepth = 0;
        _lastDraftSnapshot = _session.Draft.Clone();
    }

    private static void PushDraftHistory(Stack<PlanetAppearance> history, PlanetAppearance appearance)
    {
        history.Push(appearance.Clone());
        if (history.Count <= MaximumDraftHistoryEntries)
        {
            return;
        }

        var newestFirst = history.Take(MaximumDraftHistoryEntries).ToArray();
        history.Clear();
        for (var index = newestFirst.Length - 1; index >= 0; index--)
        {
            history.Push(newestFirst[index]);
        }
    }

    private void Validate()
    {
        var numericError = Groups.SelectMany(group => group.Fields).Any(field => field.HasError);
        if (numericError)
        {
            ErrorMessage = "Correct the highlighted numeric material value before applying.";
        }
        else if (IsDirty || IsNewPlanet)
        {
            if (_workspace() is { } currentWorkspace)
            {
                var sourceLayer = currentWorkspace.Layers.FirstOrDefault(candidate =>
                    string.Equals(candidate.Module.Tag, _session.ModuleTag, StringComparison.OrdinalIgnoreCase));
                if (sourceLayer is not { Module.IsReadOnly: false, Module.IsBaseGame: false })
                {
                    ErrorMessage = currentWorkspace.Modules.Any(module => !module.IsReadOnly && !module.IsBaseGame)
                        ? string.Empty
                        : "Select a writable module before applying this Planet appearance.";
                    return;
                }

                var shader = PlanetShaderNameValidator.Validate(
                    currentWorkspace,
                    _session.Key,
                    _session.Draft,
                    sourceLayer.Module.Tag);
                ErrorMessage = shader.IsValid ? string.Empty : shader.Message;
            }
            else
            {
                ErrorMessage = "The galaxy-map workspace is no longer available.";
            }
        }
        else
        {
            ErrorMessage = string.Empty;
        }
    }

    private void RefreshFields()
    {
        foreach (var field in Groups.SelectMany(group => group.Fields))
        {
            field.Refresh();
        }
    }

    private void RefreshTextureOptions()
    {
        foreach (var group in Groups)
        {
            var options = GetModuleTextureOptions(group.Name);
            foreach (var field in group.Fields)
            {
                field.RefreshTextureOptions(options);
            }
        }
    }

    private void ReportUnavailableTextureLinks()
    {
        if (!string.IsNullOrWhiteSpace(StatusMessage))
        {
            return;
        }

        var unavailable = GetLinkedTextureOptions().Count(option => !option.IsAvailable);
        if (unavailable > 0)
        {
            StatusMessage = $"{unavailable} linked Planet texture" +
                            (unavailable == 1 ? " has" : "s have") +
                            " a missing preview file and were excluded from material menus and randomisation.";
        }
    }

    private IReadOnlyList<PlanetAppearanceGroupViewModel> CreateGroups(PlanetAppearance appearance)
        => PlanetAppearanceSchema.Groups
            .Select(group =>
            {
                var textureOptions = GetModuleTextureOptions(group.Name).ToArray();
                return new PlanetAppearanceGroupViewModel(
                group.Name,
                group.Description,
                group.ExpandedByDefault,
                string.Equals(group.Name, "Identity", StringComparison.Ordinal) &&
                _workspace()?.Modules.Any(module => !module.IsPccBacked) == true,
                PlanetAppearanceSchema.Properties
                    .Where(property => property.Group == group.Name)
                    .Select(definition => new PlanetAppearanceFieldViewModel(
                        appearance,
                        definition,
                        textureOptions,
                        AppearanceChanged)).ToArray());
            })
            .ToArray();

    private IEnumerable<string> GetModuleTextureOptions(string groupName)
    {
        var category = groupName switch
        {
            "Continent / Landmass" => PlanetTextureCategory.Continent,
            "Normals" => PlanetTextureCategory.Normals,
            "Ocean" => PlanetTextureCategory.Ocean,
            "City Emissive" => PlanetTextureCategory.CityEmissive,
            "Atmosphere / Horizon" => PlanetTextureCategory.Atmosphere,
            _ => PlanetTextureCategory.None
        };
        if (category == PlanetTextureCategory.None || _workspace() is not { } workspace)
        {
            return [];
        }

        return workspace.Layers
            .SelectMany(layer => layer.Module.PlanetTextureLinks)
            .Where(link => (link.Categories & category) != 0)
            .Where(link => _resolvePreviewTexture(link.InMemoryPath) is not null)
            .Select(link => link.InMemoryPath)
            .Concat(_cachedPackageTextureOptions ??= _packageTextureOptions())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void RefreshPresetCatalog()
    {
        _allPresets = _workspace() is { } currentWorkspace
            ? PlanetAppearancePresetCatalog.Build(currentWorkspace)
            : [];
        RefreshPresets();
    }

    private void RefreshPresets()
    {
        PresetModules.Clear();
        foreach (var module in PlanetAppearancePresetCatalog.Group(_allPresets, PresetSearch))
        {
            PresetModules.Add(module);
        }
    }

    private void RefreshTemplates()
    {
        PersonalTemplates.Clear();
        foreach (var template in _templates.LoadAll())
        {
            PersonalTemplates.Add(template);
        }

        if (_templates.Warnings.Count > 0)
        {
            StatusMessage = _templates.Warnings.Count == 1
                ? _templates.Warnings[0]
                : $"Skipped {_templates.Warnings.Count} unavailable or invalid personal Planet templates.";
        }
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(IsNewPlanet));
        OnPropertyChanged(nameof(HasErrors));
        ApplyCommand.RaiseCanExecuteChanged();
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
    }
}
