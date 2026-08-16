namespace LE1GalaxyMapEditor.Models;

internal enum GalaxyMapIdentityKind
{
    Cluster,
    System,
    Planet
}

internal enum GalaxyMapLabelParseStatus
{
    Malformed,
    Parsed,
    NumericOverflow
}

internal readonly record struct GalaxyMapLabelParseResult(
    GalaxyMapLabelParseStatus Status,
    int Suffix = 0);

/// <summary>
/// Pure parsing and encoding rules shared by galaxy-map identity callers.
/// Contextual authoring limits, uniqueness and diagnostics remain with callers.
/// </summary>
internal static class GalaxyMapIdentity
{
    public static GalaxyMapLabelParseResult ParseLabelSyntax(
        string? label,
        GalaxyMapIdentityKind kind)
    {
        var prefix = kind switch
        {
            GalaxyMapIdentityKind.Cluster => "Cluster",
            GalaxyMapIdentityKind.System => "System",
            GalaxyMapIdentityKind.Planet => "Planet",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        if (label is null ||
            !label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            label.Length == prefix.Length)
        {
            return new GalaxyMapLabelParseResult(GalaxyMapLabelParseStatus.Malformed);
        }

        var digits = label.AsSpan(prefix.Length);
        foreach (var character in digits)
        {
            if (character is < '0' or > '9')
            {
                return new GalaxyMapLabelParseResult(GalaxyMapLabelParseStatus.Malformed);
            }
        }

        if (!int.TryParse(
                digits,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var suffix))
        {
            return new GalaxyMapLabelParseResult(GalaxyMapLabelParseStatus.NumericOverflow);
        }

        return new GalaxyMapLabelParseResult(GalaxyMapLabelParseStatus.Parsed, suffix);
    }

    public static bool IsValidExistingSuffix(GalaxyMapIdentityKind kind, int suffix)
        => suffix > 0 && suffix <= GalaxyMapIdentityLimits.MaxLabel(kind);

    public static bool TryDeriveActiveWorld(
        string? clusterLabel,
        string? systemLabel,
        string? planetLabel,
        out int activeWorld)
    {
        activeWorld = 0;
        if (!TryGetValidSuffix(clusterLabel, GalaxyMapIdentityKind.Cluster, out var cluster) ||
            !TryGetValidSuffix(systemLabel, GalaxyMapIdentityKind.System, out var system) ||
            !TryGetValidSuffix(planetLabel, GalaxyMapIdentityKind.Planet, out var planet))
        {
            return false;
        }

        var calculated = (long)cluster * 10_000 + system * 100L + planet;
        if (calculated > GalaxyMapIdentityLimits.MaxActiveWorld)
        {
            return false;
        }

        activeWorld = (int)calculated;
        return true;
    }

    public static bool TryEncodeClusterRelayEndpoint(string? clusterLabel, out int encoded)
    {
        encoded = 0;
        if (!TryGetValidSuffix(clusterLabel, GalaxyMapIdentityKind.Cluster, out var suffix))
        {
            return false;
        }

        encoded = suffix * 10_000;
        return true;
    }

    private static bool TryGetValidSuffix(
        string? label,
        GalaxyMapIdentityKind kind,
        out int suffix)
    {
        var parsed = ParseLabelSyntax(label, kind);
        suffix = parsed.Suffix;
        return parsed.Status == GalaxyMapLabelParseStatus.Parsed &&
               IsValidExistingSuffix(kind, suffix);
    }
}
