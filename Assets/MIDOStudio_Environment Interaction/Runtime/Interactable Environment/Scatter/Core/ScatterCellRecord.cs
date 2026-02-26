using System;
using UnityEngine;

[Serializable]
public struct CellRecord
{
    public ushort cx;
    public ushort cy;
    public byte variant;
    public byte scaleByte;

    public static int Key(int cx, int cy, int cellsPerAxis) => cy * cellsPerAxis + cx;
    public int Key(int cellsPerAxis) => Key(cx, cy, cellsPerAxis);

    public float Scale(float min, float max)
    {
        if (max < min) (min, max) = (max, min);
        float t = scaleByte * (1f / 255f);
        return Mathf.Max(0.1f, Mathf.Lerp(min, max, t));
    }

    public static byte Encode01(float t01) =>
        (byte)Mathf.Clamp(Mathf.RoundToInt(t01 * 255f), 0, 255);
}
