using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Trolls/Scatter/Room Scatter Data", fileName = "RoomScatterData")]
public sealed class RoomScatterDataSO : ScriptableObject
{
    [Serializable]
    public sealed class ChunkData
    {
        public int chunkX;
        public int chunkY;
        public List<CellRecord> cells = new();
    }

    [Serializable]
    public sealed class SurfaceLayerData
    {
        [Header("Identity")]
        public ScatterSurfaceType surfaceType = ScatterSurfaceType.Grass;
        public ScatterLayerProfileSO profile;

        [Header("Chunk Settings")]
        public float chunkSize = 32f;
        public float cellSize = 0.4f;
        [Range(1, 16)] public int variationCount = 8;
        public uint globalSeed = 12345;
        public float scaleMin = 0.85f;
        public float scaleMax = 1.15f;

        [Header("Chunk Data")]
        public List<ChunkData> chunks = new();

        public int CellsPerAxis
        {
            get
            {
                float v = chunkSize / Mathf.Max(0.0001f, cellSize);
                int n = Mathf.RoundToInt(v);
                if (Mathf.Abs(v - n) > 0.001f)
                    n = Mathf.FloorToInt(v);
                return Mathf.Max(1, n);
            }
        }

        public int EffectiveVariationCount => Mathf.Clamp(profile != null ? profile.variationCount : variationCount, 1, 16);
        public uint EffectiveGlobalSeed => profile != null ? profile.globalSeed : globalSeed;
        public float EffectiveScaleMin => profile != null ? profile.scaleMin : scaleMin;
        public float EffectiveScaleMax => profile != null ? profile.scaleMax : scaleMax;

        public int ComputeEffectiveSettingsHash()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (int)surfaceType;
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

    public readonly struct ChunkRef
    {
        public readonly SurfaceLayerData surface;
        public readonly ChunkData chunk;

        public ChunkRef(SurfaceLayerData surface, ChunkData chunk)
        {
            this.surface = surface;
            this.chunk = chunk;
        }
    }

    [Header("Surface Layers")]
    public List<SurfaceLayerData> surfaces = new();

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

    public void CollectChunkRefs(List<ChunkRef> dst)
    {
        if (dst == null)
            return;

        dst.Clear();
        if (surfaces == null)
            return;

        for (int s = 0; s < surfaces.Count; s++)
        {
            SurfaceLayerData surface = surfaces[s];
            if (surface == null || surface.chunks == null)
                continue;

            for (int c = 0; c < surface.chunks.Count; c++)
            {
                ChunkData chunk = surface.chunks[c];
                if (chunk == null)
                    continue;
                dst.Add(new ChunkRef(surface, chunk));
            }
        }
    }

    public SurfaceLayerData GetOrCreateSurface(ScatterSurfaceType surfaceType)
    {
        SurfaceLayerData existing = FindSurface(surfaceType);
        if (existing != null)
            return existing;

        var surface = new SurfaceLayerData { surfaceType = surfaceType };
        surfaces.Add(surface);
        Touch();
        return surface;
    }

    public SurfaceLayerData FindSurface(ScatterSurfaceType surfaceType)
    {
        if (surfaces == null)
            return null;

        for (int i = 0; i < surfaces.Count; i++)
        {
            SurfaceLayerData s = surfaces[i];
            if (s != null && s.surfaceType == surfaceType)
                return s;
        }

        return null;
    }

    public ChunkData GetOrCreateChunkAtLocalPosition(SurfaceLayerData surface, Vector3 localPos)
    {
        if (surface == null)
            return null;

        GetChunkCoordAtLocalPosition(surface, localPos, out int chunkX, out int chunkY);
        ChunkData existing = FindChunk(surface, chunkX, chunkY);
        if (existing != null)
            return existing;

        var chunk = new ChunkData { chunkX = chunkX, chunkY = chunkY, cells = new List<CellRecord>() };
        surface.chunks ??= new List<ChunkData>();
        surface.chunks.Add(chunk);
        Touch();
        return chunk;
    }

    public ChunkData FindChunkAtLocalPosition(SurfaceLayerData surface, Vector3 localPos)
    {
        if (surface == null)
            return null;

        GetChunkCoordAtLocalPosition(surface, localPos, out int chunkX, out int chunkY);
        return FindChunk(surface, chunkX, chunkY);
    }

    public static void GetChunkCoordAtLocalPosition(SurfaceLayerData surface, Vector3 localPos, out int chunkX, out int chunkY)
    {
        float chunkSize = Mathf.Max(0.0001f, surface != null ? surface.chunkSize : 1f);
        float half = chunkSize * 0.5f;
        chunkX = Mathf.FloorToInt((localPos.x + half) / chunkSize);
        chunkY = Mathf.FloorToInt((localPos.z + half) / chunkSize);
    }

    public static ChunkData FindChunk(SurfaceLayerData surface, int chunkX, int chunkY)
    {
        if (surface == null || surface.chunks == null)
            return null;

        for (int i = 0; i < surface.chunks.Count; i++)
        {
            ChunkData c = surface.chunks[i];
            if (c != null && c.chunkX == chunkX && c.chunkY == chunkY)
                return c;
        }

        return null;
    }
}
