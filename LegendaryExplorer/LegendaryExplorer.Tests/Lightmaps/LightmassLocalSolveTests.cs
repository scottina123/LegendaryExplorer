using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using LegendaryExplorer.Lightmaps.Codec;
using LegendaryExplorer.Lightmaps.Lightmass;
using LegendaryExplorer.Lightmaps.Solver;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace LegendaryExplorer.Tests.Lightmaps;

[TestClass]
public class LightmassLocalSolveTests
{
    // ================= rasteriser =================

    [TestMethod]
    public void Rasterizer_PaddedQuadFillsTheInteriorOnly()
    {
        Vector2[] uv = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        var texels = uv.Select(u => LightmassTexelRasterizer.UvToTexel(u, 8, 8, padded: true)).ToArray();
        var raster = LightmassTexelRasterizer.Rasterize(8, 8, texels, [0, 1, 2, 0, 2, 3]);
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            bool interior = x is >= 1 and <= 6 && y is >= 1 and <= 6;
            Assert.AreEqual(interior, raster.IsCovered(y * 8 + x), $"texel ({x}, {y})");
        }
        // Interior texels' centres are inside the quad, so they sample exactly there
        for (int y = 1; y <= 6; y++)
        for (int x = 1; x <= 6; x++)
        {
            int i = y * 8 + x;
            var t = raster.Triangle[i];
            var b = raster.Barycentric[i];
            var p = texels[new[] { 0, 1, 2, 0, 2, 3 }[t * 3]] * b.X + texels[new[] { 0, 1, 2, 0, 2, 3 }[t * 3 + 1]] * b.Y + texels[new[] { 0, 1, 2, 0, 2, 3 }[t * 3 + 2]] * b.Z;
            Assert.AreEqual(x + 0.5f, p.X, 1e-4f);
            Assert.AreEqual(y + 0.5f, p.Y, 1e-4f);
        }
    }

    [TestMethod]
    public void Rasterizer_CoversTexelsEdgesPassThroughConservatively()
    {
        // A sliver along the first row: no texel centre is inside it, but every texel of the row is overlapped
        Vector2[] texels = [new(0.1f, 0.1f), new(5.9f, 0.2f), new(0.1f, 0.4f)];
        var raster = LightmassTexelRasterizer.Rasterize(6, 6, texels, [0, 1, 2]);
        for (int x = 0; x < 6; x++) Assert.IsTrue(raster.IsCovered(x), $"texel ({x}, 0)");
        for (int i = 6; i < 36; i++) Assert.IsFalse(raster.IsCovered(i), $"texel {i}");
        // Each sample point is on the triangle
        for (int x = 0; x < 6; x++)
        {
            var b = raster.Barycentric[x];
            Assert.IsTrue(b.X >= -1e-5f && b.Y >= -1e-5f && b.Z >= -1e-5f, $"texel {x}: {b}");
            Assert.AreEqual(1f, b.X + b.Y + b.Z, 1e-4f);
        }
    }

    [TestMethod]
    public void Rasterizer_TouchingIsNotOverlapping()
    {
        // A triangle whose edge lies on the line between texel columns 1 and 2 doesn't cover column 2
        Assert.IsFalse(LightmassTexelRasterizer.Overlaps(new(0, 0), new(2, 0), new(2, 2), 2, 0));
        Assert.IsTrue(LightmassTexelRasterizer.Overlaps(new(0, 0), new(2.01f, 0), new(2.01f, 2), 2, 0));
        // A texel beyond the hypotenuse but within the bounding box
        Assert.IsFalse(LightmassTexelRasterizer.Overlaps(new(0, 0), new(4, 0), new(0, 4), 3, 3));
        Assert.IsTrue(LightmassTexelRasterizer.Overlaps(new(0, 0), new(4, 0), new(0, 4), 2, 1));
    }

    [TestMethod]
    public void Rasterizer_UnpaddedMapsUvOntoTheWholeMapping()
    {
        Assert.AreEqual(new Vector2(0, 0), LightmassTexelRasterizer.UvToTexel(Vector2.Zero, 9, 9, padded: false));
        Assert.AreEqual(new Vector2(9, 4.5f), LightmassTexelRasterizer.UvToTexel(new Vector2(1, 0.5f), 9, 9, padded: false));
        Assert.AreEqual(new Vector2(1, 7), LightmassTexelRasterizer.UvToTexel(new Vector2(0, 1), 8, 8, padded: true));
        Assert.AreEqual(new Vector2(3, 21), LightmassTexelRasterizer.UvToTexel(new Vector2(0, 1), 8, 8, padded: true, upsample: 3));
    }

    [TestMethod]
    public void Samples_InterpolateOnTheSurfaceWithTheTexelsWorldSize()
    {
        // A 100×100 quad facing +Z, its lightmap UVs 0..1, on a padded 8×8 mapping: 6 interior texels of 100/6 units each
        LightmassGeoVertex V(float x, float y) => new()
        {
            Position = new Vector3(x * 100, y * 100, 0), TangentX = Vector3.UnitX, TangentY = Vector3.UnitY, TangentZ = Vector3.UnitZ,
            LightmapUV = new Vector2(x, y),
        };
        var material = new LightmassMaterial { Diffuse = MaterialSamples.Constant(new Vector3(0.5f), 0) };
        var mesh = new LightmassGeoMesh
        {
            Vertices = [V(0, 0), V(1, 0), V(1, 1), V(0, 1)], Indices = [0, 1, 2, 0, 2, 3], TriangleElements = [0, 0], Instance = 7,
            Elements = [new LightmassGeoElement(material, new LightmassElementSettings { TwoSidedLighting = true }, true, false, false)],
        };
        var texels = mesh.Vertices.Select(v => LightmassTexelRasterizer.UvToTexel(v.LightmapUV, 8, 8, padded: true)).ToArray();
        var samples = new List<SolverSample>();
        var twoSided = new List<bool>();
        var local = LightmassLocalSolve.TextureSamples(mesh, texels, 8, 8, 0.1f, samples, twoSided);
        Assert.AreEqual(36, samples.Count);
        Assert.AreEqual(-1, local[0]);
        var s = samples[local[1 * 8 + 1]];
        Assert.AreEqual(100f / 12, s.Position.X, 1e-3f);
        Assert.AreEqual(100f / 12, s.Position.Y, 1e-3f);
        Assert.AreEqual(Vector3.UnitZ, s.Normal);
        Assert.AreEqual(Vector3.UnitZ, s.GeometricNormal);
        Assert.AreEqual(Vector3.UnitX, s.Tangent);
        Assert.AreEqual(Vector3.UnitY, s.Bitangent);
        Assert.AreEqual(7u, s.Instance);
        Assert.AreEqual(0.5f * MathF.Sqrt(2) * 100 / 6, s.TexelRadius, 1e-3f);
        Assert.IsTrue(twoSided.All(t => t));

        // In the solver's scene, triangles are wound to face their normals' side: (V1 - V0) × (V2 - V0) along +Z
        var scene = new SolverScene();
        mesh.Indices = [0, 2, 1, 0, 3, 2];
        mesh.AddTo(scene, _ => 0);
        Assert.AreEqual(2, scene.Triangles.Count);
        foreach (var t in scene.Triangles)
        {
            var a = scene.Vertices[(int)t.V0].Position;
            var n = Vector3.Cross(scene.Vertices[(int)t.V1].Position - a, scene.Vertices[(int)t.V2].Position - a);
            Assert.IsTrue(n.Z > 0);
            Assert.AreEqual(SolverTriangleFlags.CastShadow, t.Flags);
            Assert.AreEqual(7u, t.Instance);
        }
    }

    // ================= lights =================

    static LightmassLight PointLight(uint flags, (byte, byte, byte) color, float brightness) => new()
    {
        Light = new SceneLight(new Vector3(0, 0, 100), 500, Vector3.One, 1, false, Vector3.UnitX, 0, 44),
        Guid = Guid.NewGuid(), Flags = flags, Color = color, Brightness = brightness, LightSourceRadius = 10, IndirectLightingScale = 1, IndirectLightingSaturation = 1,
    };

    [TestMethod]
    public void LightColor_IsUe3sPow22TableTimesBrightness()
    {
        Assert.AreEqual(0f, LightmassSolverLights.ColorByteToLinear(0));
        Assert.AreEqual(1f, LightmassSolverLights.ColorByteToLinear(255), 1e-6f);
        Assert.AreEqual(MathF.Pow(128 / 255f, 2.2f), LightmassSolverLights.ColorByteToLinear(128), 1e-6f);

        var light = PointLight(LightFlags.HasStaticLighting | LightFlags.UseDirectLightMap | LightFlags.CastShadows | LightFlags.CastStaticShadows, (255, 128, 0), 2);
        var converted = LightmassSolverLights.Convert(light);
        Assert.AreEqual(SolverLightType.Point, converted.Type);
        Assert.AreEqual(2f, converted.Color.X, 1e-5f);
        Assert.AreEqual(2 * MathF.Pow(128 / 255f, 2.2f), converted.Color.Y, 1e-5f);
        Assert.AreEqual(0f, converted.Color.Z);
        Assert.AreEqual(500f, converted.Radius);
        Assert.AreEqual(2f, converted.FalloffExponent);
        Assert.AreEqual(10f, converted.SourceSize);
        Assert.IsTrue(converted.CastShadows);
        Assert.IsTrue(converted.Bounces);
    }

    [TestMethod]
    public void Lights_ShadowTwinsAndSkiesDontBounce_ShadowsNeedBothFlags()
    {
        var twin = PointLight(LightFlags.CastShadows | LightFlags.CastStaticShadows | LightFlags.HasStaticShadowing, (255, 255, 255), 1);
        twin.ShadowOnly = true;
        twin.IndirectLightingScale = 0;
        Assert.IsFalse(LightmassSolverLights.Convert(twin).Bounces);

        var noStatic = PointLight(LightFlags.HasStaticLighting | LightFlags.UseDirectLightMap | LightFlags.CastShadows, (255, 255, 255), 1);
        Assert.IsFalse(LightmassSolverLights.Convert(noStatic).CastShadows);

        var (cosInner, cosOuter) = LightmassSolverLights.SpotCone(20, 40);
        Assert.AreEqual(MathF.Cos(20 * MathF.PI / 180), cosInner, 1e-6f);
        Assert.AreEqual(MathF.Cos(40 * MathF.PI / 180), cosOuter, 1e-6f);
        // An inner angle past the outer one is clamped below it
        var (_, clampedOuter) = LightmassSolverLights.SpotCone(50, 40);
        Assert.IsTrue(clampedOuter < MathF.Cos(50 * MathF.PI / 180));
    }

    // ================= routing =================

    [TestMethod]
    public void Routing_FollowsLightmassFlags()
    {
        const uint staticLight = LightFlags.HasStaticLighting | LightFlags.UseDirectLightMap | LightFlags.HasStaticShadowing;
        const uint dominant = LightFlags.HasStaticLighting | LightFlags.Dominant | LightFlags.HasStaticShadowing | LightFlags.UseSignedDistanceFieldShadows;
        const uint twin = LightFlags.HasStaticShadowing | LightFlags.CastShadows | LightFlags.CastStaticShadows;
        LightmassLightRoute R(uint flags, bool sky = false, bool shadowOnly = false, bool indirectOnly = false, bool receiver = false, bool texture = true, bool sdf = true) =>
            LightmassSolverLights.Route(flags, sky, shadowOnly, indirectOnly, receiver, texture, sdf);

        Assert.AreEqual(LightmassLightRoute.Lightmap, R(staticLight));
        Assert.AreEqual(LightmassLightRoute.DistanceFieldShadowMap, R(dominant));
        Assert.AreEqual(LightmassLightRoute.ShadowMap, R(dominant, sdf: false));
        Assert.AreEqual(LightmassLightRoute.ShadowMap, R(dominant, texture: false));
        Assert.AreEqual(LightmassLightRoute.ShadowMap, R(twin, shadowOnly: true));
        Assert.AreEqual(LightmassLightRoute.DistanceFieldShadowMap, R(twin | LightFlags.UseSignedDistanceFieldShadows, shadowOnly: true));
        // GI-only lights only bounce
        Assert.AreEqual(LightmassLightRoute.None, R(LightFlags.HasStaticLighting | LightFlags.HasStaticShadowing, indirectOnly: true));
        // A shadow-only receiver keeps only its shadow maps
        Assert.AreEqual(LightmassLightRoute.None, R(staticLight, receiver: true));
        Assert.AreEqual(LightmassLightRoute.ShadowMap, R(twin, shadowOnly: true, receiver: true));
        // Sky lights go into the lightmap or nowhere
        Assert.AreEqual(LightmassLightRoute.Lightmap, R(LightFlags.HasStaticLighting | LightFlags.UseDirectLightMap, sky: true));
        Assert.AreEqual(LightmassLightRoute.None, R(LightFlags.HasStaticLighting | LightFlags.HasStaticShadowing, sky: true));
        // No static lighting or shadowing: nothing
        Assert.AreEqual(LightmassLightRoute.None, R(LightFlags.CastShadows));
    }

    // ================= distance fields =================

    [TestMethod]
    public void DistanceField_EncodesLinearlyWithHalfAtTheEdge()
    {
        Assert.AreEqual(127, LightmassDistanceField.Encode(0, lit: true));
        Assert.AreEqual(127, LightmassDistanceField.Encode(0, lit: false));
        Assert.AreEqual(255, LightmassDistanceField.Encode(1, lit: true));
        Assert.AreEqual(0, LightmassDistanceField.Encode(1, lit: false));
        Assert.AreEqual(191, LightmassDistanceField.Encode(0.5f, lit: true));
        Assert.AreEqual(63, LightmassDistanceField.Encode(0.5f, lit: false));
        Assert.AreEqual(255, LightmassDistanceField.Encode(3, lit: true));
    }

    [TestMethod]
    public void DistanceField_DistanceToTheNearestTransition()
    {
        // A strip of 4 texels, upsampled 2×: high resolution texels 0..3 lit, 4..7 shadowed, one world unit apart
        const int width = 4, height = 1, upsample = 2;
        // Every texel refined: texel b's block holds its 2×2 high resolution texels, row-major
        int[] blocks = [0, 1, 2, 3];
        var highCovered = Enumerable.Repeat(true, width * upsample * upsample).ToArray();
        var highLit = new bool[highCovered.Length];
        var highPositions = new Vector3[highCovered.Length];
        for (int b = 0; b < width; b++)
        for (int sy = 0; sy < upsample; sy++)
        for (int sx = 0; sx < upsample; sx++)
        {
            int cell = b * 4 + sy * 2 + sx, hx = b * 2 + sx;
            highLit[cell] = hx < 4;
            highPositions[cell] = new Vector3(hx, sy, 0);
        }
        var covered = Enumerable.Repeat(true, width).ToArray();
        bool[] lit = [true, true, false, false];
        var positions = Enumerable.Range(0, width).Select(x => new Vector3(x * 2 + 0.5f, 0, 0)).ToArray();
        var (values, coverage) = LightmassDistanceField.Build(width, height, upsample, covered, lit, positions, blocks, highCovered, highLit, highPositions, 4, 1);
        // Texel 0's centre texel is at x=1 (3 from the first shadowed one at x=4), texel 1's at x=3 (1), texel 2's at x=5 (shadowed, 2 from x=3), texel 3's at x=7 (4)
        Assert.AreEqual(LightmassDistanceField.Encode(3 / 4f, true), values[0]);
        Assert.AreEqual(LightmassDistanceField.Encode(1 / 4f, true), values[1]);
        Assert.AreEqual(LightmassDistanceField.Encode(2 / 4f, false), values[2]);
        Assert.AreEqual(LightmassDistanceField.Encode(1, false), values[3]);
        Assert.IsTrue(values[0] > values[1] && values[1] > 127 && values[2] < 127 && values[3] < values[2]);
        CollectionAssert.AreEqual(new byte[] { 255, 255, 255, 0 }, coverage);
    }

    [TestMethod]
    public void DistanceField_OnlyTexelsNearEdgesAreRefined_OthersAreAFullDistanceAway()
    {
        // 10×1: lit up to texel 4, shadowed from 5; refined within 2 texels of the edge
        const int width = 10;
        var covered = Enumerable.Repeat(true, width).ToArray();
        var visibility = Enumerable.Range(0, width).Select(x => x < 5 ? 1f : 0f).ToArray();
        var refined = LightmassDistanceField.RefinedTexels(width, 1, covered, visibility, 2);
        CollectionAssert.AreEqual(new[] { false, false, true, true, true, true, true, true, false, false }, refined);
        // A partly lit texel is an edge of its own
        visibility = Enumerable.Repeat(1f, width).ToArray();
        visibility[0] = 0.5f;
        CollectionAssert.AreEqual(new[] { true, true, true, false, false, false, false, false, false, false },
            LightmassDistanceField.RefinedTexels(width, 1, covered, visibility, 2));
        // Unrefined texels are a full distance from any edge
        var blocks = Enumerable.Repeat(-1, width).ToArray();
        var lit = Enumerable.Range(0, width).Select(x => x < 5).ToArray();
        var (values, coverage) = LightmassDistanceField.Build(width, 1, 1, covered, lit, new Vector3[width], blocks, [], [], [], 50, 1);
        Assert.AreEqual(255, values[0]);
        Assert.AreEqual(0, values[9]);
        Assert.AreEqual(0, coverage[9]);
    }

    [TestMethod]
    public void DistanceField_UpsampleFactorIsClamped()
    {
        Assert.AreEqual(3, LightmassDistanceField.UpsampleFactor(1, 50, 50, 3, 64, 64));
        Assert.AreEqual(8, LightmassDistanceField.UpsampleFactor(8, 50, 50, 3, 64, 64));
        Assert.AreEqual(LightmassDistanceField.MaxUpsampleFactor, LightmassDistanceField.UpsampleFactor(100, 50, 50, 3, 64, 64));
        int big = LightmassDistanceField.UpsampleFactor(100, 50, 50, 3, 2048, 2048);
        Assert.IsTrue((long)2048 * 2048 * big * big <= LightmassDistanceField.MaxHighResTexels);
    }

    // ================= result conversion =================

    [TestMethod]
    public void ResultConversion_FromAFakeSolverOutput()
    {
        var guidLit = Guid.NewGuid();
        var guidFar = Guid.NewGuid();
        var guidShadow = Guid.NewGuid();
        var scene = new SolverScene();
        scene.Lights.Add(new SolverLight { Type = SolverLightType.Point, Position = new Vector3(0, 0, 100), Radius = 1000 });
        scene.Lights.Add(new SolverLight { Type = SolverLightType.Point, Position = new Vector3(5000, 0, 100), Radius = 100 });
        scene.Lights.Add(new SolverLight { Type = SolverLightType.Directional, Direction = -Vector3.UnitZ });
        var job = new SolverJob { Scene = scene };
        for (int i = 0; i < 3; i++)
            job.Samples.Add(new SolverSample { Position = new Vector3(i, 0, 0), Normal = Vector3.UnitZ, GeometricNormal = Vector3.UnitZ, Tangent = Vector3.UnitX, Bitangent = Vector3.UnitY });
        var plan = new LightmassLocalSolve.MappingPlan
        {
            Guid = Guid.NewGuid(), Texture = true, SizeX = 2, SizeY = 2, Samples = [0, 1, -1, 2],
            LightmapLights = [(0, guidLit), (1, guidFar)],
            ShadowMapLights = [(2, guidShadow)],
        };
        var output = new SolverOutput
        {
            Directional0 = [new(1, 2, 3), new(0, 0, 0), new(4, 4, 4)],
            Directional1 = [new(2, 2, 2), new(0, 0, 0), new(1, 1, 1)],
            Directional2 = [new(0, 1, 0), new(0, 0, 0), new(2, 2, 2)],
            Simple = [new(1, 1, 1), new(0.5f, 0, 0), new(2, 2, 2)],
            Visibility = [1f, 0.5f, 0f],
            VisibilityOffsets = [0, 1, 2],
        };
        var result = LightmassLocalSolve.Convert(plan, job, output, new LightmassLocalShadowSettings());

        Assert.AreEqual(plan.Guid, result.Guid);
        Assert.IsTrue(result.IsTextureMapping);
        CollectionAssert.AreEqual(new byte[] { 255, 255, 0, 255 }, result.Coverage);
        Assert.AreEqual(new Vector3(0.5f, 0, 0), result.Simple[1]);
        Assert.AreEqual(Vector3.Zero, result.Simple[2]);
        Assert.AreEqual(new Vector3(2, 2, 2), result.Simple[3]);
        // The directional coefficients went through Lightmass's colour + max storage
        var (colour, max) = Directional.Split(new Directional.Sample(output.Directional0[0], output.Directional1[0], output.Directional2[0]));
        var expected = Directional.Combine(colour, max);
        Assert.AreEqual(expected.E0, result.Directional[0].E0);
        Assert.AreEqual(expected.E2, result.Directional[0].E2);
        // The far light doesn't reach any sample, so the lightmap doesn't list it
        CollectionAssert.AreEqual(new[] { guidLit }, result.LightGuids);
        Assert.AreEqual(1, result.ShadowMaps.Count);
        var shadow = result.ShadowMaps[0];
        Assert.AreEqual(guidShadow, shadow.LightGuid);
        Assert.IsFalse(shadow.SignedDistanceField);
        Assert.AreEqual(255, shadow.Values[0]);
        Assert.AreEqual(LightmassLocalSolve.EncodeVisibility(0.5f), shadow.Values[1]);
        Assert.AreEqual((byte)(int)(MathF.Pow(0.5f, 1 / 2.2f) * 255), shadow.Values[1]);
        Assert.AreEqual(0, shadow.Values[3]);
        CollectionAssert.AreEqual(new byte[] { 255, 255, 0, 0 }, shadow.Coverage);
    }

    [TestMethod]
    public void ResultConversion_VertexMappingsAndOccludedShadowMaps()
    {
        var scene = new SolverScene();
        scene.Lights.Add(new SolverLight { Type = SolverLightType.Directional, Direction = -Vector3.UnitZ });
        scene.Lights.Add(new SolverLight { Type = SolverLightType.Directional, Direction = -Vector3.UnitZ });
        var job = new SolverJob { Scene = scene };
        job.Samples.Add(new SolverSample { Normal = Vector3.UnitZ });
        job.Samples.Add(new SolverSample { Normal = Vector3.UnitZ });
        var visible = Guid.NewGuid();
        var plan = new LightmassLocalSolve.MappingPlan { Guid = Guid.NewGuid(), Texture = false, SizeX = 2, SizeY = 1, Samples = [0, 1], ShadowMapLights = [(0, visible), (1, Guid.NewGuid())] };
        var output = new SolverOutput
        {
            Directional0 = new Vector3[2], Directional1 = new Vector3[2], Directional2 = new Vector3[2], Simple = new Vector3[2],
            Visibility = [0.25f, 0, 0.75f, 0], VisibilityOffsets = [0, 2],
        };
        var result = LightmassLocalSolve.Convert(plan, job, output, new LightmassLocalShadowSettings());
        Assert.IsFalse(result.IsTextureMapping);
        CollectionAssert.AreEqual(new byte[] { 255, 255 }, result.Coverage);
        // The completely occluded light gets no shadow map
        Assert.AreEqual(1, result.ShadowMaps.Count);
        Assert.AreEqual(visible, result.ShadowMaps[0].LightGuid);
        CollectionAssert.AreEqual(new[] { 0.25f, 0.75f }, result.ShadowMaps[0].Samples);
    }

    // ================= settings and jobs =================

    [TestMethod]
    public void DefaultIni_HasEveryKeyTheBakeReads()
    {
        var ini = LightmassDefaultIni.Load();
        ini.ApplyBioWareOverrides();
        foreach (var quality in Enum.GetValues<LightmassQuality>())
        {
            using var stream = new MemoryStream();
            using (var w = new LightmassWriter(stream))
                LightmassSceneHeader.Write(w, ini, quality, new LightmassWorldSettings(), new LightmassSceneHeader.Counts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
            Assert.AreEqual(LightmassSceneHeader.Size, stream.Length);
            LightmassLocalSolve.Settings(ini, quality, new LightmassWorldSettings(), SolverDevice.Cpu);
        }
        Assert.AreEqual(128, (int)ini.Float("DevOptions.StaticLightingMaterial", "DiffuseSampleSize"));

        var (solver, shadows) = LightmassLocalSolve.Settings(ini, LightmassQuality.Medium, new LightmassWorldSettings { NumIndirectLightingBounces = 2 }, SolverDevice.Gpu);
        Assert.AreEqual(SolverProfile.Lightmass, solver.Profile);
        Assert.AreEqual(16, solver.ShadowRays);
        Assert.AreEqual(32, solver.PenumbraShadowRays);
        Assert.AreEqual(128, solver.GatherRays);
        Assert.AreEqual(64, solver.BounceGatherRays);
        Assert.AreEqual(2, solver.Bounces);
        // BioWare's: plain cos into the directional coefficients (UDK's stock ini has 2·cos^0.85)
        Assert.AreEqual(1f, solver.DirectionalCoefficientScale);
        Assert.AreEqual(1f, solver.DirectionalCoefficientFalloffPower, 1e-6f);
        var (stock, _) = LightmassLocalSolve.Settings(LightmassDefaultIni.Load(), LightmassQuality.Medium, new LightmassWorldSettings(), SolverDevice.Gpu);
        Assert.AreEqual(2f, stock.DirectionalCoefficientScale);
        Assert.AreEqual(0.85f, stock.DirectionalCoefficientFalloffPower, 1e-6f);
        Assert.AreEqual(0.1f, solver.RayOffset, 1e-6f);
        Assert.AreEqual(3f, solver.NormalOffset, 1e-6f);
        Assert.AreEqual(150, shadows.HighResTexelsPerTransition);
        Assert.AreEqual(3, shadows.MinUpsampleFactor);
        Assert.AreEqual(50f, shadows.MaxTransitionDistance);
    }

    [TestMethod]
    public void Job_SolverDefaultsToGpuAndOnlyUdkNeedsUdk()
    {
        var old = JsonConvert.DeserializeObject<LightmassBakeJob>("{\"Quality\":\"High\"}");
        Assert.AreEqual(LightmassSolverChoice.Gpu, old.Solver);
        var roundTrip = JsonConvert.DeserializeObject<LightmassBakeJob>(JsonConvert.SerializeObject(new LightmassBakeJob { Solver = LightmassSolverChoice.UdkLightmass }));
        Assert.AreEqual(LightmassSolverChoice.UdkLightmass, roundTrip.Solver);
        StringAssert.Contains(JsonConvert.SerializeObject(new LightmassBakeJob { Solver = LightmassSolverChoice.Cpu }), "\"Solver\": \"Cpu\"".Replace(" ", ""));

        string missing = Path.Combine(Path.GetTempPath(), "LEX-no-such-udk-" + Guid.NewGuid().ToString("N"));
        var local = new LightmassBakeJob { Solver = LightmassSolverChoice.Gpu, UdkBinaries = missing };
        Assert.IsFalse(local.Problems().Any(p => p.Contains("UnrealLightmass") || p.Contains("SwarmAgent")), string.Join("\n", local.Problems()));
        var udk = new LightmassBakeJob { Solver = LightmassSolverChoice.UdkLightmass, UdkBinaries = missing };
        Assert.IsTrue(udk.Problems().Any(p => p.Contains("UnrealLightmass")));
    }

    [TestMethod]
    public void Settings_LocalSolverLoadsTheEmbeddedIni()
    {
        var settings = new LightmassBakeSettings { Solver = LightmassSolverChoice.Cpu, UdkBinaries = null };
        var ini = LightmassBake.LoadIni(settings, LegendaryExplorerCore.Packages.MEGame.LE3);
        // BioWare's override is applied for LE3
        Assert.AreEqual(0.01f, ini.Float("DevOptions.StaticShadows", "MinUnoccludedFraction"), 1e-6f);
        Assert.AreEqual(8, ini.Int("DevOptions.StaticShadows", "NumShadowRays"));
    }

    [STATestMethod]
    public void Window_SolverComboGreysOutAMissingGpuAndUdkFieldsFollowTheChoice()
    {
        typeof(System.Windows.Application).GetField("_resourceAssembly", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(null, typeof(LegendaryExplorer.Tools.LightmassBaker.LightmassBakerWindow).Assembly);
        _ = System.Windows.Application.Current ?? new System.Windows.Application();
        System.Windows.Application.Current.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        System.Windows.Application.Current.Resources = (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));

        var window = new LegendaryExplorer.Tools.LightmassBaker.LightmassBakerWindow { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            var content = (System.Windows.FrameworkElement)window.Content;
            content.Measure(new System.Windows.Size(900, 760));
            content.Arrange(new System.Windows.Rect(0, 0, 900, 760));
            content.UpdateLayout();
            CollectionAssert.AreEqual(new[] { "GPU", "CPU", "UDK Lightmass (reference)" }, window.Solvers.Select(s => s.Label).ToArray());
            bool gpu = LightSolver.IsGpuAvailable();
            Assert.AreEqual(gpu, window.Solvers[0].IsEnabled);
            Assert.AreEqual(gpu ? LightmassSolverChoice.Gpu : LightmassSolverChoice.Cpu, window.Solver);
            Assert.IsFalse(window.UsesUdk);
            window.Solver = LightmassSolverChoice.UdkLightmass;
            Assert.IsTrue(window.UsesUdk);
            window.Solver = LightmassSolverChoice.Gpu;
            Assert.AreEqual(gpu ? LightmassSolverChoice.Gpu : LightmassSolverChoice.Cpu, window.Solver);
        }
        finally
        {
            window.Close();
        }
    }

    [TestMethod]
    public void Heatmap_IsAPng()
    {
        var reference = new LightmassMappingResult
        {
            IsTextureMapping = true, SizeX = 2, SizeY = 2, Coverage = [255, 255, 255, 0],
            Simple = [new(1, 1, 1), new(2, 2, 2), new(0, 0, 0), new(0, 0, 0)], Directional = new Directional.Sample[4], LightGuids = [],
        };
        var local = new LightmassMappingResult
        {
            IsTextureMapping = true, SizeX = 2, SizeY = 2, Coverage = [255, 255, 255, 0],
            Simple = [new(1, 1, 1), new(1, 1, 1), new(0, 0, 0), new(0, 0, 0)], Directional = new Directional.Sample[4], LightGuids = [],
        };
        var c = LightmassCompare.Compare("test", reference, local, g => g.ToString());
        Assert.AreEqual(3, c.Covered);
        Assert.AreEqual(0.5, c.RelativeErrors[1], 1e-6);
        Assert.AreEqual(0, c.RelativeErrors[0], 1e-6);
        var png = LightmassCompare.Heatmap(c);
        CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
        // IHDR: three 128-texel panels (2 texels scaled 64×) and two 4-pixel gaps, 128 tall, 8-bit RGB
        int Int(int offset) => png[offset] << 24 | png[offset + 1] << 16 | png[offset + 2] << 8 | png[offset + 3];
        Assert.AreEqual("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.AreEqual(392, Int(16));
        Assert.AreEqual(128, Int(20));
        // The IHDR chunk's CRC covers its type and data
        Assert.AreEqual((int)Crc(png.AsSpan(12, 17)), Int(29));
        var decoded = new System.Windows.Media.Imaging.PngBitmapDecoder(new MemoryStream(png), System.Windows.Media.Imaging.BitmapCreateOptions.None,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        Assert.AreEqual(392, decoded.Frames[0].PixelWidth);
    }

    static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }
}
