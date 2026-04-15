using UnityEngine;

[CreateAssetMenu(menuName = "Trolls/Scatter/Profiles/Pebble", fileName = "PebbleScatterProfile")]
public sealed class PebbleScatterProfileSO : ScatterLayerProfileSO
{
    [Header("Pebble")]
    public float minSpacing = 0.2f;

    private void Reset()
    {
        surfaceType = ScatterSurfaceType.Pebble;
    }
}
