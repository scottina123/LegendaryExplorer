using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore.Helpers;

namespace LegendaryExplorer.Misc;

/// <summary>Offers file actions for each member of a level set without combining their names or paths.</summary>
public static class FileReferenceMenu
{
    public static ICommand CopyFileNameCommand { get; } = new RelayCommand(
        value => Clipboard.SetText(Path.GetFileName((string)value)), IsFilePath);
    public static ICommand CopyFilePathCommand { get; } = new RelayCommand(
        value => Clipboard.SetText(Path.GetFullPath((string)value)), IsFilePath);
    public static ICommand OpenFileLocationCommand { get; } = new RelayCommand(OpenFileLocation, CanOpenFileLocation);

    public static ContextMenu Create(IEnumerable<string> paths)
    {
        var menu = new ContextMenu();
        AddActions(menu, paths);
        return menu;
    }

    public static void AddActions(ContextMenu menu, IEnumerable<string> paths)
    {
        string[] files = paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        menu.Items.Add(CreateSubmenu("Copy file name", files, CopyFileNameCommand));
        menu.Items.Add(CreateSubmenu("Copy file path", files, CopyFilePathCommand));
        menu.Items.Add(CreateSubmenu("Open file location", files, OpenFileLocationCommand));
    }

    public static ContextMenu CreateForFile(string path)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateAction("Copy file name", path, CopyFileNameCommand));
        menu.Items.Add(CreateAction("Copy file path", path, CopyFilePathCommand));
        menu.Items.Add(CreateAction("Open file location", path, OpenFileLocationCommand));
        return menu;
    }

    private static MenuItem CreateSubmenu(string header, string[] paths, ICommand command)
    {
        var menu = new MenuItem { Header = header, IsEnabled = paths.Length > 0 };
        var duplicateNames = paths.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            // Different folders can contain identically named level files.
            string label = duplicateNames.Contains(name) ? $"{name} ({Path.GetDirectoryName(path)})" : name;
            menu.Items.Add(CreateAction(label.Replace("_", "__"), path, command));
        }
        return menu;
    }

    private static MenuItem CreateAction(string header, string path, ICommand command) => new()
    {
        Header = header, ToolTip = path, Command = command, CommandParameter = path
    };

    private static bool IsFilePath(object value) => value is string path && !string.IsNullOrWhiteSpace(path);

    private static bool CanOpenFileLocation(object value) => IsFilePath(value)
        && (File.Exists((string)value) || Directory.Exists(Path.GetDirectoryName(Path.GetFullPath((string)value))));

    private static void OpenFileLocation(object value)
    {
        if (!CanOpenFileLocation(value)) return;
        string path = Path.GetFullPath((string)value);
        DirectoryMemory.RememberExplorerLocation("LevelFiles.OpenFileLocation", path);
        if (File.Exists(path))
            LegendaryExplorerCoreUtilities.OpenAndSelectFileInExplorer(path);
        else
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"") { UseShellExecute = true });
    }
}
