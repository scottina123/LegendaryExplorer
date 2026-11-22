using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace LegendaryExplorerCore.Shaders.Recovery
{
    /// <summary>Operations of a scalar shader expression. Comparison and logic operations produce booleans.</summary>
    public enum ExprOp
    {
        Const, BoolConst, Leaf,
        Neg, Abs, Add, Mul, Div, Rsq, Sqrt, Rcp, Exp2, Log2, Min, Max, Sat, Frc, Floor, Ceil, Trunc, Round, Sin, Cos, Ddx, Ddy,
        Select, Lt, Ge, Eq, Ne, And, Or, Not,
        /// <summary>A texture sample (a float4); its scalars are <see cref="Component"/> nodes.</summary>
        Sample,
        Component,
    }

    /// <summary>How a <see cref="ExprOp.Sample"/> node samples: its texture, sampler and method.</summary>
    public sealed record SampleInfo(string Texture, string Sampler, bool Cube, string Method)
    {
        /// <summary>Method: Sample, SampleLevel (last argument the LOD), SampleBias (last argument the bias) or SampleGrad (then ddx and ddy).</summary>
        public int CoordinateCount => Cube ? 3 : 2;
    }

    /// <summary>
    /// A scalar expression node of a shader's dataflow, built by an <see cref="ExprPool"/>, which shares identical nodes and folds constants.
    /// Leaves are named inputs: interpolants ("in:TEXCOORD10.x"), constant buffer elements ("cb:WorldToViewMatrix[4]") and the like.
    /// </summary>
    public sealed class Expr
    {
        public ExprOp Op { get; }
        public float Value { get; }
        public string Leaf { get; }
        public IReadOnlyList<Expr> Args { get; }
        public SampleInfo SampleInfo { get; }
        /// <summary>For <see cref="ExprOp.Component"/>: which component of the sample.</summary>
        public int Index { get; }
        public int Id { get; }

        internal Expr(int id, ExprOp op, float value, string leaf, Expr[] args, SampleInfo sample, int index)
        {
            Id = id; Op = op; Value = value; Leaf = leaf; Args = args ?? []; SampleInfo = sample; Index = index;
        }

        public bool IsBool => Op is ExprOp.BoolConst or ExprOp.Lt or ExprOp.Ge or ExprOp.Eq or ExprOp.Ne or ExprOp.And or ExprOp.Or or ExprOp.Not;
        public bool IsConst => Op == ExprOp.Const;
        public bool IsZero => Op == ExprOp.Const && Value == 0;
        public bool IsOne => Op == ExprOp.Const && Value == 1;
        public override string ToString() => ExprPrinter.Print(this);
    }

    /// <summary>Builds <see cref="Expr"/> nodes: identical nodes are shared and constant subexpressions folded.</summary>
    public sealed class ExprPool
    {
        private readonly Dictionary<(ExprOp, int, string, string, string, int), Expr> nodes = new();
        private int nextId;

        public Expr Zero => Const(0);
        public Expr One => Const(1);

        private Expr Make(ExprOp op, float value = 0, string leaf = null, Expr[] args = null, SampleInfo sample = null, int index = 0)
        {
            string argKey = args == null ? "" : string.Join(",", args.Select(a => a.Id));
            int bits = BitConverter.SingleToInt32Bits(value);
            var key = (op, bits, leaf ?? "", argKey, sample == null ? "" : $"{sample.Texture}|{sample.Sampler}|{sample.Cube}|{sample.Method}", index);
            if (nodes.TryGetValue(key, out var existing)) return existing;
            var node = new Expr(nextId++, op, value, leaf, args, sample, index);
            nodes[key] = node;
            return node;
        }

        public Expr Const(float value) => Make(ExprOp.Const, value);
        public Expr Bool(bool value) => Make(ExprOp.BoolConst, value ? 1 : 0);
        public Expr Leaf(string name) => Make(ExprOp.Leaf, leaf: name);

        public Expr Neg(Expr a)
        {
            if (a.IsConst) return Const(-a.Value);
            if (a.Op == ExprOp.Neg) return a.Args[0];
            return Make(ExprOp.Neg, args: [a]);
        }

        public Expr Abs(Expr a) => a.IsConst ? Const(Math.Abs(a.Value)) : a.Op is ExprOp.Abs ? a : a.Op == ExprOp.Neg ? Abs(a.Args[0]) : Make(ExprOp.Abs, args: [a]);

        public Expr Add(Expr a, Expr b)
        {
            if (a.IsConst && b.IsConst) return Const(a.Value + b.Value);
            if (a.IsZero) return b;
            if (b.IsZero) return a;
            if (a.Id > b.Id) (a, b) = (b, a);
            return Make(ExprOp.Add, args: [a, b]);
        }

        public Expr Sub(Expr a, Expr b) => Add(a, Neg(b));

        public Expr Mul(Expr a, Expr b)
        {
            if (a.IsConst && b.IsConst) return Const(a.Value * b.Value);
            if (a.IsZero || b.IsZero) return Zero;
            if (a.IsOne) return b;
            if (b.IsOne) return a;
            if (a.IsConst && a.Value == -1) return Neg(b);
            if (b.IsConst && b.Value == -1) return Neg(a);
            if (a.Op == ExprOp.Neg && b.Op == ExprOp.Neg) return Mul(a.Args[0], b.Args[0]);
            if (a.Op == ExprOp.Neg) return Neg(Mul(a.Args[0], b));
            if (b.Op == ExprOp.Neg) return Neg(Mul(a, b.Args[0]));
            if (a.Id > b.Id) (a, b) = (b, a);
            return Make(ExprOp.Mul, args: [a, b]);
        }

        public Expr Mad(Expr a, Expr b, Expr c) => Add(Mul(a, b), c);

        public Expr Div(Expr a, Expr b)
        {
            if (a.IsConst && b.IsConst) return Const(a.Value / b.Value);
            if (a.IsZero) return Zero;
            if (b.IsOne) return a;
            return Make(ExprOp.Div, args: [a, b]);
        }

        public Expr Unary(ExprOp op, Expr a)
        {
            if (a.IsConst)
            {
                double x = a.Value;
                double? r = op switch
                {
                    ExprOp.Rsq => 1 / Math.Sqrt(x), ExprOp.Sqrt => Math.Sqrt(x), ExprOp.Rcp => 1 / x, ExprOp.Exp2 => Math.Pow(2, x),
                    ExprOp.Log2 => Math.Log2(x), ExprOp.Sat => Math.Clamp(x, 0, 1), ExprOp.Frc => x - Math.Floor(x), ExprOp.Floor => Math.Floor(x),
                    ExprOp.Ceil => Math.Ceiling(x), ExprOp.Trunc => Math.Truncate(x), ExprOp.Round => Math.Round(x, MidpointRounding.ToEven),
                    ExprOp.Sin => Math.Sin(x), ExprOp.Cos => Math.Cos(x), ExprOp.Ddx or ExprOp.Ddy => 0, _ => null
                };
                if (r != null) return Const((float)r.Value);
            }
            if (op == ExprOp.Sat && a.Op == ExprOp.Sat) return a;
            return Make(op, args: [a]);
        }

        public Expr Min(Expr a, Expr b) => a.IsConst && b.IsConst ? Const(Math.Min(a.Value, b.Value)) : a == b ? a : Make(ExprOp.Min, args: a.Id < b.Id ? [a, b] : [b, a]);
        public Expr Max(Expr a, Expr b) => a.IsConst && b.IsConst ? Const(Math.Max(a.Value, b.Value)) : a == b ? a : Make(ExprOp.Max, args: a.Id < b.Id ? [a, b] : [b, a]);

        public Expr Compare(ExprOp op, Expr a, Expr b)
        {
            if (a.IsConst && b.IsConst)
                return Bool(op switch { ExprOp.Lt => a.Value < b.Value, ExprOp.Ge => a.Value >= b.Value, ExprOp.Eq => a.Value == b.Value, _ => a.Value != b.Value });
            return Make(op, args: [a, b]);
        }

        public Expr And(Expr a, Expr b)
        {
            if (a.Op == ExprOp.BoolConst) return a.Value != 0 ? b : a;
            if (b.Op == ExprOp.BoolConst) return b.Value != 0 ? a : b;
            if (a == b) return a;
            return Make(ExprOp.And, args: a.Id < b.Id ? [a, b] : [b, a]);
        }

        public Expr Or(Expr a, Expr b)
        {
            if (a.Op == ExprOp.BoolConst) return a.Value != 0 ? a : b;
            if (b.Op == ExprOp.BoolConst) return b.Value != 0 ? b : a;
            if (a == b) return a;
            return Make(ExprOp.Or, args: a.Id < b.Id ? [a, b] : [b, a]);
        }

        public Expr Not(Expr a) => a.Op == ExprOp.BoolConst ? Bool(a.Value == 0) : a.Op == ExprOp.Not ? a.Args[0] : Make(ExprOp.Not, args: [a]);

        /// <summary>A boolean from a value: true when it isn't zero.</summary>
        public Expr Truth(Expr a) => a.IsBool ? a : Compare(ExprOp.Ne, a, Zero);

        public Expr Select(Expr condition, Expr ifTrue, Expr ifFalse)
        {
            condition = Truth(condition);
            if (condition.Op == ExprOp.BoolConst) return condition.Value != 0 ? ifTrue : ifFalse;
            if (ifTrue == ifFalse) return ifTrue;
            if (condition.Op == ExprOp.Not) return Select(condition.Args[0], ifFalse, ifTrue);
            return Make(ExprOp.Select, args: [condition, ifTrue, ifFalse]);
        }

        public Expr Sample(SampleInfo info, IReadOnlyList<Expr> args) => Make(ExprOp.Sample, args: args.ToArray(), sample: info);
        public Expr Component(Expr sample, int index) => Make(ExprOp.Component, args: [sample], index: index);

        /// <summary>Rebuilds a node with new arguments, folding again.</summary>
        public Expr Rebuild(Expr e, IReadOnlyList<Expr> a) => e.Op switch
        {
            ExprOp.Const or ExprOp.BoolConst or ExprOp.Leaf => e,
            ExprOp.Neg => Neg(a[0]),
            ExprOp.Abs => Abs(a[0]),
            ExprOp.Add => Add(a[0], a[1]),
            ExprOp.Mul => Mul(a[0], a[1]),
            ExprOp.Div => Div(a[0], a[1]),
            ExprOp.Min => Min(a[0], a[1]),
            ExprOp.Max => Max(a[0], a[1]),
            ExprOp.Lt or ExprOp.Ge or ExprOp.Eq or ExprOp.Ne => Compare(e.Op, a[0], a[1]),
            ExprOp.And => And(a[0], a[1]),
            ExprOp.Or => Or(a[0], a[1]),
            ExprOp.Not => Not(a[0]),
            ExprOp.Select => Select(a[0], a[1], a[2]),
            ExprOp.Sample => Sample(e.SampleInfo, a),
            ExprOp.Component => Component(a[0], e.Index),
            _ => Unary(e.Op, a[0])
        };
    }

    /// <summary>Thrown when an expression can't be analysed the way asked (a derivative through a nonlinear operation, an unsupported instruction).</summary>
    public sealed class ShaderRecoveryException(string message) : Exception(message);

    /// <summary>Symbolic and numeric operations on <see cref="Expr"/> graphs.</summary>
    public static class ExprAlgebra
    {
        /// <summary>Replaces leaves: <paramref name="replace"/> returns the replacement, or null to keep the leaf.</summary>
        public static Expr Substitute(ExprPool pool, Expr root, Func<Expr, Expr> replace, Dictionary<Expr, Expr> memo = null)
        {
            memo ??= new Dictionary<Expr, Expr>();
            return Walk(root);
            Expr Walk(Expr e)
            {
                if (memo.TryGetValue(e, out var done)) return done;
                Expr result;
                var direct = replace(e);
                if (direct != null) result = direct;
                else if (e.Args.Count == 0) result = e;
                else
                {
                    var args = e.Args.Select(Walk).ToArray();
                    result = args.SequenceEqual(e.Args) ? e : pool.Rebuild(e, args);
                }
                memo[e] = result;
                return result;
            }
        }

        public static bool DependsOn(Expr root, Func<Expr, bool> isTarget, Dictionary<Expr, bool> memo = null)
        {
            memo ??= new Dictionary<Expr, bool>();
            return Walk(root);
            bool Walk(Expr e)
            {
                if (memo.TryGetValue(e, out bool known)) return known;
                bool result = isTarget(e) || e.Args.Any(Walk);
                memo[e] = result;
                return result;
            }
        }

        /// <summary>
        /// The derivative of <paramref name="root"/> with respect to the nodes <paramref name="isTarget"/> picks (leaves, or any node
        /// treated as a symbol). Exact for expressions that use the target linearly (through +, ×, ÷ by independent values, select,
        /// min/max); throws for a target that flows into a nonlinear operation.
        /// </summary>
        public static Expr Derivative(ExprPool pool, Expr root, Func<Expr, bool> isTarget)
        {
            var depends = new Dictionary<Expr, bool>();
            var memo = new Dictionary<Expr, Expr>();
            return D(root);

            bool Dep(Expr e) => DependsOn(e, isTarget, depends);
            Expr D(Expr e)
            {
                if (memo.TryGetValue(e, out var done)) return done;
                Expr r;
                if (isTarget(e)) r = pool.One;
                else if (!Dep(e)) r = pool.Zero;
                else
                {
                    var a = e.Args;
                    r = e.Op switch
                    {
                        ExprOp.Neg => pool.Neg(D(a[0])),
                        ExprOp.Add => pool.Add(D(a[0]), D(a[1])),
                        ExprOp.Mul => pool.Add(pool.Mul(D(a[0]), a[1]), pool.Mul(a[0], D(a[1]))),
                        ExprOp.Div when !Dep(a[1]) => pool.Div(D(a[0]), a[1]),
                        ExprOp.Select when !Dep(a[0]) => pool.Select(a[0], D(a[1]), D(a[2])),
                        ExprOp.Min => pool.Select(pool.Compare(ExprOp.Lt, a[0], a[1]), D(a[0]), D(a[1])),
                        ExprOp.Max => pool.Select(pool.Compare(ExprOp.Lt, a[0], a[1]), D(a[1]), D(a[0])),
                        _ => throw new ShaderRecoveryException($"The value flows through a nonlinear {e.Op}, so it can't be separated.")
                    };
                }
                memo[e] = r;
                return r;
            }
        }

        /// <summary>
        /// Evaluates an expression. <paramref name="leaf"/> gives each leaf's value; samples evaluate <paramref name="sample"/>,
        /// a stand-in texture function of (texture, coordinates, component).
        /// </summary>
        public static double Evaluate(Expr root, Func<string, double> leaf, Func<SampleInfo, double[], int, double> sample, Dictionary<Expr, double> memo = null)
        {
            memo ??= new Dictionary<Expr, double>();
            return E(root);
            double E(Expr e)
            {
                if (memo.TryGetValue(e, out double v)) return v;
                var a = e.Args;
                double r = e.Op switch
                {
                    ExprOp.Const or ExprOp.BoolConst => e.Value,
                    ExprOp.Leaf => leaf(e.Leaf),
                    ExprOp.Neg => -E(a[0]),
                    ExprOp.Abs => Math.Abs(E(a[0])),
                    ExprOp.Add => E(a[0]) + E(a[1]),
                    ExprOp.Mul => E(a[0]) * E(a[1]),
                    ExprOp.Div => E(a[0]) / E(a[1]),
                    ExprOp.Rsq => 1 / Math.Sqrt(E(a[0])),
                    ExprOp.Sqrt => Math.Sqrt(E(a[0])),
                    ExprOp.Rcp => 1 / E(a[0]),
                    ExprOp.Exp2 => Math.Pow(2, E(a[0])),
                    ExprOp.Log2 => Math.Log2(E(a[0])),
                    ExprOp.Min => Math.Min(E(a[0]), E(a[1])),
                    ExprOp.Max => Math.Max(E(a[0]), E(a[1])),
                    ExprOp.Sat => Math.Clamp(E(a[0]), 0, 1),
                    ExprOp.Frc => E(a[0]) - Math.Floor(E(a[0])),
                    ExprOp.Floor => Math.Floor(E(a[0])),
                    ExprOp.Ceil => Math.Ceiling(E(a[0])),
                    ExprOp.Trunc => Math.Truncate(E(a[0])),
                    ExprOp.Round => Math.Round(E(a[0]), MidpointRounding.ToEven),
                    ExprOp.Sin => Math.Sin(E(a[0])),
                    ExprOp.Cos => Math.Cos(E(a[0])),
                    ExprOp.Ddx or ExprOp.Ddy => 0,
                    ExprOp.Select => E(a[0]) != 0 ? E(a[1]) : E(a[2]),
                    ExprOp.Lt => E(a[0]) < E(a[1]) ? 1 : 0,
                    ExprOp.Ge => E(a[0]) >= E(a[1]) ? 1 : 0,
                    ExprOp.Eq => E(a[0]) == E(a[1]) ? 1 : 0,
                    ExprOp.Ne => E(a[0]) != E(a[1]) ? 1 : 0,
                    ExprOp.And => E(a[0]) != 0 && E(a[1]) != 0 ? 1 : 0,
                    ExprOp.Or => E(a[0]) != 0 || E(a[1]) != 0 ? 1 : 0,
                    ExprOp.Not => E(a[0]) == 0 ? 1 : 0,
                    ExprOp.Sample => double.NaN,
                    ExprOp.Component => sample(a[0].SampleInfo, a[0].Args.Select(E).ToArray(), e.Index),
                    _ => throw new ShaderRecoveryException($"Can't evaluate {e.Op}.")
                };
                memo[e] = r;
                return r;
            }
        }

        public static IEnumerable<Expr> Nodes(params Expr[] roots)
        {
            var seen = new HashSet<Expr>();
            var stack = new Stack<Expr>(roots.Where(r => r != null));
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                if (!seen.Add(e)) continue;
                yield return e;
                foreach (var a in e.Args) stack.Push(a);
            }
        }
    }

    /// <summary>A readable one-line form, for diagnostics.</summary>
    public static class ExprPrinter
    {
        public static string Print(Expr e, int depth = 0)
        {
            if (depth > 12) return "…";
            string A(int i) => Print(e.Args[i], depth + 1);
            return e.Op switch
            {
                ExprOp.Const => e.Value.ToString("R", CultureInfo.InvariantCulture),
                ExprOp.BoolConst => e.Value != 0 ? "true" : "false",
                ExprOp.Leaf => e.Leaf,
                ExprOp.Neg => $"-({A(0)})",
                ExprOp.Add => $"({A(0)} + {A(1)})",
                ExprOp.Mul => $"({A(0)} * {A(1)})",
                ExprOp.Div => $"({A(0)} / {A(1)})",
                ExprOp.Select => $"({A(0)} ? {A(1)} : {A(2)})",
                ExprOp.Component => $"{Print(e.Args[0], depth + 1)}.{"xyzw"[e.Index]}",
                ExprOp.Sample => $"{e.SampleInfo.Texture}.{e.SampleInfo.Method}({string.Join(", ", e.Args.Select(a => Print(a, depth + 1)))})",
                _ => $"{e.Op}({string.Join(", ", e.Args.Select(a => Print(a, depth + 1)))})"
            };
        }
    }

    /// <summary>
    /// Writes expressions as HLSL statements: shared subexpressions become locals, leaves are written by a caller mapping, and
    /// samples become texture Sample calls.
    /// </summary>
    public sealed class HlslEmitter(Func<string, string> leaf, string localPrefix = "r")
    {
        private readonly StringBuilder statements = new();
        private readonly Dictionary<Expr, string> written = new();
        private Dictionary<Expr, int> uses = new();
        private int next;

        public string Statements => statements.ToString();

        /// <summary>Counts uses over every root about to be emitted, so shared nodes become locals.</summary>
        public void Prepare(IEnumerable<Expr> roots)
        {
            uses = new Dictionary<Expr, int>();
            var seen = new HashSet<Expr>();
            var stack = new Stack<Expr>(roots);
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                foreach (var a in e.Args)
                {
                    uses[a] = uses.GetValueOrDefault(a) + 1;
                    if (seen.Add(a)) stack.Push(a);
                }
            }
        }

        public string Emit(Expr e)
        {
            if (written.TryGetValue(e, out string name)) return name;
            string code = e.Op switch
            {
                ExprOp.Const => Literal(e.Value),
                ExprOp.BoolConst => e.Value != 0 ? "true" : "false",
                ExprOp.Leaf => leaf(e.Leaf),
                ExprOp.Neg => $"(-{Emit(e.Args[0])})",
                ExprOp.Abs => $"abs({Emit(e.Args[0])})",
                ExprOp.Add => $"({Emit(e.Args[0])} + {Emit(e.Args[1])})",
                ExprOp.Mul => $"({Emit(e.Args[0])} * {Emit(e.Args[1])})",
                ExprOp.Div => $"({Emit(e.Args[0])} / {Emit(e.Args[1])})",
                ExprOp.Rsq => $"rsqrt({Emit(e.Args[0])})",
                ExprOp.Sqrt => $"sqrt({Emit(e.Args[0])})",
                ExprOp.Rcp => $"rcp({Emit(e.Args[0])})",
                ExprOp.Exp2 => $"exp2({Emit(e.Args[0])})",
                ExprOp.Log2 => $"log2({Emit(e.Args[0])})",
                ExprOp.Min => $"min({Emit(e.Args[0])}, {Emit(e.Args[1])})",
                ExprOp.Max => $"max({Emit(e.Args[0])}, {Emit(e.Args[1])})",
                ExprOp.Sat => $"saturate({Emit(e.Args[0])})",
                ExprOp.Frc => $"frac({Emit(e.Args[0])})",
                ExprOp.Floor => $"floor({Emit(e.Args[0])})",
                ExprOp.Ceil => $"ceil({Emit(e.Args[0])})",
                ExprOp.Trunc => $"trunc({Emit(e.Args[0])})",
                ExprOp.Round => $"round({Emit(e.Args[0])})",
                ExprOp.Sin => $"sin({Emit(e.Args[0])})",
                ExprOp.Cos => $"cos({Emit(e.Args[0])})",
                ExprOp.Ddx => $"ddx({Emit(e.Args[0])})",
                ExprOp.Ddy => $"ddy({Emit(e.Args[0])})",
                ExprOp.Select => $"(({Emit(e.Args[0])}) ? {Emit(e.Args[1])} : {Emit(e.Args[2])})",
                ExprOp.Lt => $"({Emit(e.Args[0])} < {Emit(e.Args[1])})",
                ExprOp.Ge => $"({Emit(e.Args[0])} >= {Emit(e.Args[1])})",
                ExprOp.Eq => $"({Emit(e.Args[0])} == {Emit(e.Args[1])})",
                ExprOp.Ne => $"({Emit(e.Args[0])} != {Emit(e.Args[1])})",
                ExprOp.And => $"({Emit(e.Args[0])} && {Emit(e.Args[1])})",
                ExprOp.Or => $"({Emit(e.Args[0])} || {Emit(e.Args[1])})",
                ExprOp.Not => $"(!{Emit(e.Args[0])})",
                ExprOp.Sample => SampleCall(e),
                ExprOp.Component => $"{Emit(e.Args[0])}.{"xyzw"[e.Index]}",
                _ => throw new ShaderRecoveryException($"Can't write {e.Op} as HLSL.")
            };
            // Samples always get a local (their components are read separately); other shared nodes too
            bool local = e.Op == ExprOp.Sample || (uses.GetValueOrDefault(e) > 1 && e.Op is not (ExprOp.Const or ExprOp.Leaf or ExprOp.BoolConst or ExprOp.Component));
            if (!local) return code;
            string type = e.Op == ExprOp.Sample ? "float4" : e.IsBool ? "bool" : "float";
            name = $"{localPrefix}{next++}";
            statements.Append('\t').Append(type).Append(' ').Append(name).Append(" = ").Append(code).Append(";\r\n");
            written[e] = name;
            return name;
        }

        private string SampleCall(Expr e)
        {
            var s = e.SampleInfo;
            int n = s.CoordinateCount;
            string Vec(int start, int count) => count == 1 ? Emit(e.Args[start]) : $"float{count}({string.Join(", ", Enumerable.Range(start, count).Select(i => Emit(e.Args[i])))})";
            string coordinates = Vec(0, n);
            return s.Method switch
            {
                "SampleLevel" => $"{s.Texture}.SampleLevel({s.Sampler}, {coordinates}, {Emit(e.Args[n])})",
                "SampleBias" => $"{s.Texture}.SampleBias({s.Sampler}, {coordinates}, {Emit(e.Args[n])})",
                "SampleGrad" => $"{s.Texture}.SampleGrad({s.Sampler}, {coordinates}, {Vec(n, n)}, {Vec(2 * n, n)})",
                _ => $"{s.Texture}.Sample({s.Sampler}, {coordinates})"
            };
        }

        public static string Literal(float value)
        {
            if (float.IsPositiveInfinity(value)) return "asfloat(0x7f800000)";
            if (float.IsNegativeInfinity(value)) return "asfloat(0xff800000)";
            if (float.IsNaN(value)) return "asfloat(0x7fc00000)";
            string text = value.ToString("R", CultureInfo.InvariantCulture);
            if (!text.Contains('.') && !text.Contains('E')) text += ".0";
            return text.Contains('-') && !text.StartsWith('-') ? text : value < 0 ? $"({text})" : text;
        }
    }
}
