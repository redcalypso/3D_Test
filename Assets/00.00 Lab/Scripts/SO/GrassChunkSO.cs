using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Trolls/Grass/Grass Chunk SO", fileName = "GrassChunkSO")]
public sealed class GrassChunkSO : ScriptableObject
{
    [Header("Chunk Settings (Room Local)")]
    public float chunkSize = 32f;
    public float cellSize = 0.25f;

    [Tooltip("Max 16. For now you can set 8.")]
    [Range(1, 16)] public int variationCount = 8;

    [Tooltip("Stable seed for deterministic jitter/variant/yaw.")]
    public uint globalSeed = 12345;

    [Header("Scale Mapping")]
    public float scaleMin = 0.85f;
    public float scaleMax = 1.15f;

    // Sparse storage: only painted cells exist.
    public List<CellRecord> cells = new();

    public int CellsPerAxis => Mathf.RoundToInt(chunkSize / cellSize); // 32/0.25 = 128
}

[Serializable]
public struct CellRecord
{
    public ushort cx;
    public ushort cy;
    public byte variant;
    public byte scaleByte;

    public int Key(int cellsPerAxis) => cy * cellsPerAxis + cx;

    public float Scale(float min, float max) => Mathf.Lerp(min, max, scaleByte / 255f);

    public static byte Encode01(float t01) =>
        (byte)Mathf.Clamp(Mathf.RoundToInt(t01 * 255f), 0, 255);
}