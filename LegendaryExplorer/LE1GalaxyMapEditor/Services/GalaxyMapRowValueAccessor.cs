using System.Globalization;
using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Services;

/// <summary>
/// Provides the single raw-2DA-column boundary used by projections and editing.
/// UI surfaces should not duplicate model/column mappings or parse cell tokens
/// independently.
/// </summary>
public static class GalaxyMapRowValueAccessor
{
    public static string GetCsvToken(GalaxyMapRow row, string column)
    {
        var value = GetValue(row, column);
        return value switch
        {
            null => string.Empty,
            double number => GalaxyMapNumber.Serialize(number),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value?.ToString() ?? string.Empty
        };
    }

    public static object? GetValue(GalaxyMapRow row, string column)
    {
        if (IsRowId(column))
        {
            return row.RowId;
        }

        var known = row switch
        {
            Cluster cluster => ClusterValue(cluster, column),
            GalaxySystem system => SystemValue(system, column),
            Planet planet => PlanetValue(planet, column),
            PlotPlanetEntry plotPlanet => PlotPlanetValue(plotPlanet, column),
            MapEntry map => MapValue(map, column),
            RelayConnection relay => RelayValue(relay, column),
            _ => Missing.Value
        };
        return ReferenceEquals(known, Missing.Value)
            ? row.ExtraFields.GetValueOrDefault(column)
            : known;
    }

    public static bool IsReadOnly(GalaxyMapRow row, string column)
        => IsRowId(column) || row is Planet && column.Equals("ActiveWorld", StringComparison.OrdinalIgnoreCase);

    public static string PropertyName(GalaxyMapRow row, string column)
    {
        if (IsRowId(column))
        {
            return nameof(GalaxyMapRow.RowId);
        }

        return (row, column.ToUpperInvariant()) switch
        {
            (GalaxySystem, "CLUSTER") => nameof(GalaxySystem.ClusterRowId),
            (Planet, "SYSTEM") => nameof(Planet.SystemRowId),
            (Planet, "MAP") => nameof(Planet.MapRowId),
            (MapEntry, "MAP") => nameof(MapEntry.MapName),
            (RelayConnection, "STARTCLUSTER") => nameof(RelayConnection.StartClusterEncoded),
            (RelayConnection, "ENDCLUSTER") => nameof(RelayConnection.EndClusterEncoded),
            _ when IsKnownColumn(row, column) => column,
            _ => $"ExtraFields[{column}]"
        };
    }

    public static bool TryParse(
        GalaxyMapRow row,
        string column,
        string token,
        out object? value,
        out string? error)
    {
        value = null;
        error = null;
        token ??= string.Empty;

        if (IsReadOnly(row, column))
        {
            error = IsRowId(column)
                ? "Row IDs are managed by the editor and cannot be changed in the table viewer."
                : $"{column} is derived from managed galaxy-map relationships.";
            return false;
        }

        if (IsDoubleColumn(row, column))
        {
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                !double.IsFinite(number))
            {
                error = "Enter a number using a decimal point.";
                return false;
            }

            if (!GalaxyMapNumber.HasSupportedPrecision(number))
            {
                error = "Enter a value with no more than two decimal places.";
                return false;
            }

            value = number;
            return true;
        }

        if (IsNullableIntegerColumn(row, column))
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                value = null;
                return true;
            }

            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nullableInteger))
            {
                error = "Enter a whole number or leave this cell blank.";
                return false;
            }

            value = nullableInteger;
            return true;
        }

        if (row is Planet && column.Equals("RingColor", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var packedColor))
            {
                error = "Enter a packed 32-bit colour value.";
                return false;
            }

            value = packedColor;
            return true;
        }

        if (IsIntegerColumn(row, column))
        {
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            {
                error = "Enter a whole number.";
                return false;
            }

            value = integer;
            return true;
        }

        value = token;
        return true;
    }

    public static void SetValue(GalaxyMapRow row, string column, object? value)
    {
        if (IsReadOnly(row, column))
        {
            throw new InvalidOperationException($"{column} is read-only in the table viewer.");
        }

        switch (row)
        {
            case Cluster cluster:
                SetClusterValue(cluster, column, value);
                break;
            case GalaxySystem system:
                SetSystemValue(system, column, value);
                break;
            case Planet planet:
                SetPlanetValue(planet, column, value);
                break;
            case PlotPlanetEntry plotPlanet:
                SetPlotPlanetValue(plotPlanet, column, value);
                break;
            case MapEntry map:
                SetMapValue(map, column, value);
                break;
            case RelayConnection relay:
                SetRelayValue(relay, column, value);
                break;
            default:
                throw new ArgumentException($"Unsupported galaxy-map row type {row.GetType().Name}.", nameof(row));
        }
    }

    private static bool IsKnownColumn(GalaxyMapRow row, string column)
        => !ReferenceEquals(row switch
        {
            Cluster cluster => ClusterValue(cluster, column),
            GalaxySystem system => SystemValue(system, column),
            Planet planet => PlanetValue(planet, column),
            PlotPlanetEntry plotPlanet => PlotPlanetValue(plotPlanet, column),
            MapEntry map => MapValue(map, column),
            RelayConnection relay => RelayValue(relay, column),
            _ => Missing.Value
        }, Missing.Value);

    private static bool IsRowId(string column)
        => string.Equals(column, CsvRowSnapshot.RowIdColumnName, StringComparison.OrdinalIgnoreCase);

    private static bool IsDoubleColumn(GalaxyMapRow row, string column)
        => (row, column.ToUpperInvariant()) switch
        {
            (Cluster, "X" or "Y" or "SPHERESIZE") => true,
            (GalaxySystem, "X" or "Y" or "SCALE") => true,
            (Planet, "X" or "Y" or "SCALE") => true,
            _ => false
        };

    private static bool IsNullableIntegerColumn(GalaxyMapRow row, string column)
        => row is Planet && column.ToUpperInvariant() is
            "DESCRIPTION" or "BUTTONLABEL" or "PLANETLEVELTYPE" or "IMAGEINDEX";

    private static bool IsIntegerColumn(GalaxyMapRow row, string column)
        => (row, column.ToUpperInvariant()) switch
        {
            (Cluster, "NAME") => true,
            (GalaxySystem, "CLUSTER" or "NAME" or "SHOWNEBULA") => true,
            (Planet, "SYSTEM" or "NAME" or "ACTIVEWORLD" or "MAP" or "ORBITRING" or "SYSTEMLEVELTYPE") => true,
            (PlotPlanetEntry, "CODE" or "NAME") => true,
            (RelayConnection, "STARTCLUSTER" or "ENDCLUSTER") => true,
            _ => false
        };

    private static object? ClusterValue(Cluster row, string column) => column.ToUpperInvariant() switch
    {
        "LABEL" => row.Label, "X" => row.X, "Y" => row.Y, "NAME" => row.Name,
        "NAMETEXT" => row.NameText, "SPHERESIZE" => row.SphereSize, "BACKGROUND" => row.Background,
        _ => Missing.Value
    };

    private static object? SystemValue(GalaxySystem row, string column) => column.ToUpperInvariant() switch
    {
        "LABEL" => row.Label, "CLUSTER" => row.ClusterRowId, "X" => row.X, "Y" => row.Y,
        "NAME" => row.Name, "NAMETEXT" => row.NameText, "SCALE" => row.Scale, "SHOWNEBULA" => row.ShowNebula,
        _ => Missing.Value
    };

    private static object? PlanetValue(Planet row, string column) => column.ToUpperInvariant() switch
    {
        "LABEL" => row.Label, "SYSTEM" => row.SystemRowId, "X" => row.X, "Y" => row.Y,
        "NAME" => row.Name, "NAMETEXT" => row.NameText, "ACTIVEWORLD" => row.ActiveWorld,
        "DESCRIPTION" => row.Description, "BUTTONLABEL" => row.ButtonLabel, "MAP" => row.MapRowId,
        "SCALE" => row.Scale, "RINGCOLOR" => row.RingColor, "ORBITRING" => row.OrbitRing,
        "SYSTEMLEVELTYPE" => row.SystemLevelType, "PLANETLEVELTYPE" => row.PlanetLevelType,
        "EVENT" => row.Event, "IMAGEINDEX" => row.ImageIndex,
        _ => Missing.Value
    };

    private static object? PlotPlanetValue(PlotPlanetEntry row, string column) => column.ToUpperInvariant() switch
    {
        "CODE" => row.Code, "NAME" => row.Name, "NAMETEXT" => row.NameText, _ => Missing.Value
    };

    private static object? MapValue(MapEntry row, string column) => column.ToUpperInvariant() switch
    {
        "MAP" => row.MapName, "STARTPOINT" => row.StartPoint, _ => Missing.Value
    };

    private static object? RelayValue(RelayConnection row, string column) => column.ToUpperInvariant() switch
    {
        "STARTCLUSTER" => row.StartClusterEncoded, "ENDCLUSTER" => row.EndClusterEncoded, _ => Missing.Value
    };

    private static void SetClusterValue(Cluster row, string column, object? value)
    {
        switch (column.ToUpperInvariant())
        {
            case "LABEL": row.Label = (string)value!; return;
            case "X": row.X = (double)value!; return;
            case "Y": row.Y = (double)value!; return;
            case "NAME": row.Name = (int)value!; return;
            case "NAMETEXT": row.NameText = (string)value!; return;
            case "SPHERESIZE": row.SphereSize = (double)value!; return;
            case "BACKGROUND": row.Background = (string)value!; return;
            default: SetExtra(row, column, value); return;
        }
    }

    private static void SetSystemValue(GalaxySystem row, string column, object? value)
    {
        switch (column.ToUpperInvariant())
        {
            case "LABEL": row.Label = (string)value!; return;
            case "CLUSTER": row.ClusterRowId = (int)value!; return;
            case "X": row.X = (double)value!; return;
            case "Y": row.Y = (double)value!; return;
            case "NAME": row.Name = (int)value!; return;
            case "NAMETEXT": row.NameText = (string)value!; return;
            case "SCALE": row.Scale = (double)value!; return;
            case "SHOWNEBULA": row.ShowNebula = (int)value!; return;
            default: SetExtra(row, column, value); return;
        }
    }

    private static void SetPlanetValue(Planet row, string column, object? value)
    {
        switch (column.ToUpperInvariant())
        {
            case "LABEL": row.Label = (string)value!; return;
            case "SYSTEM": row.SystemRowId = (int)value!; return;
            case "X": row.X = (double)value!; return;
            case "Y": row.Y = (double)value!; return;
            case "NAME": row.Name = (int)value!; return;
            case "NAMETEXT": row.NameText = (string)value!; return;
            case "DESCRIPTION": row.Description = (int?)value; return;
            case "BUTTONLABEL": row.ButtonLabel = (int?)value; return;
            case "MAP": row.MapRowId = (int)value!; return;
            case "SCALE": row.Scale = (double)value!; return;
            case "RINGCOLOR": row.RingColor = (long)value!; return;
            case "ORBITRING": row.OrbitRing = (int)value!; return;
            case "SYSTEMLEVELTYPE": row.SystemLevelType = (int)value!; return;
            case "PLANETLEVELTYPE": row.PlanetLevelType = (int?)value; return;
            case "EVENT": row.Event = (string)value!; return;
            case "IMAGEINDEX": row.ImageIndex = (int?)value; return;
            default: SetExtra(row, column, value); return;
        }
    }

    private static void SetPlotPlanetValue(PlotPlanetEntry row, string column, object? value)
    {
        switch (column.ToUpperInvariant())
        {
            case "CODE": row.Code = (int)value!; return;
            case "NAME": row.Name = (int)value!; return;
            case "NAMETEXT": row.NameText = (string)value!; return;
            default: SetExtra(row, column, value); return;
        }
    }

    private static void SetMapValue(MapEntry row, string column, object? value)
    {
        switch (column.ToUpperInvariant())
        {
            case "MAP": row.MapName = (string)value!; return;
            case "STARTPOINT": row.StartPoint = (string)value!; return;
            default: SetExtra(row, column, value); return;
        }
    }

    private static void SetRelayValue(RelayConnection row, string column, object? value)
    {
        switch (column.ToUpperInvariant())
        {
            case "STARTCLUSTER": row.StartClusterEncoded = (int)value!; return;
            case "ENDCLUSTER": row.EndClusterEncoded = (int)value!; return;
            default: SetExtra(row, column, value); return;
        }
    }

    private static void SetExtra(GalaxyMapRow row, string column, object? value)
        => row.SetExtraField(column, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);

    private static class Missing
    {
        public static readonly object Value = new();
    }
}
