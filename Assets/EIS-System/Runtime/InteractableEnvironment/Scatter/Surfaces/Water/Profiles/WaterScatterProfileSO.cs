using UnityEngine;

[CreateAssetMenu(menuName = "Trolls/Scatter/Profiles/Water", fileName = "WaterScatterProfile")]
public sealed class WaterScatterProfileSO : ScatterLayerProfileSO
{
    [Header("Water")]
    public float rippleDamping = 0.92f;

    private void Reset()
    {
        surfaceType = ScatterSurfaceType.Water;
    }
}
