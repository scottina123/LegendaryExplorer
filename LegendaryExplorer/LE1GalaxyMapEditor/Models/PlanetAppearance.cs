namespace LE1GalaxyMapEditor.Models;

/// <summary>
/// A detached, lossless copy of the appearance columns for one Planet row.
/// Values remain as CSV tokens until a specific editor changes them.
/// </summary>
public sealed class PlanetAppearance
{
    private readonly Dictionary<string, string> _values;

    public PlanetAppearance(IEnumerable<KeyValuePair<string, string>> values)
    {
        _values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
    }

    public string this[string column]
    {
        get => _values.GetValueOrDefault(column) ?? string.Empty;
        set => _values[column] = value ?? string.Empty;
    }

    public string Shader
    {
        get => this["Shader"];
        set => this["Shader"] = value;
    }

    public IReadOnlyDictionary<string, string> Values => _values;

    public PlanetAppearance Clone() => new(_values);

    public void CopyVisualsFrom(PlanetAppearance source)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (var column in Services.PlanetAppearanceSchema.Columns)
        {
            if (!column.Equals("Shader", StringComparison.OrdinalIgnoreCase))
            {
                this[column] = source[column];
            }
        }
    }

    public void ReplaceFrom(PlanetAppearance source)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (var column in Services.PlanetAppearanceSchema.Columns)
        {
            this[column] = source[column];
        }
    }
}
