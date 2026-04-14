using UnityEngine;

public class FramerateDebugger : MonoBehaviour
{
    void Awake()
    {
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 120; // 사실상 무제한
    }
}
