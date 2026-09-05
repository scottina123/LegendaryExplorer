using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorer.Tools.AssetDatabase.VFXPreview;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.Tools.LevelEditor;

public sealed class LensFlareElementPreview
{
    public bool IsSource { get; }
    public bool IsEnabled { get; }
    public bool UseSourceDistance { get; }
    public bool NormalizeRadialDistance { get; }
    public bool ModulateColorBySource { get; }
    public float RayDistance { get; }
    public Vector3 Size { get; }
    public IReadOnlyList<IEntry> Materials { get; }
    private readonly IVfxDistribution<float> materialIndex, scaling, rotation, alpha, distanceAlpha;
    private readonly IVfxDistribution<Vector3> axisScaling, color, offset, distanceScale, distanceColor;

    public LensFlareElementPreview(StructProperty element, ExportEntry template, bool isSource)
    {
        IsSource = isSource;
        IsEnabled = element.GetProp<BoolProperty>("bIsEnabled")?.Value ?? true;
        UseSourceDistance = element.GetProp<BoolProperty>("bUseSourceDistance")?.Value ?? false;
        NormalizeRadialDistance = element.GetProp<BoolProperty>("bNormalizeRadialDistance")?.Value ?? false;
        ModulateColorBySource = element.GetProp<BoolProperty>("bModulateColorBySource")?.Value ?? false;
        RayDistance = isSource ? 0 : element.GetProp<FloatProperty>("RayDistance")?.Value ?? 0;
        Size = element.GetProp<StructProperty>("Size") is { } size ? CommonStructs.GetVector3(size) : Vector3.One;
        Materials = element.GetProp<ArrayProperty<ObjectProperty>>("LFMaterials")?
            .Select(reference => reference.ResolveToEntry(template.FileRef)).ToArray() ?? [];
        materialIndex = ReadFloat("LFMaterialIndex", 0);
        scaling = ReadFloat("Scaling", 1);
        rotation = ReadFloat("Rotation", 0);
        alpha = ReadFloat("Alpha", 1);
        distanceAlpha = ReadFloat("DistMap_Alpha", 1);
        axisScaling = ReadVector("AxisScaling", Vector3.One);
        color = ReadVector("Color", Vector3.One);
        offset = ReadVector("Offset", Vector3.Zero);
        distanceScale = ReadVector("DistMap_Scale", Vector3.One);
        distanceColor = ReadVector("DistMap_Color", Vector3.One);

        IVfxDistribution<float> ReadFloat(string name, float fallback) =>
            ParticleSystemSourceAdapter.ReadFloatDistribution(element.GetProp<StructProperty>(name), template, fallback);
        IVfxDistribution<Vector3> ReadVector(string name, Vector3 fallback) =>
            ParticleSystemSourceAdapter.ReadVectorDistribution(element.GetProp<StructProperty>(name), template, fallback);
    }

    public LensFlareElementSample Evaluate(float radialDistance, float sourceDistance, Vector4 sourceColor)
    {
        float input = UseSourceDistance ? sourceDistance : radialDistance;
        Vector3 elementColor = color.Evaluate(input, 0.5f) * distanceColor.Evaluate(sourceDistance, 0.5f);
        float opacity = alpha.Evaluate(input, 0.5f) * distanceAlpha.Evaluate(sourceDistance, 0.5f);
        var tint = new Vector4(elementColor, opacity);
        if (IsSource || ModulateColorBySource) tint *= sourceColor;
        Vector3 size = Size * scaling.Evaluate(input, 0.5f) * axisScaling.Evaluate(input, 0.5f)
                       * distanceScale.Evaluate(sourceDistance, 0.5f);
        float index = materialIndex.Evaluate(input, 0.5f);
        IEntry material = Materials.Count > 0 && float.IsFinite(index)
            ? Materials[Math.Clamp((int)index, 0, Materials.Count - 1)] : null;
        return new LensFlareElementSample(material, new Vector2(size.X, size.Y),
            rotation.Evaluate(input, 0.5f) * MathF.Tau, tint, offset.Evaluate(input, 0.5f));
    }
}

public readonly record struct LensFlareElementSample(IEntry Material, Vector2 Size, float Rotation, Vector4 Color, Vector3 Offset);

public sealed class LensFlarePreview
{
    public IReadOnlyList<LensFlareElementPreview> Elements { get; }
    public float Radius { get; }
    public float InnerCone { get; }
    public float OuterCone { get; }
    private readonly float coneFudgeFactor;
    private readonly IVfxDistribution<float> screenPercentageMap;

    public LensFlarePreview(ExportEntry template)
    {
        PropertyCollection properties = template.GetCondensedProperties();
        List<LensFlareElementPreview> elements = [];
        if (properties.GetProp<StructProperty>("SourceElement") is { } source)
            elements.Add(new LensFlareElementPreview(source, template, true));
        if (properties.GetProp<ArrayProperty<StructProperty>>("Reflections") is { } reflections)
            elements.AddRange(reflections.Select(element => new LensFlareElementPreview(element, template, false)));
        Elements = elements;
        Radius = properties.GetProp<FloatProperty>("Radius")?.Value ?? 0;
        InnerCone = properties.GetProp<FloatProperty>("InnerCone")?.Value ?? 0;
        OuterCone = properties.GetProp<FloatProperty>("OuterCone")?.Value ?? 0;
        coneFudgeFactor = properties.GetProp<FloatProperty>("ConeFudgeFactor")?.Value ?? 1;
        screenPercentageMap = ParticleSystemSourceAdapter.ReadFloatDistribution(
            properties.GetProp<StructProperty>("ScreenPercentageMap"), template, 1);
    }

    public float GetIntensity(Vector3 sourceToCamera, Vector3 forward)
    {
        float distance = sourceToCamera.Length();
        if (!float.IsFinite(distance) || distance < 0.001f || Radius > 0 && distance > Radius) return 0;
        if (OuterCone <= 0 || forward.LengthSquared() < 0.000001f) return 1;
        float angle = MathF.Acos(Math.Clamp(Vector3.Dot(sourceToCamera / distance, Vector3.Normalize(forward)), -1, 1))
                      * (180f / MathF.PI) * MathF.Max(0, coneFudgeFactor);
        if (angle <= InnerCone) return 1;
        if (angle >= OuterCone) return 0;
        return (OuterCone - angle) / MathF.Max(0.001f, OuterCone - InnerCone);
    }

    public float GetOcclusion(float visibleFraction) => Math.Clamp(screenPercentageMap.Evaluate(visibleFraction, 0.5f), 0, 1);

    public static Vector3 GetElementPosition(Vector3 source, SceneCamera camera, float rayDistance)
    {
        Matrix4x4.Invert(camera.ViewMatrix, out Matrix4x4 viewToWorld);
        Vector3 forward = Vector3.TransformNormal(Vector3.UnitZ, viewToWorld);
        float depth = Vector3.Dot(source - viewToWorld.Translation, forward);
        Vector3 center = viewToWorld.Translation + forward * depth;
        return Vector3.Lerp(source, center, rayDistance);
    }
}
