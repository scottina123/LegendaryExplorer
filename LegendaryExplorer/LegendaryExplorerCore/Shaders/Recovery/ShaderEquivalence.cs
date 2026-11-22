using System;
using System.Collections.Generic;
using System.Linq;

namespace LegendaryExplorerCore.Shaders.Recovery
{
    /// <summary>The result of <see cref="ShaderEquivalence.Compare"/>.</summary>
    public sealed record ShaderEquivalenceResult(bool Equivalent, double MaxError, string WorstOutput, IReadOnlyList<string> Differences)
    {
        public override string ToString() => Equivalent ? $"equivalent (max error {MaxError:G3})" : $"differs at {WorstOutput} (error {MaxError:G3}): {string.Join("; ", Differences.Take(3))}";
    }

    /// <summary>
    /// Checks two shaders compute the same thing: both are executed symbolically (<see cref="DxbcSymbolicExecutor"/>) and every output
    /// they share is evaluated on the same random inputs. Inputs are matched by meaning (interpolant semantic, constant buffer variable
    /// and element, texture name), so shaders compiled from different code and with different register layouts compare. Texture
    /// samples are a smooth stand-in function of (texture, coordinates), the same in both.
    /// </summary>
    public static class ShaderEquivalence
    {
        public static ShaderEquivalenceResult Compare(byte[] expected, byte[] actual, int trials = 48, double tolerance = 2e-3,
            Func<string, bool> ignoreOutput = null)
        {
            var a = DxbcSymbolicExecutor.Execute(expected);
            var b = DxbcSymbolicExecutor.Execute(actual);
            return Compare(a, b, trials, tolerance, ignoreOutput);
        }

        public static ShaderEquivalenceResult Compare(ShaderDataflow a, ShaderDataflow b, int trials = 48, double tolerance = 2e-3,
            Func<string, bool> ignoreOutput = null)
        {
            // Outputs by semantic, so differing output register numbers still pair up
            Dictionary<string, Expr> BySemantic(ShaderDataflow d) => d.Outputs.ToDictionary(o =>
            {
                int dot = o.Key.IndexOf('.');
                string register = o.Key[..dot];
                return (int.TryParse(register[1..], out int index) && d.OutputSemantics.TryGetValue(index, out string s) ? s : register) + o.Key[dot..];
            }, o => o.Value);
            var outA = BySemantic(a);
            var outB = BySemantic(b);
            var shared = outA.Keys.Intersect(outB.Keys).Where(k => ignoreOutput?.Invoke(k) != true).OrderBy(k => k).ToList();
            var differences = new List<string>();
            foreach (string missing in outA.Keys.Except(outB.Keys).Where(k => ignoreOutput?.Invoke(k) != true)) differences.Add($"{missing} not written");
            double worst = 0;
            string worstOutput = null;
            var random = new Random(1234);
            for (int trial = 0; trial < trials; trial++)
            {
                var inputs = new TrialInputs(random.Next());
                Func<string, double> Leaf = inputs.Leaf;
                Func<SampleInfo, double[], int, double> Sample = inputs.Sample;
                // Evaluate the discard too: a discarded pixel's outputs don't matter
                bool killA = a.Discard.Op != ExprOp.BoolConst && ExprAlgebra.Evaluate(a.Discard, Leaf, Sample) != 0;
                bool killB = b.Discard.Op != ExprOp.BoolConst && ExprAlgebra.Evaluate(b.Discard, Leaf, Sample) != 0;
                if (killA != killB) { differences.Add($"discard differs (trial {trial})"); worst = double.PositiveInfinity; worstOutput = "discard"; continue; }
                if (killA) continue;
                var memoA = new Dictionary<Expr, double>();
                var memoB = new Dictionary<Expr, double>();
                foreach (string key in shared)
                {
                    double va = ExprAlgebra.Evaluate(outA[key], Leaf, Sample, memoA);
                    double vb = ExprAlgebra.Evaluate(outB[key], Leaf, Sample, memoB);
                    if (double.IsNaN(va) && double.IsNaN(vb)) continue;
                    if (double.IsInfinity(va) && va == vb) continue;
                    double error = Math.Abs(va - vb) / Math.Max(1, Math.Abs(va));
                    if (double.IsNaN(error)) error = double.PositiveInfinity;
                    if (error > worst) { worst = error; worstOutput = key; }
                    if (error > tolerance && differences.Count < 12) differences.Add($"{key}: {va:G6} vs {vb:G6}");
                }
            }
            bool equivalent = worst <= tolerance && !differences.Any(d => d.EndsWith("not written") || d.StartsWith("discard"));
            return new ShaderEquivalenceResult(equivalent, worst, worstOutput, differences);
        }

        /// <summary>
        /// Whether two sets of expressions over the same leaves evaluate equal on random inputs (realistic tangent frames).
        /// </summary>
        public static bool SameValues(IReadOnlyList<Expr> a, IReadOnlyList<Expr> b, int trials = 24, double tolerance = 2e-3)
        {
            var random = new Random(4321);
            for (int trial = 0; trial < trials; trial++)
            {
                var inputs = new TrialInputs(random.Next());
                var memo = new Dictionary<Expr, double>();
                for (int i = 0; i < a.Count; i++)
                {
                    double va = ExprAlgebra.Evaluate(a[i], inputs.Leaf, inputs.Sample, memo);
                    double vb = ExprAlgebra.Evaluate(b[i], inputs.Leaf, inputs.Sample, memo);
                    if (double.IsNaN(va) && double.IsNaN(vb)) continue;
                    if (!(Math.Abs(va - vb) / Math.Max(1, Math.Abs(va)) <= tolerance)) return false;
                }
            }
            return true;
        }

        /// <summary>One trial's inputs: leaf values by canonical name, and the smooth texture stand-in.</summary>
        private sealed class TrialInputs
        {
            private readonly int seed;
            private readonly Dictionary<string, double> leafValues = new();

            public TrialInputs(int seed)
            {
                this.seed = seed;
                // A plausible interpolated tangent frame (TANGENTTOWORLD0/2 = TEXCOORD10/11): tangent and normal near unit length and
                // perpendicular, handedness ±1. Unrelated random vectors can be near parallel, a frame no mesh has.
                var frameRandom = new Random(seed);
                double[] Unit()
                {
                    var v = new double[3];
                    double length;
                    do
                    {
                        for (int i = 0; i < 3; i++) v[i] = frameRandom.NextDouble() * 2 - 1;
                        length = Math.Sqrt(v.Sum(x => x * x));
                    } while (length < 0.2);
                    return v.Select(x => x / length).ToArray();
                }
                var tangent = Unit();
                var normal = Unit();
                double along = normal.Zip(tangent, (a, b) => a * b).Sum();
                normal = normal.Zip(tangent, (n, t) => n - along * t).ToArray();
                double normalLength = Math.Sqrt(normal.Sum(x => x * x));
                normal = normal.Select(x => x / normalLength * (0.9 + 0.2 * frameRandom.NextDouble())).ToArray();
                tangent = tangent.Select(x => x * (0.9 + 0.2 * frameRandom.NextDouble())).ToArray();
                for (int i = 0; i < 3; i++)
                {
                    leafValues[$"in:TEXCOORD10.{"xyz"[i]}"] = tangent[i];
                    leafValues[$"in:TEXCOORD11.{"xyz"[i]}"] = normal[i];
                }
                leafValues["in:TEXCOORD11.w"] = frameRandom.Next(2) == 0 ? -1 : 1;
            }

            public double Leaf(string name)
            {
                name = CanonicalLeaf(name);
                if (leafValues.TryGetValue(name, out double v)) return v;
                // Front-facing; other leaves uniform in [-1, 1], away from zero
                v = name == "in:SV_IsFrontFace0.x" ? BitConverter.Int32BitsToSingle(-1)
                    : (Hash(name, seed) % 2000 - 1000) / 900.0;
                if (Math.Abs(v) < 0.05) v += 0.1;
                leafValues[name] = v;
                return v;
            }

            public double Sample(SampleInfo info, double[] coordinates, int component)
            {
                int h = Hash(info.Texture + component, 77);
                double phase = (h % 628) / 100.0;
                double sum = 0;
                int n = info.CoordinateCount;
                for (int i = 0; i < n; i++) sum += coordinates[i] * (1.3 + 0.7 * i + (h % 7) * 0.11);
                return 0.5 + 0.45 * Math.Sin(sum + phase);
            }
        }

        /// <summary>
        /// One name per value: LE1/LE2 declare each uniform scalar alone (UniformPixelScalar_5), LE3 packs four to a vector
        /// (UniformPixelScalars_1, element 1).
        /// </summary>
        private static string CanonicalLeaf(string name)
        {
            foreach (string kind in new[] { "Pixel", "Vertex" })
            {
                string single = $"cb:Uniform{kind}Scalar_";
                if (name.StartsWith(single, StringComparison.Ordinal) && name.EndsWith("[0]", StringComparison.Ordinal) &&
                    int.TryParse(name[single.Length..^3], out int index))
                    return $"cb:Uniform{kind}Scalars_{index / 4}[{index % 4}]";
            }
            return name;
        }

        private static int Hash(string text, int seed)
        {
            unchecked
            {
                int h = seed * 16777619 ^ (int)2166136261;
                foreach (char c in text) h = (h ^ c) * 16777619;
                return h & 0x7fffffff;
            }
        }
    }
}
