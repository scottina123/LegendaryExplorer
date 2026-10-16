using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Scaleform;
using LegendaryExplorerCore.Unreal.Classes;
using Microsoft.Win32;

namespace LegendaryExplorer.UserControls.ExportLoaderControls.Scaleform;

/// <summary>Simple element palette. Texture pixels are decoded only after an explicit selection.</summary>
public partial class ScaleformElementCreator : UserControl
{
    private ExportEntry export;
    private List<TextureChoice> textures;
    private BitmapSource chosenImage;
    private Point dragStart;
    private int requestVersion;
    private bool ready;
    public event Action<ScaleformElementSpec> ElementRequested;

    public ScaleformElementCreator() { InitializeComponent(); ready = true; Loaded += (_, _) => EnsureTextures(); }

    public void LoadMovie(ExportEntry movie, ScaleformDocument document)
    {
        requestVersion++; export = movie; textures = null; chosenImage = null;
        TextureList.ItemsSource = null; ImagePreview.Source = null; ImageInfo.Text = ""; Feedback.Text = "";
        FontBox.ItemsSource = document?.GetElementFonts().Select(f => new FontChoice(f.Id, f.Name)).ToList();
        FontBox.SelectedIndex = FontBox.Items.Count > 0 ? 0 : -1;
        FontHelp.Text = FontBox.Items.Count == 0 ? "This movie has no reusable font. Add an image or panel, or use a movie with an embedded/shared font." : "Reuses this movie's font in game.";
        if (IsVisible) EnsureTextures();
    }

    private void EnsureTextures()
    {
        if (textures != null || export == null) return;
        textures = export.FileRef.Exports.Where(e => e.ClassName == "Texture2D" && !e.IsDefaultObject)
            .Select(e => new TextureChoice(e, e.InstancedFullPath)).OrderBy(e => e.Label).ToList();
        FilterTextures();
    }
    private void FilterTextures()
    {
        if (TextureList == null) return;
        string query = TextureSearch.Text.Trim();
        TextureList.ItemsSource = textures?.Where(t => t.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }
    private void TextureSearch_Changed(object sender, TextChangedEventArgs e) { if (ready) { EnsureTextures(); FilterTextures(); } }
    private async void Texture_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (TextureList.SelectedItem is not TextureChoice choice) return;
        int version = ++requestVersion; chosenImage = null; ImagePreview.Source = null; ImageInfo.Text = "Loading texture…";
        try
        {
            // Package texture conversion can decompress a large mip. Keep it off the UI thread.
            var bitmap = await Task.Run(() =>
            {
                using var stream = new MemoryStream(); new Texture2D(choice.Export).ExportToPNG(outStream: stream); stream.Position = 0;
                var image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]; image.Freeze(); return image;
            });
            if (version != requestVersion) return;
            ChooseImage(bitmap, choice.Export.ObjectName.Name);
        }
        catch (Exception ex) { if (version == requestVersion) ImageInfo.Text = "Could not load this texture: " + ex.Message; }
    }
    private void ChooseImage(BitmapSource image, string name)
    {
        if ((long)image.PixelWidth * image.PixelHeight > 16_777_216) throw new InvalidDataException("Choose an image up to 16 megapixels.");
        chosenImage = image; ImagePreview.Source = image;
        WidthBox.Text = image.PixelWidth.ToString(CultureInfo.InvariantCulture); HeightBox.Text = image.PixelHeight.ToString(CultureInfo.InvariantCulture);
        NameBox.Text = SafeName(name); ImageInfo.Text = $"{image.PixelWidth} × {image.PixelHeight} · Embedded copy of the image";
    }
    private void ImageFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp", Title = "Choose UI artwork" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            requestVersion++; using var stream = File.OpenRead(dialog.FileName);
            var image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]; image.Freeze();
            ChooseImage(image, Path.GetFileNameWithoutExtension(dialog.FileName));
        }
        catch (Exception ex) { ImageInfo.Text = "Could not use this image: " + ex.Message; }
    }
    private void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        var kind = (ScaleformElementKind)KindBox.SelectedIndex;
        TextOptions.Visibility = kind is ScaleformElementKind.Text or ScaleformElementKind.Button ? Visibility.Visible : Visibility.Collapsed;
        ImageOptions.Visibility = kind == ScaleformElementKind.Image ? Visibility.Visible : Visibility.Collapsed;
        ColorPanel.Visibility = kind == ScaleformElementKind.Image ? Visibility.Collapsed : Visibility.Visible;
        NameBox.Text = "new" + kind; Feedback.Text = "";
        if (kind == ScaleformElementKind.Text) { ElementColor.SelectedColor = Colors.White; HeightBox.Text = "40"; }
        if (kind == ScaleformElementKind.Button) { TextBox.Text = "Button"; HeightBox.Text = "48"; }
        if (kind == ScaleformElementKind.Image) EnsureTextures();
    }
    public ScaleformElementSpec CreateSpec()
    {
        var kind = (ScaleformElementKind)KindBox.SelectedIndex;
        Color selectedColor = ElementColor.SelectedColor ?? Colors.White;
        uint color = (uint)selectedColor.R << 24 | (uint)selectedColor.G << 16 | (uint)selectedColor.B << 8 | selectedColor.A;
        var spec = new ScaleformElementSpec { Kind = kind, Name = NameBox.Text.Trim(), Width = Number(WidthBox.Text), Height = Number(HeightBox.Text), Color = color,
            Text = TextBox.Text, FontId = (FontBox.SelectedItem as FontChoice)?.Id ?? 0, FontSize = Number(FontSizeBox.Text) };
        if (kind == ScaleformElementKind.Text && spec.FontId == 0) throw new InvalidDataException("Choose a movie font before adding text.");
        if (kind == ScaleformElementKind.Image)
        {
            if (chosenImage == null) throw new InvalidDataException("Choose a Texture2D or an image first.");
            var bitmap = new FormatConvertedBitmap(chosenImage, PixelFormats.Bgra32, null, 0);
            byte[] pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            spec.Pixels = pixels; spec.PixelWidth = bitmap.PixelWidth; spec.PixelHeight = bitmap.PixelHeight;
        }
        return spec;
    }
    private void Add_Click(object sender, RoutedEventArgs e) { try { ElementRequested?.Invoke(CreateSpec()); Feedback.Text = "Added. Drag the element on the canvas to position it."; } catch (Exception ex) { Feedback.Text = ex.Message; } }
    private void Card_Down(object sender, MouseButtonEventArgs e) => dragStart = e.GetPosition(DragCard);
    private void Card_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(DragCard) - dragStart).Length < 6) return;
        try { DragDrop.DoDragDrop(DragCard, new DataObject("LegendaryExplorer.ScaleformNewElement", CreateSpec()), DragDropEffects.Copy); }
        catch (Exception ex) { Feedback.Text = ex.Message; }
    }
    private static double Number(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static string SafeName(string name) { string result = new(name.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_').ToArray()); return string.IsNullOrEmpty(result) || char.IsDigit(result[0]) ? "image_" + result : result; }
    private sealed record TextureChoice(ExportEntry Export, string Label);
    private sealed record FontChoice(ushort Id, string Name);
}
