using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorerCore.Unreal.Classes;

/// <summary>Removes hairstyles without replacing the character's head with another morph's vertices.</summary>
public static class BioMorphHair
{
    private static readonly HashSet<string> HairFeatures = new(StringComparer.OrdinalIgnoreCase)
    {
        "Afro", "Buzzcut", "BuzzCut_WidowsPeak", "Deiter", "Greezer", "HAIR_pulledbackslick",
        "HAIR_sidepart", "HAIR_slickWidowsPeak", "HAIR_pulledBackBig", "Hair_splitSide",
        "HAIR_centerPart", "Flattop", "flattop_widowspeak", "widowsPeak", "rollins", "straightHairLine"
        // Eastwood is a facial sculpt (including eyelids and mouth), not a hairstyle.
    };

    public sealed record Result(Vector3[][] Lods, IReadOnlyDictionary<string, Vector3> BoneDeltas, bool RemovedHairMorphs);

    public static bool IsHairFeature(string name) => name != null && HairFeatures.Contains(name);

    /// <summary>
    /// Prepares hair removal without writing the export. Use the editor's current feature weights when
    /// positions contain live edits. Callers clear m_oHairMesh for every head type and apply the result's
    /// feature/bone changes together with its LODs. Baked shapes without hairstyle metadata are preserved.
    /// </summary>
    public static Result MakeBald(ExportEntry morph, Vector3[][] positions, IReadOnlyDictionary<string, float> featureWeights = null)
    {
        if (morph is not { ClassName: "BioMorphFace", IsDefaultObject: false } || !morph.Game.IsMEGame())
            throw new ArgumentException("Select a BioMorphFace export from a Mass Effect game.", nameof(morph));

        featureWeights ??= (morph.GetProperty<ArrayProperty<StructProperty>>("m_aMorphFeatures")
                            ?? new ArrayProperty<StructProperty>("m_aMorphFeatures"))
            .Select(feature => new MorphFeature(feature))
            .GroupBy(feature => feature.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(feature => feature.Offset), StringComparer.OrdinalIgnoreCase);
        var activeHair = featureWeights.Where(feature => IsHairFeature(feature.Key) && feature.Value != 0).ToArray();
        if (activeHair.Length == 0)
            return new Result(CloneLods(positions), new Dictionary<string, Vector3>(), false);

        IEntry baseHead = morph.GetProperty<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(morph.FileRef);
        if (baseHead?.ObjectName.Name.StartsWith("HMM_", StringComparison.OrdinalIgnoreCase) != true)
            throw new InvalidDataException("The stored hairstyle features require a compatible male human base head.");

        using IMEPackage targetPackage = MEPackageHandler.OpenMEPackage(FindTargetsPackage(morph.Game));
        ExportEntry targetSet = targetPackage.FindExport("HMM_BaseMorphSet");
        if (targetSet is not { ClassName: "MorphTargetSet" }
            || !string.Equals(targetSet.GetProperty<ObjectProperty>("BaseSkelMesh")?.ResolveToEntry(targetPackage)?.ObjectName.Instanced,
                baseHead.ObjectName.Instanced, StringComparison.OrdinalIgnoreCase)
            || targetSet.GetProperty<ArrayProperty<ObjectProperty>>("Targets") is not { } targets)
            throw new InvalidDataException("Could not find a compatible HMM_BaseMorphSet in BIOG_HMM_HED_PROMorph.");

        using var cache = new PackageCache();
        var hairTargets = new Dictionary<string, MorphTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectProperty target in targets)
        {
            if (target.ResolveToEntry(targetPackage) is not { } entry
                || !activeHair.Any(feature => feature.Key.Equals(entry.ObjectName.Name, StringComparison.OrdinalIgnoreCase))) continue;
            if (target.ResolveToExport(targetPackage, cache) is not { ClassName: "MorphTarget" } export)
                throw new InvalidDataException($"Could not resolve hairstyle target {entry.ObjectName}.");
            hairTargets[entry.ObjectName.Name] = ObjectBinary.From<MorphTarget>(export);
        }
        Result result = RemoveHairMorphs(positions, featureWeights, hairTargets);
        if (result.BoneDeltas.Count > 0)
        {
            // Authored targets may include full-body rig bones (for example RFIK) absent from the head.
            // Those never influenced this mesh and must not be introduced into its final skeleton.
            ExportEntry headExport = morph.GetProperty<ObjectProperty>("m_oBaseHead").ResolveToExport(morph.FileRef, cache);
            if (headExport is not { ClassName: "SkeletalMesh" })
                throw new InvalidDataException("Could not resolve the base head to remove hairstyle bone offsets.");
            var headBones = ObjectBinary.From<SkeletalMesh>(headExport).RefSkeleton.Select(bone => bone.Name.Instanced)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            result = result with { BoneDeltas = result.BoneDeltas.Where(bone => headBones.Contains(bone.Key))
                .ToDictionary(bone => bone.Key, bone => bone.Value, StringComparer.OrdinalIgnoreCase) };
        }
        return result;
    }

    /// <summary>
    /// Removes only the weighted deltas that the current hairstyles added. Applying each complete target
    /// keeps duplicate vertices at material/UV seams together and preserves all other sculpting.
    /// </summary>
    public static Result RemoveHairMorphs(Vector3[][] positions, IReadOnlyDictionary<string, float> featureWeights,
        IReadOnlyDictionary<string, MorphTarget> targets)
    {
        Vector3[][] result = CloneLods(positions);
        var boneDeltas = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        bool removed = false;
        foreach ((string name, float weight) in featureWeights)
        {
            if (!IsHairFeature(name) || weight == 0) continue;
            if (!float.IsFinite(weight))
                throw new InvalidDataException($"The hairstyle weight for {name} is not finite.");
            if (!targets.TryGetValue(name, out MorphTarget target) || target.MorphLODModels is not { Length: > 0 })
                throw new InvalidDataException($"Could not find the morph target for hairstyle {name}.");
            if (positions is not { Length: > 0 } || positions[0] is not { Length: > 0 })
                throw new InvalidDataException("The selected morph has no stored face vertices.");

            for (int lod = 0; lod < Math.Min(result.Length, target.MorphLODModels.Length); lod++)
            {
                MorphTarget.MorphLODModel model = target.MorphLODModels[lod];
                if (model?.Vertices is not { Length: > 0 } vertices) continue;
                if (result[lod] == null || model.NumBaseMeshVerts != result[lod].Length)
                    throw new InvalidDataException($"The hairstyle target and selected head have incompatible vertices at LOD {lod}.");
                foreach (MorphTarget.MorphVertex vertex in vertices)
                {
                    if (vertex.SourceIdx >= result[lod].Length)
                        throw new InvalidDataException($"Hairstyle vertex {vertex.SourceIdx} is outside LOD {lod}.");
                    result[lod][vertex.SourceIdx] -= vertex.PositionDelta * weight;
                }
            }
            foreach (MorphTarget.BoneOffset bone in target.BoneOffsets ?? [])
                boneDeltas[bone.Bone.Instanced] = boneDeltas.GetValueOrDefault(bone.Bone.Instanced) - bone.Offset * weight;
            removed = true;
        }
        return new Result(result, boneDeltas, removed);
    }

    public static void ApplyProperties(ExportEntry morph, PropertyCollection properties, Result result)
    {
        properties.AddOrReplaceProp(new ObjectProperty(0, "m_oHairMesh"));
        if (!result.RemovedHairMorphs) return;
        if (properties.GetProp<ArrayProperty<StructProperty>>("m_aMorphFeatures") is { } features)
        {
            for (int i = features.Count - 1; i >= 0; i--)
                if (IsHairFeature(features[i].GetProp<NameProperty>("sFeatureName")?.Value.Name)) features.RemoveAt(i);
        }
        if (result.BoneDeltas.Count == 0) return;

        var skeleton = properties.GetProp<ArrayProperty<StructProperty>>("m_aFinalSkeleton")
                       ?? new ArrayProperty<StructProperty>("m_aFinalSkeleton");
        using var cache = new PackageCache();
        SkeletalMesh baseHead = null;
        foreach ((string name, Vector3 delta) in result.BoneDeltas)
        {
            if (delta == Vector3.Zero) continue;
            StructProperty bone = skeleton.FirstOrDefault(item =>
                string.Equals(item.GetProp<NameProperty>("nName")?.Value.Instanced, name, StringComparison.OrdinalIgnoreCase));
            if (bone?.GetProp<StructProperty>("vPos") is { } position)
                bone.Properties.AddOrReplaceProp(CommonStructs.Vector3Prop(CommonStructs.GetVector3(position) + delta, "vPos"));
            else
            {
                baseHead ??= ObjectBinary.From<SkeletalMesh>(properties.GetProp<ObjectProperty>("m_oBaseHead")?.ResolveToExport(morph.FileRef, cache));
                MeshBone bindBone = baseHead?.RefSkeleton.FirstOrDefault(item => item.Name.Instanced.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (bindBone == null) throw new InvalidDataException($"Could not resolve hairstyle bone {name}.");
                skeleton.Add(new StructProperty("OffsetBonePos", false, new NameProperty(name, "nName"),
                    CommonStructs.Vector3Prop(bindBone.Position + delta, "vPos")));
            }
        }
        properties.AddOrReplaceProp(skeleton);
    }

    private static string FindTargetsPackage(MEGame game)
    {
        // ME1 uses .upk files, often below CookedPC subdirectories.
        string path = MELoadedFiles.GetFilesLoadedInGame(game, forceUseCached: true)
            .FirstOrDefault(file => Path.GetFileNameWithoutExtension(file.Key).Equals("BIOG_HMM_HED_PROMorph", StringComparison.OrdinalIgnoreCase)).Value;
        if (!File.Exists(path))
            throw new FileNotFoundException($"Could not find BIOG_HMM_HED_PROMorph for {game}. Check the configured game path and installed game files.");
        return path;
    }

    private static Vector3[][] CloneLods(Vector3[][] source) => source?.Select(lod => lod?.ToArray()).ToArray() ?? [];
}
