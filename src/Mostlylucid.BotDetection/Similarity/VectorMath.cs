using System.Numerics;

namespace Mostlylucid.BotDetection.Similarity;

internal static class VectorMath
{
    /// <summary>Returns false for zero-length, zero-norm, NaN, or infinite vectors.</summary>
    internal static bool IsValidVector(float[] v)
    {
        if (v.Length == 0) return false;
        var normSq = 0f;
        foreach (var x in v) normSq += x * x;
        return normSq > 0f && !float.IsNaN(normSq) && !float.IsInfinity(normSq);
    }

    /// <summary>
    ///     SIMD-accelerated cosine similarity.
    ///     Returns 0 on dimension mismatch or near-zero magnitude.
    ///     Values are in [0, 1] for normalised vectors; unclamped for raw vectors.
    /// </summary>
    internal static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        var len = a.Length;
        var vA = a.AsSpan(0, len);
        var vB = b.AsSpan(0, len);
        float dot = 0f, magA = 0f, magB = 0f;
        var i = 0;
        var simdLen = Vector<float>.Count;

        for (; i <= len - simdLen; i += simdLen)
        {
            var va = new Vector<float>(vA.Slice(i));
            var vb = new Vector<float>(vB.Slice(i));
            dot  += Vector.Dot(va, vb);
            magA += Vector.Dot(va, va);
            magB += Vector.Dot(vb, vb);
        }

        for (; i < len; i++)
        {
            dot  += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return denom < 1e-8f ? 0f : dot / denom;
    }
}
