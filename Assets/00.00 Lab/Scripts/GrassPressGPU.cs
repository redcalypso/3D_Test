using UnityEngine;

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

    private static readonly int IdKernelCSMain = Shader.PropertyToID("CSMain");
    private static readonly int IdKernelClear = Shader.PropertyToID("Clear");

    private static readonly int IdInstancePosScale = Shader.PropertyToID("_InstancePosScale");
    private static readonly int IdInstancePress01 = Shader.PropertyToID("_InstancePress01");
    private static readonly int IdInstanceCount = Shader.PropertyToID("_InstanceCount");

    private static readonly int IdInteractionRT = Shader.PropertyToID("_InteractionRT");
    private static readonly int IdInteractionViewProj = Shader.PropertyToID("_InteractionViewProj");
    private static readonly int IdInteractionCamData = Shader.PropertyToID("_InteractionCamData");

    private void Awake()
    {
        if (_compute == null)
        {
            Debug.LogError("GrassPressGPU: ComputeShader not assigned.");
            enabled = false;
            return;
        }

        _kernel = _compute.FindKernel("CSMain");
    }

    private void OnDisable()
    {
        ReleaseBuffers();
    }

    private void OnDestroy()
    {
        ReleaseBuffers();
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

        _posScaleBuffer?.Dispose();
        _pressBuffer?.Dispose();

        if (_instanceCount <= 0)
        {
            _posScaleBuffer = null;
            _pressBuffer = null;
            return;
        }

        _posScaleBuffer = new ComputeBuffer(_instanceCount, sizeof(float) * 4);
        _pressBuffer = new ComputeBuffer(_instanceCount, sizeof(float));

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

        // CamData는 Baker가 글로벌로 박아둔 값 사용
        Vector4 camData = Shader.GetGlobalVector("_InteractionCamData");
        _compute.SetVector(IdInteractionCamData, camData);

        // VP 계산
        Matrix4x4 view = _interactionCam.worldToCameraMatrix;
        Matrix4x4 proj = _interactionCam.projectionMatrix;
        Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(proj, true);
        Matrix4x4 vp = gpuProj * view;

        _compute.SetMatrix(IdInteractionViewProj, vp);
        _compute.SetTexture(_kernel, IdInteractionRT, _interactionRT);

        // IMPORTANT: uint로 통일 (음수 방지)
        uint countU = (uint)Mathf.Max(0, _instanceCount);
        _compute.SetInt(IdInstanceCount, (int)countU);

        int groups = (_instanceCount + 63) / 64;
        _compute.Dispatch(_kernel, groups, 1, 1);
    }
}