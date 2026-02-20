using UnityEngine;

public static class GrassHash
{
    // Simple integer hash (good enough for deterministic jitter)
    public static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352d;
        x ^= x >> 15;
        x *= 0x846ca68b;
        x ^= x >> 16;
        return x;
    }

    public static uint MakeSeed(uint globalSeed, int cx, int cy)
    {
        unchecked
        {
            uint s = globalSeed;
            s ^= (uint)cx * 73856093u;
            s ^= (uint)cy * 19349663u;
            return Hash(s);
        }
    }

    public static float To01(uint h) => (h & 0x00FFFFFFu) / 16777215f;

    public static Vector2 Jitter(uint seed, float jitterRadius)
    {
        uint h1 = Hash(seed ^ 0xA2C2A2C2u);
        uint h2 = Hash(seed ^ 0xB3D3B3D3u);

        float rx = To01(h1) * 2f - 1f;
        float ry = To01(h2) * 2f - 1f;
        return new Vector2(rx, ry) * jitterRadius;
    }

    public static byte Variant(uint seed, int variationCount)
    {
        uint h = Hash(seed ^ 0xC4E4C4E4u);
        int v = (int)(h % (uint)Mathf.Max(1, variationCount));
        return (byte)Mathf.Clamp(v, 0, 15);
    }

    public static byte YawByte(uint seed)
    {
        uint h = Hash(seed ^ 0xD5F5D5F5u);
        // 0..255
        return (byte)(h & 0xFF);
    }
}