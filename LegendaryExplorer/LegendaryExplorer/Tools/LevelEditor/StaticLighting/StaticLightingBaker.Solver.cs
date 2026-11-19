using LegendaryExplorer.Lightmaps.Lightmass;
using LegendaryExplorer.Lightmaps.Solver;
using LegendaryExplorerCore.Packages;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// The light solver backends (<see cref="StaticLightingBakeBackend.Gpu"/>, <see cref="StaticLightingBakeBackend.Cpu"/>): the baker's samples, lights,
/// emitters and environment become one <see cref="SolverJob"/> (<see cref="SolverProfile.Scottina"/>) over the whole scene, which adds bounced light
/// and occludes the sky and environment; its output is packed into exactly the coefficients the classic backends write
/// </summary>
public sealed partial class StaticLightingBaker
{
    /// <summary>Replaces <see cref="LightSolver.Solve"/> (tests)</summary>
    internal Func<SolverJob, IProgress<SolverProgress>, CancellationToken, SolverOutput> SolveOverride { get; set; }

    /// <summary>Replaces rendering a material for its albedo (tests); null from it: the constant fallback</summary>
    internal Func<ExportEntry, Vector3?> AlbedoOverride { get; set; }

    /// <summary>The last job the light solver backends built (tests)</summary>
    internal SolverJob LastSolverJob { get; private set; }

    /// <summary>One target's part of the job</summary>
    private sealed class SolverReceiverPlan
    {
        public required StaticLightingMeshTarget Target { get; init; }
        public required int[] AffectingLights { get; init; }
        public required Guid[] LightGuids { get; init; }
        public required Guid[] IrrelevantLightGuids { get; init; }
        public required BakeCounters Counters { get; init; }
        public StaticLightingSurfaceSample[] Samples = [];
        /// <summary>Texture mappings: the texel of each sample</summary>
        public int[] Texels;
        public bool[] Mapped;
        public int Resolution;
        public Vector2 CoordinateScale, CoordinateBias;
        public int WorkUnitCount = 1;
        public int FirstSample;
        public long PreparationTicks;
    }

    private StaticLightingBakeResult BakeWithLightSolver((Vector3 Minimum, Vector3 Maximum)[] targetBounds,
        Stopwatch bakeTimer, string progressMode, CancellationToken cancellationToken, IProgress<string> progress,
        IProgress<StaticLightingBuildProgress> detailedProgress)
    {
        int workerCount = settings.EffectiveWorkerThreads;
        (int outerWorkerCount, int componentWorkerCount) = CalculateBakeConcurrency(targets, workerCount);

        // Samples, as the classic backends make them
        var plans = new SolverReceiverPlan[targets.Count];
        int prepared = 0;
        Parallel.For(0, targets.Count, CreateParallelOptions(cancellationToken, outerWorkerCount), index =>
        {
            long start = Stopwatch.GetTimestamp();
            StaticLightingMeshTarget target = targets[index];
            SolverReceiverPlan plan = CreateSolverReceiverPlan(target, targetBounds[index]);
            if (target.UseTextureMapping)
                RasterizeSolverTextureSamples(plan, cancellationToken, componentWorkerCount);
            else
                CreateSolverVertexSamples(plan);
            plan.PreparationTicks = Stopwatch.GetTimestamp() - start;
            plans[index] = plan;
            ReportBuildProgress(detailedProgress, progressMode, "Preparing light solver samples",
                Interlocked.Increment(ref prepared), targets.Count, target.Component);
        });

        cancellationToken.ThrowIfCancellationRequested();
        string albedoSummary;
        SolverJob job = BuildSolverJob(plans, progressMode, detailedProgress, cancellationToken, out albedoSummary);
        LastSolverJob = job;

        var solverProgress = new SolverProgressAdapter(detailedProgress, progressMode);
        SolverOutput output;
        try
        {
            output = SolveOverride is { } solve
                ? solve(job, solverProgress, cancellationToken)
                : LightSolver.Solve(job, solverProgress, cancellationToken);
        }
        catch (SolverUnavailableException exception)
        {
            throw new InvalidOperationException(
                $"The {StaticLightingBakeBackends.DisplayName(settings.Backend)} can't run: {exception.Message} " +
                $"Choose the \"{StaticLightingBakeBackends.ClassicDisplayName}\" device to bake without it.", exception);
        }
        ValidateSolverOutput(output, job.Samples.Count);

        // Packing, filtering and encoding per receiver
        var results = new StaticLightingComponentBake[targets.Count];
        int textureMapped = 0, vertexMapped = 0, workUnitCount = 0, finished = 0;
        Parallel.For(0, plans.Length, CreateParallelOptions(cancellationToken, outerWorkerCount), index =>
        {
            SolverReceiverPlan plan = plans[index];
            StaticLightingMeshTarget target = plan.Target;
            Stopwatch componentTimer = Stopwatch.StartNew();
            bool compressed = UsesCompressedDirectionalLightMap(target.Component.Game);
            int coefficientCount = compressed ? 3 : 4;
            int length = target.UseTextureMapping ? plan.Resolution * plan.Resolution : target.Vertices.Count;
            var coefficients = Enumerable.Range(0, coefficientCount).Select(_ => new Vector3[length]).ToArray();
            BakeCounters counters = plan.Counters;
            for (int local = 0; local < plan.Samples.Length; local++)
            {
                int sample = plan.FirstSample + local;
                int destination = plan.Texels?[local] ?? local;
                StaticLightingSolverConversion.PackCoefficients(output.Simple[sample], output.Directional0[sample],
                    output.Directional1[sample], output.Directional2[sample], 0f, compressed, coefficients, destination);
                Vector3 indirect = output.IndirectSimple?[sample] ?? Vector3.Zero;
                counters.DirectContributionMicroSum += ToMicro(MaxComponent(output.Simple[sample] - indirect));
                counters.EnvironmentContributionMicroSum += ToMicro(MaxComponent(indirect));
            }
            counters.LitSampleCount += plan.Samples.Length;

            if (target.UseTextureMapping)
            {
                string ReceiverPhase(string phase) => $"{phase} - receiver {index + 1:N0}/{plans.Length:N0}";
                StaticLightingTextureBake texture = FinishTextureBake(target, coefficients, plan.Mapped,
                    plan.Resolution, plan.CoordinateScale, plan.CoordinateBias, plan.WorkUnitCount, counters,
                    cancellationToken, componentWorkerCount, detailedProgress, progressMode, ReceiverPhase);
                componentTimer.Stop();
                results[index] = new StaticLightingComponentBake
                {
                    Target = target,
                    LightGuids = plan.LightGuids,
                    IrrelevantLightGuids = plan.IrrelevantLightGuids,
                    Texture = texture,
                    Diagnostics = counters.CreateDiagnostics(target.MappingDiagnostics,
                        componentTimer.Elapsed.TotalMilliseconds + TicksToMilliseconds(plan.PreparationTicks))
                };
                Interlocked.Increment(ref textureMapped);
                Interlocked.Add(ref workUnitCount, texture.WorkUnitCount);
            }
            else
            {
                StaticLightingVertexBake vertex = FinishVertexBake(target, coefficients, compressed, counters);
                componentTimer.Stop();
                results[index] = new StaticLightingComponentBake
                {
                    Target = target,
                    LightGuids = plan.LightGuids,
                    IrrelevantLightGuids = plan.IrrelevantLightGuids,
                    Vertex = vertex,
                    Diagnostics = counters.CreateDiagnostics(target.MappingDiagnostics,
                        componentTimer.Elapsed.TotalMilliseconds + TicksToMilliseconds(plan.PreparationTicks))
                };
                Interlocked.Increment(ref vertexMapped);
                Interlocked.Add(ref workUnitCount, Math.Max(1, (target.Vertices.Count + 255) / 256));
            }
            ReportBuildProgress(detailedProgress, progressMode, "Encoding light solver lightmaps",
                Interlocked.Increment(ref finished), plans.Length, target.Component);
        });

        bakeTimer.Stop();
        SolverStatistics statistics = output.Statistics ?? new SolverStatistics();
        string summary = $"{StaticLightingBakeBackends.DisplayName(settings.Backend)} on {statistics.DeviceName ?? "an unnamed device"}: " +
                         $"{job.Samples.Count:N0} samples, {job.Scene.Triangles.Count:N0} triangles, {job.Scene.Lights.Count:N0} lights, " +
                         $"{settings.SolverBounces:N0} bounce(s), {settings.SolverGatherRays:N0} gather rays; " +
                         $"BVH / direct / indirect / total {statistics.BvhTime.TotalSeconds:F2}s / {statistics.DirectTime.TotalSeconds:F2}s / " +
                         $"{statistics.IndirectTime.TotalSeconds:F2}s / {statistics.TotalTime.TotalSeconds:F2}s; {albedoSummary}";
        progress?.Report(summary);

        StaticLightingComponentDiagnostics[] diagnostics = results.Select(result => result.Diagnostics).ToArray();
        return new StaticLightingBakeResult
        {
            Components = results,
            SourceTriangleCount = collision.TriangleCount,
            LightCount = lights.Count,
            EmissiveEmitterCount = emissiveEmitterIndex.Count,
            TextureMappedComponentCount = textureMapped,
            VertexMappedComponentCount = vertexMapped,
            UvFallbackComponentCount = results.Count(result =>
                result.Vertex is not null && result.Diagnostics.Mapping.HasTextureMappingErrors),
            WorkUnitCount = workUnitCount,
            WorkerCount = workerCount,
            RaysCast = statistics.RaysCast,
            AverageVisibility = 1d,
            AverageDirectContribution = diagnostics.Length == 0 ? 0d : diagnostics.Average(item => item.AverageDirectContribution),
            AverageEnvironmentContribution = diagnostics.Length == 0 ? 0d : diagnostics.Average(item => item.AverageEnvironmentContribution),
            BakeMilliseconds = bakeTimer.Elapsed.TotalMilliseconds,
            SceneDiagnostics = sceneDiagnostics,
            TextureRasterizationMilliseconds = diagnostics.Sum(item => item.TextureRasterizationMilliseconds),
            DirectLightingMilliseconds = (statistics.DirectTime + statistics.IndirectTime).TotalMilliseconds,
            FilteringMilliseconds = diagnostics.Sum(item => item.FilteringMilliseconds),
            TextureConstructionMilliseconds = diagnostics.Sum(item => item.TextureConstructionMilliseconds),
            Backend = settings.Backend,
            LightSolverSummary = summary
        };
    }

    private SolverReceiverPlan CreateSolverReceiverPlan(StaticLightingMeshTarget target,
        (Vector3 Minimum, Vector3 Maximum) bounds)
    {
        // The same light selection as the classic backends: these lights' direct light reaches this receiver
        var affecting = new bool[lights.Count];
        if (target.AffectingLightIndices is { } nativeLightIndices)
        {
            foreach (int lightIndex in nativeLightIndices)
            {
                if ((uint)lightIndex >= (uint)lights.Count)
                    throw new InvalidOperationException("The native scene scan returned an invalid light index.");
                affecting[lightIndex] = true;
            }
        }
        else
        {
            for (int lightIndex = 0; lightIndex < lights.Count; lightIndex++)
                affecting[lightIndex] = LightCanAffect(lights[lightIndex], target.LightingChannelMask,
                    target.Vertices.Count > 0, bounds.Minimum, bounds.Maximum);
        }
        int[] affectingLights = Enumerable.Range(0, lights.Count).Where(index => affecting[index]).ToArray();
        Guid[] lightGuids = affectingLights.Select(index => lights[index].Guid).Distinct().ToArray();
        var affectingLightGuids = lightGuids.ToHashSet();
        Guid[] irrelevantLightGuids = Enumerable.Range(0, lights.Count).Where(index => !affecting[index])
            .Select(index => lights[index].Guid)
            .Where(guid => !affectingLightGuids.Contains(guid))
            .Distinct().ToArray();
        return new SolverReceiverPlan
        {
            Target = target,
            AffectingLights = affectingLights,
            LightGuids = lightGuids,
            IrrelevantLightGuids = irrelevantLightGuids,
            Counters = new BakeCounters()
        };
    }

    /// <summary>A texture mapping's texels, rasterized as <see cref="BakeTexture"/> rasterizes them</summary>
    private void RasterizeSolverTextureSamples(SolverReceiverPlan plan, CancellationToken cancellationToken, int workerCount)
    {
        StaticLightingMeshTarget target = plan.Target;
        int resolution = target.TextureResolution > 0 ? target.TextureResolution : settings.TextureResolution;
        int pixelCount = resolution * resolution;
        var mapped = new bool[pixelCount];
        var mappingConflicts = new bool[pixelCount];
        var triangleOwners = new int[pixelCount];
        Array.Fill(triangleOwners, -1);
        Vector2 coordinateScale = new((resolution - 2f) / resolution);
        Vector2 coordinateBias = new(1f / resolution);
        var samples = new StaticLightingSurfaceSample[pixelCount];
        StaticLightingBakeTile[] tiles = TextureTileCache.GetOrAdd((resolution, settings.WorkTileSize), key =>
            CreateTextureWorkTiles(key.Resolution, key.TileSize).ToArray());
        List<int>[] triangleBuckets = BuildTileTriangleBuckets(target.Triangles, tiles, resolution,
            coordinateScale, coordinateBias, settings.WorkTileSize);
        var geometricNormals = new Vector3[target.Triangles.Count];
        var worldUnitsPerTexel = new float[target.Triangles.Count];
        for (int triangleIndex = 0; triangleIndex < target.Triangles.Count; triangleIndex++)
        {
            StaticLightingTriangle triangle = target.Triangles[triangleIndex];
            geometricNormals[triangleIndex] = SafeNormal(Vector3.Cross(
                triangle.B.Position - triangle.A.Position, triangle.C.Position - triangle.A.Position),
                Vector3.UnitZ);
            worldUnitsPerTexel[triangleIndex] = EstimateTriangleWorldUnitsPerTexel(triangle, resolution);
        }
        int[] activeTiles = Enumerable.Range(0, tiles.Length)
            .Where(index => triangleBuckets[index].Count > 0).ToArray();
        int conflicts = 0;
        long rasterizationTicks = 0;
        Parallel.ForEach(activeTiles, CreateParallelOptions(cancellationToken, workerCount), tileIndex =>
        {
            long start = Stopwatch.GetTimestamp();
            int localConflicts = 0;
            foreach (int triangleIndex in triangleBuckets[tileIndex])
                localConflicts += RasterizeTriangle(target.Triangles[triangleIndex], triangleIndex, resolution,
                    coordinateScale, coordinateBias, tiles[tileIndex], target.Component,
                    geometricNormals[triangleIndex], worldUnitsPerTexel[triangleIndex], samples,
                    triangleOwners, mapped, mappingConflicts);
            Interlocked.Add(ref conflicts, localConflicts);
            Interlocked.Add(ref rasterizationTicks, Stopwatch.GetTimestamp() - start);
        });

        var texels = new List<int>();
        for (int pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
            if (mapped[pixelIndex]) texels.Add(pixelIndex);
        plan.Texels = texels.ToArray();
        plan.Samples = plan.Texels.Select(pixelIndex => samples[pixelIndex]).ToArray();
        plan.Mapped = mapped;
        plan.Resolution = resolution;
        plan.CoordinateScale = coordinateScale;
        plan.CoordinateBias = coordinateBias;
        plan.WorkUnitCount = Math.Max(1, activeTiles.Length);
        plan.Counters.MappedTexels = plan.Texels.Length;
        plan.Counters.MappingConflictTexels = conflicts;
        plan.Counters.TextureRasterizationTicks = rasterizationTicks;
    }

    /// <summary>A vertex mapping's samples, as <see cref="BakeVertices"/> makes them</summary>
    private static void CreateSolverVertexSamples(SolverReceiverPlan plan)
    {
        StaticLightingMeshTarget target = plan.Target;
        float vertexShadowScale = EstimateVertexShadowScale(target);
        var samples = new StaticLightingSurfaceSample[target.Vertices.Count];
        for (int vertexIndex = 0; vertexIndex < samples.Length; vertexIndex++)
        {
            StaticLightingVertex vertex = target.Vertices[vertexIndex];
            samples[vertexIndex] = new StaticLightingSurfaceSample(vertex.Position, vertex.Normal, vertex.Tangent,
                vertex.Bitangent, vertex.Normal, target.Component, -1, vertexShadowScale);
        }
        plan.Samples = samples;
    }

    private SolverJob BuildSolverJob(SolverReceiverPlan[] plans, string progressMode,
        IProgress<StaticLightingBuildProgress> detailedProgress, CancellationToken cancellationToken,
        out string albedoSummary)
    {
        var scene = new SolverScene
        {
            Environment = StaticLightingSolverConversion.ConvertEnvironment(settings.AmbientIntensity)
        };
        foreach (StaticLightingLight light in lights)
            scene.Lights.Add(StaticLightingSolverConversion.ConvertLight(light, settings));

        // Instance 0 is geometry of no known component
        var instances = new Dictionary<ExportEntry, uint>(ReferenceEqualityComparer.Instance);
        uint InstanceOf(ExportEntry source)
        {
            if (source is null) return 0;
            if (!instances.TryGetValue(source, out uint instance))
                instances.Add(source, instance = (uint)instances.Count + 1);
            return instance;
        }

        if (solverGeometry is not null)
            AddSolverGeometry(scene, InstanceOf, progressMode, detailedProgress, cancellationToken, out albedoSummary);
        else
            AddFallbackSolverGeometry(scene, InstanceOf, out albedoSummary);

        var job = new SolverJob
        {
            Scene = scene,
            Settings = StaticLightingSolverConversion.CreateSettings(settings),
            Grids = new SolverSampleGrid[plans.Length]
        };
        int sampleCount = plans.Sum(plan => plan.Samples.Length);
        job.Samples.Capacity = sampleCount;
        for (int receiverIndex = 0; receiverIndex < plans.Length; receiverIndex++)
        {
            SolverReceiverPlan plan = plans[receiverIndex];
            job.Receivers.Add(new SolverReceiver
            {
                LightmapLights = plan.AffectingLights,
                ShadowMapLights = [],
                TwoSidedLighting = false,
                GatherIndirect = true
            });
            uint instance = InstanceOf(plan.Target.Component);
            plan.FirstSample = job.Samples.Count;
            foreach (StaticLightingSurfaceSample sample in plan.Samples)
                job.Samples.Add(StaticLightingSolverConversion.CreateSample(sample, instance, (uint)receiverIndex));
            if (plan.Texels is not null)
            {
                var grid = new SolverSampleGrid { Width = plan.Resolution, Height = plan.Resolution, Samples = new int[plan.Resolution * plan.Resolution] };
                Array.Fill(grid.Samples, -1);
                for (int local = 0; local < plan.Texels.Length; local++)
                    grid.Samples[plan.Texels[local]] = plan.FirstSample + local;
                job.Grids[receiverIndex] = grid;
            }
        }
        return job;
    }

    /// <summary>
    /// The shadow casters and emissive sections from <see cref="BuildScene"/>: shadow casters block every ray and shadow from both sides, as the
    /// classic backends' rays do; an emissive section that doesn't cast shadows only blocks (and so is seen by) gather rays, so its light
    /// reaches receivers as emission from its real surface instead of the classic backends' area samples
    /// </summary>
    private void AddSolverGeometry(SolverScene scene, Func<ExportEntry, uint> instanceOf, string progressMode,
        IProgress<StaticLightingBuildProgress> detailedProgress, CancellationToken cancellationToken,
        out string albedoSummary)
    {
        Dictionary<ExportEntry, Vector3> albedos = CaptureAlbedos(solverGeometry.Surfaces
                .SelectMany(surface => surface.SectionMaterials).Where(material => material is not null),
            progressMode, detailedProgress, cancellationToken, out albedoSummary);
        var materials = new Dictionary<(ExportEntry Material, Vector3 Emission, bool TwoSided), uint>();
        uint MaterialOf(ExportEntry material, Vector3? emission, bool twoSided)
        {
            var key = (material, emission ?? Vector3.Zero, emission.HasValue && twoSided);
            if (materials.TryGetValue(key, out uint index)) return index;
            Vector3 albedo = material is not null && albedos.TryGetValue(material, out Vector3 captured)
                ? captured
                : new Vector3(StaticLightingSolverConversion.FallbackAlbedo);
            index = (uint)scene.Materials.Count;
            scene.Materials.Add(new SolverMaterial
            {
                Diffuse = SolverTexture.Constant(new Vector4(albedo, 1f)),
                Emissive = emission is { } radiance ? SolverTexture.Constant(new Vector4(radiance * StaticLightingSolverConversion.GatheredRadianceScale, 1f)) : null,
                TwoSided = key.Item3
            });
            materials.Add(key, index);
            return index;
        }

        int surfaceIndex = 0;
        foreach (StaticLightingSolverSurface surface in solverGeometry.Surfaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportBuildProgress(detailedProgress, progressMode, "Building the light solver's scene",
                ++surfaceIndex, solverGeometry.Surfaces.Count, surface.Component);
            uint instance = instanceOf(surface.Component);
            foreach (StaticLightingTriangle triangle in surface.Triangles)
            {
                int section = triangle.SectionIndex;
                Vector3? emission = surface.SectionEmission is { } sectionEmission && (uint)section < sectionEmission.Length
                    ? sectionEmission[section]
                    : null;
                SolverTriangleFlags flags;
                if (surface.CastsShadow)
                    flags = SolverTriangleFlags.CastShadow | SolverTriangleFlags.TwoSidedShadow;
                else if (emission.HasValue)
                    flags = SolverTriangleFlags.CastShadow | SolverTriangleFlags.ShadowIndirectOnly | SolverTriangleFlags.TwoSidedShadow;
                else
                    continue;
                ExportEntry material = (uint)section < surface.SectionMaterials.Length ? surface.SectionMaterials[section] : null;
                AddSolverTriangle(scene, triangle.A.Position, triangle.B.Position, triangle.C.Position,
                    triangle.A.Normal, triangle.B.Normal, triangle.C.Normal,
                    MaterialOf(material, emission, surface.TwoSidedEmission), instance, flags);
            }
        }
    }

    /// <summary>
    /// Without <see cref="BuildScene"/>'s geometry: the collision's occluders with the constant albedo, and each emissive area sample as an
    /// emissive square of its area and normal (which lights a distant receiver as the sample does)
    /// </summary>
    private void AddFallbackSolverGeometry(SolverScene scene, Func<ExportEntry, uint> instanceOf, out string albedoSummary)
    {
        albedoSummary = $"constant albedo {StaticLightingSolverConversion.FallbackAlbedo:F2} (no material data)";
        scene.Materials.Add(new SolverMaterial
        {
            Diffuse = SolverTexture.Constant(new Vector4(new Vector3(StaticLightingSolverConversion.FallbackAlbedo), 1f))
        });
        foreach (LevelCollisionTriangle triangle in collision.StaticLightingTriangles)
        {
            Vector3 b = triangle.A + triangle.Edge1, c = triangle.A + triangle.Edge2;
            Vector3 normal = SafeNormal(Vector3.Cross(triangle.Edge1, triangle.Edge2), Vector3.UnitZ);
            AddSolverTriangle(scene, triangle.A, b, c, normal, normal, normal, 0, instanceOf(triangle.Source),
                SolverTriangleFlags.CastShadow | SolverTriangleFlags.TwoSidedShadow);
        }
        foreach (StaticLightingAreaEmitter emitter in emissiveEmitterIndex.Emitters)
        {
            if (emitter.Area <= 0f) continue;
            uint material = (uint)scene.Materials.Count;
            scene.Materials.Add(new SolverMaterial
            {
                Diffuse = SolverTexture.Constant(Vector4.Zero),
                Emissive = SolverTexture.Constant(new Vector4(emitter.Radiance * StaticLightingSolverConversion.GatheredRadianceScale, 1f)),
                TwoSided = emitter.TwoSided
            });
            Vector3 normal = SafeNormal(emitter.Normal, Vector3.UnitZ);
            CreateOrthonormalBasis(normal, out Vector3 right, out Vector3 up);
            float halfSide = MathF.Sqrt(emitter.Area) * 0.5f;
            // Lifted off the emitting mesh so its own triangles don't hide it
            Vector3 center = emitter.Position + normal * settings.ShadowBias;
            Vector3 p0 = center - right * halfSide - up * halfSide, p1 = center + right * halfSide - up * halfSide,
                p2 = center + right * halfSide + up * halfSide, p3 = center - right * halfSide + up * halfSide;
            var flags = SolverTriangleFlags.CastShadow | SolverTriangleFlags.ShadowIndirectOnly | SolverTriangleFlags.TwoSidedShadow;
            uint instance = instanceOf(emitter.Source);
            AddSolverTriangle(scene, p0, p1, p2, normal, normal, normal, material, instance, flags);
            AddSolverTriangle(scene, p0, p2, p3, normal, normal, normal, material, instance, flags);
        }
    }

    private static void AddSolverTriangle(SolverScene scene, Vector3 a, Vector3 b, Vector3 c, Vector3 normalA,
        Vector3 normalB, Vector3 normalC, uint material, uint instance, SolverTriangleFlags flags)
    {
        Vector3 cross = Vector3.Cross(b - a, c - a);
        float lengthSquared = cross.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared < 0.0001f)
            return;
        Vector3 geometric = cross / MathF.Sqrt(lengthSquared);
        uint first = (uint)scene.Vertices.Count;
        scene.Vertices.Add(new SolverVertex { Position = a, Normal = SafeNormal(normalA, geometric) });
        scene.Vertices.Add(new SolverVertex { Position = b, Normal = SafeNormal(normalB, geometric) });
        scene.Vertices.Add(new SolverVertex { Position = c, Normal = SafeNormal(normalC, geometric) });
        scene.Triangles.Add(new SolverTriangle
        {
            V0 = first,
            V1 = first + 1,
            V2 = first + 2,
            Material = material,
            Instance = instance,
            Flags = flags
        });
    }

    /// <summary>
    /// Each material's albedo: its diffuse rendered with the game's shaders (<see cref="LightmassMaterialCapture"/>, as the UDK Lightmass Baker
    /// renders it) and averaged, since the baker's triangles carry no material UVs. A material that can't be rendered gets the constant albedo
    /// </summary>
    private Dictionary<ExportEntry, Vector3> CaptureAlbedos(IEnumerable<ExportEntry> materials, string progressMode,
        IProgress<StaticLightingBuildProgress> detailedProgress, CancellationToken cancellationToken, out string summary)
    {
        var albedos = new Dictionary<ExportEntry, Vector3>(ReferenceEqualityComparer.Instance);
        ExportEntry[] unique = materials.Distinct<ExportEntry>(ReferenceEqualityComparer.Instance).ToArray();
        string fallback = $"constant albedo {StaticLightingSolverConversion.FallbackAlbedo:F2}";
        if (unique.Length == 0)
        {
            summary = $"no materials ({fallback})";
            return albedos;
        }

        LightmassMaterialCapture capture = null;
        string captureError = null;
        if (AlbedoOverride is null)
        {
            try
            {
                capture = new LightmassMaterialCapture();
            }
            catch (Exception exception)
            {
                captureError = exception.Message;
            }
        }
        int rendered = 0;
        try
        {
            for (int index = 0; index < unique.Length && (capture is not null || AlbedoOverride is not null); index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportBuildProgress(detailedProgress, progressMode, "Rendering material albedo for bounced light",
                    index + 1, unique.Length, unique[index]);
                try
                {
                    Vector3? albedo = AlbedoOverride is { } albedoOverride
                        ? albedoOverride(unique[index])
                        : capture.Capture(unique[index], [StaticLightingSolverConversion.AlbedoCaptureSize])
                            .TryGetValue(StaticLightingSolverConversion.AlbedoCaptureSize, out CapturedMaterial captured)
                            ? StaticLightingSolverConversion.AverageAlbedo(captured)
                            : null;
                    if (albedo is { } value)
                    {
                        albedos[unique[index]] = value;
                        rendered++;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Not renderable with the game's shaders: the constant albedo
                }
            }
        }
        finally
        {
            capture?.Dispose();
        }
        summary = captureError is not null
            ? $"materials can't be rendered ({captureError}): {fallback}"
            : $"albedo of {rendered:N0}/{unique.Length:N0} materials rendered, {fallback} for the rest";
        return albedos;
    }

    private static void ValidateSolverOutput(SolverOutput output, int sampleCount)
    {
        if (output is null)
            throw new InvalidOperationException("The light solver returned no output.");
        if (output.Simple?.Length != sampleCount || output.Directional0?.Length != sampleCount ||
            output.Directional1?.Length != sampleCount || output.Directional2?.Length != sampleCount)
            throw new InvalidOperationException(
                $"The light solver returned lighting for the wrong number of samples (expected {sampleCount:N0}).");
        if (output.IndirectSimple is not null && output.IndirectSimple.Length != sampleCount)
            output.IndirectSimple = null;
    }

    /// <summary>The solver's progress as the baker's detailed progress (thousandths of the solve)</summary>
    private sealed class SolverProgressAdapter(IProgress<StaticLightingBuildProgress> progress, string mode)
        : IProgress<SolverProgress>
    {
        public void Report(SolverProgress value) => ReportBuildProgress(progress, mode,
            $"Light solver: {value.Stage}", (int)Math.Round(Math.Clamp(value.Fraction, 0d, 1d) * 1000d), 1000);
    }
}
