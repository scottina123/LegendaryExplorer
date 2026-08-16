using System.Text;
using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Services;

namespace LE1GalaxyMapEditor.ViewModels;

public sealed record GalaxyMapPropertyMetadata(string DisplayName, string Description, bool IsStrRef = false)
{
    public Func<string, GalaxyMapStrRefPresentation>? ResolveStrRef { get; init; }
}

/// <summary>
/// Human-facing knowledge about the raw 2DA columns. This deliberately keeps
/// confidence in the wording so observed vanilla patterns are not presented as
/// reverse-engineered fact.
/// </summary>
public static class GalaxyMapPropertyCatalog
{
    public static GalaxyMapPropertyMetadata Get(GalaxyMapTable table, string column)
    {
        if (TryAvailability(column, out var availability))
        {
            return availability;
        }

        return (table, column.ToUpperInvariant()) switch
        {
            (_, "ROW ID") => new("Row ID", "Confirmed: the actual 2DA row index. This is separate from the numbered Label and derived ActiveWorld ID."),
            (GalaxyMapTable.Cluster, "LABEL") => new("Label", "Confirmed: internal Cluster01-Cluster99 label. Its numeric suffix × 10,000 is used by Relay endpoints and descendant ActiveWorld IDs."),
            (GalaxyMapTable.System, "LABEL") => new("Label", "Confirmed: internal System01-System09 label. Its numeric suffix × 100 contributes to descendant ActiveWorld IDs and must be unique inside the Cluster."),
            (GalaxyMapTable.Planet, "LABEL") => new("Label", "Confirmed: internal Planet01-Planet99 label. Its numeric suffix contributes directly to ActiveWorld and must be unique inside the System."),
            (_, "NAME") => new("Name (TLK)", "Confirmed: localised TLK string reference used for the displayed name.", IsStrRef: true),
            (_, "NAMETEXT") => new("Internal name", "Confirmed: non-localised internal/editor-facing name."),
            (_, "X") => new("Map X", "Confirmed: horizontal position on this map, normally from 0 to 1."),
            (_, "Y") => new("Map Y", "Confirmed: vertical position on this map, normally from 0 to 1."),
            (GalaxyMapTable.System, "CLUSTER") => new("Parent Cluster", "Confirmed: row ID of the Cluster containing this System."),
            (GalaxyMapTable.Planet, "SYSTEM") => new("Parent System", "Confirmed: row ID of the System containing this object."),
            (GalaxyMapTable.Planet, "ACTIVEWORLD") => new("ActiveWorld ID", "Derived: Cluster suffix × 10,000 + System suffix × 100 + Planet suffix. The maximum supported value is 990999; the editor maintains it automatically."),
            (GalaxyMapTable.Planet, "DESCRIPTION") => new("Description (TLK)", "Confirmed: localised TLK string reference for the object description.", IsStrRef: true),
            (GalaxyMapTable.Planet, "BUTTONLABEL") => new("Use button (TLK)", "Confirmed: localised TLK string reference for the button used to interact with the object.", IsStrRef: true),
            (GalaxyMapTable.Planet, "MAP") => new("Linked Map", "Confirmed: Map-table row ID, or -1 when no destination is linked."),
            (GalaxyMapTable.System, "SCALE") => new("Canvas scale", "Confirmed: size of the navigable System canvas. Vanilla ranges from 0.1 to 2."),
            (GalaxyMapTable.Planet, "SCALE") => new("Object scale", "Confirmed: physical size in System view. Scale is the only structural distinction between ordinary moons, planets and giants."),
            (GalaxyMapTable.Cluster, "SPHERESIZE") => new("Map size", "Confirmed: visual size of the Cluster marker/map sphere. Vanilla values are approximately 4 to 4.5."),
            (GalaxyMapTable.Cluster, "BACKGROUND") => new("Background texture", "Confirmed: Cluster texture reference used by the Cluster map and ShowNebula Systems."),
            (GalaxyMapTable.System, "SHOWNEBULA") => new("Show Cluster nebula", "Confirmed: replaces the normal sun/star background with the parent Cluster texture. Vanilla uses this only for Widow."),
            (GalaxyMapTable.System, "EXITMAP") => new("ExitMap (unused)", "Observed in vanilla: every System uses 0. No functioning System-level purpose has been verified."),
            (GalaxyMapTable.Planet, "EXITMAP") => new("ExitMap", "Experimental: non-zero only for Citadel, Noveria and Feros in vanilla. Its exact docking/skybox behaviour is not yet verified."),
            (GalaxyMapTable.Planet, "PLANETROTATION") => new("Planet rotation (unused)", "Observed in vanilla: every entry uses 0. No functioning effect has been verified."),
            (GalaxyMapTable.Planet, "RINGCOLOR") => new("Ring colour", "Confirmed: packed colour for SystemLevelType 2. Non-ringed objects must use -1; -1 is also retained by one vanilla ringed planet."),
            (GalaxyMapTable.Planet, "ORBITRING") => new("Orbit display", "Confirmed: 0 = none, 1 = orbit ring, 2 = asteroid belt."),
            (GalaxyMapTable.Planet, "SYSTEMLEVELTYPE") => new("System-view type", "Confirmed: selects what is rendered in System view: planet, anomaly, ringed planet, relay, depot or sun."),
            (GalaxyMapTable.Planet, "PLANETLEVELTYPE") => new("Selection-view type", "Confirmed: selects the model shown after selection. Values 3, 5 and 7 are known to be broken in LE1."),
            (GalaxyMapTable.Planet, "EVENT") => new("Remote event", "Confirmed: Kismet remote event fired when the object is used. Vanilla commonly uses Land or a destination-specific event name."),
            (GalaxyMapTable.Planet, "IMAGEINDEX") => new("Preview image", "Confirmed: thumbnail index displayed beside the object description. -1 means no image."),
            (GalaxyMapTable.Planet, "EVENTCONDITION" or "EVENTFUNCTION" or "EVENTPARAMETER" or
                "EVENTTRANSITION" or "EVENTTRANSITIONPARAMETER" or "EVENTMESSAGE") =>
                new(LegacyEventName(column), "Observed in vanilla: legacy event-routing field retained at an inert default. Vanilla behaviour is implemented through the Remote Event instead."),
            (_, "COLOUR" or "COLOUR2") => new(Humanize(column), "Experimental: packed visual colour. Vanilla values vary, but the exact rendered target is not yet verified."),
            (GalaxyMapTable.System, "FLARETINT") => new("Flare tint", "Experimental: packed visual colour. It matches Colour 2 on 42 of 43 vanilla Systems, suggesting a related visual role."),
            (GalaxyMapTable.Cluster, "NEBULARDENSITY") => new("Nebula density", "Experimental: Cluster visual parameter. Vanilla usually uses 1, with observed values from 0.2 to 2."),
            (GalaxyMapTable.Cluster, "CLOUDTILE") => new("Cloud tiling", "Experimental: Cluster visual parameter. Vanilla usually uses 1."),
            (GalaxyMapTable.Cluster, "SPHEREINTENSITY") => new("Sphere intensity", "Experimental: Cluster visual parameter. Vanilla usually uses 3."),
            (GalaxyMapTable.Map, "MAP") => new("Persistent level", "Confirmed: Unreal persistent-level/package name loaded by this destination."),
            (GalaxyMapTable.Map, "STARTPOINT") => new("Start point", "Confirmed: coordinate-node name used as the player's spawn point when the Map loads."),
            (GalaxyMapTable.Relay, "STARTCLUSTER") => new("Start Cluster", "Confirmed: encoded Cluster label suffix × 10,000."),
            (GalaxyMapTable.Relay, "ENDCLUSTER") => new("End Cluster", "Confirmed: encoded Cluster label suffix × 10,000."),
            (GalaxyMapTable.PlotPlanet, "CODE") => new("ActiveWorld code", "Derived: must equal the linked Planet's ActiveWorld value."),
            (GalaxyMapTable.Planet, _) when PlanetAppearanceSchema.IsAppearanceColumn(column) =>
                new(CompactAppearanceName(column), $"Experimental: Planet shader/appearance parameter. Exact rendering behaviour is not yet documented. Raw column: {column}."),
            _ => new(Humanize(column), "Advanced 2DA field. Its precise runtime behaviour has not yet been documented by this editor.")
        };
    }

    private static bool TryAvailability(string column, out GalaxyMapPropertyMetadata metadata)
    {
        foreach (var prefix in new[] { "Visible", "UsablePlanet", "Usable" })
        {
            if (!column.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var scope = prefix switch
            {
                "Visible" => "Visible",
                "UsablePlanet" => "Button",
                _ => "Usable"
            };
            var suffix = column[prefix.Length..];
            metadata = suffix.ToUpperInvariant() switch
            {
                "CONDITIONAL" => new($"{scope}: conditional", "Confirmed triplet component. It is independent from Parameter; vanilla contains valid rows where they differ."),
                "FUNCTION" => new($"{scope}: function", "Confirmed triplet component. The known Always preset uses function 974; hidden anomaly visibility commonly uses 975."),
                "PARAMETER" => new($"{scope}: parameter", "Confirmed triplet component passed to the selected function. Do not assume it matches Conditional."),
                _ => new(Humanize(column), "Availability-rule field.")
            };
            return true;
        }

        metadata = null!;
        return false;
    }

    private static string LegacyEventName(string column) => column.ToUpperInvariant() switch
    {
        "EVENTCONDITION" => "Condition",
        "EVENTFUNCTION" => "Function",
        "EVENTPARAMETER" => "Parameter",
        "EVENTTRANSITION" => "Transition",
        "EVENTTRANSITIONPARAMETER" => "Transition parameter",
        "EVENTMESSAGE" => "Message",
        _ => Humanize(column)
    };

    private static string CompactAppearanceName(string column)
    {
        var name = Humanize(column);
        return name
            .Replace("Horizon Atmosphere", "Horizon", StringComparison.OrdinalIgnoreCase)
            .Replace("Emissive Twinkle Multiplier", "Emissive twinkle", StringComparison.OrdinalIgnoreCase)
            .Replace("Atmosphere Pan Multiplier", "Atmosphere pan", StringComparison.OrdinalIgnoreCase)
            .Replace("Multiplier", "amount", StringComparison.OrdinalIgnoreCase)
            .Replace("Mixer02", "mix 2", StringComparison.OrdinalIgnoreCase)
            .Replace("Mixer", "mix", StringComparison.OrdinalIgnoreCase);
    }

    private static string Humanize(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index] == '_' ? ' ' : value[index];
            if (index > 0 && char.IsUpper(character) && char.IsLower(value[index - 1]) && value[index - 1] != '_')
            {
                builder.Append(' ');
            }
            builder.Append(character);
        }
        return builder.ToString();
    }
}
