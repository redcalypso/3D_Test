using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public sealed class GrassPressGPU : MonoBehaviour, IScatterPressProvider
{
    [Header("References")]
    [SerializeField] private ComputeShader _compute;
    [SerializeField] private Camera _interactionCam;
    [SerializeField] private RenderTexture _interactionRT;

    [Header("Update Policy")]
    [Tooltip("true면 MarkDirty()가 호출된 프레임에만 Dispatch합니다.")]
    [SerializeField] private bool _eventDrivenDispatch = true;

    [Tooltip("MarkDirty 호출 후, 몇 프레임 동안 Dispatch를 유지할지.")]
    [SerializeField] private int _dirtyFrames = 2;

    [Tooltip("카메라/RT가 안 바뀌면 Dispatch를 스킵합니다.")]
    [SerializeField] private bool _skipWhenUnchanged = true;

    public ComputeBuffer PressBuffer => _pressBuffer;
    public int InstanceCount => _instanceCount;

    private ComputeBuffer _posScaleBuffer;
    private ComputeBuffer _pressBuffer;

    private int _kernel;
    private bool _kernelReady;

    private int _instanceCount;
    private int _capacity; // 버퍼 용량(실제 count는 capacity)

    private int _dirtyCountdown;

    // Cached state (to skip redundant Set* and Dispatch)
    private int _lastRTId;
    private Vector3 _lastCamPos;
    private Quaternion _lastCamRot;
    private float _lastOrthoSize;
    private float _lastAspect;
    private bool _lastOrtho;
    private int _lastInstanceCount;

    private static readonly int IdInstancePosScale = Shader.PropertyToID("_InstancePosScale");
    private static readonly int IdInstancePress01 = Shader.PropertyToID("_InstancePress01");
    private static readonly int IdInstanceCount = Shader.PropertyToID("_InstanceCount");

    private static readonly int IdInteractionRT = Shader.PropertyToID("_InteractionRT");
    private static readonly int IdInteractionViewProj = Shader.PropertyToID("_InteractionViewProj");
    private static readonly int IdInteractionCamPosXZ = Shader.PropertyToID("_InteractionCamPosXZ");
    private static readonly int IdInteractionCamParams = Shader.PropertyToID("_InteractionCamParams");

    // 외부에서 호출: InteractionMapBaker.MarkDirty와 같이 묶어서 호출하는 걸 추천
    public void MarkDirty(int frames = -1)
    {
        if (!_eventDrivenDispatch) return;
        int f = (frames > 0) ? frames : _dirtyFrames;
        if (f > _dirtyCountdown) _dirtyCountdown = f;
    }

    private void OnEnable()
    {
        EnsureKernel();
#if UNITY_EDITOR
        AssemblyReloadEvents.beforeAssemblyReload -= HandleBeforeAssemblyReload;
        AssemblyReloadEvents.beforeAssemblyReload += HandleBeforeAssemblyReload;
        EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
        EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
#endif
        // 켜질 때 1~2프레임은 강제로 한번 계산해주면 초기 검정/0 방지에 도움됨
        MarkDirty(2);
    }

    private void Awake()
    {
        EnsureKernel();
    }

    private void OnDisable()
    {
        ReleaseBuffers();
#if UNITY_EDITOR
        AssemblyReloadEvents.beforeAssemblyReload -= HandleBeforeAssemblyReload;
        EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
#endif
    }

    private void OnDestroy()
    {
        ReleaseBuffers();
#if UNITY_EDITOR
        AssemblyReloadEvents.beforeAssemblyReload -= HandleBeforeAssemblyReload;
        EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
#endif
    }

    private void OnApplicationQuit()
    {
        ReleaseBuffers();
    }

    private bool EnsureKernel()
    {
        if (_compute == null)
        {
            Debug.LogError("GrassPressGPU: ComputeShader not assigned.");
            _kernelReady = false;
            enabled = false;
            return false;
        }

        _kernel = _compute.FindKernel("CSMain");
        _kernelReady = true;
        return true;
    }

    private void ReleaseBuffers()
    {
        _posScaleBuffer?.Dispose();
        _posScaleBuffer = null;

        _pressBuffer?.Dispose();
        _pressBuffer = null;

        _instanceCount = 0;
        _capacity = 0;

        _dirtyCountdown = 0;
    }

    // 작은 변동에도 버퍼를 매번 갈지 말고, capacity로 넉넉히 잡고 재사용
    private static int NextCapacity(int needed)
    {
        // 0/1 방지
        int n = Mathf.Max(1, needed);
        // 2의 거듭제곱으로 올림(혹은 1.5배 성장 정책도 가능)
        n--;
        n |= n >> 1;
        n |= n >> 2;
        n |= n >> 4;
        n |= n >> 8;
        n |= n >> 16;
        n++;
        return n;
    }

    public void SetInstances(Vector4[] posScale)
    {
        _instanceCount = posScale != null ? posScale.Length : 0;

        if (_instanceCount <= 0)
        {
            // 0개면 press는 의미 없으니 릴리즈
            ReleaseBuffers();
            return;
        }

        if (!_kernelReady && !EnsureKernel())
        {
            ReleaseBuffers();
            return;
        }

        int neededCapacity = NextCapacity(_instanceCount);

        bool needRecreate =
            _posScaleBuffer == null ||
            _pressBuffer == null ||
            _capacity != neededCapacity;

        if (needRecreate)
        {
            _posScaleBuffer?.Dispose();
            _pressBuffer?.Dispose();

            _capacity = neededCapacity;
            _posScaleBuffer = new ComputeBuffer(_capacity, sizeof(float) * 4);
            _pressBuffer = new ComputeBuffer(_capacity, sizeof(float));

            _compute.SetBuffer(_kernel, IdInstancePosScale, _posScaleBuffer);
            _compute.SetBuffer(_kernel, IdInstancePress01, _pressBuffer);

            // 버퍼가 새로 만들어졌으면 한 번 계산이 필요함
            MarkDirty(2);
        }

        // 데이터는 실제 인스턴스만 업로드
        _posScaleBuffer.SetData(posScale);

        // 인스턴스가 바뀌었으니 press 재계산
        MarkDirty(2);
    }

    private void LateUpdate()
    {
        if (_instanceCount <= 0 ||
            _interactionCam == null ||
            _interactionRT == null ||
            _posScaleBuffer == null ||
            _pressBuffer == null)
            return;

        if (!_kernelReady && !EnsureKernel())
            return;

        // 이벤트 드리븐: dirty가 아니면 스킵
        if (_eventDrivenDispatch)
        {
            if (_dirtyCountdown <= 0)
                return;

            _dirtyCountdown--;
        }

        // 변경 감지: 아무 것도 안 바뀌면 Dispatch 스킵
        if (_skipWhenUnchanged)
        {
            int rtId = _interactionRT.GetInstanceID();

            bool camSame =
                _interactionCam.orthographic == _lastOrtho &&
                Mathf.Abs(_interactionCam.orthographicSize - _lastOrthoSize) < 0.000001f &&
                Mathf.Abs(_interactionCam.aspect - _lastAspect) < 0.000001f &&
                (_interactionCam.transform.position - _lastCamPos).sqrMagnitude < 0.0000001f &&
                Quaternion.Dot(_interactionCam.transform.rotation, _lastCamRot) > 0.999999f;

            bool rtSame = (rtId == _lastRTId);
            bool countSame = (_instanceCount == _lastInstanceCount);

            // eventDrivenDispatch=false일 때도, 이 조건이면 확실히 스킵 이득
            if (camSame && rtSame && countSame && !_eventDrivenDispatch)
                return;
        }

        // legacy global을 아직 쓰는 compute라면 유지(너희가 split global로 바꿨으면, 아래는 같이 바꿔야 함)
        // 현재 코드 그대로면 _InteractionCamData를 읽고 있으니 계속 SetVector 해줌
        // InteractionMapBaker가 SetGlobalVector로 넣어둔 "스냅된" 값을 그대로 가져와서 compute에 전달
        Vector4 camPosXZ = Shader.GetGlobalVector("_InteractionCamPosXZ");
        Vector4 camParams = Shader.GetGlobalVector("_InteractionCamParams");

        _compute.SetVector(IdInteractionCamPosXZ, camPosXZ);
        _compute.SetVector(IdInteractionCamParams, camParams);

        _compute.SetTexture(_kernel, IdInteractionRT, _interactionRT);
        _compute.SetInt(IdInstanceCount, _instanceCount);

        int groups = (_instanceCount + 63) / 64;
        _compute.Dispatch(_kernel, groups, 1, 1);

        // update caches
        _lastRTId = _interactionRT.GetInstanceID();
        _lastCamPos = _interactionCam.transform.position;
        _lastCamRot = _interactionCam.transform.rotation;
        _lastOrtho = _interactionCam.orthographic;
        _lastOrthoSize = _interactionCam.orthographicSize;
        _lastAspect = _interactionCam.aspect;
        _lastInstanceCount = _instanceCount;
    }

#if UNITY_EDITOR
    private void HandleBeforeAssemblyReload()
    {
        ReleaseBuffers();
    }

    private void HandlePlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.ExitingEditMode)
            ReleaseBuffers();
    }
#endif
}
