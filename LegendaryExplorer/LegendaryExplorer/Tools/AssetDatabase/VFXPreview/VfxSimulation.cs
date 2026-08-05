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
    public float SystemDelay { get; private set; }
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
            foreach (VfxEmitterState emitter in emitters)
            {
                InitializeEmitterTiming(emitter);
            }
            // ParticleSystem.Delay (optionally ranged with DelayLow) offsets the whole system.
            SystemDelay = Definition.UseSystemDelayRange
                ? Lerp(Definition.SystemDelayLow, Definition.SystemDelay, random.NextFloat())
                : Definition.SystemDelay;
        }
        IsPlaying = true;
        if (Definition is { WarmupTime: > 0 })
        {
            AdvanceWarmup(Definition.WarmupTime);
        }
    }

    /// <summary>
    /// ParticleSystem.WarmupTime fast-forwards the simulation so the preview opens in a settled state.
    /// </summary>
    private void AdvanceWarmup(float warmupTime)
    {
        float remaining = Math.Min(warmupTime, 10f);
        while (remaining > 0)
        {
            float timestep = Math.Min(remaining, MaximumTick);
            TickStep(timestep);
            remaining -= timestep;
        }
    }

    /// <summary>
    /// Resolves the randomized emitter duration and delay described by the required module.
    /// </summary>
    private void InitializeEmitterTiming(VfxEmitterState emitter)
    {
        VfxEmitterDefinition definition = emitter.Definition;
        emitter.CurrentDuration = definition.UseDurationRange
            ? Lerp(definition.DurationLow, definition.Duration, random.NextFloat())
            : definition.Duration;
        emitter.CurrentDelay = definition.UseDelayRange
            ? Lerp(definition.DelayLow, definition.Delay, random.NextFloat())
            : definition.Delay;
    }

    private static float Lerp(float from, float to, float alpha) => from + ((to - from) * alpha);

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
        if (Time < SystemDelay)
        {
            return;
        }
        bool allFinished = true;
        foreach (VfxEmitterState emitter in emitters)
        {
            UpdateParticles(emitter, timestep);
            bool spawningFinished = SpawnParticles(emitter, timestep);
            if (spawningFinished && emitter.Definition.KillOnCompleted)
            {
                // ParticleModuleRequired.bKillOnCompleted removes remaining particles once looping ends.
                emitter.Particles.Clear();
            }
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
            if (IsKilled(emitter.Definition, particle, relativeTime))
            {
                emitter.Particles.RemoveAt(index);
                continue;
            }
            particle.OrbitRotation += particle.OrbitRotationRate * timestep;
            particle.OrbitOffset = RotateOrbitOffset(particle.OrbitBaseOffset, particle.OrbitRotation);
            // ParticleModuleRotationRateMultiplyLife scales the rate; ParticleModuleRotationOverLifetime
            // either scales or offsets the resulting angle depending on its Scale flag.
            particle.BaseRotation += particle.RotationRate
                * emitter.Definition.RotationRateMultiplierOverLife.Evaluate(relativeTime, particle.Random)
                * timestep;
            float rotationOverLife = emitter.Definition.RotationOverLife.Evaluate(relativeTime, particle.Random);
            particle.Rotation = emitter.Definition.RotationOverLifeScales
                ? particle.BaseRotation * rotationOverLife
                : particle.BaseRotation + rotationOverLife;
            if (emitter.Definition.MeshEmitter is { } meshDefinition)
            {
                particle.MeshRotation += particle.MeshRotationRate
                    * meshDefinition.RotationRateMultiplierOverLife.Evaluate(relativeTime, particle.Random)
                    * timestep;
            }
            particle.Size = particle.BaseSize * emitter.Definition.SizeOverLife.Evaluate(relativeTime, particle.Random);
            particle.Size *= emitter.Definition.SizeScale.Evaluate(relativeTime, particle.Random);
            particle.Size *= emitter.Definition.SizeScaleByTime.Evaluate(particle.Age, particle.Random);
            // ParticleModuleSizeMultiplyVelocity scales each axis by the current velocity magnitude.
            Vector3 velocityMultiplier = emitter.Definition.SizeMultiplyVelocity.Evaluate(relativeTime, particle.Random);
            if (velocityMultiplier != Vector3.One)
            {
                particle.Size *= Vector3.One + ((velocityMultiplier - Vector3.One) * particle.Velocity.Length());
            }
            float colorScaleTime = emitter.Definition.ColorScaleUsesEmitterTime
                ? GetEmitterRelativeTime(emitter)
                : relativeTime;
            particle.Color = emitter.Definition.InitialColor.Evaluate(0, particle.Random)
                * emitter.Definition.ColorOverLife.Evaluate(relativeTime, particle.Random)
                * emitter.Definition.ColorScaleOverLife.Evaluate(colorScaleTime, particle.Random);
            UpdateDynamicParameters(emitter, ref particle, spawn: false);
            particle.SubImageIndex = EvaluateSubImageIndex(emitter, particle);
            AdvanceRandomImage(emitter.Definition, ref particle, timestep);
            emitter.Particles[index] = particle;
        }
    }

    private bool SpawnParticles(VfxEmitterState emitter, float timestep)
    {
        VfxEmitterDefinition definition = emitter.Definition;
        float previousAge = emitter.Age;
        emitter.Age += timestep;
        if (emitter.Age <= emitter.CurrentDelay)
        {
            return false;
        }

        float previousActiveAge = Math.Max(0, previousAge - emitter.CurrentDelay);
        float activeAge = emitter.Age - emitter.CurrentDelay;
        float duration = emitter.CurrentDuration;
        if (duration > 0 && definition.Loops > 0 && activeAge >= duration * definition.Loops)
        {
            return true;
        }

        int previousCycle = duration > 0 ? (int)(previousActiveAge / duration) : 0;
        int currentCycle = duration > 0 ? (int)(activeAge / duration) : 0;
        if (currentCycle != previousCycle && definition.RecalculateDurationEachLoop)
        {
            // bDurationRecalcEachLoop re-rolls the ranged duration at every loop boundary.
            emitter.CurrentDuration = definition.UseDurationRange
                ? Lerp(definition.DurationLow, definition.Duration, random.NextFloat())
                : definition.Duration;
        }
        for (int cycle = previousCycle; cycle <= currentCycle; cycle++)
        {
            if (definition.Loops > 0 && cycle >= definition.Loops)
            {
                break;
            }

            float segmentStart = cycle == previousCycle ? previousActiveAge - (cycle * duration) : 0;
            float segmentEnd = cycle == currentCycle ? activeAge - (cycle * duration) : duration;
            if (duration <= 0)
            {
                segmentStart = previousActiveAge;
                segmentEnd = activeAge;
            }

            SpawnBursts(emitter, cycle, segmentStart, segmentEnd);
            float rate = Math.Max(0, definition.SpawnRate.Evaluate(segmentEnd, random.NextFloat()));
            rate += emitter.InterpolatedBurstRate;
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
                if (emitter.Definition.BurstMethod == VfxBurstMethod.Interpolated && emitter.CurrentDuration > 0)
                {
                    // EPBM_Interpolated spreads the burst across the remaining emitter duration instead of
                    // releasing everything on the burst frame.
                    float span = Math.Max(0.0001f, emitter.CurrentDuration - burst.Time);
                    emitter.InterpolatedBurstRate += count / span;
                    continue;
                }
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
            float initialRotation = emitter.Definition.InitialRotation.Evaluate(emitterTime, random.NextFloat());
            var particle = new VfxParticle
            {
                Position = emitter.Definition.InitialLocation.Evaluate(emitterTime, random.NextFloat()),
                BaseVelocity = initialVelocity,
                Velocity = initialVelocity,
                BaseSize = baseSize,
                Size = baseSize * emitter.Definition.SizeOverLife.Evaluate(0, sample),
                Color = emitter.Definition.InitialColor.Evaluate(0, random.NextFloat()),
                Rotation = initialRotation,
                BaseRotation = initialRotation,
                RotationRate = emitter.Definition.RotationRate.Evaluate(emitterTime, random.NextFloat()),
                Lifetime = lifetime,
                Random = sample,
                RandomImageChangesRemaining = emitter.Definition.RandomImageChanges
            };
            if (emitter.Definition.MeshEmitter is { } meshDefinition)
            {
                particle.MeshRotation = meshDefinition.StartRotation.Evaluate(emitterTime, random.NextFloat());
                particle.MeshRotationRate = meshDefinition.StartRotationRate.Evaluate(emitterTime, random.NextFloat());
            }
            ApplySpawnInitializers(emitter.Definition, ref particle, emitterTime);
            particle.BaseVelocity = particle.Velocity;
            UpdateDynamicParameters(emitter, ref particle, spawn: true);
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
                case VfxSphereSpawnInitializer sphere:
                    ApplySphereInitializer(sphere, ref particle, emitterTime);
                    break;
                case VfxRadialVelocitySpawnInitializer radial:
                {
                    // ParticleModuleVelocity.StartVelocityRadial pushes the particle away from the emitter origin.
                    float magnitude = radial.Speed.Evaluate(emitterTime, random.NextFloat());
                    Vector3 direction = particle.Position.LengthSquared() > 0.000001f
                        ? Vector3.Normalize(particle.Position)
                        : GetRandomUnitVector();
                    particle.Velocity += direction * magnitude;
                    break;
                }
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

    /// <summary>
    /// Applies ParticleModuleKillBox and ParticleModuleKillHeight volumes.
    /// </summary>
    private static bool IsKilled(VfxEmitterDefinition definition, in VfxParticle particle, float relativeTime)
    {
        if (definition.KillVolumes.Count == 0)
        {
            return false;
        }

        Vector3 position = particle.Position + particle.OrbitOffset;
        foreach (VfxKillVolume volume in definition.KillVolumes)
        {
            switch (volume)
            {
                case VfxKillBox box:
                {
                    Vector3 lower = box.LowerLeftCorner.Evaluate(relativeTime, particle.Random);
                    Vector3 upper = box.UpperRightCorner.Evaluate(relativeTime, particle.Random);
                    Vector3 minimum = Vector3.Min(lower, upper);
                    Vector3 maximum = Vector3.Max(lower, upper);
                    bool inside = position.X >= minimum.X && position.X <= maximum.X
                        && position.Y >= minimum.Y && position.Y <= maximum.Y
                        && position.Z >= minimum.Z && position.Z <= maximum.Z;
                    if (inside == box.KillInside)
                    {
                        return true;
                    }
                    break;
                }
                case VfxKillHeight height:
                {
                    float plane = height.Height.Evaluate(relativeTime, particle.Random);
                    // bFloor kills particles that fall below the plane, otherwise those that rise above it.
                    if (height.IsFloor ? position.Z < plane : position.Z > plane)
                    {
                        return true;
                    }
                    break;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Applies ParticleModuleRequired.RandomImageTime / RandomImageChanges by re-rolling the sub-image.
    /// </summary>
    private static void AdvanceRandomImage(VfxEmitterDefinition definition, ref VfxParticle particle, float timestep)
    {
        if (definition.RandomImageTime <= 0 || definition.RandomImageChanges <= 0)
        {
            return;
        }

        particle.RandomImageTimer += timestep;
        if (particle.RandomImageTimer < definition.RandomImageTime || particle.RandomImageChangesRemaining <= 0)
        {
            return;
        }

        particle.RandomImageTimer -= definition.RandomImageTime;
        particle.RandomImageChangesRemaining--;
        int frameCount = Math.Max(1, definition.SubImagesHorizontal * definition.SubImagesVertical);
        // Reuse the particle's stable random sample so the sequence stays deterministic per particle.
        float sample = Fract((particle.Random * 977.13f) + (definition.RandomImageChanges - particle.RandomImageChangesRemaining));
        particle.SubImageIndex = (int)(sample * frameCount) % frameCount;
    }

    private static float Fract(float value) => value - MathF.Floor(value);

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

    /// <summary>
    /// ParticleModuleLocationPrimitiveSphere places particles inside or on a hemisphere-masked sphere.
    /// </summary>
    private void ApplySphereInitializer(VfxSphereSpawnInitializer sphere, ref VfxParticle particle, float emitterTime)
    {
        float radius = Math.Max(0, sphere.StartRadius.Evaluate(emitterTime, random.NextFloat()));
        Vector3 direction = GetRandomUnitVector();
        direction = new Vector3(
            MaskAxis(direction.X, sphere.PositiveX, sphere.NegativeX),
            MaskAxis(direction.Y, sphere.PositiveY, sphere.NegativeY),
            MaskAxis(direction.Z, sphere.PositiveZ, sphere.NegativeZ));
        if (direction.LengthSquared() <= 0.000001f)
        {
            direction = Vector3.UnitX;
        }
        direction = Vector3.Normalize(direction);
        float distance = sphere.SurfaceOnly ? radius : MathF.Cbrt(random.NextFloat()) * radius;
        Vector3 offset = direction * distance;
        particle.Position += sphere.StartLocation.Evaluate(emitterTime, random.NextFloat()) + offset;
        if (sphere.Velocity)
        {
            particle.Velocity += direction * sphere.VelocityScale.Evaluate(emitterTime, random.NextFloat());
        }
    }

    private static float MaskAxis(float value, bool positiveAllowed, bool negativeAllowed)
    {
        if (value >= 0)
        {
            return positiveAllowed ? value : negativeAllowed ? -value : 0;
        }
        return negativeAllowed ? value : positiveAllowed ? -value : 0;
    }

    private Vector3 GetRandomUnitVector()
    {
        float z = (random.NextFloat() * 2f) - 1f;
        float angle = random.NextFloat() * MathF.Tau;
        float planar = MathF.Sqrt(Math.Max(0, 1f - (z * z)));
        return new Vector3(planar * MathF.Cos(angle), planar * MathF.Sin(angle), z);
    }

    private static float GetEmitterRelativeTime(VfxEmitterState emitter)
    {
        float activeAge = Math.Max(0, emitter.Age - emitter.CurrentDelay);
        if (emitter.CurrentDuration <= 0)
        {
            return activeAge;
        }
        return (activeAge % emitter.CurrentDuration) / emitter.CurrentDuration;
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
        => TryGetBounds(Definition?.SystemTransform ?? Matrix4x4.Identity, out minimum, out maximum);

    public bool TryGetBounds(Matrix4x4 previewTransform, out Vector3 minimum, out Vector3 maximum)
    {
        if (Definition?.FixedLocalBounds is { IsValid: true } fixedLocalBounds)
        {
            VfxBounds fixedWorldBounds = VfxBoundsMath.Transform(fixedLocalBounds, previewTransform);
            minimum = fixedWorldBounds.Minimum;
            maximum = fixedWorldBounds.Maximum;
            return fixedWorldBounds.IsValid;
        }

        return TryGetDynamicBounds(previewTransform, out minimum, out maximum);
    }

    private static void UpdateDynamicParameters(VfxEmitterState emitter, ref VfxParticle particle, bool spawn)
    {
        IReadOnlyList<VfxDynamicParameterDefinition> parameters = emitter.Definition.DynamicParameters;
        for (int index = 0; index < Math.Min(4, parameters.Count); index++)
        {
            VfxDynamicParameterDefinition parameter = parameters[index];
            if (!spawn && parameter.SpawnTimeOnly)
            {
                continue;
            }

            float time = parameter.UseEmitterTime ? GetEmitterRelativeTime(emitter) : particle.RelativeTime;
            float authoredValue = parameter.Value.Evaluate(time, particle.Random);
            float value = parameter.ValueMethod switch
            {
                VfxDynamicParameterValueMethod.VelocityX => particle.Velocity.X,
                VfxDynamicParameterValueMethod.VelocityY => particle.Velocity.Y,
                VfxDynamicParameterValueMethod.VelocityZ => particle.Velocity.Z,
                VfxDynamicParameterValueMethod.VelocityMagnitude => particle.Velocity.Length(),
                _ => authoredValue
            };
            if (parameter.ValueMethod != VfxDynamicParameterValueMethod.UserSet
                && parameter.ScaleVelocityByParamValue)
            {
                value *= authoredValue;
            }
            particle.DynamicParameter[index] = value;
        }
    }

    public bool TryGetDynamicBounds(out Vector3 minimum, out Vector3 maximum)
        => TryGetDynamicBounds(Definition?.SystemTransform ?? Matrix4x4.Identity, out minimum, out maximum);

    public bool TryGetDynamicBounds(Matrix4x4 previewTransform, out Vector3 minimum, out Vector3 maximum)
        => TryGetDynamicBounds(_ => previewTransform, out minimum, out maximum);

    public bool TryGetDynamicBounds(Func<VfxEmitterDefinition, Matrix4x4> transformProvider, out Vector3 minimum, out Vector3 maximum)
    {
        minimum = new Vector3(float.MaxValue);
        maximum = new Vector3(float.MinValue);
        bool found = false;
        foreach (VfxEmitterState emitter in emitters)
        {
            Matrix4x4 previewTransform = transformProvider?.Invoke(emitter.Definition) ?? Matrix4x4.Identity;
            foreach (VfxParticle particle in emitter.Particles)
            {
                Vector3 size = Vector3.Abs(particle.Size);
                Vector3 extent = new(
                    size.X * Math.Max(0.0001f, emitter.Definition.SourceAspect.X) * 0.5f,
                    size.Y * Math.Max(0.0001f, emitter.Definition.SourceAspect.Y) * 0.5f,
                    Math.Max(size.X, size.Y) * 0.5f);
                Vector3 localRenderPosition = particle.Position + particle.OrbitOffset;
                VfxBounds particleBounds = VfxBoundsMath.Transform(
                    new VfxBounds(localRenderPosition - extent, localRenderPosition + extent),
                    previewTransform);
                if (!particleBounds.IsValid
                    || !IsUsableBoundsValue(particleBounds.Minimum)
                    || !IsUsableBoundsValue(particleBounds.Maximum))
                {
                    continue;
                }
                minimum = Vector3.Min(minimum, particleBounds.Minimum);
                maximum = Vector3.Max(maximum, particleBounds.Maximum);
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
    internal float CurrentDuration { get; set; }
    internal float CurrentDelay { get; set; }
    internal float InterpolatedBurstRate { get; set; }
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
