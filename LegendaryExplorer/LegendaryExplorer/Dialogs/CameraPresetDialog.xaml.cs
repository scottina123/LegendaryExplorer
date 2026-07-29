using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.InterpEditor;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Dialogs;

public partial class CameraPresetDialog : Window
{
    private sealed record PresetSearchResult(CameraPreset Preset, string Name, string CategoryDisplay);

    private static string SavedOriginPath => Path.Combine(AppDirectories.AppDataFolder, "CameraPresetOriginV2.txt");
    private static string SavedPresetPath => Path.Combine(AppDirectories.AppDataFolder, "CameraPresetSelection.txt");

    private readonly Func<CameraOrigin?> _getTrackKeyOrigin;
    private readonly Func<CameraOrigin?> _getViewportOrigin;
    private readonly Action<GeneratedCameraKey> _previewCamera;
    private readonly float? _maximumEndTime;
    private readonly IMEPackage _package;
    private CameraPreset _selectedPreset;
    private bool _updatingCameraSpeed;
    private bool _updatingPresetSelection;

    public IReadOnlyList<GeneratedCameraKey> GeneratedKeys { get; private set; }
    public float GeneratedStartTime { get; private set; }

    public CameraPresetDialog(Func<CameraOrigin?> getTrackKeyOrigin, Func<CameraOrigin?> getViewportOrigin,
        Action<GeneratedCameraKey> previewCamera, float initialStartTime = 0, float? maximumEndTime = null,
        IMEPackage package = null)
    {
        _getTrackKeyOrigin = getTrackKeyOrigin;
        _getViewportOrigin = getViewportOrigin;
        _previewCamera = previewCamera;
        _maximumEndTime = maximumEndTime;
        _package = package;

        InitializeComponent();
        CustomWindowChrome.ApplyCustomChrome(this);

        StaticPresetList.ItemsSource = CameraPresetCatalog.GetByCategory(CameraPresetCategory.StaticShots);
        DynamicPresetList.ItemsSource = CameraPresetCatalog.GetByCategory(CameraPresetCategory.DynamicShots);
        ReactionPresetList.ItemsSource = CameraPresetCatalog.GetByCategory(CameraPresetCategory.ReactionShots);
        UseTrackKeyButton.IsEnabled = _getTrackKeyOrigin?.Invoke() is not null;
        UseOtherTrackKeyButton.IsEnabled = _package is not null;
        bool hasViewport = _getViewportOrigin?.Invoke() is not null;
        UseViewportLocationButton.IsEnabled = hasViewport;
        UseViewportTransformButton.IsEnabled = hasViewport;
        PreviewButton.IsEnabled = _previewCamera is not null && hasViewport;
        StatusTextBlock.Text = hasViewport ? "Preview uses the connected Level Editor viewport." : "Connect a Level Editor to enable viewport actions and preview.";
        StartTimeTextBox.Text = Format(initialStartTime);
        SetOrigin(LoadSavedOrigin());
        foreach (TextBox textBox in new[]
        {
            OriginXTextBox, OriginYTextBox, OriginZTextBox, OriginRollTextBox, OriginPitchTextBox, OriginYawTextBox,
            ForwardDistanceTextBox, DistanceScaleTextBox, SideOffsetTextBox, HeightOffsetTextBox, LookAtHeightTextBox,
            LocalRollTextBox, LocalPitchTextBox, LocalYawTextBox, DurationTextBox, MovementAmountTextBox
        })
        {
            textBox.TextChanged += PreviewParameter_TextChanged;
        }
        SelectSavedPreset();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (TryReadOrigin(out CameraOrigin origin))
        {
            SaveOrigin(origin);
        }
        if (_selectedPreset is not null)
        {
            SavePreset(_selectedPreset);
        }

        CameraPreviewControl.Dispose();
        base.OnClosed(e);
    }

    public static bool GenerateForTrack(Window owner, ExportEntry export, float initialStartTime = 0,
        Func<CameraOrigin?> getViewportOrigin = null, Action<GeneratedCameraKey> previewCamera = null)
    {
        if (export?.ClassName != "InterpTrackMove")
        {
            return false;
        }

        var track = new InterpTrackMove(export);
        float? maximumEndTime = FindOwningInterpData(export)?.GetProperty<FloatProperty>("InterpLength")?.Value;
        var dialog = new CameraPresetDialog(
            () => track.GetCameraOriginNearestTime(initialStartTime),
            getViewportOrigin,
            previewCamera,
            initialStartTime,
            maximumEndTime,
            export.FileRef)
        {
            Owner = owner
        };

        if (dialog.ShowDialog() != true || dialog.GeneratedKeys is not { Count: > 0 })
        {
            return false;
        }

        track.InsertCameraPresetKeys(dialog.GeneratedStartTime, dialog.GeneratedKeys);
        return true;
    }

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPresetSelection || sender is not ListBox { SelectedItem: CameraPreset preset })
        {
            return;
        }

        _updatingPresetSelection = true;
        foreach (ListBox list in new[] { StaticPresetList, DynamicPresetList, ReactionPresetList })
        {
            if (list != sender)
            {
                list.SelectedItem = null;
            }
        }
        PresetSearchResultsList.SelectedItem = null;
        _updatingPresetSelection = false;

        SelectPreset(preset);
    }

    private void PresetSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PresetTabs is null || PresetSearchResultsList is null)
        {
            return;
        }

        string search = PresetSearchTextBox.Text.Trim();
        bool isSearching = search.Length > 0;
        PresetTabs.Visibility = isSearching ? Visibility.Collapsed : Visibility.Visible;
        PresetSearchResultsList.Visibility = isSearching ? Visibility.Visible : Visibility.Collapsed;
        if (!isSearching)
        {
            PresetSearchResultsList.ItemsSource = null;
            return;
        }

        PresetSearchResultsList.ItemsSource = CameraPresetCatalog.All
            .Where(preset => preset.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || GetCategoryDisplay(preset.Category).Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(preset => new PresetSearchResult(preset, preset.Name, GetCategoryDisplay(preset.Category)))
            .ToList();
    }

    private void PresetSearchResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPresetSelection || PresetSearchResultsList.SelectedItem is not PresetSearchResult result)
        {
            return;
        }

        _updatingPresetSelection = true;
        foreach (ListBox list in new[] { StaticPresetList, DynamicPresetList, ReactionPresetList })
        {
            list.SelectedItem = null;
        }
        _updatingPresetSelection = false;
        SelectPreset(result.Preset);
    }

    private void SelectPreset(CameraPreset preset)
    {
        _selectedPreset = preset;
        SetPresetFields(preset);
        RefreshLivePreview();
    }

    private void SelectSavedPreset()
    {
        CameraPreset preset = LoadSavedPreset() ?? CameraPresetCatalog.All[0];
        (ListBox list, int tabIndex) = preset.Category switch
        {
            CameraPresetCategory.DynamicShots => (DynamicPresetList, 1),
            CameraPresetCategory.ReactionShots => (ReactionPresetList, 2),
            _ => (StaticPresetList, 0)
        };
        PresetTabs.SelectedIndex = tabIndex;
        list.SelectedItem = preset;
        list.Dispatcher.BeginInvoke(() => list.ScrollIntoView(preset),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static string GetCategoryDisplay(CameraPresetCategory category) => category switch
    {
        CameraPresetCategory.StaticShots => "Static Shot",
        CameraPresetCategory.DynamicShots => "Dynamic Shot",
        CameraPresetCategory.ReactionShots => "Reaction Shot",
        _ => category.ToString()
    };

    private void SetPresetFields(CameraPreset preset)
    {
        ForwardDistanceTextBox.Text = Format(preset.ForwardDistance);
        SideOffsetTextBox.Text = Format(preset.SideOffset);
        HeightOffsetTextBox.Text = Format(preset.HeightOffset);
        LookAtHeightTextBox.Text = Format(preset.LookAtHeight);
        LocalRollTextBox.Text = Format(preset.LocalRoll);
        LocalPitchTextBox.Text = Format(preset.LocalPitch);
        LocalYawTextBox.Text = Format(preset.LocalYaw);
        DurationTextBox.Text = Format(preset.Duration);
        KeyCountTextBox.Text = CameraPresetGenerator.GetKeyCount(preset).ToString(CultureInfo.InvariantCulture);
        MovementAmountTextBox.Text = Format(preset.MovementAmount);
        bool isDynamic = preset.Category == CameraPresetCategory.DynamicShots;
        CameraSpeedSlider.IsEnabled = isDynamic;
        CameraSpeedTextBox.IsEnabled = isDynamic;
        SetCameraSpeed(1);
    }

    private void TogglePreviewButton_Checked(object sender, RoutedEventArgs e)
    {
        PreviewColumn.Width = new GridLength(520);
        PreviewPanel.Visibility = Visibility.Visible;
        Width = Math.Max(Width, 1360);
        RefreshLivePreview();
    }

    private void TogglePreviewButton_Unchecked(object sender, RoutedEventArgs e)
    {
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewColumn.Width = new GridLength(0);
        CameraPreviewControl.Visibility = Visibility.Collapsed;
        Width = 820;
    }

    private void PreviewParameter_TextChanged(object sender, TextChangedEventArgs e) => RefreshLivePreview();

    private void RefreshLivePreview()
    {
        if (TogglePreviewButton is not { IsChecked: true }
            || CameraPreviewControl is null
            || _selectedPreset is null
            || !TryReadOrigin(out CameraOrigin origin)
            || !TryCreateConfiguredPreset(out CameraPreset configured, out int sampleCount, out float pathFraction,
                out float distanceScale))
        {
            return;
        }

        CameraPreviewControl.Visibility = Visibility.Visible;
        CameraPreviewControl.SetPreview(_selectedPreset, origin,
            CameraPresetGenerator.Generate(configured, origin, sampleCount, pathFraction, distanceScale));
    }

    private bool TryCreateConfiguredPreset(out CameraPreset configured, out int sampleCount, out float pathFraction,
        out float distanceScale)
    {
        configured = null;
        sampleCount = 0;
        pathFraction = 1;
        distanceScale = 1;
        if (!TryReadOrigin(out CameraOrigin origin)
            || !TryReadFloat(ForwardDistanceTextBox, out float distance)
            || !TryReadFloat(DistanceScaleTextBox, out float distanceScalePercent)
            || !TryReadFloat(SideOffsetTextBox, out float side)
            || !TryReadFloat(HeightOffsetTextBox, out float height)
            || !TryReadFloat(LookAtHeightTextBox, out float lookHeight)
            || !TryReadFloat(LocalRollTextBox, out float roll)
            || !TryReadFloat(LocalPitchTextBox, out float pitch)
            || !TryReadFloat(LocalYawTextBox, out float yaw)
            || !TryReadFloat(DurationTextBox, out float duration)
            || !TryReadFloat(CameraSpeedTextBox, out float cameraSpeed)
            || !TryReadFloat(MovementAmountTextBox, out float movement)
            || duration < 0 || cameraSpeed <= 0 || distanceScalePercent <= 0)
        {
            return false;
        }

        var samplingPreset = _selectedPreset with
        {
            ForwardDistance = distance,
            SideOffset = side,
            HeightOffset = height,
            LookAtHeight = lookHeight,
            LocalYaw = yaw,
            LocalPitch = pitch,
            LocalRoll = roll,
            Duration = duration,
            MovementAmount = movement
        };
        sampleCount = CameraPresetGenerator.GetKeyCount(samplingPreset);
        float generatedDuration = samplingPreset.Category == CameraPresetCategory.DynamicShots
            ? duration / cameraSpeed
            : duration;
        configured = samplingPreset with { Duration = generatedDuration };
        distanceScale = distanceScalePercent / 100f;
        return true;
    }

    private void CameraSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingCameraSpeed || CameraSpeedTextBox is null)
        {
            return;
        }

        _updatingCameraSpeed = true;
        CameraSpeedTextBox.Text = e.NewValue.ToString("0.##", CultureInfo.InvariantCulture);
        CameraSpeedTextBox.CaretIndex = CameraSpeedTextBox.Text.Length;
        _updatingCameraSpeed = false;
        RefreshLivePreview();
    }

    private void CameraSpeedTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingCameraSpeed || CameraSpeedSlider is null
            || !double.TryParse(CameraSpeedTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed)
            || speed < CameraSpeedSlider.Minimum || speed > CameraSpeedSlider.Maximum)
        {
            return;
        }

        _updatingCameraSpeed = true;
        CameraSpeedSlider.Value = speed;
        _updatingCameraSpeed = false;
        RefreshLivePreview();
    }

    private void SetCameraSpeed(double speed)
    {
        _updatingCameraSpeed = true;
        CameraSpeedSlider.Value = speed;
        CameraSpeedTextBox.Text = speed.ToString("0.##", CultureInfo.InvariantCulture);
        _updatingCameraSpeed = false;
    }

    private void EnterManually_Click(object sender, RoutedEventArgs e)
    {
        OriginXTextBox.Focus();
        OriginXTextBox.SelectAll();
    }

    private void UseTrackKey_Click(object sender, RoutedEventArgs e)
    {
        if (_getTrackKeyOrigin?.Invoke() is { } origin)
        {
            SetOrigin(origin);
        }
    }

    private void UseOtherTrackKey_Click(object sender, RoutedEventArgs e)
    {
        if (_package is null)
        {
            return;
        }

        var picker = new TrackMoveOriginPicker(_package)
        {
            Owner = this
        };
        if (picker.ShowDialog() == true)
        {
            SetOrigin(picker.SelectedOrigin);
            StatusTextBlock.Text = "Origin loaded from the selected PCC TrackMove key.";
        }
    }

    private void UseViewportLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_getViewportOrigin?.Invoke() is { } origin)
        {
            CameraOrigin existing = TryReadOrigin(out var current) ? current : new CameraOrigin(Vector3.Zero, Vector3.Zero);
            SetOrigin(new CameraOrigin(origin.Location, existing.Rotation));
        }
    }

    private void UseViewportTransform_Click(object sender, RoutedEventArgs e)
    {
        if (_getViewportOrigin?.Invoke() is { } origin)
        {
            SetOrigin(origin);
        }
    }

    private void CopyOrigin_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadOrigin(out CameraOrigin origin))
        {
            ShowInvalidOrigin();
            return;
        }

        Clipboard.SetText(string.Join(", ", new[]
        {
            origin.Location.X, origin.Location.Y, origin.Location.Z,
            origin.Rotation.X, origin.Rotation.Y, origin.Rotation.Z
        }.Select(Format)));
        StatusTextBlock.Text = "Origin copied.";
    }

    private void PasteOrigin_Click(object sender, RoutedEventArgs e)
    {
        string text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        string[] values = text.Split(new[] { ',', ';', '\t', '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != 6 || values.Any(value => !float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            MessageBox.Show("Clipboard text must contain six numbers: X, Y, Z, Roll (X), Pitch (Y), Yaw (Z).", "Invalid Origin",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var parsed = values.Select(value => float.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        SetOrigin(new CameraOrigin(new Vector3(parsed[0], parsed[1], parsed[2]), new Vector3(parsed[3], parsed[4], parsed[5])));
    }

    private void ResetOrigin_Click(object sender, RoutedEventArgs e) => SetOrigin(new CameraOrigin(Vector3.Zero, Vector3.Zero));

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGenerate(out IReadOnlyList<GeneratedCameraKey> keys))
        {
            return;
        }

        _previewCamera?.Invoke(keys[keys.Count / 2]);
        StatusTextBlock.Text = $"Previewing {_selectedPreset.Name}.";
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGenerate(out IReadOnlyList<GeneratedCameraKey> keys))
        {
            return;
        }

        GeneratedKeys = keys;
        DialogResult = true;
        Close();
    }

    private bool TryGenerate(out IReadOnlyList<GeneratedCameraKey> keys)
    {
        keys = null;
        if (_selectedPreset is null)
        {
            MessageBox.Show("Select a camera preset.", "No Preset Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!TryReadOrigin(out CameraOrigin origin))
        {
            ShowInvalidOrigin();
            return false;
        }

        if (!TryReadFloat(ForwardDistanceTextBox, out float distance)
            || !TryReadFloat(DistanceScaleTextBox, out float distanceScalePercent)
            || !TryReadFloat(SideOffsetTextBox, out float side)
            || !TryReadFloat(HeightOffsetTextBox, out float height)
            || !TryReadFloat(LookAtHeightTextBox, out float lookHeight)
            || !TryReadFloat(LocalRollTextBox, out float roll)
            || !TryReadFloat(LocalPitchTextBox, out float pitch)
            || !TryReadFloat(LocalYawTextBox, out float yaw)
            || !TryReadFloat(DurationTextBox, out float duration)
            || !TryReadFloat(CameraSpeedTextBox, out float cameraSpeed)
            || !TryReadFloat(MovementAmountTextBox, out float movement)
            || !TryReadFloat(StartTimeTextBox, out float startTime)
            || duration < 0
            || distanceScalePercent <= 0
            || cameraSpeed < CameraSpeedSlider.Minimum
            || cameraSpeed > CameraSpeedSlider.Maximum)
        {
            MessageBox.Show($"All composition fields must contain valid numbers. Distance scale must be greater than 0%, duration cannot be negative, and movement speed must be between {CameraSpeedSlider.Minimum:0.##}x and {CameraSpeedSlider.Maximum:0.##}x.",
                "Invalid Preset Parameters", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var samplingPreset = _selectedPreset with
        {
            ForwardDistance = distance,
            SideOffset = side,
            HeightOffset = height,
            LookAtHeight = lookHeight,
            LocalYaw = yaw,
            LocalPitch = pitch,
            LocalRoll = roll,
            Duration = duration,
            MovementAmount = movement
        };
        int sampleCount = CameraPresetGenerator.GetKeyCount(samplingPreset);
        float distanceScale = distanceScalePercent / 100f;
        float pathFraction = 1f;
        float generatedDuration = duration;
        float movementRate = 0;
        if (samplingPreset.Category == CameraPresetCategory.DynamicShots)
        {
            float pathLength = CameraPresetGenerator.GetPathLength(samplingPreset, origin, distanceScale);
            if (duration <= float.Epsilon && pathLength > float.Epsilon)
            {
                MessageBox.Show("Dynamic camera movement duration must be greater than zero.",
                    "Invalid Preset Parameters", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            movementRate = pathLength <= float.Epsilon ? 0 : pathLength / duration * cameraSpeed;
            generatedDuration = movementRate <= float.Epsilon ? duration : pathLength / movementRate;
        }

        if (samplingPreset.Category == CameraPresetCategory.DynamicShots && _maximumEndTime is float maximumEndTime)
        {
            float remainingDuration = maximumEndTime - startTime;
            if (remainingDuration <= 0)
            {
                MessageBox.Show($"Dynamic shots must start before the InterpData length of {maximumEndTime:0.###} seconds.",
                    "No Timeline Time Available", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (generatedDuration > remainingDuration)
            {
                pathFraction = generatedDuration > float.Epsilon ? remainingDuration / generatedDuration : 1f;
                generatedDuration = remainingDuration;
                StatusTextBlock.Text = $"Timeline fits {pathFraction:P0} of the path at {movementRate:0.##} units/second; movement will stop at the InterpData end.";
            }
        }

        var configured = samplingPreset with { Duration = generatedDuration };
        KeyCountTextBox.Text = sampleCount.ToString(CultureInfo.InvariantCulture);
        GeneratedStartTime = startTime;
        keys = CameraPresetGenerator.Generate(configured, origin, sampleCount, pathFraction, distanceScale);
        return true;
    }

    private static ExportEntry FindOwningInterpData(ExportEntry export)
    {
        for (ExportEntry current = export?.Parent as ExportEntry; current is not null; current = current.Parent as ExportEntry)
        {
            if (current.ClassName == "InterpData")
            {
                return current;
            }
        }

        return null;
    }

    private bool TryReadOrigin(out CameraOrigin origin)
    {
        origin = default;
        if (!TryReadFloat(OriginXTextBox, out float x)
            || !TryReadFloat(OriginYTextBox, out float y)
            || !TryReadFloat(OriginZTextBox, out float z)
            || !TryReadFloat(OriginRollTextBox, out float roll)
            || !TryReadFloat(OriginPitchTextBox, out float pitch)
            || !TryReadFloat(OriginYawTextBox, out float yaw))
        {
            return false;
        }

        origin = new CameraOrigin(new Vector3(x, y, z), new Vector3(roll, pitch, yaw));
        return true;
    }

    private void SetOrigin(CameraOrigin origin)
    {
        OriginXTextBox.Text = Format(origin.Location.X);
        OriginYTextBox.Text = Format(origin.Location.Y);
        OriginZTextBox.Text = Format(origin.Location.Z);
        OriginRollTextBox.Text = Format(origin.Rotation.X);
        OriginPitchTextBox.Text = Format(origin.Rotation.Y);
        OriginYawTextBox.Text = Format(origin.Rotation.Z);
    }

    private static CameraOrigin LoadSavedOrigin()
    {
        try
        {
            if (File.Exists(SavedOriginPath) && TryParseOrigin(File.ReadAllText(SavedOriginPath), out CameraOrigin origin))
            {
                return origin;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new CameraOrigin(Vector3.Zero, Vector3.Zero);
    }

    private static CameraPreset LoadSavedPreset()
    {
        try
        {
            if (File.Exists(SavedPresetPath))
            {
                string[] values = File.ReadAllLines(SavedPresetPath);
                if (values.Length == 2
                    && Enum.TryParse(values[0], out CameraPresetCategory category))
                {
                    return CameraPresetCatalog.All.FirstOrDefault(preset =>
                        preset.Category == category && preset.Name == values[1]);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static void SaveOrigin(CameraOrigin origin)
    {
        try
        {
            File.WriteAllText(SavedOriginPath, string.Join(",", new[]
            {
                origin.Location.X, origin.Location.Y, origin.Location.Z,
                origin.Rotation.X, origin.Rotation.Y, origin.Rotation.Z
            }.Select(Format)));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void SavePreset(CameraPreset preset)
    {
        try
        {
            File.WriteAllLines(SavedPresetPath, new[] { preset.Category.ToString(), preset.Name });
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool TryParseOrigin(string text, out CameraOrigin origin)
    {
        origin = default;
        string[] values = text.Split(new[] { ',', ';', '\t', '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != 6 || values.Any(value => !float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        var parsed = values.Select(value => float.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        origin = new CameraOrigin(new Vector3(parsed[0], parsed[1], parsed[2]), new Vector3(parsed[3], parsed[4], parsed[5]));
        return true;
    }

    private static bool TryReadFloat(TextBox textBox, out float value) =>
        float.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void ShowInvalidOrigin() =>
        MessageBox.Show("Enter valid Origin X, Y, Z, Roll (X), Pitch (Y), and Yaw (Z) values before generating or previewing.",
            "Origin Required", MessageBoxButton.OK, MessageBoxImage.Warning);
}
