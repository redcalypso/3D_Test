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

    private void OnEnable()
    {
        for (int i = 0; i < _lists.Length; i++)
            _lists[i] ??= new List<Matrix4x4>(1024);

        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
    }

    private void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
    {
        // 씬/게임뷰 둘 다 그리기(원하면 여기서 cam 타입 필터 가능)
        RenderForCamera(cam);
    }

    private void RenderForCamera(Camera cam)
    {
        if (grass == null || sharedMaterial == null) return;
        if (variationMeshes == null || variationMeshes.Length == 0) return;
        if (grass.variationCount <= 0) return;

        // 머티리얼 인스턴싱 켜져있어야 함(머티리얼 체크박스)
        // sharedMaterial.enableInstancing = true; // 강제하고 싶으면 주석 해제

        for (int i = 0; i < grass.variationCount; i++)
            _lists[i].Clear();

        int cellsPerAxis = grass.CellsPerAxis;
        float half = grass.chunkSize * 0.5f;

        // NOTE: 지금은 지터를 CellRecord에 저장 안 했으니까 “센터 기반”으로만 그림.
        // (지터까지 에디터에서도 똑같이 보이게 하려면 seed로 jitter를 여기서도 재생성하면 됨)
        foreach (var rec in grass.cells)
        {
            uint seed = GrassHash.MakeSeed(grass.globalSeed, rec.cx, rec.cy);
            Vector2 jitter = GrassHash.Jitter(seed, grass.cellSize * 0.35f);

            float x = (rec.cx + 0.5f) * grass.cellSize - half + jitter.x;
            float z = (rec.cy + 0.5f) * grass.cellSize - half + jitter.y; ;

            float scale = rec.Scale(grass.scaleMin, grass.scaleMax);

            Vector3 localPos = new Vector3(x, 0f, z);
            Vector3 worldPos = transform.TransformPoint(localPos);

            Quaternion rot = Quaternion.Euler(0f, 0f, 0f);
            Vector3 scl = Vector3.one * scale;

            _lists[rec.variant].Add(Matrix4x4.TRS(worldPos, rot, scl));
        }

        // Draw batches (no GC)
        for (int v = 0; v < grass.variationCount; v++)
        {
            var list = _lists[v];
            if (list.Count == 0) continue;

            Mesh mesh = variationMeshes[Mathf.Min(v, variationMeshes.Length - 1)];
            if (mesh == null) continue;

            int offset = 0;
            while (offset < list.Count)
            {
                int count = Mathf.Min(1023, list.Count - offset);
                for (int i = 0; i < count; i++)
                    _tmp[i] = list[offset + i];

                Graphics.DrawMeshInstanced(mesh, 0, sharedMaterial, _tmp, count);
                offset += count;
            }
        }
    }
}