using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace LegendaryExplorer.Misc;

internal static class BinkPlayerLauncher
{
    private static readonly string[] SupportedExecutableNames =
    [
        "Bink2ForUnreal.exe",
        "RADVideo64.exe",
        "RADVideo.exe",
        "BinkPl64.exe",
        "BinkPlay64.exe",
        "BinkPlay.exe"
    ];

    private static readonly string[] RadVideoExecutableNames =
    [
        "RADVideo64.exe",
        "RADVideo.exe",
        "Bink2ForUnreal.exe",
        "BinkPl64.exe",
        "BinkPlay64.exe",
        "BinkPlay.exe"
    ];

    public static string FindExecutable(string configuredPath)
    {
        if (IsSupportedExecutable(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        foreach (string installDirectory in GetRadVideoInstallDirectories())
        {
            foreach (string executableName in RadVideoExecutableNames)
            {
                string candidate = Path.Combine(installDirectory, executableName);
                if (IsSupportedExecutable(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public static bool SupportsBink2(string executablePath)
    {
        if (!IsSupportedExecutable(executablePath))
        {
            return false;
        }

        string executableName = Path.GetFileName(executablePath);
        if (executableName.Equals("Bink2ForUnreal.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
        return SupportsBink2Version(executableName, versionInfo.ProductVersion ?? versionInfo.FileVersion);
    }

    internal static bool SupportsBink2Version(string executableName, string version)
    {
        if (!IsSupportedExecutableName(executableName))
        {
            return false;
        }

        if (executableName.Equals("Bink2ForUnreal.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        string majorPart = version.Split('.')[0];
        return int.TryParse(majorPart, out int majorVersion) && majorVersion >= 2;
    }

    public static ProcessStartInfo CreateStartInfo(string executablePath, string moviePath)
    {
        if (!IsSupportedExecutableName(Path.GetFileName(executablePath)))
        {
            throw new ArgumentException("The selected executable is not a supported Bink player.", nameof(executablePath));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false
        };

        string executableName = Path.GetFileName(executablePath);
        if (executableName.Equals("RADVideo64.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("RADVideo.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("Bink2ForUnreal.exe", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("BinkPlay");
        }

        startInfo.ArgumentList.Add(moviePath);
        return startInfo;
    }

    private static bool IsSupportedExecutable(string path)
        => !string.IsNullOrWhiteSpace(path)
           && File.Exists(path)
           && IsSupportedExecutableName(Path.GetFileName(path));

    private static bool IsSupportedExecutableName(string executableName)
        => Array.Exists(SupportedExecutableNames,
            supportedName => supportedName.Equals(executableName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> GetRadVideoInstallDirectories()
    {
        var yieldedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RegistryView registryView in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (string subKeyName in new[] { @"SOFTWARE\RAD Game Tools\RADVideo", @"SOFTWARE\WOW6432Node\RAD Game Tools\RADVideo" })
            {
                string installDirectory = null;
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, registryView);
                    using RegistryKey key = baseKey.OpenSubKey(subKeyName);
                    installDirectory = key?.GetValue("InstallDir") as string
                                       ?? key?.GetValue("InstallLocation") as string;
                }
                catch
                {
                    // A missing or inaccessible registry view simply means RAD Video Tools was not installed there.
                }

                if (!string.IsNullOrWhiteSpace(installDirectory) && yieldedDirectories.Add(installDirectory))
                {
                    yield return installDirectory;
                }
            }
        }
    }
}
