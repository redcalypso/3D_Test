using UnityEngine;

[DisallowMultipleComponent]
public sealed class InteractionMapBaker : MonoBehaviour
{
    private const int RT_RES = 256;

    [Header("References")]
    [SerializeField] private Transform _target;                 // Player transform
    [SerializeField] private Camera _interactionCamera;         // Cam_Interaction
    [SerializeField] private RenderTexture _interactionRT;      // RT_Interaction

    [Header("Camera Follow")]
    [SerializeField] private float _cameraHeight = 50f;
    [SerializeField] private bool _useLateUpdate = true;

    [Header("Shader Globals")]
    [SerializeField] private string _rtGlobalName = "_InteractionRT";

    [SerializeField] private string _camPosXZGlobalName = "_InteractionCamPosXZ";   // (camX, camZ, 0, 0)
    [SerializeField] private string _camParamsGlobalName = "_InteractionCamParams"; // (orthoSize, worldSize, invWorldSize, pixelSize)

    [Header("Neutral Clear Color (Data Encoding)")]
    [Tooltip("Linear/Gamma 차이 보정 트릭 유지 여부")]
    [SerializeField] private bool _useManualNeutralCompensation = true;

    [Tooltip("수동 보정값 (기본: 0.735). 미르가 쓰던 값 그대로")]
    [SerializeField] private float _manualNeutralCompensatedRG = 0.735f;

    [Tooltip("보정 끄면 이 값 사용")]
    [SerializeField] private Color _neutralTarget = new Color(0.5f, 0.5f, 0f, 0f);

    [Header("Update Policy")]
    [Tooltip("카메라/글로벌 데이터 갱신을 몇 Hz로 제한할지. 0이면 매 프레임.")]
    [SerializeField] private int _cameraDataUpdateHz = 0;

    [Tooltip("스냅된 좌표/파라미터가 안 바뀌면 글로벌 갱신을 스킵")]
    [SerializeField] private bool _skipWhenSnapUnchanged = true;

    [Header("Event-driven Rendering (Big Win)")]
    [Tooltip("true면 매 프레임 렌더하지 않고, MarkDirty()로 요청된 프레임에만 RT를 렌더합니다.")]
    [SerializeField] private bool _eventDrivenRender = true;

    [Tooltip("MarkDirty 호출 후, 몇 프레임 동안 RT 렌더를 유지할지. (브러시가 여러 프레임이면 2~3 추천)")]
    [SerializeField] private int _dirtyFrames = 2;

    [Header("Auto Dirty (Bridge)")]
    [SerializeField] private bool _autoDirtyOnCameraMove = true;
    [SerializeField] private int _autoDirtyFramesOnMove = 2;

    [Header("Bypass / Debug")]
    [SerializeField] private bool _bypassEventDriven = false; // true면 MarkDirty 없어도 항상 렌더

    private int _rtId;
    private int _camPosXZId;
    private int _camParamsId;

    private float _nextUpdateTime;

    private float _lastSnapX = float.NaN;
    private float _lastSnapZ = float.NaN;
    private float _lastOrthoSize = float.NaN;

    private int _dirtyFrameCountdown = 0;

    private static readonly Quaternion CamRotation = Quaternion.Euler(90f, 0f, 0f);

    // 외부(폭탄/발자국/바람 브러시 시스템)에서 호출해주면, 그 프레임부터 RT 렌더가 켜짐
    public void MarkDirty(int frames = -1)
    {
        if (!_eventDrivenRender) return;
        int f = (frames > 0) ? frames : _dirtyFrames;
        if (f > _dirtyFrameCountdown) _dirtyFrameCountdown = f;
    }

    private void Reset()
    {
        _interactionCamera = GetComponent<Camera>();
    }

    private void Awake()
    {
        if (_interactionCamera == null) _interactionCamera = GetComponent<Camera>();

        _rtId = Shader.PropertyToID(_rtGlobalName);
        _camPosXZId = Shader.PropertyToID(_camPosXZGlobalName);
        _camParamsId = Shader.PropertyToID(_camParamsGlobalName);

        SetupCameraDefaults();
        ValidateRenderTexture(_interactionRT);

        if (_interactionRT != null)
        {
            _interactionCamera.targetTexture = _interactionRT;
            Shader.SetGlobalTexture(_rtId, _interactionRT);
        }

        if (_eventDrivenRender && !_bypassEventDriven)
            _interactionCamera.enabled = false;
        else
            _interactionCamera.enabled = true;

        PushCameraGlobals(force: true);
    }

    private void OnEnable()
    {
        if (_interactionRT != null) Shader.SetGlobalTexture(_rtId, _interactionRT);
        PushCameraGlobals(force: true);
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
        if (_interactionCamera == null || _target == null || _interactionRT == null)
            return;

        // Frequency gate (optional)
        if (_cameraDataUpdateHz > 0)
        {
            if (Time.unscaledTime < _nextUpdateTime) return;
            _nextUpdateTime = Time.unscaledTime + (1f / _cameraDataUpdateHz);
        }

        // Pixel snapping
        float orthoSize = _interactionCamera.orthographicSize;
        float worldSize = orthoSize * 2f;
        float invWorldSize = (worldSize > 0f) ? (1f / worldSize) : 0f;

        // RT_RES const 사용 (실 RT와 다르면 Validate에서 경고)
        float pixelSize = worldSize / RT_RES;

        Vector3 targetPos = _target.position;
        float snapX = Mathf.Round(targetPos.x / pixelSize) * pixelSize;
        float snapZ = Mathf.Round(targetPos.z / pixelSize) * pixelSize;

        if (_skipWhenSnapUnchanged)
        {
            bool sameSnap = Mathf.Abs(snapX - _lastSnapX) < 0.000001f && Mathf.Abs(snapZ - _lastSnapZ) < 0.000001f;
            bool sameOrtho = Mathf.Abs(orthoSize - _lastOrthoSize) < 0.000001f;

            // 스냅/파라미터 둘 다 그대로면 글로벌 갱신도, 카메라 이동도 스킵
            if (sameSnap && sameOrtho)
            {
                // 렌더 요청만 처리 (브러시가 같은 픽셀 안에서 찍힐 수도 있으니)
                MaybeRenderRT();
                return;
            }
        }

        _lastSnapX = snapX;
        _lastSnapZ = snapZ;
        _lastOrthoSize = orthoSize;

        _interactionCamera.transform.SetPositionAndRotation(
            new Vector3(snapX, _cameraHeight, snapZ),
            CamRotation
        );

        if (_eventDrivenRender && _autoDirtyOnCameraMove)
            MarkDirty(_autoDirtyFramesOnMove);

        // CamPosXZ: only changes when camera moves
        Shader.SetGlobalVector(_camPosXZId, new Vector4(snapX, snapZ, 0f, 0f));

        // CamParams: ortho/derived params
        Shader.SetGlobalVector(_camParamsId, new Vector4(orthoSize, worldSize, invWorldSize, pixelSize));

        // Render RT only when needed (big win)
        MaybeRenderRT();
    }

    private void MaybeRenderRT()
    {
        if (_bypassEventDriven) return;

        if (!_eventDrivenRender)
            return;

        if (_dirtyFrameCountdown <= 0)
            return;

        _dirtyFrameCountdown--;
        _interactionCamera.Render();
    }

    private void SetupCameraDefaults()
    {
        if (_interactionCamera == null) return;

        _interactionCamera.orthographic = true;
        _interactionCamera.transform.rotation = CamRotation;
        _interactionCamera.clearFlags = CameraClearFlags.SolidColor;
        _interactionCamera.backgroundColor = ComputeNeutralClearColor();
    }

    private Color ComputeNeutralClearColor()
    {
        if (_useManualNeutralCompensation)
            return new Color(_manualNeutralCompensatedRG, _manualNeutralCompensatedRG, 0f, 0f);

        return _neutralTarget;
    }

    private void PushCameraGlobals(bool force)
    {
        if (_interactionCamera == null) return;

        Vector3 camPos = _interactionCamera.transform.position;
        float orthoSize = _interactionCamera.orthographicSize;
        float worldSize = orthoSize * 2f;
        float invWorldSize = (worldSize > 0f) ? (1f / worldSize) : 0f;
        float pixelSize = worldSize / RT_RES;

        Shader.SetGlobalVector(_camPosXZId, new Vector4(camPos.x, camPos.z, 0f, 0f));
        Shader.SetGlobalVector(_camParamsId, new Vector4(orthoSize, worldSize, invWorldSize, pixelSize));

        if (force && _interactionRT != null)
            Shader.SetGlobalTexture(_rtId, _interactionRT);
    }

    private void ValidateRenderTexture(RenderTexture rt)
    {
        if (rt == null) return;

        if (rt.width != rt.height)
            Debug.LogWarning($"[InteractionMapBaker] RT is not square: {rt.width}x{rt.height}. Recommend square {RT_RES}x{RT_RES}.");

        if (rt.width != RT_RES || rt.height != RT_RES)
            Debug.LogWarning($"[InteractionMapBaker] RT is {rt.width}x{rt.height}, but code assumes RT_RES={RT_RES}. Update RT_RES or RT size.");

        // Data RT recommendations (warn only)
        if (rt.filterMode != FilterMode.Point)
            Debug.LogWarning($"[InteractionMapBaker] RT filterMode is {rt.filterMode}. For data maps, Point is usually recommended.");

        if (rt.wrapMode != TextureWrapMode.Clamp)
            Debug.LogWarning($"[InteractionMapBaker] RT wrapMode is {rt.wrapMode}. Clamp is usually recommended.");
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (_interactionCamera != null)
            _interactionCamera.backgroundColor = ComputeNeutralClearColor();

        if (Application.isPlaying)
        {
            if (_interactionRT != null) Shader.SetGlobalTexture(_rtId, _interactionRT);
            PushCameraGlobals(force: true);
        }
    }
#endif
}