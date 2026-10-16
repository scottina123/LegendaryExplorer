using System;
using System.Globalization;
using System.IO;
using System.Linq;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Sound.Wwise;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

internal static class VoTrackAudioNavigation
{
    internal static bool IsTrackStringRef(ExportEntry export, Property property, Property parentProperty = null) =>
        IsWwiseVoTrack(export)
        && property is IntProperty or StringRefProperty
        && (export.ClassName == "BioEvtSysTrackVOElements" && property.Name.Name == "m_nStrRefID"
            || IsFaceOnlyVoTrack(export) && property.Name.Name == "nLineStrRef"
            && parentProperty is StructProperty { StructType: "BioFOVOTrackData" }
            || export.IsA("BioEvtSysTrackSubtitles") && property.Name.Name == "nStrRefID"
            && parentProperty is StructProperty { StructType: "BioSubtitleTrackData" });

    internal static bool IsTrackAudioProperty(ExportEntry export, Property property, Property parentProperty = null) =>
        IsTrackStringRef(export, property, parentProperty)
        || IsWwiseVoTrack(export) && export.IsA("InterpTrackWwiseEvent")
        && property is ObjectProperty { Name.Name: "Event" }
        && parentProperty is StructProperty { StructType: "WwiseEventTrackKey" };

    internal static IEntry FindAudioEntry(ExportEntry track, Property property, Property parentProperty, bool isFemale, bool stream,
        Func<ImportEntry, ExportEntry> resolveImport = null)
    {
        if (!IsTrackAudioProperty(track, property, parentProperty))
        {
            return null;
        }

        if (property is ObjectProperty eventRef)
        {
            IEntry referencedEvent = track.FileRef.GetEntry(eventRef.Value);
            if (referencedEvent?.ClassName != "WwiseEvent"
                || !TryGetPlayEventInfo(referencedEvent.ObjectName.Name, out int stringRef, out bool eventIsFemale))
            {
                return null;
            }

            // The key explicitly identifies this event, even when another package entry has the same name.
            return eventIsFemale == isFemale
                ? FindAudioEntry(track, referencedEvent, stringRef, isFemale, stream, resolveImport)
                : FindAudioEntry(track, stringRef, isFemale, stream, resolveImport);
        }

        return FindAudioEntry(track, property is IntProperty intProperty
            ? intProperty.Value : ((StringRefProperty)property).Value, isFemale, stream, resolveImport);
    }

    /// <summary>
    /// Resolves a gendered dialogue play event or audio stream, following imported events when a resolver is supplied.
    /// Ambiguous matches are left unresolved rather than choosing the first entry.
    /// </summary>
    internal static IEntry FindAudioEntry(ExportEntry track, int stringRef, bool isFemale, bool stream,
        Func<ImportEntry, ExportEntry> resolveImport = null)
    {
        if (!IsWwiseVoTrack(track) || stringRef <= 0)
        {
            return null;
        }

        var entries = track.FileRef.Exports.Cast<IEntry>().Concat(track.FileRef.Imports).ToList();
        var matchingEvents = entries.Where(entry => entry.ClassName == "WwiseEvent"
                                                   && MatchesPlayEventName(entry.ObjectName.Name, stringRef, isFemale))
            .ToList();
        if (matchingEvents.Count > 1)
        {
            return null;
        }

        return FindAudioEntry(track, matchingEvents.SingleOrDefault(), stringRef, isFemale, stream, resolveImport);
    }

    private static IEntry FindAudioEntry(ExportEntry track, IEntry matchingEvent, int stringRef, bool isFemale, bool stream,
        Func<ImportEntry, ExportEntry> resolveImport)
    {
        if (!stream)
        {
            return matchingEvent;
        }

        try
        {
            ExportEntry wwiseEvent = matchingEvent as ExportEntry
                                     ?? (matchingEvent is ImportEntry import ? resolveImport?.Invoke(import) : null);
            if (wwiseEvent?.ClassName == "WwiseEvent")
            {
                var referencedStreams = WwiseHelper.GetReferencedWwiseStreamEntries(wwiseEvent);
                // A sole reference can intentionally reuse an NPC voice for both genders. With several
                // references, require complete ID and gender tokens, including imported stream names.
                var matches = referencedStreams.Count == 1 ? referencedStreams
                    : referencedStreams.Where(entry => MatchesStreamName(entry.ObjectName.Name, stringRef, isFemale)).Take(2).ToList();
                if (matches.Count == 1)
                {
                    return PreferLocalStream(track.FileRef, matches[0]);
                }
            }
        }
        catch (Exception)
        {
            // An unavailable import or incomplete event must not prevent an identifiable named fallback.
        }

        var matchingStreams = track.FileRef.Exports.Cast<IEntry>().Concat(track.FileRef.Imports)
            .Where(entry => entry.ClassName == "WwiseStream"
                            && MatchesStreamName(entry.ObjectName.Name, stringRef, isFemale))
            .Take(2)
            .ToList();
        return matchingStreams.Count == 1 ? matchingStreams[0] : null;
    }

    private static IEntry PreferLocalStream(IMEPackage package, IEntry stream)
    {
        if (ReferenceEquals(package, stream.FileRef))
        {
            return stream;
        }

        var localMatches = package.Imports
            .Where(entry => entry.ClassName == "WwiseStream"
                            && (entry.InstancedFullPath.Equals(stream.InstancedFullPath, StringComparison.OrdinalIgnoreCase)
                                || stream is ExportEntry { IsForcedExport: false }
                                && entry.InstancedFullPath.Equals($"{Path.GetFileNameWithoutExtension(stream.FileRef.FilePath)}.{stream.InstancedFullPath}",
                                    StringComparison.OrdinalIgnoreCase)))
            .Take(2).ToList();
        return localMatches.Count == 1 ? localMatches[0] : stream;
    }

    private static bool IsWwiseVoTrack(ExportEntry export) =>
        export != null
        && export.Game is MEGame.ME2 or MEGame.ME3 or MEGame.LE2 or MEGame.LE3
        && (export.ClassName == "BioEvtSysTrackVOElements" || IsFaceOnlyVoTrack(export)
            || export.IsA("BioEvtSysTrackSubtitles") || export.IsA("InterpTrackWwiseEvent"));

    private static bool IsFaceOnlyVoTrack(ExportEntry export) => export.IsA("SFXInterpTrackPlayFaceOnlyVO");

    private static bool MatchesPlayEventName(string name, int stringRef, bool isFemale)
        => TryGetPlayEventInfo(name, out int parsedId, out bool parsedIsFemale)
           && parsedId == stringRef && parsedIsFemale == isFemale;

    internal static bool TryGetPlayEventStringRef(string name, out int stringRef) =>
        TryGetPlayEventInfo(name, out stringRef, out _);

    internal static bool TryGetPlayEventInfo(string name, out int stringRef, out bool isFemale)
    {
        stringRef = 0;
        isFemale = false;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        string[] parts = name.Split('_');
        if (parts.Length != 4)
        {
            return false;
        }

        string id;
        string gender;
        if (parts[0].Equals("VO", StringComparison.OrdinalIgnoreCase)
            && parts[3].Equals("Play", StringComparison.OrdinalIgnoreCase))
        {
            id = parts[1];
            gender = parts[2];
        }
        else if (parts[0].Equals("Play", StringComparison.OrdinalIgnoreCase)
                 && parts[1].Equals("VO", StringComparison.OrdinalIgnoreCase))
        {
            id = parts[2];
            gender = parts[3];
        }
        else
        {
            return false;
        }

        isFemale = gender.Equals("f", StringComparison.OrdinalIgnoreCase);
        return (isFemale || gender.Equals("m", StringComparison.OrdinalIgnoreCase))
               && int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out stringRef)
               && stringRef > 0;
    }

    private static bool MatchesStreamName(string name, int stringRef, bool isFemale)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        string[] parts = name.Split('_', ',');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (MatchesIdAndGender(parts[i], parts[i + 1], stringRef, isFemale))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesIdAndGender(string id, string gender, int stringRef, bool isFemale) =>
        int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedId)
        && parsedId == stringRef
        && gender.Equals(isFemale ? "f" : "m", StringComparison.OrdinalIgnoreCase);
}
