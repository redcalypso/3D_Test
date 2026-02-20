using UnityEngine;

[DisallowMultipleComponent]
public sealed class InteractionMapBaker : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform _target;              // Player transform
    [SerializeField] private Camera _interactionCamera;       // Cam_Interaction
    [SerializeField] private RenderTexture _interactionRT;    // RT_Interaction

    [Header("Camera Follow")]
    [SerializeField] private float _cameraHeight = 50f;       // ���� �°� ����
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

        if (_interactionRT != null)
            Shader.SetGlobalTexture(_rtId, _interactionRT);

        _interactionCamera.orthographic = true;
        _interactionCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        _interactionCamera.clearFlags = CameraClearFlags.SolidColor;
        _interactionCamera.backgroundColor = new Color(0.735f, 0.735f, 0f, 0f);
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

        float orthoSize = _interactionCamera.orthographicSize;
        float worldSize = orthoSize * 2f;

        int rtRes = (_interactionRT != null) ? _interactionRT.width : 256;
        float pixelSize = worldSize / Mathf.Max(1, rtRes);

        Vector3 t = _target.position;

        float snapX = Mathf.Round(t.x / pixelSize) * pixelSize;
        float snapZ = Mathf.Round(t.z / pixelSize) * pixelSize;

        _interactionCamera.transform.position = new Vector3(snapX, _cameraHeight, snapZ);

        if (_interactionRT != null)
            Shader.SetGlobalTexture(_rtId, _interactionRT);

        Shader.SetGlobalVector(_camDataId, new Vector4(snapX, snapZ, orthoSize, worldSize));
        Debug.Log($"_InteractionCamData = {snapX},{snapZ},{orthoSize},{worldSize}");

        Shader.SetGlobalVector("_CamRightWS", _interactionCamera.transform.right);
    }
}
