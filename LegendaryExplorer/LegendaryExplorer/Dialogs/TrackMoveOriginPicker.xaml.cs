using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.InterpEditor;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.Dialogs;

public partial class TrackMoveOriginPicker : Window
{
    private sealed record TrackItem(ExportEntry Export, string DisplayName, string ExportPath);

    private sealed record KeyItem(int KeyIndex, int KeyNumber, float Time, Vector3 Location, Vector3 Rotation)
    {
        public string TimeDisplay => $"{Time:0.###} seconds";
        public string LocationDisplay => FormattableString.Invariant($"Location: X={Location.X:0.###}, Y={Location.Y:0.###}, Z={Location.Z:0.###}");
        public string RotationDisplay => FormattableString.Invariant($"Rotation: Roll (X)={Rotation.X:0.###}, Pitch (Y)={Rotation.Y:0.###}, Yaw (Z)={Rotation.Z:0.###}");
    }

    private readonly List<TrackItem> _tracks;

    public CameraOrigin SelectedOrigin { get; private set; }

    public TrackMoveOriginPicker(IMEPackage package)
    {
        InitializeComponent();
        CustomWindowChrome.ApplyCustomChrome(this);

        _tracks = package?.Exports
            .Where(export => export.ClassName == "InterpTrackMove")
            .OrderBy(export => export.InstancedFullPath, StringComparer.OrdinalIgnoreCase)
            .Select(export => new TrackItem(export, $"#{export.UIndex} {export.ObjectName.Instanced}", export.InstancedFullPath))
            .ToList() ?? [];
        TrackListBox.ItemsSource = _tracks;
        StatusTextBlock.Text = _tracks.Count == 0
            ? "No InterpTrackMove exports were found in this PCC."
            : $"{_tracks.Count} InterpTrackMove export(s) found.";
        if (_tracks.Count > 0)
        {
            TrackListBox.SelectedIndex = 0;
        }
    }

    private void ExportSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TrackListBox is null || _tracks is null)
        {
            return;
        }

        string search = ExportSearchTextBox.Text.Trim().TrimStart('#');
        List<TrackItem> filteredTracks = search.Length == 0
            ? _tracks
            : _tracks.Where(track => track.Export.UIndex.ToString(CultureInfo.InvariantCulture).Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        TrackListBox.ItemsSource = filteredTracks;
        StatusTextBlock.Text = filteredTracks.Count == 0
            ? $"No InterpTrackMove export numbers match '{ExportSearchTextBox.Text.Trim()}'."
            : $"{filteredTracks.Count} matching InterpTrackMove export(s).";
        TrackListBox.SelectedIndex = filteredTracks.Count > 0 ? 0 : -1;
    }

    private void TrackListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UseKeyButton.IsEnabled = false;
        if (TrackListBox.SelectedItem is not TrackItem track)
        {
            KeyListBox.ItemsSource = null;
            return;
        }

        List<KeyItem> keys = GetKeys(track.Export);
        KeyListBox.ItemsSource = keys;
        StatusTextBlock.Text = keys.Count == 0
            ? $"{track.DisplayName} has no synchronized position and rotation keys."
            : $"{track.DisplayName} has {keys.Count} synchronized key(s).";
        if (keys.Count > 0)
        {
            KeyListBox.SelectedIndex = 0;
        }
    }

    private static List<KeyItem> GetKeys(ExportEntry export)
    {
        PropertyCollection properties = export.GetProperties();
        ArrayProperty<StructProperty> lookupPoints = properties.GetProp<StructProperty>("LookupTrack")?.GetProp<ArrayProperty<StructProperty>>("Points");
        ArrayProperty<StructProperty> positionPoints = properties.GetProp<StructProperty>("PosTrack")?.GetProp<ArrayProperty<StructProperty>>("Points");
        ArrayProperty<StructProperty> rotationPoints = properties.GetProp<StructProperty>("EulerTrack")?.GetProp<ArrayProperty<StructProperty>>("Points");
        int count = Math.Min(lookupPoints?.Count ?? 0, Math.Min(positionPoints?.Count ?? 0, rotationPoints?.Count ?? 0));
        var keys = new List<KeyItem>(count);
        for (int i = 0; i < count; i++)
        {
            FloatProperty timeProperty = lookupPoints[i].GetProp<FloatProperty>("Time");
            StructProperty positionProperty = positionPoints[i].GetProp<StructProperty>("OutVal");
            StructProperty rotationProperty = rotationPoints[i].GetProp<StructProperty>("OutVal");
            if (timeProperty is null || positionProperty is null || rotationProperty is null)
            {
                continue;
            }

            keys.Add(new KeyItem(i, i + 1, timeProperty.Value,
                CommonStructs.GetVector3(positionProperty), CommonStructs.GetVector3(rotationProperty)));
        }

        return keys;
    }

    private void KeyListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UseKeyButton.IsEnabled = KeyListBox.SelectedItem is KeyItem;

    private void KeyListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (KeyListBox.SelectedItem is KeyItem)
        {
            AcceptSelection();
        }
    }

    private void UseKeyButton_Click(object sender, RoutedEventArgs e) => AcceptSelection();

    private void AcceptSelection()
    {
        if (KeyListBox.SelectedItem is not KeyItem key)
        {
            return;
        }

        SelectedOrigin = new CameraOrigin(key.Location, key.Rotation);
        DialogResult = true;
    }
}
