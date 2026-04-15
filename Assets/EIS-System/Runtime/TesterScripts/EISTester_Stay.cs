using UnityEngine;

public class EISTester : MonoBehaviour
{
    public InteractionMapBakerV2 bakerV2;
    public EISStampPreset testPreset;

    private void Update()
    {
        if (bakerV2 == null || testPreset == null)
            return;

        bakerV2.RequestStamp(transform.position, transform.forward, 1f, 1f, testPreset);
    }
}
