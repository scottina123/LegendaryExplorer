using System.IO;
using System.Security.Cryptography;

namespace LE1GalaxyMapEditor.Models;

/// <summary>Content identity captured when a PCC is loaded or successfully committed.</summary>
public sealed record GalaxyMapPackageFingerprint(
    long Length,
    DateTime LastWriteTimeUtc,
    string Sha256)
{
    public static GalaxyMapPackageFingerprint Capture(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var fullPath = Path.GetFullPath(packagePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The PCC file does not exist.", fullPath);
        }

        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            FileOptions.SequentialScan);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        file.Refresh();
        return new GalaxyMapPackageFingerprint(file.Length, file.LastWriteTimeUtc, hash);
    }
}
