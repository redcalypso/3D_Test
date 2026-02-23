using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
public sealed class GrassRenderer : MonoBehaviour
{
    public GrassChunkSO grass;
    public Mesh[] variationMeshes;      // size >= variationCount
    public Material sharedMaterial;

    private readonly List<Matrix4x4>[] _lists = new List<Matrix4x4>[16];
    private readonly Matrix4x4[] _tmp = new Matrix4x4[1023];

    [Header("Interaction / GPU Press")]
    [SerializeField] private GrassPressGPU _pressGPU;

    // GPU Press / 색상용
    private MaterialPropertyBlock _mpb;
    private Vector4[] _instancePosScale;
    private bool _pressInitialized;

    // 모든 GrassRenderer가 공유할 더미 버퍼 (press = 0)
    private static ComputeBuffer s_dummyPressBuffer;


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

        ReleaseDummyPressBuffer();
    }

    private void OnDestroy()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;

        ReleaseDummyPressBuffer();
    }

    private void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
    {
        RenderForCamera(cam);
    }

    private void RenderForCamera(Camera cam)
    {
        var camData = cam.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
        if (camData != null && camData.renderType == UnityEngine.Rendering.Universal.CameraRenderType.Overlay)
            return;

        if (grass == null || sharedMaterial == null) return;
        if (variationMeshes == null || variationMeshes.Length == 0) return;

        _mpb ??= new MaterialPropertyBlock();

        for (int i = 0; i < _lists.Length; i++)
            _lists[i].Clear();

        // ====== 기존 셀 → 행렬 빌드 로직 ======

        float half = grass.chunkSize * 0.5f;

        foreach (var rec in grass.cells)
        {
            // 셀 해시 기반 지터 (원래 코드 그대로)
            uint seed = GrassHash.MakeSeed(grass.globalSeed, rec.cx, rec.cy);
            Vector2 jitter = GrassHash.Jitter(seed, grass.cellSize * 0.35f);

            float x = (rec.cx + 0.5f) * grass.cellSize - half + jitter.x;
            float z = (rec.cy + 0.5f) * grass.cellSize - half + jitter.y;

            float scale = rec.Scale(grass.scaleMin, grass.scaleMax);

            Vector3 localPos = new Vector3(x, 0f, z);
            Vector3 worldPos = transform.TransformPoint(localPos);

            Quaternion rot = Quaternion.Euler(0f, 0f, 0f);
            Vector3 scl = Vector3.one * scale;

            _lists[rec.variant].Add(Matrix4x4.TRS(worldPos, rot, scl));
        }

        // ====== 여기서부터 GPU Press 연동 ======

        // 1) 첫 프레임에만 posScale → GrassPressGPU로 보내기
        EnsurePressSetup();          // PressGPU 인스턴스 데이터 세팅
        EnsureDummyPressBuffer();    // 더미 버퍼는 항상 준비

        ComputeBuffer pressBuffer =
            (_pressGPU != null && _pressGPU.PressBuffer != null)
            ? _pressGPU.PressBuffer
            : s_dummyPressBuffer;

        int baseIndex = 0;

        // total instances in this chunk (should match press buffer instance count)
        int totalInstanceCount = 0;
        for (int vv = 0; vv < grass.variationCount; vv++) totalInstanceCount += _lists[vv].Count;

        int globalBatchIndex = 0;

        for (int v = 0; v < grass.variationCount; v++)
        {
            var list = _lists[v];
            if (list == null || list.Count == 0)
            {
                continue;
            }

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
                _mpb.SetBuffer("_InstancePress01", pressBuffer);
                _mpb.SetInt("_BaseInstanceIndex", baseIndex + offset);
                _mpb.SetInt("_PressCount", pressBuffer != null ? pressBuffer.count : 1);

                Graphics.DrawMeshInstanced(
                    mesh, 0, sharedMaterial,
                    _tmp, count, _mpb
                );

                globalBatchIndex++;

                offset += count;
            }
            baseIndex += list.Count;
        }
    }

    // GrassPressGPU와 posScale 초기화
    private void EnsurePressSetup()
    {
        if (_pressInitialized || _pressGPU == null || grass == null)
            return;

        _mpb ??= new MaterialPropertyBlock();

        var posScaleList = new List<Vector4>();

        // _lists 내용(셀별 행렬) 기반으로 월드 위치만 추출
        for (int v = 0; v < grass.variationCount && v < _lists.Length; v++)
        {
            var list = _lists[v];
            if (list == null) continue;

            for (int i = 0; i < list.Count; i++)
            {
                Matrix4x4 m = list[i];
                Vector3 pos = m.GetColumn(3); // TRS의 translation 컬럼

                // scale은 지금 Press 계산에는 안 쓰이니까 1f로 둬도 됨
                posScaleList.Add(new Vector4(pos.x, pos.y, pos.z, 1f));
            }
        }

        _instancePosScale = posScaleList.ToArray();
        _pressGPU.SetInstances(_instancePosScale);
        _pressInitialized = true;
    }

    private static void EnsureDummyPressBuffer()
    {
        if (s_dummyPressBuffer != null) return;

        s_dummyPressBuffer = new ComputeBuffer(1, sizeof(float));
        s_dummyPressBuffer.SetData(new float[] { 0f });
    }

    private static void ReleaseDummyPressBuffer()
    {
        if (s_dummyPressBuffer != null)
        {
            s_dummyPressBuffer.Dispose();
            s_dummyPressBuffer = null;
        }
    }
}