using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SharpDX.D3DCompiler;

namespace LegendaryExplorerCore.Shaders.Recovery
{
    /// <summary>
    /// What a shader computes, as expressions: each written output component ("o0.x"), and the condition under which it discards
    /// the pixel. Leaves are named by meaning, so two shaders compiled from different sources can be compared: interpolants by
    /// semantic ("in:TEXCOORD10.x"), constant buffer elements by variable and element ("cb:UniformPixelVector_0[2]"),
    /// resources by name.
    /// </summary>
    public sealed class ShaderDataflow
    {
        public ExprPool Pool { get; init; }
        public Dictionary<string, Expr> Outputs { get; init; } = new();
        /// <summary>True where the shader discards the pixel; a false constant when it never does.</summary>
        public Expr Discard { get; init; }
        /// <summary>Input semantic of each v register ("TEXCOORD10", "SV_IsFrontFace0").</summary>
        public IReadOnlyDictionary<int, string> InputSemantics { get; init; }
        /// <summary>Output register to semantic ("SV_Target0").</summary>
        public IReadOnlyDictionary<int, string> OutputSemantics { get; init; }
        public string Disassembly { get; init; }

        public Expr Output(string register) => Outputs.TryGetValue(register, out var e) ? e : null;
    }

    /// <summary>
    /// Symbolically executes Shader Model 5 bytecode (through its disassembly and reflection), turning every register component
    /// into an <see cref="Expr"/>. Straight-line code and structured if/else are supported (branches merge as selects); loops,
    /// integer arithmetic and dynamic indexing aren't, and throw <see cref="ShaderRecoveryException"/>.
    /// </summary>
    public static class DxbcSymbolicExecutor
    {
        private const string Components = "xyzw";

        public static ShaderDataflow Execute(byte[] bytecode, ExprPool pool = null)
        {
            pool ??= new ExprPool();
            using var shader = new ShaderBytecode(bytecode);
            string disassembly = shader.Disassemble();
            using var reflection = new ShaderReflection(bytecode);

            var inputs = new Dictionary<int, string>();
            // Several semantics can share a register (TEXCOORD10 in .xyz, TEXCOORD7 in .w): name each register component by
            // its own semantic and its component within that semantic
            var inputComponents = new Dictionary<(int Register, int Component), string>();
            for (int i = 0; i < reflection.Description.InputParameters; i++)
            {
                var p = reflection.GetInputParameterDescription(i);
                string semantic = p.SemanticName + p.SemanticIndex;
                inputs[p.Register] = inputs.TryGetValue(p.Register, out string existing) ? existing + "+" + semantic : semantic;
                int mask = (int)p.UsageMask, first = -1;
                for (int c = 0; c < 4; c++)
                {
                    if ((mask & (1 << c)) == 0) continue;
                    if (first < 0) first = c;
                    inputComponents[(p.Register, c)] = $"{semantic}.{"xyzw"[c - first]}";
                }
            }
            var outputs = new Dictionary<int, string>();
            for (int i = 0; i < reflection.Description.OutputParameters; i++)
            {
                var p = reflection.GetOutputParameterDescription(i);
                outputs[p.Register] = p.SemanticName + p.SemanticIndex;
            }
            var textures = new Dictionary<int, string>();
            var samplers = new Dictionary<int, string>();
            var buffers = new Dictionary<int, string>();
            for (int i = 0; i < reflection.Description.BoundResources; i++)
            {
                var r = reflection.GetResourceBindingDescription(i);
                switch (r.Type)
                {
                    case ShaderInputType.Texture: textures[r.BindPoint] = r.Name; break;
                    case ShaderInputType.Sampler: samplers[r.BindPoint] = r.Name; break;
                    case ShaderInputType.ConstantBuffer: buffers[r.BindPoint] = r.Name; break;
                }
            }
            // Constant buffer variables, to name each element a shader reads
            var variables = new Dictionary<string, List<(string Name, int Start, int Size)>>();
            for (int c = 0; c < reflection.Description.ConstantBuffers; c++)
            {
                var cb = reflection.GetConstantBuffer(c);
                var list = new List<(string, int, int)>();
                for (int v = 0; v < cb.Description.VariableCount; v++)
                {
                    var d = cb.GetVariable(v).Description;
                    list.Add((d.Name, d.StartOffset, d.Size));
                }
                variables[cb.Description.Name] = list;
            }

            var state = new ExecutionState(pool, inputs, inputComponents, textures, samplers, buffers, variables);
            state.Run(disassembly);
            return new ShaderDataflow
            {
                Pool = pool, Outputs = state.Outputs, Discard = state.Discard, InputSemantics = inputs, OutputSemantics = outputs, Disassembly = disassembly
            };
        }

        private sealed class ExecutionState(ExprPool pool, Dictionary<int, string> inputs, Dictionary<(int Register, int Component), string> inputComponents, Dictionary<int, string> textures,
            Dictionary<int, string> samplers, Dictionary<int, string> buffers, Dictionary<string, List<(string Name, int Start, int Size)>> variables)
        {
            private Dictionary<string, Expr> registers = new();
            public Dictionary<string, Expr> Outputs => registers.Where(r => r.Key.StartsWith('o')).ToDictionary(r => r.Key, r => r.Value);
            public Expr Discard = pool.Bool(false);
            private readonly Dictionary<int, bool> cubeResources = new();
            /// <summary>Open ifs: the condition and the registers before the if (and after its first branch, once in else).</summary>
            private readonly Stack<(Expr Condition, Dictionary<string, Expr> Before, Dictionary<string, Expr> ThenEnd)> branches = new();
            private Expr live = pool.Bool(true);
            private bool returned;

            public void Run(string disassembly)
            {
                foreach (string rawLine in disassembly.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("//") || line.StartsWith('{') || line.StartsWith('}')) continue;
                    if (line.StartsWith("dcl_resource_texturecube")) cubeResources[ResourceIndex(line)] = true;
                    if (line.StartsWith("dcl_resource_texture2d")) cubeResources[ResourceIndex(line)] = false;
                    if (line.StartsWith("dcl_") || line.StartsWith("ps_") || line.StartsWith("vs_")) continue;
                    if (returned) continue;
                    Step(line);
                }
            }

            private static int ResourceIndex(string line) => int.Parse(line[(line.LastIndexOf(" t", StringComparison.Ordinal) + 2)..].Split(',', ' ')[0]);

            private void Step(string line)
            {
                int space = line.IndexOf(' ');
                string opcode = space < 0 ? line : line[..space];
                // Strip parenthesised decorations: sample_indexable(texture2d)(float,float,float,float)
                string decorated = opcode;
                int paren = opcode.IndexOf('(');
                if (paren >= 0) opcode = opcode[..paren];
                var operands = space < 0 ? new List<string>() : SplitOperands(line[(space + 1)..]);
                if (paren >= 0 && space >= 0 && decorated.Count(c => c == '(') > decorated.Count(c => c == ')'))
                    throw new ShaderRecoveryException($"Unparsed instruction: {line}");
                bool saturate = opcode.EndsWith("_sat", StringComparison.Ordinal);
                if (saturate) opcode = opcode[..^4];

                switch (opcode)
                {
                    case "ret":
                        if (branches.Count > 0) throw new ShaderRecoveryException("Return inside a branch isn't supported.");
                        returned = true;
                        return;
                    case "nop": return;
                    case "if_nz":
                    case "if_z":
                    {
                        var c = pool.Truth(Read(operands[0], 0));
                        if (opcode == "if_z") c = pool.Not(c);
                        branches.Push((c, new Dictionary<string, Expr>(registers), null));
                        live = pool.And(live, c);
                        return;
                    }
                    case "else":
                    {
                        var (c, before, _) = branches.Pop();
                        branches.Push((c, before, registers));
                        registers = new Dictionary<string, Expr>(before);
                        live = pool.And(OuterLive(), pool.Not(c));
                        return;
                    }
                    case "endif":
                    {
                        var (c, before, thenEnd) = branches.Pop();
                        var thenRegs = thenEnd ?? registers;
                        var elseRegs = thenEnd == null ? before : registers;
                        var merged = new Dictionary<string, Expr>();
                        foreach (string key in thenRegs.Keys.Union(elseRegs.Keys))
                        {
                            thenRegs.TryGetValue(key, out var t);
                            elseRegs.TryGetValue(key, out var f);
                            t ??= f; f ??= t;
                            merged[key] = t.IsBool && f.IsBool ? pool.Or(pool.And(c, t), pool.And(pool.Not(c), f)) : pool.Select(c, t, f);
                        }
                        registers = merged;
                        live = OuterLive();
                        return;
                    }
                    case "discard_nz":
                    case "discard_z":
                    {
                        var c = pool.Truth(Read(operands[0], 0));
                        if (opcode == "discard_z") c = pool.Not(c);
                        Discard = pool.Or(Discard, pool.And(live, c));
                        return;
                    }
                    case "loop": case "endloop": case "break": case "breakc_nz": case "breakc_z": case "continue": case "switch":
                        throw new ShaderRecoveryException($"Shader loops/switches ({opcode}) aren't supported.");
                }

                if (opcode is "sincos")
                {
                    // sincos sinDest, cosDest, src
                    WriteEach(operands[0], c => pool.Unary(ExprOp.Sin, Read(operands[2], c)), saturate);
                    WriteEach(operands[1], c => pool.Unary(ExprOp.Cos, Read(operands[2], c)), saturate);
                    return;
                }
                if (opcode.StartsWith("sample", StringComparison.Ordinal))
                {
                    Sample(opcode, operands, saturate);
                    return;
                }
                if (opcode is "dp2" or "dp3" or "dp4")
                {
                    int n = opcode[2] - '0';
                    Expr sum = pool.Zero;
                    for (int k = 0; k < n; k++) sum = pool.Add(sum, pool.Mul(Read(operands[1], k), Read(operands[2], k)));
                    WriteEach(operands[0], _ => sum, saturate);
                    return;
                }

                Func<int, Expr> compute = opcode switch
                {
                    "mov" => c => Read(operands[1], c),
                    "add" => c => pool.Add(Read(operands[1], c), Read(operands[2], c)),
                    "mul" => c => pool.Mul(Read(operands[1], c), Read(operands[2], c)),
                    "mad" => c => pool.Mad(Read(operands[1], c), Read(operands[2], c), Read(operands[3], c)),
                    "div" => c => pool.Div(Read(operands[1], c), Read(operands[2], c)),
                    "min" => c => pool.Min(Read(operands[1], c), Read(operands[2], c)),
                    "max" => c => pool.Max(Read(operands[1], c), Read(operands[2], c)),
                    "rsq" => c => pool.Unary(ExprOp.Rsq, Read(operands[1], c)),
                    "sqrt" => c => pool.Unary(ExprOp.Sqrt, Read(operands[1], c)),
                    "rcp" => c => pool.Unary(ExprOp.Rcp, Read(operands[1], c)),
                    "exp" => c => pool.Unary(ExprOp.Exp2, Read(operands[1], c)),
                    "log" => c => pool.Unary(ExprOp.Log2, Read(operands[1], c)),
                    "frc" => c => pool.Unary(ExprOp.Frc, Read(operands[1], c)),
                    "round_ni" => c => pool.Unary(ExprOp.Floor, Read(operands[1], c)),
                    "round_pi" => c => pool.Unary(ExprOp.Ceil, Read(operands[1], c)),
                    "round_z" => c => pool.Unary(ExprOp.Trunc, Read(operands[1], c)),
                    "round_ne" => c => pool.Unary(ExprOp.Round, Read(operands[1], c)),
                    "deriv_rtx" or "deriv_rtx_coarse" or "deriv_rtx_fine" => c => pool.Unary(ExprOp.Ddx, Read(operands[1], c)),
                    "deriv_rty" or "deriv_rty_coarse" or "deriv_rty_fine" => c => pool.Unary(ExprOp.Ddy, Read(operands[1], c)),
                    "lt" => c => pool.Compare(ExprOp.Lt, Read(operands[1], c), Read(operands[2], c)),
                    "ge" => c => pool.Compare(ExprOp.Ge, Read(operands[1], c), Read(operands[2], c)),
                    "eq" => c => pool.Compare(ExprOp.Eq, Read(operands[1], c), Read(operands[2], c)),
                    "ne" => c => pool.Compare(ExprOp.Ne, Read(operands[1], c), Read(operands[2], c)),
                    "movc" => c => Movc(Read(operands[1], c), Read(operands[2], c), Read(operands[3], c)),
                    "and" => c => BitAnd(Read(operands[1], c), Read(operands[2], c), operands[2]),
                    "or" => c => BitOr(Read(operands[1], c), Read(operands[2], c)),
                    "not" => c => pool.Not(BoolOf(Read(operands[1], c))),
                    // Integer conversions, as the float values they hold (approximate for large integers)
                    "ftoi" or "ftou" => c => pool.Unary(ExprOp.Trunc, Read(operands[1], c)),
                    "itof" or "utof" => c => Read(operands[1], c),
                    _ => throw new ShaderRecoveryException($"Unsupported instruction {opcode}: {line}")
                };
                WriteEach(operands[0], compute, saturate);
            }

            private Expr OuterLive()
            {
                Expr l = pool.Bool(true);
                foreach (var (c, _, thenEnd) in branches.Reverse()) l = pool.And(l, thenEnd == null ? c : pool.Not(c));
                return l;
            }

            private Expr Movc(Expr condition, Expr a, Expr b)
            {
                if (a.IsBool && b.IsBool) return pool.Or(pool.And(pool.Truth(condition), a), pool.And(pool.Not(pool.Truth(condition)), b));
                return pool.Select(condition, FloatOf(a), FloatOf(b));
            }

            /// <summary>A comparison mask ANDed with a constant's bits: the constant where true, else zero.</summary>
            private Expr BitAnd(Expr a, Expr b, string bText)
            {
                if (a.IsBool && b.IsBool) return pool.And(a, b);
                // A comparison mask ANDed with a value's bits: the value where true, else zero
                if (a.IsBool) return pool.Select(a, b, pool.Zero);
                if (b.IsBool) return pool.Select(b, a, pool.Zero);
                // Clearing the sign bit: abs
                if (b.IsConst && BitConverter.SingleToInt32Bits(b.Value) == 0x7fffffff) return pool.Abs(a);
                if (a.IsConst && BitConverter.SingleToInt32Bits(a.Value) == 0x7fffffff) return pool.Abs(b);
                throw new ShaderRecoveryException("Bitwise AND of non-mask values isn't supported.");
            }

            private Expr BitOr(Expr a, Expr b)
            {
                if (a.IsBool && b.IsBool) return pool.Or(a, b);
                throw new ShaderRecoveryException("Bitwise OR of non-mask values isn't supported.");
            }

            private Expr BoolOf(Expr e) => e.IsBool ? e : pool.Truth(e);
            /// <summary>A mask read as a float: all bits set is NaN; such uses are only valid as conditions.</summary>
            private Expr FloatOf(Expr e) => e.IsBool ? pool.Select(e, pool.Const(BitConverter.Int32BitsToSingle(-1)), pool.Zero) : e;

            private void Sample(string opcode, List<string> operands, bool saturate)
            {
                // sample dest, coords, tN.swizzle, sN [, lod | bias | ddx, ddy]
                var (_, textureIndex, textureSwizzle) = ParseResource(operands[2], 't');
                var (_, samplerIndex, _) = ParseResource(operands[3], 's');
                bool cube = cubeResources.GetValueOrDefault(textureIndex);
                if (!textures.TryGetValue(textureIndex, out string texture) || !samplers.TryGetValue(samplerIndex, out string sampler))
                    throw new ShaderRecoveryException($"Unknown texture/sampler t{textureIndex}/s{samplerIndex}.");
                string method = opcode switch
                {
                    "sample" or "sample_indexable" => "Sample",
                    "sample_l" or "sample_l_indexable" => "SampleLevel",
                    "sample_b" or "sample_b_indexable" => "SampleBias",
                    "sample_d" or "sample_d_indexable" => "SampleGrad",
                    _ => throw new ShaderRecoveryException($"Unsupported sample instruction {opcode}.")
                };
                int n = cube ? 3 : 2;
                var args = new List<Expr>();
                for (int k = 0; k < n; k++) args.Add(Read(operands[1], k));
                if (method is "SampleLevel" or "SampleBias") args.Add(Read(operands[4], 0));
                if (method == "SampleGrad")
                {
                    for (int k = 0; k < n; k++) args.Add(Read(operands[4], k));
                    for (int k = 0; k < n; k++) args.Add(Read(operands[5], k));
                }
                var node = pool.Sample(new SampleInfo(texture, sampler, cube, method), args);
                WriteEach(operands[0], c => pool.Component(node, Components.IndexOf(textureSwizzle[c])), saturate);
            }

            private static (char Kind, int Index, string Swizzle) ParseResource(string text, char kind)
            {
                int dot = text.IndexOf('.');
                string name = dot < 0 ? text : text[..dot];
                string swizzle = dot < 0 ? "xyzw" : text[(dot + 1)..];
                if (swizzle.Length == 1) swizzle = new string(swizzle[0], 4);
                return (kind, int.Parse(name[1..]), swizzle);
            }

            private void WriteEach(string destination, Func<int, Expr> compute, bool saturate)
            {
                if (destination == "null") return;
                int dot = destination.IndexOf('.');
                string register = dot < 0 ? destination : destination[..dot];
                string mask = dot < 0 ? "xyzw" : destination[(dot + 1)..];
                var values = new List<(string Key, Expr Value)>();
                foreach (char m in mask)
                {
                    int c = Components.IndexOf(m);
                    var value = compute(c);
                    if (saturate) value = pool.Unary(ExprOp.Sat, FloatOf(value));
                    values.Add(($"{register}.{m}", value));
                }
                // All components are computed from the sources before any is written (dest may alias a source)
                foreach (var (key, value) in values) registers[key] = value;
            }

            /// <summary>Reads component <paramref name="c"/> of a source operand: its swizzle picks the source component.</summary>
            private Expr Read(string operand, int c)
            {
                string text = operand.Trim();
                bool negate = false, absolute = false;
                if (text.StartsWith('-')) { negate = true; text = text[1..]; }
                if (text.StartsWith('|') && text.EndsWith('|')) { absolute = true; text = text[1..^1]; }
                Expr value = ReadRaw(text, c);
                if (absolute) value = pool.Abs(FloatOf(value));
                if (negate) value = pool.Neg(FloatOf(value));
                return value;
            }

            private Expr ReadRaw(string text, int c)
            {
                if (text.StartsWith("l(", StringComparison.Ordinal))
                {
                    var parts = text[2..^1].Split(',').Select(p => p.Trim()).ToArray();
                    return pool.Const(ParseLiteral(parts[parts.Length == 1 ? 0 : c]));
                }
                // Split off a trailing swizzle (after the last '.', outside brackets)
                int dot = text.LastIndexOf('.');
                int bracket = text.LastIndexOf(']');
                string baseName = dot > bracket && dot >= 0 ? text[..dot] : text;
                string swizzle = dot > bracket && dot >= 0 ? text[(dot + 1)..] : "xyzw";
                if (swizzle.Length == 1) swizzle = new string(swizzle[0], 4);
                char component = swizzle[Math.Min(c, swizzle.Length - 1)];
                int ci = Components.IndexOf(component);

                if (baseName.StartsWith("cb", StringComparison.Ordinal))
                {
                    int open = baseName.IndexOf('[');
                    int slot = int.Parse(baseName[2..open]);
                    string index = baseName[(open + 1)..^1];
                    if (!int.TryParse(index, out int reg)) throw new ShaderRecoveryException($"Dynamic constant buffer indexing ({text}) isn't supported.");
                    return pool.Leaf(ConstantLeaf(slot, reg, ci));
                }
                if (baseName.StartsWith('v') && int.TryParse(baseName[1..], out int v))
                    return pool.Leaf(inputComponents.TryGetValue((v, ci), out string named) ? $"in:{named}"
                        : $"in:{(inputs.TryGetValue(v, out string semantic) ? semantic : $"v{v}")}.{Components[ci]}");
                if (baseName.StartsWith('r') || baseName.StartsWith('o'))
                {
                    string key = $"{baseName}.{Components[ci]}";
                    if (registers.TryGetValue(key, out var value)) return value;
                    throw new ShaderRecoveryException($"Read of unwritten register {key}.");
                }
                throw new ShaderRecoveryException($"Unsupported operand {text}.");
            }

            private string ConstantLeaf(int slot, int register, int component)
            {
                int offset = register * 16 + component * 4;
                if (buffers.TryGetValue(slot, out string bufferName) && variables.TryGetValue(bufferName, out var list))
                    foreach (var (name, start, size) in list)
                        if (offset >= start && offset < start + size)
                            return $"cb:{name}[{(offset - start) / 4}]";
                return $"cb{slot}@{register}.{Components[component]}";
            }

            private static float ParseLiteral(string text)
            {
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return BitConverter.Int32BitsToSingle(unchecked((int)uint.Parse(text[2..], NumberStyles.HexNumber)));
                return float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            private static List<string> SplitOperands(string text)
            {
                var list = new List<string>();
                int depth = 0, start = 0;
                for (int i = 0; i < text.Length; i++)
                {
                    char ch = text[i];
                    if (ch is '(' or '[') depth++;
                    else if (ch is ')' or ']') depth--;
                    else if (ch == ',' && depth == 0)
                    {
                        list.Add(text[start..i].Trim());
                        start = i + 1;
                    }
                }
                if (start < text.Length) list.Add(text[start..].Trim());
                return list;
            }
        }
    }
}
