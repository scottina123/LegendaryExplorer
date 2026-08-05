using System;
using System.Collections.Generic;
using System.Numerics;

namespace LegendaryExplorer.Tools.AssetDatabase.VFXPreview;

public sealed class VfxConstantDistribution<T>(T value) : IVfxDistribution<T>
{
    public T Value { get; } = value;

    public T Evaluate(float time, float random) => Value;
}

public sealed class VfxUniformFloatDistribution(float minimum, float maximum) : IVfxDistribution<float>
{
    public float Evaluate(float time, float random) => minimum + ((maximum - minimum) * Math.Clamp(random, 0, 1));
}

public sealed class VfxUniformVectorDistribution(Vector3 minimum, Vector3 maximum) : IVfxDistribution<Vector3>
{
    public Vector3 Evaluate(float time, float random) => Vector3.Lerp(minimum, maximum, Math.Clamp(random, 0, 1));
}

public sealed class VfxProductFloatDistribution(IVfxDistribution<float> left, IVfxDistribution<float> right) : IVfxDistribution<float>
{
    public float Evaluate(float time, float random) => left.Evaluate(time, random) * right.Evaluate(time, random);
}

public sealed class VfxRawFloatDistribution(
    IReadOnlyList<float> values,
    int operation,
    int chunkSize,
    float startTime,
    float timeScale,
    float fallback) : IVfxDistribution<float>
{
    public float Evaluate(float time, float random)
    {
        int stride = Math.Max(chunkSize, operation == 2 ? 2 : 1);
        int sampleCount = values.Count / stride;
        if (sampleCount == 0)
        {
            return fallback;
        }

        float samplePosition = GetSamplePosition(time, startTime, timeScale, sampleCount);
        int lower = Math.Min((int)samplePosition, sampleCount - 1);
        int upper = Math.Min(lower + 1, sampleCount - 1);
        float alpha = samplePosition - lower;
        float lowerValue = ReadSample(lower, stride, random);
        float upperValue = ReadSample(upper, stride, random);
        return float.Lerp(lowerValue, upperValue, alpha);
    }

    private float ReadSample(int sample, int stride, float random)
    {
        int offset = sample * stride;
        if (operation == 2 && offset + 1 < values.Count)
        {
            return float.Lerp(values[offset], values[offset + 1], Math.Clamp(random, 0, 1));
        }
        return values[offset];
    }

    internal static float GetSamplePosition(float time, float startTime, float timeScale, int sampleCount)
    {
        if (sampleCount <= 1)
        {
            return 0;
        }

        // UE3 serializes LookupTableTimeScale as samples per unit of input time, not as a normalized
        // curve-time multiplier. Multiplying by the sample count again makes a 21-sample, scale-20
        // curve reach its final value at 0.05 instead of 1.0 (commonly making fire alpha immediately zero).
        return timeScale > 0
            ? Math.Clamp((time - startTime) * timeScale, 0, sampleCount - 1)
            : Math.Clamp(time, 0, 1) * (sampleCount - 1);
    }
}

public sealed class VfxRawVectorDistribution(
    IReadOnlyList<Vector3> values,
    int operation,
    int chunkSize,
    float startTime,
    float timeScale,
    Vector3 fallback) : IVfxDistribution<Vector3>
{
    public Vector3 Evaluate(float time, float random)
    {
        int stride = Math.Max(chunkSize, operation == 2 ? 2 : 1);
        int sampleCount = values.Count / stride;
        if (sampleCount == 0)
        {
            return fallback;
        }

        float samplePosition = VfxRawFloatDistribution.GetSamplePosition(time, startTime, timeScale, sampleCount);
        int lower = Math.Min((int)samplePosition, sampleCount - 1);
        int upper = Math.Min(lower + 1, sampleCount - 1);
        float alpha = samplePosition - lower;
        return Vector3.Lerp(ReadSample(lower, stride, random), ReadSample(upper, stride, random), alpha);
    }

    private Vector3 ReadSample(int sample, int stride, float random)
    {
        int offset = sample * stride;
        if (operation == 2 && offset + 1 < values.Count)
        {
            return Vector3.Lerp(values[offset], values[offset + 1], Math.Clamp(random, 0, 1));
        }
        return values[offset];
    }
}

public sealed class VfxCurveFloatDistribution(IReadOnlyList<float> values, float timeScale = 1) : IVfxDistribution<float>
{
    public float Evaluate(float time, float random)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        if (values.Count == 1)
        {
            return values[0];
        }

        float position = Math.Clamp(time * timeScale, 0, 1) * (values.Count - 1);
        int lower = (int)position;
        int upper = Math.Min(lower + 1, values.Count - 1);
        return float.Lerp(values[lower], values[upper], position - lower);
    }
}

public sealed class VfxCurveVectorDistribution(IReadOnlyList<Vector3> values, float timeScale = 1) : IVfxDistribution<Vector3>
{
    public Vector3 Evaluate(float time, float random)
    {
        if (values.Count == 0)
        {
            return Vector3.Zero;
        }

        if (values.Count == 1)
        {
            return values[0];
        }

        float position = Math.Clamp(time * timeScale, 0, 1) * (values.Count - 1);
        int lower = (int)position;
        int upper = Math.Min(lower + 1, values.Count - 1);
        return Vector3.Lerp(values[lower], values[upper], position - lower);
    }
}

public sealed class VfxCurveColorDistribution(IReadOnlyList<Vector4> values, float timeScale = 1) : IVfxDistribution<Vector4>
{
    public Vector4 Evaluate(float time, float random)
    {
        if (values.Count == 0)
        {
            return Vector4.One;
        }

        if (values.Count == 1)
        {
            return values[0];
        }

        float position = Math.Clamp(time * timeScale, 0, 1) * (values.Count - 1);
        int lower = (int)position;
        int upper = Math.Min(lower + 1, values.Count - 1);
        return Vector4.Lerp(values[lower], values[upper], position - lower);
    }
}
