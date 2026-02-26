using UnityEngine;

public abstract class ScatterLayerProfileSO : ScriptableObject
{
    [Header("Identity")]
    public ScatterSurfaceType surfaceType = ScatterSurfaceType.Grass;

    [Header("Placement")]
    [Range(1, 16)] public int variationCount = 8;
    public uint globalSeed = 12345;

    [Header("Scale")]
    public float scaleMin = 0.85f;
    public float scaleMax = 1.15f;
}
