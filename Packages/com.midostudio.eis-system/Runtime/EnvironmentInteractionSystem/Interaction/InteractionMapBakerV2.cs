using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class InteractionMapBakerV2 : MonoBehaviour
{
    private struct StampRequest
    {
        public Vector3 worldPos;
        public Vector3 dir;
        public Vector3 arcForward;
        public float sizeMul;
        public float strengthMul;
        public bool useArcMask;
        public float arcAngle;
        public float arcSoftness;
        public EISStampPreset preset;
    }

    [Header("References")]
    [SerializeField] private Transform _target;
    [SerializeField] private Shader _globalRelaxationShader;
    [SerializeField] private Shader _stampShader;
    [SerializeField] private RenderTexture _interactionRT;

    [Header("Field Window")]
    [SerializeField] private float _orthoSize = 50f;
    [SerializeField] private bool _useLateUpdate = true;
    [SerializeField] private int _cameraDataUpdateHz = 0;
    [SerializeField] private bool _snapToPixelGrid = true;
    [SerializeField] private bool _skipWhenSnapUnchanged = true;

    [Header("Tick Policy")]
    [SerializeField] private bool _eventDrivenTick = false;
    [SerializeField] private int _dirtyFrames = 2;
    [SerializeField] private bool _autoDirtyOnMove = true;
    [SerializeField] private int _autoDirtyFramesOnMove = 2;
    [SerializeField] private int _maxStampsPerTick = 32;

    [Header("Relaxation")]
    [SerializeField] private float _decayRate = 3.0f;
    [SerializeField] private float _dtCap = 0.05f;
    [SerializeField] private bool _runRelaxationWhenIdle = true;
    [SerializeField] private Color _neutralColor = new Color(0.5f, 0.5f, 0f, 0f);

    [Header("RT")]
    [SerializeField] private int _rtResolution = 256;
    [SerializeField] private RenderTextureFormat _rtFormat = RenderTextureFormat.ARGBHalf;
    [SerializeField] private bool _allowFormatFallbackToARGB32 = true;
    [SerializeField] private FilterMode _rtFilter = FilterMode.Point;
    [SerializeField] private TextureWrapMode _rtWrap = TextureWrapMode.Clamp;

    [Header("Shader Globals (Compatibility)")]
    [SerializeField] private string _rtGlobalName = "_InteractionRT";
    [SerializeField] private string _camPosXZGlobalName = "_InteractionCamPosXZ";
    [SerializeField] private string _camParamsGlobalName = "_InteractionCamParams";
    [SerializeField] private string _uvOffsetGlobalName = "_InteractionUVOffset";

    [Header("Debug")]
    [SerializeField] private int _debugQueueLength;

    private static readonly int IdPrevRT = Shader.PropertyToID("_PrevRT");
    private static readonly int IdDecayRate = Shader.PropertyToID("_DecayRate");
    private static readonly int IdDeltaTime = Shader.PropertyToID("_DeltaTime");
    private static readonly int IdUVOffset = Shader.PropertyToID("_UVOffset");
    private static readonly int IdNeutralColor = Shader.PropertyToID("_NeutralColor");
    private static readonly int IdMaskTex = Shader.PropertyToID("_MaskTex");
    private static readonly int IdStampCenter = Shader.PropertyToID("_StampCenter");
    private static readonly int IdStampDir = Shader.PropertyToID("_StampDir");
    private static readonly int IdStampSize = Shader.PropertyToID("_StampSize");
    private static readonly int IdStampStrength = Shader.PropertyToID("_StampStrength");
    private static readonly int IdForceMultiplier = Shader.PropertyToID("_ForceMultiplier");
    private static readonly int IdPressMultiplier = Shader.PropertyToID("_PressMultiplier");
    private static readonly int IdWeightMultiplier = Shader.PropertyToID("_WeightMultiplier");
    private static readonly int IdMaxForce = Shader.PropertyToID("_MaxForce");
    private static readonly int IdDirectionMode = Shader.PropertyToID("_DirectionMode");
    private static readonly int IdStampMode = Shader.PropertyToID("_StampMode");
    private static readonly int IdUseArcMask = Shader.PropertyToID("_UseArcMask");
    private static readonly int IdArcAngle = Shader.PropertyToID("_ArcAngle");
    private static readonly int IdArcSoftness = Shader.PropertyToID("_ArcSoftness");
    private static readonly int IdArcForward = Shader.PropertyToID("_ArcForward");

    private int _rtId;
    private int _camPosXZId;
    private int _camParamsId;
    private int _uvOffsetId;

    private RenderTexture _rtA;
    private RenderTexture _rtB;
    private RenderTexture _prevRT;
    private RenderTexture _currRT;
    private Material _relaxationMaterial;
    private Material _stampMaterial;
    private readonly Queue<StampRequest> _stampQueue = new Queue<StampRequest>(128);

    private float _nextUpdateTime;
    private float _lastSnapX = float.NaN;
    private float _lastSnapZ = float.NaN;
    private float _lastOrthoSize = float.NaN;
    private int _dirtyCountdown;

    public RenderTexture CurrentRT => _prevRT;

    public void RequestStamp(Vector3 worldPos, Vector3 dir, float sizeMul, float strengthMul, EISStampPreset preset)
    {
        if (preset == null)
            return;

        RequestStamp(
            worldPos,
            dir,
            sizeMul,
            strengthMul,
            preset,
            preset.useArcMask,
            preset.arcAngle,
            preset.arcSoftness,
            dir);
    }

    public void RequestStamp(
        Vector3 worldPos,
        Vector3 dir,
        float sizeMul,
        float strengthMul,
        EISStampPreset preset,
        bool useArcMask,
        float arcAngle,
        float arcSoftness,
        Vector3 arcForward)
    {
        if (preset == null || preset.maskTex == null)
            return;

        Vector3 safeArcForward = arcForward.sqrMagnitude > 0.000001f ? arcForward.normalized : Vector3.forward;
        StampRequest req = new StampRequest
        {
            worldPos = worldPos,
            dir = dir,
            arcForward = safeArcForward,
            sizeMul = Mathf.Max(0f, sizeMul),
            strengthMul = Mathf.Max(0f, strengthMul),
            useArcMask = useArcMask,
            arcAngle = Mathf.Clamp(arcAngle, 0f, 360f),
            arcSoftness = Mathf.Max(0f, arcSoftness),
            preset = preset
        };

        _stampQueue.Enqueue(req);
        _debugQueueLength = _stampQueue.Count;

        if (_eventDrivenTick)
            MarkDirty(_dirtyFrames);
    }

    public void MarkDirty(int frames = -1)
    {
        if (!_eventDrivenTick)
            return;

        int f = (frames > 0) ? frames : _dirtyFrames;
        if (f > _dirtyCountdown)
            _dirtyCountdown = f;
    }

    private void Awake()
    {
        EnsureGlobalPropertyIds();
        EnsureRuntimeResources();
        PushGlobals(forceTexture: true, snapX: 0f, snapZ: 0f, uvOffset: Vector2.zero);
    }

    private void OnEnable()
    {
        EnsureGlobalPropertyIds();
        EnsureRuntimeResources();
        PushGlobals(forceTexture: true, snapX: 0f, snapZ: 0f, uvOffset: Vector2.zero);
    }

    private void OnDisable()
    {
        ReleaseRuntimeResources();
    }

    private void OnDestroy()
    {
        ReleaseRuntimeResources();
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
        if (_target == null)
            return;
        if (!EnsureRuntimeResources())
            return;

        if (_cameraDataUpdateHz > 0)
        {
            if (Time.unscaledTime < _nextUpdateTime)
                return;

            _nextUpdateTime = Time.unscaledTime + (1f / _cameraDataUpdateHz);
        }

        float orthoSize = Mathf.Max(0.0001f, _orthoSize);
        float worldSize = orthoSize * 2f;
        float invWorldSize = 1f / worldSize;
        float pixelSize = worldSize / Mathf.Max(1, _rtResolution);

        Vector3 targetPos = _target.position;
        float snapX = _snapToPixelGrid ? Mathf.Round(targetPos.x / pixelSize) * pixelSize : targetPos.x;
        float snapZ = _snapToPixelGrid ? Mathf.Round(targetPos.z / pixelSize) * pixelSize : targetPos.z;

        Vector2 uvOffset = Vector2.zero;
        bool moved = false;
        if (!float.IsNaN(_lastSnapX) && !float.IsNaN(_lastSnapZ))
        {
            float dx = snapX - _lastSnapX;
            float dz = snapZ - _lastSnapZ;
            uvOffset = new Vector2(dx * invWorldSize, dz * invWorldSize);
            moved = Mathf.Abs(dx) > 0.000001f || Mathf.Abs(dz) > 0.000001f;
        }

        bool sameSnap = Mathf.Abs(snapX - _lastSnapX) < 0.000001f && Mathf.Abs(snapZ - _lastSnapZ) < 0.000001f;
        bool sameOrtho = Mathf.Abs(orthoSize - _lastOrthoSize) < 0.000001f;
        bool hasPendingStamps = _stampQueue.Count > 0;
        if (_skipWhenSnapUnchanged && sameSnap && sameOrtho && !_eventDrivenTick && !hasPendingStamps)
        {
            PushGlobals(forceTexture: true, snapX: snapX, snapZ: snapZ, uvOffset: Vector2.zero);
            return;
        }

        _lastSnapX = snapX;
        _lastSnapZ = snapZ;
        _lastOrthoSize = orthoSize;

        if (_autoDirtyOnMove && _eventDrivenTick && moved)
            MarkDirty(_autoDirtyFramesOnMove);

        if (_eventDrivenTick)
        {
            if (_dirtyCountdown <= 0 && !hasPendingStamps)
            {
                if (_runRelaxationWhenIdle && _decayRate > 0f)
                {
                    float idleDt = Mathf.Min(Time.unscaledDeltaTime, Mathf.Max(0.0001f, _dtCap));
                    RunRelaxationPass(idleDt, uvOffset);
                    SwapRT();

                    _interactionRT = _prevRT;
                    Shader.SetGlobalTexture(_rtId, _interactionRT);
                    Shader.SetGlobalVector(_camPosXZId, new Vector4(snapX, snapZ, 0f, 0f));
                    Shader.SetGlobalVector(_camParamsId, new Vector4(orthoSize, worldSize, invWorldSize, pixelSize));
                    Shader.SetGlobalVector(_uvOffsetId, new Vector4(uvOffset.x, uvOffset.y, 0f, 0f));
                    _debugQueueLength = _stampQueue.Count;
                }
                else
                {
                    PushGlobals(forceTexture: true, snapX: snapX, snapZ: snapZ, uvOffset: uvOffset);
                }
                return;
            }

            if (_dirtyCountdown > 0)
                _dirtyCountdown--;
        }

        float dt = Mathf.Min(Time.unscaledDeltaTime, Mathf.Max(0.0001f, _dtCap));
        RunRelaxationPass(dt, uvOffset);
        SwapRT();
        RunStampPass(snapX, snapZ);

        _interactionRT = _prevRT;
        Shader.SetGlobalTexture(_rtId, _interactionRT);
        Shader.SetGlobalVector(_camPosXZId, new Vector4(snapX, snapZ, 0f, 0f));
        Shader.SetGlobalVector(_camParamsId, new Vector4(orthoSize, worldSize, invWorldSize, pixelSize));
        Shader.SetGlobalVector(_uvOffsetId, new Vector4(uvOffset.x, uvOffset.y, 0f, 0f));
        _debugQueueLength = _stampQueue.Count;
    }

    private void RunRelaxationPass(float dt, Vector2 uvOffset)
    {
        _relaxationMaterial.SetTexture(IdPrevRT, _prevRT);
        _relaxationMaterial.SetFloat(IdDecayRate, Mathf.Max(0f, _decayRate));
        _relaxationMaterial.SetFloat(IdDeltaTime, Mathf.Max(0f, dt));
        _relaxationMaterial.SetVector(IdUVOffset, new Vector4(uvOffset.x, uvOffset.y, 0f, 0f));
        _relaxationMaterial.SetColor(IdNeutralColor, _neutralColor);

        Graphics.Blit(_prevRT, _currRT, _relaxationMaterial, 0);
    }

    private void RunStampPass(float snapX, float snapZ)
    {
        if (_stampMaterial == null || _stampQueue.Count <= 0)
            return;

        int processCount = Mathf.Min(Mathf.Max(0, _maxStampsPerTick), _stampQueue.Count);
        for (int i = 0; i < processCount; i++)
        {
            StampRequest req = _stampQueue.Dequeue();
            if (req.preset == null || req.preset.maskTex == null)
                continue;

            float stampSize = Mathf.Max(0.0001f, req.preset.baseSize * Mathf.Max(0f, req.sizeMul));
            float stampStrength = Mathf.Max(0f, req.preset.baseStrength * Mathf.Max(0f, req.strengthMul));
            Vector3 dir = req.dir.sqrMagnitude > 0.000001f ? req.dir.normalized : Vector3.forward;

            _stampMaterial.SetTexture(IdPrevRT, _prevRT);
            _stampMaterial.SetTexture(IdMaskTex, req.preset.maskTex);
            _stampMaterial.SetVector(IdStampCenter, new Vector4(req.worldPos.x, req.worldPos.y, req.worldPos.z, 0f));
            _stampMaterial.SetVector(IdStampDir, new Vector4(dir.x, dir.y, dir.z, 0f));
            _stampMaterial.SetFloat(IdStampSize, stampSize);
            _stampMaterial.SetFloat(IdStampStrength, stampStrength);
            _stampMaterial.SetFloat(IdForceMultiplier, req.preset.forceMultiplier);
            _stampMaterial.SetFloat(IdPressMultiplier, req.preset.pressMultiplier);
            _stampMaterial.SetFloat(IdWeightMultiplier, req.preset.weightMultiplier);
            _stampMaterial.SetFloat(IdMaxForce, Mathf.Max(0.0001f, req.preset.maxForce));
            _stampMaterial.SetFloat(IdDirectionMode, (int)req.preset.directionMode);
            _stampMaterial.SetInt(IdStampMode, (int)req.preset.blendMode);
            _stampMaterial.SetFloat(IdUseArcMask, req.useArcMask ? 1f : 0f);
            _stampMaterial.SetFloat(IdArcAngle, req.arcAngle);
            _stampMaterial.SetFloat(IdArcSoftness, req.arcSoftness);
            _stampMaterial.SetVector(IdArcForward, new Vector4(req.arcForward.x, req.arcForward.y, req.arcForward.z, 0f));
            _stampMaterial.SetVector("_CurrentCamXZ", new Vector4(snapX, snapZ, 0f, 0f));

            Graphics.Blit(_prevRT, _currRT, _stampMaterial, 0);
            SwapRT();
        }
    }

    private void SwapRT()
    {
        RenderTexture tmp = _prevRT;
        _prevRT = _currRT;
        _currRT = tmp;
    }

    private bool EnsureRuntimeResources()
    {
        EnsureGlobalPropertyIds();

        if (_globalRelaxationShader == null)
            _globalRelaxationShader = Shader.Find("Hidden/Interaction/GlobalRelaxation");
        if (_globalRelaxationShader == null)
            return false;
        if (_stampShader == null)
            _stampShader = Shader.Find("Hidden/Interaction/Stamp");
        if (_stampShader == null)
            return false;

        if (_relaxationMaterial == null)
        {
            _relaxationMaterial = new Material(_globalRelaxationShader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
        }
        if (_stampMaterial == null)
        {
            _stampMaterial = new Material(_stampShader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        RenderTextureFormat fmt = _rtFormat;
        if (!SystemInfo.SupportsRenderTextureFormat(fmt))
            fmt = _allowFormatFallbackToARGB32 ? RenderTextureFormat.ARGB32 : _rtFormat;

        int res = Mathf.Max(1, _rtResolution);
        if (!IsRTValid(_rtA, res, fmt))
        {
            ReleaseRT(ref _rtA);
            _rtA = CreateRT(res, fmt, "_InteractionRT_A");
        }

        if (!IsRTValid(_rtB, res, fmt))
        {
            ReleaseRT(ref _rtB);
            _rtB = CreateRT(res, fmt, "_InteractionRT_B");
        }

        if (_prevRT == null || _currRT == null)
        {
            _prevRT = _rtA;
            _currRT = _rtB;
            ClearRT(_prevRT, _neutralColor);
            ClearRT(_currRT, _neutralColor);
        }

        if (_interactionRT == null)
            _interactionRT = _prevRT;

        return true;
    }

    private void PushGlobals(bool forceTexture, float snapX, float snapZ, Vector2 uvOffset)
    {
        float orthoSize = Mathf.Max(0.0001f, _orthoSize);
        float worldSize = orthoSize * 2f;
        float invWorldSize = 1f / worldSize;
        float pixelSize = worldSize / Mathf.Max(1, _rtResolution);

        if (forceTexture && _prevRT != null)
            Shader.SetGlobalTexture(_rtId, _prevRT);

        Shader.SetGlobalVector(_camPosXZId, new Vector4(snapX, snapZ, 0f, 0f));
        Shader.SetGlobalVector(_camParamsId, new Vector4(orthoSize, worldSize, invWorldSize, pixelSize));
        Shader.SetGlobalVector(_uvOffsetId, new Vector4(uvOffset.x, uvOffset.y, 0f, 0f));
    }

    private void EnsureGlobalPropertyIds()
    {
        if (string.IsNullOrWhiteSpace(_rtGlobalName))
            _rtGlobalName = "_InteractionRT";
        if (string.IsNullOrWhiteSpace(_camPosXZGlobalName))
            _camPosXZGlobalName = "_InteractionCamPosXZ";
        if (string.IsNullOrWhiteSpace(_camParamsGlobalName))
            _camParamsGlobalName = "_InteractionCamParams";
        if (string.IsNullOrWhiteSpace(_uvOffsetGlobalName))
            _uvOffsetGlobalName = "_InteractionUVOffset";

        _rtId = Shader.PropertyToID(_rtGlobalName);
        _camPosXZId = Shader.PropertyToID(_camPosXZGlobalName);
        _camParamsId = Shader.PropertyToID(_camParamsGlobalName);
        _uvOffsetId = Shader.PropertyToID(_uvOffsetGlobalName);
    }

    private RenderTexture CreateRT(int res, RenderTextureFormat fmt, string name)
    {
        var rt = new RenderTexture(res, res, 0, fmt)
        {
            name = name,
            filterMode = _rtFilter,
            wrapMode = _rtWrap,
            useMipMap = false,
            autoGenerateMips = false
        };
        rt.Create();
        return rt;
    }

    private static bool IsRTValid(RenderTexture rt, int expectedRes, RenderTextureFormat expectedFmt)
    {
        return rt != null &&
               rt.width == expectedRes &&
               rt.height == expectedRes &&
               rt.format == expectedFmt;
    }

    private static void ClearRT(RenderTexture rt, Color color)
    {
        if (rt == null)
            return;

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(false, true, color);
        RenderTexture.active = prev;
    }

    private void ReleaseRuntimeResources()
    {
        _interactionRT = null;
        _prevRT = null;
        _currRT = null;
        ReleaseRT(ref _rtA);
        ReleaseRT(ref _rtB);

        if (_relaxationMaterial != null)
        {
            if (Application.isPlaying)
                Destroy(_relaxationMaterial);
            else
                DestroyImmediate(_relaxationMaterial);
            _relaxationMaterial = null;
        }
        if (_stampMaterial != null)
        {
            if (Application.isPlaying)
                Destroy(_stampMaterial);
            else
                DestroyImmediate(_stampMaterial);
            _stampMaterial = null;
        }
        _stampQueue.Clear();
        _debugQueueLength = 0;
    }

    private static void ReleaseRT(ref RenderTexture rt)
    {
        if (rt == null)
            return;
        rt.Release();
        if (Application.isPlaying)
            Destroy(rt);
        else
            DestroyImmediate(rt);
        rt = null;
    }
}
