using UnityEngine;

[CreateAssetMenu(menuName = "Trolls/Scatter/Chunks/Scatter Chunk", fileName = "ScatterChunk")]
public sealed class ScatterChunkAssetSO : ScatterChunkSO
{
    // Concrete asset type so ScatterRenderer/ScatterField can reference chunk assets directly.
}
