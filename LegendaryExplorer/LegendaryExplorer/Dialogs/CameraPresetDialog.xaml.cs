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
    private static string SavedOriginPath => Path.Combine(AppDirectories.AppDataFolder, "CameraPresetOriginV2.txt");

    private readonly Func<CameraOrigin?> _getTrackKeyOrigin;
    private readonly Func<CameraOrigin?> _getViewportOrigin;
    private readonly Action<GeneratedCameraKey> _previewCamera;
    private readonly float? _maximumEndTime;
    private CameraPreset _selectedPreset;

    public IReadOnlyList<GeneratedCameraKey> GeneratedKeys { get; private set; }
    public float GeneratedStartTime { get; private set; }

    public CameraPresetDialog(Func<CameraOrigin?> getTrackKeyOrigin, Func<CameraOrigin?> getViewportOrigin,
        Action<GeneratedCameraKey> previewCamera, float initialStartTime = 0, float? maximumEndTime = null)
    {
        _getTrackKeyOrigin = getTrackKeyOrigin;
        _getViewportOrigin = getViewportOrigin;
        _previewCamera = previewCamera;
        _maximumEndTime = maximumEndTime;

        InitializeComponent();
        CustomWindowChrome.ApplyCustomChrome(this);

        StaticPresetList.ItemsSource = CameraPresetCatalog.GetByCategory(CameraPresetCategory.StaticShots);
        DynamicPresetList.ItemsSource = CameraPresetCatalog.GetByCategory(CameraPresetCategory.DynamicShots);
        ReactionPresetList.ItemsSource = CameraPresetCatalog.GetByCategory(CameraPresetCategory.ReactionShots);
        UseTrackKeyButton.IsEnabled = _getTrackKeyOrigin?.Invoke() is not null;
        bool hasViewport = _getViewportOrigin?.Invoke() is not null;
        UseViewportLocationButton.IsEnabled = hasViewport;
        UseViewportTransformButton.IsEnabled = hasViewport;
        PreviewButton.IsEnabled = _previewCamera is not null && hasViewport;
        StatusTextBlock.Text = hasViewport ? "Preview uses the connected Level Editor viewport." : "Connect a Level Editor to enable viewport actions and preview.";
        StartTimeTextBox.Text = Format(initialStartTime);
        SetOrigin(LoadSavedOrigin());
        StaticPresetList.SelectedIndex = 0;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (TryReadOrigin(out CameraOrigin origin))
        {
            SaveOrigin(origin);
        }

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
            maximumEndTime)
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
        if (sender is not ListBox { SelectedItem: CameraPreset preset })
        {
            return;
        }

        foreach (ListBox list in new[] { StaticPresetList, DynamicPresetList, ReactionPresetList })
        {
            if (list != sender)
            {
                list.SelectedItem = null;
            }
        }

        _selectedPreset = preset;
        SetPresetFields(preset);
    }

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
            || !TryReadFloat(SideOffsetTextBox, out float side)
            || !TryReadFloat(HeightOffsetTextBox, out float height)
            || !TryReadFloat(LookAtHeightTextBox, out float lookHeight)
            || !TryReadFloat(LocalRollTextBox, out float roll)
            || !TryReadFloat(LocalPitchTextBox, out float pitch)
            || !TryReadFloat(LocalYawTextBox, out float yaw)
            || !TryReadFloat(DurationTextBox, out float duration)
            || !TryReadFloat(MovementAmountTextBox, out float movement)
            || !TryReadFloat(StartTimeTextBox, out float startTime)
            || duration < 0)
        {
            MessageBox.Show("All composition fields must contain valid numbers. Duration cannot be negative.",
                "Invalid Preset Parameters", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var configured = _selectedPreset with
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

        if (configured.Category == CameraPresetCategory.DynamicShots && _maximumEndTime is float maximumEndTime)
        {
            float remainingDuration = maximumEndTime - startTime;
            if (remainingDuration <= 0)
            {
                MessageBox.Show($"Dynamic shots must start before the InterpData length of {maximumEndTime:0.###} seconds.",
                    "No Timeline Time Available", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (configured.Duration > remainingDuration)
            {
                configured = configured with { Duration = remainingDuration };
                DurationTextBox.Text = Format(remainingDuration);
                StatusTextBlock.Text = $"Duration limited to {remainingDuration:0.###} seconds to fit the InterpData length.";
            }
        }

        KeyCountTextBox.Text = CameraPresetGenerator.GetKeyCount(configured).ToString(CultureInfo.InvariantCulture);
        GeneratedStartTime = startTime;
        keys = CameraPresetGenerator.Generate(configured, origin);
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
