namespace LegendaryExplorer.Lightmaps.Solver;

/// <summary>The solver's HLSL compute kernels (cs_5_0). One source, compiled once per process; the same bytecode runs on the GPU and on WARP</summary>
internal static class SolverShaders
{
    public const int GroupSize = 64;
    public const int StackSize = 64;

    public const string Source = """
#define GROUP_SIZE 64
#define STACK_SIZE 64
#define PI 3.14159265358979
#define INF 3.0e38
#define INVALID 0xFFFFFFFFu

#define LIGHT_POINT 0u
#define LIGHT_SPOT 1u
#define LIGHT_DIRECTIONAL 2u

#define TF_MATERIAL_MASK 0x00FFFFFFu
#define TF_CAST_SHADOW (1u << 24)
#define TF_TWO_SIDED_SHADOW (1u << 25)
#define TF_SELF_SHADOW_ONLY (1u << 26)
#define TF_SHADOW_INDIRECT_ONLY (1u << 27)
#define TF_TRANSMISSIVE (1u << 28)
#define TF_MAT_TWO_SIDED (1u << 29)
#define TF_FLIPPED (1u << 30)

#define MF_TWO_SIDED 1u
#define MF_MASKED 2u

#define SF_TRANSLUCENCY 1u
#define SF_LIGHT_FACING 2u
#define SF_SKY_BOUNCES 4u

#define MAX_TRANSLUCENT_LAYERS 8u

cbuffer Params : register(b0)
{
    uint ItemCount;
    uint GatherRays;
    uint Bounces;
    uint ShadowRays;

    uint PenumbraRays;
    uint Seed;
    uint NumBounceLights;
    uint NumTriangles;

    float RayOffset;
    float NormalOffset;
    float NormalInfluence;
    float MaxOcclusionDistance;

    float DirScale;
    float DirPower;
    float SceneExtent;
    uint SceneFlags;

    float3 SkyUpper;
    uint ParamPad0;
    float3 SkyLower;
    uint ParamPad1;

    float4 LmBasis[3];

    uint PhotonMode;
    uint EmitPass;
    uint EmitBase;
    uint EmitCapacity;

    float3 ImpMin;
    uint NumEmitLights;
    float3 ImpMax;
    float ImpRadius;

    uint TableAOff;
    uint TableASize;
    float CellA;
    float RadiusA;

    uint TableBOff;
    uint TableBSize;
    float CellB;
    float RadiusB;

    uint KNearest;
    float ConeK;
    float NormalCos;
    float PlaneSin;

    uint PhotonVariant;
    float NormalTexelScale;
    uint2 PhotonPad;
};

#define PV_SPOT_SPHERE 1u
#define PV_NO_FALLOFF 2u
#define PV_DIRECT_EXPLICIT 4u
#define PV_LOOKUP_SAME_INSTANCE 8u
#define PV_LOOKUP_FINE_ONLY 16u
#define PV_PASS_BACK 32u

struct BvhNode { float3 aMin; uint aData; float3 aMax; uint aCount; float3 bMin; uint bData; float3 bMax; uint bCount; };
struct Tri { float3 v0; uint flags; float3 e1; uint instance; float3 e2; uint orig; };
struct Vert { float3 n; float2 uv; float pad; };
struct Material { uint diffOff; uint diffSize; uint emisOff; uint emisSize; uint transOff; uint transSize; uint flags; float clip; };
struct Light { float3 pos; uint type; float3 dir; float radius; float3 color; float falloff; float3 indColor; float cosInner; float cosOuter; float sourceSize; uint castShadows; float shadowExp; };
struct Sample { float3 pos; float texelRadius; float3 n; uint instance; float3 ng; uint flags; float3 t; uint pad0; float3 b; uint pad1; };

StructuredBuffer<BvhNode> Nodes : register(t0);
StructuredBuffer<Tri> Tris : register(t1);
StructuredBuffer<uint4> TriVerts : register(t2);
StructuredBuffer<Vert> Verts : register(t3);
StructuredBuffer<Material> Materials : register(t4);
StructuredBuffer<float4> Texels : register(t5);
StructuredBuffer<Light> Lights : register(t6);
StructuredBuffer<uint> BounceLights : register(t7);
StructuredBuffer<Sample> Samples : register(t8);
StructuredBuffer<uint4> Items : register(t9);

struct Photon { float3 pos; uint nrm; float3 power; uint tri; };
struct HashCell { int3 cell; uint start; uint count; uint3 pad; };

// photon mapping: sorted photons (irradiance estimate), hash tables over grid cells, irradiance photons, the lights' shares of the photon paths
StructuredBuffer<Photon> PhotonsIn : register(t10);
StructuredBuffer<HashCell> Cells : register(t11);
StructuredBuffer<Photon> IrrPhotons : register(t12);
StructuredBuffer<uint4> EmitLights : register(t13);

RWStructuredBuffer<uint4> DirectOut : register(u0);
RWStructuredBuffer<uint4> GatherRecords : register(u1);
RWStructuredBuffer<uint4> ReduceOut : register(u2);
RWStructuredBuffer<Photon> PhotonOut : register(u3);
RWStructuredBuffer<uint> Counter : register(u4);
RWStructuredBuffer<float4> IrrOut : register(u5);

// ---------------------------------------------------------------- random numbers

uint Pcg(uint v)
{
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

uint SeedFor(uint a, uint b, uint c)
{
    return Pcg(Seed ^ Pcg(a ^ Pcg(b ^ Pcg(c))));
}

float NextFloat(inout uint state)
{
    state = Pcg(state);
    return (float)(state >> 8) * (1.0 / 16777216.0);
}

// Jittered stratum i of n over the unit square, rotated (Cranley-Patterson) so a partly filled last column doesn't bias the estimate
float2 Stratified(uint i, uint n, float2 jitter, float2 rot)
{
    uint rows = max(1u, (uint)sqrt((float)n + 0.5));
    uint cols = (n + rows - 1u) / rows;
    float2 u = float2(((float)(i % rows) + jitter.x) / (float)rows, ((float)(i / rows) + jitter.y) / (float)cols);
    return frac(u + rot);
}

// ---------------------------------------------------------------- geometry helpers

void Basis(float3 n, out float3 t, out float3 b)
{
    float s = n.z >= 0 ? 1.0 : -1.0;
    float a = -1.0 / (s + n.z);
    float bb = n.x * n.y * a;
    t = float3(1.0 + s * n.x * n.x * a, s * bb, -s * n.x);
    b = float3(bb, s + n.y * n.y * a, -n.y);
}

float2 DiskSample(float2 u)
{
    float r = sqrt(u.x);
    float phi = 2.0 * PI * u.y;
    return float2(r * cos(phi), r * sin(phi));
}

float3 CosineSample(float3 n, float3 t, float3 b, float2 u)
{
    float r = sqrt(u.x);
    float phi = 2.0 * PI * u.y;
    return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(saturate(1.0 - u.x)));
}

float3 SafeInv(float3 d)
{
    float3 a = abs(d);
    float3 s = float3(d.x >= 0 ? 1e-20 : -1e-20, d.y >= 0 ? 1e-20 : -1e-20, d.z >= 0 ? 1e-20 : -1e-20);
    return 1.0 / float3(a.x > 1e-20 ? d.x : s.x, a.y > 1e-20 ? d.y : s.y, a.z > 1e-20 ? d.z : s.z);
}

float Slab(float3 bmin, float3 bmax, float3 o, float3 inv, float tmin, float tmax)
{
    float3 t0 = (bmin - o) * inv;
    float3 t1 = (bmax - o) * inv;
    float3 lo = min(t0, t1);
    float3 hi = max(t0, t1);
    float tn = max(max(lo.x, lo.y), max(lo.z, tmin));
    float tf = min(min(hi.x, hi.y), min(hi.z, tmax));
    return tn <= tf ? tn : INF;
}

bool IntersectTri(Tri tri, float3 o, float3 d, float tmin, float tmax, out float t, out float u, out float v, out bool front)
{
    t = 0; u = 0; v = 0; front = true;
    float3 p = cross(d, tri.e2);
    float det = dot(tri.e1, p);
    if (det == 0) return false;
    float inv = 1.0 / det;
    float3 s = o - tri.v0;
    u = dot(s, p) * inv;
    if (u < 0 || u > 1) return false;
    float3 q = cross(s, tri.e1);
    v = dot(d, q) * inv;
    if (v < 0 || u + v > 1) return false;
    t = dot(tri.e2, q) * inv;
    // det > 0: the ray runs against cross(e1, e2)
    front = (det > 0) != ((tri.flags & TF_FLIPPED) != 0);
    return t > tmin && t < tmax;
}

// ---------------------------------------------------------------- materials

float4 FetchTexel(uint off, uint size, int x, int y)
{
    int s = (int)size;
    x = ((x % s) + s) % s;
    y = ((y % s) + s) % s;
    return Texels[off + (uint)y * size + (uint)x];
}

float4 SampleTex(uint off, uint size, float2 uv)
{
    float2 p = uv * (float)size - 0.5;
    p = clamp(p, -1.0e7, 1.0e7);
    float2 f = floor(p);
    float2 w = p - f;
    int x = (int)f.x;
    int y = (int)f.y;
    float4 a = FetchTexel(off, size, x, y);
    float4 b = FetchTexel(off, size, x + 1, y);
    float4 c = FetchTexel(off, size, x, y + 1);
    float4 d = FetchTexel(off, size, x + 1, y + 1);
    return lerp(lerp(a, b, w.x), lerp(c, d, w.x), w.y);
}

float2 HitUV(Tri tri, float u, float v)
{
    uint4 iv = TriVerts[tri.orig];
    return Verts[iv.x].uv * (1.0 - u - v) + Verts[iv.y].uv * u + Verts[iv.z].uv * v;
}

// How a triangle treats a ray: 0 lets it through, 1 blocks it, 2 tints it (translucent, trans = transmission)
uint Classify(Tri tri, float u, float v, bool front, uint fromInstance, bool indirect, bool shadowRay, out float3 trans)
{
    trans = 1;
    uint f = tri.flags;
    if ((f & TF_CAST_SHADOW) == 0) return 0;
    if ((f & TF_SELF_SHADOW_ONLY) != 0 && tri.instance != fromInstance) return 0;
    if ((f & TF_SHADOW_INDIRECT_ONLY) != 0 && !indirect) return 0;
    if (shadowRay && (f & (TF_TWO_SIDED_SHADOW | TF_MAT_TWO_SIDED)) == 0)
    {
        // UE3: a one-sided triangle stops direct light only at its front (the side facing the light; a shadow ray runs from the receiver, so it
        // passes the front it meets), and bounced light at either side. Otherwise: it shadows what faces its front
        if ((SceneFlags & SF_LIGHT_FACING) != 0) { if (!indirect && front) return 0; }
        else if (!front) return 0;
    }
    if ((f & TF_TRANSMISSIVE) != 0)
    {
        Material m = Materials[f & TF_MATERIAL_MASK];
        float4 tr = SampleTex(m.transOff, m.transSize, HitUV(tri, u, v));
        if ((m.flags & MF_MASKED) != 0) return tr.r < m.clip ? 0 : 1;
        trans = saturate(tr.rgb);
        return 2;
    }
    return 1;
}

// ---------------------------------------------------------------- traversal

struct Hit { float t; float u; float v; uint tri; bool front; };

void LeafClosest(uint first, uint count, float3 o, float3 d, float tmin, uint fromInstance, uint ignoreTri, inout Hit hit)
{
    [loop] for (uint i = first; i < first + count; i++)
    {
        if (i == ignoreTri) continue;
        Tri tri = Tris[i];
        float t, u, v; bool front;
        if (!IntersectTri(tri, o, d, tmin, hit.t, t, u, v, front)) continue;
        float3 tr;
        if (Classify(tri, u, v, front, fromInstance, true, false, tr) != 1) continue;
        hit.t = t; hit.u = u; hit.v = v; hit.tri = i; hit.front = front;
    }
}

#ifdef HW_RT
// Hardware ray tracing (Direct3D 12, DXR 1.1 inline ray queries): the scene's triangles in an acceleration structure, primitive i being Tris[i].
// Every triangle is non-opaque, so each candidate goes through Classify exactly as the BVH traversal below does
RaytracingAccelerationStructure SceneBVH : register(t14);

bool FrontFacing(Tri tri, float3 d)
{
    float det = dot(tri.e1, cross(d, tri.e2));
    return (det > 0) != ((tri.flags & TF_FLIPPED) != 0);
}

bool TraceClosest(float3 o, float3 d, float tmin, float tmax, uint fromInstance, uint ignoreTri, out Hit hit)
{
    hit.t = tmax; hit.u = 0; hit.v = 0; hit.tri = INVALID; hit.front = true;
    if (NumTriangles == 0 || !(tmax > tmin)) return false;
    RayDesc ray;
    ray.Origin = o; ray.Direction = d; ray.TMin = tmin; ray.TMax = tmax;
    RayQuery<RAY_FLAG_FORCE_NON_OPAQUE> q;
    q.TraceRayInline(SceneBVH, RAY_FLAG_NONE, 0xFF, ray);
    [loop] while (q.Proceed())
    {
        uint i = q.CandidatePrimitiveIndex();
        if (i == ignoreTri) continue;
        Tri tri = Tris[i];
        float2 b = q.CandidateTriangleBarycentrics();
        float3 tr;
        if (Classify(tri, b.x, b.y, FrontFacing(tri, d), fromInstance, true, false, tr) == 1) q.CommitNonOpaqueTriangleHit();
    }
    if (q.CommittedStatus() != COMMITTED_TRIANGLE_HIT) return false;
    float2 cb = q.CommittedTriangleBarycentrics();
    hit.t = q.CommittedRayT(); hit.u = cb.x; hit.v = cb.y; hit.tri = q.CommittedPrimitiveIndex();
    hit.front = FrontFacing(Tris[hit.tri], d);
    return true;
}

float3 TraceShadow(float3 o, float3 d, float tmin, float tmax, uint fromInstance, uint ignoreTri, bool indirect)
{
    float3 trans = 1;
    if (NumTriangles == 0 || !(tmax > tmin)) return trans;
    RayDesc ray;
    ray.Origin = o; ray.Direction = d; ray.TMin = tmin; ray.TMax = tmax;
    RayQuery<RAY_FLAG_FORCE_NON_OPAQUE> q;
    q.TraceRayInline(SceneBVH, RAY_FLAG_NONE, 0xFF, ray);
    uint layers = 0;
    bool blocked = false;
    [loop] while (q.Proceed())
    {
        uint i = q.CandidatePrimitiveIndex();
        if (i == ignoreTri) continue;
        Tri tri = Tris[i];
        float2 b = q.CandidateTriangleBarycentrics();
        float3 tr;
        uint c = Classify(tri, b.x, b.y, FrontFacing(tri, d), fromInstance, indirect, true, tr);
        if (c == 0) continue;
        if (c == 1) { blocked = true; q.Abort(); continue; }
        trans *= tr;
        layers++;
        if (layers > MAX_TRANSLUCENT_LAYERS || all(trans <= 0)) { blocked = true; q.Abort(); }
    }
    return blocked ? float3(0, 0, 0) : trans;
}
#else
// The nearest surface that stops a gather/path ray (masked holes and translucent surfaces let it through)
bool TraceClosest(float3 o, float3 d, float tmin, float tmax, uint fromInstance, uint ignoreTri, out Hit hit)
{
    hit.t = tmax; hit.u = 0; hit.v = 0; hit.tri = INVALID; hit.front = true;
    if (NumTriangles == 0) return false;
    float3 inv = SafeInv(d);
    uint stack[STACK_SIZE];
    uint sp = 0;
    uint node = 0;
    [loop] while (true)
    {
        BvhNode n = Nodes[node];
        float ta = n.aData == INVALID ? INF : Slab(n.aMin, n.aMax, o, inv, tmin, hit.t);
        float tb = n.bData == INVALID ? INF : Slab(n.bMin, n.bMax, o, inv, tmin, hit.t);
        bool goA = ta < INF;
        bool goB = tb < INF;
        [branch] if (goA && n.aCount > 0) { LeafClosest(n.aData, n.aCount, o, d, tmin, fromInstance, ignoreTri, hit); goA = false; }
        [branch] if (goB && n.bCount > 0) { LeafClosest(n.bData, n.bCount, o, d, tmin, fromInstance, ignoreTri, hit); goB = false; }
        if (goA && goB)
        {
            uint nearNode = n.aData, farNode = n.bData;
            if (tb < ta) { nearNode = n.bData; farNode = n.aData; }
            if (sp < STACK_SIZE) { stack[sp] = farNode; sp++; }
            node = nearNode;
        }
        else if (goA) node = n.aData;
        else if (goB) node = n.bData;
        else
        {
            if (sp == 0) break;
            sp--;
            node = stack[sp];
        }
    }
    return hit.tri != INVALID;
}

// Transmission along a shadow ray over (tmin, tmax): 0 when something opaque blocks it
float3 TraceShadow(float3 o, float3 d, float tmin, float tmax, uint fromInstance, uint ignoreTri, bool indirect)
{
    float3 trans = 1;
    if (NumTriangles == 0 || !(tmax > tmin)) return trans;
    float3 inv = SafeInv(d);
    uint layers = 0;
    uint stack[STACK_SIZE];
    uint sp = 0;
    uint node = 0;
    bool blocked = false;
    [loop] while (!blocked)
    {
        BvhNode n = Nodes[node];
        float ta = n.aData == INVALID ? INF : Slab(n.aMin, n.aMax, o, inv, tmin, tmax);
        float tb = n.bData == INVALID ? INF : Slab(n.bMin, n.bMax, o, inv, tmin, tmax);
        bool goA = ta < INF;
        bool goB = tb < INF;
        [loop] for (uint side = 0; side < 2 && !blocked; side++)
        {
            bool go = side == 0 ? goA : goB;
            uint count = side == 0 ? n.aCount : n.bCount;
            if (!go || count == 0) continue;
            uint first = side == 0 ? n.aData : n.bData;
            [loop] for (uint i = first; i < first + count; i++)
            {
                if (i == ignoreTri) continue;
                Tri tri = Tris[i];
                float t, u, v; bool front;
                if (!IntersectTri(tri, o, d, tmin, tmax, t, u, v, front)) continue;
                float3 tr;
                uint c = Classify(tri, u, v, front, fromInstance, indirect, true, tr);
                if (c == 0) continue;
                if (c == 1) { blocked = true; break; }
                trans *= tr;
                layers++;
                if (layers > MAX_TRANSLUCENT_LAYERS || all(trans <= 0)) { blocked = true; break; }
            }
            if (side == 0) goA = false; else goB = false;
        }
        if (blocked) break;
        if (goA && goB)
        {
            if (sp < STACK_SIZE) { stack[sp] = n.bData; sp++; }
            node = n.aData;
        }
        else if (goA) node = n.aData;
        else if (goB) node = n.bData;
        else
        {
            if (sp == 0) break;
            sp--;
            node = stack[sp];
        }
    }
    return blocked ? float3(0, 0, 0) : trans;
}
#endif

// ---------------------------------------------------------------- lights

float Attenuation(Light L, float3 p)
{
    if (L.type == LIGHT_DIRECTIONAL) return 1.0;
    float3 v = p - L.pos;
    float d2 = dot(v, v);
    float r2 = L.radius * L.radius;
    if (!(d2 < r2)) return 0.0;
    float a = pow(max(1.0 - d2 / r2, 0.0), L.falloff);
    if (L.type == LIGHT_SPOT)
    {
        float c = d2 > 0 ? dot(v * rsqrt(d2), L.dir) : 1.0;
        float denom = L.cosInner - L.cosOuter;
        float s = denom > 1e-6 ? saturate((c - L.cosOuter) / denom) : (c >= L.cosOuter ? 1.0 : 0.0);
        a *= s * s;
    }
    return a;
}

// A point on the light (point/spot: on its sphere; directional: a direction in its cone) as seen from p
void SampleLight(Light L, float3 p, float2 u, out float3 dir, out float dist)
{
    if (L.type == LIGHT_DIRECTIONAL)
    {
        float3 axis = -L.dir;
        float cosMax = cos(L.sourceSize);
        float cosT = 1.0 - u.x * (1.0 - cosMax);
        float sinT = sqrt(saturate(1.0 - cosT * cosT));
        float phi = 2.0 * PI * u.y;
        float3 t, b;
        Basis(axis, t, b);
        dir = normalize(t * (cos(phi) * sinT) + b * (sin(phi) * sinT) + axis * cosT);
        dist = SceneExtent;
    }
    else
    {
        float z = 1.0 - 2.0 * u.x;
        float r = sqrt(saturate(1.0 - z * z));
        float phi = 2.0 * PI * u.y;
        float3 lp = L.pos + L.sourceSize * float3(r * cos(phi), r * sin(phi), z);
        float3 v = lp - p;
        dist = length(v);
        dir = dist > 0 ? v / dist : float3(0, 0, 1);
    }
}

// Whether all of the light lies behind the plane through p with normal n
bool LightBehind(Light L, float3 p, float3 n)
{
    if (L.type == LIGHT_DIRECTIONAL) return dot(-L.dir, n) < -sin(L.sourceSize);
    return dot(L.pos - p, n) < -L.sourceSize;
}

float Coef(float3 dT, uint i)
{
    float c = dot(dT, LmBasis[i].xyz);
    return c > 0 ? DirScale * pow(c, DirPower) : 0.0;
}

// How far off its surface a sample's rays leave (Lightmass: a fraction of the texel radius, at most VisibilityNormalOffsetDistance)
float SampleOffset(Sample s)
{
    return NormalTexelScale > 0 ? clamp(s.texelRadius * NormalTexelScale, RayOffset, NormalOffset) : NormalOffset;
}

// ---------------------------------------------------------------- direct pass: one thread per (sample, light)

[numthreads(GROUP_SIZE, 1, 1)]
void DirectCS(uint3 id : SV_DispatchThreadID)
{
    uint idx = id.x;
    if (idx >= ItemCount) return;
    uint4 item = Items[idx];
    Sample s = Samples[item.x];
    Light L = Lights[item.y];
    bool shadowMap = item.z != 0;

    float3 N = s.n, Ng = s.ng, T = s.t, B = s.b;
    float3 toCenter = L.type == LIGHT_DIRECTIONAL ? -L.dir : L.pos - s.pos;
    if ((s.flags & 1u) != 0 && dot(toCenter, N) < 0) { N = -N; Ng = -Ng; T = -T; B = -B; }

    float3 e0 = 0, e1 = 0, e2 = 0, es = 0;
    float vis = 0;
    uint rays = 0;
    float att = Attenuation(L, s.pos);
    [branch] if (att > 0 && !LightBehind(L, s.pos, N))
    {
        float3 color = L.color * att;
        float3 tg, bg;
        Basis(Ng, tg, bg);
        uint state = SeedFor(item.x, item.y, 0x5EDu);
        float2 rot = float2(NextFloat(state), NextFloat(state));
        bool shadows = L.castShadows != 0;
        float visSum = 0;
        uint count = 0;
        [loop] for (uint phase = 0; phase < 2; phase++)
        {
            uint n = phase == 0 ? ShadowRays : PenumbraRays;
            if (phase == 1)
            {
                if (!shadows || !(visSum > 0 && visSum < (float)count)) break;
                rot = float2(NextFloat(state), NextFloat(state));
            }
            [loop] for (uint i = 0; i < n; i++)
            {
                float2 jit = float2(NextFloat(state), NextFloat(state));
                float2 u = Stratified(i, n, jit, rot);
                float2 du = DiskSample(float2(NextFloat(state), NextFloat(state))) * s.texelRadius;
                float3 p = s.pos + tg * du.x + bg * du.y;
                float3 dir; float dist;
                SampleLight(L, p, u, dir, dist);
                float3 tr = 1;
                if (shadows)
                {
                    tr = TraceShadow(p + Ng * SampleOffset(s), dir, RayOffset, dist - RayOffset, s.instance, INVALID, false);
                    rays++;
                }
                visSum += (tr.r + tr.g + tr.b) * (1.0 / 3.0);
                count++;
                if (!shadowMap)
                {
                    float3 dT = float3(dot(dir, T), dot(dir, B), dot(dir, N));
                    if (dT.z > 0)
                    {
                        float3 c = color * tr;
                        e0 += c * Coef(dT, 0);
                        e1 += c * Coef(dT, 1);
                        e2 += c * Coef(dT, 2);
                        es += c * dT.z;
                    }
                }
            }
        }
        float invCount = count > 0 ? 1.0 / (float)count : 0.0;
        vis = visSum * invCount;
        // Lightmass's ShadowExponent: the shadow factor is raised to it, which narrows penumbras; the lighting keeps its directions
        float shaped = vis > 0 && vis < 1 && L.shadowExp != 1 ? pow(vis, L.shadowExp) : vis;
        float scale = invCount * (vis > 0 ? shaped / vis : 1.0);
        e0 *= scale; e1 *= scale; e2 *= scale; es *= scale;
        vis = shaped;
    }
    DirectOut[idx * 4 + 0] = uint4(asuint(e0), asuint(vis));
    DirectOut[idx * 4 + 1] = uint4(asuint(e1), rays);
    DirectOut[idx * 4 + 2] = uint4(asuint(e2), 0);
    DirectOut[idx * 4 + 3] = uint4(asuint(es), 0);
}

// ---------------------------------------------------------------- photon mapping

uint OctEncode(float3 n)
{
    float3 a = n / max(abs(n.x) + abs(n.y) + abs(n.z), 1e-20);
    float2 e = a.xy;
    if (a.z < 0) e = (1.0 - abs(a.yx)) * float2(a.x >= 0 ? 1.0 : -1.0, a.y >= 0 ? 1.0 : -1.0);
    uint2 q = (uint2)round(saturate(e * 0.5 + 0.5) * 65535.0);
    return q.x | (q.y << 16);
}

float3 OctDecode(uint v)
{
    float2 e = float2((float)(v & 0xFFFFu), (float)(v >> 16)) * (2.0 / 65535.0) - 1.0;
    float3 n = float3(e, 1.0 - abs(e.x) - abs(e.y));
    if (n.z < 0) n.xy = (1.0 - abs(n.yx)) * float2(n.x >= 0 ? 1.0 : -1.0, n.y >= 0 ? 1.0 : -1.0);
    return normalize(n);
}

bool InImportance(float3 p)
{
    return all(p >= ImpMin) && all(p <= ImpMax);
}

uint CellHash(int3 c, uint size)
{
    return Pcg((uint)c.x ^ Pcg((uint)c.y ^ Pcg((uint)c.z))) & (size - 1u);
}

bool FindCell(uint off, uint size, int3 c, out uint start, out uint count)
{
    start = 0; count = 0;
    if (size == 0) return false;
    uint h = CellHash(c, size);
    [loop] for (uint probe = 0; probe < size; probe++)
    {
        HashCell hc = Cells[off + h];
        if (hc.count == 0) return false;
        if (all(hc.cell == c)) { start = hc.start; count = hc.count; return true; }
        h = (h + 1u) & (size - 1u);
    }
    return false;
}

// The surface a photon or gather ray landed on: false when it's a one-sided material's back
bool HitSurface(float3 o, float3 d, Hit h, out float3 p, out float3 ng, out float3 ns, out float3 albedo, out uint inst)
{
    Tri tri = Tris[h.tri];
    Material m = Materials[tri.flags & TF_MATERIAL_MASK];
    p = o + d * h.t;
    inst = tri.instance;
    ng = normalize(cross(tri.e1, tri.e2));
    if (dot(ng, d) > 0) ng = -ng;
    uint4 iv = TriVerts[tri.orig];
    Vert v0 = Verts[iv.x], v1 = Verts[iv.y], v2 = Verts[iv.z];
    float w0 = 1.0 - h.u - h.v;
    ns = v0.n * w0 + v1.n * h.u + v2.n * h.v;
    float2 uv = v0.uv * w0 + v1.uv * h.u + v2.uv * h.v;
    ns = dot(ns, ns) > 1e-12 ? normalize(ns) : ng;
    if (dot(ns, ng) < 0) ns = -ns;
    albedo = m.diffSize > 0 ? saturate(SampleTex(m.diffOff, m.diffSize, uv).rgb) : float3(0, 0, 0);
    return h.front || (m.flags & MF_TWO_SIDED) != 0;
}

static uint gEmitSlot;

// A photon's flight: with PV_PASS_BACK it passes the back of a one-sided surface (UE3: a one-sided surface only stops light that reaches its front).
// o moves to the last surface passed, so o + d * h.t is the hit
bool TracePhoton(inout float3 o, float3 d, float tmax, uint fromInst, uint ignore, out Hit h)
{
    [loop] for (uint layer = 0; layer < 16; layer++)
    {
        if (!TraceClosest(o, d, RayOffset, tmax, fromInst, ignore, h)) return false;
        if ((PhotonVariant & PV_PASS_BACK) == 0 || h.front) return true;
        Tri tri = Tris[h.tri];
        if ((tri.flags & (TF_TWO_SIDED_SHADOW | TF_MAT_TWO_SIDED)) != 0) return true;
        o += d * h.t;
        tmax -= h.t;
        ignore = h.tri;
        fromInst = INVALID;
    }
    return false;
}

// Pass 2 (the pilot) stores the light's slot in place of the triangle
void Deposit(float3 p, float3 n, float3 power, uint tri)
{
    if (!InImportance(p) || all(power <= 0)) return;
    uint slot;
    InterlockedAdd(Counter[0], 1u, slot);
    if (slot >= EmitCapacity) return;
    Photon ph;
    ph.pos = p; ph.nrm = OctEncode(n); ph.power = power; ph.tri = EmitPass == 2 ? gEmitSlot : tri;
    PhotonOut[slot] = ph;
}

// One thread per photon path. Pass 0 stores direct photons (where light first lands), pass 1 indirect photons (where it lands after each bounce).
// A photon's power makes the photon density reproduce the light's own irradiance (UE3's falloff, no inverse square): colour × attenuation × d² / pdf
[numthreads(GROUP_SIZE, 1, 1)]
void EmitCS(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ItemCount) return;
    uint path = EmitBase + id.x;
    uint lo = 0, hi = NumEmitLights - 1u;
    [loop] while (lo < hi)
    {
        uint mid = (lo + hi + 1u) / 2u;
        if (EmitLights[mid].y <= path) lo = mid; else hi = mid - 1u;
    }
    gEmitSlot = lo;
    uint4 el = EmitLights[lo];
    Light L = Lights[el.x];
    float invCount = asfloat(el.w);
    uint state = SeedFor(path, EmitPass, 0xE417u);
    float3 o, d, power;
    float tmax;
    if (L.type == LIGHT_DIRECTIONAL)
    {
        float3 t, b;
        Basis(L.dir, t, b);
        float2 disk = DiskSample(float2(NextFloat(state), NextFloat(state))) * ImpRadius;
        o = (ImpMin + ImpMax) * 0.5 - L.dir * (2.0 * ImpRadius) + t * disk.x + b * disk.y;
        d = L.dir;
        tmax = SceneExtent + 4.0 * ImpRadius;
        power = L.indColor * (PI * ImpRadius * ImpRadius * invCount);
    }
    else
    {
        float2 u = float2(NextFloat(state), NextFloat(state));
        float pdf;
        float phi = 2.0 * PI * u.y;
        if (L.type == LIGHT_SPOT && (PhotonVariant & PV_SPOT_SPHERE) == 0)
        {
            float cosMax = clamp(L.cosOuter, -1.0, 0.9999);
            float cosT = 1.0 - u.x * (1.0 - cosMax);
            float sinT = sqrt(saturate(1.0 - cosT * cosT));
            float3 t, b;
            Basis(L.dir, t, b);
            d = normalize(t * (cos(phi) * sinT) + b * (sin(phi) * sinT) + L.dir * cosT);
            pdf = 1.0 / (2.0 * PI * (1.0 - cosMax));
        }
        else
        {
            float z = 1.0 - 2.0 * u.x;
            float r = sqrt(saturate(1.0 - z * z));
            d = float3(r * cos(phi), r * sin(phi), z);
            pdf = 1.0 / (4.0 * PI);
        }
        o = L.pos;
        tmax = L.radius;
        power = L.indColor * (invCount / pdf);
    }
    Hit h;
    if (!TracePhoton(o, d, tmax, INVALID, INVALID, h)) return;
    float3 p, ng, ns, albedo;
    uint inst;
    if (!HitSurface(o, d, h, p, ng, ns, albedo, inst)) return;
    if (L.type != LIGHT_DIRECTIONAL)
    {
        float att;
        if ((PhotonVariant & PV_NO_FALLOFF) != 0) att = h.t < L.radius ? 1.0 : 0.0;
        else if ((PhotonVariant & PV_SPOT_SPHERE) != 0) { float3 v = p - L.pos; float r2 = L.radius * L.radius; att = pow(max(1.0 - dot(v, v) / r2, 0.0), L.falloff); }
        else att = Attenuation(L, p);
        float dist = length(p - L.pos);
        power *= att * dist * dist;
    }
    if (EmitPass != 1)
    {
        Deposit(p, ng, power, h.tri);
        return;
    }
    float3 thr = power;
    uint tri = h.tri;
    [loop] for (uint k = 1; k < Bounces; k++)
    {
        thr *= albedo;
        if (k >= 2)
        {
            float pr = clamp(max(albedo.r, max(albedo.g, albedo.b)), 0.05, 0.95);
            if (NextFloat(state) >= pr) return;
            thr /= pr;
        }
        if (all(thr <= 0)) return;
        float3 ct, cb;
        Basis(ns, ct, cb);
        d = CosineSample(ns, ct, cb, float2(NextFloat(state), NextFloat(state)));
        if (dot(d, ng) <= 0) return;
        o = p + ng * NormalOffset;
        if (!TracePhoton(o, d, SceneExtent, inst, tri, h)) return;
        if (!HitSurface(o, d, h, p, ng, ns, albedo, inst)) return;
        tri = h.tri;
        Deposit(p, ng, thr, tri);
    }
}

#define HIST_BINS 32

#define EST_SHELLS 4

// Histogram of the squared distances (as fractions of r2) of the photons in the cube of cells within reach of c0 that face like n
void Histogram(float3 x, float3 n, uint off, uint size, int3 c0, int reach, float r2, inout uint hist[HIST_BINS])
{
    [loop] for (int dz = -reach; dz <= reach; dz++)
    [loop] for (int dy = -reach; dy <= reach; dy++)
    [loop] for (int dx = -reach; dx <= reach; dx++)
    {
        uint start, count;
        if (!FindCell(off, size, c0 + int3(dx, dy, dz), start, count)) continue;
        [loop] for (uint i = start; i < start + count; i++)
        {
            Photon ph = PhotonsIn[i];
            float3 v = ph.pos - x;
            float d2 = dot(v, v);
            if (d2 >= r2) continue;
            if (dot(OctDecode(ph.nrm), n) < NormalCos) continue;
            hist[min(HIST_BINS - 1u, (uint)(d2 / r2 * HIST_BINS))]++;
        }
    }
}

// Irradiance at x (surface normal n) from one photon map: the KNearest nearest photons within radius R, cone filtered (Jensen).
// The map's cells are R / EST_SHELLS wide; the search grows a shell of cells at a time and stops once KNearest photons are known to be closer
// than the shell's reach, so dense areas cost no more than sparse ones
float3 EstimateIrradiance(float3 x, float3 n, uint off, uint size, float cell, float R)
{
    if (size == 0 || !(R > 0)) return 0;
    int3 c0 = (int3)floor(x / cell);
    float R2 = R * R;
    uint hist[HIST_BINS];
    [unroll] for (uint hb = 0; hb < HIST_BINS; hb++) hist[hb] = 0;
    int reach = EST_SHELLS;
    float r2 = R2;
    bool enough = false;
    [loop] for (int s = 0; s <= EST_SHELLS && !enough; s++)
    {
        [loop] for (int dz = -s; dz <= s; dz++)
        [loop] for (int dy = -s; dy <= s; dy++)
        [loop] for (int dx = -s; dx <= s; dx++)
        {
            if (max(abs(dx), max(abs(dy), abs(dz))) != s) continue;
            uint start, count;
            if (!FindCell(off, size, c0 + int3(dx, dy, dz), start, count)) continue;
            [loop] for (uint i = start; i < start + count; i++)
            {
                Photon ph = PhotonsIn[i];
                float3 v = ph.pos - x;
                float d2 = dot(v, v);
                if (d2 >= R2) continue;
                if (dot(OctDecode(ph.nrm), n) < NormalCos) continue;
                hist[min(HIST_BINS - 1u, (uint)(d2 / R2 * HIST_BINS))]++;
            }
        }
        if (s == 0) continue;
        // every photon closer than s cells is in by now: the bins below (s / EST_SHELLS)² of R² are complete
        uint complete = (uint)(HIST_BINS * s * s / (EST_SHELLS * EST_SHELLS));
        uint acc = 0;
        [loop] for (uint b = 0; b < complete; b++)
        {
            acc += hist[b];
            if (acc >= KNearest) { r2 = R2 * (float)(b + 1u) / HIST_BINS; reach = s; enough = true; break; }
        }
    }
    if (enough)
    {
        // a finer histogram inside the coarse radius
        uint fine[HIST_BINS];
        [unroll] for (uint fb = 0; fb < HIST_BINS; fb++) fine[fb] = 0;
        Histogram(x, n, off, size, c0, reach, r2, fine);
        uint acc2 = 0;
        [loop] for (uint b2 = 0; b2 < HIST_BINS; b2++)
        {
            acc2 += fine[b2];
            if (acc2 >= KNearest) { r2 = r2 * (float)(b2 + 1u) / HIST_BINS; break; }
        }
    }
    float r = sqrt(r2);
    float3 sum = 0;
    uint used = 0;
    [loop] for (int ez = -reach; ez <= reach; ez++)
    [loop] for (int ey = -reach; ey <= reach; ey++)
    [loop] for (int ex = -reach; ex <= reach; ex++)
    {
        uint start, count;
        if (!FindCell(off, size, c0 + int3(ex, ey, ez), start, count)) continue;
        [loop] for (uint i = start; i < start + count; i++)
        {
            Photon ph = PhotonsIn[i];
            float3 v = ph.pos - x;
            float d2 = dot(v, v);
            if (d2 >= r2) continue;
            if (dot(OctDecode(ph.nrm), n) < NormalCos) continue;
            sum += ph.power * max(0.0, 1.0 - sqrt(d2) / (ConeK * r));
            used++;
        }
    }
    if (used == 0) return 0;
    return sum / ((1.0 - 2.0 / (3.0 * ConeK)) * PI * r2);
}


// Direct light at a point by shadow rays to every light that bounces (one ray each)
float3 DirectAt(float3 p, float3 n, uint inst, uint ignoreTri, inout uint state)
{
    float3 E = 0;
    [loop] for (uint k = 0; k < NumBounceLights; k++)
    {
        Light L = Lights[BounceLights[k]];
        float att = Attenuation(L, p);
        float2 u = float2(NextFloat(state), NextFloat(state));
        if (att <= 0 || LightBehind(L, p, n)) continue;
        float3 dir; float dist;
        SampleLight(L, p, u, dir, dist);
        float cosN = dot(dir, n);
        if (cosN <= 0) continue;
        float3 c = L.indColor * (att * cosN);
        if (L.castShadows != 0) c *= TraceShadow(p, dir, RayOffset, dist - RayOffset, inst, ignoreTri, true);
        E += c;
    }
    return E;
}

// One thread per irradiance photon: its irradiance from the direct (table A) and indirect (table B) photon maps
[numthreads(GROUP_SIZE, 1, 1)]
void IrradianceCS(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ItemCount) return;
    uint index = EmitBase + id.x;
    Photon ip = IrrPhotons[index];
    float3 n = OctDecode(ip.nrm);
    float3 E = 0;
    [branch] if ((PhotonVariant & PV_DIRECT_EXPLICIT) != 0)
    {
        uint state = SeedFor(index, 0x1AAu, 0x77u);
        E += DirectAt(ip.pos + n * NormalOffset, n, Tris[ip.tri].instance, ip.tri, state);
    }
    else
        E += EstimateIrradiance(ip.pos, n, TableAOff, TableASize, CellA, RadiusA);
    E += EstimateIrradiance(ip.pos, n, TableBOff, TableBSize, CellB, RadiusB);
    IrrOut[index] = float4(E, 0);
}

// The irradiance of the irradiance photon nearest to x that faces like n and lies near its plane: the fine grid (table A) first, then the coarse (B)
float3 LookupIrradiance(float3 x, float3 n, uint inst)
{
    float best = (PhotonVariant & PV_LOOKUP_FINE_ONLY) != 0 ? CellA * CellA : RadiusB * RadiusB;
    float3 E = 0;
    [loop] for (uint stage = 0; stage < 2; stage++)
    {
        uint off = stage == 0 ? TableAOff : TableBOff;
        uint size = stage == 0 ? TableASize : TableBSize;
        float cell = stage == 0 ? CellA : CellB;
        int3 c0 = (int3)floor(x / cell);
        [loop] for (int dz = -1; dz <= 1; dz++)
        [loop] for (int dy = -1; dy <= 1; dy++)
        [loop] for (int dx = -1; dx <= 1; dx++)
        {
            uint start, count;
            if (!FindCell(off, size, c0 + int3(dx, dy, dz), start, count)) continue;
            [loop] for (uint i = start; i < start + count; i++)
            {
                Photon ip = IrrPhotons[i];
                float3 v = ip.pos - x;
                float d2 = dot(v, v);
                if (d2 >= best) continue;
                float pd = dot(v, n);
                if (pd * pd > d2 * PlaneSin * PlaneSin + 4.0) continue;
                if (dot(OctDecode(ip.nrm), n) < NormalCos) continue;
                if ((PhotonVariant & PV_LOOKUP_SAME_INSTANCE) != 0 && Tris[ip.tri].instance != inst) continue;
                best = d2;
                E = ip.power;
            }
        }
        if (stage == 0 && (best <= cell * cell || (PhotonVariant & PV_LOOKUP_FINE_ONLY) != 0)) break;
    }
    return E;
}

// ---------------------------------------------------------------- indirect pass

void GatherFrame(Sample s, out float3 nh, out float3 t, out float3 b)
{
    nh = normalize(lerp(s.ng, s.n, NormalInfluence));
    if (!(dot(nh, s.ng) > 0.01)) nh = s.ng;
    Basis(nh, t, b);
}

float3 GatherDir(uint si, uint ray, float3 nh, float3 t, float3 b)
{
    uint rs = SeedFor(si, 0xFFFFFFFFu, 0x6A7u);
    float2 rot = float2(NextFloat(rs), NextFloat(rs));
    uint st = SeedFor(si, ray, 0x9A7u);
    float2 jit = float2(NextFloat(st), NextFloat(st));
    return CosineSample(nh, t, b, Stratified(ray, GatherRays, jit, rot));
}

float LightWeight(Light L, float3 p, float3 n)
{
    float att = Attenuation(L, p);
    if (att <= 0 || LightBehind(L, p, n)) return 0;
    float3 toC = L.type == LIGHT_DIRECTIONAL ? -L.dir : L.pos - p;
    float len = length(toC);
    float cosC = len > 0 ? dot(toC / len, n) : 1.0;
    return att * dot(L.indColor, float3(0.3, 0.59, 0.11)) * max(cosC, 0.05);
}

// Irradiance at a path vertex from the lights that bounce: one or two lights picked in proportion to their unshadowed contribution
float3 NextEventEstimate(float3 po, float3 ns, float3 ng, uint inst, uint ignoreTri, inout uint state, inout uint rays)
{
    if (NumBounceLights == 0) return 0;
    float total = 0;
    [loop] for (uint k = 0; k < NumBounceLights; k++) total += LightWeight(Lights[BounceLights[k]], po, ns);
    if (!(total > 0)) return 0;
    uint picks = NumBounceLights > 1 ? 2u : 1u;
    float3 E = 0;
    [loop] for (uint pk = 0; pk < picks; pk++)
    {
        float r = NextFloat(state) * total;
        uint chosen = NumBounceLights - 1;
        float w = 0;
        float acc = 0;
        [loop] for (uint k2 = 0; k2 < NumBounceLights; k2++)
        {
            w = LightWeight(Lights[BounceLights[k2]], po, ns);
            acc += w;
            if (r < acc) { chosen = k2; break; }
        }
        float2 u = float2(NextFloat(state), NextFloat(state));
        if (!(w > 0)) continue;
        Light L = Lights[BounceLights[chosen]];
        float3 dir; float dist;
        SampleLight(L, po, u, dir, dist);
        float cosN = dot(dir, ns);
        if (cosN <= 0 || dot(dir, ng) <= 0) continue;
        float3 c = L.indColor * (Attenuation(L, po) * cosN);
        if (L.castShadows != 0)
        {
            c *= TraceShadow(po, dir, RayOffset, dist - RayOffset, inst, ignoreTri, true);
            rays++;
        }
        E += c * (total / (w * (float)picks));
    }
    return E;
}

// One thread per (sample, gather ray): the radiance arriving along the ray, path traced through the bounces
[numthreads(GROUP_SIZE, 1, 1)]
void GatherCS(uint3 id : SV_DispatchThreadID)
{
    uint idx = id.x;
    if (idx >= ItemCount) return;
    uint local = idx / GatherRays;
    uint ray = idx % GatherRays;
    uint4 item = Items[local];
    uint si = item.x;
    // The receiver's sky lights (upper and lower hemisphere radiance as halves): only the sky lights relevant to it light it
    float3 skyUp = float3(f16tof32(item.y), f16tof32(item.y >> 16), f16tof32(item.z));
    float3 skyLo = float3(f16tof32(item.z >> 16), f16tof32(item.w), f16tof32(item.w >> 16));
    Sample s = Samples[si];
    float3 nh, t, b;
    GatherFrame(s, nh, t, b);
    float3 d = GatherDir(si, ray, nh, t, b);
    uint state = SeedFor(si, ray, 0xB0Bu);

    float3 o = s.pos + s.ng * SampleOffset(s);
    uint fromInst = s.instance;
    uint ignore = INVALID;
    float3 thr = 1;
    float3 L = 0;
    uint rays = 0;
    float firstHit = 65504.0; // how far the ray went before it first hit something (half-precision max: it escaped)
    [loop] for (uint bounce = 0; bounce < 64; bounce++)
    {
        Hit h;
        bool hit = TraceClosest(o, d, RayOffset, SceneExtent, fromInst, ignore, h);
        rays++;
        [branch] if ((SceneFlags & SF_TRANSLUCENCY) != 0)
        {
            thr *= TraceShadow(o, d, RayOffset, h.t, fromInst, ignore, true);
            rays++;
        }
        if (bounce == 0 && hit) firstHit = min(h.t, 65504.0);
        if (all(thr <= 0)) break;
        if (!hit)
        {
            // The environment lights every escaping ray; sky light only the receiver's own rays (Lightmass never bounces sky light) unless the
            // profile bounces it
            bool sky = bounce == 0 || (SceneFlags & SF_SKY_BOUNCES) != 0;
            L += thr * (d.z >= 0 ? SkyUpper + (sky ? skyUp : 0) : SkyLower + (sky ? skyLo : 0));
            break;
        }
        Tri tri = Tris[h.tri];
        Material m = Materials[tri.flags & TF_MATERIAL_MASK];
        if (!h.front && (m.flags & MF_TWO_SIDED) == 0) break;
        float3 ngTri = normalize(cross(tri.e1, tri.e2));
        if (dot(ngTri, d) > 0) ngTri = -ngTri;
        uint4 iv = TriVerts[tri.orig];
        Vert v0 = Verts[iv.x], v1 = Verts[iv.y], v2 = Verts[iv.z];
        float w0 = 1.0 - h.u - h.v;
        float3 ns = v0.n * w0 + v1.n * h.u + v2.n * h.v;
        float2 uv = v0.uv * w0 + v1.uv * h.u + v2.uv * h.v;
        ns = dot(ns, ns) > 1e-12 ? normalize(ns) : ngTri;
        if (dot(ns, ngTri) < 0) ns = -ns;
        if (m.emisSize > 0) L += thr * max(SampleTex(m.emisOff, m.emisSize, uv).rgb, 0);
        if (bounce >= Bounces) break;
        float3 albedo = m.diffSize > 0 ? saturate(SampleTex(m.diffOff, m.diffSize, uv).rgb) : float3(0, 0, 0);
        if (all(albedo <= 0)) break;
        float3 po = o + d * h.t + ngTri * NormalOffset;
        [branch] if (PhotonMode != 0)
        {
            float3 hp = o + d * h.t;
            // Lightmass: the irradiance photon nearest to the hit; outside the importance volume, one bounce of direct light
            float3 Ep = InImportance(hp) ? LookupIrradiance(hp, ngTri, tri.instance) : NextEventEstimate(po, ns, ngTri, tri.instance, h.tri, state, rays);
            L += thr * albedo * (1.0 / PI) * Ep;
            break;
        }
        float3 E = NextEventEstimate(po, ns, ngTri, tri.instance, h.tri, state, rays);
        L += thr * albedo * (1.0 / PI) * E;
        if (bounce + 1 >= Bounces) break;
        thr *= albedo;
        if (bounce >= 1)
        {
            float p = clamp(max(albedo.r, max(albedo.g, albedo.b)), 0.05, 0.95);
            if (NextFloat(state) >= p) break;
            thr /= p;
        }
        float3 ct, cb;
        Basis(ns, ct, cb);
        d = CosineSample(ns, ct, cb, float2(NextFloat(state), NextFloat(state)));
        if (dot(d, ngTri) <= 0) break;
        o = po;
        fromInst = tri.instance;
        ignore = h.tri;
    }
    GatherRecords[idx] = uint4(asuint(L), min(rays, 0xFFFFu) | (f32tof16(firstHit) << 16));
}

// One thread per sample: folds its gather rays into the directional and simple coefficients
[numthreads(GROUP_SIZE, 1, 1)]
void ReduceCS(uint3 id : SV_DispatchThreadID)
{
    uint local = id.x;
    if (local >= ItemCount) return;
    uint si = Items[local].x;
    Sample s = Samples[si];
    float3 nh, t, b;
    GatherFrame(s, nh, t, b);
    float3 e0 = 0, e1 = 0, e2 = 0, es = 0;
    uint occ = 0, rays = 0;
    float minHit = 65504.0;
    float w0 = PI / (float)GatherRays;
    [loop] for (uint r = 0; r < GatherRays; r++)
    {
        uint4 rec = GatherRecords[local * GatherRays + r];
        rays += rec.w & 0xFFFFu;
        float firstHit = f16tof32(rec.w >> 16);
        if (firstHit < MaxOcclusionDistance) occ++;
        minHit = min(minHit, firstHit);
        float3 L = asfloat(rec.xyz);
        float3 d = GatherDir(si, r, nh, t, b);
        float3 dT = float3(dot(d, s.t), dot(d, s.b), dot(d, s.n));
        if (dT.z <= 0) continue;
        // 1/pdf of the cosine-weighted ray; the directional coefficients clamp it near the horizon, where dot(d, B_i) / cos would spike
        float cosH = dot(d, nh);
        float3 c = L * (w0 / max(cosH, 0.05));
        e0 += c * Coef(dT, 0);
        e1 += c * Coef(dT, 1);
        e2 += c * Coef(dT, 2);
        es += L * (w0 * dT.z / max(cosH, 1e-6));
    }
    float occFraction = (float)occ / (float)GatherRays;
    ReduceOut[local * 4 + 0] = uint4(asuint(e0), asuint(occFraction));
    ReduceOut[local * 4 + 1] = uint4(asuint(e1), rays);
    ReduceOut[local * 4 + 2] = uint4(asuint(e2), asuint(minHit));
    ReduceOut[local * 4 + 3] = uint4(asuint(es), 0);
}
""";
}
