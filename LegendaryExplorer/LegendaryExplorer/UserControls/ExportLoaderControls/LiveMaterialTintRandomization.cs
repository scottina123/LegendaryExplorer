using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorer.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

/// <summary>Shared tint randomization settings and preview behavior for the mesh and actor editors.</summary>
public sealed class LiveMaterialTintRandomization : NotifyPropertyChangedBase
{
    private readonly Func<MEGame, Task<LiveMaterialTintCatalogResult>> _loadCatalog;

    public LiveMaterialTintRandomization() : this(LiveMaterialTintCatalog.GetCatalogAsync) { }

    internal LiveMaterialTintRandomization(Func<MEGame, Task<LiveMaterialTintCatalogResult>> loadCatalog) =>
        _loadCatalog = loadCatalog;

    private bool _useBioWareTintValues;
    public bool UseBioWareTintValues
    {
        get => _useBioWareTintValues;
        set => SetProperty(ref _useBioWareTintValues, value);
    }

    private bool _isIdle = true;
    public bool IsIdle
    {
        get => _isIdle;
        private set => SetProperty(ref _isIdle, value);
    }

    private string _status;
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    internal async Task RandomizeAsync(LiveMaterialEditorMaterial material, Func<bool> isCurrent)
    {
        if (!IsIdle || material is null || !isCurrent())
            return;

        if (!UseBioWareTintValues)
        {
            foreach (LiveVectorMaterialParameter parameter in material.VectorParameters.Where(parameter => IsTint(parameter.ParameterName)))
                parameter.SetValue(Random.Shared.NextSingle(), Random.Shared.NextSingle(), Random.Shared.NextSingle(), parameter.A);
            Status = null;
            return;
        }

        IsIdle = false;
        Status = $"Loading vanilla BioWare tints from the {material.MaterialExport.Game} Asset Database…";
        try
        {
            LiveMaterialTintCatalogResult catalog = await _loadCatalog(material.MaterialExport.Game);
            if (!isCurrent())
            {
                Status = null;
                return;
            }
            if (!string.IsNullOrWhiteSpace(catalog.Error))
            {
                Status = catalog.Error;
                return;
            }

            var currentTints = material.VectorParameters.Where(parameter => IsTint(parameter.ParameterName))
                .ToDictionary(parameter => parameter.ParameterName,
                    parameter => new LinearColor(parameter.R, parameter.G, parameter.B, parameter.A), StringComparer.OrdinalIgnoreCase);
            var parent = material.MaterialExport.GetProperty<ObjectProperty>("Parent")?.ResolveToEntry(material.MaterialExport.FileRef);
            IReadOnlyList<LiveMaterialTintProfile> profiles = SelectProfiles(catalog.Profiles, currentTints.Keys, parent?.InstancedFullPath);
            if (profiles.Count == 0)
            {
                Status = "No compatible vanilla BioWare tint palettes were found. The current values were left unchanged.";
                return;
            }

            int firstIndex = Random.Shared.Next(profiles.Count);
            int secondIndex = profiles.Count == 1 ? firstIndex : Random.Shared.Next(profiles.Count - 1);
            if (profiles.Count > 1 && secondIndex >= firstIndex)
                secondIndex++;
            IReadOnlyDictionary<string, LinearColor> values = BlendTints(currentTints, profiles[firstIndex], profiles[secondIndex],
                0.4f + Random.Shared.NextSingle() * 0.2f);
            foreach (LiveVectorMaterialParameter parameter in material.VectorParameters)
                if (values.TryGetValue(parameter.ParameterName, out LinearColor color))
                    parameter.SetValue(color.R, color.G, color.B, color.A);

            int unmatched = currentTints.Count - values.Count;
            Status = $"Randomized {values.Count} tints from {profiles.Count} vanilla BioWare palettes."
                     + (unmatched > 0 ? $" {unmatched} unmatched tints were left unchanged." : "");
        }
        catch (Exception exception)
        {
            Status = isCurrent() ? $"Could not load vanilla BioWare tints: {exception.Message}" : null;
        }
        finally
        {
            IsIdle = true;
        }
    }

    internal static IReadOnlyList<LiveMaterialTintProfile> SelectProfiles(IEnumerable<LiveMaterialTintProfile> profiles,
        IEnumerable<string> parameterNames, string parentMaterialKey)
    {
        var names = parameterNames.Where(IsTint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = profiles.Select(profile => (Profile: profile, Count: profile.Tints.Keys.Count(names.Contains)))
            .Where(match => match.Count > 0).ToList();
        if (!string.IsNullOrWhiteSpace(parentMaterialKey))
        {
            var sameParent = matches.Where(match => string.Equals(match.Profile.ParentMaterialKey, parentMaterialKey,
                StringComparison.OrdinalIgnoreCase)).ToList();
            if (sameParent.Count > 0)
                matches = sameParent;
        }
        int mostMatches = matches.Count == 0 ? 0 : matches.Max(match => match.Count);
        return matches.Where(match => match.Count == mostMatches).Select(match => match.Profile).ToArray();
    }

    internal static IReadOnlyDictionary<string, LinearColor> BlendTints(IReadOnlyDictionary<string, LinearColor> currentTints,
        LiveMaterialTintProfile first, LiveMaterialTintProfile second, float firstWeight)
    {
        // A single pair and blend weight preserve each donor palette's relationship between tint slots.
        var firstTints = new Dictionary<string, LinearColor>(first.Tints, StringComparer.OrdinalIgnoreCase);
        var secondTints = new Dictionary<string, LinearColor>(second.Tints, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, LinearColor>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, LinearColor current) in currentTints)
        {
            if (!IsTint(name))
                continue;
            bool hasFirst = firstTints.TryGetValue(name, out LinearColor firstColor);
            bool hasSecond = secondTints.TryGetValue(name, out LinearColor secondColor);
            if (!hasFirst && !hasSecond)
                continue;
            if (!hasFirst) firstColor = secondColor;
            if (!hasSecond) secondColor = firstColor;
            result[name] = new LinearColor(
                firstColor.R * firstWeight + secondColor.R * (1f - firstWeight),
                firstColor.G * firstWeight + secondColor.G * (1f - firstWeight),
                firstColor.B * firstWeight + secondColor.B * (1f - firstWeight), current.A);
        }
        return result;
    }

    private static bool IsTint(string name) => name?.StartsWith("TNT_", StringComparison.OrdinalIgnoreCase) == true;
}
