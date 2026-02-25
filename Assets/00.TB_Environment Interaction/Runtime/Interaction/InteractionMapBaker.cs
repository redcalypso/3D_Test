using UnityEngine;
using UnityEngine.Rendering;

[DisallowMultipleComponent]
public sealed class InteractionMapBaker : MonoBehaviour
{
    private const int RT_RES = 256;

    [Header("References")]
    [SerializeField] private Transform _target;
    [SerializeField] private Camera _interactionCamera;
    [SerializeField] private RenderTexture _interactionRT;

    [Header("Camera Follow")]
    [SerializeField] private float _cameraHeight = 50f;
    [SerializeField] private bool _useLateUpdate = true;

    [Header("Shader Globals")]
    [SerializeField] private string _rtGlobalName = "_InteractionRT";
    [SerializeField] private string _camPosXZGlobalName = "_InteractionCamPosXZ";
    [SerializeField] private string _camParamsGlobalName = "_InteractionCamParams";

    [Header("Neutral Clear Color (Data Encoding)")]
    [SerializeField] private bool _useManualNeutralCompensation = true;
    [SerializeField] private float _manualNeutralCompensatedRG = 0.735f;
    [SerializeField] private Color _neutralTarget = new Color(0.5f, 0.5f, 0f, 0f);

    [Header("Update Policy")]
    [SerializeField] private int _cameraDataUpdateHz = 0;
    [SerializeField] private bool _skipWhenSnapUnchanged = true;

    [Header("Event-driven Rendering")]
    [SerializeField] private bool _eventDrivenRender = true;
    [SerializeField] private int _dirtyFrames = 2;

    [Header("Auto Dirty")]
    [SerializeField] private bool _autoDirtyOnCameraMove = true;
    [SerializeField] private int _autoDirtyFramesOnMove = 2;

    [Header("Bypass / Debug")]
    [SerializeField] private bool _bypassEventDriven = false;

    [Header("SRP Safety")]
    [Tooltip("In SRP (URP/HDRP), disable manual Camera.Render and let camera auto-render to avoid RenderPass errors.")]
    [SerializeField] private bool _disableManualRenderInSRP = true;

    private int _rtId;
    private int _camPosXZId;
    private int _camParamsId;

    private float _nextUpdateTime;
    private float _lastSnapX = float.NaN;
    private float _lastSnapZ = float.NaN;
    private float _lastOrthoSize = float.NaN;
    private int _dirtyFrameCountdown;

    private static readonly Quaternion CamRotation = Quaternion.Euler(90f, 0f, 0f);

    private bool IsManualRenderAllowed
    {
        get
        {
            if (_bypassEventDriven)
                return false;

            if (!_disableManualRenderInSRP)
                return true;

            return GraphicsSettings.currentRenderPipeline == null;
        }
    }

    public void MarkDirty(int frames = -1)
    {
        if (!_eventDrivenRender)
            return;

        int f = (frames > 0) ? frames : _dirtyFrames;
        if (f > _dirtyFrameCountdown)
            _dirtyFrameCountdown = f;
    }

    private void Reset()
    {
        _interactionCamera = GetComponent<Camera>();
    }

    private void Awake()
    {
        if (_interactionCamera == null)
            _interactionCamera = GetComponent<Camera>();

        EnsureGlobalPropertyIds();
        SetupCameraDefaults();
        ValidateRenderTexture(_interactionRT);

        if (_interactionRT != null && _interactionCamera != null)
        {
            _interactionCamera.targetTexture = _interactionRT;
            Shader.SetGlobalTexture(_rtId, _interactionRT);
        }

        ApplyCameraEnabledMode();
        PushCameraGlobals(force: true);
    }

    private void OnEnable()
    {
        EnsureGlobalPropertyIds();
        ApplyCameraEnabledMode();

        if (_interactionRT != null)
            Shader.SetGlobalTexture(_rtId, _interactionRT);

        PushCameraGlobals(force: true);
    }

    private void Update()
    {
        if (!_useLateUpdate)
            Tick();
    }

    private void LateUpdate()
    {
        if (_useLateUpdate)
            Tick();
    }

    private void Tick()
    {
        if (_interactionCamera == null || _target == null || _interactionRT == null)
            return;

        EnsureGlobalPropertyIds();

        if (_cameraDataUpdateHz > 0)
        {
            if (Time.unscaledTime < _nextUpdateTime)
                return;

            _nextUpdateTime = Time.unscaledTime + (1f / _cameraDataUpdateHz);
        }

        float orthoSize = _interactionCamera.orthographicSize;
        float worldSize = orthoSize * 2f;
        float invWorldSize = worldSize > 0f ? (1f / worldSize) : 0f;
        float pixelSize = worldSize / RT_RES;

        Vector3 targetPos = _target.position;
        float snapX = Mathf.Round(targetPos.x / pixelSize) * pixelSize;
        float snapZ = Mathf.Round(targetPos.z / pixelSize) * pixelSize;

        if (_skipWhenSnapUnchanged)
        {
            bool sameSnap = Mathf.Abs(snapX - _lastSnapX) < 0.000001f && Mathf.Abs(snapZ - _lastSnapZ) < 0.000001f;
            bool sameOrtho = Mathf.Abs(orthoSize - _lastOrthoSize) < 0.000001f;
            if (sameSnap && sameOrtho)
            {
                MaybeRenderRT();
                return;
            }
        }

        _lastSnapX = snapX;
        _lastSnapZ = snapZ;
        _lastOrthoSize = orthoSize;

        _interactionCamera.transform.SetPositionAndRotation(new Vector3(snapX, _cameraHeight, snapZ), CamRotation);

        if (_eventDrivenRender && _autoDirtyOnCameraMove)
            MarkDirty(_autoDirtyFramesOnMove);

        Shader.SetGlobalVector(_camPosXZId, new Vector4(snapX, snapZ, 0f, 0f));
        Shader.SetGlobalVector(_camParamsId, new Vector4(orthoSize, worldSize, invWorldSize, pixelSize));

        MaybeRenderRT();
    }

    private void MaybeRenderRT()
    {
        if (_bypassEventDriven)
            return;

        if (!_eventDrivenRender)
            return;

        if (!IsManualRenderAllowed)
            return;

        if (_dirtyFrameCountdown <= 0)
            return;

        _dirtyFrameCountdown--;
        _interactionCamera.Render();
    }

    private void SetupCameraDefaults()
    {
        if (_interactionCamera == null)
            return;

        _interactionCamera.orthographic = true;
        _interactionCamera.transform.rotation = CamRotation;
        _interactionCamera.clearFlags = CameraClearFlags.SolidColor;
        _interactionCamera.backgroundColor = ComputeNeutralClearColor();
        _interactionCamera.allowHDR = false;
        _interactionCamera.allowMSAA = false;
    }

    private void ApplyCameraEnabledMode()
    {
        if (_interactionCamera == null)
            return;

        if (_eventDrivenRender && IsManualRenderAllowed)
            _interactionCamera.enabled = false;
        else
            _interactionCamera.enabled = true;
    }

    private Color ComputeNeutralClearColor()
    {
        if (_useManualNeutralCompensation)
            return new Color(_manualNeutralCompensatedRG, _manualNeutralCompensatedRG, 0f, 0f);

        return _neutralTarget;
    }

    private void EnsureGlobalPropertyIds()
    {
        if (string.IsNullOrWhiteSpace(_rtGlobalName))
            _rtGlobalName = "_InteractionRT";
        if (string.IsNullOrWhiteSpace(_camPosXZGlobalName))
            _camPosXZGlobalName = "_InteractionCamPosXZ";
        if (string.IsNullOrWhiteSpace(_camParamsGlobalName))
            _camParamsGlobalName = "_InteractionCamParams";

        _rtId = Shader.PropertyToID(_rtGlobalName);
        _camPosXZId = Shader.PropertyToID(_camPosXZGlobalName);
        _camParamsId = Shader.PropertyToID(_camParamsGlobalName);
    }

    private void PushCameraGlobals(bool force)
    {
        if (_interactionCamera == null)
            return;

        EnsureGlobalPropertyIds();

        Vector3 camPos = _interactionCamera.transform.position;
        float orthoSize = _interactionCamera.orthographicSize;
        float worldSize = orthoSize * 2f;
        float invWorldSize = worldSize > 0f ? (1f / worldSize) : 0f;
        float pixelSize = worldSize / RT_RES;

        Shader.SetGlobalVector(_camPosXZId, new Vector4(camPos.x, camPos.z, 0f, 0f));
        Shader.SetGlobalVector(_camParamsId, new Vector4(orthoSize, worldSize, invWorldSize, pixelSize));

        if (force && _interactionRT != null)
            Shader.SetGlobalTexture(_rtId, _interactionRT);
    }

    private void ValidateRenderTexture(RenderTexture rt)
    {
        if (rt == null)
            return;

        if (rt.width != rt.height)
            Debug.LogWarning($"[InteractionMapBaker] RT is not square: {rt.width}x{rt.height}. Recommend square {RT_RES}x{RT_RES}.");

        if (rt.width != RT_RES || rt.height != RT_RES)
            Debug.LogWarning($"[InteractionMapBaker] RT is {rt.width}x{rt.height}, but code assumes RT_RES={RT_RES}. Update RT_RES or RT size.");

        if (rt.filterMode != FilterMode.Point)
            Debug.LogWarning($"[InteractionMapBaker] RT filterMode is {rt.filterMode}. For data maps, Point is usually recommended.");

        if (rt.wrapMode != TextureWrapMode.Clamp)
            Debug.LogWarning($"[InteractionMapBaker] RT wrapMode is {rt.wrapMode}. Clamp is usually recommended.");
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        EnsureGlobalPropertyIds();

        if (_interactionCamera != null)
        {
            _interactionCamera.backgroundColor = ComputeNeutralClearColor();
            ApplyCameraEnabledMode();
        }

        if (Application.isPlaying)
        {
            if (_interactionRT != null)
                Shader.SetGlobalTexture(_rtId, _interactionRT);

            PushCameraGlobals(force: true);
        }
    }
#endif
}
