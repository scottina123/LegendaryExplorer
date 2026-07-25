global using LegendaryExplorer.Misc;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using LegendaryExplorer.Misc.AppSettings;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
using WinFormsDialogResult = System.Windows.Forms.DialogResult;
using WinFormsFolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;

namespace LegendaryExplorer.Misc
{
    public static class DirectoryMemory
    {
        public static string GetLastDirectory(string key, string fallback = "")
        {
            if (!string.IsNullOrWhiteSpace(key)
                && Settings.Global_LastUsedDirectories.TryGetValue(key, out string savedDirectory)
                && Directory.Exists(savedDirectory))
            {
                return savedDirectory;
            }

            return GetExistingDirectory(fallback) ?? "";
        }

        public static void SaveLastDirectory(string key, string selectedPath)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            string directory = GetExistingDirectory(selectedPath);
            if (directory is null)
            {
                return;
            }

            var directories = new Dictionary<string, string>(Settings.Global_LastUsedDirectories, StringComparer.Ordinal);
            if (directories.TryGetValue(key, out string existingDirectory)
                && string.Equals(existingDirectory, directory, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            directories[key] = directory;
            Settings.Global_LastUsedDirectories = directories;
        }

        public static bool? ShowDialog(FileDialog dialog, Window owner = null, string fallback = "",
            [CallerFilePath] string callerFilePath = "", [CallerMemberName] string callerMemberName = "")
        {
            string key = GetWorkflowKey(callerFilePath, callerMemberName, dialog.Title);
            dialog.InitialDirectory = GetLastDirectory(key, string.IsNullOrWhiteSpace(fallback) ? dialog.InitialDirectory : fallback);
            bool? result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            if (result == true)
            {
                SaveLastDirectory(key, dialog.FileName);
            }
            return result;
        }

        public static CommonFileDialogResult ShowDialog(CommonFileDialog dialog, Window owner = null, string fallback = "",
            [CallerFilePath] string callerFilePath = "", [CallerMemberName] string callerMemberName = "")
        {
            string key = GetWorkflowKey(callerFilePath, callerMemberName, dialog.Title);
            dialog.InitialDirectory = GetLastDirectory(key, string.IsNullOrWhiteSpace(fallback) ? dialog.InitialDirectory : fallback);
            CommonFileDialogResult result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            if (result == CommonFileDialogResult.Ok)
            {
                SaveLastDirectory(key, dialog.FileName);
            }
            return result;
        }

        public static WinFormsDialogResult ShowDialog(WinFormsFolderBrowserDialog dialog, string fallback = "",
            [CallerFilePath] string callerFilePath = "", [CallerMemberName] string callerMemberName = "")
        {
            string key = GetWorkflowKey(callerFilePath, callerMemberName, dialog.Description);
            dialog.SelectedPath = GetLastDirectory(key, string.IsNullOrWhiteSpace(fallback) ? dialog.SelectedPath : fallback);
            WinFormsDialogResult result = dialog.ShowDialog();
            if (result == WinFormsDialogResult.OK)
            {
                SaveLastDirectory(key, dialog.SelectedPath);
            }
            return result;
        }

        public static void RememberExplorerLocation(string key, string path)
        {
            SaveLastDirectory(key, path);
        }

        private static string GetExistingDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    return Path.GetFullPath(path);
                }

                string directory = Path.GetDirectoryName(path);
                return Directory.Exists(directory) ? Path.GetFullPath(directory) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string GetWorkflowKey(string callerFilePath, string callerMemberName, string operation)
        {
            string sourceName = Path.GetFileNameWithoutExtension(callerFilePath).Replace(".xaml", "");
            string operationName = string.IsNullOrWhiteSpace(operation) ? "Dialog" : operation.Trim();
            return $"{sourceName}.{callerMemberName}.{operationName}";
        }
    }
}
