using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LE1GalaxyMapEditor.Infrastructure;
using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Rendering;
using LE1GalaxyMapEditor.Services;
using LE1GalaxyMapEditor.ViewModels;

namespace LE1GalaxyMapEditor.Views;

public partial class PlanetDesignerWindow : Window
{
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _resizeTimer;
    private readonly Stopwatch _animationClock = new();
    private readonly PlanetDesignerWindowDiagnostics _diagnostics = new();
    private readonly ConcurrentDictionary<string, PlanetPreviewTextureSource> _previewTextureSources =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pendingPreviewTextures =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _unavailablePreviewTextures =
        new(StringComparer.OrdinalIgnoreCase);
    private PlanetPreviewRenderer? _renderer;
    private WriteableBitmap? _previewBitmap;
    private byte[]? _previewPixels;
    private DispatcherOperation? _scheduledPreview;
    private double _accumulatedAnimationTime;
    private double _lastClockTime;
    private int _renderWidth;
    private int _renderHeight;
    private bool _allowClose;
    private bool _closed;
    private bool _initializingRenderer;
    private bool _rendering;
    private PlanetAppearancePreset? _mouseNavigatedPreset;
    private bool _restoringPresetSelection;

    public PlanetDesignerWindow(PlanetDesignerViewModel viewModel)
    {
        global::LE1GalaxyMapEditor.Theming.EditorThemeManager.Initialize(this);
        InitializeComponent();
        DataContext = viewModel;
        _previewTimer = new DispatcherTimer(DispatcherPriority.Render);
        _previewTimer.Tick += PreviewTimer_OnTick;
        _resizeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _resizeTimer.Tick += ResizeTimer_OnTick;
        viewModel.PreviewRequested += ViewModel_OnPreviewRequested;
        viewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        UpdatePerformanceMode();
        Loaded += Window_OnLoaded;
        Closed += Window_OnClosed;
        Activated += Window_OnPreviewActivityChanged;
        Deactivated += Window_OnPreviewActivityChanged;
        StateChanged += Window_OnPreviewActivityChanged;
        IsVisibleChanged += Window_OnVisibilityChanged;
    }

    public PlanetDesignerViewModel ViewModel => (PlanetDesignerViewModel)DataContext;
    public PlanetDesignerWindowDiagnostics Diagnostics => _diagnostics;

    /// <summary>
    /// Measures the expensive expanded preset tree before an HWND is made visible,
    /// so the first displayed frame already contains the dark designer surface.
    /// </summary>
    public void PrepareForFirstShow()
    {
        if (IsLoaded)
        {
            return;
        }

        ApplyTemplate();
        var layoutWidth = double.IsFinite(Width) ? Width : MinWidth;
        var layoutHeight = double.IsFinite(Height) ? Height : MinHeight;
        if (Content is FrameworkElement content)
        {
            content.ApplyTemplate();
            content.Measure(new Size(layoutWidth, layoutHeight));
            content.Arrange(new Rect(0, 0, layoutWidth, layoutHeight));
            content.UpdateLayout();
        }
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs eventArgs) =>
        Dispatcher.BeginInvoke(InitializeRenderer, DispatcherPriority.ContextIdle);

    private void Window_OnClosed(object? sender, EventArgs eventArgs)
    {
        _closed = true;
        _previewTimer.Stop();
        _resizeTimer.Stop();
        _animationClock.Stop();
        AbortScheduledPreview();
        ViewModel.PreviewRequested -= ViewModel_OnPreviewRequested;
        ViewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        if (_renderer is { } renderer)
        {
            DisposeRenderer(renderer);
        }
        _renderer = null;
        _previewBitmap = null;
        _previewPixels = null;
    }

    private void Window_OnPreviewActivityChanged(object? sender, EventArgs eventArgs) =>
        UpdatePreviewActivity();

    private void Window_OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs eventArgs) =>
        UpdatePreviewActivity();

    private void ViewModel_OnPreviewRequested(object? sender, EventArgs eventArgs)
    {
        _diagnostics.RecordPreviewRequest();
        SchedulePreview();
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(PlanetDesignerViewModel.PerformanceMode))
        {
            UpdatePerformanceMode();
        }
    }

    private async void InitializeRenderer()
    {
        if (_renderer is not null || _initializingRenderer || _closed)
        {
            return;
        }

        _initializingRenderer = true;
        _diagnostics.RecordRendererInitializationStarted();
        var target = GetPreviewResolution();
        var initialKey = ViewModel.PlanetKey;
        var material = ViewModel.CreateRenderMaterial();
        var options = ViewModel.CreatePreviewOptions();
        try
        {
            if (!await CachePreviewTextureSourcesAsync(material) || _closed)
            {
                return;
            }

            var initialized = await Task.Run(() =>
            {
                var renderer = new PlanetPreviewRenderer(
                    target.Width,
                    target.Height,
                    materialTextureResolver: name => _previewTextureSources.GetValueOrDefault(name));
                try
                {
                    return (Renderer: renderer, Frame: renderer.Render(material, options, 0));
                }
                catch
                {
                    DisposeRenderer(renderer);
                    throw;
                }
            });

            _diagnostics.RecordRendererInitializationCompleted();
            if (_closed)
            {
                DisposeRenderer(initialized.Renderer);
                return;
            }

            _renderer = initialized.Renderer;
            _renderWidth = target.Width;
            _renderHeight = target.Height;
            _previewBitmap = new WriteableBitmap(
                _renderWidth, _renderHeight, 96, 96, PixelFormats.Bgra32, null);
            _previewPixels = CreatePreviewBuffer(_renderWidth, _renderHeight);
            PresentFrame(initialized.Frame);

            var currentTarget = GetPreviewResolution();
            if (currentTarget.Width != _renderWidth || currentTarget.Height != _renderHeight)
            {
                ResizeRendererToViewport();
            }
            else if (initialKey != ViewModel.PlanetKey)
            {
                RenderPreview();
            }

            UpdatePreviewActivity(scheduleCurrentFrame: false);
        }
        catch (Exception exception)
        {
            _diagnostics.RecordRendererInitializationFailure();
            ViewModel.SetPreviewError($"Planet preview could not be initialised: {exception.Message}");
        }
        finally
        {
            _initializingRenderer = false;
        }
    }

    private void PreviewHost_OnSizeChanged(object sender, SizeChangedEventArgs eventArgs)
    {
        if (!IsLoaded || !IsPreviewActive)
        {
            return;
        }
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void ResizeTimer_OnTick(object? sender, EventArgs eventArgs)
    {
        _resizeTimer.Stop();
        ResizeRendererToViewport();
    }

    private bool ResizeRendererToViewport()
    {
        if (_renderer is null)
        {
            return false;
        }

        var target = GetPreviewResolution();
        var width = target.Width;
        var height = target.Height;
        if (width == _renderWidth && height == _renderHeight)
        {
            return false;
        }

        _renderer.Resize(width, height);
        _previewBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        _previewPixels = CreatePreviewBuffer(width, height);
        _renderWidth = width;
        _renderHeight = height;
        RenderPreview();
        return true;
    }

    private PlanetPreviewPixelSize GetPreviewResolution()
    {
        var dpi = VisualTreeHelper.GetDpi(PreviewHost);
        return PlanetPreviewResolution.Fit16By9(
            PreviewHost.ActualWidth,
            PreviewHost.ActualHeight,
            dpi.DpiScaleX,
            dpi.DpiScaleY);
    }

    private bool IsPreviewActive =>
        !_closed && IsVisible && WindowState != WindowState.Minimized &&
        (IsActive || OwnedWindows.Cast<Window>().Any(window =>
            window.IsVisible && window is ColorPickerWindow or HdrColorPickerWindow));

    private void UpdatePreviewActivity(bool scheduleCurrentFrame = true)
    {
        if (!IsPreviewActive)
        {
            _previewTimer.Stop();
            _resizeTimer.Stop();
            _animationClock.Stop();
            AbortScheduledPreview();
            return;
        }

        if (_renderer is null || _previewBitmap is null || _previewPixels is null)
        {
            return;
        }

        if (!_animationClock.IsRunning)
        {
            _animationClock.Start();
            _lastClockTime = _animationClock.Elapsed.TotalSeconds;
        }

        var resized = ResizeRendererToViewport();
        _previewTimer.Start();
        if (scheduleCurrentFrame && !resized)
        {
            SchedulePreview();
        }
    }

    private static byte[] CreatePreviewBuffer(int width, int height) =>
        new byte[checked(width * height * 4)];

    private void AbortScheduledPreview()
    {
        _scheduledPreview?.Abort();
        _scheduledPreview = null;
    }

    private void SchedulePreview()
    {
        if (!IsPreviewActive || _renderer is null || _previewBitmap is null ||
            _previewPixels is null || _scheduledPreview is not null)
        {
            return;
        }

        _diagnostics.RecordScheduledPreviewOperation();
        _scheduledPreview = Dispatcher.BeginInvoke(() =>
        {
            _scheduledPreview = null;
            _diagnostics.RecordScheduledPreviewDispatch();
            RenderPreview();
        }, DispatcherPriority.Render);
    }

    private void PreviewTimer_OnTick(object? sender, EventArgs eventArgs)
    {
        if (!IsPreviewActive)
        {
            return;
        }

        _diagnostics.RecordTimerTick();
        RenderPreview();
    }

    private void RenderPreview()
    {
        if (!IsPreviewActive)
        {
            return;
        }

        _diagnostics.RecordRenderAttempt();
        if (_renderer is null || _previewBitmap is null || _previewPixels is null)
        {
            _diagnostics.RecordRenderSkipUnavailable();
            return;
        }
        if (_rendering)
        {
            _diagnostics.RecordRenderSkipBusy();
            return;
        }
        try
        {
            _rendering = true;
            var now = _animationClock.Elapsed.TotalSeconds;
            _accumulatedAnimationTime += (now - _lastClockTime) * ViewModel.CloudSpeed;
            _lastClockTime = now;

            var material = ViewModel.CreateRenderMaterial();
            _ = CachePreviewTextureSourcesAsync(material);
            var frame = _renderer.Render(
                material,
                ViewModel.CreatePreviewOptions(),
                _previewPixels,
                (float)_accumulatedAnimationTime);
            PresentFrame(frame);
        }
        catch (Exception exception)
        {
            ViewModel.SetPreviewError($"Planet preview could not be rendered: {exception.Message}");
        }
        finally
        {
            _rendering = false;
        }
    }

    private void PresentFrame(PlanetPreviewFrame frame)
    {
        if (_previewBitmap is null)
        {
            return;
        }

        _previewBitmap.WritePixels(
            new Int32Rect(0, 0, frame.Width, frame.Height),
            frame.BgraPixels,
            frame.Width * 4,
            0);
        ViewModel.SetPreview(_previewBitmap, frame.RenderTime, frame.MissingTextures);
        _diagnostics.RecordFramePresented();
    }

    private async Task<bool> CachePreviewTextureSourcesAsync(PlanetRenderMaterial material)
    {
        var activeReferences = PreviewTextureReferences(material).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var obsolete in _previewTextureSources.Keys.Where(key => !activeReferences.Contains(key)))
        {
            _previewTextureSources.TryRemove(obsolete, out _);
        }
        foreach (var obsolete in _unavailablePreviewTextures.Keys.Where(key => !activeReferences.Contains(key)))
        {
            _unavailablePreviewTextures.TryRemove(obsolete, out _);
        }
        var missingReferences = activeReferences.Where(reference =>
                !_previewTextureSources.ContainsKey(reference) &&
                !_unavailablePreviewTextures.ContainsKey(reference) &&
                _pendingPreviewTextures.TryAdd(reference, 0))
            .ToArray();
        if (missingReferences.Length == 0)
        {
            return true;
        }

        IReadOnlyDictionary<string, PlanetPreviewTextureSource> resolved;
        try
        {
            var viewModel = ViewModel;
            resolved = await Task.Run(() => viewModel.ResolvePreviewTextures(missingReferences));
        }
        catch (Exception exception)
        {
            foreach (var reference in missingReferences)
            {
                _pendingPreviewTextures.TryRemove(reference, out _);
            }
            if (!_closed)
            {
                ViewModel.SetPreviewError(
                    $"Planet textures could not be loaded: {exception.GetBaseException().Message}");
            }
            return false;
        }

        if (_closed)
        {
            return false;
        }
        foreach (var reference in missingReferences)
        {
            _pendingPreviewTextures.TryRemove(reference, out _);
            if (resolved.TryGetValue(reference, out var source))
            {
                _previewTextureSources[reference] = source;
                _unavailablePreviewTextures.TryRemove(reference, out _);
            }
            else
            {
                _previewTextureSources.TryRemove(reference, out _);
                _unavailablePreviewTextures[reference] = 0;
            }
        }
        SchedulePreview();
        return true;
    }

    private static IReadOnlyList<string> PreviewTextureReferences(PlanetRenderMaterial material)
        => new[]
            {
                material.NormalMap,
                material.CityEmissive,
                material.ContinentMask01,
                material.ContinentMask02,
                material.ContinentTexture,
                material.OceanTexture,
                material.AtmosphereMaster,
                "BIOA_GXM10_T.GXM_PlanetNormal01",
                "BIOA_GXM10_T.GXM_ContinentMask02",
                "BIOA_GXM10_T.GXM_ContinentMask01",
                "BIOA_GXM10_T.GXM_DiffuseMask01",
                "BIOA_GXM10_T.GXM_Atmosphere01",
                "BIOA_GXM10_T.GXM_CoronaGradient"
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private void DisposeRenderer(PlanetPreviewRenderer renderer)
    {
        renderer.Dispose();
        _diagnostics.RecordRendererDisposal();
    }

    private void UpdatePerformanceMode()
    {
        _previewTimer.Interval = ViewModel.PerformanceMode
            ? TimeSpan.FromMilliseconds(1000.0 / 60)
            : TimeSpan.FromMilliseconds(100);
        RenderOptions.SetBitmapScalingMode(
            PreviewImage,
            ViewModel.PerformanceMode ? BitmapScalingMode.LowQuality : BitmapScalingMode.HighQuality);
        SchedulePreview();
    }

    private void ResetClouds_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        _accumulatedAnimationTime = 0;
        _lastClockTime = _animationClock.Elapsed.TotalSeconds;
        RenderPreview();
    }

    private void LinkModuleTexture_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new PlanetTextureLinkWindow { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            ViewModel.LinkModuleTexture(dialog.Request);
        }
    }

    private void ManageModuleTextures_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        ViewModel.RefreshLinkedTextureState();
        var manager = new PlanetTextureManagerWindow(ViewModel.GetLinkedTextureOptions()) { Owner = this };
        if (manager.ShowDialog() != true || manager.SelectedOption is not { } option)
        {
            return;
        }

        var referenceWarning = option.ReferenceCount == 0
            ? "No Planet rows currently reference it."
            : $"{option.ReferenceCount} Planet row" + (option.ReferenceCount == 1 ? " currently references" : "s currently reference") +
              " it and will use a fallback preview until changed.";
        var confirmation = new ConfirmationWindow(
            "Unlink Planet texture",
            $"Unlink {option.InMemoryPath} from {option.ModuleTag}?\n\n{referenceWarning}\n\n" +
            "The module preview file will not be deleted.",
            "Unlink texture",
            "Keep linked")
        {
            Owner = this
        };
        confirmation.ShowDialog();
        if (confirmation.Choice == ConfirmationChoice.Primary)
        {
            ViewModel.UnlinkModuleTexture(option);
        }
    }

    public bool NavigateToPlanet(GalaxyMapRowKey key, string? moduleTag = null) =>
        TryNavigateToPlanet(key, moduleTag, null);

    private void PresetTree_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (ItemsControl.ContainerFromElement(PresetTree, eventArgs.OriginalSource as DependencyObject)
                is not TreeViewItem { DataContext: PlanetAppearancePreset preset })
        {
            return;
        }

        if (!TryNavigateToPlanet(preset.PlanetKey, preset.ModuleTag, preset.PlanetName))
        {
            eventArgs.Handled = true;
            return;
        }

        _mouseNavigatedPreset = preset;
    }

    private void PresetTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> eventArgs)
    {
        if (_restoringPresetSelection || eventArgs.NewValue is not PlanetAppearancePreset preset)
        {
            return;
        }

        if (ReferenceEquals(_mouseNavigatedPreset, preset))
        {
            _mouseNavigatedPreset = null;
            return;
        }

        if (!TryNavigateToPlanet(preset.PlanetKey, preset.ModuleTag, preset.PlanetName))
        {
            RestorePresetSelection(eventArgs.OldValue, eventArgs.NewValue);
        }
    }

    private bool TryNavigateToPlanet(GalaxyMapRowKey key, string? moduleTag, string? destinationName)
    {
        var choice = PlanetDesignerNavigationChoice.Discard;
        var samePlanet = key == ViewModel.PlanetKey &&
                         (moduleTag is null || string.Equals(
                             moduleTag,
                             ViewModel.ModuleTag,
                             StringComparison.OrdinalIgnoreCase));
        if (!samePlanet && (ViewModel.IsDirty || ViewModel.IsNewPlanet))
        {
            var dialog = new ConfirmationWindow(
                "Unapplied Planet appearance",
                $"Apply the changes to {ViewModel.PlanetName} before switching to {destinationName ?? $"Planet row {key.RowId}"}?",
                "Apply",
                "Discard",
                "Cancel") { Owner = this };
            dialog.ShowDialog();
            choice = dialog.Choice switch
            {
                ConfirmationChoice.Primary => PlanetDesignerNavigationChoice.Apply,
                ConfirmationChoice.Secondary => PlanetDesignerNavigationChoice.Discard,
                _ => PlanetDesignerNavigationChoice.Cancel
            };
        }

        return ViewModel.TryNavigateToPlanet(key, moduleTag, choice);
    }

    private void RestorePresetSelection(object? oldValue, object? newValue)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _restoringPresetSelection = true;
            try
            {
                if (FindTreeItem(PresetTree, newValue) is { } rejected)
                {
                    rejected.IsSelected = false;
                }
                if (FindTreeItem(PresetTree, oldValue) is { } previous)
                {
                    previous.IsSelected = true;
                }
            }
            finally
            {
                _restoringPresetSelection = false;
            }
        }, DispatcherPriority.Input);
    }

    private static TreeViewItem? FindTreeItem(ItemsControl parent, object? item)
    {
        if (item is null)
        {
            return null;
        }

        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem direct)
        {
            return direct;
        }

        foreach (var childItem in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(childItem) is TreeViewItem child &&
                FindTreeItem(child, item) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private void CopyAppearance_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is MenuItem { DataContext: PlanetAppearancePreset preset })
        {
            ViewModel.CopyAppearance(preset);
        }
    }

    private void PasteAppearance_OnClick(object sender, RoutedEventArgs eventArgs) => ViewModel.PasteAppearance();

    private void TemplateList_OnMouseDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (TemplateList.SelectedItem is PlanetAppearanceTemplate template)
        {
            ViewModel.UseTemplate(template);
        }
    }

    private void SaveTemplate_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new PlanetTemplateWindow { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            ViewModel.SaveTemplate(dialog.TemplateName, dialog.Description);
        }
    }

    private void PackedColor_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not FrameworkElement { DataContext: PlanetAppearanceFieldViewModel field }) return;
        var original = field.Primary.Value;
        var dialog = new ColorPickerWindow(field.Primary.Value) { Owner = this };
        ViewModel.BeginDraftPropertyEdit();
        try
        {
            dialog.PreviewColorChanged += value => field.Primary.Value = value;
            if (dialog.ShowDialog() == true && dialog.Result is { } result)
            {
                field.Primary.Value = result;
            }
            else
            {
                field.Primary.Value = original;
            }
        }
        finally
        {
            ViewModel.EndDraftPropertyEdit();
        }
    }

    private void HdrColor_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not FrameworkElement { DataContext: PlanetAppearanceFieldViewModel { Components.Count: 4 } field }) return;
        var originalValues = field.Components.Select(component => component.Value).ToArray();
        var values = originalValues.Select(value =>
            PlanetAppearanceCodec.TryParseFloat(value, out var parsed) ? parsed : 0).ToArray();
        var original = new Vector4(values[0], values[1], values[2], values[3]);
        var dialog = new HdrColorPickerWindow(original) { Owner = this };
        void ApplyColor(Vector4 selected)
        {
            field.Components[0].Value = PlanetAppearanceCodec.Format(selected.X);
            field.Components[1].Value = PlanetAppearanceCodec.Format(selected.Y);
            field.Components[2].Value = PlanetAppearanceCodec.Format(selected.Z);
            field.Components[3].Value = PlanetAppearanceCodec.Format(selected.W);
        }

        ViewModel.BeginDraftPropertyEdit();
        try
        {
            dialog.PreviewColorChanged += ApplyColor;
            if (dialog.ShowDialog() == true && dialog.SelectedColor is { } selected)
            {
                ApplyColor(selected);
            }
            else
            {
                for (var index = 0; index < field.Components.Count; index++)
                {
                    field.Components[index].Value = originalValues[index];
                }
            }
        }
        finally
        {
            ViewModel.EndDraftPropertyEdit();
        }
    }

    private void PropertySlider_OnDragStarted(object sender, DragStartedEventArgs eventArgs) =>
        ViewModel.BeginDraftPropertyEdit();

    private void PropertySlider_OnDragCompleted(object sender, DragCompletedEventArgs eventArgs) =>
        ViewModel.EndDraftPropertyEdit();

    private void Close_OnClick(object sender, RoutedEventArgs eventArgs) => Close();

    private void Window_OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_allowClose || (!ViewModel.IsDirty && !ViewModel.IsNewPlanet)) return;
        var dialog = new ConfirmationWindow(
            "Unapplied Planet appearance",
            "Apply this Planet appearance before closing the designer?",
            "Apply",
            "Discard",
            "Cancel") { Owner = this };
        dialog.ShowDialog();
        switch (dialog.Choice)
        {
            case ConfirmationChoice.Primary when ViewModel.TryApply():
            case ConfirmationChoice.Secondary:
                _allowClose = true;
                break;
            default:
                eventArgs.Cancel = true;
                break;
        }
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && eventArgs.Key == Key.S)
        {
            if (ViewModel.ApplyCommand.CanExecute(null)) ViewModel.ApplyCommand.Execute(null);
            eventArgs.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && eventArgs.Key == Key.Z)
        {
            if (ViewModel.UndoCommand.CanExecute(null)) ViewModel.UndoCommand.Execute(null);
            eventArgs.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && eventArgs.Key == Key.Y)
        {
            if (ViewModel.RedoCommand.CanExecute(null)) ViewModel.RedoCommand.Execute(null);
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Escape)
        {
            Close();
            eventArgs.Handled = true;
        }
    }
}
