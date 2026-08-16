namespace LE1GalaxyMapEditor.Services;

public sealed class GalaxyMapLoadException : Exception
{
    public GalaxyMapLoadException(string message) : base(message)
    {
    }

    public GalaxyMapLoadException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
