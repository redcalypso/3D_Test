using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public sealed class GrassPressGPU : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ComputeShader _compute;
    [SerializeField] private Camera _interactionCam;
    [SerializeField] private RenderTexture _interactionRT;

    public ComputeBuffer PressBuffer => _pressBuffer;
    public int InstanceCount => _instanceCount;

    private ComputeBuffer _posScaleBuffer;
    private ComputeBuffer _pressBuffer;
    private int _kernel;
    private int _instanceCount;
    private bool _kernelReady;

    private static readonly int IdInstancePosScale = Shader.PropertyToID("_InstancePosScale");
    private static readonly int IdInstancePress01 = Shader.PropertyToID("_InstancePress01");
    private static readonly int IdInstanceCount = Shader.PropertyToID("_InstanceCount");

    private static readonly int IdInteractionRT = Shader.PropertyToID("_InteractionRT");
    private static readonly int IdInteractionViewProj = Shader.PropertyToID("_InteractionViewProj");
    private static readonly int IdInteractionCamData = Shader.PropertyToID("_InteractionCamData");

    private void OnEnable()
    {
        EnsureKernel();
#if UNITY_EDITOR
        AssemblyReloadEvents.beforeAssemblyReload -= HandleBeforeAssemblyReload;
        AssemblyReloadEvents.beforeAssemblyReload += HandleBeforeAssemblyReload;
        EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
        EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
#endif
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
    }

    public void SetInstances(Vector4[] posScale)
    {
        _instanceCount = posScale != null ? posScale.Length : 0;

        if (_instanceCount <= 0)
        {
            ReleaseBuffers();
            return;
        }

        if (!_kernelReady && !EnsureKernel())
        {
            ReleaseBuffers();
            return;
        }

        bool needRecreate =
            _posScaleBuffer == null ||
            _pressBuffer == null ||
            _posScaleBuffer.count != _instanceCount ||
            _pressBuffer.count != _instanceCount;

        if (needRecreate)
        {
            _posScaleBuffer?.Dispose();
            _pressBuffer?.Dispose();

            _posScaleBuffer = new ComputeBuffer(_instanceCount, sizeof(float) * 4);
            _pressBuffer = new ComputeBuffer(_instanceCount, sizeof(float));
        }

        _posScaleBuffer.SetData(posScale);

        _compute.SetBuffer(_kernel, IdInstancePosScale, _posScaleBuffer);
        _compute.SetBuffer(_kernel, IdInstancePress01, _pressBuffer);
    }

    private void LateUpdate()
    {
        if (_instanceCount <= 0 ||
            _interactionCam == null ||
            _interactionRT == null ||
            _posScaleBuffer == null ||
            _pressBuffer == null)
        {
            return;
        }

        if (!_kernelReady && !EnsureKernel())
            return;

        Vector4 camData = Shader.GetGlobalVector("_InteractionCamData");
        _compute.SetVector(IdInteractionCamData, camData);

        Matrix4x4 view = _interactionCam.worldToCameraMatrix;
        Matrix4x4 proj = _interactionCam.projectionMatrix;
        Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(proj, true);
        Matrix4x4 vp = gpuProj * view;

        _compute.SetMatrix(IdInteractionViewProj, vp);
        _compute.SetTexture(_kernel, IdInteractionRT, _interactionRT);
        _compute.SetInt(IdInstanceCount, _instanceCount);

        int groups = (_instanceCount + 63) / 64;
        _compute.Dispatch(_kernel, groups, 1, 1);
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