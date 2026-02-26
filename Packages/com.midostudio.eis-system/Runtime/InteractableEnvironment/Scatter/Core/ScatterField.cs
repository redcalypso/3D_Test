using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class ScatterField : MonoBehaviour
{
    [Header("Room Data Source")]
    public RoomScatterDataSO roomData;

    [Header("Render Source")]
    public Mesh[] variationMeshes;
    public Material sharedMaterial;

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

    [Header("Interaction Color Tuning")]
    public bool overrideInteractionColorTuning = false;
    [Range(0f, 1f)] public float pressColorWeight = 0f;
    [Range(0f, 1f)] public float bendColorWeight = 0f;

    public bool HasRenderConfig =>
        sharedMaterial != null &&
        variationMeshes != null &&
        variationMeshes.Length > 0;

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
}
