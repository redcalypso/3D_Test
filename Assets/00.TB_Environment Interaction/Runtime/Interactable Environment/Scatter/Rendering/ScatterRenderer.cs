using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
public sealed class ScatterRenderer : MonoBehaviour
{
    public ScatterChunkSO chunk;
    public Mesh[] variationMeshes;
    public Material sharedMaterial;

    [Header("Render Options")]
    [SerializeField] private bool renderInSceneViewWhilePlaying = true;

    [Header("Interaction Color Tuning")]
    [SerializeField] private bool _overrideInteractionColorTuning = false;
    [SerializeField, Range(0f, 1f)] private float _pressColorWeight = 0f;
    [SerializeField, Range(0f, 1f)] private float _bendColorWeight = 0f;

    private readonly List<Matrix4x4>[] _lists = new List<Matrix4x4>[16];
    private readonly Matrix4x4[] _tmp = new Matrix4x4[1023];
    private CellRecord[] _sortedCellsScratch;

    private MaterialPropertyBlock _mpb;

    private int _currentOrderHash;
    private int _currentTotalCount;
    private int _currentSettingsHash;
    private int _currentDataVersion = -1;
    private bool _listsDirty = true;
    private int _lastRenderFrame = -1;
    private int _lastRenderCameraId = int.MinValue;

    private static readonly int IdPressColorWeight = Shader.PropertyToID("_PressColorWeight");
    private static readonly int IdBendColorWeight = Shader.PropertyToID("_BendColorWeight");
    private static readonly IComparer<CellRecord> s_cellComparer = Comparer<CellRecord>.Create((a, b) =>
    {
        int va = a.variant;
        int vb = b.variant;
        if (va != vb) return va.CompareTo(vb);
        int cxa = a.cx;
        int cxb = b.cx;
        if (cxa != cxb) return cxa.CompareTo(cxb);
        return a.cy.CompareTo(b.cy);
    });

    public void ApplyLegacyConfig(
        ScatterChunkSO sourceChunk,
        Mesh[] sourceVariationMeshes,
        Material sourceSharedMaterial,
        MonoBehaviour sourcePressProviderComponent,
        bool sourceRenderOnlyGameCamera)
    {
        chunk = sourceChunk;
        variationMeshes = sourceVariationMeshes;
        sharedMaterial = sourceSharedMaterial;
        renderInSceneViewWhilePlaying = !sourceRenderOnlyGameCamera;
        _listsDirty = true;
    }

    private void OnEnable()
    {
        _listsDirty = true;
        for (int i = 0; i < _lists.Length; i++)
            _lists[i] ??= new List<Matrix4x4>(1024);

        _mpb ??= new MaterialPropertyBlock();

        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        ReleaseManagedCaches();
    }

    private void OnDestroy()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        ReleaseManagedCaches();
    }

    private void OnValidate()
    {
        _listsDirty = true;
    }

    private void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
    {
        RenderForCamera(cam);
    }

    private void RenderForCamera(Camera cam)
    {
        if (cam == null)
            return;

        bool isSceneViewCamera = cam.cameraType == CameraType.SceneView;
        if (!isSceneViewCamera && !cam.isActiveAndEnabled)
            return;

        if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection)
            return;

        if (IsUrpOverlayCamera(cam))
            return;

        if (!Application.isPlaying)
        {
            if (cam.cameraType != CameraType.SceneView)
                return;
        }
        else
        {
            bool isGameCamera = cam.cameraType == CameraType.Game;
            if (!isGameCamera && !(renderInSceneViewWhilePlaying && isSceneViewCamera))
                return;
        }

        if (chunk == null || sharedMaterial == null)
            return;
        if (variationMeshes == null || variationMeshes.Length == 0)
            return;

        int frame = Time.frameCount;
        int camId = cam.GetInstanceID();
        if (_lastRenderFrame == frame && _lastRenderCameraId == camId)
            return;
        _lastRenderFrame = frame;
        _lastRenderCameraId = camId;

        _mpb ??= new MaterialPropertyBlock();
        EnsureListsUpToDate();

        _mpb.Clear();
        if (_overrideInteractionColorTuning)
        {
            _mpb.SetFloat(IdPressColorWeight, Mathf.Clamp01(_pressColorWeight));
            _mpb.SetFloat(IdBendColorWeight, Mathf.Clamp01(_bendColorWeight));
        }

        int vCount = Mathf.Min(chunk.EffectiveVariationCount, _lists.Length);
        for (int v = 0; v < vCount; v++)
        {
            var list = _lists[v];
            if (list == null || list.Count == 0)
                continue;

            Mesh mesh = variationMeshes[Mathf.Min(v, variationMeshes.Length - 1)];
            if (mesh == null)
                continue;

            int offset = 0;
            while (offset < list.Count)
            {
                int count = Mathf.Min(1023, list.Count - offset);
                for (int i = 0; i < count; i++)
                    _tmp[i] = list[offset + i];

                Graphics.DrawMeshInstanced(mesh, 0, sharedMaterial, _tmp, count, _mpb);
                offset += count;
            }
        }
    }

    private void BuildListsDeterministic(out int orderHash, out int totalCount)
    {
        for (int i = 0; i < _lists.Length; i++)
            _lists[i].Clear();

        float half = chunk.chunkSize * 0.5f;
        float cellSize = chunk.cellSize;
        int variationCount = chunk.EffectiveVariationCount;
        uint globalSeed = chunk.EffectiveGlobalSeed;
        float scaleMin = chunk.EffectiveScaleMin;
        float scaleMax = chunk.EffectiveScaleMax;
        var src = chunk.cells;
        int n = src != null ? src.Count : 0;

        if (_sortedCellsScratch == null || _sortedCellsScratch.Length < n)
            _sortedCellsScratch = new CellRecord[n];
        for (int i = 0; i < n; i++)
            _sortedCellsScratch[i] = src[i];

        Array.Sort(_sortedCellsScratch, 0, n, s_cellComparer);

        unchecked
        {
            int h = 17;
            h = h * 31 + variationCount;
            h = h * 31 + n;
            h = h * 31 + transform.localToWorldMatrix.GetHashCode();

            for (int i = 0; i < n; i++)
            {
                var rec = _sortedCellsScratch[i];
                uint seed = ScatterHash.MakeSeed(globalSeed, rec.cx, rec.cy);
                Vector2 jitter = ScatterHash.Jitter(seed, cellSize * 0.35f);

                float x = ((int)rec.cx + 0.5f) * cellSize - half + jitter.x;
                float z = ((int)rec.cy + 0.5f) * cellSize - half + jitter.y;
                float scale = rec.Scale(scaleMin, scaleMax);

                Vector3 worldPos = transform.TransformPoint(new Vector3(x, 0f, z));
                int variant = Mathf.Clamp(rec.variant, 0, variationCount - 1);

                if (variant < _lists.Length)
                    _lists[variant].Add(Matrix4x4.TRS(worldPos, Quaternion.identity, Vector3.one * scale));

                h = h * 31 + variant;
                h = h * 31 + rec.cx;
                h = h * 31 + rec.cy;
            }

            orderHash = h;
        }

        totalCount = 0;
        int countV = Mathf.Min(chunk.EffectiveVariationCount, _lists.Length);
        for (int v = 0; v < countV; v++)
            totalCount += _lists[v].Count;
    }

    private void EnsureListsUpToDate()
    {
        if (!NeedsListRebuild())
            return;

        BuildListsDeterministic(out _currentOrderHash, out _currentTotalCount);
        _currentSettingsHash = ComputeSettingsHash();
        _currentDataVersion = chunk != null ? chunk.DataVersion : -1;
        _listsDirty = false;
        transform.hasChanged = false;
    }

    private bool NeedsListRebuild()
    {
        if (_listsDirty)
            return true;
        if (chunk == null)
            return false;
        if (!Application.isPlaying)
            return true;
        if (transform.hasChanged)
            return true;
        if (ComputeSettingsHash() != _currentSettingsHash)
            return true;
        return chunk.DataVersion != _currentDataVersion;
    }

    private int ComputeSettingsHash()
    {
        if (chunk == null)
            return 0;
        return chunk.ComputeEffectiveSettingsHash();
    }

    private void ReleaseManagedCaches()
    {
        _sortedCellsScratch = null;
        _currentOrderHash = 0;
        _currentTotalCount = 0;
        _currentSettingsHash = 0;
        _currentDataVersion = -1;
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

    private static bool IsUrpOverlayCamera(Camera cam)
    {
        if (cam == null)
            return false;

        var additionalData = cam.GetComponent("UniversalAdditionalCameraData");
        if (additionalData == null)
            return false;

        var renderTypeProp = additionalData.GetType().GetProperty("renderType");
        if (renderTypeProp == null)
            return false;

        object renderTypeValue = renderTypeProp.GetValue(additionalData, null);
        return renderTypeValue != null &&
               string.Equals(renderTypeValue.ToString(), "Overlay", StringComparison.Ordinal);
    }
}