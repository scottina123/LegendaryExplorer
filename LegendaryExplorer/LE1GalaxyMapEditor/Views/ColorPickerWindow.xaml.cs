using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LE1GalaxyMapEditor.Infrastructure;

namespace LE1GalaxyMapEditor.Views;

public partial class ColorPickerWindow : Window
{
    public ColorPickerWindow(string initialValue)
    {
        global::LE1GalaxyMapEditor.Theming.EditorThemeManager.Initialize(this);
        InitializeComponent();
        var packed = long.TryParse(initialValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? unchecked((uint)value) : uint.MaxValue;
        SetColor(UnpackArgb(packed));
    }
    public string? Result { get; private set; }
    public event Action<string>? PreviewColorChanged;
    public static uint PackArgb(byte a, byte r, byte g, byte b) => ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    public static Color UnpackArgb(uint value) => Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    public static string SignedDecimal(uint value) => unchecked((int)value).ToString(CultureInfo.InvariantCulture);
    private void Wheel_OnColorChanged(object? sender, EventArgs e) => SetColor(Wheel.SelectedColor);
    private void SetColor(Color color)
    {
        Wheel.SelectedColor = color;
        Preview.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        HexBox.Text = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        ABox.Text = color.A.ToString(); RBox.Text = color.R.ToString(); GBox.Text = color.G.ToString(); BBox.Text = color.B.ToString();
        var packed = PackArgb(color.A, color.R, color.G, color.B); PackedText.Text = $"Packed value: {SignedDecimal(packed)}  (unsigned {packed})"; ErrorText.Text = "";
        PreviewColorChanged?.Invoke(SignedDecimal(packed));
    }
    private void ApplyFields_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var hex = HexBox.Text.Trim().TrimStart('#'); Color color;
            if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed)) color = UnpackArgb(packed);
            else if (byte.TryParse(ABox.Text, out var a) && byte.TryParse(RBox.Text, out var r) && byte.TryParse(GBox.Text, out var g) && byte.TryParse(BBox.Text, out var b)) color = Color.FromArgb(a, r, g, b);
            else throw new FormatException();
            SetColor(color);
        }
        catch (FormatException) { ErrorText.Text = "Enter #AARRGGBB or four values from 0 to 255."; }
    }
    private void Use_OnClick(object sender, RoutedEventArgs e) { var c = Wheel.SelectedColor; Result = SignedDecimal(PackArgb(c.A, c.R, c.G, c.B)); DialogResult = true; }
}
