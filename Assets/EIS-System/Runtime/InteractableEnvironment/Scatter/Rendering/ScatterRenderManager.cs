using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
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
    [SerializeField] private InteractionMapBakerV2 interactionMapBaker;

    [SerializeField] private List<ScatterField> fields = new List<ScatterField>(128);
    private readonly List<RoomScatterDataSO.ChunkRef> _fieldChunkScratch = new List<RoomScatterDataSO.ChunkRef>(64);

    private double _nextRefreshTime;
    private readonly Dictionary<BucketKey, DrawBucket> _buckets = new Dictionary<BucketKey, DrawBucket>(64);
    private readonly Dictionary<FieldSourceKey, FieldSourceState> _fieldStates = new Dictionary<FieldSourceKey, FieldSourceState>(128);
    private readonly List<FieldSourceState> _activeFieldSources = new List<FieldSourceState>(128);
    private readonly List<FieldSourceKey> _staleFieldSourceKeys = new List<FieldSourceKey>(32);
    private readonly Dictionary<RenderConfigWarningKey, string> _renderConfigWarnings = new Dictionary<RenderConfigWarningKey, string>(32);
    private InteractionMapBakerV2 _registeredInteractionMapBaker;

    private static readonly int IdPressColorWeight = Shader.PropertyToID("_PressColorWeight");
    private static readonly int IdBendColorWeight = Shader.PropertyToID("_BendColorWeight");
    private static readonly Plane[] s_frustumPlanes = new Plane[6];
    private static readonly Dictionary<Mesh, float> s_meshRadiusCache = new Dictionary<Mesh, float>(64);
    private const float DefaultPebblePushStrength = 0.08f;
    private const float DefaultPebbleMaxDisplacement = 0.20f;
    private const float DefaultPebbleRadius = 0.02f;
    private const float DefaultPebbleRollSpeed = 0.4f;
    private const float DefaultPebbleAngularSpeed = 720f;
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
        public int currentTransformHash;
        public bool listsDirty = true;
        public bool listsReady = false;
        public Bounds cachedWorldBounds;
        public int cachedBoundsHash;
        public bool hasCachedBounds;
        public readonly Dictionary<int, int> cellIndexByKey = new Dictionary<int, int>(128);
        public int cellLookupDataVersion = -1;
        public int cellLookupCellCount = -1;
        public int cellLookupCellsPerAxis = -1;
        public int pebbleRuntimeDataVersion = -1;
        public bool pebbleTransitionActive = false;
        public int lastPebbleResetLogFrame = -1;
        public int lastPebbleStampLogFrame = -1;
        public int lastPebbleTransitionLogFrame = -1;
        public int lastPebbleBuildLogFrame = -1;

        public FieldSourceState()
        {
            for (int i = 0; i < lists.Length; i++)
                lists[i] = new List<Matrix4x4>(64);
        }
    }

    private readonly struct RenderConfigWarningKey : IEquatable<RenderConfigWarningKey>
    {
        public readonly ScatterField field;
        public readonly ScatterSurfaceType surfaceType;

        public RenderConfigWarningKey(ScatterField field, ScatterSurfaceType surfaceType)
        {
            this.field = field;
            this.surfaceType = surfaceType;
        }

        public bool Equals(RenderConfigWarningKey other)
        {
            return ReferenceEquals(field, other.field) && surfaceType == other.surfaceType;
        }

        public override bool Equals(object obj)
        {
            return obj is RenderConfigWarningKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (field != null ? field.GetHashCode() : 0);
                h = h * 31 + (int)surfaceType;
                return h;
            }
        }
    }

    private void OnEnable()
    {
        EnsurePebbleStampHandlerRegistered();
        _buckets.Clear();
        RefreshFieldSources(force: true);
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    private void OnDisable()
    {
        UnregisterPebbleStampHandler();
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        _renderConfigWarnings.Clear();
        _fieldStates.Clear();
        _activeFieldSources.Clear();
    }

    private void OnDestroy()
    {
        UnregisterPebbleStampHandler();
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        _renderConfigWarnings.Clear();
        _fieldStates.Clear();
        _activeFieldSources.Clear();
    }

    private void Update()
    {
        if (Application.isPlaying)
        {
            EnsurePebbleStampHandlerRegistered();
            UpdatePebbleTransitions(Time.unscaledDeltaTime);
        }

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

    private void EnsurePebbleStampHandlerRegistered()
    {
        InteractionMapBakerV2 targetBaker = ResolveInteractionMapBaker();
        if (_registeredInteractionMapBaker == targetBaker)
            return;

        UnregisterPebbleStampHandler();
        if (targetBaker == null)
            return;

        targetBaker.RegisterPebbleStampHandler(ApplyPebbleStamp);
        _registeredInteractionMapBaker = targetBaker;
    }

    private void UnregisterPebbleStampHandler()
    {
        if (_registeredInteractionMapBaker == null)
            return;

        _registeredInteractionMapBaker.UnregisterPebbleStampHandler(ApplyPebbleStamp);
        _registeredInteractionMapBaker = null;
    }

    private InteractionMapBakerV2 ResolveInteractionMapBaker()
    {
        if (interactionMapBaker != null)
            return interactionMapBaker;

#if UNITY_2023_1_OR_NEWER
        interactionMapBaker = UnityEngine.Object.FindFirstObjectByType<InteractionMapBakerV2>(FindObjectsInactive.Exclude);
#else
        interactionMapBaker = UnityEngine.Object.FindObjectOfType<InteractionMapBakerV2>();
#endif
        return interactionMapBaker;
    }

    public void ApplyPebbleStamp(Vector3 worldPos, float radius, Vector3 pushDirection, float strength)
    {
        if (!Application.isPlaying)
            return;
        if (radius <= 0f || strength <= 0f || pushDirection.sqrMagnitude <= 0.000001f)
            return;

        int pebbleStateCount = 0;
        int intersectedCount = 0;
        int cellsAffectedTotal = 0;
        bool anyDebug = false;

        for (int i = 0; i < _activeFieldSources.Count; i++)
        {
            FieldSourceState state = _activeFieldSources[i];
            if (!IsValidPebbleState(state))
                continue;
            pebbleStateCount++;
            if (ShouldLogPebbleDiagnostics(state))
                anyDebug = true;
            if (!state.field.TryGetValidRenderConfig(state.surface, out ScatterSurfaceRenderConfig renderConfig, out string invalidReason))
            {
                ReportInvalidRenderConfig(state.field, state.surface.surfaceType, invalidReason);
                continue;
            }

            ClearRenderConfigWarning(state.field, state.surface.surfaceType);
            if (!IntersectsStampXZ(GetFieldChunkWorldBounds(state, state.field, renderConfig), worldPos, radius))
                continue;

            intersectedCount++;
            EnsurePebbleRuntimeStateInitialized(state);
            EnsureCellLookupCache(state);
            int cellsBefore = CountNonZeroDisplacementCells(state);
            ApplyPebbleStampToState(state, worldPos, radius, pushDirection, strength);
            int cellsAfter = CountNonZeroDisplacementCells(state);
            cellsAffectedTotal += Mathf.Max(0, cellsAfter - cellsBefore);
        }

        if (anyDebug)
        {
            Debug.Log(
                $"[PebbleDiag][StampSummary] frame={Time.frameCount} worldPos={worldPos} radius={radius:F3} " +
                $"strength={strength:F3} pushDir={pushDirection} totalFieldSources={_activeFieldSources.Count} " +
                $"pebbleStates={pebbleStateCount} intersectedChunks={intersectedCount} newCellsAffected={cellsAffectedTotal}");
        }
    }

    private static int CountNonZeroDisplacementCells(FieldSourceState state)
    {
        if (state?.chunk?.cells == null) return 0;
        int count = 0;
        for (int i = 0; i < state.chunk.cells.Count; i++)
        {
            if (state.chunk.cells[i].targetDisplacement.sqrMagnitude > 0.00000001f)
                count++;
        }
        return count;
    }

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
        // Preserve _fieldStates to retain pebble runtime data (displacement, rotation)
        // across refreshes. RegisterFieldSource reuses existing states via TryGetValue.
        // Stale entries (destroyed root Transforms) are cleaned up after re-registration.

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

        // Remove stale entries whose root Transform has been destroyed
        _staleFieldSourceKeys.Clear();
        var enumerator = _fieldStates.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (enumerator.Current.Key.root == null)
                _staleFieldSourceKeys.Add(enumerator.Current.Key);
        }
        enumerator.Dispose();
        for (int i = 0; i < _staleFieldSourceKeys.Count; i++)
            _fieldStates.Remove(_staleFieldSourceKeys[i]);

        debugFieldCount = fields.Count;
        debugFieldSourceCount = _activeFieldSources.Count;
    }

    private void RegisterFieldSource(ScatterField field, RoomScatterDataSO.ChunkRef chunkRef)
    {
        if (field == null)
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
        state.listsReady = false;
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

        Profiler.BeginSample("Scatter.DrawIntegrated");
        DrawIntegratedFromFields(cam);
        Profiler.EndSample();
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
            if (!field.TryGetValidRenderConfig(surface, out ScatterSurfaceRenderConfig renderConfig, out string invalidReason))
            {
                ReportInvalidRenderConfig(field, surface.surfaceType, invalidReason);
                continue;
            }

            ClearRenderConfigWarning(field, surface.surfaceType);
            if (!PassesFieldCameraFilters(field, cam))
                continue;

            if (!TryPrepareFieldSourceForCamera(state, field, renderConfig, cam, out int lodStride))
                continue;

            int variantCount = Mathf.Min(surface.EffectiveVariationCount, state.lists.Length);
            bool disableCullAndLodInEditorSceneView = !Application.isPlaying && cam.cameraType == CameraType.SceneView;
            bool useInstanceCull = !disableCullAndLodInEditorSceneView && field.enableInstanceCulling;
            for (int v = 0; v < variantCount; v++)
            {
                List<Matrix4x4> list = state.lists[v];
                if (list == null || list.Count == 0)
                    continue;

                Mesh mesh = renderConfig.variationMeshes[v];
                if (mesh == null)
                    continue;

                BucketKey key = BucketKey.Create(
                    mesh,
                    renderConfig.sharedMaterial,
                    renderConfig.overrideInteractionColorTuning,
                    renderConfig.pressColorWeight,
                    renderConfig.bendColorWeight);
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

        // Reset hasChanged for all active field sources to prevent unnecessary rebuilds next frame
        for (int s = 0; s < _activeFieldSources.Count; s++)
        {
            FieldSourceState state = _activeFieldSources[s];
            if (state?.root != null)
                state.root.hasChanged = false;
        }

        FlushUsedBuckets();
    }

    private bool TryPrepareFieldSourceForCamera(FieldSourceState state, ScatterField field, ScatterSurfaceRenderConfig renderConfig, Camera cam, out int lodStride)
    {
        lodStride = 0;
        if (state == null || field == null || renderConfig == null || cam == null)
            return false;

        // In edit-mode SceneView, show full data without LOD/culling for authoring.
        if (!Application.isPlaying && cam.cameraType == CameraType.SceneView)
        {
            EnsureFieldListsUpToDate(state);
            lodStride = 1;
            return true;
        }

        // In play mode, reject non-visible chunks before rebuilding their instance matrices.
        Bounds bounds = GetFieldChunkWorldBounds(state, field, renderConfig);
        if (!GeometryUtility.TestPlanesAABB(s_frustumPlanes, bounds))
            return false;

        EnsureFieldListsUpToDate(state);
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

        Profiler.BeginSample("Scatter.BuildLists");
        BuildFieldListsDeterministic(state);
        Profiler.EndSample();
        state.currentSettingsHash = ComputeFieldSettingsHash(state);
        state.currentDataVersion = state.roomData != null ? state.roomData.DataVersion : -1;
        state.currentTransformHash = ComputeRootTransformHash(state.root);
        state.listsDirty = false;
        state.listsReady = true;
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
        int currentTransformHash = ComputeRootTransformHash(state.root);
        if (!state.listsReady)
            return true;
        if (currentTransformHash != state.currentTransformHash)
            return true;
        if (state.roomData.DataVersion != state.currentDataVersion)
            return true;
        return ComputeFieldSettingsHash(state) != state.currentSettingsHash;
    }

    private static int ComputeRootTransformHash(Transform root)
    {
        return root != null ? root.localToWorldMatrix.GetHashCode() : 0;
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
        bool isPebbleSurface = surface.surfaceType == ScatterSurfaceType.Pebble;
        if (isPebbleSurface)
            EnsurePebbleRuntimeStateInitialized(state);

        int variationCount = Mathf.Clamp(surface.EffectiveVariationCount, 1, state.lists.Length);
        int cellCount = chunk.cells != null ? chunk.cells.Count : 0;

        // Pre-grow only the variants we can actually fill for steadier first-build allocations.
        int estimatedPerVariant = Mathf.Max(64, ((cellCount + variationCount - 1) / variationCount) * 2);
        for (int i = 0; i < variationCount; i++)
        {
            if (state.lists[i].Capacity < estimatedPerVariant)
                state.lists[i].Capacity = estimatedPerVariant;
        }

        float chunkSize = Mathf.Max(0.0001f, surface.chunkSize);
        float half = chunkSize * 0.5f;
        float cellSize = Mathf.Max(0.0001f, surface.cellSize);
        uint globalSeed = surface.EffectiveGlobalSeed;
        float scaleMin = surface.EffectiveScaleMin;
        float scaleMax = surface.EffectiveScaleMax;
        float chunkBaseX = chunk.chunkX * chunkSize;
        float chunkBaseZ = chunk.chunkY * chunkSize;

        List<CellRecord> src = chunk.cells;
        int n = src != null ? src.Count : 0;

        // Only copy+sort when data version changed; otherwise read directly from src
        bool needsSort = state.currentDataVersion != (state.roomData != null ? state.roomData.DataVersion : -1);
        CellRecord[] scratch = null;
        if (needsSort)
        {
            if (state.sortedCellsScratch == null || state.sortedCellsScratch.Length < n)
                state.sortedCellsScratch = new CellRecord[Mathf.NextPowerOfTwo(n)];
            for (int i = 0; i < n; i++)
                state.sortedCellsScratch[i] = src[i];
            Array.Sort(state.sortedCellsScratch, 0, n, s_cellComparer);
            scratch = state.sortedCellsScratch;
        }

        // Cache root rotation + inverse outside the loop (was per-cell Quaternion.Inverse before)
        Quaternion cachedRootRot = state.root != null ? state.root.rotation : Quaternion.identity;
        Quaternion cachedRootInvRot = Quaternion.Inverse(cachedRootRot);

        for (int i = 0; i < n; i++)
        {
            CellRecord rec = scratch != null ? scratch[i] : src[i];
            uint seed = ScatterHash.MakeSeed(globalSeed, rec.cx, rec.cy);
            Vector2 jitter = ScatterHash.Jitter(seed, cellSize * 0.35f);

            float x = chunkBaseX + ((int)rec.cx + 0.5f) * cellSize - half + jitter.x;
            float z = chunkBaseZ + ((int)rec.cy + 0.5f) * cellSize - half + jitter.y;
            float scale = rec.Scale(scaleMin, scaleMax);

            bool useProjectedPlacement = state.field != null && state.field.projectToStaticSurface;
            float localY = useProjectedPlacement ? rec.localY : 0f;
            Vector3 localPos = new Vector3(x, localY, z);
            Quaternion baseWorldRotation = Quaternion.identity;
            if (useProjectedPlacement && state.field.alignToSurfaceNormal)
                baseWorldRotation = ComputeProjectedRotation(state.root, rec.localNormal);
            if (isPebbleSurface)
                localPos += rec.displacement;

            Vector3 worldPos = state.root.TransformPoint(localPos);
            Quaternion worldRot = baseWorldRotation;
            if (isPebbleSurface)
            {
                Quaternion worldRollRotation = state.root != null
                    ? cachedRootRot * rec.rollRotation * cachedRootInvRot
                    : rec.rollRotation;
                worldRot = worldRollRotation * baseWorldRotation;
            }
            int variant = Mathf.Clamp(rec.variant, 0, variationCount - 1);
            if (variant < state.lists.Length)
                state.lists[variant].Add(Matrix4x4.TRS(worldPos, worldRot, Vector3.one * scale));

            if (isPebbleSurface &&
                ShouldLogPebbleDiagnostics(state) &&
                state.lastPebbleBuildLogFrame != Time.frameCount &&
                rec.displacement.sqrMagnitude > 0.00000001f)
            {
                Vector3 worldDisplacement = state.root != null
                    ? state.root.TransformVector(rec.displacement)
                    : rec.displacement;
                state.lastPebbleBuildLogFrame = Time.frameCount;
                Debug.Log(
                    $"[PebbleDiag][Build] frame={Time.frameCount} {GetPebbleStateLabel(state)} cell=({rec.cx},{rec.cy}) " +
                    $"localPos={localPos} localDisplacement={rec.displacement} worldDisplacement={worldDisplacement} worldPos={worldPos}",
                    state.field);
            }
        }
    }

    private static bool IsValidPebbleState(FieldSourceState state)
    {
        return state != null &&
               state.surface != null &&
               state.surface.surfaceType == ScatterSurfaceType.Pebble &&
               state.chunk != null &&
               state.root != null;
    }

    private static bool ShouldLogPebbleDiagnostics(FieldSourceState state)
    {
        return state != null &&
               state.field != null &&
               state.field.debugPebbleMotion;
    }

    private static string GetPebbleStateLabel(FieldSourceState state)
    {
        string fieldName = state?.field != null ? state.field.name : "<null-field>";
        string surfaceName = state?.surface != null ? state.surface.surfaceType.ToString() : "<null-surface>";
        int chunkX = state?.chunk != null ? state.chunk.chunkX : 0;
        int chunkY = state?.chunk != null ? state.chunk.chunkY : 0;
        int dataVersion = state?.roomData != null ? state.roomData.DataVersion : -1;
        return $"field={fieldName} surface={surfaceName} chunk=({chunkX},{chunkY}) dataVersion={dataVersion}";
    }

    private static bool IntersectsStampXZ(Bounds bounds, Vector3 stampWorldPos, float radius)
    {
        Vector3 center = bounds.center;
        Vector3 extents = bounds.extents;
        float dx = Mathf.Max(Mathf.Abs(stampWorldPos.x - center.x) - extents.x, 0f);
        float dz = Mathf.Max(Mathf.Abs(stampWorldPos.z - center.z) - extents.z, 0f);
        return dx * dx + dz * dz <= radius * radius;
    }

    private void UpdatePebbleTransitions(float deltaTime)
    {
        Profiler.BeginSample("Scatter.PebbleTransitions");
        if (deltaTime <= 0f)
        {
            Profiler.EndSample();
            return;
        }

        for (int i = 0; i < _activeFieldSources.Count; i++)
        {
            FieldSourceState state = _activeFieldSources[i];
            if (!IsValidPebbleState(state))
                continue;

            EnsurePebbleRuntimeStateInitialized(state);
            UpdatePebbleCellTransitions(state, deltaTime);
        }
        Profiler.EndSample();
    }

    private static void EnsurePebbleRuntimeStateInitialized(FieldSourceState state)
    {
        if (!IsValidPebbleState(state) || state.chunk.cells == null)
            return;

        int dataVersion = state.roomData != null ? state.roomData.DataVersion : -1;
        if (state.pebbleRuntimeDataVersion == dataVersion)
            return;

        int previousDataVersion = state.pebbleRuntimeDataVersion;

        List<CellRecord> cells = state.chunk.cells;
        for (int i = 0; i < cells.Count; i++)
        {
            CellRecord cell = cells[i];
            cell.ResetRuntimeState();
            cells[i] = cell;
        }

        state.pebbleRuntimeDataVersion = dataVersion;
        if (ShouldLogPebbleDiagnostics(state) && state.lastPebbleResetLogFrame != Time.frameCount)
        {
            state.lastPebbleResetLogFrame = Time.frameCount;
            Debug.Log(
                $"[PebbleDiag][Reset] frame={Time.frameCount} {GetPebbleStateLabel(state)} previousRuntimeDataVersion={previousDataVersion} cellCount={cells.Count}",
                state.field);
        }
    }

    private static void EnsureCellLookupCache(FieldSourceState state)
    {
        if (!IsValidPebbleState(state) || state.chunk.cells == null)
            return;

        int dataVersion = state.roomData != null ? state.roomData.DataVersion : -1;
        int cellCount = state.chunk.cells.Count;
        int cellsPerAxis = state.surface.CellsPerAxis;
        if (state.cellLookupDataVersion == dataVersion &&
            state.cellLookupCellCount == cellCount &&
            state.cellLookupCellsPerAxis == cellsPerAxis)
            return;

        state.cellIndexByKey.Clear();
        List<CellRecord> cells = state.chunk.cells;
        for (int i = 0; i < cells.Count; i++)
        {
            CellRecord cell = cells[i];
            state.cellIndexByKey[cell.Key(cellsPerAxis)] = i;
        }

        state.cellLookupDataVersion = dataVersion;
        state.cellLookupCellCount = cellCount;
        state.cellLookupCellsPerAxis = cellsPerAxis;
    }

    private static void ApplyPebbleStampToState(
        FieldSourceState state,
        Vector3 worldPos,
        float radius,
        Vector3 pushDirection,
        float strength)
    {
        if (!IsValidPebbleState(state) || state.chunk.cells == null || state.chunk.cells.Count == 0)
            return;

        PebbleScatterProfileSO profile = state.surface.profile as PebbleScatterProfileSO;
        if (profile == null && ShouldLogPebbleDiagnostics(state))
        {
            Debug.LogWarning($"[PebbleDiag] Pebble surface has no PebbleScatterProfileSO assigned! Using defaults (pushStrength={DefaultPebblePushStrength}, maxDisplacement={DefaultPebbleMaxDisplacement}). " +
                             $"Assign a PebbleScatterProfileSO to RoomScatterDataSO > Pebble surface > profile.");
        }
        float pushStrength = profile != null ? Mathf.Max(0f, profile.pushStrength) : DefaultPebblePushStrength;
        float maxDisplacement = profile != null ? Mathf.Max(0f, profile.maxDisplacement) : DefaultPebbleMaxDisplacement;
        float pebbleRadius = profile != null ? Mathf.Max(0.0001f, profile.pebbleRadius) : DefaultPebbleRadius;
        float friction = profile != null ? Mathf.Max(0f, profile.friction) : 2f;
        float strengthThreshold = profile != null ? Mathf.Clamp01(profile.strengthThreshold) : 0.05f;

        Vector3 localStampPos = state.root.InverseTransformPoint(worldPos);
        Vector3 worldPushDirection = pushDirection;
        worldPushDirection.y = 0f;
        if (worldPushDirection.sqrMagnitude <= 0.000001f)
            return;

        worldPushDirection.Normalize();

        RoomScatterDataSO.SurfaceLayerData surface = state.surface;
        RoomScatterDataSO.ChunkData chunk = state.chunk;
        float chunkSize = Mathf.Max(0.0001f, surface.chunkSize);
        float half = chunkSize * 0.5f;
        float cellSize = Mathf.Max(0.0001f, surface.cellSize);
        float chunkBaseX = chunk.chunkX * chunkSize;
        float chunkBaseZ = chunk.chunkY * chunkSize;
        float chunkMinX = chunkBaseX - half;
        float chunkMinZ = chunkBaseZ - half;
        int cellsPerAxis = surface.CellsPerAxis;

        int minCellX = Mathf.Clamp(Mathf.FloorToInt((localStampPos.x - radius - chunkMinX) / cellSize) - 1, 0, cellsPerAxis - 1);
        int maxCellX = Mathf.Clamp(Mathf.CeilToInt((localStampPos.x + radius - chunkMinX) / cellSize) + 1, 0, cellsPerAxis - 1);
        int minCellZ = Mathf.Clamp(Mathf.FloorToInt((localStampPos.z - radius - chunkMinZ) / cellSize) - 1, 0, cellsPerAxis - 1);
        int maxCellZ = Mathf.Clamp(Mathf.CeilToInt((localStampPos.z + radius - chunkMinZ) / cellSize) + 1, 0, cellsPerAxis - 1);

        bool anyChanged = false;
        for (int z = minCellZ; z <= maxCellZ; z++)
        {
            for (int x = minCellX; x <= maxCellX; x++)
            {
                int key = CellRecord.Key(x, z, cellsPerAxis);
                if (!state.cellIndexByKey.TryGetValue(key, out int cellIndex))
                    continue;
                if (cellIndex < 0 || cellIndex >= chunk.cells.Count)
                    continue;

                CellRecord cell = chunk.cells[cellIndex];
                Vector3 localBasePos = ComputeCellLocalPosition(surface, chunk, cell);
                Vector3 cellLocalPos = localBasePos + cell.displacement;
                float dist = Vector2.Distance(
                    new Vector2(localStampPos.x, localStampPos.z),
                    new Vector2(cellLocalPos.x, cellLocalPos.z));
                if (dist > radius)
                    continue;

                float normalizedDist = dist / Mathf.Max(0.0001f, radius);
                // Distance falloff: use profile curve if available, else squared falloff
                float falloff = (profile != null && profile.distanceFalloff != null)
                    ? profile.distanceFalloff.Evaluate(normalizedDist)
                    : (1f - normalizedDist) * (1f - normalizedDist);

                // Strength response: remap input strength through curve
                float effectiveStrength = (profile != null && profile.strengthResponse != null)
                    ? profile.strengthResponse.Evaluate(Mathf.Clamp01(strength))
                    : strength;

                // Push direction: radial outward from stamp center to this cell
                Vector3 cellWorldPos = state.root != null ? state.root.TransformPoint(cellLocalPos) : cellLocalPos;
                Vector3 radialDir = cellWorldPos - worldPos;
                radialDir.y = 0f;
                if (radialDir.sqrMagnitude <= 0.000001f)
                    radialDir = worldPushDirection; // fallback if cell is exactly at stamp center
                else
                    radialDir.Normalize();

                // Hard cutoff: skip cells where effective force is below threshold
                float effectiveForce = effectiveStrength * falloff;
                if (effectiveForce < strengthThreshold)
                    continue;

                Vector3 worldPush = radialDir * (effectiveForce * pushStrength);
                worldPush.y = 0f;
                if (worldPush.sqrMagnitude <= 0.000001f)
                    continue;

                // Friction: further displaced pebbles resist additional pushing
                float currentDisp = cell.targetDisplacement.magnitude;
                float frictionFactor = 1f / (1f + currentDisp * friction);
                worldPush *= frictionFactor;

                // Clamp per-push (prevents teleportation), NOT total displacement
                worldPush = Vector3.ClampMagnitude(worldPush, maxDisplacement);

                Vector3 localPush = state.root != null
                    ? state.root.InverseTransformVector(worldPush)
                    : worldPush;
                localPush.y = 0f;
                if (localPush.sqrMagnitude <= 0.000001f)
                    continue;

                cell.targetDisplacement += localPush;
                // No total magnitude clamp — displacement accumulates like rigidbody

                // Roll proportional to actual push distance
                float pushedDistance = worldPush.magnitude;
                if (pushedDistance > 0.001f)
                {
                    float rollAngle = (pushedDistance / pebbleRadius) * Mathf.Rad2Deg;
                    Vector3 localRollDirection = localPush.normalized;
                    Vector3 rollAxis = Vector3.Cross(Vector3.up, localRollDirection);
                    cell.targetRollRotation = Quaternion.AngleAxis(rollAngle, rollAxis) * cell.targetRollRotation;
                }

                chunk.cells[cellIndex] = cell;
                anyChanged = true;
                if (ShouldLogPebbleDiagnostics(state) && state.lastPebbleStampLogFrame != Time.frameCount)
                {
                    Vector3 targetWorldDisplacementAfterClamp = state.root != null
                        ? state.root.TransformVector(cell.targetDisplacement)
                        : cell.targetDisplacement;
                    state.lastPebbleStampLogFrame = Time.frameCount;
                    Debug.Log(
                        $"[PebbleDiag][Stamp] frame={Time.frameCount} {GetPebbleStateLabel(state)} cell=({cell.cx},{cell.cy}) " +
                        $"stampWorldPos={worldPos} localStampPos={localStampPos} localBasePos={localBasePos} " +
                        $"radius={radius:F3} strength={strength:F3} falloff={falloff:F4} " +
                        $"worldPush={worldPush} localPush={localPush} currentDisplacement={cell.displacement} " +
                        $"targetDisplacement={cell.targetDisplacement} targetWorldDisplacement={targetWorldDisplacementAfterClamp}",
                        state.field);
                }
            }
        }

        if (anyChanged)
        {
            state.listsDirty = true;
            state.pebbleTransitionActive = true;
        }
    }

    private static void UpdatePebbleCellTransitions(FieldSourceState state, float deltaTime)
    {
        if (!IsValidPebbleState(state) || state.chunk.cells == null || state.chunk.cells.Count == 0)
            return;
        // Skip entirely if no cells are transitioning
        if (!state.pebbleTransitionActive)
            return;

        PebbleScatterProfileSO profile = state.surface.profile as PebbleScatterProfileSO;
        float moveStepWorld = (profile != null ? Mathf.Max(0f, profile.rollSpeed) : DefaultPebbleRollSpeed) * deltaTime;
        float rotationStep = (profile != null ? Mathf.Max(0f, profile.rollAngularSpeed) : DefaultPebbleAngularSpeed) * deltaTime;

        bool anyChanged = false;
        List<CellRecord> cells = state.chunk.cells;
        for (int i = 0; i < cells.Count; i++)
        {
            CellRecord cell = cells[i];
            float displacementDelta = (cell.displacement - cell.targetDisplacement).sqrMagnitude;
            float rotationDelta = Quaternion.Angle(cell.rollRotation, cell.targetRollRotation);
            // Snap to target when sub-pixel to prevent infinite dirty state from Lerp asymptote
            if (displacementDelta <= 0.0001f && rotationDelta <= 0.1f)
            {
                if (displacementDelta > 0f)
                    cell.displacement = cell.targetDisplacement;
                if (rotationDelta > 0f)
                    cell.rollRotation = cell.targetRollRotation;
                cells[i] = cell;
                continue;
            }

            if (moveStepWorld > 0f)
            {
                Vector3 currentWorldDisplacement = state.root != null
                    ? state.root.TransformVector(cell.displacement)
                    : cell.displacement;
                Vector3 targetWorldDisplacement = state.root != null
                    ? state.root.TransformVector(cell.targetDisplacement)
                    : cell.targetDisplacement;
                currentWorldDisplacement.y = 0f;
                targetWorldDisplacement.y = 0f;

                // Exponential decay: fast initial kick, then decelerates
                // rollSpeed controls snappiness (higher = faster response)
                float t = 1f - Mathf.Exp(-moveStepWorld * 10f);
                Vector3 nextWorldDisplacement = Vector3.Lerp(
                    currentWorldDisplacement,
                    targetWorldDisplacement,
                    t);

                cell.displacement = state.root != null
                    ? state.root.InverseTransformVector(nextWorldDisplacement)
                    : nextWorldDisplacement;
            }
            else
            {
                cell.displacement = cell.targetDisplacement;
            }

            cell.rollRotation = rotationStep > 0f
                ? Quaternion.RotateTowards(cell.rollRotation, cell.targetRollRotation, rotationStep)
                : cell.targetRollRotation;
            cells[i] = cell;
            anyChanged = true;
            if (ShouldLogPebbleDiagnostics(state) && state.lastPebbleTransitionLogFrame != Time.frameCount)
            {
                Vector3 currentWorldDisplacement = state.root != null
                    ? state.root.TransformVector(cell.displacement)
                    : cell.displacement;
                Vector3 targetWorldDisplacement = state.root != null
                    ? state.root.TransformVector(cell.targetDisplacement)
                    : cell.targetDisplacement;
                state.lastPebbleTransitionLogFrame = Time.frameCount;
                Debug.Log(
                    $"[PebbleDiag][Transition] frame={Time.frameCount} {GetPebbleStateLabel(state)} cell=({cell.cx},{cell.cy}) " +
                    $"dt={deltaTime:F4} moveStepWorld={moveStepWorld:F4} rotationStep={rotationStep:F3} " +
                    $"displacement={cell.displacement} targetDisplacement={cell.targetDisplacement} " +
                    $"worldDisplacement={currentWorldDisplacement} targetWorldDisplacement={targetWorldDisplacement} " +
                    $"rotationDelta={Quaternion.Angle(cell.rollRotation, cell.targetRollRotation):F3}",
                    state.field);
            }
        }

        if (anyChanged)
            state.listsDirty = true;
        else
            state.pebbleTransitionActive = false; // All cells settled — stop iterating
    }

    private static Vector3 ComputeCellLocalPosition(
        RoomScatterDataSO.SurfaceLayerData surface,
        RoomScatterDataSO.ChunkData chunk,
        CellRecord cell)
    {
        float chunkSize = Mathf.Max(0.0001f, surface.chunkSize);
        float half = chunkSize * 0.5f;
        float cellSize = Mathf.Max(0.0001f, surface.cellSize);
        float chunkBaseX = chunk.chunkX * chunkSize;
        float chunkBaseZ = chunk.chunkY * chunkSize;
        uint seed = ScatterHash.MakeSeed(surface.EffectiveGlobalSeed, cell.cx, cell.cy);
        Vector2 jitter = ScatterHash.Jitter(seed, cellSize * 0.35f);
        return new Vector3(
            chunkBaseX + ((int)cell.cx + 0.5f) * cellSize - half + jitter.x,
            cell.localY,
            chunkBaseZ + ((int)cell.cy + 0.5f) * cellSize - half + jitter.y);
    }

    private static Quaternion ComputeProjectedLocalRotation(Vector3 localNormal)
    {
        Vector3 normalLocal = localNormal.sqrMagnitude > 1e-6f ? localNormal.normalized : Vector3.up;
        Vector3 tangent = Vector3.ProjectOnPlane(Vector3.forward, normalLocal);
        if (tangent.sqrMagnitude < 1e-6f)
            tangent = Vector3.ProjectOnPlane(Vector3.right, normalLocal);
        if (tangent.sqrMagnitude < 1e-6f)
            tangent = Vector3.right;

        return Quaternion.LookRotation(tangent.normalized, normalLocal);
    }

    private static Quaternion ComputeProjectedRotation(Transform root, Vector3 localNormal)
    {
        Quaternion localRotation = ComputeProjectedLocalRotation(localNormal);
        return root != null ? root.rotation * localRotation : localRotation;
    }

    private static Bounds GetFieldChunkWorldBounds(FieldSourceState state, ScatterField field, ScatterSurfaceRenderConfig renderConfig)
    {
        int hash = ComputeFieldBoundsHash(state, renderConfig);
        if (state.hasCachedBounds && hash == state.cachedBoundsHash)
            return state.cachedWorldBounds;

        float half = Mathf.Max(0.001f, state.surface != null ? state.surface.chunkSize * 0.5f : 0.5f);
        float scaleMax = state.surface != null ? Mathf.Max(1f, state.surface.EffectiveScaleMax) : 1f;

        float meshExtentXZ = 0f;
        float meshExtentY = 0f;
        Mesh[] meshes = renderConfig != null ? renderConfig.variationMeshes : null;
        if (meshes != null)
        {
            for (int i = 0; i < meshes.Length; i++)
            {
                Mesh mesh = meshes[i];
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

    private static int ComputeFieldBoundsHash(FieldSourceState state, ScatterSurfaceRenderConfig renderConfig)
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
            if (renderConfig != null && renderConfig.variationMeshes != null)
            {
                h = h * 31 + renderConfig.variationMeshes.Length;
                for (int i = 0; i < renderConfig.variationMeshes.Length; i++)
                {
                    Mesh m = renderConfig.variationMeshes[i];
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
        var enumerator = _buckets.GetEnumerator();
        while (enumerator.MoveNext())
        {
            DrawBucket bucket = enumerator.Current.Value;
            if (!bucket.usedThisFrame)
                continue;

            FlushBucket(bucket);
            bucket.usedThisFrame = false;
            debugRenderedBuckets++;
        }
        enumerator.Dispose();
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

    private static System.Type s_urpCamDataType;
    private static System.Reflection.PropertyInfo s_renderTypeProp;
    private static bool s_urpTypeResolved;

    private static bool IsUrpOverlayCamera(Camera cam)
    {
        if (cam == null)
            return false;

        if (!s_urpTypeResolved)
        {
            s_urpCamDataType = System.Type.GetType(
                "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
            if (s_urpCamDataType != null)
                s_renderTypeProp = s_urpCamDataType.GetProperty("renderType");
            s_urpTypeResolved = true;
        }

        if (s_urpCamDataType == null || s_renderTypeProp == null)
            return false;

        Component data = cam.GetComponent(s_urpCamDataType);
        if (data == null)
            return false;

        object val = s_renderTypeProp.GetValue(data, null);
        return val != null &&
               string.Equals(val.ToString(), "Overlay", StringComparison.Ordinal);
    }

    private void ReportInvalidRenderConfig(ScatterField field, ScatterSurfaceType surfaceType, string reason)
    {
        if (field == null)
            return;

        var key = new RenderConfigWarningKey(field, surfaceType);
        string normalizedReason = string.IsNullOrWhiteSpace(reason) ? "Surface render config required." : reason;
        if (_renderConfigWarnings.TryGetValue(key, out string existing) && string.Equals(existing, normalizedReason, StringComparison.Ordinal))
            return;

        _renderConfigWarnings[key] = normalizedReason;
        Debug.LogWarning($"[ScatterRenderManager] {field.name} / {surfaceType}: {normalizedReason}", field);
    }

    private void ClearRenderConfigWarning(ScatterField field, ScatterSurfaceType surfaceType)
    {
        if (field == null)
            return;

        _renderConfigWarnings.Remove(new RenderConfigWarningKey(field, surfaceType));
    }
}
