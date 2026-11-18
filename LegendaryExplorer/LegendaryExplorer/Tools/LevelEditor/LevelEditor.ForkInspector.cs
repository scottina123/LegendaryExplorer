using System;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// Fork: the right column's inspector: the export ComboBox and the embedded Properties/Metadata tabs, the Actor Position/Rotation/DrawScale scrubbers and
/// rotation dial, and the Light group's scrubbers, cone dial and Other Levels To Affect list. Transform edits go through upstream's undo history
/// </summary>
public partial class LevelEditor
{
    /// <summary>Called by <see cref="SelectActor"/> when the selection changes</summary>
    private void OnForkSelectionChanged()
    {
        OnPropertyChanged(nameof(CanEditSelectedVolume));
        UpdateActiveFile();
        if (!_showSelectedLightWireframe) RenderContext.LightGizmo.Attach = null;
        if (selectedActor is { Export: not null })
        {
            RefreshPropertiesExportSelection(selectedActor, PropertiesTabControl?.SelectedIndex ?? 0);
        }
        else
        {
            UnloadPropertyTabs();
        }
        SynchronizeLevelLiveMaterialEditor(selectedActor);
        SynchronizeLevelMorphEditor(selectedActor);
        UpdateActorRotationDialIndicator();
        UpdateLightValueDialIndicator();
    }

    /// <summary>Called by <see cref="OnActorPropertyChanged"/> for the selected actor's every change</summary>
    private void OnForkActorPropertyChanged(string propertyName)
    {
        switch (propertyName)
        {
            case nameof(ActorProxy.LightRadius) or nameof(ActorProxy.InnerConeAngle) or nameof(ActorProxy.OuterConeAngle):
                UpdateLightValueDialIndicator();
                break;
            case nameof(ActorProxy.Rotation):
                UpdateActorRotationDialIndicator();
                if (!_isActorTransformScrubbing) RefreshSelectedPropertiesPreview();
                break;
            case nameof(ActorProxy.Location) or nameof(ActorProxy.DrawScale) or nameof(ActorProxy.DrawScale3D):
                if (!_isActorTransformScrubbing) RefreshSelectedPropertiesPreview();
                break;
        }
    }

    #region Properties / Metadata panel

    public ObservableCollectionExtended<ExportEntry> PropertiesExportList { get; } = [];

    private ExportEntry _selectedPropertiesExport;
    private IMEPackage _selectedPropertiesExportPackage;
    private int _selectedPropertiesExportUIndex;

    public ExportEntry SelectedPropertiesExport
    {
        get => _selectedPropertiesExport;
        set
        {
            if (SetProperty(ref _selectedPropertiesExport, value) && value is not null)
            {
                _selectedPropertiesExportPackage = value.FileRef;
                _selectedPropertiesExportUIndex = value.UIndex;
                LevelEditorInterpreter.LoadExport(value);
                LevelEditorMetadata.LoadExport(value);
            }
        }
    }

    private void LoadExportIntoTabs(ExportEntry export, int tabIndex)
    {
        if (export is null) return;
        PropertiesTabControl.SelectedIndex = tabIndex;
        SelectedPropertiesExport = export;
    }

    /// <summary>The interpreter shows the actor with its uncommitted transform</summary>
    private void RefreshSelectedPropertiesPreview()
    {
        if (SelectedActor?.Export is not { } actorExport || SelectedPropertiesExport is null) return;
        if (SelectedPropertiesExport.FileRef != actorExport.FileRef || SelectedPropertiesExport.UIndex != actorExport.UIndex) return;

        LevelEditorInterpreter.LoadExport(actorExport, SelectedActor.GetPropertiesForInterpreter());
    }

    private void RefreshPropertiesExportSelection(ActorProxy actor, int tabIndex)
    {
        PropertiesExportList.Clear();
        PropertiesExportList.AddRange(actor.GetPropertyExports());

        ExportEntry exportToLoad = PropertiesExportList.FirstOrDefault(x => x.FileRef == _selectedPropertiesExportPackage && x.UIndex == _selectedPropertiesExportUIndex)
                                   ?? actor.Export;
        LoadExportIntoTabs(exportToLoad, tabIndex);
    }

    private void UnloadPropertyTabs()
    {
        if (_selectedPropertiesExport is null && PropertiesExportList.Count == 0) return;
        PropertiesExportList.Clear();
        _selectedPropertiesExport = null;
        OnPropertyChanged(nameof(SelectedPropertiesExport));
        LevelEditorInterpreter?.UnloadExport();
        LevelEditorMetadata?.UnloadExport();
    }

    public ICommand ViewActorPropertiesCommand { get; private set; }
    public ICommand ViewActorMetadataCommand { get; private set; }

    #endregion

    #region Transform scrubbers and rotation dial

    private char _actorLocationScrubAxis = 'X';
    private double _actorLocationScrubAccumulator;
    private double _actorLocationScrubPreviousHorizontalChange;
    private string _actorScaleScrubAxes = "X";
    private double _actorScaleScrubAccumulator;
    private double _actorScaleScrubPreviousHorizontalChange;
    private string _actorRotationDialAxis = nameof(ActorProxy.PitchDegrees);
    private bool _actorRotationDialDragging;
    private double _actorRotationDialAngleAccumulator;
    private double _actorRotationDialPreviousAngle;
    private bool _isActorTransformScrubbing;
    private ActorProxy _actorTransformScrubActor;
    private TransformSnapshot? _actorTransformScrubBefore;

    private bool CanScrubSelectedActor() => CanMutateScene && SelectedActor is { IsReadOnly: false, CanEditTransform: true };

    private void ActorLocationScrubAxis_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string { Length: 1 } axis })
        {
            _actorLocationScrubAxis = axis[0];
        }
    }

    private void ActorLocationScrubThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (!CanScrubSelectedActor())
        {
            e.Handled = true;
            return;
        }

        _actorLocationScrubAccumulator = 0;
        _actorLocationScrubPreviousHorizontalChange = 0;
        BeginActorTransformScrub();
    }

    private void ActorLocationScrubThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_isActorTransformScrubbing || SelectedActor != _actorTransformScrubActor || !double.IsFinite(e.HorizontalChange)) return;

        double horizontalChange = e.HorizontalChange - _actorLocationScrubPreviousHorizontalChange;
        _actorLocationScrubPreviousHorizontalChange = e.HorizontalChange;
        _actorLocationScrubAccumulator += horizontalChange;
        double dragStep = SystemParameters.MinimumHorizontalDragDistance;
        int stepCount = (int)(_actorLocationScrubAccumulator / dragStep);
        if (stepCount == 0) return;

        _actorLocationScrubAccumulator -= stepCount * dragStep;
        float delta = stepCount * PosIncrement;
        Vector3 location = SelectedActor.Location;
        switch (_actorLocationScrubAxis)
        {
            case 'X': location.X += delta; break;
            case 'Y': location.Y += delta; break;
            case 'Z': location.Z += delta; break;
        }

        SelectedActor.Location = location;
    }

    private void ActorLocationScrubThumb_DragCompleted(object sender, DragCompletedEventArgs e) => EndActorTransformScrub();

    private void ActorScaleScrubAxis_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string axes })
        {
            _actorScaleScrubAxes = axes;
        }
    }

    private void ActorScaleScrubThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (!CanScrubSelectedActor())
        {
            e.Handled = true;
            return;
        }

        _actorScaleScrubAccumulator = 0;
        _actorScaleScrubPreviousHorizontalChange = 0;
        BeginActorTransformScrub();
    }

    private void ActorScaleScrubThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_isActorTransformScrubbing || SelectedActor != _actorTransformScrubActor || !double.IsFinite(e.HorizontalChange)) return;

        double horizontalChange = e.HorizontalChange - _actorScaleScrubPreviousHorizontalChange;
        _actorScaleScrubPreviousHorizontalChange = e.HorizontalChange;
        _actorScaleScrubAccumulator += horizontalChange;
        double dragStep = SystemParameters.MinimumHorizontalDragDistance;
        int stepCount = (int)(_actorScaleScrubAccumulator / dragStep);
        if (stepCount == 0) return;

        _actorScaleScrubAccumulator -= stepCount * dragStep;
        float delta = stepCount * ScaleIncrement;
        Vector3 scale = SelectedActor.DrawScale3D;
        if (_actorScaleScrubAxes is "X" or "All") scale.X += delta;
        if (_actorScaleScrubAxes is "Y" or "All") scale.Y += delta;
        if (_actorScaleScrubAxes is "Z" or "All") scale.Z += delta;
        SelectedActor.DrawScale3D = scale;
    }

    private void ActorScaleScrubThumb_DragCompleted(object sender, DragCompletedEventArgs e) => EndActorTransformScrub();

    private void ActorRotationDialAxis_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string axis })
        {
            _actorRotationDialAxis = axis switch
            {
                "Pitch" => nameof(ActorProxy.PitchDegrees),
                "Roll" => nameof(ActorProxy.RollDegrees),
                "Yaw" => nameof(ActorProxy.YawDegrees),
                _ => _actorRotationDialAxis
            };
            UpdateActorRotationDialIndicator();
        }
    }

    private void ActorRotationDial_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanScrubSelectedActor() || RotIncrement <= 0) return;

        _actorRotationDialPreviousAngle = GetActorRotationDialPointerAngle(e.GetPosition(ActorRotationDial));
        _actorRotationDialAngleAccumulator = 0;
        _actorRotationDialDragging = ActorRotationDial.CaptureMouse();
        if (_actorRotationDialDragging)
        {
            BeginActorTransformScrub();
        }
        e.Handled = true;
    }

    private void ActorRotationDial_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_actorRotationDialDragging || SelectedActor is null || SelectedActor != _actorTransformScrubActor || e.LeftButton != MouseButtonState.Pressed) return;

        double pointerAngle = GetActorRotationDialPointerAngle(e.GetPosition(ActorRotationDial));
        double angleDelta = NormalizeActorRotationDialAngle(pointerAngle - _actorRotationDialPreviousAngle);
        _actorRotationDialPreviousAngle = pointerAngle;
        _actorRotationDialAngleAccumulator += angleDelta;

        int stepCount = (int)(_actorRotationDialAngleAccumulator / RotIncrement);
        if (stepCount == 0) return;

        _actorRotationDialAngleAccumulator -= stepCount * RotIncrement;
        float delta = stepCount * RotIncrement;
        switch (_actorRotationDialAxis)
        {
            case nameof(ActorProxy.PitchDegrees): SelectedActor.PitchDegrees += delta; break;
            case nameof(ActorProxy.RollDegrees): SelectedActor.RollDegrees += delta; break;
            case nameof(ActorProxy.YawDegrees): SelectedActor.YawDegrees += delta; break;
        }

        UpdateActorRotationDialIndicator();
        e.Handled = true;
    }

    private void ActorRotationDial_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_actorRotationDialDragging) return;

        _actorRotationDialDragging = false;
        EndActorTransformScrub();
        ActorRotationDial.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ActorRotationDial_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _actorRotationDialDragging = false;
        EndActorTransformScrub();
    }

    private void BeginActorTransformScrub()
    {
        _actorTransformScrubActor = SelectedActor;
        _actorTransformScrubBefore = SelectedActor?.SnapshotTransform();
        _isActorTransformScrubbing = _actorTransformScrubActor is not null;
    }

    /// <summary>A scrub or dial drag is one undo step</summary>
    private void EndActorTransformScrub()
    {
        if (!_isActorTransformScrubbing || _actorTransformScrubActor is null || _actorTransformScrubBefore is not { } before)
        {
            _isActorTransformScrubbing = false;
            return;
        }

        TransformSnapshot after = _actorTransformScrubActor.SnapshotTransform();
        if (!before.Equals(after) && !_actorTransformScrubActor.IsDisposed)
        {
            UndoHistory.Push(new TransformAction(_actorTransformScrubActor, before, after, $"Drag {_actorTransformScrubActor.ActorName}"));
            if (_actorTransformScrubActor == selectedActor) _preEditSnapshot = after;
        }

        _isActorTransformScrubbing = false;
        _actorTransformScrubActor = null;
        _actorTransformScrubBefore = null;
        LightDiagnosticsStale = true;
        RefreshSelectedPropertiesPreview();
        UpdateActorRotationDialIndicator();
    }

    private void UpdateActorRotationDialIndicator()
    {
        if (ActorRotationDialIndicator?.RenderTransform is not System.Windows.Media.RotateTransform indicatorTransform) return;

        indicatorTransform.Angle = _actorRotationDialAxis switch
        {
            nameof(ActorProxy.PitchDegrees) => SelectedActor?.PitchDegrees ?? 0,
            nameof(ActorProxy.RollDegrees) => SelectedActor?.RollDegrees ?? 0,
            nameof(ActorProxy.YawDegrees) => SelectedActor?.YawDegrees ?? 0,
            _ => 0
        };
    }

    private static double GetActorRotationDialPointerAngle(Point pointerPosition)
        => Math.Atan2(pointerPosition.Y - 60d, pointerPosition.X - 60d) * 180d / Math.PI + 90d;

    private static double NormalizeActorRotationDialAngle(double angle)
    {
        while (angle > 180d) angle -= 360d;
        while (angle < -180d) angle += 360d;
        return angle;
    }

    #endregion

    #region Light group (scrubbers, cone dial, other levels)

    private double _lightRadiusScrubAccumulator;
    private double _lightRadiusScrubPreviousHorizontalChange;
    private double _lightBrightnessScrubAccumulator;
    private double _lightBrightnessScrubPreviousHorizontalChange;
    private string _lightDialProperty = nameof(ActorProxy.InnerConeAngle);
    private bool _lightValueDialDragging;
    private double _lightValueDialAccumulator;
    private double _lightValueDialPreviousAngle;

    private void LightDialProperty_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string propertyName })
        {
            _lightDialProperty = propertyName;
            UpdateLightValueDialIndicator();
        }
    }

    private void LightRadiusScrubThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (SelectedActor is null || SelectedActor.IsReadOnly || !SelectedActor.HasLightRadius)
        {
            e.Handled = true;
            return;
        }

        _lightRadiusScrubAccumulator = 0;
        _lightRadiusScrubPreviousHorizontalChange = 0;
    }

    private void LightRadiusScrubThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (SelectedActor is null || SelectedActor.IsReadOnly || !double.IsFinite(e.HorizontalChange)) return;

        double horizontalChange = e.HorizontalChange - _lightRadiusScrubPreviousHorizontalChange;
        _lightRadiusScrubPreviousHorizontalChange = e.HorizontalChange;
        _lightRadiusScrubAccumulator += horizontalChange;
        double dragStep = SystemParameters.MinimumHorizontalDragDistance;
        int stepCount = (int)(_lightRadiusScrubAccumulator / dragStep);
        if (stepCount == 0) return;

        _lightRadiusScrubAccumulator -= stepCount * dragStep;
        // Through the light's EditableLight: upstream records the edit (merged into one undo step) and refreshes the preview
        SelectedActor.LightRadius = MathF.Max(0, SelectedActor.LightRadius + stepCount * 16f);
    }

    private void LightRadiusScrubThumb_DragCompleted(object sender, DragCompletedEventArgs e) => LightDiagnosticsStale = true;

    private void LightBrightnessScrubThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (SelectedActor is null || SelectedActor.IsReadOnly || !SelectedActor.HasLightSettings)
        {
            e.Handled = true;
            return;
        }

        _lightBrightnessScrubAccumulator = 0;
        _lightBrightnessScrubPreviousHorizontalChange = 0;
    }

    private void LightBrightnessScrubThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (SelectedActor is null || SelectedActor.IsReadOnly || !SelectedActor.HasLightSettings || !double.IsFinite(e.HorizontalChange)) return;

        double horizontalChange = e.HorizontalChange - _lightBrightnessScrubPreviousHorizontalChange;
        _lightBrightnessScrubPreviousHorizontalChange = e.HorizontalChange;
        _lightBrightnessScrubAccumulator += horizontalChange;
        double dragStep = SystemParameters.MinimumHorizontalDragDistance;
        int stepCount = (int)(_lightBrightnessScrubAccumulator / dragStep);
        if (stepCount == 0) return;

        _lightBrightnessScrubAccumulator -= stepCount * dragStep;
        SelectedActor.Brightness = MathF.Max(0, SelectedActor.Brightness + stepCount * 0.25f);
    }

    private void LightBrightnessScrubThumb_DragCompleted(object sender, DragCompletedEventArgs e) => LightDiagnosticsStale = true;

    private void LightValueDial_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (SelectedActor is null || SelectedActor.IsReadOnly || !SelectedActor.HasConeAngles) return;

        _lightValueDialPreviousAngle = GetActorRotationDialPointerAngle(e.GetPosition(LightValueDial));
        _lightValueDialAccumulator = 0;
        _lightValueDialDragging = LightValueDial.CaptureMouse();
        e.Handled = true;
    }

    private void LightValueDial_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_lightValueDialDragging || SelectedActor is null || e.LeftButton != MouseButtonState.Pressed) return;

        double pointerAngle = GetActorRotationDialPointerAngle(e.GetPosition(LightValueDial));
        double angleDelta = NormalizeActorRotationDialAngle(pointerAngle - _lightValueDialPreviousAngle);
        _lightValueDialPreviousAngle = pointerAngle;
        _lightValueDialAccumulator += angleDelta;

        const float increment = 1f;
        int stepCount = (int)(_lightValueDialAccumulator / increment);
        if (stepCount == 0) return;

        _lightValueDialAccumulator -= stepCount * increment;
        float delta = stepCount * increment;
        switch (_lightDialProperty)
        {
            case nameof(ActorProxy.InnerConeAngle):
                SelectedActor.InnerConeAngle = Math.Clamp(SelectedActor.InnerConeAngle + delta, 0, 89.9f);
                break;
            case nameof(ActorProxy.OuterConeAngle):
                SelectedActor.OuterConeAngle = Math.Clamp(SelectedActor.OuterConeAngle + delta, 0, 89.9f);
                break;
            case "ConeAngles":
                SelectedActor.InnerConeAngle = Math.Clamp(SelectedActor.InnerConeAngle + delta, 0, 89.9f);
                SelectedActor.OuterConeAngle = Math.Clamp(SelectedActor.OuterConeAngle + delta, 0, 89.9f);
                break;
        }

        UpdateLightValueDialIndicator();
        e.Handled = true;
    }

    private void LightValueDial_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_lightValueDialDragging) return;

        _lightValueDialDragging = false;
        LightValueDial.ReleaseMouseCapture();
        LightDiagnosticsStale = true;
        e.Handled = true;
    }

    private void LightValueDial_LostMouseCapture(object sender, MouseEventArgs e) => _lightValueDialDragging = false;

    private void UpdateLightValueDialIndicator()
    {
        if (LightValueDialIndicator?.RenderTransform is not System.Windows.Media.RotateTransform indicatorTransform) return;

        indicatorTransform.Angle = _lightDialProperty switch
        {
            nameof(ActorProxy.InnerConeAngle) => SelectedActor?.InnerConeAngle ?? 0,
            nameof(ActorProxy.OuterConeAngle) => SelectedActor?.OuterConeAngle ?? 0,
            "ConeAngles" when SelectedActor is not null => (SelectedActor.InnerConeAngle + SelectedActor.OuterConeAngle) / 2f,
            _ => 0
        };
    }

    private void AddOtherLevel_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedActor is null) return;
        string levelName = NewOtherLevel_TextBox.Text?.Trim();
        if (string.IsNullOrEmpty(levelName)) return;
        SelectedActor.AddOtherLevelToAffect(levelName);
        NewOtherLevel_TextBox.Text = "";
    }

    private void RemoveOtherLevel_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedActor is null || OtherLevelsListBox.SelectedItem is not string selectedLevel) return;
        SelectedActor.RemoveOtherLevelFromAffect(selectedLevel);
    }

    #endregion
}
