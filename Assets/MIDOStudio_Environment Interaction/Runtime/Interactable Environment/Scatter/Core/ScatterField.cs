using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class ScatterField : MonoBehaviour
{
    [Header("Primary Layer")]
    public ScatterChunkSO primaryChunk;

    [Header("Optional Layers")]
    public List<ScatterChunkSO> layers = new();

    [Header("Render Source")]
    public Mesh[] variationMeshes;
    public Material sharedMaterial;

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
}
