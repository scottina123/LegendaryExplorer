using System.Globalization;
using System.IO;
using LegendaryExplorerCore.Packages;

namespace LE1GalaxyMapEditor.Services;

public sealed record GalaxyMapStrRefPresentation(string State, string Text, string Context);

public static class GalaxyMapStrRefPresenter
{
    public static GalaxyMapStrRefPresentation Present(
        GalaxyMapTlkService? tlk,
        MELocalization locale,
        string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new("No TLK reference", "This property is null.", $"Locale: {locale}");
        }
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stringRef) || stringRef < 0)
        {
            return new("Invalid TLK reference", $"'{token}' is not a valid non-negative StrRef.",
                $"Locale: {locale}");
        }
        if (tlk is null || (tlk.AvailableLocales.Count == 0 && tlk.Diagnostics.Count > 0))
        {
            return new("TLK cache unavailable",
                "Legendary Explorer's current LE1 TLK selection could not be loaded.",
                tlk?.Diagnostics.FirstOrDefault() ?? "No TLK cache service is available.");
        }
        if (!tlk.AvailableLocales.Contains(locale))
        {
            return new("Locale unavailable",
                $"{locale} is not present in Legendary Explorer's current LE1 TLK selection.",
                $"StrRef: {stringRef}");
        }

        var lookup = tlk.Find(locale, stringRef);
        return lookup is null
            ? new("String not found", $"No selected {locale} TLK contains StrRef {stringRef}.",
                $"Locale: {locale}")
            : new(
                $"{lookup.Locale} · StrRef {lookup.StringRef}",
                lookup.Text,
                $"Source: {Path.GetFileName(lookup.SourcePackage)} · {lookup.SourceExportName} " +
                $"(export {lookup.SourceExportUIndex})");
    }
}
