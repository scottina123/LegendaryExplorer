using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LegendaryExplorer.Tools.AssetDatabase.VFXPreview;

public sealed class VfxSimulation
{
    private const float MaximumTick = 1f / 15f;
    private const float MaximumPreviewCoordinate = 1_000_000f;
    private readonly List<VfxEmitterState> emitters = [];
    private VfxRandom random = new(0x4C455856u);

    public VfxPreviewDefinition Definition { get; private set; }
    public IReadOnlyList<VfxEmitterState> Emitters => emitters;
    public bool IsPlaying { get; private set; } = true;
    public bool Loop { get; set; } = true;
    public float Time { get; private set; }
    public int ParticleCount => emitters.Sum(emitter => emitter.Particles.Count);

    public void Load(VfxPreviewDefinition definition)
    {
        Definition = definition;
        Restart();
    }

    public void Play() => IsPlaying = true;

    public void Pause() => IsPlaying = false;

    public void Restart()
    {
        Time = 0;
        random = new VfxRandom(0x4C455856u);
        emitters.Clear();
        if (Definition is not null)
        {
            emitters.AddRange(Definition.Emitters.Select(definition => new VfxEmitterState(definition)));
        }
        IsPlaying = true;
    }

    public void Clear()
    {
        Definition = null;
        emitters.Clear();
        Time = 0;
        IsPlaying = false;
    }

    public void Tick(float elapsedSeconds)
    {
        if (!IsPlaying || Definition is null || elapsedSeconds <= 0)
        {
            return;
        }

        float remaining = Math.Min(elapsedSeconds, 0.25f);
        while (remaining > 0)
        {
            float timestep = Math.Min(remaining, MaximumTick);
            TickStep(timestep);
            remaining -= timestep;
        }
    }

    private void TickStep(float timestep)
    {
        Time += timestep;
        bool allFinished = true;
        foreach (VfxEmitterState emitter in emitters)
        {
            UpdateParticles(emitter, timestep);
            bool spawningFinished = SpawnParticles(emitter, timestep);
            allFinished &= spawningFinished && emitter.Particles.Count == 0;
        }

        if (allFinished)
        {
            if (Loop)
            {
                Restart();
            }
            else
            {
                IsPlaying = false;
            }
        }
    }

    private static void UpdateParticles(VfxEmitterState emitter, float timestep)
    {
        for (int index = emitter.Particles.Count - 1; index >= 0; index--)
        {
            VfxParticle particle = emitter.Particles[index];
            particle.Age += timestep;
            if (!particle.IsAlive)
            {
                emitter.Particles.RemoveAt(index);
                continue;
            }

            float relativeTime = particle.RelativeTime;
            Vector3 acceleration = particle.Acceleration
                + emitter.Definition.AccelerationOverLife.Evaluate(relativeTime, particle.Random);
            particle.BaseVelocity += acceleration * timestep;
            Vector3 velocityOverLife = emitter.Definition.VelocityOverLife.Evaluate(relativeTime, particle.Random);
            particle.Velocity = emitter.Definition.VelocityOverLifeIsAbsolute
                ? velocityOverLife
                : particle.BaseVelocity * velocityOverLife;
            particle.Position += particle.Velocity * timestep;
            particle.OrbitRotation += particle.OrbitRotationRate * timestep;
            particle.OrbitOffset = RotateOrbitOffset(particle.OrbitBaseOffset, particle.OrbitRotation);
            particle.Rotation += particle.RotationRate * timestep;
            particle.Size = particle.BaseSize * emitter.Definition.SizeOverLife.Evaluate(relativeTime, particle.Random);
            float colorScaleTime = emitter.Definition.ColorScaleUsesEmitterTime
                ? GetEmitterRelativeTime(emitter)
                : relativeTime;
            particle.Color = emitter.Definition.InitialColor.Evaluate(0, particle.Random)
                * emitter.Definition.ColorOverLife.Evaluate(relativeTime, particle.Random)
                * emitter.Definition.ColorScaleOverLife.Evaluate(colorScaleTime, particle.Random);
            particle.SubImageIndex = EvaluateSubImageIndex(emitter, particle);
            emitter.Particles[index] = particle;
        }
    }

    private bool SpawnParticles(VfxEmitterState emitter, float timestep)
    {
        VfxEmitterDefinition definition = emitter.Definition;
        float previousAge = emitter.Age;
        emitter.Age += timestep;
        if (emitter.Age <= definition.Delay)
        {
            return false;
        }

        float previousActiveAge = Math.Max(0, previousAge - definition.Delay);
        float activeAge = emitter.Age - definition.Delay;
        if (definition.Duration > 0 && definition.Loops > 0 && activeAge >= definition.Duration * definition.Loops)
        {
            return true;
        }

        int previousCycle = definition.Duration > 0 ? (int)(previousActiveAge / definition.Duration) : 0;
        int currentCycle = definition.Duration > 0 ? (int)(activeAge / definition.Duration) : 0;
        for (int cycle = previousCycle; cycle <= currentCycle; cycle++)
        {
            if (definition.Loops > 0 && cycle >= definition.Loops)
            {
                break;
            }

            float segmentStart = cycle == previousCycle ? previousActiveAge - (cycle * definition.Duration) : 0;
            float segmentEnd = cycle == currentCycle ? activeAge - (cycle * definition.Duration) : definition.Duration;
            if (definition.Duration <= 0)
            {
                segmentStart = previousActiveAge;
                segmentEnd = activeAge;
            }

            SpawnBursts(emitter, cycle, segmentStart, segmentEnd);
            float rate = Math.Max(0, definition.SpawnRate.Evaluate(segmentEnd, random.NextFloat()));
            emitter.SpawnRemainder += rate * Math.Max(0, segmentEnd - segmentStart);
            int spawnCount = (int)(emitter.SpawnRemainder + 0.00001f);
            emitter.SpawnRemainder -= spawnCount;
            Spawn(emitter, spawnCount, segmentEnd);
        }

        return false;
    }

    private void SpawnBursts(VfxEmitterState emitter, int cycle, float start, float end)
    {
        for (int index = 0; index < emitter.Definition.Bursts.Count; index++)
        {
            VfxBurst burst = emitter.Definition.Bursts[index];
            long key = ((long)cycle << 32) | (uint)index;
            if (burst.Time >= start && burst.Time <= end && emitter.FiredBursts.Add(key))
            {
                int count = burst.CountLow >= 0
                    ? random.NextInt(Math.Min(burst.CountLow, burst.Count), Math.Max(burst.CountLow, burst.Count) + 1)
                    : burst.Count;
                Spawn(emitter, count, burst.Time);
            }
        }
    }

    private void Spawn(VfxEmitterState emitter, int count, float emitterTime)
    {
        int available = Math.Max(0, emitter.Definition.MaxParticles - emitter.Particles.Count);
        count = Math.Min(count, available);
        for (int index = 0; index < count; index++)
        {
            float sample = random.NextFloat();
            float lifetime = Math.Max(0.001f, emitter.Definition.Lifetime.Evaluate(emitterTime, random.NextFloat()));
            Vector3 baseSize = emitter.Definition.InitialSize.Evaluate(emitterTime, random.NextFloat());
            Vector3 initialVelocity = emitter.Definition.InitialVelocity.Evaluate(emitterTime, random.NextFloat());
            var particle = new VfxParticle
            {
                Position = emitter.Definition.InitialLocation.Evaluate(emitterTime, random.NextFloat()),
                BaseVelocity = initialVelocity,
                Velocity = initialVelocity,
                BaseSize = baseSize,
                Size = baseSize * emitter.Definition.SizeOverLife.Evaluate(0, sample),
                Color = emitter.Definition.InitialColor.Evaluate(0, random.NextFloat()),
                Rotation = emitter.Definition.InitialRotation.Evaluate(emitterTime, random.NextFloat()),
                RotationRate = emitter.Definition.RotationRate.Evaluate(emitterTime, random.NextFloat()),
                Lifetime = lifetime,
                Random = sample
            };
            ApplySpawnInitializers(emitter.Definition, ref particle, emitterTime);
            particle.BaseVelocity = particle.Velocity;
            particle.SubImageIndex = EvaluateSubImageIndex(emitter, particle);
            emitter.Particles.Add(particle);
        }
    }

    private void ApplySpawnInitializers(VfxEmitterDefinition definition, ref VfxParticle particle, float emitterTime)
    {
        foreach (VfxSpawnInitializer initializer in definition.SpawnInitializers)
        {
            switch (initializer)
            {
                case VfxLocationSpawnInitializer location:
                    particle.Position += location.Location.Evaluate(emitterTime, random.NextFloat());
                    break;
                case VfxVelocitySpawnInitializer velocity:
                    particle.Velocity += velocity.Velocity.Evaluate(emitterTime, random.NextFloat());
                    break;
                case VfxCylinderSpawnInitializer cylinder:
                    ApplyCylinderInitializer(cylinder, ref particle, emitterTime);
                    break;
                case VfxAccelerationSpawnInitializer acceleration:
                    particle.Acceleration += acceleration.Acceleration.Evaluate(emitterTime, random.NextFloat());
                    break;
                case VfxOrbitSpawnInitializer orbit:
                    particle.OrbitBaseOffset = orbit.Offset.Evaluate(emitterTime, random.NextFloat());
                    particle.OrbitRotation = orbit.Rotation.Evaluate(emitterTime, random.NextFloat()) * MathF.Tau;
                    particle.OrbitRotationRate = orbit.RotationRate.Evaluate(emitterTime, random.NextFloat()) * MathF.Tau;
                    particle.OrbitOffset = RotateOrbitOffset(particle.OrbitBaseOffset, particle.OrbitRotation);
                    break;
            }
        }
    }

    private static Vector3 RotateOrbitOffset(Vector3 offset, Vector3 rotation)
    {
        Quaternion orientation = Quaternion.CreateFromYawPitchRoll(rotation.Z, rotation.Y, rotation.X);
        return Vector3.Transform(offset, orientation);
    }

    private void ApplyCylinderInitializer(VfxCylinderSpawnInitializer cylinder, ref VfxParticle particle, float emitterTime)
    {
        float radius = Math.Max(0, cylinder.StartRadius.Evaluate(emitterTime, random.NextFloat()));
        float height = Math.Max(0, cylinder.StartHeight.Evaluate(emitterTime, random.NextFloat()));
        Vector3 radialDirection = GetCylinderRadialDirection(cylinder);
        float radialDistance = cylinder.SurfaceOnly ? radius : MathF.Sqrt(random.NextFloat()) * radius;
        float heightCoordinate = GetSignedExtent(height * 0.5f, IsPositiveAllowed(cylinder, cylinder.HeightAxis), IsNegativeAllowed(cylinder, cylinder.HeightAxis));
        Vector3 cylinderOffset = radialDirection * radialDistance;
        cylinderOffset += GetAxis(cylinder.HeightAxis) * heightCoordinate;
        Vector3 startLocation = cylinder.StartLocation.Evaluate(emitterTime, random.NextFloat());
        particle.Position += startLocation + cylinderOffset;

        if (cylinder.Velocity)
        {
            Vector3 velocityDirection = cylinder.RadialVelocity
                ? cylinderOffset - (GetAxis(cylinder.HeightAxis) * heightCoordinate)
                : cylinderOffset;
            float velocityScale = cylinder.VelocityScale.Evaluate(emitterTime, random.NextFloat());
            particle.Velocity += velocityDirection * velocityScale;
        }
    }

    private Vector3 GetCylinderRadialDirection(VfxCylinderSpawnInitializer cylinder)
    {
        (VfxCylinderHeightAxis first, VfxCylinderHeightAxis second) = cylinder.HeightAxis switch
        {
            VfxCylinderHeightAxis.X => (VfxCylinderHeightAxis.Y, VfxCylinderHeightAxis.Z),
            VfxCylinderHeightAxis.Y => (VfxCylinderHeightAxis.X, VfxCylinderHeightAxis.Z),
            _ => (VfxCylinderHeightAxis.X, VfxCylinderHeightAxis.Y)
        };
        float firstCoordinate = GetSignedUnit(IsPositiveAllowed(cylinder, first), IsNegativeAllowed(cylinder, first));
        float secondCoordinate = GetSignedUnit(IsPositiveAllowed(cylinder, second), IsNegativeAllowed(cylinder, second));
        Vector3 direction = (GetAxis(first) * firstCoordinate) + (GetAxis(second) * secondCoordinate);
        return direction.LengthSquared() > 0.000001f ? Vector3.Normalize(direction) : GetAxis(first);
    }

    private float GetSignedUnit(bool positive, bool negative)
    {
        if (!positive && !negative)
        {
            return 0;
        }
        float magnitude = random.NextFloat();
        return positive && negative && random.NextFloat() < 0.5f ? -magnitude : negative && !positive ? -magnitude : magnitude;
    }

    private float GetSignedExtent(float extent, bool positive, bool negative) => GetSignedUnit(positive, negative) * extent;

    private static Vector3 GetAxis(VfxCylinderHeightAxis axis) => axis switch
    {
        VfxCylinderHeightAxis.X => Vector3.UnitX,
        VfxCylinderHeightAxis.Y => Vector3.UnitY,
        _ => Vector3.UnitZ
    };

    private static bool IsPositiveAllowed(VfxCylinderSpawnInitializer cylinder, VfxCylinderHeightAxis axis) => axis switch
    {
        VfxCylinderHeightAxis.X => cylinder.PositiveX,
        VfxCylinderHeightAxis.Y => cylinder.PositiveY,
        _ => cylinder.PositiveZ
    };

    private static bool IsNegativeAllowed(VfxCylinderSpawnInitializer cylinder, VfxCylinderHeightAxis axis) => axis switch
    {
        VfxCylinderHeightAxis.X => cylinder.NegativeX,
        VfxCylinderHeightAxis.Y => cylinder.NegativeY,
        _ => cylinder.NegativeZ
    };

    private static float GetEmitterRelativeTime(VfxEmitterState emitter)
    {
        float activeAge = Math.Max(0, emitter.Age - emitter.Definition.Delay);
        if (emitter.Definition.Duration <= 0)
        {
            return activeAge;
        }
        return (activeAge % emitter.Definition.Duration) / emitter.Definition.Duration;
    }

    private static float EvaluateSubImageIndex(VfxEmitterState emitter, in VfxParticle particle)
    {
        VfxEmitterDefinition definition = emitter.Definition;
        float frameRate = Math.Max(0, definition.SubUVFrameRate.Evaluate(
            definition.SubUVUseEmitterTime ? emitter.Age : particle.RelativeTime,
            particle.Random));
        if (frameRate > 0)
        {
            float time = definition.SubUVUseEmitterTime ? emitter.Age : particle.Age;
            return Math.Max(0, definition.SubUVStartingFrame - 1) + (time * frameRate);
        }
        return definition.SubImageIndex.Evaluate(particle.RelativeTime, particle.Random);
    }

    public bool TryGetBounds(out Vector3 minimum, out Vector3 maximum)
    {
        if (Definition?.FixedLocalBounds is { IsValid: true } fixedLocalBounds)
        {
            VfxBounds fixedWorldBounds = VfxBoundsMath.Transform(fixedLocalBounds, Definition.SystemTransform);
            minimum = fixedWorldBounds.Minimum;
            maximum = fixedWorldBounds.Maximum;
            return fixedWorldBounds.IsValid;
        }

        return TryGetDynamicBounds(out minimum, out maximum);
    }

    public bool TryGetDynamicBounds(out Vector3 minimum, out Vector3 maximum)
    {
        minimum = new Vector3(float.MaxValue);
        maximum = new Vector3(float.MinValue);
        bool found = false;
        foreach (VfxEmitterState emitter in emitters)
        {
            foreach (VfxParticle particle in emitter.Particles)
            {
                Vector3 size = Vector3.Abs(particle.Size);
                Vector3 extent = new(
                    size.X * Math.Max(0.0001f, emitter.Definition.SourceAspect.X) * 0.5f,
                    size.Y * Math.Max(0.0001f, emitter.Definition.SourceAspect.Y) * 0.5f,
                    Math.Max(size.X, size.Y) * 0.5f);
                Vector3 localRenderPosition = particle.Position + particle.OrbitOffset;
                Vector3 renderPosition = Definition is not null
                    ? Vector3.Transform(localRenderPosition, Definition.SystemTransform)
                    : localRenderPosition;
                if (!IsUsableBoundsValue(renderPosition) || !IsUsableBoundsValue(extent))
                {
                    continue;
                }
                minimum = Vector3.Min(minimum, renderPosition - extent);
                maximum = Vector3.Max(maximum, renderPosition + extent);
                found = true;
            }
        }
        return found;
    }

    private static bool IsUsableBoundsValue(Vector3 value) => VfxBoundsMath.IsFinite(value)
        && MathF.Abs(value.X) <= MaximumPreviewCoordinate
        && MathF.Abs(value.Y) <= MaximumPreviewCoordinate
        && MathF.Abs(value.Z) <= MaximumPreviewCoordinate;
}

public sealed class VfxEmitterState(VfxEmitterDefinition definition)
{
    public VfxEmitterDefinition Definition { get; } = definition;
    public List<VfxParticle> Particles { get; } = [];
    internal HashSet<long> FiredBursts { get; } = [];
    internal float Age { get; set; }
    internal float SpawnRemainder { get; set; }
}

internal struct VfxRandom(uint state)
{
    private uint state = state == 0 ? 1u : state;

    public uint Next()
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    public float NextFloat() => (Next() & 0x00FFFFFF) / 16777216f;

    public int NextInt(int minimum, int maximum)
        => maximum <= minimum ? minimum : minimum + (int)(Next() % (uint)(maximum - minimum));
}
