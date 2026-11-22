using System;
using System.Collections.Generic;
using System.Linq;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorerCore.Shaders.Recovery
{
    /// <summary>
    /// A material's code recovered from the shaders a game compiled for it: HLSL for each material property, written against
    /// FMaterialPixelParameters and the uniform/texture names of the material template, plus the uniform expressions and textures
    /// those names refer to (taken unchanged from the source shader map and resource).
    /// </summary>
    public sealed class RecoveredMaterialCode
    {
        /// <summary>Property name (as the compiler's MaterialInputs: "Normal", "EmissiveColor", "DiffuseColor", ...) to statements ending in "\treturn expr".</summary>
        public Dictionary<string, string> PropertyCode { get; } = new(StringComparer.Ordinal);
        public MaterialUniformExpression[] UniformPixelVectors { get; init; } = [];
        public MaterialUniformExpression[] UniformPixelScalars { get; init; } = [];
        public MaterialUniformExpressionTexture[] Uniform2DTextures { get; init; } = [];
        public MaterialUniformExpressionTexture[] UniformCubeTextures { get; init; } = [];
        /// <summary>The resource's texture list the texture uniforms index into (UIndexes in the material's package).</summary>
        public int[] UniformExpressionTextures { get; init; } = [];
        public uint NumUserTexCoords { get; init; }
        /// <summary>Transform usage bits for the template: 0x1 world (TangentToWorld), 0x8 world position.</summary>
        public uint UsingTransforms { get; set; }
        /// <summary>The source compile's transform usage (its cooked resource): what its interpolants mean.</summary>
        public uint SourceUsingTransforms { get; init; }
        public bool UsesVertexColor { get; set; }
        public bool UsesTime { get; init; }
        public int MaxTextureDependencyLength { get; init; }
        /// <summary>The source shader map's static parameters (the permutation the code was recovered from).</summary>
        public StaticParameterSet StaticParameters { get; init; }
        public List<string> Notes { get; } = [];
    }

    /// <summary>
    /// Recovers a material's per-property HLSL from the base pass pixel shaders its game shipped for it, when the cooked graph is
    /// stripped. The base pass is linear in the engine's lighting inputs, which separates the properties exactly:
    /// Color = Emissive + LightTransfer(Diffuse, Specular, SpecularPower, Normal) × LightColor + Diffuse × AmbientColor, and its
    /// second target holds the view-space normal. The engine inputs are treated as symbols (substituted or differentiated), and
    /// what's left is the material's own code, in terms of interpolants, uniforms and texture samples. Interpolants are then
    /// written as the FMaterialPixelParameters fields the LE material template derives from them (identical in LE1, LE2 and LE3
    /// for the local vertex factory).
    /// </summary>
    public static class MaterialCodeRecovery
    {
        public const string VertexFactory = "FLocalVertexFactory";
        public const string UnlitShader = "TBasePassPixelShaderFNoLightMapPolicyNoSkyLight";
        public const string DirectionalLightShader = "TBasePassPixelShaderFDirectionalLightLightMapPolicyNoSkyLight";

        /// <summary>Engine leaves the base pass reads that aren't part of the material, with the neutral values they get while recovering.</summary>
        private static readonly Dictionary<string, float> EngineNeutral = new(StringComparer.Ordinal)
        {
            ["UpperSkyColor"] = 0, ["LowerSkyColor"] = 0, ["CharacterMask"] = 0, ["MotionBlurMask"] = 0,
            ["bEnableDistanceShadowFading"] = 0, ["SpotDirection"] = 0, ["SpotAngles"] = 0, ["DistanceFieldParameters"] = 0,
            // The dynamic light is an unshadowed directional light
            ["bReceiveDynamicShadows"] = 0, ["bDynamicSpotLight"] = 0, ["bDynamicDirectionalLight"] = 1,
        };

        /// <param name="owner">The Material, or MaterialInstance with a static permutation, whose shaders to recover from.</param>
        /// <param name="getShaderBytecode">The bytecode of one of its shaders (vertex factory, shader type), or null if its map has none.</param>
        /// <param name="sourceMap">Its shader map (for the uniform expressions).</param>
        /// <param name="cookedResource">Its cooked resource (for the texture list and texture coordinate count).</param>
        public static RecoveredMaterialCode Recover(ExportEntry owner, MaterialShaderMap sourceMap, MaterialResource cookedResource,
            Func<string, string, byte[]> getShaderBytecode, ExportEntry baseMaterial = null)
        {
            ArgumentNullException.ThrowIfNull(sourceMap);
            ArgumentNullException.ThrowIfNull(cookedResource);
            baseMaterial ??= owner;
            // Blend mode, lighting model and clip value are the base Material's (an instance has none of its own)
            var properties = baseMaterial.GetProperties();
            string blend = properties.GetProp<EnumProperty>("BlendMode")?.Value.Name;
            string lighting = properties.GetProp<EnumProperty>("LightingModel")?.Value.Name;

            byte[] unlitBytes = getShaderBytecode(VertexFactory, UnlitShader)
                ?? getShaderBytecode(VertexFactory, "TBasePassPixelShaderFNoLightMapPolicySkyLight")
                ?? throw new ShaderRecoveryException($"Its shader map has no {UnlitShader} for {VertexFactory} to recover from.");

            var result = new RecoveredMaterialCode
            {
                UniformPixelVectors = sourceMap.UniformPixelVectorExpressions ?? [],
                UniformPixelScalars = sourceMap.UniformPixelScalarExpressions ?? [],
                Uniform2DTextures = sourceMap.Uniform2DTextureExpressions ?? [],
                UniformCubeTextures = sourceMap.UniformCubeTextureExpressions ?? [],
                UniformExpressionTextures = cookedResource.UniformExpressionTextures ?? [],
                NumUserTexCoords = cookedResource.NumUserTexCoords,
                MaxTextureDependencyLength = cookedResource.MaxTextureDependencyLength,
                StaticParameters = sourceMap.StaticParameters,
                UsesTime = (sourceMap.UniformPixelVectorExpressions ?? []).Concat(sourceMap.UniformPixelScalarExpressions ?? [])
                    .Any(UsesTimeExpression),
                // The source compile's transform usage; the tangent basis adds 0x1 if recovered code reads it
                UsingTransforms = cookedResource.UsingTransforms,
                SourceUsingTransforms = cookedResource.UsingTransforms,
            };

            var pool = new ExprPool();
            var unlit = DxbcSymbolicExecutor.Execute(unlitBytes, pool);
            var mapper = new LeafMapper(pool, unlit, result);

            Expr Neutral(Expr e) => e.Op == ExprOp.Leaf && e.Leaf.StartsWith("cb:", StringComparison.Ordinal) &&
                                    EngineNeutral.TryGetValue(VariableOf(e.Leaf), out float neutral) ? pool.Const(neutral)
                : e.Op == ExprOp.Leaf && e.Leaf == "in:SV_IsFrontFace0.x" ? pool.Const(BitConverter.Int32BitsToSingle(-1))
                // The static shadow mask in the light vector's w: unshadowed
                : e.Op == ExprOp.Leaf && e.Leaf is "in:TEXCOORD3.w" or "in:TEXCOORD4.w" ? pool.One : null;
            // The unlit policy's TEXCOORD4 is the vertex fog (translucency): none
            Expr UnlitNeutral(Expr e) => e.Op == ExprOp.Leaf && e.Leaf is "in:TEXCOORD4.x" or "in:TEXCOORD4.y" or "in:TEXCOORD4.z" ? pool.Zero : Neutral(e);
            bool IsAmbient(Expr e, int c) => e.Op == ExprOp.Leaf && e.Leaf == $"cb:AmbientColorAndSkyFactor[{c}]";

            // Emissive and diffuse: Color = Emissive + Diffuse × AmbientColor (the unlit policy has no other lighting)
            var emissive = new Expr[3];
            var diffuse = new Expr[3];
            for (int c = 0; c < 3; c++)
            {
                var color = unlit.Output($"o0.{"xyz"[c]}") ?? throw new ShaderRecoveryException("The base pass writes no color.");
                color = ExprAlgebra.Substitute(pool, color, UnlitNeutral);
                emissive[c] = ExprAlgebra.Substitute(pool, color, e => e.Op == ExprOp.Leaf && e.Leaf.StartsWith("cb:AmbientColorAndSkyFactor", StringComparison.Ordinal) ? pool.Zero : null);
                diffuse[c] = ExprAlgebra.Derivative(pool, color, e => IsAmbient(e, c));
                for (int other = 0; other < 3; other++)
                    if (other != c && !ExprAlgebra.Derivative(pool, color, e => IsAmbient(e, other)).IsZero)
                        throw new ShaderRecoveryException("The base pass mixes ambient light channels; it isn't a plain Diffuse × AmbientColor.");
            }
            result.PropertyCode["EmissiveColor"] = mapper.Write(emissive);
            if (lighting != "MLM_Unlit" && diffuse.Any(d => !d.IsZero)) result.PropertyCode["DiffuseColor"] = mapper.Write(diffuse);

            // Opacity: the color target's alpha, for translucent blending
            if (blend is "BLEND_Translucent" or "BLEND_AlphaComposite" && unlit.Output("o0.w") is { } alpha)
                result.PropertyCode["Opacity"] = mapper.Write([ExprAlgebra.Substitute(pool, alpha, UnlitNeutral)]);

            // Opacity mask: the pixel is discarded where GetMaterialMask (mask input - clip value) is below zero
            var kill = unlit.Discard.Op == ExprOp.BoolConst ? unlit.Discard : ExprAlgebra.Substitute(pool, unlit.Discard, UnlitNeutral);
            // A decal's shaders also clip to the decal's box (texture coordinates outside 0..1, the far plane); LE3's base pass adds that itself
            if (baseMaterial.ClassName == "DecalMaterial") kill = WithoutDecalClip(pool, kill);
            if (kill.Op != ExprOp.BoolConst)
            {
                if (kill.Op != ExprOp.Lt) throw new ShaderRecoveryException("The pixel discard isn't a plain opacity mask test.");
                float clip = properties.GetProp<FloatProperty>("OpacityMaskClipValue")?.Value ?? 0.3333f;
                var mask = pool.Add(pool.Sub(kill.Args[0], kill.Args[1]), pool.Const(clip));
                result.PropertyCode["OpacityMask"] = mapper.Write([mask]);
            }

            // Normal: SV_Target2.xy = normalize(WorldToView × TangentToWorld × Normal).xy × 0.5 + 0.5. The view vector's x is linear in
            // the matrix's first row; its coefficients are the world-space normal. Unlit materials write it too (for the post effects).
            if (unlit.Output("o2.x") is { } viewNormalX)
            {
                var x = PreNormalized(ExprAlgebra.Substitute(pool, viewNormalX, UnlitNeutral));
                if (x != null)
                {
                    var world = new Expr[3];
                    for (int c = 0; c < 3; c++)
                    {
                        string element = $"cb:WorldToViewMatrix[{c * 4}]";
                        world[c] = ExprAlgebra.Derivative(pool, x, e => e.Op == ExprOp.Leaf && e.Leaf == element);
                    }
                    // world = TangentToWorld(frame) × Normal. At the identity frame (tangent x, normal z, handedness +1) the transform
                    // vanishes and what's left is the tangent-space normal itself.
                    var identityFrame = new Dictionary<string, float>
                    {
                        ["in:TEXCOORD10.x"] = 1, ["in:TEXCOORD10.y"] = 0, ["in:TEXCOORD10.z"] = 0,
                        ["in:TEXCOORD11.x"] = 0, ["in:TEXCOORD11.y"] = 0, ["in:TEXCOORD11.z"] = 1, ["in:TEXCOORD11.w"] = 1,
                    };
                    var tangent = world.Select(w => ExprAlgebra.Substitute(pool, w,
                        e => e.Op == ExprOp.Leaf && identityFrame.TryGetValue(e.Leaf, out float v) ? pool.Const(v) : null)).ToArray();
                    // Unless the material reads the tangent frame itself (a triplanar blend on the vertex normal, say): the identity frame
                    // changes that too. Then invert the frame instead: world_c = row_c · n over the frame rows (T, B, N), so
                    // n = (world_0 (B × N) + world_1 (N × T) + world_2 (T × B)) / (T · (B × N)), exact for any (interpolated) frame.
                    var rows = FrameRows(pool);
                    var adjugate = new[] { Cross(pool, rows[1], rows[2]), Cross(pool, rows[2], rows[0]), Cross(pool, rows[0], rows[1]) };
                    var determinant = Dot(pool, rows[0], adjugate[0]);
                    var inverted = Enumerable.Range(0, 3).Select(i => pool.Div(pool.Add(pool.Add(pool.Mul(world[0], adjugate[0][i]),
                        pool.Mul(world[1], adjugate[1][i])), pool.Mul(world[2], adjugate[2][i])), determinant)).ToArray();
                    if (!ShaderEquivalence.SameValues(inverted, tangent)) tangent = inverted;
                    // Left at its (0,0,1) default when that's what it is
                    if (!(tangent[0].IsZero && tangent[1].IsZero && tangent[2].IsConst && tangent[2].Value > 0))
                        result.PropertyCode["Normal"] = mapper.Write(tangent);
                }
                else result.Notes.Add("The normal output isn't in the expected normalize(...) form; the normal was left at (0,0,1).");
            }

            // Specular and specular power, from the dynamic directional light's base pass:
            // Transfer = Diffuse × DiffuseLighting + exp2(log2(max(|saturate(R·L)|, ε)) × SpecularPower) × Specular
            if (lighting is null or "MLM_Phong" && getShaderBytecode(VertexFactory, DirectionalLightShader) is { } litBytes)
            {
                var lit = DxbcSymbolicExecutor.Execute(litBytes, pool);
                var litMapper = new LeafMapper(pool, lit, result);
                var specular = new Expr[3];
                var transmission = new Expr[3];
                Expr power = null;
                Expr[] normal = null;
                for (int c = 0; c < 3; c++)
                {
                    var color = ExprAlgebra.Substitute(pool, lit.Output($"o0.{"xyz"[c]}"), Neutral);
                    var transfer = ExprAlgebra.Derivative(pool, color, e => e.Op == ExprOp.Leaf && e.Leaf == $"cb:LightColorAndFalloffExponent[{c}]");
                    var (pow, exponent) = SpecularPowerTerm(pool, transfer);
                    if (pow != null)
                    {
                        power ??= exponent;
                        specular[c] = ExprAlgebra.Derivative(pool, transfer, e => e == pow);
                    }
                    else specular[c] = pool.Zero;

                    // The diffuse lighting term: the topmost max/saturate of N·L (the light vector, without the camera vector)
                    var diffusePart = pow == null ? transfer : ExprAlgebra.Substitute(pool, transfer, e => e == pow ? pool.Zero : null);
                    var lightLeaves = ExprAlgebra.Nodes(diffusePart).Where(e => e.Op == ExprOp.Leaf && IsLightVector(e.Leaf)).ToHashSet();
                    if (lightLeaves.Count == 0) { transmission[c] = pool.Zero; continue; }
                    var dependsOnLight = new Dictionary<Expr, bool>();
                    bool OnLight(Expr e) => ExprAlgebra.DependsOn(e, n => lightLeaves.Contains(n), dependsOnLight);
                    var diffuseLighting = TopmostLightTerm(diffusePart, OnLight);
                    if (diffuseLighting == null) { transmission[c] = pool.Zero; continue; }

                    // Transmission: Transfer = Diffuse × (DiffuseLighting × (1 - TM) + TM²), so TM = 1 - ∂Transfer/∂DiffuseLighting / Diffuse
                    var slope = ExprAlgebra.Derivative(pool, diffusePart, e => e == diffuseLighting);
                    transmission[c] = slope == diffuse[c] || diffuse[c].IsZero ? pool.Zero
                        : pool.Select(pool.Compare(ExprOp.Ne, diffuse[c], pool.Zero), pool.Sub(pool.One, pool.Div(slope, diffuse[c])), pool.Zero);

                    // The normal, when the base pass didn't give it (translucency writes no normal target): N·normalize(L) with L taken
                    // as unit length is linear in L, and its coefficients are N
                    if (c == 0 && !result.PropertyCode.ContainsKey("Normal"))
                    {
                        var dot = diffuseLighting;
                        while (dot.Op is ExprOp.Max or ExprOp.Sat) dot = dot.Args.First(OnLight);
                        var unitLight = ExprAlgebra.Substitute(pool, dot, e => e.Op == ExprOp.Rsq && ExprAlgebra.Nodes(e).Where(n => n.Op == ExprOp.Leaf)
                            .All(n => lightLeaves.Contains(n)) ? pool.One : null);
                        try
                        {
                            normal = new[] { "x", "y", "z" }.Select(axis => ExprAlgebra.Derivative(pool, unitLight,
                                e => e.Op == ExprOp.Leaf && lightLeaves.Contains(e) && e.Leaf.EndsWith("." + axis))).ToArray();
                        }
                        catch (ShaderRecoveryException) { normal = null; }
                    }
                }
                if (power != null)
                {
                    result.PropertyCode["SpecularColor"] = litMapper.Write(specular);
                    result.PropertyCode["SpecularPower"] = litMapper.Write([power]);
                }
                if (transmission.Any(t => !t.IsZero)) result.PropertyCode["TwoSidedLightingMask"] = litMapper.Write(transmission);
                if (normal != null && !(normal[0].IsZero && normal[1].IsZero && normal[2].IsConst && normal[2].Value > 0))
                    result.PropertyCode["Normal"] = litMapper.Write(normal);
            }
            // Distortion, from the distortion accumulation pass: OutColor = (max(D, 0).xy, |min(D, 0)|.xy) with
            // D = clamp(Distortion × 4, -255, 255) / 255, unless the scene in front occludes it (an engine depth test, dropped here)
            if (getShaderBytecode(VertexFactory, "TDistortionMeshPixelShader<FDistortMeshAccumulatePolicy>") is { } distortBytes)
            {
                var distort = DxbcSymbolicExecutor.Execute(distortBytes, pool);
                var memo = new Dictionary<Expr, Expr>();
                Expr Unoccluded(Expr e)
                {
                    if (e.Op != ExprOp.Select || !ExprAlgebra.DependsOn(e.Args[0], n => n.Op == ExprOp.Sample && !IsMaterialTexture(n.SampleInfo.Texture)))
                        return Neutral(e);
                    var kept = e.Args[1].IsZero ? e.Args[2] : e.Args[2].IsZero ? e.Args[1] : null;
                    return kept == null ? null : ExprAlgebra.Substitute(pool, kept, Unoccluded, memo);
                }
                var o = "xyzw".Select(c => distort.Output($"o0.{c}") is { } v ? ExprAlgebra.Substitute(pool, v, Unoccluded, memo) : null).ToArray();
                if (o.All(v => v != null))
                {
                    const float scale = 255f / 4f;
                    var distortion = new[] { pool.Mul(pool.Sub(o[0], o[2]), pool.Const(scale)), pool.Mul(pool.Sub(o[1], o[3]), pool.Const(scale)) };
                    if (!distortion.All(d => d.IsZero))
                    {
                        try { result.PropertyCode["Distortion"] = new LeafMapper(pool, distort, result).Write(distortion); }
                        catch (ShaderRecoveryException e) { result.Notes.Add("Distortion not recovered: " + e.Message); }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// The specular term ClampedPow(saturate(R·L), SpecularPower) in a light transfer, and its exponent: exp2(log2(base) × P), or for a
        /// constant integer power the repeated multiplication of base the compiler expands it to. Base is the clamped R·L, which reads both
        /// the light vector and the camera vector. Null when there's no specular term.
        /// </summary>
        private static (Expr Term, Expr Exponent) SpecularPowerTerm(ExprPool pool, Expr transfer)
        {
            bool Reads(Expr e, Func<string, bool> leaf) => ExprAlgebra.Nodes(e).Any(n => n.Op == ExprOp.Leaf && leaf(n.Leaf));
            // exp2(log2(R·L) × P); other powers in the material (a triplanar blend's |normal|^k, say) don't read the light vector
            var exp = ExprAlgebra.Nodes(transfer).FirstOrDefault(e => e.Op == ExprOp.Exp2 && e.Args[0].Op == ExprOp.Mul &&
                                                                      e.Args[0].Args.Any(a => a.Op == ExprOp.Log2 && Reads(a, IsLightVector)));
            if (exp != null) return (exp, exp.Args[0].Args.First(a => a.Op != ExprOp.Log2));

            bool IsCamera(string leaf) => leaf is "in:TEXCOORD6.x" or "in:TEXCOORD6.y" or "in:TEXCOORD6.z";
            // Whether e reads the camera vector itself, not only through texture coordinates (a parallax-offset normal map makes N·L
            // read the camera vector too, but only inside its samples; R = 2(N·V)N - V reads it directly)
            var directCamera = new Dictionary<Expr, bool>();
            bool ReadsCameraDirectly(Expr e)
            {
                if (directCamera.TryGetValue(e, out bool known)) return known;
                bool reads = e.Op == ExprOp.Leaf ? IsCamera(e.Leaf) : e.Op != ExprOp.Sample && e.Args.Any(ReadsCameraDirectly);
                directCamera[e] = reads;
                return reads;
            }
            // The clamped R·L: a max reading both the light and the camera vector, not inside another such max
            var bases = ExprAlgebra.Nodes(transfer).Where(e => e.Op == ExprOp.Max && Reads(e, IsLightVector) && ReadsCameraDirectly(e)).ToList();
            var basis = bases.FirstOrDefault(b => !bases.Any(o => o != b && ExprAlgebra.Nodes(o).Contains(b) && o.Op == ExprOp.Max && o.Args.Contains(b)));
            if (basis == null) return (null, null);
            var powers = new Dictionary<Expr, int?>();
            int? PowerOf(Expr e)
            {
                if (powers.TryGetValue(e, out var known)) return known;
                int? p = e == basis ? 1 : e.Op == ExprOp.Mul && PowerOf(e.Args[0]) is { } a && PowerOf(e.Args[1]) is { } b ? a + b : null;
                powers[e] = p;
                return p;
            }
            var term = ExprAlgebra.Nodes(transfer).Where(e => PowerOf(e) != null).OrderByDescending(e => PowerOf(e)).FirstOrDefault();
            return term == null ? (null, null) : (term, pool.Const(PowerOf(term).Value));
        }

        /// <summary>
        /// The pixel shader's tangent frame rows as the base pass builds them: T = normalize(TEXCOORD10), N = normalize(TEXCOORD11.xyz),
        /// B = cross(N, T) × TEXCOORD11.w.
        /// </summary>
        private static Expr[][] FrameRows(ExprPool pool)
        {
            Expr[] Normalized(string semantic)
            {
                var v = "xyz".Select(c => pool.Leaf($"in:{semantic}.{c}")).ToArray();
                var scale = pool.Unary(ExprOp.Rsq, pool.Add(pool.Add(pool.Mul(v[0], v[0]), pool.Mul(v[1], v[1])), pool.Mul(v[2], v[2])));
                return v.Select(x => pool.Mul(x, scale)).ToArray();
            }
            var t = Normalized("TEXCOORD10");
            var n = Normalized("TEXCOORD11");
            var w = pool.Leaf("in:TEXCOORD11.w");
            var b = Cross(pool, n, t).Select(x => pool.Mul(x, w)).ToArray();
            return [t, b, n];
        }

        private static Expr[] Cross(ExprPool pool, Expr[] a, Expr[] b) => Enumerable.Range(0, 3).Select(i =>
        {
            int j = (i + 1) % 3, k = (i + 2) % 3;
            return pool.Sub(pool.Mul(a[j], b[k]), pool.Mul(a[k], b[j]));
        }).ToArray();

        private static Expr Dot(ExprPool pool, Expr[] a, Expr[] b) => pool.Add(pool.Add(pool.Mul(a[0], b[0]), pool.Mul(a[1], b[1])), pool.Mul(a[2], b[2]));

        /// <summary>
        /// The discard condition without the engine's decal clipping: uv × (1 - uv) &lt; 0 on the decal's texture coordinates, and the
        /// DecalFarPlaneDistance test.
        /// </summary>
        private static Expr WithoutDecalClip(ExprPool pool, Expr kill)
        {
            if (kill.Op == ExprOp.Or) return pool.Or(WithoutDecalClip(pool, kill.Args[0]), WithoutDecalClip(pool, kill.Args[1]));
            if (kill.Op is not (ExprOp.Lt or ExprOp.Ge)) return kill;
            var leaves = ExprAlgebra.Nodes(kill).Where(n => n.Op == ExprOp.Leaf).Select(n => n.Leaf).ToList();
            bool decal = leaves.Contains("cb:DecalFarPlaneDistance") ||
                         leaves.Count > 0 && leaves.All(l => l is "in:TEXCOORD0.x" or "in:TEXCOORD0.y") && kill.Args.Any(a => a.IsZero);
            return decal ? pool.Bool(false) : kill;
        }

        private static bool IsMaterialTexture(string name) => name.StartsWith("Texture2D_", StringComparison.Ordinal) || name.StartsWith("TextureCube_", StringComparison.Ordinal);

        /// <summary>The base pass's tangent-space light vector: TEXCOORD3 when the pass also carries vertex fog, else TEXCOORD4.</summary>
        private static bool IsLightVector(string leaf) => leaf is "in:TEXCOORD3.x" or "in:TEXCOORD3.y" or "in:TEXCOORD3.z" or "in:TEXCOORD4.x" or "in:TEXCOORD4.y" or "in:TEXCOORD4.z";

        /// <summary>The highest max/saturate node on the light vector: the diffuse lighting term the transfer is linear in.</summary>
        private static Expr TopmostLightTerm(Expr root, Func<Expr, bool> onLight)
        {
            var queue = new Queue<Expr>([root]);
            var seen = new HashSet<Expr>();
            while (queue.Count > 0)
            {
                var e = queue.Dequeue();
                if (!seen.Add(e) || !onLight(e)) continue;
                if (e.Op is ExprOp.Max or ExprOp.Sat) return e;
                foreach (var a in e.Args) queue.Enqueue(a);
            }
            return null;
        }

        /// <summary>Whether a recovered world normal component is just the interpolated vertex normal (the default (0,0,1) tangent normal).</summary>
        private static bool IsNormalizedInterpolant(Expr e, string leaf) =>
            e.Op == ExprOp.Mul && e.Args.Any(a => a.Op == ExprOp.Leaf && a.Leaf == leaf) && e.Args.Any(a => a.Op == ExprOp.Rsq);

        /// <summary>From normalize(V).x × 0.5 + 0.5 (as compiled), V.x: the factor multiplying a reciprocal square root.</summary>
        private static Expr PreNormalized(Expr e)
        {
            // Strip the affine × 0.5 + 0.5
            if (e.Op == ExprOp.Add && e.Args.Any(a => a.IsConst)) e = e.Args.First(a => !a.IsConst);
            if (e.Op == ExprOp.Mul && e.Args.Any(a => a.IsConst && a.Value == 0.5f)) e = e.Args.First(a => !(a.IsConst && a.Value == 0.5f));
            if (e.Op == ExprOp.Mul && e.Args.Count(a => a.Op == ExprOp.Rsq) == 1) return e.Args.First(a => a.Op != ExprOp.Rsq);
            return null;
        }

        private static string VariableOf(string cbLeaf)
        {
            int open = cbLeaf.IndexOf('[');
            return open < 0 ? cbLeaf[3..] : cbLeaf[3..open];
        }

        private static bool UsesTimeExpression(MaterialUniformExpression e) => e switch
        {
            null => false,
            MaterialUniformExpressionTime or MaterialUniformExpressionRealTime => true,
            MaterialUniformExpressionUnaryOp u => UsesTimeExpression(u.X),
            MaterialUniformExpressionBinaryOp b => UsesTimeExpression(b.A) || UsesTimeExpression(b.B),
            _ => false
        };


        /// <summary>Writes recovered expressions as LE material template HLSL: interpolants as FMaterialPixelParameters fields, uniforms by their template names.</summary>
        private sealed class LeafMapper(ExprPool pool, ShaderDataflow source, RecoveredMaterialCode result)
        {
            /// <summary>Statements and the return of a scalar (1 value) or vector property.</summary>
            public string Write(Expr[] values, string format = null)
            {
                var emitter = new HlslEmitter(MapLeaf, "Local");
                emitter.Prepare(values);
                var parts = values.Select(emitter.Emit).ToArray();
                string value = format != null ? string.Format(format, parts)
                    : parts.Length == 1 ? parts[0] : $"float{parts.Length}({string.Join(", ", parts)})";
                return emitter.Statements + "\treturn " + value;
            }

            private string MapLeaf(string leaf)
            {
                if (leaf.StartsWith("in:", StringComparison.Ordinal)) return MapInterpolant(leaf[3..]);
                if (leaf.StartsWith("cb:", StringComparison.Ordinal)) return MapConstant(leaf[3..]);
                throw new ShaderRecoveryException($"The material reads {leaf}, which can't be mapped to the LE3 material template.");
            }

            private string MapInterpolant(string semanticComponent)
            {
                int dot = semanticComponent.LastIndexOf('.');
                string semantic = semanticComponent[..dot];
                char c = semanticComponent[dot + 1];
                switch (semantic)
                {
                    case "TEXCOORD10": result.UsingTransforms |= 0x1; return $"Parameters.TangentToWorld[0].{c}";
                    case "TEXCOORD11": result.UsingTransforms |= 0x1; return c == 'w' ? "Parameters.UnMirrored" : $"Parameters.TangentToWorld[2].{c}";
                    case "COLOR0": result.UsesVertexColor = true; return $"Parameters.VertexColor.{c}";
                    case "TEXCOORD6": return c == 'w' ? "1.0" : $"Parameters.TangentCameraVector.{c}";
                    case "TEXCOORD5":
                        // PixelPosition: world-relative position when the source was compiled with WORLD_POS, else the screen position
                        if ((result.SourceUsingTransforms & 0x8) == 0) return $"Parameters.ScreenPosition.{c}";
                        result.UsingTransforms |= 0x8;
                        return c == 'w' ? "1.0" : $"(Parameters.WorldPosition.{c} - CameraWorldPos.{c})";
                }
                if (semantic.StartsWith("TEXCOORD", StringComparison.Ordinal) && int.TryParse(semantic[8..], out int index) &&
                    index * 2 < Math.Max(result.NumUserTexCoords, 1))
                {
                    // TexCoords[2i] in .xy, TexCoords[2i+1] in .wz
                    return c switch
                    {
                        'x' => $"Parameters.TexCoords[{index * 2}].x",
                        'y' => $"Parameters.TexCoords[{index * 2}].y",
                        'w' => $"Parameters.TexCoords[{index * 2 + 1}].x",
                        _ => $"Parameters.TexCoords[{index * 2 + 1}].y"
                    };
                }
                throw new ShaderRecoveryException($"The material reads interpolant {semanticComponent}, which has no LE3 material template equivalent.");
            }

            private string MapConstant(string element)
            {
                int open = element.IndexOf('[');
                string name = element[..open];
                int index = int.Parse(element[(open + 1)..^1]);
                const string xyzw = "xyzw";
                if (name.StartsWith("UniformPixelScalar_", StringComparison.Ordinal))
                {
                    // LE1/LE2 declare each scalar alone; the LE3 template packs four to a vector
                    int scalar = int.Parse(name["UniformPixelScalar_".Length..]);
                    return $"UniformPixelScalars_{scalar / 4}.{xyzw[scalar % 4]}";
                }
                if (name.StartsWith("UniformPixelScalars_", StringComparison.Ordinal) || name.StartsWith("UniformPixelVector_", StringComparison.Ordinal))
                    return $"{name}.{xyzw[index % 4]}";
                if (name.EndsWith("Matrix", StringComparison.Ordinal) || name is "LocalToWorld" or "WorldToLocal" or "PreviousLocalToWorld")
                    // Column-major: each column is a 16-byte register; element (row, column) is name[row][column]
                    return $"{name}[{index % 4}][{index / 4}]";
                return index < 4 ? $"{name}.{xyzw[index]}" : $"{name}[{index / 4}].{xyzw[index % 4]}";
            }
        }
    }
}
