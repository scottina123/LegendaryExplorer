using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Interfaces;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Textures;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Classes;
using Microsoft.Win32;
using Image = LegendaryExplorerCore.Textures.Image;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.UserControls.ExportLoaderControls.TextureViewer;

// Shared by the texture viewer and inline texture-reference rows. These actions do not require a renderer.
internal static class TextureFileActions
{
    private const string CreateNewTfc = "Create new TFC";
    private const string PackageStored = "Package stored";

    public static bool CanEdit(ExportEntry export) => export != null && !export.IsDefaultObject && export.IsTexture() && export.FileRef.CanReconstruct();

    public static bool CanMoveToTfc(ExportEntry export) => CanEdit(export) && export.Game > MEGame.ME1;

    public static void ExportToFile(ExportEntry export, FrameworkElement owner)
    {
        if (export == null || !export.IsTexture())
            return;

        try
        {
            var texture = new Texture2D(export);
            if (texture.GetTopMip() == null)
                throw new InvalidOperationException("The texture has no non-empty mips to export.");

            var dialog = new SaveFileDialog
            {
                Filter = "PNG files (*.png)|*.png|DDS files (*.dds)|*.dds|TGA files (*.tga)|*.tga",
                FileName = export.ObjectName.Instanced + ".png"
            };
            if (DirectoryMemory.ShowDialog(dialog, Window.GetWindow(owner)) != true)
                return;

            if (!texture.ExportToFile(dialog.FileName))
                throw new InvalidOperationException("The texture could not be exported.");
        }
        catch (Exception exception)
        {
            ShowError(owner, "Export texture", exception);
        }
    }

    public static async Task<bool> ImportFromFileAsync(ExportEntry export, FrameworkElement owner, IBusyUIHost host)
    {
        if (!CanEdit(export))
            return false;

        try
        {
            string selectedTfcName = GetDestinationTfcName(export, owner);
            if (string.IsNullOrEmpty(selectedTfcName))
                return false;

            if (IsOfficialTfc(export.Game, selectedTfcName))
            {
                ShowWarning(owner, "Import texture", "Cannot replace textures into a TFC provided by BioWare. Choose a different target TFC from the list.");
                return false;
            }

            var dialog = new OpenFileDialog
            {
                Title = "Select texture file",
                Filter = "All supported types|*.png;*.dds;*.tga;*.jpg|PNG files (*.png)|*.png|DDS files (*.dds)|*.dds|TGA files (*.tga)|*.tga|JPEG files (*.jpg)|*.jpg",
                CustomPlaces = AppDirectories.GameCustomPlaces
            };
            if (DirectoryMemory.ShowDialog(dialog, Window.GetWindow(owner)) != true)
                return false;

            if (selectedTfcName == CreateNewTfc)
            {
                string defaultTfcName = GetPreferredDlcTfcName(export) ?? "Textures_DLC_MOD_YourModFolderNameHere";
                var prompt = new PromptDialog("Enter name for a new TFC. It must start with Textures_DLC_MOD_, and will be created in the local directory of this package file.",
                    "Enter new name for TFC", defaultTfcName, true, "Textures_DLC_MOD_".Length) { Owner = Window.GetWindow(owner) };
                if (prompt.ShowDialog() != true)
                    return false;

                if (!prompt.ResponseText.StartsWith("Textures_DLC_MOD_") || prompt.ResponseText.Length <= "Textures_DLC_MOD_".Length)
                {
                    ShowWarning(owner, "Import texture", "The name must start with Textures_DLC_MOD_ and include your mod's folder name.");
                    return false;
                }
                selectedTfcName = prompt.ResponseText;
            }

            bool isPackageStored = selectedTfcName == PackageStored;
            string targetTfcName = isPackageStored ? null : selectedTfcName;
            SetBusy(host, true, "Replacing textures");
            try
            {
                List<string> messages = await Task.Run(() =>
                {
                    var props = export.GetProperties();
                    var image = Image.LoadFromFile(dialog.FileName, PixelFormat.ARGB);
                    int width = props.GetProp<IntProperty>("SizeX")?.Value ?? 0;
                    int height = props.GetProp<IntProperty>("SizeY")?.Value ?? 0;
                    if (!HasSameAspectRatio(image.mipMaps[0].origWidth, image.mipMaps[0].origHeight, width, height))
                        throw new InvalidOperationException("Cannot replace texture: Aspect ratios must be the same.");

                    return new Texture2D(export).Replace(image, props, dialog.FileName, targetTfcName, isPackageStored: isPackageStored);
                });
                if (messages.Any())
                    new ListDialog(messages, "Textures replaced", "The following messages were generated during replacement of textures.", Window.GetWindow(owner)).Show();
                return true;
            }
            finally
            {
                SetBusy(host, false);
            }
        }
        catch (TextureSizeNotPowerOf2Exception)
        {
            ShowWarning(owner, "Import texture", "The width and height of a texture must both be a power of 2\n(1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192 (LE only))");
            return false;
        }
        catch (Exception exception)
        {
            ShowError(owner, "Import texture", exception);
            return false;
        }
    }

    public static async Task<bool> MoveToTfcAsync(ExportEntry export, FrameworkElement owner, IBusyUIHost host)
    {
        if (!CanMoveToTfc(export))
            return false;

        try
        {
            string currentTfcName = export.GetProperty<NameProperty>("TextureFileCacheName")?.Value.Name;
            string preferredTfcName = GetPreferredMoveTfcName(export) ?? currentTfcName;
            if (!SelectOrAddNamePromptDialog.Prompt(GetOwnerControl(owner),
                    "Select or add the destination TFC name. A new .tfc file will be created automatically if needed.",
                    "Move texture to another TFC", export.FileRef, out NameReference targetTfcName, new NameReference(preferredTfcName)))
                return false;

            string selectedTfcName = targetTfcName.Name;
            if (string.IsNullOrWhiteSpace(selectedTfcName) || !selectedTfcName.StartsWith("Textures_", StringComparison.OrdinalIgnoreCase))
            {
                ShowWarning(owner, "Move texture to another TFC", "TFC names must start with 'Textures_'.");
                return false;
            }
            if (string.Equals(selectedTfcName, currentTfcName, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(Window.GetWindow(owner), "The selected destination TFC matches the current TFC.", "Move texture to another TFC", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            if (IsOfficialTfc(export.Game, selectedTfcName))
            {
                ShowWarning(owner, "Move texture to another TFC", "Cannot move textures into a TFC provided by BioWare. Choose a different target TFC from the list.");
                return false;
            }

            SetBusy(host, true, "Moving texture to another TFC");
            try
            {
                var messages = await Task.Run(() => MoveToTfc(export, selectedTfcName));
                if (messages.Any())
                    new ListDialog(messages, "Move texture to another TFC", "The following messages were generated while moving the texture.", Window.GetWindow(owner)).Show();
                return true;
            }
            finally
            {
                SetBusy(host, false);
            }
        }
        catch (Exception exception)
        {
            ShowError(owner, "Move texture to another TFC", exception);
            return false;
        }
    }

    internal static bool HasSameAspectRatio(int newWidth, int newHeight, int oldWidth, int oldHeight) =>
        newWidth > 0 && newHeight > 0 && oldWidth > 0 && oldHeight > 0
        && (long)newWidth * oldHeight == (long)oldWidth * newHeight;

    private static List<string> MoveToTfc(ExportEntry export, string targetTfcName)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "LegendaryExplorer", "MoveTextureBetweenTfcs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var texture = new Texture2D(export);
            var invalidChars = Path.GetInvalidFileNameChars();
            string safeName = new(export.InstancedFullPath.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
            string tempTexturePath = Path.Combine(tempDirectory, $"{export.UIndex:D8}_{safeName}.tga");
            // Moving must read the actual top mip. ExportToFile can fall back to a small internal mip
            // when the source cache is missing, which would reduce the texture's resolution on import.
            var topMip = texture.GetTopMip() ?? throw new InvalidOperationException("The texture has no non-empty mips to move.");
            byte[] textureData = Texture2D.GetTextureData(topMip, export.Game);
            TexConverter.SaveTexture(textureData, (uint)topMip.width, (uint)topMip.height,
                Image.getPixelFormatType(texture.TextureFormat), tempTexturePath);
            var image = Image.LoadFromFile(tempTexturePath, PixelFormat.ARGB);
            var messages = texture.Replace(image, export.GetProperties(), tempTexturePath, forcedTFCName: targetTfcName);
            messages.Insert(0, $"Moved {export.InstancedFullPath} to '{targetTfcName}'.");
            return messages;
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, true);
            }
            catch
            {
                // Cleanup failure must not hide the result of the texture operation.
            }
        }
    }

    private static string GetDestinationTfcName(ExportEntry export, FrameworkElement owner)
    {
        if (ObjectBinary.From<UTexture2D>(export).Mips.Count == 1)
            return PackageStored;

        string preferredTfcName = GetPreferredDlcTfcName(export);
        var options = new List<string>();
        if (export.Game > MEGame.ME1)
        {
            options.AddRange(export.FileRef.Names.Where(x => x.StartsWith("Textures_DLC_MOD_")));
            int preferredIndex = options.FindIndex(option => option.Equals(preferredTfcName, StringComparison.OrdinalIgnoreCase));
            if (preferredIndex > 0)
            {
                options.Insert(0, options[preferredIndex]);
                options.RemoveAt(preferredIndex + 1);
            }
            options.Add(CreateNewTfc);
        }
        options.Add(PackageStored);
        string defaultOption = options.FirstOrDefault(option => option.Equals(preferredTfcName, StringComparison.OrdinalIgnoreCase))
            ?? options.LastOrDefault(option => option != CreateNewTfc && option != PackageStored)
            ?? options.Last();
        return StringSelectorDialog.GetValue(GetOwnerControl(owner),
            "Select where the new texture should be stored. TFCs are better for game performance.",
            "Select storage location", options, defaultOption);
    }

    private static string GetPreferredDlcTfcName(ExportEntry export)
    {
        string filePath = export.FileRef.FilePath;
        if (string.IsNullOrWhiteSpace(filePath) || export.Game <= MEGame.ME1 || MEDirectories.IsInOfficialDLC(filePath, export.Game))
            return null;

        string dlcName = filePath.DetermineDLCNameFromPath();
        return !string.IsNullOrWhiteSpace(dlcName) ? $"Textures_{dlcName}" : null;
    }

    private static string GetPreferredMoveTfcName(ExportEntry export)
    {
        string filePath = export.FileRef.FilePath;
        if (string.IsNullOrWhiteSpace(filePath) || export.Game <= MEGame.ME1)
            return null;

        string dlcName = filePath.DetermineDLCNameFromPath();
        if (string.IsNullOrWhiteSpace(dlcName))
        {
            for (DirectoryInfo directory = Directory.GetParent(filePath); directory != null; directory = directory.Parent)
            {
                dlcName = directory.Name.NormalizeDLCFolderName();
                if (!string.IsNullOrWhiteSpace(dlcName))
                    break;
            }
        }
        return string.IsNullOrWhiteSpace(dlcName) ? null : $"Textures_{dlcName}";
    }

    private static bool IsOfficialTfc(MEGame game, string name) =>
        MEDirectories.BasegameTFCs(game).Contains(name, StringComparer.InvariantCultureIgnoreCase)
        || MEDirectories.OfficialDLC(game).Any(x => $"Textures_{x}".Equals(name, StringComparison.InvariantCultureIgnoreCase));

    private static Control GetOwnerControl(FrameworkElement owner) => Window.GetWindow(owner) ?? owner as Control;

    private static void SetBusy(IBusyUIHost host, bool isBusy, string text = null)
    {
        if (host == null)
            return;
        if (text != null)
            host.BusyText = text;
        host.IsBusy = isBusy;
    }

    private static void ShowWarning(FrameworkElement owner, string title, string message) =>
        MessageBox.Show(Window.GetWindow(owner), message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    private static void ShowError(FrameworkElement owner, string title, Exception exception) =>
        MessageBox.Show(Window.GetWindow(owner), $"Error: {exception.Message}", title, MessageBoxButton.OK, MessageBoxImage.Error);
}
