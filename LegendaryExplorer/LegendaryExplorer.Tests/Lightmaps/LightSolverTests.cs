using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using LegendaryExplorer.Lightmaps.Codec;
using LegendaryExplorer.Lightmaps.Solver;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Lightmaps;

[TestClass]
public class LightSolverTests
{
    public TestContext TestContext { get; set; }

    // ------------------------------------------------------------------ scene helpers

    sealed class SceneBuilder
    {
        public readonly SolverScene Scene = new();
        public readonly SolverJob Job;

        public SceneBuilder(SolverDevice device = SolverDevice.Cpu)
        {
            Job = new SolverJob { Scene = Scene, Settings = new SolverSettings { Device = device, FilterIndirect = false } };
        }

        public int Material(Vector3 albedo, bool twoSided = false, SolverTexture transmission = null, bool masked = false, Vector3 emissive = default)
        {
            Scene.Materials.Add(new SolverMaterial
            {
                Diffuse = SolverTexture.Constant(new Vector4(albedo, 1)),
                Emissive = emissive == default ? null : SolverTexture.Constant(new Vector4(emissive, 1)),
                Transmission = transmission,
                Masked = masked,
                TwoSided = twoSided,
            });
            return Scene.Materials.Count - 1;
        }

        /// <summary>A quad p0..p3 (counter-clockwise seen from the side its normal faces), split into n×n cells</summary>
        public void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, int material, uint instance = 0,
            SolverTriangleFlags flags = SolverTriangleFlags.CastShadow, int n = 1)
        {
            Vector3 normal = Vector3.Normalize(Vector3.Cross(p1 - p0, p3 - p0));
            int baseIndex = Scene.Vertices.Count;
            for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++)
                {
                    float u = (float)i / n, v = (float)j / n;
                    Vector3 p = Vector3.Lerp(Vector3.Lerp(p0, p1, u), Vector3.Lerp(p3, p2, u), v);
                    Scene.Vertices.Add(new SolverVertex { Position = p, Normal = normal, MaterialUV = new Vector2(u, v) });
                }
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    uint a = (uint)(baseIndex + j * (n + 1) + i), b = a + 1, c = a + (uint)(n + 1) + 1, d = a + (uint)(n + 1);
                    Scene.Triangles.Add(new SolverTriangle { V0 = a, V1 = b, V2 = c, Material = (uint)material, Instance = instance, Flags = flags });
                    Scene.Triangles.Add(new SolverTriangle { V0 = a, V1 = c, V2 = d, Material = (uint)material, Instance = instance, Flags = flags });
                }
        }

        /// <summary>A horizontal square at height z facing up (or down)</summary>
        public void HorizontalSquare(float cx, float cy, float half, float z, bool up, int material, uint instance = 0,
            SolverTriangleFlags flags = SolverTriangleFlags.CastShadow, int n = 1)
        {
            Vector3 a = new(cx - half, cy - half, z), b = new(cx + half, cy - half, z), c = new(cx + half, cy + half, z), d = new(cx - half, cy + half, z);
            if (up)
                Quad(a, b, c, d, material, instance, flags, n);
            else
                Quad(a, d, c, b, material, instance, flags, n);
        }

        public int Receiver(int[] lightmapLights, int[] shadowLights = null, bool gather = true)
        {
            Job.Receivers.Add(new SolverReceiver { LightmapLights = lightmapLights, ShadowMapLights = shadowLights ?? [], GatherIndirect = gather });
            return Job.Receivers.Count - 1;
        }

        public int Sample(Vector3 p, Vector3 n, int receiver, uint instance = 100, float texelRadius = 0.5f)
        {
            Vector3 t = MathF.Abs(n.Z) < 0.9f ? Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, n)) : Vector3.UnitX;
            t = Vector3.Normalize(t - n * Vector3.Dot(t, n));
            Vector3 b = Vector3.Cross(n, t);
            Job.Samples.Add(new SolverSample
            {
                Position = p, Normal = n, GeometricNormal = n, Tangent = t, Bitangent = b,
                TexelRadius = texelRadius, Instance = instance, Receiver = (uint)receiver,
            });
            return Job.Samples.Count - 1;
        }
    }

    static SolverLight PointLight(Vector3 pos, float radius, float sourceSize, Vector3? color = null) => new()
    {
        Type = SolverLightType.Point, Position = pos, Radius = radius, FalloffExponent = 2, SourceSize = sourceSize, Color = color ?? Vector3.One,
    };

    static float Attenuation(float d, float radius, float falloff) => d >= radius ? 0 : MathF.Pow(1 - d * d / (radius * radius), falloff);

    static void AssertClose(float expected, float actual, float relTol, string what)
    {
        float tol = MathF.Max(1e-5f, MathF.Abs(expected) * relTol);
        Assert.IsTrue(MathF.Abs(expected - actual) <= tol, $"{what}: expected {expected}, got {actual}");
    }

    SolverOutput Solve(SceneBuilder sb, out TimeSpan elapsed)
    {
        var sw = Stopwatch.StartNew();
        var output = LightSolver.Solve(sb.Job);
        elapsed = sw.Elapsed;
        TestContext?.WriteLine($"{output.Statistics.DeviceName}: {output.Statistics.RaysCast} rays in {elapsed.TotalMilliseconds:F0} ms");
        return output;
    }

    SolverOutput Solve(SceneBuilder sb) => Solve(sb, out _);

    // ------------------------------------------------------------------ tests

    [TestMethod]
    public void PointLightOverPlane_MatchesAnalyticIrradiance()
    {
        var sb = new SceneBuilder();
        int mat = sb.Material(new Vector3(0.5f));
        sb.HorizontalSquare(0, 0, 1000, 0, true, mat);
        const float height = 100, radius = 600;
        sb.Scene.Lights.Add(PointLight(new Vector3(0, 0, height), radius, 0, new Vector3(2, 1, 0.5f)));
        int rec = sb.Receiver([0], gather: false);
        float[] xs = [0, 50, 150, 300, 500, 700];
        foreach (float x in xs)
            sb.Sample(new Vector3(x, 0, 0), Vector3.UnitZ, rec, texelRadius: 0);
        var output = Solve(sb);

        for (int i = 0; i < xs.Length; i++)
        {
            float d = MathF.Sqrt(xs[i] * xs[i] + height * height);
            float att = Attenuation(d, radius, 2);
            Vector3 dir = Vector3.Normalize(new Vector3(-xs[i], 0, height));
            Vector3 expected = new Vector3(2, 1, 0.5f) * att * dir.Z;
            AssertClose(expected.X, output.Simple[i].X, 1e-3f, $"Simple.R at x={xs[i]}");
            AssertClose(expected.Z, output.Simple[i].Z, 1e-3f, $"Simple.B at x={xs[i]}");
            // tangent frame: T = +X, B = +Y, N = +Z
            Vector3 dT = dir;
            for (int k = 0; k < 3; k++)
            {
                float c = MathF.Max(0, Vector3.Dot(dT, Directional.Basis[k]));
                Vector3 e = k == 0 ? output.Directional0[i] : k == 1 ? output.Directional1[i] : output.Directional2[i];
                AssertClose(2 * att * c, e.X, 1e-3f, $"E{k} at x={xs[i]}");
            }
        }
    }

    [TestMethod]
    public void Occluder_CastsShadowWithSoftPenumbra()
    {
        var sb = new SceneBuilder();
        int mat = sb.Material(new Vector3(0.5f));
        sb.HorizontalSquare(0, 0, 1000, 0, true, mat, instance: 1);
        sb.HorizontalSquare(0, 0, 20, 50, false, mat, instance: 2); // faces down, toward the floor
        sb.Scene.Lights.Add(PointLight(new Vector3(0, 0, 200), 2000, 20));
        sb.Job.Settings.ShadowRays = 16;
        sb.Job.Settings.PenumbraShadowRays = 64;
        int rec = sb.Receiver([0], gather: false);
        int smRec = sb.Receiver([], [0], gather: false);
        // the penumbra runs from x ≈ 20 to x ≈ 33 along y = 0
        float[] xs = [0, 10, 18, 22, 25, 27, 29, 32, 36, 60, 300];
        foreach (float x in xs)
            sb.Sample(new Vector3(x, 0, 0), Vector3.UnitZ, rec, instance: 1);
        foreach (float x in xs)
            sb.Sample(new Vector3(x, 0, 0), Vector3.UnitZ, smRec, instance: 1);
        var output = Solve(sb);

        float Unshadowed(float x)
        {
            float d = MathF.Sqrt(x * x + 200 * 200);
            return Attenuation(d, 2000, 2) * 200 / d;
        }
        float[] ratio = xs.Select((x, i) => output.Simple[i].X / Unshadowed(x)).ToArray();
        TestContext?.WriteLine("ratios: " + string.Join(", ", ratio.Select(r => r.ToString("F3"))));
        Assert.IsTrue(ratio[0] < 0.01f && ratio[1] < 0.01f, "umbra under the occluder");
        Assert.IsTrue(ratio[^1] > 0.98f && ratio[^2] > 0.98f, "fully lit away from it");
        int partial = ratio.Count(r => r > 0.05f && r < 0.95f);
        Assert.IsTrue(partial >= 3, "a soft penumbra spans several samples");
        for (int i = 1; i < ratio.Length; i++)
            Assert.IsTrue(ratio[i] >= ratio[i - 1] - 0.08f, $"penumbra brightens outwards ({ratio[i - 1]} → {ratio[i]})");

        // the shadow map gives the same visibility
        for (int i = 0; i < xs.Length; i++)
        {
            int s = xs.Length + i;
            float vis = output.Visibility[output.VisibilityOffsets[s]];
            Assert.AreEqual(ratio[i], vis, 0.12f, $"shadow-map visibility at x={xs[i]}");
            Assert.AreEqual(Vector3.Zero, output.Simple[s], "shadow-map lights add nothing to the lightmap");
        }
    }

    [TestMethod]
    public void OneSidedBackface_DoesNotShadow_UnlessTwoSidedShadow()
    {
        var sb = new SceneBuilder();
        int mat = sb.Material(new Vector3(0.5f));
        sb.HorizontalSquare(0, 0, 1000, 0, true, mat, instance: 1);
        sb.HorizontalSquare(0, 0, 30, 50, true, mat, instance: 2);                       // faces the light: the ray hits its back
        sb.HorizontalSquare(300, 0, 30, 50, true, mat, instance: 3, SolverTriangleFlags.CastShadow | SolverTriangleFlags.TwoSidedShadow);
        sb.HorizontalSquare(-300, 0, 30, 50, true, mat, instance: 4, SolverTriangleFlags.None); // casts no shadow
        sb.HorizontalSquare(0, 300, 30, 50, false, mat, instance: 5, SolverTriangleFlags.CastShadow | SolverTriangleFlags.SelfShadowOnly);
        sb.HorizontalSquare(0, -300, 30, 50, false, mat, instance: 6, SolverTriangleFlags.CastShadow | SolverTriangleFlags.ShadowIndirectOnly);
        sb.Scene.Lights.Add(new SolverLight { Type = SolverLightType.Directional, Direction = -Vector3.UnitZ, Color = Vector3.One, SourceSize = 0 });
        int rec = sb.Receiver([0], gather: false);
        int a = sb.Sample(new Vector3(0, 0, 0), Vector3.UnitZ, rec, 1);
        int b = sb.Sample(new Vector3(300, 0, 0), Vector3.UnitZ, rec, 1);
        int c = sb.Sample(new Vector3(-300, 0, 0), Vector3.UnitZ, rec, 1);
        int d = sb.Sample(new Vector3(0, 300, 0), Vector3.UnitZ, rec, 1);
        int d5 = sb.Sample(new Vector3(0, 300, 0), Vector3.UnitZ, rec, 5); // a sample of instance 5 itself
        int e = sb.Sample(new Vector3(0, -300, 0), Vector3.UnitZ, rec, 1);
        var output = Solve(sb);
        AssertClose(1, output.Simple[a].X, 1e-3f, "one-sided back face lets light through");
        AssertClose(0, output.Simple[b].X, 1e-3f, "TwoSidedShadow blocks from behind");
        AssertClose(1, output.Simple[c].X, 1e-3f, "no CastShadow");
        AssertClose(1, output.Simple[d].X, 1e-3f, "SelfShadowOnly doesn't shadow other instances");
        AssertClose(0, output.Simple[d5].X, 1e-3f, "SelfShadowOnly shadows its own instance");
        AssertClose(1, output.Simple[e].X, 1e-3f, "ShadowIndirectOnly doesn't block direct light");
    }

    [TestMethod]
    public void LightFacingShadows_OneSidedShadowsOnlyFromTheFrontTheLightSees()
    {
        var sb = new SceneBuilder();
        sb.Job.Settings.LightFacingShadows = true;
        int mat = sb.Material(new Vector3(0.5f));
        sb.HorizontalSquare(0, 0, 1000, 0, true, mat, instance: 1);
        sb.HorizontalSquare(0, 0, 30, 50, true, mat, instance: 2);      // its front faces the light: shadows
        sb.HorizontalSquare(300, 0, 30, 50, false, mat, instance: 3);   // its back faces the light: light passes
        sb.HorizontalSquare(-300, 0, 30, 50, false, mat, instance: 4, SolverTriangleFlags.CastShadow | SolverTriangleFlags.TwoSidedShadow);
        sb.Scene.Lights.Add(new SolverLight { Type = SolverLightType.Directional, Direction = -Vector3.UnitZ, Color = Vector3.One, SourceSize = 0 });
        int rec = sb.Receiver([0], gather: false);
        int a = sb.Sample(new Vector3(0, 0, 0), Vector3.UnitZ, rec, 1);
        int b = sb.Sample(new Vector3(300, 0, 0), Vector3.UnitZ, rec, 1);
        int c = sb.Sample(new Vector3(-300, 0, 0), Vector3.UnitZ, rec, 1);
        var output = Solve(sb);
        AssertClose(0, output.Simple[a].X, 1e-3f, "a front facing the light shadows");
        AssertClose(1, output.Simple[b].X, 1e-3f, "a back facing the light lets it through");
        AssertClose(0, output.Simple[c].X, 1e-3f, "TwoSidedShadow shadows from either side");
    }

    [TestMethod]
    public void PhotonMap_AgreesWithPathTracedBounce()
    {
        static SceneBuilder Box(bool photons)
        {
            var sb = ClosedBox(SolverDevice.Cpu, 3, 256);
            sb.Job.Settings.NormalOffset = 0.5f;
            if (photons)
                // the box is tiny next to a level: many photons per square unit, and searches a box's size apart
                sb.Job.Settings.Photons = new SolverPhotonSettings { CountScale = 4000, DirectSearchDistance = 25, IndirectSearchDistance = 25 };
            return sb;
        }
        var traced = Solve(Box(false));
        var mapped = Solve(Box(true));
        TestContext?.WriteLine(mapped.Statistics.PhotonSummary);
        Assert.IsNotNull(mapped.Statistics.PhotonSummary, "the photon pass ran");
        double meanTraced = traced.IndirectSimple.Average(v => (double)v.X), meanMapped = mapped.IndirectSimple.Average(v => (double)v.X);
        int centre = 4 * 9 + 4;
        TestContext?.WriteLine($"mean indirect: traced {meanTraced:F4}, photons {meanMapped:F4}; shadowed centre {traced.IndirectSimple[centre].X:F4} / {mapped.IndirectSimple[centre].X:F4}");
        Assert.AreEqual(1, meanMapped / meanTraced, 0.2, "mean bounced light");
        Assert.AreEqual(1, mapped.IndirectSimple[centre].X / traced.IndirectSimple[centre].X, 0.35, "bounced light in the plate's shadow");
        Assert.AreEqual(traced.Simple[0].X - traced.IndirectSimple[0].X, mapped.Simple[0].X - mapped.IndirectSimple[0].X, 1e-3, "direct light is the same");
    }

    [TestMethod]
    public void MaskedAndTranslucentShadows()
    {
        var sb = new SceneBuilder();
        int floor = sb.Material(new Vector3(0.5f));
        int maskedPass = sb.Material(new Vector3(0.5f), transmission: SolverTexture.Constant(new Vector4(0.1f, 0, 0, 1)), masked: true);
        int maskedBlock = sb.Material(new Vector3(0.5f), transmission: SolverTexture.Constant(new Vector4(0.9f, 0, 0, 1)), masked: true);
        int tinted = sb.Material(new Vector3(0.5f), transmission: SolverTexture.Constant(new Vector4(0.5f, 0.25f, 1f, 1)));
        // a masked material whose mask is a checker: half the texels pass
        var checker = new SolverTexture { Size = 2, Texels = [new(0, 0, 0, 1), new(1, 0, 0, 1), new(1, 0, 0, 1), new(0, 0, 0, 1)] };
        int maskedChecker = sb.Material(new Vector3(0.5f), transmission: checker, masked: true);
        sb.HorizontalSquare(0, 0, 2000, 0, true, floor, instance: 1);
        sb.HorizontalSquare(0, 0, 30, 50, false, maskedPass, instance: 2);
        sb.HorizontalSquare(300, 0, 30, 50, false, maskedBlock, instance: 3);
        sb.HorizontalSquare(-300, 0, 30, 50, false, tinted, instance: 4);
        sb.HorizontalSquare(-300, 0, 30, 60, false, tinted, instance: 5); // a second layer
        sb.HorizontalSquare(0, 300, 30, 50, false, tinted, instance: 6);
        sb.HorizontalSquare(600, 0, 200, 50, false, maskedChecker, instance: 7);
        sb.Scene.Lights.Add(new SolverLight { Type = SolverLightType.Directional, Direction = -Vector3.UnitZ, Color = Vector3.One });
        int rec = sb.Receiver([0], gather: false);
        int a = sb.Sample(new Vector3(0, 0, 0), Vector3.UnitZ, rec, 1);
        int b = sb.Sample(new Vector3(300, 0, 0), Vector3.UnitZ, rec, 1);
        int c = sb.Sample(new Vector3(-300, 0, 0), Vector3.UnitZ, rec, 1);
        int d = sb.Sample(new Vector3(0, 300, 0), Vector3.UnitZ, rec, 1);
        // checker over [400, 800]: UV u = (x - 400) / 400, so texel columns switch at 0.25/0.75 when sampled at texel centres
        int e = sb.Sample(new Vector3(400 + 400 * 0.25f, 400 * 0.25f - 200 + 0f, 0), Vector3.UnitZ, rec, 1);
        int f = sb.Sample(new Vector3(400 + 400 * 0.75f, 400 * 0.25f - 200 + 0f, 0), Vector3.UnitZ, rec, 1);
        var output = Solve(sb);
        AssertClose(1, output.Simple[a].X, 1e-3f, "masked, below the clip value: passes");
        AssertClose(0, output.Simple[b].X, 1e-3f, "masked, above the clip value: blocks");
        AssertClose(0.25f, output.Simple[c].X, 1e-3f, "two translucent layers multiply (R)");
        AssertClose(0.0625f, output.Simple[c].Y, 1e-3f, "two translucent layers multiply (G)");
        AssertClose(1f, output.Simple[c].Z, 1e-3f, "two translucent layers multiply (B)");
        AssertClose(0.5f, output.Simple[d].X, 1e-3f, "one translucent layer tints (R)");
        AssertClose(0.25f, output.Simple[d].Y, 1e-3f, "one translucent layer tints (G)");
        Assert.IsTrue(output.Simple[e].X > 0.99f != output.Simple[f].X > 0.99f, $"checker mask: one passes, one blocks ({output.Simple[e].X}, {output.Simple[f].X})");
    }

    static SceneBuilder ClosedBox(SolverDevice device, int bounces, int gatherRays = 128)
    {
        var sb = new SceneBuilder(device);
        int white = sb.Material(new Vector3(0.8f));
        int plate = sb.Material(new Vector3(0.8f), twoSided: true);
        const float h = 100;
        // inward-facing walls
        sb.Quad(new(-h, -h, -h), new(h, -h, -h), new(h, h, -h), new(-h, h, -h), white, 1, n: 4);   // floor, up
        sb.Quad(new(-h, -h, h), new(-h, h, h), new(h, h, h), new(h, -h, h), white, 1, n: 4);       // ceiling, down
        sb.Quad(new(-h, -h, -h), new(-h, h, -h), new(-h, h, h), new(-h, -h, h), white, 1, n: 4);   // x = -h, +x
        sb.Quad(new(h, -h, -h), new(h, -h, h), new(h, h, h), new(h, h, -h), white, 1, n: 4);       // x = +h, -x
        sb.Quad(new(-h, -h, -h), new(-h, -h, h), new(h, -h, h), new(h, -h, -h), white, 1, n: 4);   // y = -h, +y
        sb.Quad(new(-h, h, -h), new(h, h, -h), new(h, h, h), new(-h, h, h), white, 1, n: 4);       // y = +h, -y
        // a two-sided plate between the light and the middle of the floor
        sb.HorizontalSquare(0, 0, 50, -60, true, plate, 2);
        sb.Scene.Lights.Add(PointLight(new Vector3(0, 0, -30), 1000, 5));
        sb.Job.Settings.Bounces = bounces;
        sb.Job.Settings.GatherRays = gatherRays;
        int rec = sb.Receiver([0]);
        // samples over the floor, a 9×9 grid
        for (int j = 0; j < 9; j++)
            for (int i = 0; i < 9; i++)
                sb.Sample(new Vector3(-80 + 20 * i, -80 + 20 * j, -h), Vector3.UnitZ, rec, 1, 10);
        // and on a wall
        for (int j = 0; j < 9; j++)
            for (int i = 0; i < 9; i++)
                sb.Sample(new Vector3(-h, -80 + 20 * i, -80 + 20 * j), Vector3.UnitX, rec, 1, 10);
        return sb;
    }

    [TestMethod]
    public void ClosedBox_BounceBrightensTheShadowedFloor()
    {
        var direct = Solve(ClosedBox(SolverDevice.Cpu, 0));
        var bounce1 = Solve(ClosedBox(SolverDevice.Cpu, 1));
        var bounce3 = Solve(ClosedBox(SolverDevice.Cpu, 3));
        int centre = 4 * 9 + 4;
        TestContext?.WriteLine($"floor centre: direct-only {direct.Simple[centre].X}, 1 bounce {bounce1.Simple[centre].X}, 3 bounces {bounce3.Simple[centre].X}");
        Assert.IsTrue(direct.Simple[centre].X < 1e-4f, "the plate shadows the floor's centre");
        Assert.IsTrue(bounce1.Simple[centre].X > 0.01f, "bounced light reaches the shadowed floor");
        Assert.IsTrue(bounce3.Simple[centre].X > bounce1.Simple[centre].X * 1.1f, "more bounces, more light");
        Assert.AreEqual(bounce1.IndirectSimple[centre].X, bounce1.Simple[centre].X, 1e-5f, "in the umbra all light is indirect");
        for (int s = 0; s < direct.Simple.Length; s++)
            Assert.IsTrue(bounce1.Simple[s].X >= direct.Simple[s].X - 1e-5f, "indirect light only adds");

        // energy check: a closed white box's indirect light is roughly albedo/(1 - albedo) of the direct, on average
        double meanDirect = bounce3.Simple.Select((v, i) => (double)(v.X - bounce3.IndirectSimple[i].X)).Average();
        double meanIndirect = bounce3.IndirectSimple.Average(v => (double)v.X);
        TestContext?.WriteLine($"mean direct {meanDirect:F4}, mean indirect {meanIndirect:F4}");
        Assert.IsTrue(meanIndirect > 0.2 * meanDirect && meanIndirect < 5 * meanDirect);
    }

    [TestMethod]
    public void Furnace_EmissiveBoxMatchesTheGeometricSeries()
    {
        // inside a closed box whose walls emit e and reflect albedo a, a path of B vertices carries e·(1 + a + … + a^(B-1)), so Simple = π·that
        foreach (int bounces in new[] { 1, 2, 4 })
        {
            var sb = new SceneBuilder();
            int wall = sb.Material(new Vector3(0.5f), emissive: new Vector3(1));
            const float h = 100;
            sb.Quad(new(-h, -h, -h), new(h, -h, -h), new(h, h, -h), new(-h, h, -h), wall, 1);
            sb.Quad(new(-h, -h, h), new(-h, h, h), new(h, h, h), new(h, -h, h), wall, 1);
            sb.Quad(new(-h, -h, -h), new(-h, h, -h), new(-h, h, h), new(-h, -h, h), wall, 1);
            sb.Quad(new(h, -h, -h), new(h, -h, h), new(h, h, h), new(h, h, -h), wall, 1);
            sb.Quad(new(-h, -h, -h), new(-h, -h, h), new(h, -h, h), new(h, -h, -h), wall, 1);
            sb.Quad(new(-h, h, -h), new(h, h, -h), new(h, h, h), new(-h, h, h), wall, 1);
            sb.Job.Settings.Bounces = bounces;
            sb.Job.Settings.GatherRays = 1024;
            int rec = sb.Receiver([]);
            for (int i = 0; i < 16; i++)
                sb.Sample(new Vector3(-75 + 10 * i, 0, -h), Vector3.UnitZ, rec, 1, 5);
            var output = Solve(sb);
            float series = 0;
            for (int k = 0; k < Math.Max(1, bounces); k++)
                series += MathF.Pow(0.5f, k);
            double mean = output.Simple.Average(v => v.X);
            TestContext?.WriteLine($"{bounces} bounces: mean {mean:F4}, expected {MathF.PI * series:F4}");
            Assert.AreEqual(MathF.PI * series, mean, MathF.PI * series * 0.02, $"{bounces} bounces");
        }
    }

    [TestMethod]
    public void SkyLight_IsOccludedUnderAnOverhang()
    {
        var sb = new SceneBuilder();
        int black = sb.Material(Vector3.Zero);
        sb.HorizontalSquare(0, 0, 2000, 0, true, black, 1);
        sb.HorizontalSquare(0, 0, 60, 30, false, black, 2, n: 2);
        sb.Scene.Lights.Add(new SolverLight { Type = SolverLightType.Sky, Color = new Vector3(1, 0.5f, 0.25f), LowerColor = new Vector3(5) });
        sb.Job.Settings.GatherRays = 256;
        int rec = sb.Receiver([0]);
        int under = sb.Sample(new Vector3(0, 0, 0), Vector3.UnitZ, rec, 1);
        int open = sb.Sample(new Vector3(1500, 0, 0), Vector3.UnitZ, rec, 1);
        int edge = sb.Sample(new Vector3(60, 0, 0), Vector3.UnitZ, rec, 1);
        var output = Solve(sb);
        TestContext?.WriteLine($"under {output.Simple[under]}, edge {output.Simple[edge]}, open {output.Simple[open]}");
        AssertClose(MathF.PI, output.Simple[open].X, 1e-3f, "an open upward surface gathers π × the upper sky colour");
        AssertClose(MathF.PI * 0.25f, output.Simple[open].Z, 1e-3f, "upper sky colour (B)");
        Assert.IsTrue(output.Simple[under].X < 0.3f * output.Simple[open].X, "darker under the overhang");
        Assert.IsTrue(output.Simple[edge].X > output.Simple[under].X && output.Simple[edge].X < output.Simple[open].X, "the edge is in between");
        Assert.AreEqual(output.Simple[open], output.IndirectSimple[open], "sky light is all indirect");
    }

    [TestMethod]
    public void AmbientOcclusion_DarkensUnderTheOverhang()
    {
        var sb = new SceneBuilder();
        int grey = sb.Material(new Vector3(0.5f));
        sb.HorizontalSquare(0, 0, 2000, 0, true, grey, 1);
        sb.HorizontalSquare(0, 0, 60, 30, false, grey, 2, SolverTriangleFlags.CastShadow, 2);
        sb.Scene.Lights.Add(new SolverLight { Type = SolverLightType.Directional, Direction = Vector3.Normalize(new Vector3(1, 0, -1)), Color = Vector3.One });
        sb.Job.Settings.AmbientOcclusion = true;
        sb.Job.Settings.DirectOcclusionFraction = 1;
        sb.Job.Settings.MaxOcclusionDistance = 100;
        int rec = sb.Receiver([0]);
        int under = sb.Sample(new Vector3(-45, 0, 0), Vector3.UnitZ, rec, 1);
        int open = sb.Sample(new Vector3(-500, 0, 0), Vector3.UnitZ, rec, 1);
        var withAo = Solve(sb);
        sb.Job.Settings.AmbientOcclusion = false;
        var without = Solve(sb);
        // the directional light reaches under the overhang's edge at x = -45 (it comes in at 45°)
        Assert.IsTrue(without.Simple[under].X > 0.5f);
        Assert.IsTrue(withAo.Simple[under].X < without.Simple[under].X * 0.9f, "AO darkens the occluded sample");
        AssertClose(without.Simple[open].X, withAo.Simple[open].X, 1e-4f, "AO leaves open ground alone");
    }

    [TestMethod]
    public void Filter_SmoothsIndirectWithinAGrid()
    {
        SolverOutput Run(bool filter)
        {
            var sb = ClosedBox(SolverDevice.Cpu, 1, 16);
            sb.Job.Settings.FilterIndirect = filter;
            var floor = new SolverSampleGrid { Width = 9, Height = 9, Samples = Enumerable.Range(0, 81).ToArray() };
            var wall = new SolverSampleGrid { Width = 9, Height = 9, Samples = Enumerable.Range(81, 81).ToArray() };
            sb.Job.Grids = [floor, wall];
            return Solve(sb);
        }
        static double Roughness(Vector3[] v)
        {
            double sum = 0;
            for (int j = 0; j < 9; j++)
                for (int i = 1; i < 9; i++)
                    sum += Math.Abs(v[j * 9 + i].X - v[j * 9 + i - 1].X);
            return sum;
        }
        var raw = Run(false);
        var filtered = Run(true);
        double r0 = Roughness(raw.IndirectSimple), r1 = Roughness(filtered.IndirectSimple);
        TestContext?.WriteLine($"roughness {r0:F4} → {r1:F4}");
        Assert.IsTrue(r1 < r0, "the filter smooths the gathered light");
        double m0 = raw.IndirectSimple.Take(81).Average(v => v.X), m1 = filtered.IndirectSimple.Take(81).Average(v => v.X);
        Assert.AreEqual(m0, m1, m0 * 0.25, "and keeps its level");
    }

    [TestMethod]
    public void SameSeed_IsRepeatable()
    {
        var a = Solve(ClosedBox(SolverDevice.Cpu, 2, 32));
        var b = Solve(ClosedBox(SolverDevice.Cpu, 2, 32));
        CollectionAssert.AreEqual(a.Simple, b.Simple);
        CollectionAssert.AreEqual(a.Directional1, b.Directional1);
    }

    [TestMethod]
    public void GpuAndWarp_Agree()
    {
        if (!LightSolver.IsGpuAvailable())
            Assert.Inconclusive("No Direct3D 11 GPU.");
        var cpu = Solve(ClosedBox(SolverDevice.Cpu, 3, 64));
        var gpu = Solve(ClosedBox(SolverDevice.Gpu, 3, 64));
        TestContext?.WriteLine($"GPU device: {gpu.Statistics.DeviceName}");
        Assert.AreNotEqual(cpu.Statistics.DeviceName, gpu.Statistics.DeviceName);
        int n = cpu.Simple.Length, close = 0;
        double sumDiff = 0, sumMag = 0, maxRel = 0;
        for (int i = 0; i < n; i++)
        {
            foreach (var (x, y) in new[] { (cpu.Simple[i], gpu.Simple[i]), (cpu.Directional0[i], gpu.Directional0[i]), (cpu.Directional2[i], gpu.Directional2[i]) })
            {
                double diff = (x - y).Length(), mag = Math.Max(x.Length(), 1e-3);
                sumDiff += diff;
                sumMag += mag;
                maxRel = Math.Max(maxRel, diff / mag);
            }
            if ((cpu.Simple[i] - gpu.Simple[i]).Length() <= 1e-3 * Math.Max(cpu.Simple[i].Length(), 1e-2))
                close++;
        }
        TestContext?.WriteLine($"mean relative difference {sumDiff / sumMag:E2}, max {maxRel:E2}, {close}/{n} samples within 1e-3");
        Assert.IsTrue(sumDiff / sumMag < 2e-3, "GPU and WARP agree on average");
        Assert.IsTrue(close >= n * 0.95, "nearly every sample agrees to float noise");
    }

    sealed class SyncProgress(Action<SolverProgress> onReport) : IProgress<SolverProgress>
    {
        public void Report(SolverProgress value) => onReport(value);
    }

    [TestMethod]
    public void Cancellation_StopsTheSolve()
    {
        using var cts = new CancellationTokenSource();
        var stages = new List<string>();
        var sb = ClosedBox(SolverDevice.Cpu, 3, 64);
        var progress = new SyncProgress(p =>
        {
            stages.Add(p.Stage);
            if (p.Stage == "Bounced light")
                cts.Cancel();
        });
        Assert.ThrowsExactly<OperationCanceledException>(() => LightSolver.Solve(sb.Job, progress, cts.Token));
        CollectionAssert.Contains(stages, "Direct lighting");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => LightSolver.Solve(ClosedBox(SolverDevice.Cpu, 1).Job, null, cancelled.Token));
    }

    [TestMethod]
    public void SpotCone_AndTwoSidedLighting()
    {
        var sb = new SceneBuilder();
        int mat = sb.Material(new Vector3(0.5f));
        const float height = 100;
        float cosInner = MathF.Cos(20 * MathF.PI / 180), cosOuter = MathF.Cos(40 * MathF.PI / 180);
        sb.Scene.Lights.Add(new SolverLight
        {
            Type = SolverLightType.Spot, Position = new Vector3(0, 0, height), Direction = -Vector3.UnitZ, Radius = 1000, FalloffExponent = 2,
            CosInnerCone = cosInner, CosOuterCone = cosOuter, Color = Vector3.One,
        });
        int rec = sb.Receiver([0], gather: false);
        int twoSided = sb.Receiver([0], gather: false);
        sb.Job.Receivers[twoSided].TwoSidedLighting = true;
        float[] angles = [10, 30, 50];
        foreach (float a in angles)
            sb.Sample(new Vector3(height * MathF.Tan(a * MathF.PI / 180), 0, 0), Vector3.UnitZ, rec, texelRadius: 0);
        int flippedOneSided = sb.Sample(new Vector3(0, 0, 0), -Vector3.UnitZ, rec, texelRadius: 0);
        int flippedTwoSided = sb.Sample(new Vector3(0, 0, 0), -Vector3.UnitZ, twoSided, texelRadius: 0);
        var output = Solve(sb);
        for (int i = 0; i < angles.Length; i++)
        {
            float c = MathF.Cos(angles[i] * MathF.PI / 180), d = height / c;
            float cone = Math.Clamp((c - cosOuter) / (cosInner - cosOuter), 0, 1);
            AssertClose(Attenuation(d, 1000, 2) * cone * cone * c, output.Simple[i].X, 1e-3f, $"spot at {angles[i]}°");
        }
        AssertClose(0, output.Simple[flippedOneSided].X, 1e-6f, "a light behind a one-sided receiver adds nothing");
        AssertClose(Attenuation(height, 1000, 2), output.Simple[flippedTwoSided].X, 1e-3f, "two-sided lighting flips the frame");
    }

    // ------------------------------------------------------------------ performance (set LEX_SOLVER_PERF=1 to run)

    [TestMethod]
    public void Performance_RaysPerSecond()
    {
        if (Environment.GetEnvironmentVariable("LEX_SOLVER_PERF") != "1")
            Assert.Inconclusive("Set LEX_SOLVER_PERF=1 to measure.");
        int gpuSamples = int.TryParse(Environment.GetEnvironmentVariable("LEX_SOLVER_PERF_SAMPLES"), out int g) ? g : 1 << 20;
        int warpSamples = int.TryParse(Environment.GetEnvironmentVariable("LEX_SOLVER_PERF_WARP_SAMPLES"), out int w) ? w : 1 << 16;
        foreach (var (device, count) in new[] { (SolverDevice.Gpu, gpuSamples), (SolverDevice.Cpu, warpSamples) })
        {
            if (device == SolverDevice.Gpu && !LightSolver.IsGpuAvailable())
                continue;
            var sb = PerfScene(device, count);
            var output = Solve(sb, out var elapsed);
            var st = output.Statistics;
            double traceSeconds = (st.DirectTime + st.IndirectTime).TotalSeconds;
            TestContext?.WriteLine($"{st.DeviceName}: {sb.Scene.Triangles.Count:N0} triangles, {count:N0} samples, BVH {st.BvhTime.TotalSeconds:F2} s, " +
                $"direct {st.DirectTime.TotalSeconds:F2} s, indirect {st.IndirectTime.TotalSeconds:F2} s, total {st.TotalTime.TotalSeconds:F2} s, " +
                $"{st.RaysCast:N0} rays, {st.RaysCast / traceSeconds / 1e6:F1} Mrays/s");
        }
    }

    /// <summary>About a million triangles: a bumpy terrain under a field of boxes, lit by a sun, two point lights and a sky</summary>
    static SceneBuilder PerfScene(SolverDevice device, int sampleCount)
    {
        var sb = new SceneBuilder(device);
        int ground = sb.Material(new Vector3(0.4f, 0.35f, 0.3f));
        int boxMat = sb.Material(new Vector3(0.6f));
        const int terrain = 500; // 500² cells, 500k triangles
        const float size = 10000;
        int baseIndex = sb.Scene.Vertices.Count;
        static float Height(float x, float y) => 40 * MathF.Sin(x * 0.003f) * MathF.Cos(y * 0.002f) + 10 * MathF.Sin(x * 0.02f + y * 0.017f);
        for (int j = 0; j <= terrain; j++)
            for (int i = 0; i <= terrain; i++)
            {
                float x = -size / 2 + size * i / terrain, y = -size / 2 + size * j / terrain;
                const float e = 1;
                Vector3 n = Vector3.Normalize(new Vector3(Height(x - e, y) - Height(x + e, y), Height(x, y - e) - Height(x, y + e), 2 * e));
                sb.Scene.Vertices.Add(new SolverVertex { Position = new Vector3(x, y, Height(x, y)), Normal = n, MaterialUV = new Vector2(x / 256, y / 256) });
            }
        for (int j = 0; j < terrain; j++)
            for (int i = 0; i < terrain; i++)
            {
                uint a = (uint)(baseIndex + j * (terrain + 1) + i), b = a + 1, c = a + terrain + 2, d = a + terrain + 1;
                sb.Scene.Triangles.Add(new SolverTriangle { V0 = a, V1 = b, V2 = c, Material = (uint)ground, Instance = 1, Flags = SolverTriangleFlags.CastShadow });
                sb.Scene.Triangles.Add(new SolverTriangle { V0 = a, V1 = c, V2 = d, Material = (uint)ground, Instance = 1, Flags = SolverTriangleFlags.CastShadow });
            }
        // boxes, finely tessellated: 5 visible faces × 2·8² triangles each = 640 per box, 800 boxes = 512k triangles
        var rng = new Random(1);
        for (int k = 0; k < 800; k++)
        {
            float cx = (float)(rng.NextDouble() - 0.5) * size * 0.9f, cy = (float)(rng.NextDouble() - 0.5) * size * 0.9f;
            float hw = 30 + (float)rng.NextDouble() * 100, hh = 50 + (float)rng.NextDouble() * 300;
            float z0 = Height(cx, cy) - 20;
            uint inst = (uint)(10 + k);
            Vector3 p000 = new(cx - hw, cy - hw, z0), p100 = new(cx + hw, cy - hw, z0), p110 = new(cx + hw, cy + hw, z0), p010 = new(cx - hw, cy + hw, z0);
            Vector3 up = new(0, 0, hh);
            sb.Quad(p000 + up, p100 + up, p110 + up, p010 + up, boxMat, inst, n: 8);
            sb.Quad(p000, p100, p100 + up, p000 + up, boxMat, inst, n: 8);
            sb.Quad(p100, p110, p110 + up, p100 + up, boxMat, inst, n: 8);
            sb.Quad(p110, p010, p010 + up, p110 + up, boxMat, inst, n: 8);
            sb.Quad(p010, p000, p000 + up, p010 + up, boxMat, inst, n: 8);
        }
        sb.Scene.Lights.Add(new SolverLight { Type = SolverLightType.Directional, Direction = Vector3.Normalize(new Vector3(0.4f, 0.3f, -0.8f)), Color = new Vector3(3, 2.8f, 2.5f), SourceSize = 0.01f });
        sb.Scene.Lights.Add(PointLight(new Vector3(0, 0, 200), 3000, 30, new Vector3(5, 3, 1)));
        sb.Scene.Lights.Add(PointLight(new Vector3(2000, -1500, 150), 2000, 20, new Vector3(1, 3, 5)));
        sb.Scene.Lights.Add(new SolverLight { Type = SolverLightType.Sky, Color = new Vector3(0.2f, 0.25f, 0.35f), LowerColor = new Vector3(0.05f) });
        sb.Job.Settings.ShadowRays = 8;
        sb.Job.Settings.PenumbraShadowRays = 32;
        sb.Job.Settings.GatherRays = 64;
        sb.Job.Settings.Bounces = 3;
        sb.Job.Settings.RayOffset = 0.5f;
        sb.Job.Settings.NormalOffset = 0.5f;
        int rec = sb.Receiver([0, 1, 2]);
        // samples on the terrain
        int side = (int)MathF.Ceiling(MathF.Sqrt(sampleCount));
        for (int s = 0; s < sampleCount; s++)
        {
            float x = -size * 0.45f + size * 0.9f * (s % side) / side, y = -size * 0.45f + size * 0.9f * (s / side) / side;
            const float e = 1;
            Vector3 n = Vector3.Normalize(new Vector3(Height(x - e, y) - Height(x + e, y), Height(x, y - e) - Height(x, y + e), 2 * e));
            sb.Sample(new Vector3(x, y, Height(x, y)), n, rec, 1, size * 0.9f / side * 0.7f);
        }
        return sb;
    }
}
