using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LE1GalaxyMapEditor.Infrastructure;

namespace LE1GalaxyMapEditor.Views;

public partial class HdrColorPickerWindow : Window
{
    private bool _updating;

    public HdrColorPickerWindow(Vector4 initial)
    {
        global::LE1GalaxyMapEditor.Theming.EditorThemeManager.Initialize(this);
        InitializeComponent();
        var intensity = Math.Max(initial.X, Math.Max(initial.Y, initial.Z));
        IntensitySlider.Maximum = Math.Max(10, Math.Ceiling(intensity + 1));
        SetFromVector(initial);
    }

    public Vector4? SelectedColor { get; private set; }
    public event Action<Vector4>? PreviewColorChanged;

    private void Wheel_OnColorChanged(object? sender, EventArgs eventArgs)
    {
        if (!_updating) UpdateFromWheel();
    }

    private void IntensitySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        if (_updating) return;
        _updating = true;
        IntensityBox.Text = Format((float)IntensitySlider.Value);
        _updating = false;
        UpdateFromWheel();
    }

    private void IntensityBox_OnTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        if (_updating || !TryParse(IntensityBox, out var intensity)) return;
        _updating = true;
        IntensitySlider.Value = Math.Clamp(intensity, (float)IntensitySlider.Minimum, (float)IntensitySlider.Maximum);
        _updating = false;
        UpdateFromWheel();
    }

    private void Components_OnTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        if (_updating) return;
        if (!TryReadComponents(out var color))
        {
            ErrorText.Text = "Enter four finite RGBA values using a dot as the decimal separator.";
            return;
        }

        ErrorText.Text = string.Empty;
        var intensity = Math.Max(color.X, Math.Max(color.Y, color.Z));
        var divisor = intensity > 0 ? intensity : 1;
        _updating = true;
        Wheel.SelectedColor = Color.FromRgb(ToByte(color.X / divisor), ToByte(color.Y / divisor), ToByte(color.Z / divisor));
        IntensityBox.Text = Format(intensity);
        IntensitySlider.Value = Math.Clamp(intensity, (float)IntensitySlider.Minimum, (float)IntensitySlider.Maximum);
        _updating = false;
        UpdatePreview(color);
    }

    private void Use_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryReadComponents(out var color))
        {
            ErrorText.Text = "Enter four finite RGBA values using a dot as the decimal separator.";
            return;
        }

        SelectedColor = color;
        DialogResult = true;
    }

    private void UpdateFromWheel()
    {
        if (!TryParse(IntensityBox, out var intensity)) return;
        var selected = Wheel.SelectedColor;
        var alpha = TryParse(ABox, out var currentAlpha) ? currentAlpha : 0;
        var color = new Vector4(
            selected.R / 255f * intensity,
            selected.G / 255f * intensity,
            selected.B / 255f * intensity,
            alpha);
        _updating = true;
        RBox.Text = Format(color.X);
        GBox.Text = Format(color.Y);
        BBox.Text = Format(color.Z);
        _updating = false;
        UpdatePreview(color);
    }

    private void SetFromVector(Vector4 color)
    {
        var intensity = Math.Max(color.X, Math.Max(color.Y, color.Z));
        var divisor = intensity > 0 ? intensity : 1;
        _updating = true;
        Wheel.SelectedColor = Color.FromRgb(ToByte(color.X / divisor), ToByte(color.Y / divisor), ToByte(color.Z / divisor));
        IntensityBox.Text = Format(intensity);
        IntensitySlider.Value = Math.Clamp(intensity, (float)IntensitySlider.Minimum, (float)IntensitySlider.Maximum);
        RBox.Text = Format(color.X);
        GBox.Text = Format(color.Y);
        BBox.Text = Format(color.Z);
        ABox.Text = Format(color.W);
        _updating = false;
        UpdatePreview(color);
    }

    private bool TryReadComponents(out Vector4 color)
    {
        color = default;
        if (!TryParse(RBox, out var red) || !TryParse(GBox, out var green) ||
            !TryParse(BBox, out var blue) || !TryParse(ABox, out var alpha)) return false;
        color = new Vector4(red, green, blue, alpha);
        return true;
    }

    private void UpdatePreview(Vector4 color)
    {
        var maximum = Math.Max(color.X, Math.Max(color.Y, color.Z));
        var divisor = maximum > 1 ? maximum : 1;
        Preview.Background = new SolidColorBrush(Color.FromRgb(
            ToByte(color.X / divisor), ToByte(color.Y / divisor), ToByte(color.Z / divisor)));
        PreviewColorChanged?.Invoke(color);
    }

    private static bool TryParse(TextBox box, out float value) =>
        float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    private static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static byte ToByte(float value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
