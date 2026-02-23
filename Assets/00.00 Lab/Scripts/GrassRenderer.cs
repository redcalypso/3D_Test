using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
public sealed class GrassRenderer : MonoBehaviour
{
    public GrassChunkSO grass;
    public Mesh[] variationMeshes;      // size >= variationCount
    public Material sharedMaterial;

    [Header("Interaction / GPU Press")]
    [SerializeField] private GrassPressGPU _pressGPU;
    [SerializeField, Range(0f, 1f)] private float _pressColorWeight = 0.35f;

    [Header("Render Options")]
    [SerializeField] private bool renderOnlyGameCamera = false;

    private readonly List<Matrix4x4>[] _lists = new List<Matrix4x4>[16];
    private readonly Matrix4x4[] _tmp = new Matrix4x4[1023];

    private MaterialPropertyBlock _mpb;
    private Vector4[] _instancePosScale;

    // 캐시: "순서"가 바뀌었는지 감지하기 위한 해시
    private int _cachedOrderHash;
    private int _cachedTotalCount;

    // Dummy press buffer (press = 0)
    private static ComputeBuffer s_dummyPressBuffer;

    private static readonly int IdInstancePress01 = Shader.PropertyToID("_InstancePress01");
    private static readonly int IdInstancePressCount = Shader.PropertyToID("_InstancePressCount");
    private static readonly int IdBaseInstanceIndex = Shader.PropertyToID("_BaseInstanceIndex");
    private static readonly int IdPressColorWeight = Shader.PropertyToID("_PressColorWeight");

    private void OnEnable()
    {
        for (int i = 0; i < _lists.Length; i++)
            _lists[i] ??= new List<Matrix4x4>(1024);

        _mpb ??= new MaterialPropertyBlock();

        EnsureDummyPressBuffer();
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        _pressGPU?.SetInstances(null);
        ReleaseDummyPressBuffer();
    }

    private void OnDestroy()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        _pressGPU?.SetInstances(null);
        ReleaseDummyPressBuffer();
    }

    private void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
    {
        RenderForCamera(cam);
    }

    private void RenderForCamera(Camera cam)
    {
        // Overlay 카메라 제외 (URP stack)
        var urpData = cam.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
        if (urpData != null && urpData.renderType == UnityEngine.Rendering.Universal.CameraRenderType.Overlay)
            return;

        // 필요하면 Game 카메라만 렌더 (SceneView가 순서 꼬임 만드는지 검사할 때 유용)
        if (renderOnlyGameCamera && cam.cameraType != CameraType.Game)
            return;

        if (grass == null || sharedMaterial == null) return;
        if (variationMeshes == null || variationMeshes.Length == 0) return;

        _mpb ??= new MaterialPropertyBlock();

        // 1) 리스트를 매번 "결정론적 순서"로 빌드
        BuildListsDeterministic(out int orderHash, out int totalCount);

        // 2) 순서/개수가 바뀌면 PressBuffer 인스턴스 순서도 동일하게 재빌드
        if (_pressGPU != null && (orderHash != _cachedOrderHash || totalCount != _cachedTotalCount))
        {
            RebuildPressInstances(totalCount);
            _cachedOrderHash = orderHash;
            _cachedTotalCount = totalCount;
        }

        EnsureDummyPressBuffer();

        ComputeBuffer pressBuffer =
            (_pressGPU != null && _pressGPU.PressBuffer != null)
                ? _pressGPU.PressBuffer
                : s_dummyPressBuffer;

        int pressCount = pressBuffer != null ? pressBuffer.count : 1;

        // 3) Draw — sharedMaterial 상태를 건드리지 말고 MPB로만 세팅
        int baseIndex = 0;

        int vCount = Mathf.Min(grass.variationCount, _lists.Length);
        for (int v = 0; v < vCount; v++)
        {
            var list = _lists[v];
            if (list == null || list.Count == 0)
                continue;

            Mesh mesh = variationMeshes[Mathf.Min(v, variationMeshes.Length - 1)];
            if (mesh == null)
            {
                baseIndex += list.Count;
                continue;
            }

            int offset = 0;
            while (offset < list.Count)
            {
                int count = Mathf.Min(1023, list.Count - offset);
                for (int i = 0; i < count; i++)
                    _tmp[i] = list[offset + i];

                _mpb.Clear();
                _mpb.SetBuffer(IdInstancePress01, pressBuffer);
                _mpb.SetFloat(IdInstancePressCount, pressCount);
                _mpb.SetFloat(IdBaseInstanceIndex, baseIndex + offset);
                _mpb.SetFloat(IdPressColorWeight, Mathf.Clamp01(_pressColorWeight));

                Graphics.DrawMeshInstanced(mesh, 0, sharedMaterial, _tmp, count, _mpb);

                offset += count;
            }

            baseIndex += list.Count;
        }
    }

    private void BuildListsDeterministic(out int orderHash, out int totalCount)
    {
        for (int i = 0; i < _lists.Length; i++)
            _lists[i].Clear();

        float half = grass.chunkSize * 0.5f;
        int cellsPerAxis = grass.CellsPerAxis;

        // CellRecord 배열로 복사해서 정렬 (variant -> cx -> cy)
        var src = grass.cells;
        int n = src != null ? src.Count : 0;

        CellRecord[] sorted = new CellRecord[n];
        for (int i = 0; i < n; i++) sorted[i] = src[i];

        Array.Sort(sorted, (a, b) =>
        {
            int va = a.variant;
            int vb = b.variant;
            if (va != vb) return va.CompareTo(vb);

            int cxa = a.cx;
            int cxb = b.cx;
            if (cxa != cxb) return cxa.CompareTo(cxb);

            int cya = a.cy;
            int cyb = b.cy;
            return cya.CompareTo(cyb);
        });

        unchecked
        {
            int h = 17;
            h = h * 31 + grass.variationCount;
            h = h * 31 + n;
            h = h * 31 + transform.localToWorldMatrix.GetHashCode();

            for (int i = 0; i < n; i++)
            {
                var rec = sorted[i];

                uint seed = GrassHash.MakeSeed(grass.globalSeed, rec.cx, rec.cy);
                Vector2 jitter = GrassHash.Jitter(seed, grass.cellSize * 0.35f);

                float x = ((int)rec.cx + 0.5f) * grass.cellSize - half + jitter.x;
                float z = ((int)rec.cy + 0.5f) * grass.cellSize - half + jitter.y;

                float scale = rec.Scale(grass.scaleMin, grass.scaleMax);

                Vector3 localPos = new Vector3(x, 0f, z);
                Vector3 worldPos = transform.TransformPoint(localPos);

                int variant = Mathf.Clamp(rec.variant, 0, grass.variationCount - 1);
                if (variant < _lists.Length)
                    _lists[variant].Add(Matrix4x4.TRS(worldPos, Quaternion.identity, Vector3.one * scale));

                // 해시에 순서 키를 포함(중요!)
                h = h * 31 + variant;
                h = h * 31 + rec.cx;
                h = h * 31 + rec.cy;
            }

            orderHash = h;
        }

        totalCount = 0;
        int vCount = Mathf.Min(grass.variationCount, _lists.Length);
        for (int v = 0; v < vCount; v++)
            totalCount += _lists[v].Count;
    }

    private void RebuildPressInstances(int totalCount)
    {
        if (_pressGPU == null) return;

        var posScaleList = new List<Vector4>(totalCount);

        int vCount = Mathf.Min(grass.variationCount, _lists.Length);
        for (int v = 0; v < vCount; v++)
        {
            var list = _lists[v];
            if (list == null) continue;

            for (int i = 0; i < list.Count; i++)
            {
                Vector3 pos = list[i].GetColumn(3);
                posScaleList.Add(new Vector4(pos.x, pos.y, pos.z, 1f));
            }
        }

        _instancePosScale = posScaleList.ToArray();
        _pressGPU.SetInstances(_instancePosScale);
    }

    private static void EnsureDummyPressBuffer()
    {
        if (s_dummyPressBuffer != null) return;

        s_dummyPressBuffer = new ComputeBuffer(1, sizeof(float));
        s_dummyPressBuffer.SetData(new float[] { 0f });
    }

    private static void ReleaseDummyPressBuffer()
    {
        if (s_dummyPressBuffer == null) return;

        s_dummyPressBuffer.Dispose();
        s_dummyPressBuffer = null;
    }
}


