#include "LightmassNative.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstring>
#include <exception>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <numbers>
#include <string>
#include <thread>
#include <unordered_set>
#include <utility>
#include <vector>

namespace
{
    using Clock = std::chrono::steady_clock;
    constexpr float Epsilon = 1.0e-6f;
    constexpr std::uint64_t MicroScale = 1'000'000;
    constexpr std::uint32_t BvhBuildBinCount = 12;
    thread_local std::string LastError;

    struct Bounds
    {
        LmnVector3 minimum{std::numeric_limits<float>::max(), std::numeric_limits<float>::max(),
            std::numeric_limits<float>::max()};
        LmnVector3 maximum{-std::numeric_limits<float>::max(), -std::numeric_limits<float>::max(),
            -std::numeric_limits<float>::max()};
    };

    struct Ray
    {
        LmnVector3 origin;
        LmnVector3 direction;
        LmnVector3 inverse_direction;
        std::uint32_t parallel_axis_mask = 0;
    };

    struct Triangle
    {
        LmnVector3 a;
        LmnVector3 edge1;
        LmnVector3 edge2;
        std::int32_t source_id;
        std::int32_t source_triangle_index;
    };

    struct BuildTriangleData
    {
        LmnVector3 centroid;
        Bounds bounds;
        std::uint32_t stable_index;
    };

    struct BvhNode
    {
        Bounds bounds;
        std::uint32_t left = 0;
        std::uint32_t right = 0;
        std::uint32_t start = 0;
        std::uint32_t count = 0;
        std::uint32_t split_axis = 0;
    };

    struct BakeContext
    {
        std::vector<Triangle> triangles;
        std::vector<BuildTriangleData> build_data;
        std::vector<std::uint32_t> triangle_indices;
        std::vector<BvhNode> nodes;
        std::uint32_t leaf_triangle_count = 8;
        double bvh_build_milliseconds = 0.0;
    };

    struct BvhBuildProgress
    {
        LmnBuildProgressCallback callback = nullptr;
        void* state = nullptr;
        std::uint32_t completed = 0;
        std::uint32_t total = 0;
        std::uint32_t stride = 1;
    };

    struct BvhBuildBin
    {
        Bounds bounds;
        std::uint32_t count = 0;
    };

    struct MeshTopology
    {
        std::vector<LmnScannedTriangle> triangles;
        LmnMeshScanResult result{};
    };

    struct InstanceWork
    {
        LmnInstanceScanResult result{};
        std::vector<LmnScannedTriangle> triangles;
        std::vector<std::uint32_t> relevant_lights;
    };

    struct SceneScanContext
    {
        std::vector<LmnScannedVertex> vertices;
        std::vector<LmnScannedTriangle> triangles;
        std::vector<LmnMeshScanResult> meshes;
        std::vector<LmnInstanceScanResult> instances;
        std::vector<std::uint32_t> relevant_light_indices;
        double topology_scan_milliseconds = 0.0;
        double instance_scan_milliseconds = 0.0;
        double light_scan_milliseconds = 0.0;
        double total_scan_milliseconds = 0.0;
    };

    struct alignas(64) LocalCounters
    {
        std::uint64_t samples_processed = 0;
        std::uint64_t rays_cast = 0;
        std::uint64_t occluded_samples = 0;
        std::uint64_t rejected_self_intersections = 0;
        std::uint64_t visibility_sample_count = 0;
        std::uint64_t visibility_micro_sum = 0;
        std::uint64_t direct_contribution_micro_sum = 0;
        std::uint64_t environment_contribution_micro_sum = 0;
        std::uint64_t emissive_samples_evaluated = 0;
        std::uint64_t emissive_rays_cast = 0;
        std::uint64_t ray_triangle_tests = 0;
        std::uint64_t bvh_nodes_visited = 0;
        std::uint64_t any_hit_early_outs = 0;
        std::uint64_t timed_shadow_rays = 0;
        std::chrono::nanoseconds shadow_time{};
    };

    [[nodiscard]] LmnVector3 add(const LmnVector3 a, const LmnVector3 b) noexcept
    {
        return {a.x + b.x, a.y + b.y, a.z + b.z};
    }

    [[nodiscard]] LmnVector3 subtract(const LmnVector3 a, const LmnVector3 b) noexcept
    {
        return {a.x - b.x, a.y - b.y, a.z - b.z};
    }

    [[nodiscard]] LmnVector3 multiply(const LmnVector3 a, const float scalar) noexcept
    {
        return {a.x * scalar, a.y * scalar, a.z * scalar};
    }

    [[nodiscard]] LmnVector3 multiply(const LmnVector3 a, const LmnVector3 b) noexcept
    {
        return {a.x * b.x, a.y * b.y, a.z * b.z};
    }

    [[nodiscard]] LmnVector3 divide(const LmnVector3 a, const float scalar) noexcept
    {
        return {a.x / scalar, a.y / scalar, a.z / scalar};
    }

    [[nodiscard]] float dot(const LmnVector3 a, const LmnVector3 b) noexcept
    {
        return a.x * b.x + a.y * b.y + a.z * b.z;
    }

    [[nodiscard]] LmnVector3 cross(const LmnVector3 a, const LmnVector3 b) noexcept
    {
        return {a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x};
    }

    [[nodiscard]] float length_squared(const LmnVector3 value) noexcept
    {
        return dot(value, value);
    }

    [[nodiscard]] LmnVector3 safe_normalize(const LmnVector3 value, const LmnVector3 fallback) noexcept
    {
        const float squared = length_squared(value);
        if (!std::isfinite(squared) || squared < Epsilon)
            return fallback;
        return divide(value, std::sqrt(squared));
    }

    [[nodiscard]] LmnVector3 maximum(const LmnVector3 value, const float floor) noexcept
    {
        return {std::max(floor, value.x), std::max(floor, value.y), std::max(floor, value.z)};
    }

    [[nodiscard]] float maximum_component(const LmnVector3 value) noexcept
    {
        return std::max(value.x, std::max(value.y, value.z));
    }

    [[nodiscard]] float component(const LmnVector3 value, const std::uint32_t axis) noexcept
    {
        return axis == 0 ? value.x : axis == 1 ? value.y : value.z;
    }

    void include(Bounds& bounds, const LmnVector3 point) noexcept
    {
        bounds.minimum.x = std::min(bounds.minimum.x, point.x);
        bounds.minimum.y = std::min(bounds.minimum.y, point.y);
        bounds.minimum.z = std::min(bounds.minimum.z, point.z);
        bounds.maximum.x = std::max(bounds.maximum.x, point.x);
        bounds.maximum.y = std::max(bounds.maximum.y, point.y);
        bounds.maximum.z = std::max(bounds.maximum.z, point.z);
    }

    void include(Bounds& bounds, const Bounds& other) noexcept
    {
        include(bounds, other.minimum);
        include(bounds, other.maximum);
    }

    [[nodiscard]] Ray create_ray(const LmnVector3 origin, const LmnVector3 direction) noexcept
    {
        Ray ray{origin, direction, {}};
        if (std::abs(direction.x) < Epsilon) ray.parallel_axis_mask |= 1u;
        else ray.inverse_direction.x = 1.0f / direction.x;
        if (std::abs(direction.y) < Epsilon) ray.parallel_axis_mask |= 2u;
        else ray.inverse_direction.y = 1.0f / direction.y;
        if (std::abs(direction.z) < Epsilon) ray.parallel_axis_mask |= 4u;
        else ray.inverse_direction.z = 1.0f / direction.z;
        return ray;
    }

    [[nodiscard]] bool intersect_ray_axis(const float origin, const float inverse_direction,
        const bool parallel, const float minimum, const float maximum, float& minimum_t,
        float& maximum_t) noexcept
    {
        if (parallel)
            return origin >= minimum && origin <= maximum;
        float first = (minimum - origin) * inverse_direction;
        float second = (maximum - origin) * inverse_direction;
        if (first > second)
            std::swap(first, second);
        minimum_t = std::max(minimum_t, first);
        maximum_t = std::min(maximum_t, second);
        return minimum_t <= maximum_t;
    }

    [[nodiscard]] bool intersects_ray(const Bounds& bounds, const Ray& ray,
        const float maximum_distance) noexcept
    {
        float minimum_t = 0.0f;
        float maximum_t = maximum_distance;
        return intersect_ray_axis(ray.origin.x, ray.inverse_direction.x,
                   (ray.parallel_axis_mask & 1u) != 0, bounds.minimum.x, bounds.maximum.x,
                   minimum_t, maximum_t) &&
               intersect_ray_axis(ray.origin.y, ray.inverse_direction.y,
                   (ray.parallel_axis_mask & 2u) != 0, bounds.minimum.y, bounds.maximum.y,
                   minimum_t, maximum_t) &&
               intersect_ray_axis(ray.origin.z, ray.inverse_direction.z,
                   (ray.parallel_axis_mask & 4u) != 0, bounds.minimum.z, bounds.maximum.z,
                   minimum_t, maximum_t) && maximum_t >= 0.0f;
    }

    [[nodiscard]] bool intersects_triangle(const LmnVector3 origin, const LmnVector3 direction,
        const Triangle& triangle, float& distance) noexcept
    {
        const LmnVector3 p = cross(direction, triangle.edge2);
        const float determinant = dot(triangle.edge1, p);
        if (std::abs(determinant) < Epsilon)
            return false;
        const float inverse = 1.0f / determinant;
        const LmnVector3 t = subtract(origin, triangle.a);
        const float u = dot(t, p) * inverse;
        if (u < 0.0f || u > 1.0f)
            return false;
        const LmnVector3 q = cross(t, triangle.edge1);
        const float v = dot(direction, q) * inverse;
        if (v < 0.0f || u + v > 1.0f)
            return false;
        distance = dot(triangle.edge2, q) * inverse;
        return distance >= 0.0f;
    }

    void report_bvh_leaf(BvhBuildProgress& progress, const std::uint32_t current_index,
        const std::uint32_t count) noexcept
    {
        const std::uint32_t previous = progress.completed;
        progress.completed += count;
        if (progress.callback != nullptr && (progress.completed == progress.total ||
            previous / progress.stride != progress.completed / progress.stride))
            progress.callback(progress.state, current_index, progress.completed, progress.total);
    }

    [[nodiscard]] float surface_area(const Bounds& bounds) noexcept
    {
        const LmnVector3 size = maximum(subtract(bounds.maximum, bounds.minimum), 0.0f);
        return 2.0f * (size.x * size.y + size.x * size.z + size.y * size.z);
    }

    [[nodiscard]] std::uint32_t get_build_bin(const BakeContext& context,
        const std::uint32_t triangle_index, const std::uint32_t axis, const float minimum,
        const float scale) noexcept
    {
        const int bin = static_cast<int>((component(context.build_data[triangle_index].centroid, axis) -
            minimum) * scale);
        return static_cast<std::uint32_t>(std::clamp(bin, 0,
            static_cast<int>(BvhBuildBinCount - 1)));
    }

    [[nodiscard]] bool partition_by_surface_area(BakeContext& context, const std::uint32_t start,
        const std::uint32_t count, const Bounds& centroid_bounds, std::uint32_t& split_axis,
        std::uint32_t& left_count)
    {
        bool found = false;
        std::uint32_t best_split_bin = 0;
        float best_cost = std::numeric_limits<float>::max();
        const LmnVector3 centroid_extent = subtract(centroid_bounds.maximum, centroid_bounds.minimum);

        for (std::uint32_t candidate_axis = 0; candidate_axis < 3; ++candidate_axis)
        {
            const float extent = component(centroid_extent, candidate_axis);
            if (!(extent > Epsilon) || !std::isfinite(extent))
                continue;
            std::array<BvhBuildBin, BvhBuildBinCount> bins{};
            const float minimum = component(centroid_bounds.minimum, candidate_axis);
            const float scale = static_cast<float>(BvhBuildBinCount) / extent;
            for (std::uint32_t offset = 0; offset < count; ++offset)
            {
                const std::uint32_t triangle_index = context.triangle_indices[start + offset];
                BvhBuildBin& bin = bins[get_build_bin(context, triangle_index, candidate_axis,
                    minimum, scale)];
                if (bin.count == 0)
                    bin.bounds = context.build_data[triangle_index].bounds;
                else
                    include(bin.bounds, context.build_data[triangle_index].bounds);
                ++bin.count;
            }

            std::array<Bounds, BvhBuildBinCount> prefix_bounds{};
            std::array<Bounds, BvhBuildBinCount> suffix_bounds{};
            std::array<std::uint32_t, BvhBuildBinCount> prefix_counts{};
            std::array<std::uint32_t, BvhBuildBinCount> suffix_counts{};
            Bounds running_bounds{};
            std::uint32_t running_count = 0;
            for (std::uint32_t bin_index = 0; bin_index < BvhBuildBinCount; ++bin_index)
            {
                if (bins[bin_index].count > 0)
                {
                    if (running_count == 0)
                        running_bounds = bins[bin_index].bounds;
                    else
                        include(running_bounds, bins[bin_index].bounds);
                }
                running_count += bins[bin_index].count;
                prefix_bounds[bin_index] = running_bounds;
                prefix_counts[bin_index] = running_count;
            }
            running_bounds = {};
            running_count = 0;
            for (std::uint32_t reverse = BvhBuildBinCount; reverse-- > 0;)
            {
                if (bins[reverse].count > 0)
                {
                    if (running_count == 0)
                        running_bounds = bins[reverse].bounds;
                    else
                        include(running_bounds, bins[reverse].bounds);
                }
                running_count += bins[reverse].count;
                suffix_bounds[reverse] = running_bounds;
                suffix_counts[reverse] = running_count;
            }

            for (std::uint32_t bin_index = 0; bin_index + 1 < BvhBuildBinCount; ++bin_index)
            {
                const std::uint32_t candidate_left = prefix_counts[bin_index];
                const std::uint32_t candidate_right = suffix_counts[bin_index + 1];
                if (candidate_left == 0 || candidate_right == 0)
                    continue;
                const float cost = surface_area(prefix_bounds[bin_index]) *
                        static_cast<float>(candidate_left) +
                    surface_area(suffix_bounds[bin_index + 1]) * static_cast<float>(candidate_right);
                if (cost < best_cost)
                {
                    best_cost = cost;
                    split_axis = candidate_axis;
                    best_split_bin = bin_index;
                    found = true;
                }
            }
        }
        if (!found)
            return false;

        const float split_minimum = component(centroid_bounds.minimum, split_axis);
        const float split_extent = component(subtract(centroid_bounds.maximum,
            centroid_bounds.minimum), split_axis);
        const float split_scale = static_cast<float>(BvhBuildBinCount) / split_extent;
        std::uint32_t lower = start;
        std::uint32_t upper = start + count - 1;
        while (lower <= upper)
        {
            while (lower <= upper && get_build_bin(context, context.triangle_indices[lower], split_axis,
                split_minimum, split_scale) <= best_split_bin)
                ++lower;
            while (lower <= upper && get_build_bin(context, context.triangle_indices[upper], split_axis,
                split_minimum, split_scale) > best_split_bin)
                --upper;
            if (lower > upper)
                break;
            std::swap(context.triangle_indices[lower++], context.triangle_indices[upper--]);
        }
        left_count = lower - start;
        return left_count > 0 && left_count < count;
    }

    std::uint32_t build_node(BakeContext& context, const std::uint32_t start,
        const std::uint32_t count, BvhBuildProgress& progress)
    {
        Bounds bounds;
        Bounds centroid_bounds;
        for (std::uint32_t offset = 0; offset < count; ++offset)
        {
            const BuildTriangleData& triangle = context.build_data[
                context.triangle_indices[start + offset]];
            include(bounds, triangle.bounds);
            include(centroid_bounds, triangle.centroid);
        }

        const std::uint32_t node_index = static_cast<std::uint32_t>(context.nodes.size());
        context.nodes.push_back({});
        context.nodes[node_index].bounds = bounds;
        if (count <= context.leaf_triangle_count)
        {
            context.nodes[node_index].start = start;
            context.nodes[node_index].count = count;
            report_bvh_leaf(progress, context.triangle_indices[start], count);
            return node_index;
        }

        const LmnVector3 extent = subtract(centroid_bounds.maximum, centroid_bounds.minimum);
        std::uint32_t axis = extent.y > extent.x ? 1u : 0u;
        if (extent.z > component(extent, axis))
            axis = 2;
        if (component(extent, axis) <= Epsilon)
        {
            context.nodes[node_index].start = start;
            context.nodes[node_index].count = count;
            report_bvh_leaf(progress, context.triangle_indices[start], count);
            return node_index;
        }

        std::uint32_t left_count = 0;
        if (!partition_by_surface_area(context, start, count, centroid_bounds, axis, left_count))
        {
            auto first = context.triangle_indices.begin() + start;
            auto last = first + count;
            left_count = count / 2;
            auto middle = first + left_count;
            std::nth_element(first, middle, last, [&](const std::uint32_t left,
                const std::uint32_t right)
            {
                const BuildTriangleData& a = context.build_data[left];
                const BuildTriangleData& b = context.build_data[right];
                const float ca = component(a.centroid, axis);
                const float cb = component(b.centroid, axis);
                return ca == cb ? a.stable_index < b.stable_index : ca < cb;
            });
        }

        const std::uint32_t left = build_node(context, start, left_count, progress);
        const std::uint32_t right = build_node(context, start + left_count, count - left_count, progress);
        context.nodes[node_index].left = left;
        context.nodes[node_index].right = right;
        context.nodes[node_index].split_axis = axis;
        return node_index;
    }

    [[nodiscard]] bool is_occluded(const BakeContext& context, const LmnVector3 origin,
        const LmnVector3 direction, const float maximum_distance, const std::int32_t receiver_source_id,
        const std::int32_t receiver_triangle_index, const float self_intersection_distance,
        LocalCounters& counters) noexcept
    {
        if (context.nodes.empty() || maximum_distance <= 0.0f)
            return false;

        const Ray ray = create_ray(origin, direction);
        std::array<std::uint32_t, 96> stack;
        std::uint32_t stack_count = 0;
        std::uint32_t node_index = 0;
        while (true)
        {
            const BvhNode& node = context.nodes[node_index];
            ++counters.bvh_nodes_visited;
            if (intersects_ray(node.bounds, ray, maximum_distance))
            {
                if (node.count > 0)
                {
                    for (std::uint32_t offset = 0; offset < node.count; ++offset)
                    {
                        const Triangle& triangle = context.triangles[
                            context.triangle_indices[node.start + offset]];
                        ++counters.ray_triangle_tests;
                        float distance = 0.0f;
                        if (!intersects_triangle(origin, direction, triangle, distance) ||
                            distance > maximum_distance)
                            continue;
                        if (receiver_source_id >= 0 && triangle.source_id == receiver_source_id &&
                            (triangle.source_triangle_index == receiver_triangle_index ||
                                distance <= self_intersection_distance))
                        {
                            ++counters.rejected_self_intersections;
                            continue;
                        }
                        ++counters.any_hit_early_outs;
                        return true;
                    }
                }
                else
                {
                    const bool reverse = component(direction, node.split_axis) < 0.0f;
                    const std::uint32_t first = reverse ? node.right : node.left;
                    const std::uint32_t second = reverse ? node.left : node.right;
                    if (stack_count < stack.size())
                    {
                        stack[stack_count++] = second;
                        node_index = first;
                        continue;
                    }
                    // A balanced median tree should never exhaust this stack. Conservatively keep
                    // traversing the near child if an adversarial input somehow does.
                    node_index = first;
                    continue;
                }
            }
            if (stack_count == 0)
                return false;
            node_index = stack[--stack_count];
        }
    }

    void create_basis(const LmnVector3 normal, LmnVector3& right, LmnVector3& up) noexcept
    {
        const LmnVector3 helper = std::abs(normal.z) < 0.999f
            ? LmnVector3{0.0f, 0.0f, 1.0f}
            : LmnVector3{0.0f, 1.0f, 0.0f};
        right = safe_normalize(cross(helper, normal), {1.0f, 0.0f, 0.0f});
        up = safe_normalize(cross(normal, right), {0.0f, 1.0f, 0.0f});
    }

    [[nodiscard]] bool evaluate_local_light(const LmnPreparedLight& light,
        const LmnVector3 sampled_position, const LmnSurfaceSample& sample, LmnVector3& surface_to_light,
        LmnVector3& unshadowed, LmnVector3& irradiance, float& distance) noexcept
    {
        const LmnVector3 delta = subtract(sampled_position, sample.position);
        const float distance_squared = length_squared(delta);
        if (distance_squared <= 0.0001f || distance_squared >= light.radius_squared)
            return false;
        distance = std::sqrt(distance_squared);
        surface_to_light = divide(delta, distance);
        const float normalized_distance = distance * light.inverse_radius;
        float attenuation = std::max(0.0f, 1.0f - normalized_distance * normalized_distance);
        attenuation *= attenuation;
        if (light.type == LMN_LIGHT_SPOT)
        {
            const float cone_dot = dot(multiply(surface_to_light, -1.0f), light.direction);
            if (cone_dot <= light.outer_cone_cos)
                return false;
            attenuation *= std::clamp((cone_dot - light.outer_cone_cos) * light.inverse_cone_range,
                0.0f, 1.0f);
        }
        const float normal_dot_light = std::max(0.0f, dot(sample.normal, surface_to_light));
        if (normal_dot_light <= 0.0f)
            return false;
        unshadowed = multiply(light.radiance, attenuation);
        irradiance = multiply(unshadowed, normal_dot_light);
        return true;
    }

    [[nodiscard]] bool evaluate_emitter(const LmnAreaEmitter& emitter, const LmnSurfaceSample& sample,
        const float minimum_contribution, LmnVector3& surface_to_emitter, LmnVector3& unshadowed,
        LmnVector3& irradiance, float& distance) noexcept
    {
        const LmnVector3 delta = subtract(emitter.position, sample.position);
        const float distance_squared = length_squared(delta);
        const float radius_squared = emitter.influence_radius * emitter.influence_radius;
        if (!std::isfinite(distance_squared) || distance_squared <= 0.0001f ||
            distance_squared >= radius_squared)
            return false;
        distance = std::sqrt(distance_squared);
        surface_to_emitter = divide(delta, distance);
        const float receiver_cosine = std::max(0.0f, dot(sample.normal, surface_to_emitter));
        float emitter_cosine = dot(emitter.normal, multiply(surface_to_emitter, -1.0f));
        emitter_cosine = emitter.two_sided != 0 ? std::abs(emitter_cosine) : std::max(0.0f, emitter_cosine);
        if (receiver_cosine <= 0.0f || emitter_cosine <= 0.0f)
            return false;
        const float falloff_base = std::max(0.0f, 1.0f - distance / emitter.influence_radius);
        const float falloff = emitter.falloff_exponent == 2.0f
            ? falloff_base * falloff_base
            : emitter.falloff_exponent == 1.0f
                ? falloff_base
                : std::pow(falloff_base, emitter.falloff_exponent);
        const float solid_angle = emitter.area /
            (std::numbers::pi_v<float> * distance_squared + emitter.area);
        unshadowed = multiply(emitter.radiance, emitter_cosine * solid_angle * falloff);
        irradiance = multiply(unshadowed, receiver_cosine);
        return maximum_component(irradiance) >= minimum_contribution;
    }

    constexpr std::array<LmnVector3, 3> DirectionalBasis{{
        {0.816496580927726f, 0.0f, 0.577350269189626f},
        {-0.408248290463863f, 0.707106781186548f, 0.577350269189626f},
        {-0.408248290463863f, -0.707106781186548f, 0.577350269189626f}}};

    void evaluate_sample(const BakeContext& context, const LmnBakeDesc& bake,
        const std::uint32_t sample_index, LmnVector3* coefficients, LocalCounters& counters) noexcept
    {
        const LmnSurfaceSample& sample = bake.samples[sample_index];
        const LmnVector3 environment = bake.environment;
        LmnVector3 simple = environment;
        LmnVector3 total_direct{};
        const float isotropic_maximum = maximum_component(environment);
        std::array<LmnVector3, 3> directional{};
        const float epsilon = std::max(bake.shadow_bias,
            std::max(0.01f, sample.world_units_per_texel * 0.02f));
        LmnVector3 geometric = safe_normalize(sample.geometric_normal, sample.normal);
        if (dot(geometric, sample.normal) < 0.0f)
            geometric = multiply(geometric, -1.0f);
        const LmnVector3 origin = add(sample.position, multiply(geometric, epsilon));

        for (std::uint32_t light_index = 0; light_index < bake.light_count; ++light_index)
        {
            const LmnPreparedLight& light = bake.lights[light_index];
            LmnVector3 sampled_irradiance{};
            std::array<LmnVector3, 3> sampled_directional{};
            LmnVector3 source_right{};
            LmnVector3 source_up{};
            if (light.type != LMN_LIGHT_DIRECTIONAL && light.sample_count > 1)
            {
                const LmnVector3 light_to_receiver = safe_normalize(
                    subtract(sample.position, light.position), light.direction);
                create_basis(light_to_receiver, source_right, source_up);
            }

            for (std::uint32_t relative_sample = 0; relative_sample < light.sample_count; ++relative_sample)
            {
                const LmnVector3 light_sample = bake.light_samples[light.first_sample + relative_sample];
                LmnVector3 surface_to_light{};
                LmnVector3 unshadowed{};
                LmnVector3 irradiance{};
                float light_distance = 0.0f;
                if (light.type == LMN_LIGHT_DIRECTIONAL)
                {
                    surface_to_light = light_sample;
                    const float normal_dot_light = std::max(0.0f, dot(sample.normal, surface_to_light));
                    if (normal_dot_light <= 0.0f)
                        continue;
                    unshadowed = light.radiance;
                    irradiance = multiply(unshadowed, normal_dot_light);
                    light_distance = 10'000'000.0f;
                }
                else
                {
                    LmnVector3 sampled_position = light.position;
                    if (light.sample_count > 1)
                    {
                        sampled_position = add(sampled_position, multiply(add(
                            multiply(source_right, light_sample.x), multiply(source_up, light_sample.y)),
                            light.source_radius));
                    }
                    if (!evaluate_local_light(light, sampled_position, sample, surface_to_light,
                            unshadowed, irradiance, light_distance))
                        continue;
                }

                if (light.casts_shadow != 0)
                {
                    const float maximum_distance = light.type == LMN_LIGHT_DIRECTIONAL
                        ? light_distance
                        : std::max(epsilon, light_distance - epsilon * 2.0f);
                    ++counters.rays_cast;
                    ++counters.visibility_sample_count;
                    const bool time_shadow = (counters.rays_cast & 4095u) == 1u;
                    const auto shadow_start = time_shadow ? Clock::now() : Clock::time_point{};
                    const bool occluded = is_occluded(context, origin, surface_to_light, maximum_distance,
                        sample.source_id, sample.source_triangle_index, epsilon * 4.0f, counters);
                    if (time_shadow)
                    {
                        counters.shadow_time += Clock::now() - shadow_start;
                        ++counters.timed_shadow_rays;
                    }
                    if (occluded)
                    {
                        ++counters.occluded_samples;
                        continue;
                    }
                    counters.visibility_micro_sum += MicroScale;
                }

                sampled_irradiance = add(sampled_irradiance, irradiance);
                const LmnVector3 tangent_direction = safe_normalize({
                    dot(surface_to_light, sample.tangent), dot(surface_to_light, sample.bitangent),
                    dot(surface_to_light, sample.normal)}, {0.0f, 0.0f, 1.0f});
                for (std::size_t basis_index = 0; basis_index < sampled_directional.size(); ++basis_index)
                {
                    sampled_directional[basis_index] = add(sampled_directional[basis_index],
                        multiply(unshadowed, std::max(0.0f,
                            dot(tangent_direction, DirectionalBasis[basis_index]))));
                }
            }

            const float inverse_sample_count = 1.0f / static_cast<float>(light.sample_count);
            const LmnVector3 direct = multiply(sampled_irradiance, inverse_sample_count);
            simple = add(simple, direct);
            total_direct = add(total_direct, direct);
            for (std::size_t basis_index = 0; basis_index < directional.size(); ++basis_index)
            {
                directional[basis_index] = add(directional[basis_index],
                    multiply(sampled_directional[basis_index], inverse_sample_count));
            }
        }

        for (std::uint32_t emitter_index = 0; emitter_index < bake.emitter_count; ++emitter_index)
        {
            ++counters.emissive_samples_evaluated;
            LmnVector3 surface_to_emitter{};
            LmnVector3 unshadowed{};
            LmnVector3 irradiance{};
            float emitter_distance = 0.0f;
            if (!evaluate_emitter(bake.emitters[emitter_index], sample,
                    bake.minimum_emissive_contribution, surface_to_emitter, unshadowed,
                    irradiance, emitter_distance))
                continue;
            ++counters.rays_cast;
            ++counters.emissive_rays_cast;
            ++counters.visibility_sample_count;
            const bool time_shadow = (counters.rays_cast & 4095u) == 1u;
            const auto shadow_start = time_shadow ? Clock::now() : Clock::time_point{};
            const bool occluded = is_occluded(context, origin, surface_to_emitter,
                std::max(epsilon, emitter_distance - epsilon * 2.0f), sample.source_id,
                sample.source_triangle_index, epsilon * 4.0f, counters);
            if (time_shadow)
            {
                counters.shadow_time += Clock::now() - shadow_start;
                ++counters.timed_shadow_rays;
            }
            if (occluded)
            {
                ++counters.occluded_samples;
                continue;
            }
            counters.visibility_micro_sum += MicroScale;
            simple = add(simple, irradiance);
            total_direct = add(total_direct, irradiance);
            const LmnVector3 tangent_direction = safe_normalize({
                dot(surface_to_emitter, sample.tangent), dot(surface_to_emitter, sample.bitangent),
                dot(surface_to_emitter, sample.normal)}, {0.0f, 0.0f, 1.0f});
            for (std::size_t basis_index = 0; basis_index < directional.size(); ++basis_index)
            {
                directional[basis_index] = add(directional[basis_index],
                    multiply(unshadowed, std::max(0.0f,
                        dot(tangent_direction, DirectionalBasis[basis_index]))));
            }
        }

        ++counters.samples_processed;
        counters.direct_contribution_micro_sum += static_cast<std::uint64_t>(std::llround(
            std::max(0.0f, maximum_component(total_direct)) * static_cast<float>(MicroScale)));
        counters.environment_contribution_micro_sum += static_cast<std::uint64_t>(std::llround(
            std::max(0.0f, maximum_component(environment)) * static_cast<float>(MicroScale)));

        LmnVector3* output = coefficients + static_cast<std::size_t>(sample_index) * bake.coefficient_count;
        if (bake.compressed_directional != 0)
        {
            const float maximum_color = maximum_component(simple);
            if (maximum_color > 0.000001f)
            {
                output[0] = divide(simple, maximum_color);
                const LmnVector3 directional_maximums{
                    maximum_component(directional[0]) + isotropic_maximum,
                    maximum_component(directional[1]) + isotropic_maximum,
                    maximum_component(directional[2]) + isotropic_maximum};
                const float flat_response = (directional_maximums.x + directional_maximums.y +
                    directional_maximums.z) / 3.0f;
                output[1] = flat_response > 0.000001f
                    ? multiply(directional_maximums, maximum_color / flat_response)
                    : LmnVector3{maximum_color, maximum_color, maximum_color};
            }
            else
            {
                output[0] = {};
                output[1] = {};
            }
            output[2] = maximum(simple, 0.0f);
            return;
        }

        for (std::uint32_t basis_index = 0; basis_index < 3; ++basis_index)
            output[basis_index] = maximum(directional[basis_index], 0.0f);
        output[bake.coefficient_count - 1] = maximum(simple, 0.0f);
    }

    [[nodiscard]] LmnVector3 transform_position(const LmnVector3 value,
        const LmnMatrix4x4& matrix) noexcept
    {
        return {
            value.x * matrix.m11 + value.y * matrix.m21 + value.z * matrix.m31 + matrix.m41,
            value.x * matrix.m12 + value.y * matrix.m22 + value.z * matrix.m32 + matrix.m42,
            value.x * matrix.m13 + value.y * matrix.m23 + value.z * matrix.m33 + matrix.m43
        };
    }

    [[nodiscard]] LmnVector3 transform_normal(const LmnVector3 value,
        const LmnMatrix4x4& matrix) noexcept
    {
        return {
            value.x * matrix.m11 + value.y * matrix.m21 + value.z * matrix.m31,
            value.x * matrix.m12 + value.y * matrix.m22 + value.z * matrix.m32,
            value.x * matrix.m13 + value.y * matrix.m23 + value.z * matrix.m33
        };
    }

    [[nodiscard]] float cross_2d(const LmnVector2 first, const LmnVector2 second) noexcept
    {
        return first.x * second.y - first.y * second.x;
    }

    [[nodiscard]] LmnVector2 subtract_2d(const LmnVector2 first, const LmnVector2 second) noexcept
    {
        return {first.x - second.x, first.y - second.y};
    }

    [[nodiscard]] float length_2d(const LmnVector2 value) noexcept
    {
        return std::sqrt(value.x * value.x + value.y * value.y);
    }

    [[nodiscard]] bool point_inside_uv_triangle_strict(const LmnVector2 point,
        const std::array<LmnVector2, 3>& triangle) noexcept
    {
        const float denominator = cross_2d(subtract_2d(triangle[1], triangle[0]),
            subtract_2d(triangle[2], triangle[0]));
        if (std::abs(denominator) < 1.0e-7f)
            return false;
        const float v = cross_2d(subtract_2d(point, triangle[0]),
            subtract_2d(triangle[2], triangle[0])) / denominator;
        const float w = cross_2d(subtract_2d(triangle[1], triangle[0]),
            subtract_2d(point, triangle[0])) / denominator;
        const float u = 1.0f - v - w;
        constexpr float tolerance = 1.0e-5f;
        return u > tolerance && v > tolerance && w > tolerance;
    }

    [[nodiscard]] bool uv_edges_intersect_properly(const LmnVector2 first_start,
        const LmnVector2 first_end, const LmnVector2 second_start,
        const LmnVector2 second_end) noexcept
    {
        const LmnVector2 first_direction = subtract_2d(first_end, first_start);
        const LmnVector2 second_direction = subtract_2d(second_end, second_start);
        const float denominator = cross_2d(first_direction, second_direction);
        if (std::abs(denominator) < 1.0e-7f)
            return false;
        const LmnVector2 delta = subtract_2d(second_start, first_start);
        const float first_t = cross_2d(delta, second_direction) / denominator;
        const float second_t = cross_2d(delta, first_direction) / denominator;
        if (first_t <= 0.0f || first_t >= 1.0f || second_t <= 0.0f || second_t >= 1.0f)
            return false;
        constexpr float boundary_distance_tolerance = 1.0f / (1024.0f * 1024.0f);
        const float first_distance = std::min(first_t, 1.0f - first_t) * length_2d(first_direction);
        const float second_distance = std::min(second_t, 1.0f - second_t) * length_2d(second_direction);
        return first_distance > boundary_distance_tolerance &&
            second_distance > boundary_distance_tolerance;
    }

    [[nodiscard]] bool uv_triangles_overlap(const LmnScannedTriangle& first,
        const LmnScannedTriangle& second, const LmnRawMeshVertex* vertices) noexcept
    {
        const std::array<LmnVector2, 3> left{{vertices[first.first].lightmap_uv,
            vertices[first.second].lightmap_uv, vertices[first.third].lightmap_uv}};
        const std::array<LmnVector2, 3> right{{vertices[second.first].lightmap_uv,
            vertices[second.second].lightmap_uv, vertices[second.third].lightmap_uv}};
        const LmnVector2 left_center{(left[0].x + left[1].x + left[2].x) / 3.0f,
            (left[0].y + left[1].y + left[2].y) / 3.0f};
        const LmnVector2 right_center{(right[0].x + right[1].x + right[2].x) / 3.0f,
            (right[0].y + right[1].y + right[2].y) / 3.0f};
        if (point_inside_uv_triangle_strict(left_center, right) ||
            point_inside_uv_triangle_strict(right_center, left))
            return true;
        for (std::size_t left_edge = 0; left_edge < 3; ++left_edge)
            for (std::size_t right_edge = 0; right_edge < 3; ++right_edge)
                if (uv_edges_intersect_properly(left[left_edge], left[(left_edge + 1) % 3],
                    right[right_edge], right[(right_edge + 1) % 3]))
                    return true;
        return false;
    }

    [[nodiscard]] bool uv_degenerate(const LmnScannedTriangle& triangle,
        const LmnRawMeshVertex* vertices) noexcept
    {
        return std::abs(cross_2d(subtract_2d(vertices[triangle.second].lightmap_uv,
            vertices[triangle.first].lightmap_uv), subtract_2d(vertices[triangle.third].lightmap_uv,
            vertices[triangle.first].lightmap_uv))) < 1.0e-7f;
    }

    [[nodiscard]] std::uint32_t count_uv_overlaps(const std::vector<LmnScannedTriangle>& triangles,
        const LmnRawMeshVertex* vertices)
    {
        constexpr int grid_size = 16;
        std::array<std::vector<std::uint32_t>, grid_size * grid_size> cells;
        std::unordered_set<std::uint64_t> tested_pairs;
        std::uint32_t overlaps = 0;
        for (std::uint32_t triangle_index = 0; triangle_index < triangles.size(); ++triangle_index)
        {
            const LmnScannedTriangle& triangle = triangles[triangle_index];
            if (uv_degenerate(triangle, vertices))
                continue;
            const LmnVector2 first = vertices[triangle.first].lightmap_uv;
            const LmnVector2 second = vertices[triangle.second].lightmap_uv;
            const LmnVector2 third = vertices[triangle.third].lightmap_uv;
            const float minimum_x = std::min(first.x, std::min(second.x, third.x));
            const float maximum_x = std::max(first.x, std::max(second.x, third.x));
            const float minimum_y = std::min(first.y, std::min(second.y, third.y));
            const float maximum_y = std::max(first.y, std::max(second.y, third.y));
            const int first_x = std::clamp(static_cast<int>(std::floor(minimum_x * grid_size)), 0, grid_size - 1);
            const int last_x = std::clamp(static_cast<int>(std::floor(maximum_x * grid_size)), 0, grid_size - 1);
            const int first_y = std::clamp(static_cast<int>(std::floor(minimum_y * grid_size)), 0, grid_size - 1);
            const int last_y = std::clamp(static_cast<int>(std::floor(maximum_y * grid_size)), 0, grid_size - 1);
            for (int y = first_y; y <= last_y; ++y)
                for (int x = first_x; x <= last_x; ++x)
                {
                    std::vector<std::uint32_t>& occupants = cells[y * grid_size + x];
                    for (const std::uint32_t other_index : occupants)
                    {
                        const std::uint64_t key = (static_cast<std::uint64_t>(other_index) << 32) |
                            triangle_index;
                        if (tested_pairs.insert(key).second &&
                            uv_triangles_overlap(triangles[other_index], triangle, vertices))
                            ++overlaps;
                    }
                    occupants.push_back(triangle_index);
                }
        }
        return overlaps;
    }

    void scan_mesh_topology(const LmnSceneScanDesc& scene, const std::uint32_t mesh_index,
        MeshTopology& output)
    {
        const LmnRawMeshDesc& mesh = scene.meshes[mesh_index];
        const LmnRawMeshVertex* vertices = scene.vertices + mesh.first_vertex;
        std::vector<std::uint8_t> referenced(mesh.vertex_count);
        for (std::uint32_t section_index = 0; section_index < mesh.section_count; ++section_index)
        {
            const LmnMeshSection& section = scene.sections[mesh.first_section + section_index];
            const std::uint64_t requested_end = static_cast<std::uint64_t>(section.first_index) +
                static_cast<std::uint64_t>(section.triangle_count) * 3u;
            if (section.first_index > mesh.index_count || requested_end > mesh.index_count)
                ++output.result.invalid_section_range_count;
            const std::uint32_t start = std::min(section.first_index, mesh.index_count);
            const std::uint32_t end = static_cast<std::uint32_t>(std::min<std::uint64_t>(
                mesh.index_count, requested_end));
            for (std::uint32_t offset = start; offset + 2 < end; offset += 3)
            {
                const std::uint32_t first = scene.indices[mesh.first_index + offset];
                const std::uint32_t second = scene.indices[mesh.first_index + offset + 1];
                const std::uint32_t third = scene.indices[mesh.first_index + offset + 2];
                if (first >= mesh.vertex_count || second >= mesh.vertex_count || third >= mesh.vertex_count)
                {
                    ++output.result.invalid_index_count;
                    continue;
                }
                referenced[first] = referenced[second] = referenced[third] = 1;
                const LmnVector3 edge1 = subtract(vertices[second].position, vertices[first].position);
                const LmnVector3 edge2 = subtract(vertices[third].position, vertices[first].position);
                if (length_squared(cross(edge1, edge2)) <= 1.0e-4f)
                    continue;
                output.triangles.push_back({first, second, third,
                    static_cast<std::int32_t>(section_index), static_cast<std::int32_t>(offset / 3)});
            }
        }
        output.result.triangle_count = static_cast<std::uint32_t>(output.triangles.size());
        if (mesh.coordinate_channel_available == 0)
            return;
        for (std::uint32_t index = 0; index < mesh.vertex_count; ++index)
        {
            if (referenced[index] == 0)
                continue;
            const LmnVector2 uv = vertices[index].lightmap_uv;
            if (!std::isfinite(uv.x) || !std::isfinite(uv.y) || uv.x < -1.0e-4f ||
                uv.x > 1.0001f || uv.y < -1.0e-4f || uv.y > 1.0001f)
                ++output.result.invalid_uv_vertex_count;
        }
        for (const LmnScannedTriangle& triangle : output.triangles)
            if (uv_degenerate(triangle, vertices))
                ++output.result.degenerate_uv_triangle_count;
        if (output.result.invalid_uv_vertex_count == 0)
            output.result.overlapping_uv_triangle_pair_count = count_uv_overlaps(output.triangles, vertices);
    }

    [[nodiscard]] bool light_can_affect(const LmnScanLight& light, const std::uint32_t channels,
        const Bounds& bounds) noexcept
    {
        const bool channels_overlap = (light.lighting_channels & 1u) == 0 || (channels & 1u) == 0 ||
            ((light.lighting_channels & channels & ~1u) != 0);
        if (!channels_overlap)
            return false;
        if (light.type == LMN_LIGHT_DIRECTIONAL || light.type == LMN_LIGHT_SKY)
            return true;
        const LmnVector3 closest{std::clamp(light.position.x, bounds.minimum.x, bounds.maximum.x),
            std::clamp(light.position.y, bounds.minimum.y, bounds.maximum.y),
            std::clamp(light.position.z, bounds.minimum.z, bounds.maximum.z)};
        if (length_squared(subtract(closest, light.position)) >= light.radius * light.radius)
            return false;
        if (light.type != LMN_LIGHT_SPOT)
            return true;
        const LmnVector3 center = multiply(add(bounds.minimum, bounds.maximum), 0.5f);
        const float bounds_radius = std::sqrt(length_squared(subtract(bounds.maximum, center)));
        const LmnVector3 light_to_center = subtract(center, light.position);
        const float center_distance = std::sqrt(length_squared(light_to_center));
        if (center_distance <= bounds_radius)
            return true;
        const float angular_radius = std::asin(std::clamp(bounds_radius / center_distance, 0.0f, 1.0f));
        const float outer_angle = light.outer_cone_angle_degrees * std::numbers::pi_v<float> / 180.0f;
        const float expanded_angle = std::min(std::numbers::pi_v<float>, outer_angle + angular_radius);
        return dot(divide(light_to_center, center_distance),
            safe_normalize(light.direction, {1.0f, 0.0f, 0.0f})) >= std::cos(expanded_angle);
    }

    [[nodiscard]] bool valid_range(const std::uint32_t first, const std::uint32_t count,
        const std::uint32_t total) noexcept
    {
        return first <= total && count <= total - first;
    }

    [[nodiscard]] bool valid_scan(const LmnSceneScanDesc* scene) noexcept
    {
        if (scene == nullptr || scene->struct_size != sizeof(LmnSceneScanDesc) ||
            scene->abi_version != LMN_ABI_VERSION ||
            (scene->vertex_count > 0 && scene->vertices == nullptr) ||
            (scene->index_count > 0 && scene->indices == nullptr) ||
            (scene->section_count > 0 && scene->sections == nullptr) ||
            (scene->mesh_count > 0 && scene->meshes == nullptr) ||
            (scene->instance_count > 0 && scene->instances == nullptr) ||
            (scene->light_count > 0 && scene->lights == nullptr))
            return false;
        for (std::uint32_t index = 0; index < scene->mesh_count; ++index)
        {
            const LmnRawMeshDesc& mesh = scene->meshes[index];
            if (!valid_range(mesh.first_vertex, mesh.vertex_count, scene->vertex_count) ||
                !valid_range(mesh.first_index, mesh.index_count, scene->index_count) ||
                !valid_range(mesh.first_section, mesh.section_count, scene->section_count))
                return false;
        }
        for (std::uint32_t index = 0; index < scene->instance_count; ++index)
            if (scene->instances[index].mesh_index >= scene->mesh_count)
                return false;
        return true;
    }

    void report_scan_progress(const LmnSceneScanDesc& scene, const std::uint32_t phase,
        const std::uint32_t current_index, const std::uint32_t completed,
        const std::uint32_t total) noexcept
    {
        if (scene.progress_callback != nullptr)
            scene.progress_callback(scene.progress_state, phase, current_index, completed, total);
    }

    [[nodiscard]] std::uint32_t progress_stride(const std::uint32_t total) noexcept
    {
        constexpr std::uint32_t maximum_progress_reports = 200;
        return std::max(1u, (total + maximum_progress_reports - 1) / maximum_progress_reports);
    }

    template <typename Function>
    void parallel_for_indices(const std::uint32_t count, const std::uint32_t requested_workers,
        Function&& function)
    {
        if (count == 0)
            return;
        const std::uint32_t workers = std::min(count, requested_workers == 0
            ? std::max(1u, std::thread::hardware_concurrency()) : requested_workers);
        std::atomic<std::uint32_t> next{0};
        std::atomic<bool> failed{false};
        std::exception_ptr failure;
        std::mutex failure_mutex;
        auto worker = [&]
        {
            try
            {
                while (!failed.load(std::memory_order_relaxed))
                {
                    const std::uint32_t index = next.fetch_add(1, std::memory_order_relaxed);
                    if (index >= count)
                        return;
                    function(index);
                }
            }
            catch (...)
            {
                std::scoped_lock lock(failure_mutex);
                if (failure == nullptr)
                    failure = std::current_exception();
                failed.store(true, std::memory_order_relaxed);
            }
        };
        std::vector<std::jthread> threads;
        threads.reserve(workers - 1);
        for (std::uint32_t index = 1; index < workers; ++index)
            threads.emplace_back(worker);
        worker();
        for (std::jthread& thread : threads)
            thread.join();
        if (failure != nullptr)
            std::rethrow_exception(failure);
    }

    void set_error(const char* message) noexcept
    {
        try
        {
            LastError = message == nullptr ? "Unknown native Lightmass error." : message;
        }
        catch (...)
        {
            LastError.clear();
        }
    }

    [[nodiscard]] bool valid_scene(const LmnSceneDesc* scene) noexcept
    {
        return scene != nullptr && scene->struct_size == sizeof(LmnSceneDesc) &&
            scene->abi_version == LMN_ABI_VERSION &&
            (scene->triangle_count == 0 || scene->triangles != nullptr);
    }

    [[nodiscard]] bool valid_bake(const LmnBakeDesc* bake, const std::size_t capacity) noexcept
    {
        if (bake == nullptr || bake->struct_size != sizeof(LmnBakeDesc) ||
            bake->abi_version != LMN_ABI_VERSION ||
            (bake->compressed_directional != 0 ? bake->coefficient_count != 3 :
                bake->coefficient_count != 4) ||
            (bake->sample_count > 0 && bake->samples == nullptr) ||
            (bake->light_count > 0 && bake->lights == nullptr) ||
            (bake->light_sample_count > 0 && bake->light_samples == nullptr) ||
            (bake->emitter_count > 0 && bake->emitters == nullptr))
            return false;
        const std::size_t required = static_cast<std::size_t>(bake->sample_count) *
            bake->coefficient_count;
        if (capacity < required)
            return false;
        for (std::uint32_t index = 0; index < bake->light_count; ++index)
        {
            const LmnPreparedLight& light = bake->lights[index];
            if (light.sample_count == 0 || light.first_sample > bake->light_sample_count ||
                light.sample_count > bake->light_sample_count - light.first_sample)
                return false;
        }
        return true;
    }
}

std::uint32_t LMN_CALL LmnGetAbiVersion() noexcept
{
    return LMN_ABI_VERSION;
}

LmnStatus LMN_CALL LmnCreateBakeContext(const LmnSceneDesc* scene, LmnBakeContext* context,
    LmnSceneDiagnostics* diagnostics) noexcept
{
    if (context == nullptr || diagnostics == nullptr)
        return LMN_STATUS_INVALID_ARGUMENT;
    *context = nullptr;
    if (scene != nullptr && scene->abi_version != LMN_ABI_VERSION)
        return LMN_STATUS_ABI_MISMATCH;
    if (!valid_scene(scene) || diagnostics->struct_size != sizeof(LmnSceneDiagnostics))
        return LMN_STATUS_INVALID_ARGUMENT;

    try
    {
        const auto start = Clock::now();
        auto created = std::make_unique<BakeContext>();
        created->leaf_triangle_count = std::clamp(scene->leaf_triangle_count, 2u, 32u);
        created->triangles.reserve(scene->triangle_count);
        created->build_data.reserve(scene->triangle_count);
        for (std::uint32_t index = 0; index < scene->triangle_count; ++index)
        {
            const LmnTriangle& source = scene->triangles[index];
            Triangle triangle{};
            triangle.a = source.a;
            triangle.edge1 = subtract(source.b, source.a);
            triangle.edge2 = subtract(source.c, source.a);
            triangle.source_id = source.source_id;
            triangle.source_triangle_index = source.source_triangle_index;
            created->triangles.push_back(triangle);
            BuildTriangleData build{};
            build.centroid = divide(add(add(source.a, source.b), source.c), 3.0f);
            include(build.bounds, source.a);
            include(build.bounds, source.b);
            include(build.bounds, source.c);
            build.stable_index = index;
            created->build_data.push_back(build);
        }
        created->triangle_indices.resize(created->triangles.size());
        for (std::size_t index = 0; index < created->triangle_indices.size(); ++index)
            created->triangle_indices[index] = static_cast<std::uint32_t>(index);
        BvhBuildProgress progress{};
        progress.callback = scene->progress_callback;
        progress.state = scene->progress_state;
        progress.total = static_cast<std::uint32_t>(created->triangles.size());
        progress.stride = std::max(1u, (progress.total + 199u) / 200u);
        if (progress.callback != nullptr)
            progress.callback(progress.state, 0, 0, progress.total);
        if (!created->triangles.empty())
            build_node(*created, 0, static_cast<std::uint32_t>(created->triangles.size()), progress);
        // Centroids and primitive bounds are construction-only. Release them so traversal touches
        // a compact 44-byte triangle instead of pulling nearly twice as much cache-cold data.
        std::vector<BuildTriangleData>().swap(created->build_data);
        created->bvh_build_milliseconds = std::chrono::duration<double, std::milli>(
            Clock::now() - start).count();
        diagnostics->abi_version = LMN_ABI_VERSION;
        diagnostics->triangle_count = static_cast<std::uint32_t>(created->triangles.size());
        diagnostics->bvh_node_count = static_cast<std::uint32_t>(created->nodes.size());
        diagnostics->bvh_build_milliseconds = created->bvh_build_milliseconds;
        *context = created.release();
        LastError.clear();
        return LMN_STATUS_OK;
    }
    catch (const std::bad_alloc&)
    {
        set_error("Native Lightmass could not allocate the bake context.");
        return LMN_STATUS_OUT_OF_MEMORY;
    }
    catch (...)
    {
        set_error("Native Lightmass failed while constructing the bake context.");
        return LMN_STATUS_INTERNAL_ERROR;
    }
}

void LMN_CALL LmnDestroyBakeContext(const LmnBakeContext context) noexcept
{
    delete static_cast<BakeContext*>(context);
}

LmnStatus LMN_CALL LmnBakeSamples(const LmnBakeContext context_handle, const LmnBakeDesc* bake,
    LmnVector3* coefficients, const std::size_t coefficient_capacity,
    LmnBakeDiagnostics* diagnostics) noexcept
{
    if (context_handle == nullptr || diagnostics == nullptr ||
        diagnostics->struct_size != sizeof(LmnBakeDiagnostics) ||
        (coefficient_capacity > 0 && coefficients == nullptr))
        return LMN_STATUS_INVALID_ARGUMENT;
    if (bake != nullptr && bake->abi_version != LMN_ABI_VERSION)
        return LMN_STATUS_ABI_MISMATCH;
    if (!valid_bake(bake, coefficient_capacity))
        return LMN_STATUS_INVALID_ARGUMENT;

    try
    {
        const auto start = Clock::now();
        const auto& context = *static_cast<const BakeContext*>(context_handle);
        const std::uint32_t requested_workers = bake->worker_count == 0
            ? std::max(1u, std::thread::hardware_concurrency())
            : bake->worker_count;
        const std::uint32_t worker_count = bake->sample_count == 0 ? 1u :
            std::min(requested_workers, bake->sample_count);
        std::vector<LocalCounters> locals(worker_count);
        std::atomic<std::uint32_t> next_sample{0};
        std::atomic<std::uint32_t> completed_samples{0};
        std::mutex progress_mutex;
        std::uint32_t last_reported = 0;
        const std::uint32_t bake_progress_stride = progress_stride(bake->sample_count);
        if (bake->progress_callback != nullptr)
            bake->progress_callback(bake->progress_state, 0, bake->sample_count);
        constexpr std::uint32_t ChunkSize = 64;
        auto worker = [&](const std::uint32_t worker_index)
        {
            LocalCounters& local = locals[worker_index];
            while (true)
            {
                const std::uint32_t first = next_sample.fetch_add(ChunkSize, std::memory_order_relaxed);
                if (first >= bake->sample_count)
                    return;
                const std::uint32_t last = std::min(bake->sample_count, first + ChunkSize);
                for (std::uint32_t sample_index = first; sample_index < last; ++sample_index)
                    evaluate_sample(context, *bake, sample_index, coefficients, local);
                const std::uint32_t count = last - first;
                const std::uint32_t done = completed_samples.fetch_add(count,
                    std::memory_order_relaxed) + count;
                const std::uint32_t previous = done - count;
                if (bake->progress_callback != nullptr && (done == bake->sample_count ||
                    previous / bake_progress_stride != done / bake_progress_stride))
                {
                    std::scoped_lock lock(progress_mutex);
                    if (done > last_reported)
                    {
                        last_reported = done;
                        bake->progress_callback(bake->progress_state, done, bake->sample_count);
                    }
                }
            }
        };

        std::vector<std::jthread> threads;
        threads.reserve(worker_count > 0 ? worker_count - 1 : 0);
        for (std::uint32_t index = 1; index < worker_count; ++index)
            threads.emplace_back(worker, index);
        worker(0);
        for (std::jthread& thread : threads)
            thread.join();

        LmnBakeDiagnostics merged{};
        merged.struct_size = sizeof(LmnBakeDiagnostics);
        merged.abi_version = LMN_ABI_VERSION;
        merged.occupied_texels = bake->mapping_type == LMN_MAPPING_TEXTURE_2D ? bake->sample_count : 0;
        merged.relevant_lights = bake->light_count;
        std::uint64_t timed_shadow_rays = 0;
        std::chrono::nanoseconds timed_shadow_duration{};
        for (const LocalCounters& local : locals)
        {
            merged.samples_processed += local.samples_processed;
            merged.rays_cast += local.rays_cast;
            merged.occluded_samples += local.occluded_samples;
            merged.rejected_self_intersections += local.rejected_self_intersections;
            merged.visibility_sample_count += local.visibility_sample_count;
            merged.visibility_micro_sum += local.visibility_micro_sum;
            merged.direct_contribution_micro_sum += local.direct_contribution_micro_sum;
            merged.environment_contribution_micro_sum += local.environment_contribution_micro_sum;
            merged.emissive_samples_evaluated += local.emissive_samples_evaluated;
            merged.emissive_rays_cast += local.emissive_rays_cast;
            merged.ray_triangle_tests += local.ray_triangle_tests;
            merged.bvh_nodes_visited += local.bvh_nodes_visited;
            merged.any_hit_early_outs += local.any_hit_early_outs;
            timed_shadow_rays += local.timed_shadow_rays;
            timed_shadow_duration += local.shadow_time;
        }
        merged.total_compute_milliseconds = std::chrono::duration<double, std::milli>(
            Clock::now() - start).count();
        if (bake->mapping_type == LMN_MAPPING_VERTEX_1D)
            merged.bake_1d_milliseconds = merged.total_compute_milliseconds;
        else if (bake->mapping_type == LMN_MAPPING_TEXTURE_2D)
            merged.bake_2d_milliseconds = merged.total_compute_milliseconds;
        const double seconds = merged.total_compute_milliseconds / 1000.0;
        if (seconds > 0.0)
        {
            merged.samples_per_second = static_cast<double>(merged.samples_processed) / seconds;
            merged.rays_per_second = static_cast<double>(merged.rays_cast) / seconds;
        }
        if (timed_shadow_rays > 0)
            merged.shadow_traversal_milliseconds =
                std::chrono::duration<double, std::milli>(timed_shadow_duration).count() *
                static_cast<double>(merged.rays_cast) / static_cast<double>(timed_shadow_rays);
        *diagnostics = merged;
        LastError.clear();
        return LMN_STATUS_OK;
    }
    catch (const std::bad_alloc&)
    {
        set_error("Native Lightmass could not allocate receiver work buffers.");
        return LMN_STATUS_OUT_OF_MEMORY;
    }
    catch (...)
    {
        set_error("Native Lightmass failed while baking a receiver.");
        return LMN_STATUS_INTERNAL_ERROR;
    }
}

LmnStatus LMN_CALL LmnScanScene(const LmnSceneScanDesc* scene, LmnSceneScan* scan) noexcept
{
    if (scan == nullptr)
        return LMN_STATUS_INVALID_ARGUMENT;
    *scan = nullptr;
    if (scene != nullptr && scene->abi_version != LMN_ABI_VERSION)
        return LMN_STATUS_ABI_MISMATCH;
    if (!valid_scan(scene))
        return LMN_STATUS_INVALID_ARGUMENT;

    try
    {
        const auto total_start = Clock::now();
        auto context = std::make_unique<SceneScanContext>();
        std::vector<MeshTopology> topologies(scene->mesh_count);

        const auto topology_start = Clock::now();
        report_scan_progress(*scene, LMN_SCAN_TOPOLOGY, 0, 0, scene->mesh_count);
        std::atomic<std::uint32_t> topology_completed{0};
        const std::uint32_t topology_stride = progress_stride(scene->mesh_count);
        parallel_for_indices(scene->mesh_count, scene->worker_count, [&](const std::uint32_t index)
        {
            scan_mesh_topology(*scene, index, topologies[index]);
            const std::uint32_t completed = topology_completed.fetch_add(1, std::memory_order_relaxed) + 1;
            if (completed == scene->mesh_count || completed % topology_stride == 0)
                report_scan_progress(*scene, LMN_SCAN_TOPOLOGY, index, completed, scene->mesh_count);
        });
        context->topology_scan_milliseconds = std::chrono::duration<double, std::milli>(
            Clock::now() - topology_start).count();
        context->meshes.reserve(topologies.size());
        for (const MeshTopology& topology : topologies)
            context->meshes.push_back(topology.result);

        std::vector<InstanceWork> instance_work(scene->instance_count);
        std::uint64_t total_vertices = 0;
        for (std::uint32_t index = 0; index < scene->instance_count; ++index)
            total_vertices += scene->meshes[scene->instances[index].mesh_index].vertex_count;
        if (total_vertices > std::numeric_limits<std::uint32_t>::max())
        {
            set_error("Native Lightmass scene scan exceeds the 32-bit vertex ABI limit.");
            return LMN_STATUS_INVALID_ARGUMENT;
        }
        context->vertices.resize(static_cast<std::size_t>(total_vertices));
        std::uint32_t vertex_offset = 0;
        for (std::uint32_t index = 0; index < scene->instance_count; ++index)
        {
            const LmnRawMeshDesc& mesh = scene->meshes[scene->instances[index].mesh_index];
            instance_work[index].result.first_vertex = vertex_offset;
            instance_work[index].result.vertex_count = mesh.vertex_count;
            vertex_offset += mesh.vertex_count;
        }

        const auto instance_start = Clock::now();
        report_scan_progress(*scene, LMN_SCAN_INSTANCES, 0, 0, scene->instance_count);
        std::atomic<std::uint32_t> instances_completed{0};
        const std::uint32_t instance_stride = progress_stride(scene->instance_count);
        parallel_for_indices(scene->instance_count, scene->worker_count, [&](const std::uint32_t instance_index)
        {
            const LmnMeshInstanceDesc& instance = scene->instances[instance_index];
            const LmnRawMeshDesc& mesh = scene->meshes[instance.mesh_index];
            const MeshTopology& topology = topologies[instance.mesh_index];
            InstanceWork& work = instance_work[instance_index];
            Bounds bounds{};
            for (std::uint32_t vertex_index = 0; vertex_index < mesh.vertex_count; ++vertex_index)
            {
                const LmnRawMeshVertex& source = scene->vertices[mesh.first_vertex + vertex_index];
                LmnScannedVertex& destination = context->vertices[work.result.first_vertex + vertex_index];
                destination.position = transform_position(source.position, instance.local_to_world);
                destination.normal = safe_normalize(transform_normal(source.tangent_z,
                    instance.normal_to_world), {0.0f, 0.0f, 1.0f});
                destination.tangent = safe_normalize(transform_normal(source.tangent_x,
                    instance.normal_to_world), {1.0f, 0.0f, 0.0f});
                destination.bitangent = safe_normalize(multiply(cross(destination.normal,
                    destination.tangent), source.handedness), {0.0f, 1.0f, 0.0f});
                destination.lightmap_uv = source.lightmap_uv;
                include(bounds, destination.position);
            }
            if (mesh.vertex_count == 0)
                bounds.minimum = bounds.maximum = {0.0f, 0.0f, 0.0f};
            work.result.bounds_minimum = bounds.minimum;
            work.result.bounds_maximum = bounds.maximum;
            const LmnVector3 dimensions = subtract(bounds.maximum, bounds.minimum);
            work.result.maximum_world_dimension = maximum_component(dimensions);
            work.triangles.reserve(topology.triangles.size());
            double surface_area = 0.0;
            for (const LmnScannedTriangle& triangle : topology.triangles)
            {
                const LmnVector3 first = context->vertices[work.result.first_vertex + triangle.first].position;
                const LmnVector3 second = context->vertices[work.result.first_vertex + triangle.second].position;
                const LmnVector3 third = context->vertices[work.result.first_vertex + triangle.third].position;
                const float double_area = std::sqrt(length_squared(cross(subtract(second, first),
                    subtract(third, first))));
                if (double_area * double_area <= 1.0e-4f)
                    continue;
                surface_area += static_cast<double>(double_area) * 0.5;
                work.triangles.push_back(triangle);
            }
            work.result.triangle_count = static_cast<std::uint32_t>(work.triangles.size());
            work.result.surface_area = static_cast<float>(surface_area);
            const std::uint32_t completed = instances_completed.fetch_add(1, std::memory_order_relaxed) + 1;
            if (completed == scene->instance_count || completed % instance_stride == 0)
                report_scan_progress(*scene, LMN_SCAN_INSTANCES, instance_index, completed,
                    scene->instance_count);
        });
        context->instance_scan_milliseconds = std::chrono::duration<double, std::milli>(
            Clock::now() - instance_start).count();

        const auto light_start = Clock::now();
        report_scan_progress(*scene, LMN_SCAN_LIGHTS, 0, 0, scene->instance_count);
        std::atomic<std::uint32_t> lights_completed{0};
        const std::uint32_t light_stride = progress_stride(scene->instance_count);
        parallel_for_indices(scene->instance_count, scene->worker_count, [&](const std::uint32_t instance_index)
        {
            const LmnMeshInstanceDesc& instance = scene->instances[instance_index];
            InstanceWork& work = instance_work[instance_index];
            Bounds bounds{work.result.bounds_minimum, work.result.bounds_maximum};
            for (std::uint32_t light_index = 0; light_index < scene->light_count; ++light_index)
                if (light_can_affect(scene->lights[light_index], instance.lighting_channels, bounds))
                    work.relevant_lights.push_back(light_index);
            work.result.relevant_light_count = static_cast<std::uint32_t>(work.relevant_lights.size());
            const std::uint32_t completed = lights_completed.fetch_add(1, std::memory_order_relaxed) + 1;
            if (completed == scene->instance_count || completed % light_stride == 0)
                report_scan_progress(*scene, LMN_SCAN_LIGHTS, instance_index, completed,
                    scene->instance_count);
        });
        context->light_scan_milliseconds = std::chrono::duration<double, std::milli>(
            Clock::now() - light_start).count();

        std::size_t triangle_count = 0;
        std::size_t relevant_light_count = 0;
        for (const InstanceWork& work : instance_work)
        {
            triangle_count += work.triangles.size();
            relevant_light_count += work.relevant_lights.size();
        }
        context->triangles.reserve(triangle_count);
        context->relevant_light_indices.reserve(relevant_light_count);
        context->instances.reserve(instance_work.size());
        for (InstanceWork& work : instance_work)
        {
            work.result.first_triangle = static_cast<std::uint32_t>(context->triangles.size());
            context->triangles.insert(context->triangles.end(), work.triangles.begin(), work.triangles.end());
            work.result.first_relevant_light = static_cast<std::uint32_t>(context->relevant_light_indices.size());
            context->relevant_light_indices.insert(context->relevant_light_indices.end(),
                work.relevant_lights.begin(), work.relevant_lights.end());
            context->instances.push_back(work.result);
        }
        context->total_scan_milliseconds = std::chrono::duration<double, std::milli>(
            Clock::now() - total_start).count();
        *scan = context.release();
        LastError.clear();
        return LMN_STATUS_OK;
    }
    catch (const std::bad_alloc&)
    {
        set_error("Native Lightmass could not allocate scene-scan work buffers.");
        return LMN_STATUS_OUT_OF_MEMORY;
    }
    catch (...)
    {
        set_error("Native Lightmass failed while scanning meshes and lights.");
        return LMN_STATUS_INTERNAL_ERROR;
    }
}

LmnStatus LMN_CALL LmnGetSceneScanView(const LmnSceneScan scan,
    LmnSceneScanView* view) noexcept
{
    if (scan == nullptr || view == nullptr || view->struct_size != sizeof(LmnSceneScanView))
        return LMN_STATUS_INVALID_ARGUMENT;
    const auto& context = *static_cast<const SceneScanContext*>(scan);
    view->abi_version = LMN_ABI_VERSION;
    view->vertices = context.vertices.data();
    view->vertex_count = static_cast<std::uint32_t>(context.vertices.size());
    view->triangles = context.triangles.data();
    view->triangle_count = static_cast<std::uint32_t>(context.triangles.size());
    view->meshes = context.meshes.data();
    view->mesh_count = static_cast<std::uint32_t>(context.meshes.size());
    view->instances = context.instances.data();
    view->instance_count = static_cast<std::uint32_t>(context.instances.size());
    view->relevant_light_indices = context.relevant_light_indices.data();
    view->relevant_light_index_count = static_cast<std::uint32_t>(context.relevant_light_indices.size());
    view->topology_scan_milliseconds = context.topology_scan_milliseconds;
    view->instance_scan_milliseconds = context.instance_scan_milliseconds;
    view->light_scan_milliseconds = context.light_scan_milliseconds;
    view->total_scan_milliseconds = context.total_scan_milliseconds;
    return LMN_STATUS_OK;
}

void LMN_CALL LmnDestroySceneScan(const LmnSceneScan scan) noexcept
{
    delete static_cast<SceneScanContext*>(scan);
}

std::size_t LMN_CALL LmnGetLastError(char* destination, const std::size_t destination_size) noexcept
{
    const std::size_t required = LastError.size() + 1;
    if (destination != nullptr && destination_size > 0)
    {
        const std::size_t copied = std::min(LastError.size(), destination_size - 1);
        std::memcpy(destination, LastError.data(), copied);
        destination[copied] = '\0';
    }
    return required;
}
