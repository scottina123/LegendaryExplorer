using System;
using System.Collections.Generic;
using System.Linq;

namespace LegendaryExplorer.Tools.AssetDatabase;

/// <summary>
/// Groups package copies for the Morphs tab without changing the persisted, per-export morph records.
/// </summary>
public sealed class MorphFaceAsset : IAssetRecord
{
    private MorphFaceAsset(string morphName, IEnumerable<BioMorphFaceRecord> records)
    {
        MorphName = morphName;
        Usages = records
            .DistinctBy(record => (record.FileKey, record.UIndex))
            .OrderBy(record => record.IsMod)
            .ThenBy(record => record.FileKey)
            .ThenBy(record => record.UIndex)
            .Select(record => new MorphFaceUsage(record.FileKey, record.UIndex, record.IsMod, record))
            .ToArray();
    }

    public string MorphName { get; }
    public string BaseHeadName => Usages[0].Morph.BaseHeadName;
    public string SpeciesDisplayName => Usages[0].Morph.SpeciesDisplayName;
    public bool IsModOnly => Usages.All(usage => usage.IsInMod);
    public IReadOnlyList<MorphFaceUsage> Usages { get; }
    public IEnumerable<IAssetUsage> AssetUsages => Usages;

    public static List<MorphFaceAsset> GroupRecords(IEnumerable<BioMorphFaceRecord> records) => records
        .GroupBy(record => record.MorphName, StringComparer.OrdinalIgnoreCase)
        .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
        .Select(group => new MorphFaceAsset(group.Key, group))
        .ToList();

    public IEnumerable<MorphFaceUsage> GetMatchingUsages(string searchText, Func<int, string> getSourceFileName,
        Predicate<int> includeFile = null) => Usages.Where(usage =>
        (includeFile?.Invoke(usage.FileKey) ?? true) && usage.MatchesSearch(searchText, getSourceFileName));
}

/// <summary>
/// Retains each package's morph data so selecting a usage previews that exact copy, including mod overrides.
/// </summary>
public sealed record MorphFaceUsage(int FileKey, int UIndex, bool IsInMod, BioMorphFaceRecord Morph) : IAssetUsage
{
    public bool MatchesSearch(string searchText, Func<int, string> getSourceFileName)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        bool Contains(string value) => value?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true;
        return Contains(Morph.MorphName)
               || Contains(Morph.BaseHeadName)
               || Contains(Morph.SpeciesDisplayName)
               || Contains(getSourceFileName(FileKey))
               || Contains(UIndex.ToString())
               || (Morph.Features?.Any(feature => Contains(feature.Name)) ?? false)
               || (Morph.ScalarOverrides?.Any(scalar => Contains(scalar.Name)) ?? false)
               || (Morph.ColorOverrides?.Any(color => Contains(color.Name)) ?? false);
    }
}
