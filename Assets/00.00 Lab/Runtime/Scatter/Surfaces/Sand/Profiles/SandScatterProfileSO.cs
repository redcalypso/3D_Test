using UnityEngine;

[CreateAssetMenu(menuName = "Trolls/Scatter/Profiles/Sand", fileName = "SandScatterProfile")]
public sealed class SandScatterProfileSO : ScatterLayerProfileSO
{
    [Header("Sand")]
    public float rippleStrength = 0.4f;

    private void Reset()
    {
        surfaceType = ScatterSurfaceType.Sand;
    }
}
