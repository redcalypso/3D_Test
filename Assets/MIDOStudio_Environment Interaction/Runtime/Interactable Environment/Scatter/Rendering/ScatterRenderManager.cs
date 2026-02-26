using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class ScatterRenderManager : MonoBehaviour
{
    [Header("Management")]
    [SerializeField] private bool autoDiscoverFields = true;
    [SerializeField] private bool autoDiscoverDuringPlay = false;
    [SerializeField, Min(0.1f)] private float refreshInterval = 1.0f;

    [Header("Render")]
    [Header("Debug")]
    [SerializeField] private int debugFieldCount;
    [SerializeField] private int debugFieldSourceCount;
    [SerializeField] private int debugRenderedBuckets;
    [SerializeField] private int debugDrawCalls;
    [SerializeField] private int debugSubmittedInstances;

    [SerializeField] private List<ScatterField> fields = new List<ScatterField>(128);
    private readonly List<RoomScatterDataSO.ChunkRef> _fieldChunkScratch = new List<RoomScatterDataSO.ChunkRef>(64);

    private double _nextRefreshTime;
    private readonly Dictionary<BucketKey, DrawBucket> _buckets = new Dictionary<BucketKey, DrawBucket>(64);
    private readonly Dictionary<FieldSourceKey, FieldSourceState> _fieldStates = new Dictionary<FieldSourceKey, FieldSourceState>(128);
    private readonly List<FieldSourceState> _activeFieldSources = new List<FieldSourceState>(128);

    private static readonly int IdPressColorWeight = Shader.PropertyToID("_PressColorWeight");
    private static readonly int IdBendColorWeight = Shader.PropertyToID("_BendColorWeight");
    private static readonly Plane[] s_frustumPlanes = new Plane[6];
    private static readonly Dictionary<Mesh, float> s_meshRadiusCache = new Dictionary<Mesh, float>(64);
    private static int s_frustumFrame = -1;
    private static int s_frustumCameraId = int.MinValue;
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

    private struct BucketKey : IEquatable<BucketKey>
    {
        public Mesh mesh;
        public Material material;
        public bool overrideColor;
        public int pressQ;
        public int bendQ;

        public static BucketKey Create(Mesh mesh, Material material, bool overrideColor, float press, float bend)
        {
            BucketKey key = default;
            key.mesh = mesh;
            key.material = material;
            key.overrideColor = overrideColor;
            if (overrideColor)
            {
                key.pressQ = Mathf.RoundToInt(Mathf.Clamp01(press) * 10000f);
                key.bendQ = Mathf.RoundToInt(Mathf.Clamp01(bend) * 10000f);
            }
            return key;
        }

        public bool Equals(BucketKey other)
        {
            return ReferenceEquals(mesh, other.mesh) &&
                   ReferenceEquals(material, other.material) &&
                   overrideColor == other.overrideColor &&
                   pressQ == other.pressQ &&
                   bendQ == other.bendQ;
        }

        public override bool Equals(object obj)
        {
            return obj is BucketKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (mesh != null ? mesh.GetHashCode() : 0);
                h = h * 31 + (material != null ? material.GetHashCode() : 0);
                h = h * 31 + (overrideColor ? 1 : 0);
                h = h * 31 + pressQ;
                h = h * 31 + bendQ;
                return h;
            }
        }
    }

    private sealed class DrawBucket
    {
        public Mesh mesh;
        public Material material;
        public MaterialPropertyBlock mpb;
        public readonly Matrix4x4[] matrices = new Matrix4x4[1023];
        public int count;
        public bool usedThisFrame;
    }

    private struct FieldSourceKey : IEquatable<FieldSourceKey>
    {
        public Transform root;
        public RoomScatterDataSO roomData;
        public ScatterSurfaceType surfaceType;
        public int chunkX;
        public int chunkY;

        public FieldSourceKey(Transform root, RoomScatterDataSO roomData, ScatterSurfaceType surfaceType, int chunkX, int chunkY)
        {
            this.root = root;
            this.roomData = roomData;
            this.surfaceType = surfaceType;
            this.chunkX = chunkX;
            this.chunkY = chunkY;
        }

        public bool Equals(FieldSourceKey other)
        {
            return ReferenceEquals(root, other.root) &&
                   ReferenceEquals(roomData, other.roomData) &&
                   surfaceType == other.surfaceType &&
                   chunkX == other.chunkX &&
                   chunkY == other.chunkY;
        }

        public override bool Equals(object obj)
        {
            return obj is FieldSourceKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (root != null ? root.GetHashCode() : 0);
                h = h * 31 + (roomData != null ? roomData.GetHashCode() : 0);
                h = h * 31 + (int)surfaceType;
                h = h * 31 + chunkX;
                h = h * 31 + chunkY;
                return h;
            }
        }
    }

    private sealed class FieldSourceState
    {
        public ScatterField field;
        public Transform root;
        public RoomScatterDataSO roomData;
        public RoomScatterDataSO.SurfaceLayerData surface;
        public RoomScatterDataSO.ChunkData chunk;
        public readonly List<Matrix4x4>[] lists = new List<Matrix4x4>[16];
        public CellRecord[] sortedCellsScratch;
        public int currentSettingsHash;
        public int currentDataVersion = -1;
        public bool listsDirty = true;
        public Bounds cachedWorldBounds;
        public int cachedBoundsHash;
        public bool hasCachedBounds;

        public FieldSourceState()
        {
            for (int i = 0; i < lists.Length; i++)
                lists[i] = new List<Matrix4x4>(1024);
        }
    }

    private void OnEnable()
    {
        _buckets.Clear();
        RefreshFieldSources(force: true);
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        _fieldStates.Clear();
        _activeFieldSources.Clear();
    }

    private void OnDestroy()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        _fieldStates.Clear();
        _activeFieldSources.Clear();
    }

    private void Update()
    {
        if (!autoDiscoverFields)
            return;
        if (Application.isPlaying && !autoDiscoverDuringPlay)
            return;

        if (Time.unscaledTimeAsDouble < _nextRefreshTime)
            return;

        RefreshFieldSources(force: false);
        _nextRefreshTime = Time.unscaledTimeAsDouble + refreshInterval;
    }

    [ContextMenu("Refresh Fields")]
    public void RefreshFieldsNow() => RefreshFieldSources(force: true);

    private void RefreshFieldSources(bool force)
    {
        if (force)
            _buckets.Clear();

#if UNITY_2023_1_OR_NEWER
        var found = UnityEngine.Object.FindObjectsByType<ScatterField>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
        var found = UnityEngine.Object.FindObjectsOfType<ScatterField>();
#endif

        fields.Clear();
        _activeFieldSources.Clear();
        _fieldStates.Clear();

        for (int i = 0; i < found.Length; i++)
        {
            ScatterField field = found[i];
            if (field == null)
                continue;

            fields.Add(field);
            _fieldChunkScratch.Clear();
            field.CollectChunkRefs(_fieldChunkScratch);
            for (int c = 0; c < _fieldChunkScratch.Count; c++)
                RegisterFieldSource(field, _fieldChunkScratch[c]);
        }

        debugFieldCount = fields.Count;
        debugFieldSourceCount = _activeFieldSources.Count;
    }

    private void RegisterFieldSource(ScatterField field, RoomScatterDataSO.ChunkRef chunkRef)
    {
        if (field == null || !field.HasRenderConfig)
            return;
        if (field.roomData == null || chunkRef.surface == null || chunkRef.chunk == null)
            return;

        FieldSourceKey key = new FieldSourceKey(
            field.transform,
            field.roomData,
            chunkRef.surface.surfaceType,
            chunkRef.chunk.chunkX,
            chunkRef.chunk.chunkY);
        if (!_fieldStates.TryGetValue(key, out FieldSourceState state))
        {
            state = new FieldSourceState();
            _fieldStates.Add(key, state);
        }

        state.field = field;
        state.root = field.transform;
        state.roomData = field.roomData;
        state.surface = chunkRef.surface;
        state.chunk = chunkRef.chunk;
        state.listsDirty = true;
        state.hasCachedBounds = false;
        _activeFieldSources.Add(state);
    }

    private void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
    {
        if (!PassesCameraFilters(cam))
            return;

        EnsureFrustumPlanes(cam);
        debugRenderedBuckets = 0;
        debugDrawCalls = 0;
        debugSubmittedInstances = 0;

        DrawIntegratedFromFields(cam);
    }

    private void DrawIntegratedFromFields(Camera cam)
    {
        for (int s = 0; s < _activeFieldSources.Count; s++)
        {
            FieldSourceState state = _activeFieldSources[s];
            ScatterField field = state.field;
            RoomScatterDataSO.SurfaceLayerData surface = state.surface;
            RoomScatterDataSO.ChunkData chunk = state.chunk;
            if (field == null || surface == null || chunk == null || state.root == null)
                continue;
            if (!field.HasRenderConfig || field.sharedMaterial == null)
                continue;
            if (!PassesFieldCameraFilters(field, cam))
                continue;

            if (!TryPrepareFieldSourceForCamera(state, field, cam, out int lodStride))
                continue;

            int variantCount = Mathf.Min(surface.EffectiveVariationCount, state.lists.Length);
            bool disableCullAndLodInEditorSceneView = !Application.isPlaying && cam.cameraType == CameraType.SceneView;
            bool useInstanceCull = !disableCullAndLodInEditorSceneView && field.enableInstanceCulling;
            for (int v = 0; v < variantCount; v++)
            {
                List<Matrix4x4> list = state.lists[v];
                if (list == null || list.Count == 0)
                    continue;

                Mesh mesh = field.variationMeshes[Mathf.Min(v, field.variationMeshes.Length - 1)];
                if (mesh == null)
                    continue;

                BucketKey key = BucketKey.Create(
                    mesh,
                    field.sharedMaterial,
                    field.overrideInteractionColorTuning,
                    field.pressColorWeight,
                    field.bendColorWeight);
                DrawBucket bucket = GetOrCreateBucket(in key);

                if (lodStride == 1 && !useInstanceCull)
                {
                    int src = 0;
                    int total = list.Count;
                    while (src < total)
                    {
                        int room = 1023 - bucket.count;
                        int copy = Mathf.Min(room, total - src);
                        list.CopyTo(src, bucket.matrices, bucket.count, copy);
                        src += copy;
                        bucket.count += copy;
                        bucket.usedThisFrame = true;
                        if (bucket.count < 1023)
                            continue;

                        FlushBucket(bucket);
                    }
                }
                else
                {
                    float instanceRadius = GetCachedMeshRadius(mesh) + field.instanceCullPadding;
                    for (int idx = 0; idx < list.Count; idx += lodStride)
                    {
                        Matrix4x4 m = list[idx];
                        if (useInstanceCull && !IsInstanceVisible(in m, instanceRadius))
                            continue;

                        AppendToBucket(bucket, in m);
                    }
                }
            }
        }

        FlushUsedBuckets();
    }

    private bool TryPrepareFieldSourceForCamera(FieldSourceState state, ScatterField field, Camera cam, out int lodStride)
    {
        lodStride = 0;
        if (state == null || field == null || cam == null)
            return false;

        EnsureFieldListsUpToDate(state);

        // In edit-mode SceneView, show full data without LOD/culling for authoring.
        if (!Application.isPlaying && cam.cameraType == CameraType.SceneView)
        {
            lodStride = 1;
            return true;
        }

        Bounds bounds = GetFieldChunkWorldBounds(state, field);
        if (!GeometryUtility.TestPlanesAABB(s_frustumPlanes, bounds))
            return false;

        lodStride = ComputeFieldDistanceLodStride(field, bounds, cam, out _);
        return lodStride > 0;
    }

    private static bool PassesFieldCameraFilters(ScatterField field, Camera cam)
    {
        if (field == null || cam == null)
            return false;

        bool isSceneViewCamera = cam.cameraType == CameraType.SceneView;

        if (!Application.isPlaying)
            return isSceneViewCamera;

        if (!isSceneViewCamera && !cam.isActiveAndEnabled)
            return false;

        bool isGameCamera = cam.cameraType == CameraType.Game;
        return isGameCamera || (field.renderInSceneViewWhilePlaying && isSceneViewCamera);
    }

    private void EnsureFieldListsUpToDate(FieldSourceState state)
    {
        if (!NeedsFieldListRebuild(state))
            return;

        BuildFieldListsDeterministic(state);
        state.currentSettingsHash = ComputeFieldSettingsHash(state);
        state.currentDataVersion = state.roomData != null ? state.roomData.DataVersion : -1;
        state.listsDirty = false;
        if (state.root != null)
            state.root.hasChanged = false;
    }

    private static bool NeedsFieldListRebuild(FieldSourceState state)
    {
        if (state == null)
            return false;
        if (state.listsDirty)
            return true;
        if (state.surface == null || state.chunk == null || state.roomData == null)
            return false;
        if (!Application.isPlaying)
            return true;
        if (state.root != null && state.root.hasChanged)
            return true;
        if (ComputeFieldSettingsHash(state) != state.currentSettingsHash)
            return true;
        return state.roomData.DataVersion != state.currentDataVersion;
    }

    private static int ComputeFieldSettingsHash(FieldSourceState state)
    {
        if (state == null || state.surface == null || state.chunk == null)
            return 0;
        unchecked
        {
            int h = state.surface.ComputeEffectiveSettingsHash();
            h = h * 31 + state.chunk.chunkX;
            h = h * 31 + state.chunk.chunkY;
            ScatterField field = state.field;
            if (field != null)
            {
                h = h * 31 + (field.projectToStaticSurface ? 1 : 0);
                h = h * 31 + field.projectionLayerMask.value;
                h = h * 31 + field.projectionRayStartHeight.GetHashCode();
                h = h * 31 + field.projectionRayDistance.GetHashCode();
                h = h * 31 + (field.alignToSurfaceNormal ? 1 : 0);
            }
            return h;
        }
    }

    private static void BuildFieldListsDeterministic(FieldSourceState state)
    {
        for (int i = 0; i < state.lists.Length; i++)
            state.lists[i].Clear();

        if (state.surface == null || state.chunk == null || state.root == null)
            return;

        RoomScatterDataSO.SurfaceLayerData surface = state.surface;
        RoomScatterDataSO.ChunkData chunk = state.chunk;
        float chunkSize = Mathf.Max(0.0001f, surface.chunkSize);
        float half = chunkSize * 0.5f;
        float cellSize = Mathf.Max(0.0001f, surface.cellSize);
        int variationCount = surface.EffectiveVariationCount;
        uint globalSeed = surface.EffectiveGlobalSeed;
        float scaleMin = surface.EffectiveScaleMin;
        float scaleMax = surface.EffectiveScaleMax;
        float chunkBaseX = chunk.chunkX * chunkSize;
        float chunkBaseZ = chunk.chunkY * chunkSize;

        List<CellRecord> src = chunk.cells;
        int n = src != null ? src.Count : 0;

        if (state.sortedCellsScratch == null || state.sortedCellsScratch.Length < n)
            state.sortedCellsScratch = new CellRecord[n];
        for (int i = 0; i < n; i++)
            state.sortedCellsScratch[i] = src[i];

        Array.Sort(state.sortedCellsScratch, 0, n, s_cellComparer);

        for (int i = 0; i < n; i++)
        {
            CellRecord rec = state.sortedCellsScratch[i];
            uint seed = ScatterHash.MakeSeed(globalSeed, rec.cx, rec.cy);
            Vector2 jitter = ScatterHash.Jitter(seed, cellSize * 0.35f);

            float x = chunkBaseX + ((int)rec.cx + 0.5f) * cellSize - half + jitter.x;
            float z = chunkBaseZ + ((int)rec.cy + 0.5f) * cellSize - half + jitter.y;
            float scale = rec.Scale(scaleMin, scaleMax);

            bool useProjectedPlacement = state.field != null && state.field.projectToStaticSurface;
            float localY = useProjectedPlacement ? rec.localY : 0f;
            Vector3 localPos = new Vector3(x, localY, z);
            Vector3 worldPos = state.root.TransformPoint(localPos);
            Quaternion worldRot = Quaternion.identity;
            if (useProjectedPlacement && state.field.alignToSurfaceNormal)
                worldRot = ComputeProjectedRotation(state.root, rec.localNormal);
            int variant = Mathf.Clamp(rec.variant, 0, variationCount - 1);
            if (variant < state.lists.Length)
                state.lists[variant].Add(Matrix4x4.TRS(worldPos, worldRot, Vector3.one * scale));
        }
    }

    private static Quaternion ComputeProjectedRotation(Transform root, Vector3 localNormal)
    {
        Vector3 normalLocal = localNormal.sqrMagnitude > 1e-6f ? localNormal.normalized : Vector3.up;
        Vector3 normalWorld = root != null ? root.TransformDirection(normalLocal).normalized : normalLocal;
        Vector3 forward = root != null ? root.forward : Vector3.forward;
        Vector3 tangent = Vector3.ProjectOnPlane(forward, normalWorld);
        if (tangent.sqrMagnitude < 1e-6f)
            tangent = Vector3.ProjectOnPlane(Vector3.forward, normalWorld);
        if (tangent.sqrMagnitude < 1e-6f)
            tangent = Vector3.right;

        return Quaternion.LookRotation(tangent.normalized, normalWorld);
    }

    private static Bounds GetFieldChunkWorldBounds(FieldSourceState state, ScatterField field)
    {
        int hash = ComputeFieldBoundsHash(state, field);
        if (state.hasCachedBounds && hash == state.cachedBoundsHash)
            return state.cachedWorldBounds;

        float half = Mathf.Max(0.001f, state.surface != null ? state.surface.chunkSize * 0.5f : 0.5f);
        float scaleMax = state.surface != null ? Mathf.Max(1f, state.surface.EffectiveScaleMax) : 1f;

        float meshExtentXZ = 0f;
        float meshExtentY = 0f;
        if (field.variationMeshes != null)
        {
            for (int i = 0; i < field.variationMeshes.Length; i++)
            {
                Mesh mesh = field.variationMeshes[i];
                if (mesh == null)
                    continue;

                Bounds b = mesh.bounds;
                meshExtentXZ = Mathf.Max(meshExtentXZ, b.extents.x, b.extents.z);
                meshExtentY = Mathf.Max(meshExtentY, Mathf.Abs(b.min.y), Mathf.Abs(b.max.y));
            }
        }

        float radiusXZ = half * 1.41421356f + meshExtentXZ * scaleMax + 0.5f;
        float extentY = meshExtentY * scaleMax + 2.0f;

        Vector3 localCenter = Vector3.zero;
        if (state.chunk != null && state.surface != null)
            localCenter = new Vector3(state.chunk.chunkX * state.surface.chunkSize, 0f, state.chunk.chunkY * state.surface.chunkSize);
        Vector3 center = state.root != null ? state.root.TransformPoint(localCenter) : Vector3.zero;
        Vector3 extents = new Vector3(radiusXZ, extentY, radiusXZ);

        state.cachedWorldBounds = new Bounds(center, extents * 2f);
        state.cachedBoundsHash = hash;
        state.hasCachedBounds = true;
        return state.cachedWorldBounds;
    }

    private static int ComputeFieldBoundsHash(FieldSourceState state, ScatterField field)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + (state.surface != null ? state.surface.GetHashCode() : 0);
            h = h * 31 + (state.chunk != null ? state.chunk.chunkX : 0);
            h = h * 31 + (state.chunk != null ? state.chunk.chunkY : 0);
            h = h * 31 + (state.surface != null ? state.surface.chunkSize.GetHashCode() : 0);
            h = h * 31 + (state.surface != null ? state.surface.EffectiveScaleMax.GetHashCode() : 0);
            h = h * 31 + (state.root != null ? state.root.localToWorldMatrix.GetHashCode() : 0);
            if (field.variationMeshes != null)
            {
                h = h * 31 + field.variationMeshes.Length;
                for (int i = 0; i < field.variationMeshes.Length; i++)
                {
                    Mesh m = field.variationMeshes[i];
                    if (m == null)
                        continue;
                    h = h * 31 + m.GetHashCode();
                    h = h * 31 + m.bounds.GetHashCode();
                }
            }
            return h;
        }
    }

    private static int ComputeFieldDistanceLodStride(ScatterField field, Bounds bounds, Camera cam, out float distance)
    {
        distance = 0f;
        if (!field.enableDistanceLod || cam == null)
            return 1;

        distance = Vector3.Distance(cam.transform.position, bounds.center);
        if (field.lodCullDistance > 0f && distance >= field.lodCullDistance)
            return 0;
        if (field.lodMidDistance > 0f && distance >= field.lodMidDistance)
            return Mathf.Max(1, field.lodMidStride);
        return 1;
    }

    private void FlushUsedBuckets()
    {
        foreach (var pair in _buckets)
        {
            DrawBucket bucket = pair.Value;
            if (!bucket.usedThisFrame)
                continue;

            FlushBucket(bucket);
            bucket.usedThisFrame = false;
            debugRenderedBuckets++;
        }
    }

    private DrawBucket GetOrCreateBucket(in BucketKey key)
    {
        if (_buckets.TryGetValue(key, out DrawBucket bucket))
            return bucket;

        bucket = new DrawBucket
        {
            mesh = key.mesh,
            material = key.material
        };

        if (key.overrideColor)
        {
            bucket.mpb = new MaterialPropertyBlock();
            bucket.mpb.SetFloat(IdPressColorWeight, key.pressQ / 10000f);
            bucket.mpb.SetFloat(IdBendColorWeight, key.bendQ / 10000f);
        }

        _buckets.Add(key, bucket);
        return bucket;
    }

    private void AppendToBucket(DrawBucket bucket, in Matrix4x4 matrix)
    {
        bucket.usedThisFrame = true;
        bucket.matrices[bucket.count++] = matrix;
        if (bucket.count >= 1023)
            FlushBucket(bucket);
    }

    private void FlushBucket(DrawBucket bucket)
    {
        if (bucket.count <= 0 || bucket.mesh == null || bucket.material == null)
            return;

        Graphics.DrawMeshInstanced(bucket.mesh, 0, bucket.material, bucket.matrices, bucket.count, bucket.mpb);
        debugDrawCalls++;
        debugSubmittedInstances += bucket.count;
        bucket.count = 0;
    }

    private static bool PassesCameraFilters(Camera cam)
    {
        if (cam == null)
            return false;

        if (!Application.isPlaying)
            return cam.cameraType == CameraType.SceneView;

        bool isSceneViewCamera = cam.cameraType == CameraType.SceneView;
        if (!isSceneViewCamera && !cam.isActiveAndEnabled)
            return false;

        if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection)
            return false;

        if (IsUrpOverlayCamera(cam))
            return false;

        return cam.cameraType == CameraType.Game || isSceneViewCamera;
    }

    private static void EnsureFrustumPlanes(Camera cam)
    {
        if (cam == null)
            return;

        int frame = Time.frameCount;
        int camId = cam.GetInstanceID();
        if (s_frustumFrame == frame && s_frustumCameraId == camId)
            return;

        GeometryUtility.CalculateFrustumPlanes(cam, s_frustumPlanes);
        s_frustumFrame = frame;
        s_frustumCameraId = camId;
    }

    private static bool IsInstanceVisible(in Matrix4x4 matrix, float radius)
    {
        Vector3 p = new Vector3(matrix.m03, matrix.m13, matrix.m23);
        for (int i = 0; i < 6; i++)
        {
            Plane pl = s_frustumPlanes[i];
            float d = Vector3.Dot(pl.normal, p) + pl.distance;
            if (d < -radius)
                return false;
        }
        return true;
    }

    private static float GetCachedMeshRadius(Mesh mesh)
    {
        if (mesh == null)
            return 0f;
        if (s_meshRadiusCache.TryGetValue(mesh, out float r))
            return r;

        Bounds b = mesh.bounds;
        r = Mathf.Max(b.extents.x, b.extents.y, b.extents.z);
        s_meshRadiusCache[mesh] = r;
        return r;
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
