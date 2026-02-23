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
    private int _lastUpdatedFrame = -1;

    [SerializeField] private bool _debugDisableCompute = false;

    void Awake()
    {
        if (_compute == null)
        {
            Debug.LogError("GrassPressGPU: ComputeShader not assigned");
            enabled = false;
            return;
        }

        _kernel = _compute.FindKernel("CSMain");
    }
    void OnDisable()
    {
        _posScaleBuffer?.Dispose();
        _posScaleBuffer = null;

        _pressBuffer?.Dispose();
        _pressBuffer = null;

        _instanceCount = 0;
    }

    void OnDestroy()
    {
        _posScaleBuffer?.Dispose();
        _posScaleBuffer = null;

        _pressBuffer?.Dispose();
        _pressBuffer = null;

        _instanceCount = 0;
    }

    /// <summary>
    /// GrassRenderer가 인스턴스 월드 위치+스케일 배열을 넘겨주는 함수
    /// </summary>
    public void SetInstances(Vector4[] posScale)
    {
        _instanceCount = (posScale != null) ? posScale.Length : 0;

        _posScaleBuffer?.Dispose();
        _pressBuffer?.Dispose();

        if (_instanceCount == 0)
            return;

        _posScaleBuffer = new ComputeBuffer(_instanceCount, sizeof(float) * 4);
        _pressBuffer = new ComputeBuffer(_instanceCount, sizeof(float));

        _posScaleBuffer.SetData(posScale);

        _compute.SetBuffer(_kernel, "_InstancePosScale", _posScaleBuffer);
        _compute.SetBuffer(_kernel, "_InstancePress01", _pressBuffer);
    }

    void LateUpdate()
    {
        if (_instanceCount == 0 ||
            _interactionCam == null ||
            _interactionRT == null ||
            _posScaleBuffer == null ||
            _pressBuffer == null)
            return;

        // 프레임당 1회만
        if (_lastUpdatedFrame == Time.frameCount)
            return;
        _lastUpdatedFrame = Time.frameCount;

        Matrix4x4 view = _interactionCam.worldToCameraMatrix;
        Matrix4x4 proj = _interactionCam.projectionMatrix;
        Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(proj, true); // renderIntoTexture=true
        Matrix4x4 vp = gpuProj * view;

        _compute.SetMatrix("_InteractionViewProj", vp);
        _compute.SetTexture(_kernel, "_InteractionRT", _interactionRT);
        _compute.SetInt("_InstanceCount", _instanceCount);

        int groups = (_instanceCount + 63) / 64;
        _compute.Dispatch(_kernel, groups, 1, 1);
    }
}