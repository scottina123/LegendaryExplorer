using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Threading;
using LegendaryExplorer.Lightmaps.Codec;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Lightmaps.Lightmass;

/// <summary>
/// Bakes a job's levels with UDK's Lightmass and with LEX's solver, from one scene so their mappings match, and reports how far apart they are, for
/// tuning LEX's solver: <c>LegendaryExplorer.exe --compare-lighting job.json [--log file] [--heatmaps folder]</c>. Nothing is written to the levels or the
/// texture cache: both results stay in memory, and the levels are opened fresh and discarded.
/// </summary>
public static class LightmassCompare
{
    public const string Switch = "--compare-lighting";

    /// <summary>How the two results of one mapping differ</summary>
    public sealed class MappingComparison
    {
        public string Name;
        public bool Texture;
        public int SizeX, SizeY;
        /// <summary>Texels or vertices both cover, and those only one of them covers</summary>
        public int Covered, CoverageMismatches;
        /// <summary>|local − reference| / reference of the simple coefficient's luminance over the covered texels</summary>
        public double SimpleMeanRelative, SimpleP95Relative;
        /// <summary>Mean luminance of the simple coefficient over the covered texels: UDK's and LEX's</summary>
        public double MeanReference, MeanLocal;
        /// <summary>Mean absolute error (RGB mean) of each directional component</summary>
        public double[] DirectionalMae = new double[3];
        /// <summary>Per light both have a shadow map of: the mean absolute visibility (or distance field value) difference over covered texels</summary>
        public List<(string Light, bool DistanceField, double MeanDifference)> Shadows = [];
        public List<string> ShadowsOnlyInReference = [], ShadowsOnlyInLocal = [];
        public List<string> LightsOnlyInReference = [], LightsOnlyInLocal = [];
        public float[] RelativeErrors;
        public LightmassMappingResult Reference, Local;
    }

    /// <summary>Compares one mapping's two results; null when they don't have the same layout</summary>
    public static MappingComparison Compare(string name, LightmassMappingResult reference, LightmassMappingResult local, Func<Guid, string> lightName)
    {
        if (reference.IsTextureMapping != local.IsTextureMapping || reference.SizeX != local.SizeX || reference.SizeY != local.SizeY) return null;
        int count = reference.SizeX * reference.SizeY;
        var c = new MappingComparison
        {
            Name = name, Texture = reference.IsTextureMapping, SizeX = reference.SizeX, SizeY = reference.SizeY, Reference = reference, Local = local,
            RelativeErrors = new float[count],
        };
        Array.Fill(c.RelativeErrors, float.NaN);
        var both = new bool[count];
        double meanReference = 0;
        for (int i = 0; i < count; i++)
        {
            bool r = reference.Coverage[i] != 0, l = local.Coverage[i] != 0;
            if (r != l) c.CoverageMismatches++;
            if (!(both[i] = r && l)) continue;
            c.Covered++;
            meanReference += Luminance(reference.Simple[i]);
            c.MeanLocal += Luminance(local.Simple[i]);
        }
        if (c.Covered == 0) return c;
        meanReference /= c.Covered;
        c.MeanReference = meanReference;
        c.MeanLocal /= c.Covered;
        // Relative to the texel's own reference, floored at 1% of the mapping's mean so near-black texels don't dominate
        double floor = Math.Max(1e-4, meanReference * 0.01);
        var relative = new List<float>(c.Covered);
        for (int i = 0; i < count; i++)
        {
            if (!both[i]) continue;
            double r = Luminance(reference.Simple[i]), l = Luminance(local.Simple[i]);
            float e = (float)(Math.Abs(l - r) / Math.Max(r, floor));
            c.RelativeErrors[i] = e;
            relative.Add(e);
            for (int k = 0; k < 3; k++) c.DirectionalMae[k] += Mean(Vector3.Abs(reference.Directional[i][k] - local.Directional[i][k]));
        }
        for (int k = 0; k < 3; k++) c.DirectionalMae[k] /= c.Covered;
        c.SimpleMeanRelative = relative.Average();
        relative.Sort();
        c.SimpleP95Relative = relative[Math.Min(relative.Count - 1, (int)(relative.Count * 0.95))];

        var referenceLights = reference.LightGuids ?? [];
        var localLights = local.LightGuids ?? [];
        c.LightsOnlyInReference = referenceLights.Except(localLights).Select(lightName).ToList();
        c.LightsOnlyInLocal = localLights.Except(referenceLights).Select(lightName).ToList();

        var localShadows = local.ShadowMaps.ToDictionary(s => s.LightGuid);
        foreach (var r in reference.ShadowMaps)
        {
            if (!localShadows.Remove(r.LightGuid, out var l)) { c.ShadowsOnlyInReference.Add(lightName(r.LightGuid)); continue; }
            double sum = 0; int n = 0;
            for (int i = 0; i < count; i++)
            {
                if (!both[i]) continue;
                sum += Math.Abs(ShadowValue(r, i) - ShadowValue(l, i));
                n++;
            }
            c.Shadows.Add((lightName(r.LightGuid), r.SignedDistanceField || l.SignedDistanceField, n > 0 ? sum / n : 0));
        }
        c.ShadowsOnlyInLocal = localShadows.Keys.Select(lightName).ToList();
        return c;
    }

    /// <summary>A shadow map's value at a texel: visibility (decoded from gamma 2.2 for texture maps), or a distance field's value 0..1</summary>
    static double ShadowValue(LightmassShadowMap s, int i) =>
        s.Samples != null ? s.Samples[i] : s.SignedDistanceField ? s.Values[i] / 255.0 : Math.Pow(s.Values[i] / 255.0, 2.2);

    static double Luminance(Vector3 c) => 0.3 * c.X + 0.59 * c.Y + 0.11 * c.Z;
    static double Mean(Vector3 v) => (v.X + v.Y + v.Z) / 3;

    // ================= the run =================

    /// <summary>Runs the comparison of a job's levels and logs it; true when both bakes finished</summary>
    /// <param name="referenceCache">A file UDK's results are saved to, and read from on later runs instead of baking them again (null: always bake)</param>
    /// <param name="only">Compares only the mappings whose description contains this (null: all)</param>
    public static bool Run(LightmassBakeJob job, string heatmapFolder, Action<string> log, CancellationToken cancel, string referenceCache = null, string only = null)
    {
        var problems = job.Problems();
        if (!job.UsesUdk) problems.AddRange(job.UdkProblems());
        if (problems.Count > 0)
        {
            foreach (string p in problems) log($"Problem: {p}");
            return false;
        }
        var packages = new List<IMEPackage>();
        try
        {
            var contextOnly = job.OpenLevels(packages, log, cancel);
            var settings = job.BakeSettings(contextOnly);
            settings.Solver = LightmassSolverChoice.UdkLightmass;
            var game = packages[0].Game;
            var udkIni = LightmassBake.LoadIni(settings, game);
            // Tuning: LEX_SOLVER_DEBUG=udkini:Section.Key=Value changes [DevOptions.Section] Key in UDK's ini (for probing what Lightmass does)
            foreach (var t in System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',') ?? [])
                if (t.StartsWith("udkini:") && t.IndexOf('=') is int eq and > 0 && t.LastIndexOf('.', eq) is int dot and > 7)
                {
                    udkIni.GetOrAddSection("DevOptions." + t[7..dot]).SetSingleEntry(t[(dot + 1)..eq], t[(eq + 1)..]);
                    log($"UDK ini: [DevOptions.{t[7..dot]}] {t[(dot + 1)..eq]}={t[(eq + 1)..]}");
                }
            using var built = LightmassBake.BuildScene(packages, settings, udkIni, log);
            var scene = built.Scene;
            // Tuning: LEX_SOLVER_DEBUG=droplights:Name removes the lights whose Actor.Component name contains Name from the scene, for both
            if (System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',').FirstOrDefault(t => t.StartsWith("droplights:", StringComparison.OrdinalIgnoreCase)) is { } drop)
            {
                string token = drop[11..];
                bool Dropped(LightmassLight l) => $"{l.Light.Export?.Parent?.ObjectName.Instanced}.{l.Light.Export?.ObjectName.Instanced}".Contains(token, StringComparison.OrdinalIgnoreCase);
                int removed = scene.Lights.RemoveAll(Dropped);
                foreach (var i in scene.Instances) i.RelevantLights.RemoveAll(Dropped);
                foreach (var b in scene.BspMappings) b.RelevantLights.RemoveAll(Dropped);
                foreach (var t in scene.TerrainMappings) t.RelevantLights.RemoveAll(Dropped);
                log($"Removed {removed} lights matching {token} from the scene");
            }
            // Tuning: LEX_SOLVER_DEBUG=keeplights:A|B keeps only the lights whose Actor.Component name contains A or B
            if (System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',').FirstOrDefault(t => t.StartsWith("keeplights:", StringComparison.OrdinalIgnoreCase)) is { } keep)
            {
                var tokens = keep[11..].Split('|');
                bool Dropped(LightmassLight l) => !tokens.Any(token => $"{l.Light.Export?.Parent?.ObjectName.Instanced}.{l.Light.Export?.ObjectName.Instanced}".Contains(token, StringComparison.OrdinalIgnoreCase));
                int removed = scene.Lights.RemoveAll(Dropped);
                foreach (var i in scene.Instances) i.RelevantLights.RemoveAll(Dropped);
                foreach (var b in scene.BspMappings) b.RelevantLights.RemoveAll(Dropped);
                foreach (var t in scene.TerrainMappings) t.RelevantLights.RemoveAll(Dropped);
                log($"Kept {scene.Lights.Count} lights matching {keep[11..]}, removed {removed}");
            }
            // Tuning: LEX_SOLVER_DEBUG=worldbounces:N sets NumIndirectLightingBounces for both
            if (System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',').FirstOrDefault(t => t.StartsWith("worldbounces:", StringComparison.OrdinalIgnoreCase)) is { } wb)
            {
                scene.World.NumIndirectLightingBounces = int.Parse(wb[13..]);
                log($"NumIndirectLightingBounces {scene.World.NumIndirectLightingBounces} for both");
            }
            // Tuning: LEX_SOLVER_DEBUG=albedoconst:X gives every opaque material a constant albedo X, for both
            if (System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',').FirstOrDefault(t => t.StartsWith("albedoconst:", StringComparison.OrdinalIgnoreCase)) is { } ac)
            {
                float albedo = float.Parse(ac[12..], System.Globalization.CultureInfo.InvariantCulture);
                foreach (var m in scene.Materials) if (m.Diffuse != null) { m.Diffuse = MaterialSamples.Constant(new Vector3(albedo), 0); m.Guid = Guid.NewGuid(); }
                log($"Every material's albedo is {albedo} for both");
            }
            // Tuning: LEX_SOLVER_DEBUG=bounces0 bakes both without bounced light (Lightmass's NumIndirectLightingBounces 0), to compare direct lighting alone
            if (System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Contains("bounces0", StringComparison.OrdinalIgnoreCase) == true)
            {
                scene.World.NumIndirectLightingBounces = 0;
                log("Bounced light off (NumIndirectLightingBounces 0) for both");
            }
            var mappings = scene.Mappings.Where(m => only == null || Describe(m).Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mappings.Count == 0)
            {
                log("Nothing to compare: no compatible lightmap or shadow-only mappings in these levels.");
                return true;
            }

            log("=== UDK Lightmass ===");
            var reference = new LightmassBakeResult { Scene = scene };
            if (referenceCache != null && File.Exists(referenceCache))
            {
                int read = ReadReference(referenceCache, scene, reference);
                log($"Read UDK's results for {read} mappings from {referenceCache}");
            }
            else
            {
                LightmassBake.SolveWithLightmass(packages, scene, settings, udkIni, reference, log, cancel);
                if (referenceCache != null)
                {
                    WriteReference(referenceCache, scene, reference);
                    log($"Saved UDK's results to {referenceCache}");
                }
            }

            log("=== LEX's solver ===");
            settings.Solver = job.Solver == LightmassSolverChoice.Cpu ? LightmassSolverChoice.Cpu : LightmassSolverChoice.Gpu;
            var localIni = LightmassBake.LoadIni(settings, game);
            var local = new LightmassBakeResult { Scene = scene };
            var selected = mappings.ToHashSet();
            // Tuning: LEX_SOLVER_DEBUG=udkself compares UDK with a second UDK bake of the same scene (its own run-to-run noise)
            if (System.Environment.GetEnvironmentVariable("LEX_SOLVER_DEBUG")?.Split(',').Contains("udkself") == true)
            {
                settings.Solver = LightmassSolverChoice.UdkLightmass;
                LightmassBake.SolveWithLightmass(packages, scene, settings, udkIni, local, log, cancel);
                local.SolverName = "UDK Lightmass (second run)";
            }
            else
            LightmassBake.SolveLocally(scene, settings, localIni, local, log, cancel, only == null ? null : selected.Contains);

            Report(scene, mappings, reference, local, heatmapFolder, log);
            return true;
        }
        finally
        {
            foreach (var package in packages) package.Dispose();
        }
    }

    static void Report(LightmassScene scene, List<LightmassMapping> mappings, LightmassBakeResult reference, LightmassBakeResult local, string heatmapFolder,
        Action<string> log)
    {
        var lights = scene.Lights.ToDictionary(l => l.Guid);
        string LightName(Guid g) => lights.TryGetValue(g, out var l)
            ? $"{(l.Light.Export?.Parent is { } owner ? owner.ObjectName.Instanced + "." : "")}{l.Light.Export?.ObjectName.Instanced ?? l.Light.Type.ToString()}{(l.ShadowOnly ? " (shadow-only)" : "")}" : g.ToString();

        var comparisons = new List<MappingComparison>();
        int missingReference = 0, missingLocal = 0, mismatched = 0;
        foreach (var mapping in mappings)
        {
            bool hasReference = reference.Results.TryGetValue(mapping.Guid, out var r), hasLocal = local.Results.TryGetValue(mapping.Guid, out var l);
            if (!hasReference) missingReference++;
            if (!hasLocal) missingLocal++;
            if (!hasReference || !hasLocal) continue;
            var c = Compare(Describe(mapping), r, l, LightName);
            if (c == null) { mismatched++; log($"{Describe(mapping)}: the results' layouts differ (UDK {r.SizeX}x{r.SizeY}, LEX {l.SizeX}x{l.SizeY})"); continue; }
            comparisons.Add(c);
        }

        log("=== Comparison (LEX's solver against UDK Lightmass) ===");
        log($"UDK Lightmass: {reference.LightmassTime:hh\\:mm\\:ss}; {local.SolverName}: {local.LightmassTime:hh\\:mm\\:ss}");
        if (missingReference > 0 || missingLocal > 0 || mismatched > 0)
            log($"{missingReference} mappings missing from UDK's results, {missingLocal} from LEX's, {mismatched} with different layouts");
        foreach (var c in comparisons.OrderByDescending(c => c.SimpleMeanRelative))
        {
            var line = $"{c.Name} [{(c.Texture ? $"{c.SizeX}x{c.SizeY}" : $"{c.SizeX} vertices")}, {c.Covered} covered" +
                       (c.CoverageMismatches > 0 ? $", {c.CoverageMismatches} coverage mismatches" : "") + "]: " +
                       $"simple rel. error mean {c.SimpleMeanRelative:P1}, p95 {c.SimpleP95Relative:P1}; brightness UDK {c.MeanReference:G3}, LEX {c.MeanLocal:G3}; " +
                       $"directional MAE {c.DirectionalMae[0]:G3} / {c.DirectionalMae[1]:G3} / {c.DirectionalMae[2]:G3}";
            log(line);
            foreach (var (light, sdf, diff) in c.Shadows) log($"    shadow map {light}{(sdf ? " (distance field)" : "")}: mean difference {diff:G3}");
            foreach (string s in c.ShadowsOnlyInReference) log($"    shadow map only in UDK's: {s}");
            foreach (string s in c.ShadowsOnlyInLocal) log($"    shadow map only in LEX's: {s}");
            foreach (string s in c.LightsOnlyInReference) log($"    lightmap light only in UDK's: {s}");
            foreach (string s in c.LightsOnlyInLocal) log($"    lightmap light only in LEX's: {s}");
            if (comparisons.Count <= 5)
            {
                log($"    UDK's lightmap lights: {string.Join(", ", c.Reference.LightGuids.Select(LightName))}");
                log($"    LEX's lightmap lights: {string.Join(", ", c.Local.LightGuids.Select(LightName))}");
            }
        }

        // Overall: weighted by covered texels, p95 over all of them
        int covered = comparisons.Sum(c => c.Covered);
        if (covered > 0)
        {
            var all = comparisons.SelectMany(c => c.RelativeErrors.Where(e => !float.IsNaN(e))).ToArray();
            Array.Sort(all);
            double mean = all.Average();
            double p95 = all[Math.Min(all.Length - 1, (int)(all.Length * 0.95))];
            var dir = Enumerable.Range(0, 3).Select(k => comparisons.Sum(c => c.DirectionalMae[k] * c.Covered) / covered).ToArray();
            var shadowDiffs = comparisons.SelectMany(c => c.Shadows).ToList();
            double meanUdk = comparisons.Sum(c => c.MeanReference * c.Covered) / covered, meanLex = comparisons.Sum(c => c.MeanLocal * c.Covered) / covered;
            log($"Overall brightness (mean simple luminance): UDK {meanUdk:G4}, LEX {meanLex:G4} (LEX/UDK {meanLex / Math.Max(1e-9, meanUdk):F3})");
            log($"Overall over {comparisons.Count} mappings ({covered:N0} covered texels and vertices): simple rel. error mean {mean:P1}, p95 {p95:P1}; " +
                $"directional MAE {dir[0]:G3} / {dir[1]:G3} / {dir[2]:G3}");
            log($"Shadow maps: {shadowDiffs.Count} compared (mean difference {(shadowDiffs.Count > 0 ? shadowDiffs.Average(s => s.MeanDifference) : 0):G3}), " +
                $"{comparisons.Sum(c => c.ShadowsOnlyInReference.Count)} only in UDK's, {comparisons.Sum(c => c.ShadowsOnlyInLocal.Count)} only in LEX's");
            log($"Lightmap lights: {comparisons.Sum(c => c.LightsOnlyInReference.Count)} only in UDK's lists, {comparisons.Sum(c => c.LightsOnlyInLocal.Count)} only in LEX's");
        }

        if (!string.IsNullOrWhiteSpace(heatmapFolder))
        {
            Directory.CreateDirectory(heatmapFolder);
            int written = 0;
            foreach (var c in comparisons.Where(c => c.Texture && c.Covered > 0).OrderByDescending(c => c.SimpleMeanRelative).Take(20))
            {
                string file = Path.Combine(heatmapFolder, $"{++written:D2}_{Sanitize(c.Name)}.png");
                File.WriteAllBytes(file, Heatmap(c));
            }
            log($"Wrote {written} heatmaps (UDK's simple lighting | LEX's | relative error) to {heatmapFolder}");
        }
    }

    // UDK's results by mapping description (mapping GUIDs are new on every scene build); light GUIDs are the lights' own
    static void WriteReference(string path, LightmassScene scene, LightmassBakeResult reference)
    {
        using var w = new BinaryWriter(File.Create(path));
        var entries = scene.Mappings.Where(m => reference.Results.ContainsKey(m.Guid)).ToList();
        w.Write(entries.Count);
        foreach (var m in entries)
        {
            var r = reference.Results[m.Guid];
            w.Write(Describe(m));
            w.Write(r.IsTextureMapping); w.Write(r.SizeX); w.Write(r.SizeY); w.Write(r.ExecutionTime);
            int n = r.Simple.Length;
            w.Write(n);
            for (int i = 0; i < n; i++)
            {
                var d = r.Directional[i];
                foreach (var v in new[] { d.E0, d.E1, d.E2, r.Simple[i] }) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
            }
            w.Write(r.Coverage);
            w.Write(r.LightGuids.Length);
            foreach (var g in r.LightGuids) w.Write(g.ToByteArray());
            w.Write(r.ShadowMaps.Count);
            foreach (var s in r.ShadowMaps)
            {
                w.Write(s.LightGuid.ToByteArray()); w.Write(s.SignedDistanceField);
                w.Write(s.Values?.Length ?? -1); if (s.Values != null) w.Write(s.Values);
                w.Write(s.Coverage?.Length ?? -1); if (s.Coverage != null) w.Write(s.Coverage);
                w.Write(s.Samples?.Length ?? -1); if (s.Samples != null) foreach (float f in s.Samples) w.Write(f);
            }
        }
    }

    static int ReadReference(string path, LightmassScene scene, LightmassBakeResult reference)
    {
        var byName = scene.Mappings.GroupBy(Describe).ToDictionary(g => g.Key, g => g.First());
        using var r = new BinaryReader(File.OpenRead(path));
        int count = r.ReadInt32(), read = 0;
        for (int e = 0; e < count; e++)
        {
            string name = r.ReadString();
            var result = new LightmassMappingResult { IsTextureMapping = r.ReadBoolean(), SizeX = r.ReadInt32(), SizeY = r.ReadInt32(), ExecutionTime = r.ReadDouble() };
            int n = r.ReadInt32();
            result.Directional = new Directional.Sample[n];
            result.Simple = new Vector3[n];
            Vector3 V() => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            for (int i = 0; i < n; i++)
            {
                result.Directional[i] = new Directional.Sample(V(), V(), V());
                result.Simple[i] = V();
            }
            result.Coverage = r.ReadBytes(n);
            result.LightGuids = new Guid[r.ReadInt32()];
            for (int i = 0; i < result.LightGuids.Length; i++) result.LightGuids[i] = new Guid(r.ReadBytes(16));
            int shadows = r.ReadInt32();
            for (int k = 0; k < shadows; k++)
            {
                var s = new LightmassShadowMap { LightGuid = new Guid(r.ReadBytes(16)), SignedDistanceField = r.ReadBoolean() };
                int len = r.ReadInt32(); if (len >= 0) s.Values = r.ReadBytes(len);
                len = r.ReadInt32(); if (len >= 0) s.Coverage = r.ReadBytes(len);
                len = r.ReadInt32(); if (len >= 0) { s.Samples = new float[len]; for (int i = 0; i < len; i++) s.Samples[i] = r.ReadSingle(); }
                result.ShadowMaps.Add(s);
            }
            if (!byName.TryGetValue(name, out var mapping)) continue;
            result.Guid = mapping.Guid;
            reference.Results[mapping.Guid] = result;
            read++;
        }
        return read;
    }

    static string Describe(LightmassMapping mapping) => mapping switch
    {
        LightmassInstance i => $"{i.Component.FileRef.FileNameNoExtension}: {i.Component.InstancedFullPath} LOD{i.LodIndex}",
        LightmassBspMapping b when b.Lightmaps.Count > 0 => $"{b.Lightmaps[0].Owner.FileRef.FileNameNoExtension}: BSP {b.Lightmaps[0].Owner.ObjectName.Instanced} {b.Lightmaps[0].Slot}" +
                                                            (b.Lightmaps.Count > 1 ? $" (+{b.Lightmaps.Count - 1})" : ""),
        LightmassTerrainMapping t => $"{t.Component.FileRef.FileNameNoExtension}: {t.Component.InstancedFullPath}",
        _ => mapping.Guid.ToString(),
    };

    static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(ch => invalid.Contains(ch) || ch == ' ' ? '_' : ch).ToArray());
        return s.Length > 100 ? s[..100] : s;
    }

    // ================= heatmaps =================

    /// <summary>Three panels side by side, scaled up to at least 128 texels tall: UDK's simple lighting, LEX's (same exposure), and the relative error (0 black, 100% red)</summary>
    public static byte[] Heatmap(MappingComparison c)
    {
        int w = c.SizeX, h = c.SizeY;
        int scale = Math.Max(1, 128 / Math.Max(1, Math.Max(w, h)));
        int panelW = w * scale, panelH = h * scale, gap = 4;
        int width = panelW * 3 + gap * 2, height = panelH;
        var rgb = new byte[width * height * 3];
        double max = 1e-6;
        for (int i = 0; i < w * h; i++)
            if (c.Reference.Coverage[i] != 0) max = Math.Max(max, Luminance(c.Reference.Simple[i]));
        for (int y = 0; y < panelH; y++)
        for (int x = 0; x < width; x++)
        {
            int panel = x / (panelW + gap), px = x - panel * (panelW + gap);
            (byte R, byte G, byte B) color = (32, 32, 32);
            if (px < panelW)
            {
                int i = y / scale * w + px / scale;
                if (panel == 0 && c.Reference.Coverage[i] != 0) color = Gray(Luminance(c.Reference.Simple[i]) / max);
                else if (panel == 1 && c.Local.Coverage[i] != 0) color = Gray(Luminance(c.Local.Simple[i]) / max);
                else if (panel == 2 && !float.IsNaN(c.RelativeErrors[i])) color = Heat(c.RelativeErrors[i]);
            }
            else color = (0, 0, 0);
            int o = (y * width + x) * 3;
            (rgb[o], rgb[o + 1], rgb[o + 2]) = color;
        }
        return Png(width, height, rgb);
    }

    static (byte, byte, byte) Gray(double v)
    {
        byte b = (byte)Math.Clamp((int)(Math.Pow(Math.Clamp(v, 0, 1), 1 / 2.2) * 255), 0, 255);
        return (b, b, b);
    }

    /// <summary>0 black, through blue and yellow, to red at 100% and beyond</summary>
    static (byte, byte, byte) Heat(float e)
    {
        float t = Math.Clamp(e, 0, 1);
        var color = t < 0.25f ? Vector3.Lerp(Vector3.Zero, new Vector3(0, 0, 1), t / 0.25f)
            : t < 0.5f ? Vector3.Lerp(new Vector3(0, 0, 1), new Vector3(0, 1, 0), (t - 0.25f) / 0.25f)
            : t < 0.75f ? Vector3.Lerp(new Vector3(0, 1, 0), new Vector3(1, 1, 0), (t - 0.5f) / 0.25f)
            : Vector3.Lerp(new Vector3(1, 1, 0), new Vector3(1, 0, 0), (t - 0.75f) / 0.25f);
        return ((byte)(color.X * 255), (byte)(color.Y * 255), (byte)(color.Z * 255));
    }

    /// <summary>An 8-bit RGB PNG</summary>
    public static byte[] Png(int width, int height, byte[] rgb)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            WriteBigEndian(output, (uint)data.Length);
            output.Write(typeBytes);
            output.Write(data);
            WriteBigEndian(output, Crc32(data, Crc32(typeBytes, 0xFFFFFFFF)) ^ 0xFFFFFFFF);
        }
        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8; header[9] = 2; // 8 bits, RGB
        Chunk("IHDR", header);
        using (var raw = new MemoryStream())
        {
            using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (int y = 0; y < height; y++)
                {
                    zlib.WriteByte(0); // no filter
                    zlib.Write(rgb, y * width * 3, width * 3);
                }
            }
            Chunk("IDAT", raw.ToArray());
        }
        Chunk("IEND", []);
        return output.ToArray();
    }

    static void WriteBigEndian(Stream s, uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    static void WriteBigEndian(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>Continues a CRC-32 (start at 0xFFFFFFFF, finish by inverting)</summary>
    static uint Crc32(byte[] data, uint crc)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
