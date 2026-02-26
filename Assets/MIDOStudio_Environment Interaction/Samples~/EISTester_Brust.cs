using UnityEngine;

public class EISTester_Brust : MonoBehaviour
{
    public InteractionMapBakerV2 bakerV2;
    public EISStampPreset testPreset;

    private void Update()
    {
        if (!Input.GetMouseButtonDown(0) || bakerV2 == null || testPreset == null)
            return;

        bakerV2.RequestStamp(transform.position, Vector3.forward, 1f, 1f, testPreset);
    }
}
