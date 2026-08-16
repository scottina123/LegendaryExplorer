using System.IO;

namespace LE1GalaxyMapEditor.Services;

internal static class AtomicFileWriter
{
    public static void Write(string targetPath, ReadOnlySpan<byte> contents)
    {
        var fullTargetPath = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullTargetPath)
            ?? throw new IOException($"The target path has no parent directory: {fullTargetPath}");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullTargetPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullTargetPath))
            {
                File.Replace(temporaryPath, fullTargetPath, destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullTargetPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
