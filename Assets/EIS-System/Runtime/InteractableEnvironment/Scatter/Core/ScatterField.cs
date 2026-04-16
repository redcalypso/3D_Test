using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

[DisallowMultipleComponent]
public sealed class ScatterField : MonoBehaviour
{
    [Header("Room Data Source")]
    public RoomScatterDataSO roomData;

    [Header("Render Source")]
    public List<ScatterSurfaceRenderConfig> surfaceRenderConfigs = new List<ScatterSurfaceRenderConfig>(4);

#pragma warning disable 0414
    [FormerlySerializedAs("variationMeshes"), SerializeField, HideInInspector] private Mesh[] legacyVariationMeshes;
    [FormerlySerializedAs("sharedMaterial"), SerializeField, HideInInspector] private Material legacySharedMaterial;

    [Header("Surface Projection")]
    public bool projectToStaticSurface = false;
    public LayerMask projectionLayerMask = ~0;
    [Min(0.1f)] public float projectionRayStartHeight = 50f;
    [Min(0.1f)] public float projectionRayDistance = 200f;
    public bool alignToSurfaceNormal = false;

    [Header("Render Options")]
    public bool renderInSceneViewWhilePlaying = false;
    public bool enableDistanceLod = true;
    [Min(0f)] public float lodMidDistance = 50f;
    [Min(0f)] public float lodCullDistance = 65f;
    [Min(1)] public int lodMidStride = 2;
    public bool enableInstanceCulling = true;
    [Min(0f)] public float instanceCullPadding = 0.5f;

    [Header("Diagnostics")]
    public bool debugPebbleMotion = false;

    [Header("Interaction Color Tuning")]
    [FormerlySerializedAs("overrideInteractionColorTuning"), SerializeField, HideInInspector] private bool legacyOverrideInteractionColorTuning = false;
    [FormerlySerializedAs("pressColorWeight"), SerializeField, HideInInspector, Range(0f, 1f)] private float legacyPressColorWeight = 0f;
    [FormerlySerializedAs("bendColorWeight"), SerializeField, HideInInspector, Range(0f, 1f)] private float legacyBendColorWeight = 0f;
#pragma warning restore 0414

    public bool HasAnyRenderConfigs =>
        surfaceRenderConfigs != null &&
        surfaceRenderConfigs.Count > 0;

    public void CollectChunkRefs(List<RoomScatterDataSO.ChunkRef> dst)
    {
        if (dst == null)
            return;

        dst.Clear();
        if (roomData == null)
            return;
        roomData.CollectChunkRefs(dst);
    }

    public RoomScatterDataSO.SurfaceLayerData ResolveSurface(ScatterSurfaceType surfaceType)
    {
        if (roomData == null)
            return null;
        return roomData.FindSurface(surfaceType);
    }

    public bool TryGetRenderConfig(ScatterSurfaceType surfaceType, out ScatterSurfaceRenderConfig config)
    {
        if (surfaceRenderConfigs != null)
        {
            for (int i = 0; i < surfaceRenderConfigs.Count; i++)
            {
                ScatterSurfaceRenderConfig candidate = surfaceRenderConfigs[i];
                if (candidate != null && candidate.surfaceType == surfaceType)
                {
                    config = candidate;
                    return true;
                }
            }
        }

        config = null;
        return false;
    }

    public bool TryGetValidRenderConfig(RoomScatterDataSO.SurfaceLayerData surface, out ScatterSurfaceRenderConfig config, out string reason)
    {
        if (surface == null)
        {
            config = null;
            reason = "Surface layer is missing.";
            return false;
        }

        if (!TryGetRenderConfig(surface.surfaceType, out config))
        {
            reason = $"Surface render config required for {surface.surfaceType}.";
            return false;
        }

        return ValidateRenderConfig(config, surface.EffectiveVariationCount, out reason);
    }

    public static bool ValidateRenderConfig(ScatterSurfaceRenderConfig config, int requiredVariationCount, out string reason)
    {
        if (config == null)
        {
            reason = "Surface render config required.";
            return false;
        }

        if (config.sharedMaterial == null)
        {
            reason = $"Shared material is missing for {config.surfaceType}.";
            return false;
        }

        Mesh[] meshes = config.variationMeshes;
        if (meshes == null || meshes.Length == 0)
        {
            reason = $"Variation meshes are missing for {config.surfaceType}.";
            return false;
        }

        int requiredCount = Mathf.Max(1, requiredVariationCount);
        if (meshes.Length != requiredCount)
        {
            reason = $"{config.surfaceType} requires exactly {requiredCount} variation meshes, but {meshes.Length} are assigned.";
            return false;
        }

        for (int i = 0; i < meshes.Length; i++)
        {
            if (meshes[i] == null)
            {
                reason = $"{config.surfaceType} contains an unassigned variation mesh slot at index {i}.";
                return false;
            }
        }

        reason = null;
        return true;
    }
}

[System.Serializable]
public sealed class ScatterSurfaceRenderConfig
{
    [Header("Identity")]
    public ScatterSurfaceType surfaceType = ScatterSurfaceType.Grass;

    [Header("Render Source")]
    public Mesh[] variationMeshes;
    public Material sharedMaterial;

    [Header("Interaction Color Tuning")]
    public bool overrideInteractionColorTuning = false;
    [Range(0f, 1f)] public float pressColorWeight = 0f;
    [Range(0f, 1f)] public float bendColorWeight = 0f;
}
