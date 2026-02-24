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

    [Header("Render Options")]
    [SerializeField] private bool renderOnlyGameCamera = false;

    private readonly List<Matrix4x4>[] _lists = new List<Matrix4x4>[16];
    private readonly Matrix4x4[] _tmp = new Matrix4x4[1023];
    private CellRecord[] _sortedCellsScratch;

    private MaterialPropertyBlock _mpb;
    private Vector4[] _instancePosScale;

    // Press 버퍼 동기화 캐시(마지막으로 동기화된 인스턴스 순서/개수)
    private int _cachedOrderHash;
    private int _cachedTotalCount;
    // 렌더 리스트 캐시(현재 빌드된 인스턴스 순서/개수)
    private int _currentOrderHash;
    private int _currentTotalCount;
    private int _currentGrassSettingsHash;
    private int _currentCellCount = -1;
    private bool _listsDirty = true;

    // Dummy press buffer (press = 0)
    private static ComputeBuffer s_dummyPressBuffer;

    private static readonly int IdInstancePress01 = Shader.PropertyToID("_InstancePress01");
    private static readonly int IdInstancePressCount = Shader.PropertyToID("_InstancePressCount");
    private static readonly int IdBaseInstanceIndex = Shader.PropertyToID("_BaseInstanceIndex");
    private static readonly IComparer<CellRecord> s_cellRecordComparer = Comparer<CellRecord>.Create((a, b) =>
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

    private void OnEnable()
    {
        _listsDirty = true;

        for (int i = 0; i < _lists.Length; i++)
            _lists[i] ??= new List<Matrix4x4>(1024);

        _mpb ??= new MaterialPropertyBlock();

        EnsureDummyPressBuffer();
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        _pressGPU?.SetInstances(null);
        ReleaseManagedCaches();
        ReleaseDummyPressBuffer();
    }

    private void OnDestroy()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        _pressGPU?.SetInstances(null);
        ReleaseManagedCaches();
        ReleaseDummyPressBuffer();
    }

    private void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
    {
        RenderForCamera(cam);
    }

    private void OnValidate()
    {
        _listsDirty = true;
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

        // 1) 플레이 중에는 변경 시에만 리스트 재빌드
        EnsureListsUpToDate();
        int orderHash = _currentOrderHash;
        int totalCount = _currentTotalCount;

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

        if (_sortedCellsScratch == null || _sortedCellsScratch.Length < n)
            _sortedCellsScratch = new CellRecord[n];

        for (int i = 0; i < n; i++) _sortedCellsScratch[i] = src[i];

        Array.Sort(_sortedCellsScratch, 0, n, s_cellRecordComparer);

        unchecked
        {
            int h = 17;
            h = h * 31 + grass.variationCount;
            h = h * 31 + n;
            h = h * 31 + transform.localToWorldMatrix.GetHashCode();

            for (int i = 0; i < n; i++)
            {
                var rec = _sortedCellsScratch[i];

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
        if (totalCount <= 0)
        {
            _instancePosScale = null;
            _pressGPU.SetInstances(null);
            return;
        }

        if (_instancePosScale == null || _instancePosScale.Length != totalCount)
            _instancePosScale = new Vector4[totalCount];

        int write = 0;

        int vCount = Mathf.Min(grass.variationCount, _lists.Length);
        for (int v = 0; v < vCount; v++)
        {
            var list = _lists[v];
            if (list == null) continue;

            for (int i = 0; i < list.Count; i++)
            {
                Vector3 pos = list[i].GetColumn(3);
                _instancePosScale[write++] = new Vector4(pos.x, pos.y, pos.z, 1f);
            }
        }

        if (write != totalCount)
        {
            Array.Resize(ref _instancePosScale, write);
            totalCount = write;
        }

        if (totalCount <= 0)
        {
            _pressGPU.SetInstances(null);
            return;
        }

        _pressGPU.SetInstances(_instancePosScale);
    }

    private void EnsureListsUpToDate()
    {
        if (!NeedsListRebuild())
            return;

        BuildListsDeterministic(out _currentOrderHash, out _currentTotalCount);

        _currentGrassSettingsHash = ComputeGrassSettingsHash();
        _currentCellCount = grass != null && grass.cells != null ? grass.cells.Count : 0;
        _listsDirty = false;
        transform.hasChanged = false;
    }

    private bool NeedsListRebuild()
    {
        if (_listsDirty)
            return true;

        if (grass == null)
            return false;

        // 에디터(비플레이)에서는 브러시 편집 즉시 반영을 위해 매 프레임 갱신
        if (!Application.isPlaying)
            return true;

        if (transform.hasChanged)
            return true;

        int settingsHash = ComputeGrassSettingsHash();
        if (settingsHash != _currentGrassSettingsHash)
            return true;

        int cellCount = grass.cells != null ? grass.cells.Count : 0;
        return cellCount != _currentCellCount;
    }

    private int ComputeGrassSettingsHash()
    {
        if (grass == null)
            return 0;

        unchecked
        {
            int h = 17;
            h = h * 31 + grass.variationCount;
            h = h * 31 + grass.globalSeed.GetHashCode();
            h = h * 31 + grass.chunkSize.GetHashCode();
            h = h * 31 + grass.cellSize.GetHashCode();
            h = h * 31 + grass.scaleMin.GetHashCode();
            h = h * 31 + grass.scaleMax.GetHashCode();
            return h;
        }
    }

    private void ReleaseManagedCaches()
    {
        _instancePosScale = null;
        _sortedCellsScratch = null;
        _cachedOrderHash = 0;
        _cachedTotalCount = 0;
        _currentOrderHash = 0;
        _currentTotalCount = 0;
        _currentGrassSettingsHash = 0;
        _currentCellCount = -1;
        _listsDirty = true;
        _mpb = null;

        for (int i = 0; i < _lists.Length; i++)
        {
            var list = _lists[i];
            if (list == null)
                continue;

            list.Clear();
            list.TrimExcess();
        }
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


