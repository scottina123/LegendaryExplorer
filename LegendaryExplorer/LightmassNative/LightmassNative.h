#pragma once

#include <cstddef>
#include <cstdint>

#if defined(_WIN32)
#define LMN_API extern "C" __declspec(dllexport)
#define LMN_CALL __cdecl
#else
#define LMN_API extern "C"
#define LMN_CALL
#endif

constexpr std::uint32_t LMN_ABI_VERSION = 5;

enum LmnStatus : std::int32_t
{
    LMN_STATUS_OK = 0,
    LMN_STATUS_INVALID_ARGUMENT = 1,
    LMN_STATUS_ABI_MISMATCH = 2,
    LMN_STATUS_OUT_OF_MEMORY = 3,
    LMN_STATUS_INTERNAL_ERROR = 4
};

enum LmnLightType : std::uint32_t
{
    LMN_LIGHT_POINT = 0,
    LMN_LIGHT_SPOT = 1,
    LMN_LIGHT_DIRECTIONAL = 2,
    LMN_LIGHT_SKY = 3
};

enum LmnMappingType : std::uint32_t
{
    LMN_MAPPING_VERTEX_1D = 1,
    LMN_MAPPING_TEXTURE_2D = 2
};

struct LmnVector2
{
    float x;
    float y;
};

struct LmnVector3
{
    float x;
    float y;
    float z;
};

struct LmnTriangle
{
    LmnVector3 a;
    LmnVector3 b;
    LmnVector3 c;
    std::int32_t source_id;
    std::int32_t source_triangle_index;
};

using LmnBuildProgressCallback = void (LMN_CALL*)(void* state, std::uint32_t current_index,
    std::uint32_t completed, std::uint32_t total);

struct LmnSceneDesc
{
    std::uint32_t struct_size;
    std::uint32_t abi_version;
    const LmnTriangle* triangles;
    std::uint32_t triangle_count;
    std::uint32_t leaf_triangle_count;
    LmnBuildProgressCallback progress_callback;
    void* progress_state;
};

struct LmnSceneDiagnostics
{
    std::uint32_t struct_size;
    std::uint32_t abi_version;
    std::uint32_t triangle_count;
    std::uint32_t bvh_node_count;
    double bvh_build_milliseconds;
};

struct LmnSurfaceSample
{
    LmnVector3 position;
    LmnVector3 normal;
    LmnVector3 tangent;
    LmnVector3 bitangent;
    LmnVector3 geometric_normal;
    std::int32_t source_id;
    std::int32_t source_triangle_index;
    float world_units_per_texel;
};

// Prepared light data is immutable for the duration of LmnBakeSamples. Sample data is stored in the
// separate light_samples array; directional lights use xyz and local lights use xy as a unit disk.
struct LmnPreparedLight
{
    std::uint32_t type;
    std::uint32_t casts_shadow;
    LmnVector3 position;
    LmnVector3 direction;
    LmnVector3 radiance;
    float radius_squared;
    float inverse_radius;
    float outer_cone_cos;
    float inverse_cone_range;
    float source_radius;
    std::uint32_t first_sample;
    std::uint32_t sample_count;
};

struct LmnAreaEmitter
{
    LmnVector3 position;
    LmnVector3 normal;
    LmnVector3 radiance;
    float area;
    float influence_radius;
    float falloff_exponent;
    std::uint32_t two_sided;
};

using LmnBakeProgressCallback = void (LMN_CALL*)(void* state, std::uint32_t completed,
    std::uint32_t total);

struct LmnBakeDesc
{
    std::uint32_t struct_size;
    std::uint32_t abi_version;
    const LmnSurfaceSample* samples;
    std::uint32_t sample_count;
    const LmnPreparedLight* lights;
    std::uint32_t light_count;
    const LmnVector3* light_samples;
    std::uint32_t light_sample_count;
    const LmnAreaEmitter* emitters;
    std::uint32_t emitter_count;
    LmnVector3 environment;
    float shadow_bias;
    float minimum_emissive_contribution;
    std::uint32_t coefficient_count;
    std::uint32_t compressed_directional;
    std::uint32_t worker_count;
    std::uint32_t mapping_type;
    LmnBakeProgressCallback progress_callback;
    void* progress_state;
};

struct LmnBakeDiagnostics
{
    std::uint32_t struct_size;
    std::uint32_t abi_version;
    std::uint64_t samples_processed;
    std::uint64_t occupied_texels;
    std::uint64_t relevant_lights;
    std::uint64_t rays_cast;
    std::uint64_t occluded_samples;
    std::uint64_t rejected_self_intersections;
    std::uint64_t visibility_sample_count;
    std::uint64_t visibility_micro_sum;
    std::uint64_t direct_contribution_micro_sum;
    std::uint64_t environment_contribution_micro_sum;
    std::uint64_t emissive_samples_evaluated;
    std::uint64_t emissive_rays_cast;
    std::uint64_t ray_triangle_tests;
    std::uint64_t bvh_nodes_visited;
    std::uint64_t any_hit_early_outs;
    double shadow_traversal_milliseconds;
    double bake_1d_milliseconds;
    double bake_2d_milliseconds;
    double total_compute_milliseconds;
    double samples_per_second;
    double rays_per_second;
};

// Scene extraction stays on the managed side because it owns UE3 packages and object proxies. Once
// those objects have been flattened into these POD arrays, the complete scalable mesh/light scan is
// performed by one native call. Raw meshes are unique; instances refer to them by index.
struct LmnMatrix4x4
{
    float m11, m12, m13, m14;
    float m21, m22, m23, m24;
    float m31, m32, m33, m34;
    float m41, m42, m43, m44;
};

struct LmnRawMeshVertex
{
    LmnVector3 position;
    LmnVector3 tangent_x;
    LmnVector3 tangent_z;
    LmnVector2 lightmap_uv;
    float handedness;
};

struct LmnMeshSection
{
    std::uint32_t first_index;
    std::uint32_t triangle_count;
};

struct LmnRawMeshDesc
{
    std::uint32_t first_vertex;
    std::uint32_t vertex_count;
    std::uint32_t first_index;
    std::uint32_t index_count;
    std::uint32_t first_section;
    std::uint32_t section_count;
    std::uint32_t declared_vertex_count;
    std::uint32_t position_vertex_count;
    std::uint32_t attribute_vertex_count;
    std::uint32_t texture_coordinate_count;
    std::int32_t selected_coordinate_index;
    std::uint32_t coordinate_channel_available;
};

struct LmnMeshInstanceDesc
{
    std::uint32_t mesh_index;
    LmnMatrix4x4 local_to_world;
    LmnMatrix4x4 normal_to_world;
    std::uint32_t lighting_channels;
};

struct LmnScanLight
{
    std::uint32_t type;
    LmnVector3 position;
    LmnVector3 direction;
    float radius;
    float outer_cone_angle_degrees;
    std::uint32_t lighting_channels;
};

enum LmnScanPhase : std::uint32_t
{
    LMN_SCAN_TOPOLOGY = 1,
    LMN_SCAN_INSTANCES = 2,
    LMN_SCAN_LIGHTS = 3
};

using LmnScanProgressCallback = void (LMN_CALL*)(void* state, std::uint32_t phase,
    std::uint32_t current_index, std::uint32_t completed, std::uint32_t total);

struct LmnSceneScanDesc
{
    std::uint32_t struct_size;
    std::uint32_t abi_version;
    const LmnRawMeshVertex* vertices;
    std::uint32_t vertex_count;
    const std::uint16_t* indices;
    std::uint32_t index_count;
    const LmnMeshSection* sections;
    std::uint32_t section_count;
    const LmnRawMeshDesc* meshes;
    std::uint32_t mesh_count;
    const LmnMeshInstanceDesc* instances;
    std::uint32_t instance_count;
    const LmnScanLight* lights;
    std::uint32_t light_count;
    std::uint32_t worker_count;
    LmnScanProgressCallback progress_callback;
    void* progress_state;
};

struct LmnScannedVertex
{
    LmnVector3 position;
    LmnVector3 normal;
    LmnVector3 tangent;
    LmnVector3 bitangent;
    LmnVector2 lightmap_uv;
};

struct LmnScannedTriangle
{
    std::uint32_t first;
    std::uint32_t second;
    std::uint32_t third;
    std::int32_t section_index;
    std::int32_t source_triangle_index;
};

struct LmnMeshScanResult
{
    std::uint32_t triangle_count;
    std::uint32_t invalid_section_range_count;
    std::uint32_t invalid_index_count;
    std::uint32_t invalid_uv_vertex_count;
    std::uint32_t degenerate_uv_triangle_count;
    std::uint32_t overlapping_uv_triangle_pair_count;
};

struct LmnInstanceScanResult
{
    std::uint32_t first_vertex;
    std::uint32_t vertex_count;
    std::uint32_t first_triangle;
    std::uint32_t triangle_count;
    std::uint32_t first_relevant_light;
    std::uint32_t relevant_light_count;
    LmnVector3 bounds_minimum;
    LmnVector3 bounds_maximum;
    float maximum_world_dimension;
    float surface_area;
};

struct LmnSceneScanView
{
    std::uint32_t struct_size;
    std::uint32_t abi_version;
    const LmnScannedVertex* vertices;
    std::uint32_t vertex_count;
    const LmnScannedTriangle* triangles;
    std::uint32_t triangle_count;
    const LmnMeshScanResult* meshes;
    std::uint32_t mesh_count;
    const LmnInstanceScanResult* instances;
    std::uint32_t instance_count;
    const std::uint32_t* relevant_light_indices;
    std::uint32_t relevant_light_index_count;
    double topology_scan_milliseconds;
    double instance_scan_milliseconds;
    double light_scan_milliseconds;
    double total_scan_milliseconds;
};

using LmnBakeContext = void*;
using LmnSceneScan = void*;

LMN_API std::uint32_t LMN_CALL LmnGetAbiVersion() noexcept;
LMN_API LmnStatus LMN_CALL LmnCreateBakeContext(const LmnSceneDesc* scene,
    LmnBakeContext* context, LmnSceneDiagnostics* diagnostics) noexcept;
LMN_API void LMN_CALL LmnDestroyBakeContext(LmnBakeContext context) noexcept;
LMN_API LmnStatus LMN_CALL LmnBakeSamples(LmnBakeContext context, const LmnBakeDesc* bake,
    LmnVector3* coefficients, std::size_t coefficient_capacity,
    LmnBakeDiagnostics* diagnostics) noexcept;
LMN_API LmnStatus LMN_CALL LmnScanScene(const LmnSceneScanDesc* scene,
    LmnSceneScan* scan) noexcept;
LMN_API LmnStatus LMN_CALL LmnGetSceneScanView(LmnSceneScan scan,
    LmnSceneScanView* view) noexcept;
LMN_API void LMN_CALL LmnDestroySceneScan(LmnSceneScan scan) noexcept;
LMN_API std::size_t LMN_CALL LmnGetLastError(char* destination, std::size_t destination_size) noexcept;
