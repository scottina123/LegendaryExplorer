using System;
using System.Globalization;
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
            && parentProperty is StructProperty { StructType: "BioFOVOTrackData" });

    /// <summary>
    /// Resolves a gendered dialogue play event or audio stream in the track's package.
    /// Ambiguous matches are left unresolved rather than choosing the first entry.
    /// </summary>
    internal static IEntry FindAudioEntry(ExportEntry track, int stringRef, bool isFemale, bool stream)
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

        if (!stream)
        {
            return matchingEvents.SingleOrDefault();
        }

        if (matchingEvents.SingleOrDefault() is ExportEntry wwiseEvent)
        {
            try
            {
                var referencedStreams = WwiseHelper.GetReferencedWwiseStreams(wwiseEvent);
                var matchingStream = WwiseHelper.GetMatchingReferencedWwiseStream(wwiseEvent, referencedStreams);
                // A sole reference can intentionally reuse an NPC voice for both genders. With several
                // references, require exact tokens to avoid the helper's permissive substring fallback.
                if (matchingStream != null && (referencedStreams.Count == 1
                                               || MatchesStreamName(matchingStream.ObjectName.Name, stringRef, isFemale)))
                {
                    return matchingStream;
                }
            }
            catch (Exception)
            {
                // An incomplete event should not prevent navigation to an identifiable named stream.
            }
        }

        var matchingStreams = entries.Where(entry => entry.ClassName == "WwiseStream"
                                                    && MatchesStreamName(entry.ObjectName.Name, stringRef, isFemale))
            .Take(2)
            .ToList();
        return matchingStreams.Count == 1 ? matchingStreams[0] : null;
    }

    private static bool IsWwiseVoTrack(ExportEntry export) =>
        export != null
        && export.Game is MEGame.ME2 or MEGame.ME3 or MEGame.LE2 or MEGame.LE3
        && (export.ClassName == "BioEvtSysTrackVOElements" || IsFaceOnlyVoTrack(export));

    private static bool IsFaceOnlyVoTrack(ExportEntry export) => export.IsA("SFXInterpTrackPlayFaceOnlyVO");

    private static bool MatchesPlayEventName(string name, int stringRef, bool isFemale)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        string[] parts = name.Split('_');
        if (parts.Length != 4)
        {
            return false;
        }

        if (parts[0].Equals("VO", StringComparison.OrdinalIgnoreCase)
            && parts[3].Equals("Play", StringComparison.OrdinalIgnoreCase))
        {
            return MatchesIdAndGender(parts[1], parts[2], stringRef, isFemale);
        }

        return parts[0].Equals("Play", StringComparison.OrdinalIgnoreCase)
               && parts[1].Equals("VO", StringComparison.OrdinalIgnoreCase)
               && MatchesIdAndGender(parts[2], parts[3], stringRef, isFemale);
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
