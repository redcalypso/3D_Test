using UnityEngine;

[CreateAssetMenu(menuName = "Trolls/Scatter/Profiles/Grass", fileName = "GrassScatterProfile")]
public sealed class GrassScatterProfileSO : ScatterLayerProfileSO
{
    [Header("Grass")]
    public float bendStrength = 1.0f;

    private void Reset()
    {
        surfaceType = ScatterSurfaceType.Grass;
    }
}
