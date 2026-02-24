using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class ScatterField : MonoBehaviour
{
    [Header("Primary Layer")]
    public ScatterChunkSO primaryChunk;

    [Header("Optional Layers")]
    public List<ScatterChunkSO> layers = new();
}
