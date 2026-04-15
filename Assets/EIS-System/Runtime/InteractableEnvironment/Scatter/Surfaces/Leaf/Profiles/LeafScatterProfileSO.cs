using UnityEngine;

[CreateAssetMenu(menuName = "Trolls/Scatter/Profiles/Leaf", fileName = "LeafScatterProfile")]
public sealed class LeafScatterProfileSO : ScatterLayerProfileSO
{
    [Header("Leaf")]
    public float flutterStrength = 0.5f;

    private void Reset()
    {
        surfaceType = ScatterSurfaceType.Leaf;
    }
}
