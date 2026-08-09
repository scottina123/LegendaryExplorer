using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// Emissive-material evaluation and geometry reduction used by the static-lighting baker. All
/// material traversal and triangle clustering happens during scene preprocessing, never per texel.
/// </summary>
public static class StaticLightingEmissive
{
    public const float MinimumContribution = 0.002f;
    public const float MinimumEmitterPower = 1f;
    public const float TargetAreaPerSample = 65_536f;
    public const int MaximumSamplesPerSection = 16;
    public const float MaximumInfluenceRadius = 16_384f;

    private static readonly string[] EmissiveTokens = ["emiss", "glow", "neon"];

    public static bool TryGetSettings(PropertyCollection componentProperties,
        out StaticLightingEmissiveSettings settings)
    {
        StructProperty lightmass = componentProperties.GetProp<StructProperty>("LightmassSettings");
        bool enabled = lightmass?.GetProp<BoolProperty>("bUseEmissiveForStaticLighting")?.Value
                       ?? componentProperties.GetProp<BoolProperty>("bUseEmissiveForStaticLighting")?.Value
                       ?? false;
        if (!enabled)
        {
            settings = default;
            return false;
        }

        float boost = lightmass?.GetProp<FloatProperty>("EmissiveBoost")?.Value
                      ?? componentProperties.GetProp<FloatProperty>("EmissiveBoost")?.Value
                      ?? 1f;
        float falloff = lightmass?.GetProp<FloatProperty>("EmissiveLightFalloffExponent")?.Value
                        ?? componentProperties.GetProp<FloatProperty>("EmissiveLightFalloffExponent")?.Value
                        ?? 2f;
        float radius = lightmass?.GetProp<FloatProperty>("EmissiveLightExplicitInfluenceRadius")?.Value
                       ?? componentProperties.GetProp<FloatProperty>("EmissiveLightExplicitInfluenceRadius")?.Value
                       ?? 0f;
        bool twoSided = lightmass?.GetProp<BoolProperty>("bUseTwoSidedLighting")?.Value
                        ?? componentProperties.GetProp<BoolProperty>("bUseTwoSidedLighting")?.Value
                        ?? false;
        if (!float.IsFinite(boost) || boost <= 0f)
        {
            settings = default;
            return false;
        }

        settings = new StaticLightingEmissiveSettings(
            Math.Clamp(boost, 0f, 128f),
            float.IsFinite(falloff) && falloff > 0f ? Math.Clamp(falloff, 0.25f, 8f) : 2f,
            float.IsFinite(radius) && radius > 0f ? Math.Min(radius, MaximumInfluenceRadius) : 0f,
            twoSided);
        return true;
    }

    public static bool TryResolveMaterialRadiance(ExportEntry material,
        LevelEditorRenderContext renderContext, out Vector3 radiance)
    {
        radiance = default;
        if (material is null)
            return false;

        var chain = new List<ExportEntry>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ExportEntry current = material;
        while (current is not null && visited.Add($"{current.FileRef.FilePath}|{current.UIndex}"))
        {
            chain.Add(current);
            ObjectProperty parent = current.GetProperties(packageCache: renderContext.PackageCache)
                .GetProp<ObjectProperty>("Parent");
            current = parent is null ? null : renderContext.ResolveExportCached(current.FileRef, parent.Value);
        }

        ExportEntry baseMaterial = chain.LastOrDefault(entry => entry.ClassName == "Material") ?? chain[^1];
        PropertyCollection baseProperties = baseMaterial.GetProperties(packageCache: renderContext.PackageCache);
        bool isUnlit = baseProperties.GetProp<EnumProperty>("LightingModel")?.Value.Name == "MLM_Unlit";
        bool hasPathSignal = chain.Any(entry => HasEmissiveToken(entry.InstancedFullPath));
        var vectors = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        var scalars = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        if (baseProperties.GetProp<ArrayProperty<ObjectProperty>>("Expressions") is { } expressions)
        {
            foreach (ObjectProperty reference in expressions)
            {
                ExportEntry expression = renderContext.ResolveExportCached(baseMaterial.FileRef, reference.Value);
                if (expression is null) continue;
                string parameterName = expression.GetProperty<NameProperty>("ParameterName")?.Value.Instanced;
                if (string.IsNullOrWhiteSpace(parameterName)) continue;
                if (expression.ClassName == "MaterialExpressionVectorParameter" &&
                    expression.GetProperty<StructProperty>("DefaultValue") is { } vector)
                {
                    vectors[parameterName] = ReadColor(vector, Vector3.One);
                }
                else if (expression.ClassName == "MaterialExpressionScalarParameter" &&
                         expression.GetProperty<FloatProperty>("DefaultValue") is { } scalar)
                {
                    scalars[parameterName] = scalar.Value;
                }
            }
        }

        // Base defaults are loaded first; each child MIC then replaces them with its effective override.
        for (int chainIndex = chain.Count - 1; chainIndex >= 0; chainIndex--)
        {
            PropertyCollection properties = chain[chainIndex].GetProperties(packageCache: renderContext.PackageCache);
            if (properties.GetProp<ArrayProperty<StructProperty>>("VectorParameterValues") is { } vectorParameters)
            {
                foreach (StructProperty parameter in vectorParameters)
                {
                    string name = parameter.GetProp<NameProperty>("ParameterName")?.Value.Instanced;
                    if (!string.IsNullOrWhiteSpace(name) &&
                        parameter.GetProp<StructProperty>("ParameterValue") is { } value)
                        vectors[name] = ReadColor(value, Vector3.One);
                }
            }
            if (properties.GetProp<ArrayProperty<StructProperty>>("ScalarParameterValues") is { } scalarParameters)
            {
                foreach (StructProperty parameter in scalarParameters)
                {
                    string name = parameter.GetProp<NameProperty>("ParameterName")?.Value.Instanced;
                    if (!string.IsNullOrWhiteSpace(name) &&
                        parameter.GetProp<FloatProperty>("ParameterValue") is { } value)
                        scalars[name] = value.Value;
                }
            }
        }

        bool hasParameterSignal = vectors.Keys.Any(HasEmissiveToken) || scalars.Keys.Any(HasEmissiveToken);
        if (!isUnlit && !hasPathSignal && !hasParameterSignal)
            return false;

        Vector3 color = Vector3.One;
        KeyValuePair<string, Vector3>? selectedColor = vectors
            .Select(pair => (Pair: pair, Score: ScoreColorParameter(pair.Key, isUnlit)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .Select(item => (KeyValuePair<string, Vector3>?)item.Pair)
            .FirstOrDefault();
        if (selectedColor.HasValue)
            color = selectedColor.Value.Value;

        float intensity = 1f;
        KeyValuePair<string, float>? selectedIntensity = scalars
            .Where(pair => float.IsFinite(pair.Value))
            .Select(pair => (Pair: pair, Score: ScoreScalarParameter(pair.Key, isUnlit)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .Select(item => (KeyValuePair<string, float>?)item.Pair)
            .FirstOrDefault();
        if (selectedIntensity.HasValue)
            intensity = selectedIntensity.Value.Value;

        color = Vector3.Clamp(color, Vector3.Zero, new Vector3(128f));
        if (!float.IsFinite(intensity) || intensity <= 0f || MaximumComponent(color) <= 0.0001f)
            return false;
        radiance = Vector3.Clamp(color * Math.Clamp(intensity, 0f, 128f), Vector3.Zero, new Vector3(128f));
        return MaximumComponent(radiance) > 0.0001f;
    }

    public static IReadOnlyList<StaticLightingAreaEmitter> CreateAreaEmitterSamples(
        IReadOnlyList<StaticLightingTriangle> triangles, Vector3 radiance,
        StaticLightingEmissiveSettings settings, uint lightingChannelMask, ExportEntry source = null)
    {
        if (triangles.Count == 0 || !IsFinite(radiance) || MaximumComponent(radiance) <= 0f)
            return [];

        var sourceTriangles = new List<EmitterTriangle>(triangles.Count);
        float totalArea = 0f;
        Vector3 centroidMinimum = new(float.PositiveInfinity);
        Vector3 centroidMaximum = new(float.NegativeInfinity);
        foreach (StaticLightingTriangle triangle in triangles)
        {
            Vector3 cross = Vector3.Cross(triangle.B.Position - triangle.A.Position,
                triangle.C.Position - triangle.A.Position);
            float doubleArea = cross.Length();
            if (!float.IsFinite(doubleArea) || doubleArea <= 0.0001f) continue;
            float area = doubleArea * 0.5f;
            Vector3 centroid = (triangle.A.Position + triangle.B.Position + triangle.C.Position) / 3f;
            Vector3 normal = cross / doubleArea;
            sourceTriangles.Add(new EmitterTriangle(centroid, normal, area));
            totalArea += area;
            centroidMinimum = Vector3.Min(centroidMinimum, centroid);
            centroidMaximum = Vector3.Max(centroidMaximum, centroid);
        }
        float boost = float.IsFinite(settings.Boost) ? Math.Clamp(settings.Boost, 0f, 128f) : 0f;
        Vector3 boostedRadiance = Vector3.Clamp(radiance * boost, Vector3.Zero, new Vector3(128f));
        if (sourceTriangles.Count == 0 ||
            MaximumComponent(boostedRadiance) * totalArea < MinimumEmitterPower)
            return [];

        int desiredSamples = Math.Clamp((int)MathF.Ceiling(totalArea / TargetAreaPerSample), 1,
            Math.Min(MaximumSamplesPerSection, sourceTriangles.Count));
        Vector3 extents = centroidMaximum - centroidMinimum;
        int primaryAxis = LargestAxis(extents);
        int secondaryAxis = SecondLargestAxis(extents, primaryAxis);
        float primaryExtent = Axis(extents, primaryAxis);
        float secondaryExtent = Axis(extents, secondaryAxis);
        float aspect = secondaryExtent > 0.0001f ? primaryExtent / secondaryExtent : desiredSamples;
        int primaryCells = Math.Clamp((int)MathF.Round(MathF.Sqrt(desiredSamples * MathF.Max(1f, aspect))),
            1, desiredSamples);
        int secondaryCells = Math.Max(1, desiredSamples / primaryCells);
        var clusters = new Dictionary<int, EmitterAccumulator>();
        foreach (EmitterTriangle triangle in sourceTriangles)
        {
            int primaryCell = GetCell(Axis(triangle.Centroid, primaryAxis), Axis(centroidMinimum, primaryAxis),
                primaryExtent, primaryCells);
            int secondaryCell = GetCell(Axis(triangle.Centroid, secondaryAxis), Axis(centroidMinimum, secondaryAxis),
                secondaryExtent, secondaryCells);
            int key = secondaryCell * primaryCells + primaryCell;
            clusters.TryGetValue(key, out EmitterAccumulator accumulator);
            accumulator.Area += triangle.Area;
            accumulator.WeightedPosition += triangle.Centroid * triangle.Area;
            accumulator.WeightedNormal += triangle.Normal * triangle.Area;
            clusters[key] = accumulator;
        }

        var emitters = new List<StaticLightingAreaEmitter>(clusters.Count);
        foreach (EmitterAccumulator cluster in clusters.Values)
        {
            if (cluster.Area <= 0f || MaximumComponent(boostedRadiance) * cluster.Area < MinimumEmitterPower)
                continue;
            Vector3 position = cluster.WeightedPosition / cluster.Area;
            Vector3 normal = SafeNormal(cluster.WeightedNormal, Vector3.UnitZ);
            float radius = settings.ExplicitInfluenceRadius > 0f
                ? settings.ExplicitInfluenceRadius
                : MathF.Sqrt(MaximumComponent(boostedRadiance) * cluster.Area /
                             (MathF.PI * MinimumContribution));
            radius = Math.Clamp(radius, 64f, MaximumInfluenceRadius);
            emitters.Add(new StaticLightingAreaEmitter(position, normal, boostedRadiance, cluster.Area,
                radius, settings.FalloffExponent, lightingChannelMask, settings.TwoSided, source));
        }
        return emitters;
    }

    private static Vector3 ReadColor(StructProperty property, Vector3 fallback) => new(
        property.GetProp<FloatProperty>("R")?.Value ?? fallback.X,
        property.GetProp<FloatProperty>("G")?.Value ?? fallback.Y,
        property.GetProp<FloatProperty>("B")?.Value ?? fallback.Z);

    private static int ScoreColorParameter(string name, bool isUnlit)
    {
        if (name.Contains("Emiss", StringComparison.OrdinalIgnoreCase)) return 100;
        if (name.Contains("Glow", StringComparison.OrdinalIgnoreCase)) return 90;
        if (name.Contains("Neon", StringComparison.OrdinalIgnoreCase)) return 80;
        if (isUnlit && name.Equals("Color", StringComparison.OrdinalIgnoreCase)) return 50;
        if (isUnlit && name.Contains("Tint", StringComparison.OrdinalIgnoreCase)) return 40;
        return 0;
    }

    private static int ScoreScalarParameter(string name, bool isUnlit)
    {
        bool magnitude = name.Contains("Intensity", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("Brightness", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("Boost", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("Strength", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("Scale", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("Mult", StringComparison.OrdinalIgnoreCase);
        if (HasEmissiveToken(name) && magnitude) return 100;
        if (HasEmissiveToken(name)) return 80;
        if (isUnlit && magnitude) return 40;
        return 0;
    }

    private static bool HasEmissiveToken(string value) => !string.IsNullOrWhiteSpace(value) &&
        EmissiveTokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static int GetCell(float value, float minimum, float extent, int count)
    {
        if (count <= 1 || extent <= 0.0001f) return 0;
        return Math.Clamp((int)((value - minimum) / extent * count), 0, count - 1);
    }

    private static int LargestAxis(Vector3 value) => value.X >= value.Y && value.X >= value.Z ? 0 :
        value.Y >= value.Z ? 1 : 2;

    private static int SecondLargestAxis(Vector3 value, int largest) => largest switch
    {
        0 => value.Y >= value.Z ? 1 : 2,
        1 => value.X >= value.Z ? 0 : 2,
        _ => value.X >= value.Y ? 0 : 1
    };

    private static float Axis(Vector3 value, int axis) => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;
    private static float MaximumComponent(Vector3 value) => MathF.Max(value.X, MathF.Max(value.Y, value.Z));
    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) &&
                                                   float.IsFinite(value.Z);
    private static Vector3 SafeNormal(Vector3 value, Vector3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > 0.000001f && float.IsFinite(lengthSquared)
            ? value / MathF.Sqrt(lengthSquared)
            : fallback;
    }

    private readonly record struct EmitterTriangle(Vector3 Centroid, Vector3 Normal, float Area);
    private struct EmitterAccumulator
    {
        public Vector3 WeightedPosition;
        public Vector3 WeightedNormal;
        public float Area;
    }
}

/// <summary>Immutable BVH for receiver-level emissive sample culling.</summary>
public sealed class StaticLightingAreaEmitterIndex
{
    public const int MaximumEmittersPerReceiver = 24;
    private const int LeafSize = 8;
    private static readonly IComparer<StaticLightingAreaEmitter>[] AxisComparers =
    [
        Comparer<StaticLightingAreaEmitter>.Create(static (left, right) => left.Position.X.CompareTo(right.Position.X)),
        Comparer<StaticLightingAreaEmitter>.Create(static (left, right) => left.Position.Y.CompareTo(right.Position.Y)),
        Comparer<StaticLightingAreaEmitter>.Create(static (left, right) => left.Position.Z.CompareTo(right.Position.Z))
    ];
    private readonly StaticLightingAreaEmitter[] emitters;
    private readonly List<Node> nodes = [];

    public static StaticLightingAreaEmitterIndex Empty { get; } = new([]);
    public int Count => emitters.Length;
    public int NodeCount => nodes.Count;
    public double BuildMilliseconds { get; }

    public StaticLightingAreaEmitterIndex(IEnumerable<StaticLightingAreaEmitter> source)
    {
        Stopwatch timer = Stopwatch.StartNew();
        emitters = source?.ToArray() ?? [];
        if (emitters.Length > 0)
            BuildNode(0, emitters.Length);
        timer.Stop();
        BuildMilliseconds = timer.Elapsed.TotalMilliseconds;
    }

    public StaticLightingAreaEmitter[] Query(Vector3 receiverMinimum, Vector3 receiverMaximum,
        uint lightingChannelMask, ExportEntry excludedSource = null)
    {
        if (nodes.Count == 0)
            return [];
        var candidates = new List<Candidate>(MaximumEmittersPerReceiver);
        Span<int> stack = stackalloc int[128];
        int stackCount = 1;
        stack[0] = 0;
        while (stackCount > 0)
        {
            Node node = nodes[stack[--stackCount]];
            if (!Intersects(node.Minimum, node.Maximum, receiverMinimum, receiverMaximum))
                continue;
            if (node.Count > 0)
            {
                for (int index = node.Start; index < node.Start + node.Count; index++)
                {
                    StaticLightingAreaEmitter emitter = emitters[index];
                    if ((excludedSource is not null && emitter.Source == excludedSource) ||
                        !SceneLight.ChannelsOverlap(emitter.LightingChannelMask, lightingChannelMask))
                        continue;
                    float upperBound = CalculateContributionUpperBound(emitter, receiverMinimum, receiverMaximum);
                    if (upperBound >= StaticLightingEmissive.MinimumContribution)
                        RetainCandidate(candidates, new Candidate(emitter, upperBound));
                }
                continue;
            }
            stack[stackCount++] = node.Left;
            stack[stackCount++] = node.Right;
        }

        candidates.Sort(static (left, right) => right.UpperBound.CompareTo(left.UpperBound));
        var result = new StaticLightingAreaEmitter[candidates.Count];
        for (int index = 0; index < result.Length; index++)
            result[index] = candidates[index].Emitter;
        return result;
    }

    public static float CalculateContributionUpperBound(StaticLightingAreaEmitter emitter,
        Vector3 receiverMinimum, Vector3 receiverMaximum)
    {
        Vector3 closest = Vector3.Clamp(emitter.Position, receiverMinimum, receiverMaximum);
        float distanceSquared = Vector3.DistanceSquared(emitter.Position, closest);
        if (!float.IsFinite(distanceSquared) || distanceSquared > emitter.InfluenceRadius * emitter.InfluenceRadius)
            return 0f;
        float radiusSquared = emitter.InfluenceRadius * emitter.InfluenceRadius;
        if (distanceSquared >= radiusSquared)
            return 0f;
        float distance = MathF.Sqrt(MathF.Max(0f, distanceSquared));
        float falloffBase = MathF.Max(0f, 1f - distance / emitter.InfluenceRadius);
        float falloff = emitter.FalloffExponent == 2f
            ? falloffBase * falloffBase
            : emitter.FalloffExponent == 1f
                ? falloffBase
                : MathF.Pow(falloffBase, emitter.FalloffExponent);
        float solidAngle = emitter.Area / (MathF.PI * distanceSquared + emitter.Area);
        float radiance = MathF.Max(emitter.Radiance.X, MathF.Max(emitter.Radiance.Y, emitter.Radiance.Z));
        return radiance * solidAngle * falloff;
    }

    private static void RetainCandidate(List<Candidate> candidates, Candidate candidate)
    {
        if (candidates.Count < MaximumEmittersPerReceiver)
        {
            candidates.Add(candidate);
            return;
        }

        int weakestIndex = 0;
        float weakestContribution = candidates[0].UpperBound;
        for (int index = 1; index < candidates.Count; index++)
        {
            if (candidates[index].UpperBound >= weakestContribution) continue;
            weakestIndex = index;
            weakestContribution = candidates[index].UpperBound;
        }
        if (candidate.UpperBound > weakestContribution)
            candidates[weakestIndex] = candidate;
    }

    private int BuildNode(int start, int count)
    {
        CalculateBounds(start, count, out Vector3 minimum, out Vector3 maximum,
            out Vector3 centerMinimum, out Vector3 centerMaximum);
        int nodeIndex = nodes.Count;
        nodes.Add(default);
        if (count <= LeafSize)
        {
            nodes[nodeIndex] = new Node(minimum, maximum, -1, -1, start, count);
            return nodeIndex;
        }

        Vector3 extent = centerMaximum - centerMinimum;
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        Array.Sort(emitters, start, count, AxisComparers[axis]);
        int leftCount = count / 2;
        int left = BuildNode(start, leftCount);
        int right = BuildNode(start + leftCount, count - leftCount);
        nodes[nodeIndex] = new Node(minimum, maximum, left, right, 0, 0);
        return nodeIndex;
    }

    private void CalculateBounds(int start, int count, out Vector3 minimum, out Vector3 maximum,
        out Vector3 centerMinimum, out Vector3 centerMaximum)
    {
        minimum = centerMinimum = new Vector3(float.PositiveInfinity);
        maximum = centerMaximum = new Vector3(float.NegativeInfinity);
        for (int index = start; index < start + count; index++)
        {
            StaticLightingAreaEmitter emitter = emitters[index];
            Vector3 radius = new(emitter.InfluenceRadius);
            minimum = Vector3.Min(minimum, emitter.Position - radius);
            maximum = Vector3.Max(maximum, emitter.Position + radius);
            centerMinimum = Vector3.Min(centerMinimum, emitter.Position);
            centerMaximum = Vector3.Max(centerMaximum, emitter.Position);
        }
    }

    private static bool Intersects(Vector3 firstMinimum, Vector3 firstMaximum,
        Vector3 secondMinimum, Vector3 secondMaximum) =>
        firstMinimum.X <= secondMaximum.X && firstMaximum.X >= secondMinimum.X &&
        firstMinimum.Y <= secondMaximum.Y && firstMaximum.Y >= secondMinimum.Y &&
        firstMinimum.Z <= secondMaximum.Z && firstMaximum.Z >= secondMinimum.Z;

    private readonly record struct Candidate(StaticLightingAreaEmitter Emitter, float UpperBound);
    private readonly record struct Node(Vector3 Minimum, Vector3 Maximum, int Left, int Right, int Start, int Count);
}
