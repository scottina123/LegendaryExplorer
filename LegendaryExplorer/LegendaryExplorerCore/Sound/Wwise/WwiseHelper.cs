using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorerCore.Sound.Wwise
{
    /// <summary>
    /// Contains useful utility methods for Wwise.
    /// </summary>
    public class WwiseHelper
    {
        /// <summary>
        /// Gets the local WwiseStream exports referenced by a WwiseEvent.
        /// </summary>
        /// <remarks>
        /// ME2/ME3/LE3 store stream references in the event binary, while LE2 stores them in the
        /// References property. Both locations are checked so this also handles converted events.
        /// </remarks>
        public static IReadOnlyList<ExportEntry> GetReferencedWwiseStreams(ExportEntry wwiseEventExport)
        {
            if (wwiseEventExport?.ClassName != "WwiseEvent")
            {
                return [];
            }

            var streams = new List<ExportEntry>();
            var seenStreams = new HashSet<int>();

            void AddStream(int uIndex)
            {
                if (!wwiseEventExport.FileRef.IsUExport(uIndex) || !seenStreams.Add(uIndex))
                {
                    return;
                }

                ExportEntry stream = wwiseEventExport.FileRef.GetUExport(uIndex);
                if (stream.ClassName == "WwiseStream")
                {
                    streams.Add(stream);
                }
            }

            WwiseEvent wwiseEvent = wwiseEventExport.GetBinaryData<WwiseEvent>();
            if (wwiseEvent.Links is not null)
            {
                foreach (WwiseEvent.WwiseEventLink link in wwiseEvent.Links)
                {
                    if (link.WwiseStreams is not null)
                    {
                        foreach (int streamUIndex in link.WwiseStreams)
                        {
                            AddStream(streamUIndex);
                        }
                    }
                }
            }

            var references = wwiseEventExport.GetProperty<ArrayProperty<StructProperty>>("References");
            if (references is not null)
            {
                foreach (StructProperty reference in references)
                {
                    var relationships = reference.GetProp<StructProperty>("Relationships");
                    var referencedStreams = relationships?.GetProp<ArrayProperty<ObjectProperty>>("Streams");
                    if (referencedStreams is null)
                    {
                        continue;
                    }

                    foreach (ObjectProperty streamReference in referencedStreams)
                    {
                        AddStream(streamReference.Value);
                    }
                }
            }

            return streams;
        }

        /// <summary>
        /// Finds the unique referenced WwiseStream whose name matches a dialogue WwiseEvent's TLK ID and gender.
        /// Returns the sole referenced stream for non-dialogue events, or null when several references are ambiguous.
        /// </summary>
        public static ExportEntry GetMatchingReferencedWwiseStream(ExportEntry wwiseEventExport,
            IReadOnlyList<ExportEntry> referencedStreams = null)
        {
            if (wwiseEventExport?.ClassName != "WwiseEvent")
            {
                return null;
            }

            referencedStreams ??= GetReferencedWwiseStreams(wwiseEventExport);
            if (referencedStreams.Count == 1)
            {
                return referencedStreams[0];
            }

            string[] eventNameParts = wwiseEventExport.ObjectName.Name.Split('_');
            for (int i = 0; i < eventNameParts.Length - 1; i++)
            {
                if (!int.TryParse(eventNameParts[i], out int tlkId)
                    || eventNameParts[i + 1] is not ("m" or "M" or "f" or "F"))
                {
                    continue;
                }

                string gender = eventNameParts[i + 1].ToLowerInvariant();
                string paddedGenderedId = $"{tlkId:D8}_{gender}";
                string genderedId = $"_{tlkId}_{gender}";
                List<ExportEntry> genderMatches = referencedStreams
                    .Where(stream => stream.ObjectName.Name.Contains(paddedGenderedId, StringComparison.OrdinalIgnoreCase)
                                  || stream.ObjectName.Name.Contains(genderedId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (genderMatches.Count == 1)
                {
                    return genderMatches[0];
                }

                string paddedId = $"{tlkId:D8}";
                string delimitedId = $"_{tlkId}_";
                List<ExportEntry> idMatches = referencedStreams
                    .Where(stream => stream.ObjectName.Name.Contains(paddedId, StringComparison.OrdinalIgnoreCase)
                                  || stream.ObjectName.Name.Contains(delimitedId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return idMatches.Count == 1 ? idMatches[0] : null;
            }

            return null;
        }

        /// <summary>
        /// Gets the WwiseEvents whose uniquely matched audio is the supplied WwiseStream.
        /// This excludes events that merely include the stream in a larger, ambiguous reference list.
        /// </summary>
        public static IReadOnlyList<ExportEntry> GetMatchingWwiseEvents(ExportEntry wwiseStreamExport)
        {
            if (wwiseStreamExport?.ClassName != "WwiseStream")
            {
                return [];
            }

            var matchingEvents = new List<ExportEntry>();
            foreach (ExportEntry wwiseEvent in wwiseStreamExport.FileRef.Exports.Where(exp => exp.ClassName == "WwiseEvent"))
            {
                IReadOnlyList<ExportEntry> referencedStreams = GetReferencedWwiseStreams(wwiseEvent);
                if (referencedStreams.Any(stream => stream.UIndex == wwiseStreamExport.UIndex)
                    && GetMatchingReferencedWwiseStream(wwiseEvent, referencedStreams)?.UIndex == wwiseStreamExport.UIndex)
                {
                    matchingEvents.Add(wwiseEvent);
                }
            }

            return matchingEvents;
        }

        /// <summary>
        /// Update the DurationMilliseconds property on all WwiseEvents that reference the given WwiseStream
        /// </summary>
        /// <param name="wwiseStreamExport"></param>
        /// <param name="streamLengthInMs">Value to update DurationMilliseconds to</param>
        public static void UpdateReferencedWwiseEventLengths(ExportEntry wwiseStreamExport, float streamLengthInMs)
        {
            // LE2 has the DurationSeconds property but does not appear to be on any events, so we do nothing. I think.
            // We cannot modify ME2 Wwisestreams so we don't include them here

            if (wwiseStreamExport.Game is MEGame.ME3)
            {
                var durationProperty = new FloatProperty(streamLengthInMs, "DurationMilliseconds");

                // Find referenced WwiseEvent exports and update the property
                var referencedExports = wwiseStreamExport.GetEntriesThatReferenceThisOne();
                foreach (var re in referencedExports.Select(e => e.Key)
                                                            .Where(e => e.ClassName == "WwiseEvent")
                                                            .OfType<ExportEntry>())
                {
                    re.WriteProperty(durationProperty);
                }
            }
            // Finding all WwiseEvent references in LE games will return several WwiseExports, some incorrect
            // so we have to look up the WwiseEvent by TLK ID
            else if (wwiseStreamExport.Game is MEGame.LE3)
            {
                var durationProperty = new FloatProperty(streamLengthInMs / 1000, "DurationSeconds");

                var splits = wwiseStreamExport.ObjectName.Name.Split('_', ',');
                int tlkId = 0;
                bool specifyByGender = false;
                bool isFemaleStream = false;
                for (int i = splits.Length - 1; i > 0; i--)
                {
                    //backwards is faster
                    if (int.TryParse(splits[i], out var parsed))
                    {
                        tlkId = parsed;
                        specifyByGender = wwiseStreamExport.ObjectName.Name.Contains("player_", StringComparison.OrdinalIgnoreCase);
                        isFemaleStream = splits[i + 1] == "f";
                        break; // assume first int we find is the tlk id
                    }
                }
                if (tlkId == 0) return;

                var referencedExports = wwiseStreamExport.GetEntriesThatReferenceThisOne()
                    .Select(e => e.Key)
                    .Where(e => e.ClassName == "WwiseEvent")
                    .Where(e =>
                    {
                        if (!e.ObjectName.Name.StartsWith("VO", StringComparison.OrdinalIgnoreCase)) return false;

                        var splits = e.ObjectName.Name.Split("_");
                        if (specifyByGender)
                        {
                            return splits[1] == tlkId.ToString() && (isFemaleStream == (splits[2] == "f"));
                        }
                        else return splits[1] == tlkId.ToString();
                    });
                foreach (var re in referencedExports.OfType<ExportEntry>())
                {
                    re.WriteProperty(durationProperty);
                }
            }
        }
    }
}
