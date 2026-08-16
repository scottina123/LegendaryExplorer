using System.IO;

namespace LE1GalaxyMapEditor.Services;

/// <summary>
/// Defines the deployable resource folders which live beside the application.
/// Keeping this contract in one place prevents loaders from silently falling
/// back to source-tree files which will not exist in a published build.
/// </summary>
public static class ApplicationResourcePaths
{
    public static string ResourceRoot { get; } =
        Path.Combine(AppContext.BaseDirectory, "resources");

    public static string DataDirectory { get; } =
        Path.Combine(ResourceRoot, "data");

    public static string TextureDirectory { get; } =
        Path.Combine(ResourceRoot, "textures");

    public static string GetDataFilePath(string fileName)
        => GetChildFilePath(DataDirectory, fileName);

    public static string GetTextureFilePath(string fileName)
        => GetChildFilePath(TextureDirectory, fileName);

    private static string GetChildFilePath(string directory, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (Path.IsPathRooted(fileName) ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            throw new ArgumentException("A resource name must be a single file name.", nameof(fileName));
        }

        return Path.Combine(directory, fileName);
    }
}
