using System.Collections.Generic;
using UnityEngine;

public abstract class ScatterChunkSO : ScriptableObject
{
    [Header("Profile (Recommended)")]
    public ScatterLayerProfileSO profile;

    [Header("Legacy Fallback (Used when Profile is null)")]
    public ScatterSurfaceType surfaceType = ScatterSurfaceType.Grass;

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

    [SerializeField, HideInInspector] private int _dataVersion;
    public int DataVersion => _dataVersion;

    public void Touch()
    {
        unchecked
        {
            _dataVersion++;
            if (_dataVersion == int.MinValue)
                _dataVersion = 0;
        }

#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    public int CellsPerAxis
    {
        get
        {
            float v = chunkSize / cellSize;
            int n = Mathf.RoundToInt(v);

            if (Mathf.Abs(v - n) > 0.001f)
            {
                n = Mathf.FloorToInt(v);
#if UNITY_EDITOR
                Debug.LogWarning($"[{GetType().Name}] chunkSize/cellSize is not integer. chunkSize={chunkSize}, cellSize={cellSize}, v={v}. Using CellsPerAxis={n}.");
#endif
            }

            return Mathf.Max(1, n);
        }
    }

    public ScatterSurfaceType EffectiveSurfaceType => profile != null ? profile.surfaceType : surfaceType;
    public int EffectiveVariationCount => Mathf.Clamp(profile != null ? profile.variationCount : variationCount, 1, 16);
    public uint EffectiveGlobalSeed => profile != null ? profile.globalSeed : globalSeed;
    public float EffectiveScaleMin => profile != null ? profile.scaleMin : scaleMin;
    public float EffectiveScaleMax => profile != null ? profile.scaleMax : scaleMax;

    public int ComputeEffectiveSettingsHash()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + (int)EffectiveSurfaceType;
            h = h * 31 + EffectiveVariationCount;
            h = h * 31 + EffectiveGlobalSeed.GetHashCode();
            h = h * 31 + chunkSize.GetHashCode();
            h = h * 31 + cellSize.GetHashCode();
            h = h * 31 + EffectiveScaleMin.GetHashCode();
            h = h * 31 + EffectiveScaleMax.GetHashCode();
            h = h * 31 + (profile != null ? profile.GetInstanceID() : 0);
            return h;
        }
    }
}
