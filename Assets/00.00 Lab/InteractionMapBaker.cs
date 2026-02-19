using UnityEngine;

[DisallowMultipleComponent]
public sealed class InteractionMapBakerInteractionMapBaker : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform _target;              // Player transform
    [SerializeField] private Camera _interactionCamera;       // Cam_Interaction
    [SerializeField] private RenderTexture _interactionRT;    // RT_Interaction

    [Header("Camera Follow")]
    [SerializeField] private float _cameraHeight = 50f;       // 씬에 맞게 조절
    [SerializeField] private bool _useLateUpdate = true;

    [Header("Shader Globals")]
    [SerializeField] private string _rtGlobalName = "_InteractionRT";
    [SerializeField] private string _camDataGlobalName = "_InteractionCamData";
    // _InteractionCamData = (camPosX, camPosZ, orthoSize, worldSize)

    private int _rtId;
    private int _camDataId;

    private void Reset()
    {
        _interactionCamera = GetComponent<Camera>();
    }

    private void Awake()
    {
        if (_interactionCamera == null) _interactionCamera = GetComponent<Camera>();

        _rtId = Shader.PropertyToID(_rtGlobalName);
        _camDataId = Shader.PropertyToID(_camDataGlobalName);

        // 처음 한 번 등록 (RT가 바뀔 수 있으면 Tick에서도 계속 등록해도 됨)
        if (_interactionRT != null)
            Shader.SetGlobalTexture(_rtId, _interactionRT);
    }

    private void Update()
    {
        if (!_useLateUpdate) Tick();
    }

    private void LateUpdate()
    {
        if (_useLateUpdate) Tick();
    }

    private void Tick()
    {
        if (_target == null || _interactionCamera == null)
            return;

        // Ortho 카메라 기준 월드 커버 크기
        float orthoSize = _interactionCamera.orthographicSize;
        float worldSize = orthoSize * 2f;

        // RT 해상도(기본 256)
        int rtRes = (_interactionRT != null) ? _interactionRT.width : 256;
        float pixelSize = worldSize / Mathf.Max(1, rtRes);

        Vector3 t = _target.position;

        // 핵심: 픽셀 스냅 (Shimmer/Jitter 방지)
        float snapX = Mathf.Round(t.x / pixelSize) * pixelSize;
        float snapZ = Mathf.Round(t.z / pixelSize) * pixelSize;

        // 카메라 위치 세팅 (Y는 고정)
        _interactionCamera.transform.position = new Vector3(snapX, _cameraHeight, snapZ);

        // 셰이더 글로벌 등록
        if (_interactionRT != null)
            Shader.SetGlobalTexture(_rtId, _interactionRT);

        Shader.SetGlobalVector(_camDataId, new Vector4(snapX, snapZ, orthoSize, worldSize));
    }
}
